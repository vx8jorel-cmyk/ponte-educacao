namespace Ponte.Server;

public sealed class PublishingWorker(JsonStore store, InstagramService instagram, TikTokService tiktok, YouTubeService youtube, VideoBrandingService branding, ILogger<PublishingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var posts = await store.GetPostsAsync();
                var now = DateTimeOffset.UtcNow;
                var due = posts.Where(x =>
                    x.Status == "scheduled" &&
                    x.AiStatus is not ("pending" or "analyzing") &&
                    x.PublishAt <= now &&
                    (x.NextRetryAt is null || x.NextRetryAt <= now)).ToList();
                foreach (var post in due)
                {
                    post.Status = "publishing"; await store.SavePostsAsync(posts);
                    try
                    {
                        if (post.Platform == "instagram" && post.Type == "REELS")
                        {
                            try { post.MediaPath = await branding.PrepareAsync(post, stoppingToken); await store.SavePostsAsync(posts); }
                            catch (Exception brandingError) { logger.LogWarning(brandingError, "Marca dinâmica indisponível para {PostId}; usando mídia original.", post.Id); }
                        }
                        post.InstagramMediaId = post.Platform switch
                        {
                            "tiktok" => await tiktok.PublishAsync(post, stoppingToken),
                            "youtube" => await youtube.PublishAsync(post, stoppingToken),
                            _ => await instagram.PublishAsync(post, stoppingToken)
                        };
                        post.Status = "published";
                        post.Error = null;
                        post.NextRetryAt = null;
                    }
                    catch (Exception ex)
                    {
                        post.PublishAttempts++;
                        post.Error = ex.Message;
                        if (IsTemporaryFailure(ex) && post.PublishAttempts < 6)
                        {
                            var delay = TimeSpan.FromMinutes(Math.Min(30, Math.Pow(2, post.PublishAttempts) * 2));
                            post.Status = "scheduled";
                            post.NextRetryAt = DateTimeOffset.UtcNow.Add(delay);
                            logger.LogWarning(ex, "Falha temporária ao publicar {PostId}; nova tentativa em {Delay}.", post.Id, delay);
                        }
                        else
                        {
                            post.Status = "failed";
                            post.NextRetryAt = null;
                            logger.LogError(ex, "Falha ao publicar {PostId}", post.Id);
                        }
                    }
                    await store.SavePostsAsync(posts);
                }
            }
            catch (Exception ex) { logger.LogError(ex, "Falha no ciclo do agendador"); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private static bool IsTemporaryFailure(Exception ex)
    {
        var message = ex.ToString();
        return ex is TimeoutException
            || message.Contains("demorou demais", StringComparison.OrdinalIgnoreCase)
            || message.Contains("temporar", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("500", StringComparison.OrdinalIgnoreCase)
            || message.Contains("502", StringComparison.OrdinalIgnoreCase)
            || message.Contains("503", StringComparison.OrdinalIgnoreCase)
            || message.Contains("504", StringComparison.OrdinalIgnoreCase);
    }
}
