namespace Ponte.Server;

public sealed class AiProcessingWorker(JsonStore store, ContentAiService ai, ILogger<AiProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var posts = await store.GetPostsAsync();
                var pending = posts.FirstOrDefault(item => item.AiStatus == "pending");
                if (pending is not null)
                {
                    var group = posts.Where(item => item.MediaPath == pending.MediaPath && item.AiStatus == "pending").ToList();
                    foreach (var item in group) item.AiStatus = "analyzing";
                    await store.SavePostsAsync(posts);
                    try
                    {
                        var result = await ai.AnalyzeAsync(pending.MediaPath, stoppingToken);
                        foreach (var item in group)
                        {
                            item.Title = result.Title; item.Caption = result.Caption; item.AiConfidence = result.Confidence;
                            item.AiEvidence = string.Join(" | ", result.Evidence); item.AiStatus = "ready";
                        }
                    }
                    catch (Exception ex)
                    {
                        var safeTitle = Path.GetFileNameWithoutExtension(pending.OriginalFileName)
                            .Replace('-', ' ').Replace('_', ' ').Trim();
                        if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = pending.Type == "REELS" ? "Momento em destaque" : "Novo conteúdo";
                        foreach (var item in group)
                        {
                            item.Title = safeTitle;
                            item.Caption = $"▶️ {safeTitle}\n\nConteúdo autorizado publicado automaticamente.\n\nSiga o perfil para acompanhar mais conteúdos.";
                            item.AiConfidence = 0;
                            item.AiEvidence = "Metadados de contingência; a análise multimodal estava temporariamente indisponível.";
                            item.AiStatus = "fallback";
                            item.Error = null;
                        }
                        logger.LogError(ex, "Falha na análise de {MediaPath}", pending.MediaPath);
                    }
                    await store.SavePostsAsync(posts);
                    continue;
                }
            }
            catch (Exception ex) { logger.LogError(ex, "Falha no ciclo da IA"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
