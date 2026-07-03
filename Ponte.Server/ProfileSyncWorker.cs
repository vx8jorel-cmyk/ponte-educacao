namespace Ponte.Server;

public sealed class ProfileSyncWorker(InstagramService instagram, ILogger<ProfileSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await instagram.RefreshProfilesAsync(stoppingToken, force: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Não foi possível sincronizar os perfis do Instagram."); }
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }
}
