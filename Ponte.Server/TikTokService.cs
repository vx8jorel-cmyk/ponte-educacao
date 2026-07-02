using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Ponte.Server;

public sealed class TikTokService
{
    private readonly HttpClient _http;
    private readonly TikTokOptions _options;
    private readonly IDataProtector _protector;
    private readonly JsonStore _store;
    public TikTokService(HttpClient http, IOptions<TikTokOptions> options, IDataProtectionProvider protection, JsonStore store)
    { _http = http; _options = options.Value; _protector = protection.CreateProtector("Ponte.TikTok.Tokens.v1"); _store = store; }
    public bool IsConfigured => _options.IsConfigured;

    public string GetAuthorizationUrl(string state, string verifier)
    {
        var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(verifier))).TrimEnd('=').Replace('+','-').Replace('/','_');
        return "https://www.tiktok.com/v2/auth/authorize/" + QueryString.Create(new Dictionary<string,string?>
        {
            ["client_key"]=_options.ClientKey,["scope"]="user.info.basic,video.publish,video.upload,video.list",["response_type"]="code",
            ["redirect_uri"]=_options.RedirectUri,["state"]=state,["code_challenge"]=challenge,["code_challenge_method"]="S256"
        });
    }

    public async Task<TikTokConnection> ExchangeCodeAsync(string code, string verifier, CancellationToken ct)
    {
        using var response = await _http.PostAsync("https://open.tiktokapis.com/v2/oauth/token/", new FormUrlEncodedContent(new Dictionary<string,string>
        { ["client_key"]=_options.ClientKey,["client_secret"]=_options.ClientSecret,["code"]=code,["grant_type"]="authorization_code",["redirect_uri"]=_options.RedirectUri,["code_verifier"]=verifier }), ct);
        var token = await ReadJsonAsync(response, ct);
        var access = token.GetProperty("access_token").GetString()!; var refresh = token.GetProperty("refresh_token").GetString()!;
        using var infoRequest = new HttpRequestMessage(HttpMethod.Get,"https://open.tiktokapis.com/v2/user/info/?fields=open_id,display_name,avatar_url");
        infoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer",access);
        using var infoResponse = await _http.SendAsync(infoRequest,ct); var info = (await ReadJsonAsync(infoResponse,ct)).GetProperty("data").GetProperty("user");
        var connection = new TikTokConnection(info.GetProperty("open_id").GetString()!,info.GetProperty("display_name").GetString()??"TikTok",info.TryGetProperty("avatar_url",out var av)?av.GetString()??"":"",_protector.Protect(access),_protector.Protect(refresh),DateTimeOffset.UtcNow.AddSeconds(token.GetProperty("expires_in").GetInt32()),DateTimeOffset.UtcNow);
        await _store.SaveTikTokConnectionAsync(connection); return connection;
    }

    public async Task<string> PublishAsync(ScheduledPost post, CancellationToken ct)
    {
        var connection = await _store.GetTikTokConnectionAsync() ?? throw new InvalidOperationException("TikTok não conectado.");
        var access = _protector.Unprotect(connection.ProtectedAccessToken);
        var file = new FileInfo(post.MediaPath); if (!file.Exists) throw new FileNotFoundException("Vídeo não encontrado.");
        var initBody = new { post_info = new { title=post.Caption,privacy_level="SELF_ONLY",disable_duet=false,disable_comment=false,disable_stitch=false,video_cover_timestamp_ms=1000 }, source_info = new { source="FILE_UPLOAD",video_size=file.Length,chunk_size=file.Length,total_chunk_count=1 } };
        using var initRequest = new HttpRequestMessage(HttpMethod.Post,"https://open.tiktokapis.com/v2/post/publish/video/init/");
        initRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer",access); initRequest.Content = new StringContent(JsonSerializer.Serialize(initBody),Encoding.UTF8,"application/json");
        using var initResponse = await _http.SendAsync(initRequest,ct); var data=(await ReadJsonAsync(initResponse,ct)).GetProperty("data");
        var publishId=data.GetProperty("publish_id").GetString()!; var uploadUrl=data.GetProperty("upload_url").GetString()!;
        await using var stream=File.OpenRead(file.FullName); using var uploadContent=new StreamContent(stream); uploadContent.Headers.ContentType=new MediaTypeHeaderValue("video/mp4"); uploadContent.Headers.ContentLength=file.Length; uploadContent.Headers.TryAddWithoutValidation("Content-Range",$"bytes 0-{file.Length-1}/{file.Length}");
        using var uploadResponse=await _http.PutAsync(uploadUrl,uploadContent,ct); if(!uploadResponse.IsSuccessStatusCode) throw new InvalidOperationException($"TikTok upload ({(int)uploadResponse.StatusCode}): {await uploadResponse.Content.ReadAsStringAsync(ct)}");
        return publishId;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response,CancellationToken ct)
    { var body=await response.Content.ReadAsStringAsync(ct); if(!response.IsSuccessStatusCode) throw new InvalidOperationException($"TikTok API ({(int)response.StatusCode}): {body}"); var json=JsonSerializer.Deserialize<JsonElement>(body); if(json.TryGetProperty("error",out var error)&&error.TryGetProperty("code",out var code)&&code.GetString() is string value&&value!="ok") throw new InvalidOperationException(error.TryGetProperty("message",out var message)?message.GetString():value); return json; }
}
