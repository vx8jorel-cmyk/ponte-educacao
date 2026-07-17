using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Ponte.Server;

public sealed class YouTubeService
{
    private readonly HttpClient _http;
    private readonly YouTubeOptions _options;
    private readonly IDataProtector _protector;
    private readonly JsonStore _store;

    public YouTubeService(HttpClient http, IOptions<YouTubeOptions> options, IDataProtectionProvider protection, JsonStore store)
    {
        _http = http;
        _options = options.Value;
        _protector = protection.CreateProtector("Ponte.YouTube.Tokens.v1");
        _store = store;
    }

    public bool IsConfigured => _options.IsConfigured;

    public string GetAuthorizationUrl(string state)
    {
        return "https://accounts.google.com/o/oauth2/v2/auth" + QueryString.Create(new Dictionary<string, string?>
        {
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = _options.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly",
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["state"] = state
        });
    }

    public async Task<YouTubeConnection> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        using var response = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = _options.RedirectUri
        }), ct);
        var token = await ReadJsonAsync(response, ct, "YouTube OAuth");
        var accessToken = token.GetProperty("access_token").GetString()!;
        var refreshToken = token.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            var current = await _store.GetYouTubeConnectionAsync();
            refreshToken = current is null ? null : _protector.Unprotect(current.ProtectedRefreshToken);
        }
        if (string.IsNullOrWhiteSpace(refreshToken)) throw new InvalidOperationException("O Google não retornou refresh token. Revogue o acesso do app e conecte novamente.");

        var channel = await GetOwnChannelAsync(accessToken, ct);
        var connection = new YouTubeConnection(
            channel.ChannelId,
            channel.Title,
            channel.ThumbnailUrl,
            _protector.Protect(accessToken),
            _protector.Protect(refreshToken),
            DateTimeOffset.UtcNow.AddSeconds(token.GetProperty("expires_in").GetInt32()),
            DateTimeOffset.UtcNow);
        await _store.SaveYouTubeConnectionAsync(connection);
        return connection;
    }

    public async Task<string> PublishAsync(ScheduledPost post, CancellationToken ct)
    {
        var connection = await _store.GetYouTubeConnectionAsync() ?? throw new InvalidOperationException("YouTube não conectado.");
        var accessToken = await GetAccessTokenAsync(connection, ct);
        var file = new FileInfo(post.MediaPath);
        if (!file.Exists) throw new FileNotFoundException("Vídeo do YouTube não encontrado.", post.MediaPath);
        if (!IsVideo(file.FullName)) throw new InvalidOperationException("O YouTube nesta versão aceita apenas vídeos.");

        var title = string.IsNullOrWhiteSpace(post.Title) ? Path.GetFileNameWithoutExtension(post.OriginalFileName) : post.Title;
        if (title.Length > 100) title = title[..100];
        var metadata = new
        {
            snippet = new
            {
                title,
                description = post.Caption,
                tags = post.Tags.Count > 0 ? post.Tags : TagsFromCaption(post.Caption),
                categoryId = "22"
            },
            status = new
            {
                privacyStatus = "public"
            }
        };

        using var init = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status");
        init.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        init.Headers.TryAddWithoutValidation("X-Upload-Content-Type", MimeType(file.FullName));
        init.Headers.TryAddWithoutValidation("X-Upload-Content-Length", file.Length.ToString());
        init.Content = new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json");
        using var initResponse = await _http.SendAsync(init, ct);
        if (!initResponse.IsSuccessStatusCode) throw new InvalidOperationException($"YouTube upload init ({(int)initResponse.StatusCode}): {await initResponse.Content.ReadAsStringAsync(ct)}");
        var uploadUrl = initResponse.Headers.Location?.ToString();
        if (string.IsNullOrWhiteSpace(uploadUrl)) throw new InvalidOperationException("YouTube não retornou URL de upload.");

        await using var stream = File.OpenRead(file.FullName);
        using var upload = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        upload.Content = new StreamContent(stream);
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue(MimeType(file.FullName));
        upload.Content.Headers.ContentLength = file.Length;
        using var uploadResponse = await _http.SendAsync(upload, ct);
        var json = await ReadJsonAsync(uploadResponse, ct, "YouTube upload");
        return json.GetProperty("id").GetString()!;
    }

    private async Task<string> GetAccessTokenAsync(YouTubeConnection connection, CancellationToken ct)
    {
        if (connection.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5)) return _protector.Unprotect(connection.ProtectedAccessToken);
        var refreshToken = _protector.Unprotect(connection.ProtectedRefreshToken);
        using var response = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        }), ct);
        var token = await ReadJsonAsync(response, ct, "YouTube refresh");
        var accessToken = token.GetProperty("access_token").GetString()!;
        var updated = connection with
        {
            ProtectedAccessToken = _protector.Protect(accessToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.GetProperty("expires_in").GetInt32())
        };
        await _store.SaveYouTubeConnectionAsync(updated);
        return accessToken;
    }

    private async Task<(string ChannelId, string Title, string ThumbnailUrl)> GetOwnChannelAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, ct);
        var json = await ReadJsonAsync(response, ct, "YouTube channel");
        var item = json.GetProperty("items").EnumerateArray().FirstOrDefault();
        if (item.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Nenhum canal do YouTube foi encontrado nessa conta.");
        var snippet = item.GetProperty("snippet");
        var thumbnail = "";
        if (snippet.TryGetProperty("thumbnails", out var thumbnails))
        {
            thumbnail = thumbnails.TryGetProperty("high", out var high) ? high.GetProperty("url").GetString() ?? "" :
                thumbnails.TryGetProperty("default", out var def) ? def.GetProperty("url").GetString() ?? "" : "";
        }
        return (item.GetProperty("id").GetString()!, snippet.GetProperty("title").GetString() ?? "YouTube", thumbnail);
    }

    private static bool IsVideo(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov" or ".m4v" or ".webm";

    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".m4v" => "video/x-m4v",
        _ => "video/mp4"
    };

    private static List<string> TagsFromCaption(string caption)
    {
        var tags = caption.Split([' ', '\r', '\n', ',', '.', ';', ':', '!', '?'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length is >= 3 and <= 30)
            .Select(word => word.Trim('#', '@', '(', ')', '[', ']', '"', '\''))
            .Where(word => word.Length is >= 3 and <= 30)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        if (tags.Count == 0) tags.AddRange(["jorelwast", "video", "conteudo"]);
        return tags;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct, string label)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{label} ({(int)response.StatusCode}): {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }
}
