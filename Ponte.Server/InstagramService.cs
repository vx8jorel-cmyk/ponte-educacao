using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Ponte.Server;

public sealed class InstagramService
{
    private readonly HttpClient _http;
    private readonly MetaOptions _options;
    private readonly IDataProtector _protector;
    private readonly JsonStore _store;

    public InstagramService(HttpClient http, IOptions<MetaOptions> options, IDataProtectionProvider protection, JsonStore store)
    {
        _http = http; _options = options.Value; _protector = protection.CreateProtector("Ponte.Meta.AccessToken.v1"); _store = store;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task RefreshProfilesAsync(CancellationToken ct, bool force = false)
    {
        var connections = await _store.GetConnectionsAsync();
        var changed = false;
        for (var index = 0; index < connections.Count; index++)
        {
            if (!force && connections[index].ProfileSyncedAt is { } synced && synced > DateTimeOffset.UtcNow.AddMinutes(-10)) continue;
            try
            {
                var token = _protector.Unprotect(connections[index].ProtectedAccessToken);
                using var response = await _http.GetAsync(GraphUrl("me") + QueryString.Create(new Dictionary<string, string?>
                {
                    ["fields"] = "id,username,name,profile_picture_url", ["access_token"] = token
                }), ct);
                var profile = await ReadJsonAsync(response, ct);
                var picture = profile.TryGetProperty("profile_picture_url", out var pictureValue) ? pictureValue.GetString() : null;
                var name = profile.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                connections[index] = connections[index] with
                {
                    Username = profile.TryGetProperty("username", out var usernameValue) ? usernameValue.GetString() ?? connections[index].Username : connections[index].Username,
                    DisplayName = string.IsNullOrWhiteSpace(name) ? connections[index].DisplayName : name,
                    ProfilePictureUrl = picture,
                    ProfileSyncedAt = DateTimeOffset.UtcNow
                };
                changed = true;
            }
            catch { /* O painel mantém o placeholder se a plataforma não liberar a foto. */ }
        }
        if (changed) await _store.SaveConnectionsAsync(connections);
    }

    public string GetAuthorizationUrl(string state)
    {
        var scopes = "instagram_business_basic,instagram_business_content_publish,instagram_business_manage_insights";
        return "https://www.instagram.com/oauth/authorize" + QueryString.Create(new Dictionary<string, string?>
        {
            ["client_id"] = _options.AppId, ["redirect_uri"] = _options.RedirectUri,
            ["response_type"] = "code", ["scope"] = scopes, ["state"] = state,
            ["enable_fb_login"] = "0", ["force_authentication"] = "1"
        });
    }

    public async Task<InstagramConnection> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        using var first = await _http.PostAsync("https://api.instagram.com/oauth/access_token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.AppId, ["client_secret"] = _options.AppSecret,
            ["grant_type"] = "authorization_code", ["redirect_uri"] = _options.RedirectUri, ["code"] = code
        }), ct);
        var shortJson = await ReadJsonAsync(first, ct);
        var shortToken = shortJson.GetProperty("access_token").GetString()!;

        var longUrl = GraphUrl("access_token") + QueryString.Create(new Dictionary<string, string?>
        {
            ["grant_type"] = "ig_exchange_token", ["client_secret"] = _options.AppSecret, ["access_token"] = shortToken
        });
        using var longResponse = await _http.GetAsync(longUrl, ct);
        var longJson = await ReadJsonAsync(longResponse, ct);
        var accessToken = longJson.GetProperty("access_token").GetString()!;

        using var meResponse = await _http.GetAsync(GraphUrl("me") + QueryString.Create(new Dictionary<string, string?> { ["fields"] = "id,username,name,profile_picture_url", ["access_token"] = accessToken }), ct);
        var me = await ReadJsonAsync(meResponse, ct);
        var userId = me.GetProperty("id").GetString()!;
        var username = me.GetProperty("username").GetString() ?? "instagram";
        var accountName = me.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
        var profilePictureUrl = me.TryGetProperty("profile_picture_url", out var pictureProperty) ? pictureProperty.GetString() : null;
        var connections = await _store.GetConnectionsAsync();
        var existing = connections.FindIndex(item => item.UserId == userId);
        var displayName = existing >= 0 && connections[existing].DisplayName != connections[existing].Username ? connections[existing].DisplayName : accountName ?? username;
        var connection = new InstagramConnection(userId, username, displayName, _protector.Protect(accessToken), DateTimeOffset.UtcNow, profilePictureUrl, DateTimeOffset.UtcNow);
        if (existing >= 0) connections[existing] = connection;
        else connections.Add(connection);
        await _store.SaveConnectionsAsync(connections);
        return connection;
    }

    public async Task<string> PublishAsync(ScheduledPost post, CancellationToken ct)
    {
        var connection = (await _store.GetConnectionsAsync()).SingleOrDefault(item => item.UserId == post.AccountId) ?? throw new InvalidOperationException("Conta do Instagram não conectada.");
        var token = _protector.Unprotect(connection.ProtectedAccessToken);
        var publicUrl = _options.PublicBaseUrl.TrimEnd('/') + "/media/" + Uri.EscapeDataString(Path.GetFileName(post.MediaPath));
        var personalizedCaption = post.Caption
            .Replace("{usuario}", connection.Username, StringComparison.OrdinalIgnoreCase)
            .Replace("{nome}", connection.DisplayName, StringComparison.OrdinalIgnoreCase);
        var fields = new Dictionary<string, string> { ["caption"] = personalizedCaption, ["access_token"] = token };
        if (post.Type == "REELS") { fields["media_type"] = "REELS"; fields["video_url"] = publicUrl; fields["share_to_feed"] = "true"; }
        else fields["image_url"] = publicUrl;

        using var create = await _http.PostAsync(GraphUrl($"{connection.UserId}/media"), new FormUrlEncodedContent(fields!), ct);
        var created = await ReadJsonAsync(create, ct);
        var containerId = created.GetProperty("id").GetString()!;
        if (post.Type == "REELS") await WaitForContainerAsync(containerId, token, ct);
        using var publish = await _http.PostAsync(GraphUrl($"{connection.UserId}/media_publish"), new FormUrlEncodedContent(new Dictionary<string, string> { ["creation_id"] = containerId, ["access_token"] = token }), ct);
        var published = await ReadJsonAsync(publish, ct);
        return published.GetProperty("id").GetString()!;
    }

    public async Task<EngagementSummary> GetInsightsAsync(string accountId, CancellationToken ct)
    {
        var connection = (await _store.GetConnectionsAsync()).SingleOrDefault(item => item.UserId == accountId) ?? throw new InvalidOperationException("Conta do Instagram não conectada.");
        var token = _protector.Unprotect(connection.ProtectedAccessToken);
        var url = GraphUrl($"{connection.UserId}/media") + QueryString.Create(new Dictionary<string, string?> { ["fields"] = "id,media_type,caption,permalink,timestamp,like_count,comments_count", ["limit"] = "25", ["access_token"] = token });
        using var response = await _http.GetAsync(url, ct); var json = await ReadJsonAsync(response, ct);
        var items = new List<MediaInsight>();
        foreach (var media in json.GetProperty("data").EnumerateArray())
        {
            var id = media.GetProperty("id").GetString()!;
            var type = media.TryGetProperty("media_type", out var mt) ? mt.GetString() ?? "IMAGE" : "IMAGE";
            var metrics = type == "VIDEO" || type == "REELS" ? "reach,views,likes,comments,shares,saved,total_interactions" : "reach,views,likes,comments,shares,saved,total_interactions";
            var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using var insightResponse = await _http.GetAsync(GraphUrl($"{id}/insights") + QueryString.Create(new Dictionary<string, string?> { ["metric"] = metrics, ["access_token"] = token }), ct);
            if (insightResponse.IsSuccessStatusCode)
            {
                var insightJson = await insightResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                foreach (var metric in insightJson.GetProperty("data").EnumerateArray()) values[metric.GetProperty("name").GetString()!] = metric.GetProperty("values")[0].GetProperty("value").GetInt64();
            }
            long V(string key) => values.GetValueOrDefault(key);
            items.Add(new MediaInsight(id, type, media.TryGetProperty("caption", out var cap) ? cap.GetString() ?? "" : "", media.TryGetProperty("permalink", out var link) ? link.GetString() : null, media.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTimeOffset(out var date) ? date : null, V("reach"), V("views"), V("likes"), V("comments"), V("shares"), V("saved"), V("total_interactions")));
        }
        return new EngagementSummary(items.Sum(x=>x.Reach),items.Sum(x=>x.Views),items.Sum(x=>x.Likes),items.Sum(x=>x.Comments),items.Sum(x=>x.Shares),items.Sum(x=>x.Saved),items.Sum(x=>x.TotalInteractions),items.Count,items);
    }

    private async Task WaitForContainerAsync(string id, string token, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            using var response = await _http.GetAsync(GraphUrl(id) + QueryString.Create(new Dictionary<string, string?> { ["fields"] = "status_code,status", ["access_token"] = token }), ct);
            var json = await ReadJsonAsync(response, ct); var status = json.GetProperty("status_code").GetString();
            if (status == "FINISHED") return;
            if (status is "ERROR" or "EXPIRED") throw new InvalidOperationException(json.TryGetProperty("status", out var detail) ? detail.GetString() : "Falha ao processar o Reel.");
        }
        throw new TimeoutException("A Meta demorou demais para processar o Reel. O post será tentado novamente automaticamente.");
    }

    private string GraphUrl(string path) => $"https://graph.instagram.com/{_options.GraphVersion}/{path.TrimStart('/')}";
    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Meta API ({(int)response.StatusCode}): {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }
}
