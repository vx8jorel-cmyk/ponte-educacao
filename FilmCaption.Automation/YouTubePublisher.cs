using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using System.Text.Json;

static class YouTubePublisher
{
    public static async Task<IReadOnlyList<string>> PublishAsync(IReadOnlyList<Analysis> items, Func<Analysis, string> caption,
        string clientPath, string tokenPath, string schedulePath)
    {
        if (!File.Exists(clientPath))
            throw new FileNotFoundException("Credencial OAuth do YouTube não encontrada.", clientPath);

        await using var clientStream = File.OpenRead(clientPath);
        var secrets = GoogleClientSecrets.FromStream(clientStream).Secrets;
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets, new[] { YouTubeService.Scope.YoutubeUpload }, "jorel-filmes",
            CancellationToken.None, new FileDataStore(tokenPath, true));

        using var youtube = new YouTubeService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Jorel Filmes YouTube Uploader"
        });

        var ledgerPath = Path.Combine(Path.GetDirectoryName(schedulePath)!, "youtube-uploaded.json");
        var ledger = LoadLedger(ledgerPath);
        var publishedFiles = new List<string>();
        var next = GetNextPublishTime(schedulePath);
        foreach (var item in items)
        {
            var fileInfo = new FileInfo(item.FilePath);
            var previous = ledger.Entries.FirstOrDefault(entry =>
                entry.FileName.Equals(fileInfo.Name, StringComparison.OrdinalIgnoreCase)
                && (entry.Length == 0 || entry.Length == fileInfo.Length));
            if (previous is not null)
            {
                Console.WriteLine($"Upload já registrado ({previous.VideoId}); movendo sem reenviar: {fileInfo.Name}");
                publishedFiles.Add(item.FilePath);
                continue;
            }

            var title = item.YoutubeTitle;
            if (title.Length > 100) title = title[..100];
            var video = new Video
            {
                Snippet = new VideoSnippet { Title = title, Description = caption(item), CategoryId = CategoryId(item.ContentType) },
                Status = new VideoStatus { PrivacyStatus = "private", PublishAtDateTimeOffset = next }
            };

            Console.WriteLine($"Enviando {Path.GetFileName(item.FilePath)} para {next:dd/MM/yyyy HH:mm}...");
            await using var stream = File.OpenRead(item.FilePath);
            var request = youtube.Videos.Insert(video, "snippet,status", stream, MimeType(item.FilePath));
            string? videoId = null;
            request.ResponseReceived += uploaded => videoId = uploaded.Id;
            var progress = await request.UploadAsync();
            if (progress.Status == Google.Apis.Upload.UploadStatus.Failed)
                throw progress.Exception ?? new InvalidOperationException("Falha no upload para o YouTube.");

            Console.WriteLine($"Agendado no YouTube: {videoId ?? "ID pendente"}");
            SaveSchedule(schedulePath, next);
            ledger.Entries.Add(new UploadEntry(fileInfo.Name, fileInfo.Length, videoId ?? "", DateTimeOffset.Now, next));
            SaveLedger(ledgerPath, ledger);
            publishedFiles.Add(item.FilePath);
            next = next.AddDays(1);
        }

        return publishedFiles;
    }

    private static DateTimeOffset GetNextPublishTime(string path)
    {
        var zone = FindSaoPauloTimeZone();
        var slots = new[]
        {
            new TimeSpan(8, 0, 0), new TimeSpan(9, 30, 0), new TimeSpan(11, 0, 0),
            new TimeSpan(12, 30, 0), new TimeSpan(14, 0, 0), new TimeSpan(15, 30, 0),
            new TimeSpan(17, 0, 0), new TimeSpan(18, 30, 0), new TimeSpan(20, 0, 0),
            new TimeSpan(21, 30, 0)
        };
        var minimum = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, zone).AddMinutes(30);
        var state = File.Exists(path)
            ? JsonSerializer.Deserialize<ScheduleState>(File.ReadAllText(path))
            : null;
        DateTimeOffset? after = state is null ? null : TimeZoneInfo.ConvertTime(state.LastPublishAt, zone);
        var startDate = after is { } last && last.Date > minimum.Date ? last.Date : minimum.Date;

        for (var day = 0; day < 370; day++)
        {
            var date = startDate.AddDays(day).Date;
            foreach (var slot in slots)
            {
                var local = date.Add(slot);
                var candidate = new DateTimeOffset(local, zone.GetUtcOffset(local));
                if (candidate >= minimum && (after is null || candidate > after.Value)) return candidate;
            }
        }
        throw new InvalidOperationException("Não foi possível calcular o próximo horário de publicação.");
    }

    private static void SaveSchedule(string path, DateTimeOffset publishAt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new ScheduleState(publishAt), new JsonSerializerOptions { WriteIndented = true }));
    }

    private static UploadLedger LoadLedger(string path)
    {
        if (!File.Exists(path)) return new UploadLedger(new List<UploadEntry>());
        return JsonSerializer.Deserialize<UploadLedger>(File.ReadAllText(path))
            ?? new UploadLedger(new List<UploadEntry>());
    }

    private static void SaveLedger(string path, UploadLedger ledger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(ledger, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    private static TimeZoneInfo FindSaoPauloTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time"); }
    }

    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mov" => "video/quicktime", ".webm" => "video/webm", ".m4v" => "video/x-m4v", _ => "video/mp4"
    };

    private static string CategoryId(string type) => type switch
    {
        "ESPORTE" => "17", "PODCAST" => "22", "LIVE" => "24", "FILME" => "1", _ => "22"
    };

    private sealed record ScheduleState(DateTimeOffset LastPublishAt);
    private sealed record UploadEntry(string FileName, long Length, string VideoId,
        DateTimeOffset UploadedAt, DateTimeOffset PublishAt);
    private sealed record UploadLedger(List<UploadEntry> Entries);
}
