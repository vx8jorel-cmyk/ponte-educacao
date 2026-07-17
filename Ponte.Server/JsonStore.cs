using System.Text.Json;

namespace Ponte.Server;

public sealed class JsonStore
{
    private readonly string _dataDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonStore(IWebHostEnvironment environment)
    {
        _dataDir = Environment.GetEnvironmentVariable("JORELWAST_DATA_DIR")
            ?? Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(Path.Combine(_dataDir, "uploads"));
    }

    public string UploadDirectory => Path.Combine(_dataDir, "uploads");

    public async Task<T?> ReadAsync<T>(string name)
    {
        await _gate.WaitAsync();
        try
        {
            var path = Path.Combine(_dataDir, name);
            if (!File.Exists(path)) return default;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, Json);
        }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync<T>(string name, T value)
    {
        await _gate.WaitAsync();
        try
        {
            var path = Path.Combine(_dataDir, name);
            var temporary = path + ".tmp";
            await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, value, Json);
            File.Move(temporary, path, true);
        }
        finally { _gate.Release(); }
    }

    public async Task<List<InstagramConnection>> GetConnectionsAsync()
    {
        var connections = await ReadAsync<List<InstagramConnection>>("connections.json") ?? [];
        if (connections.Count == 0)
        {
            var legacy = await ReadAsync<InstagramConnection>("connection.json");
            if (legacy is not null) { connections.Add(legacy); await SaveConnectionsAsync(connections); }
        }
        return connections;
    }
    public Task SaveConnectionsAsync(List<InstagramConnection> connections) => WriteAsync("connections.json", connections);
    public Task<TikTokConnection?> GetTikTokConnectionAsync() => ReadAsync<TikTokConnection>("tiktok-connection.json");
    public Task SaveTikTokConnectionAsync(TikTokConnection? connection) => WriteAsync("tiktok-connection.json", connection);
    public Task<YouTubeConnection?> GetYouTubeConnectionAsync() => ReadAsync<YouTubeConnection>("youtube-connection.json");
    public Task SaveYouTubeConnectionAsync(YouTubeConnection? connection) => WriteAsync("youtube-connection.json", connection);
    public async Task<List<ScheduledPost>> GetPostsAsync() => await ReadAsync<List<ScheduledPost>>("posts.json") ?? [];
    public Task SavePostsAsync(List<ScheduledPost> posts) => WriteAsync("posts.json", posts);
}
