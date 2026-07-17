using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Ponte.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
const long MaximumUploadBytes = 5L * 1024 * 1024 * 1024;
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaximumUploadBytes);
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = MaximumUploadBytes;
    options.ValueLengthLimit = 10 * 1024 * 1024;
});
builder.Configuration.AddUserSecrets<Program>(optional: true, reloadOnChange: true);
builder.Services.Configure<MetaOptions>(builder.Configuration.GetSection("Meta"));
builder.Services.Configure<TikTokOptions>(builder.Configuration.GetSection("TikTok"));
builder.Services.Configure<YouTubeOptions>(builder.Configuration.GetSection("YouTube"));
var dataDirectory = Environment.GetEnvironmentVariable("JORELWAST_DATA_DIR") ?? Path.Combine(builder.Environment.ContentRootPath, "data");
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
builder.Services.AddSingleton<JsonStore>();
builder.Services.AddHttpClient<InstagramService>();
builder.Services.AddHttpClient<TikTokService>();
builder.Services.AddHttpClient<YouTubeService>(client => client.Timeout = TimeSpan.FromMinutes(30));
builder.Services.AddHttpClient<ContentAiService>(client => client.Timeout = TimeSpan.FromMinutes(25));
builder.Services.AddHttpClient<VideoBrandingService>(client => client.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddHostedService<AiProcessingWorker>();
builder.Services.AddHostedService<ProfileSyncWorker>();
builder.Services.AddHostedService<PublishingWorker>();

var app = builder.Build();
var store = app.Services.GetRequiredService<JsonStore>();
var root = Directory.GetParent(app.Environment.ContentRootPath)!.FullName;
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = error is BadHttpRequestException ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status500InternalServerError;
    await Results.Json(new { error = error is BadHttpRequestException ? "O lote ultrapassou o limite aceito pelo servidor." : error?.Message ?? "Erro interno." }, statusCode: context.Response.StatusCode).ExecuteAsync(context);
}));
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(root) });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(root),
    OnPrepareResponse = response => response.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate"
});
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(store.UploadDirectory), RequestPath = "/media", ServeUnknownFileTypes = false });

app.MapGet("/api/status", async (InstagramService instagram, ContentAiService ai, JsonStore db, CancellationToken ct) =>
{
    await instagram.RefreshProfilesAsync(ct);
    var connections = await db.GetConnectionsAsync();
    var connection = connections.FirstOrDefault();
    return Results.Ok(new
    {
        configured = instagram.IsConfigured,
        aiConfigured = ai.IsConfigured,
        connected = connections.Count > 0,
        account = connection is null ? null : new { id = connection.UserId, username = connection.Username, name = connection.DisplayName, avatarUrl = connection.ProfilePictureUrl },
        accounts = connections.Select(item => new { id = item.UserId, username = item.Username, name = item.DisplayName, avatarUrl = item.ProfilePictureUrl })
    });
});

app.MapGet("/api/dashboard", async (JsonStore db, ContentAiService ai) =>
{
    var posts = await db.GetPostsAsync();
    var accounts = await db.GetConnectionsAsync();
    var today = DateTimeOffset.UtcNow.Date;
    return Results.Ok(new
    {
        accounts = accounts.Count,
        scheduled = posts.Count(item => item.Status == "scheduled"),
        publishing = posts.Count(item => item.Status == "publishing"),
        published = posts.Count(item => item.Status == "published"),
        failed = posts.Count(item => item.Status == "failed"),
        analyzing = posts.Count(item => item.AiStatus is "pending" or "analyzing"),
        aiConfigured = ai.IsConfigured,
        today = posts.Count(item => item.PublishAt.UtcDateTime.Date == today),
        recent = posts.OrderByDescending(item => item.CreatedAt).Take(6).Select(item => new
        {
            item.Id, item.AccountId, item.Platform, item.Type, item.Title, item.Caption, item.AiStatus, item.PublishAt, item.Status, item.Error
        })
    });
});

app.MapDelete("/api/auth/instagram/session", async (JsonStore db) => { await db.SaveConnectionsAsync([]); return Results.NoContent(); });

app.MapGet("/api/tiktok/status", async (TikTokService tiktok, JsonStore db) => { var connection=await db.GetTikTokConnectionAsync(); return Results.Ok(new { configured=tiktok.IsConfigured,connected=connection is not null,account=connection is null?null:new { id=connection.OpenId,displayName=connection.DisplayName,avatarUrl=connection.AvatarUrl } }); });
app.MapGet("/api/auth/tiktok/start", (HttpContext context,TikTokService tiktok) => { if(!tiktok.IsConfigured) return Results.Redirect("/?error=tiktok_not_configured"); var state=Convert.ToHexString(RandomNumberGenerator.GetBytes(24)); var verifier=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(); var cookie=new CookieOptions{HttpOnly=true,Secure=true,SameSite=SameSiteMode.Lax,MaxAge=TimeSpan.FromMinutes(10)}; context.Response.Cookies.Append("ponte_tiktok_state",state,cookie); context.Response.Cookies.Append("ponte_tiktok_verifier",verifier,cookie); return Results.Redirect(tiktok.GetAuthorizationUrl(state,verifier)); });
app.MapGet("/api/auth/tiktok/callback",async(HttpContext context,string? code,string? state,string? error,TikTokService tiktok,CancellationToken ct)=>{ if(error is not null)return Results.Redirect("/?tiktok=denied"); if(string.IsNullOrWhiteSpace(code)||string.IsNullOrWhiteSpace(state)||!context.Request.Cookies.TryGetValue("ponte_tiktok_state",out var expected)||state!=expected||!context.Request.Cookies.TryGetValue("ponte_tiktok_verifier",out var verifier))return Results.BadRequest("Estado OAuth do TikTok inválido."); await tiktok.ExchangeCodeAsync(code,verifier,ct); context.Response.Cookies.Delete("ponte_tiktok_state");context.Response.Cookies.Delete("ponte_tiktok_verifier");return Results.Redirect("/?tiktok=connected");});
app.MapDelete("/api/auth/tiktok/session",async(JsonStore db)=>{await db.SaveTikTokConnectionAsync(null);return Results.NoContent();});

app.MapGet("/api/youtube/status", async (YouTubeService youtube, JsonStore db) =>
{
    var connection = await db.GetYouTubeConnectionAsync();
    return Results.Ok(new
    {
        configured = youtube.IsConfigured,
        connected = connection is not null,
        account = connection is null ? null : new { id = connection.ChannelId, title = connection.Title, thumbnailUrl = connection.ThumbnailUrl }
    });
});
app.MapGet("/api/auth/youtube/start", (HttpContext context, YouTubeService youtube) =>
{
    if (!youtube.IsConfigured) return Results.Redirect("/?error=youtube_not_configured");
    var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    context.Response.Cookies.Append("ponte_youtube_state", state, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10) });
    return Results.Redirect(youtube.GetAuthorizationUrl(state));
});
app.MapGet("/api/auth/youtube/callback", async (HttpContext context, string? code, string? state, string? error, YouTubeService youtube, CancellationToken ct) =>
{
    if (error is not null) return Results.Redirect("/?youtube=denied");
    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) || !context.Request.Cookies.TryGetValue("ponte_youtube_state", out var expected) || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(state), System.Text.Encoding.UTF8.GetBytes(expected))) return Results.BadRequest("Estado OAuth do YouTube inválido.");
    await youtube.ExchangeCodeAsync(code, ct);
    context.Response.Cookies.Delete("ponte_youtube_state");
    return Results.Redirect("/?youtube=connected#accounts");
});
app.MapDelete("/api/auth/youtube/session", async (JsonStore db) => { await db.SaveYouTubeConnectionAsync(null); return Results.NoContent(); });

app.MapGet("/api/auth/instagram/start", (HttpContext context, InstagramService instagram) =>
{
    if (!instagram.IsConfigured) return Results.Redirect("/?error=meta_not_configured");
    var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    context.Response.Cookies.Append("ponte_oauth_state", state, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10) });
    return Results.Redirect(instagram.GetAuthorizationUrl(state));
});

app.MapGet("/api/auth/instagram/callback", async (HttpContext context, string? code, string? state, string? error, InstagramService instagram, CancellationToken ct) =>
{
    if (error is not null) return Results.Redirect("/?instagram=denied");
    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) || !context.Request.Cookies.TryGetValue("ponte_oauth_state", out var expected) || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(state), System.Text.Encoding.UTF8.GetBytes(expected))) return Results.BadRequest("Estado OAuth inválido.");
    await instagram.ExchangeCodeAsync(code, ct); context.Response.Cookies.Delete("ponte_oauth_state");
    return Results.Redirect("/?instagram=connected");
});

app.MapGet("/api/posts", async (JsonStore db) => Results.Ok(await db.GetPostsAsync()));
app.MapPut("/api/accounts/{id}/name", async (string id, AccountNameRequest request, JsonStore db) => { var accounts = await db.GetConnectionsAsync(); var index = accounts.FindIndex(item => item.UserId == id); if (index < 0) return Results.NotFound(); var current = accounts[index]; accounts[index] = current with { DisplayName = string.IsNullOrWhiteSpace(request.Name) ? current.Username : request.Name.Trim() }; await db.SaveConnectionsAsync(accounts); return Results.Ok(new { id, name = accounts[index].DisplayName }); });
app.MapDelete("/api/accounts/{id}", async (string id, JsonStore db) => { var accounts = await db.GetConnectionsAsync(); var removed = accounts.RemoveAll(item => item.UserId == id); await db.SaveConnectionsAsync(accounts); return removed > 0 ? (IResult)Results.NoContent() : Results.NotFound(); });
app.MapDelete("/api/posts/{id:guid}", async (Guid id, JsonStore db) => { var posts = await db.GetPostsAsync(); var removed = posts.RemoveAll(x => x.Id == id); await db.SavePostsAsync(posts); return removed > 0 ? (IResult)Results.NoContent() : Results.NotFound(); });

app.MapPost("/api/posts", async (HttpRequest request, JsonStore db, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Envie multipart/form-data." });
    var form = await request.ReadFormAsync(ct); var file = form.Files.GetFile("media");
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Selecione uma foto ou um vídeo." });
    var type = (form["type"].ToString().ToUpperInvariant() == "REELS") ? "REELS" : "IMAGE";
    var allowed = type == "REELS" ? new[] { ".mp4", ".mov" } : new[] { ".jpg", ".jpeg", ".png" };
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!allowed.Contains(extension)) return Results.BadRequest(new { error = type == "REELS" ? "Reels aceitam MP4 ou MOV." : "Fotos aceitam JPG ou PNG." });
    if (file.Length > 300L * 1024 * 1024) return Results.BadRequest(new { error = "Arquivo acima de 300 MB." });
    if (!DateTimeOffset.TryParse(form["publishAt"], out var publishAt)) return Results.BadRequest(new { error = "Data de publicação inválida." });
    var accountId = form["accountId"].ToString();
    if (!(await db.GetConnectionsAsync()).Any(item => item.UserId == accountId)) return Results.BadRequest(new { error = "Selecione uma conta do Instagram conectada." });
    var fileName = $"{Guid.NewGuid():N}{extension}"; var path = Path.Combine(db.UploadDirectory, fileName);
    await using (var output = File.Create(path)) await file.CopyToAsync(output, ct);
    var post = new ScheduledPost { AccountId = accountId, Type = type, Caption = form["caption"].ToString(), OriginalFileName = file.FileName, SourceMediaPath = path, MediaPath = path, PublishAt = publishAt.ToUniversalTime() };
    var posts = await db.GetPostsAsync(); posts.Add(post); await db.SavePostsAsync(posts);
    return Results.Created($"/api/posts/{post.Id}", post);
}).DisableAntiforgery();

app.MapPost("/api/posts/bulk", async (HttpRequest request, JsonStore db, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Envie multipart/form-data." });
    var form = await request.ReadFormAsync(ct);
    var files = form.Files.GetFiles("media");
    if (files.Count == 0) return Results.BadRequest(new { error = "Selecione pelo menos uma foto ou um vídeo." });
    if (files.Count > 100) return Results.BadRequest(new { error = "Envie no máximo 100 arquivos por lote." });

    var platforms = form["platform"].Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.ToLowerInvariant()).Distinct().ToArray();
    if (platforms.Length == 0) platforms = ["instagram"];
    if (platforms.Any(platform => platform is not ("instagram" or "youtube"))) return Results.BadRequest(new { error = "Destino inválido. Use Instagram ou YouTube." });
    var accountIds = form["accountId"].Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
    var connections = await db.GetConnectionsAsync();
    var youtubeConnection = await db.GetYouTubeConnectionAsync();
    if (platforms.Contains("instagram") && accountIds.Length == 0) return Results.BadRequest(new { error = "Selecione ao menos uma conta do Instagram." });
    if (platforms.Contains("instagram") && accountIds.Any(id => connections.All(item => item.UserId != id))) return Results.BadRequest(new { error = "Uma das contas selecionadas não está conectada." });
    if (platforms.Contains("youtube") && youtubeConnection is null) return Results.BadRequest(new { error = "Conecte o YouTube primeiro." });
    var totalPublications = (platforms.Contains("instagram") ? accountIds.Length * files.Count : 0) + (platforms.Contains("youtube") ? files.Count : 0);
    if (totalPublications > 500) return Results.BadRequest(new { error = "O lote pode gerar no máximo 500 publicações." });

    if (!DateTimeOffset.TryParse(form["publishAt"], out var requestedStart)) return Results.BadRequest(new { error = "Data inicial inválida." });
    var intervalSeconds = int.TryParse(form["intervalSeconds"], out var intervalSecondsValue)
        ? Math.Clamp(intervalSecondsValue, 1, 604800)
        : int.TryParse(form["intervalMinutes"], out var intervalMinutesValue) ? Math.Clamp(intervalMinutesValue * 60, 1, 604800) : 5400;
    var dailyLimit = int.TryParse(form["dailyLimit"], out var limitValue) ? Math.Clamp(limitValue, 1, 100) : 10;
    var captionTemplate = form["caption"].ToString();
    var useAi = !string.Equals(form["useAi"], "false", StringComparison.OrdinalIgnoreCase);
    var posts = await db.GetPostsAsync();
    var created = new List<ScheduledPost>();
    var savedMedia = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    if (platforms.Contains("instagram"))
    {
        foreach (var accountId in accountIds)
        {
            var cursor = requestedStart;
            foreach (var file in files)
            {
                var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
                var isVideo = extension is ".mp4" or ".mov";
                var isImage = extension is ".jpg" or ".jpeg" or ".png";
                if (!isVideo && !isImage) return Results.BadRequest(new { error = $"Formato não aceito: {file.FileName}. Use JPG, PNG, MP4 ou MOV." });
                if (file.Length == 0 || file.Length > 300L * 1024 * 1024) return Results.BadRequest(new { error = $"{file.FileName} está vazio ou ultrapassa 300 MB." });

                cursor = NextAvailableSlot(posts.Concat(created), accountId!, cursor, dailyLimit, "instagram");
                var path = await SaveUploadedFileAsync(file, extension, savedMedia, db.UploadDirectory, ct);
                var post = new ScheduledPost
                {
                    Platform = "instagram",
                    AccountId = accountId!,
                    Type = isVideo ? "REELS" : "IMAGE",
                    Caption = captionTemplate.Replace("{arquivo}", Path.GetFileNameWithoutExtension(file.FileName), StringComparison.OrdinalIgnoreCase),
                    OriginalFileName = file.FileName,
                    SourceMediaPath = path,
                    MediaPath = path,
                    AiStatus = useAi ? "pending" : "disabled",
                    PublishAt = cursor.ToUniversalTime()
                };
                created.Add(post);
                cursor = cursor.AddSeconds(intervalSeconds);
            }
        }
    }

    if (platforms.Contains("youtube") && youtubeConnection is not null)
    {
        var cursor = requestedStart;
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (extension is not (".mp4" or ".mov" or ".m4v" or ".webm")) return Results.BadRequest(new { error = $"YouTube aceita apenas vídeo neste lote: {file.FileName}." });
            if (file.Length == 0 || file.Length > 1024L * 1024 * 1024) return Results.BadRequest(new { error = $"{file.FileName} está vazio ou ultrapassa 1 GB para YouTube." });

            cursor = NextAvailableSlot(posts.Concat(created), youtubeConnection.ChannelId, cursor, dailyLimit, "youtube");
            var path = await SaveUploadedFileAsync(file, extension, savedMedia, db.UploadDirectory, ct);
            var post = new ScheduledPost
            {
                Platform = "youtube",
                AccountId = youtubeConnection.ChannelId,
                Type = "VIDEO",
                Title = Path.GetFileNameWithoutExtension(file.FileName),
                Caption = captionTemplate.Replace("{arquivo}", Path.GetFileNameWithoutExtension(file.FileName), StringComparison.OrdinalIgnoreCase),
                OriginalFileName = file.FileName,
                SourceMediaPath = path,
                MediaPath = path,
                AiStatus = useAi ? "pending" : "disabled",
                PublishAt = cursor.ToUniversalTime()
            };
            created.Add(post);
            cursor = cursor.AddSeconds(intervalSeconds);
        }
    }

    posts.AddRange(created);
    await db.SavePostsAsync(posts);
    return Results.Ok(new { count = created.Count, posts = created });
}).DisableAntiforgery();

app.MapPost("/api/tiktok/posts",async(HttpRequest request,JsonStore db,CancellationToken ct)=>{if(!request.HasFormContentType)return Results.BadRequest(new{error="Envie multipart/form-data."});var form=await request.ReadFormAsync(ct);var file=form.Files.GetFile("media");if(file is null||file.Length==0)return Results.BadRequest(new{error="Selecione um vídeo MP4."});var extension=Path.GetExtension(file.FileName).ToLowerInvariant();if(extension!=".mp4")return Results.BadRequest(new{error="O TikTok aceita vídeo MP4 nesta versão."});if(file.Length>64L*1024*1024)return Results.BadRequest(new{error="Use vídeo de até 64 MB nesta versão."});if(!DateTimeOffset.TryParse(form["publishAt"],out var publishAt))return Results.BadRequest(new{error="Data inválida."});if(await db.GetTikTokConnectionAsync() is null)return Results.BadRequest(new{error="Entre no TikTok primeiro."});var fileName=$"{Guid.NewGuid():N}.mp4";var path=Path.Combine(db.UploadDirectory,fileName);await using(var output=File.Create(path))await file.CopyToAsync(output,ct);var post=new ScheduledPost{Platform="tiktok",AccountId="tiktok",Type="VIDEO",Caption=form["caption"].ToString(),OriginalFileName=file.FileName,SourceMediaPath=path,MediaPath=path,PublishAt=publishAt.ToUniversalTime()};var posts=await db.GetPostsAsync();posts.Add(post);await db.SavePostsAsync(posts);return Results.Created($"/api/posts/{post.Id}",post);}).DisableAntiforgery();

app.MapPost("/api/youtube/posts", async (HttpRequest request, JsonStore db, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Envie multipart/form-data." });
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("media");
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Selecione um vídeo." });
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (extension is not (".mp4" or ".mov" or ".m4v" or ".webm")) return Results.BadRequest(new { error = "O YouTube aceita MP4, MOV, M4V ou WEBM nesta versão." });
    if (file.Length > 1024L * 1024 * 1024) return Results.BadRequest(new { error = "Use vídeo de até 1 GB nesta versão." });
    if (!DateTimeOffset.TryParse(form["publishAt"], out var publishAt)) return Results.BadRequest(new { error = "Data inválida." });
    var connection = await db.GetYouTubeConnectionAsync();
    if (connection is null) return Results.BadRequest(new { error = "Conecte o YouTube primeiro." });
    var fileName = $"{Guid.NewGuid():N}{extension}";
    var path = Path.Combine(db.UploadDirectory, fileName);
    await using (var output = File.Create(path)) await file.CopyToAsync(output, ct);
    var post = new ScheduledPost
    {
        Platform = "youtube",
        AccountId = connection.ChannelId,
        Type = "VIDEO",
        Title = form["title"].ToString(),
        Caption = form["caption"].ToString(),
        OriginalFileName = file.FileName,
        SourceMediaPath = path,
        MediaPath = path,
        PublishAt = publishAt.ToUniversalTime()
    };
    var posts = await db.GetPostsAsync();
    posts.Add(post);
    await db.SavePostsAsync(posts);
    return Results.Created($"/api/posts/{post.Id}", post);
}).DisableAntiforgery();

app.MapGet("/api/insights", async (string accountId, InstagramService instagram, CancellationToken ct) => Results.Ok(await instagram.GetInsightsAsync(accountId, ct)));
app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = new PhysicalFileProvider(root) });
app.Run();

static DateTimeOffset NextAvailableSlot(IEnumerable<ScheduledPost> posts, string accountId, DateTimeOffset candidate, int dailyLimit, string platform)
{
    TimeZoneInfo zone;
    try { zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "E. South America Standard Time" : "America/Sao_Paulo"); }
    catch { zone = TimeZoneInfo.Utc; }

    var originalLocal = TimeZoneInfo.ConvertTime(candidate, zone);
    while (true)
    {
        var localCandidate = TimeZoneInfo.ConvertTime(candidate, zone);
        var used = posts.Count(item => item.Platform == platform && item.AccountId == accountId && item.Status != "failed" && TimeZoneInfo.ConvertTime(item.PublishAt, zone).Date == localCandidate.Date);
        if (used < dailyLimit) return candidate;
        var nextLocal = localCandidate.Date.AddDays(1).Add(originalLocal.TimeOfDay);
        candidate = new DateTimeOffset(nextLocal, zone.GetUtcOffset(nextLocal));
    }
}

static async Task<string> SaveUploadedFileAsync(IFormFile file, string extension, Dictionary<string, string> savedMedia, string uploadDirectory, CancellationToken ct)
{
    var mediaKey = $"{file.FileName}:{file.Length}";
    if (savedMedia.TryGetValue(mediaKey, out var existingPath)) return existingPath;
    var storedName = $"{Guid.NewGuid():N}{extension}";
    var path = Path.Combine(uploadDirectory, storedName);
    await using var output = File.Create(path);
    await file.CopyToAsync(output, ct);
    savedMedia[mediaKey] = path;
    return path;
}

public sealed record AccountNameRequest(string Name);
