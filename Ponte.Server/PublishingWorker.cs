namespace Ponte.Server;

public sealed class PublishingWorker(JsonStore store, InstagramService instagram, TikTokService tiktok, VideoBrandingService branding, ILogger<PublishingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var posts = await store.GetPostsAsync();
                var due = posts.Where(x => x.Status == "scheduled" && x.AiStatus is not ("pending" or "analyzing") && x.PublishAt <= DateTimeOffset.UtcNow).ToList();
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
                        post.InstagramMediaId = post.Platform == "tiktok" ? await tiktok.PublishAsync(post, stoppingToken) : await instagram.PublishAsync(post, stoppingToken);
                        post.Status = "published";
                    }
                    catch (Exception ex) { post.Status = "failed"; post.Error = ex.Message; logger.LogError(ex, "Falha ao publicar {PostId}", post.Id); }
                    await store.SavePostsAsync(posts);
                }
            }
            catch (Exception ex) { logger.LogError(ex, "Falha no ciclo do agendador"); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
