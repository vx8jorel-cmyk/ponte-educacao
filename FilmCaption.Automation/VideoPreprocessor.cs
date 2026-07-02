using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

static class VideoPreprocessor
{
    private const double MaximumSeconds = 59;

    public static async Task SplitLongVideosAsync(string inbox, string channelRoot, int maxFiles)
    {
        var ffmpeg = FindTool("ffmpeg.exe");
        var ffprobe = FindTool("ffprobe.exe");
        if (ffmpeg is null || ffprobe is null)
        {
            Console.Error.WriteLine("FFmpeg não encontrado; vídeos longos não serão recortados.");
            return;
        }

        var candidates = Directory.EnumerateFiles(inbox, "*", SearchOption.AllDirectories)
            .Where(IsVideo)
            .OrderBy(path => path)
            .Take(maxFiles)
            .ToArray();

        foreach (var source in candidates)
        {
            var duration = await DurationAsync(ffprobe, source);
            if (duration <= MaximumSeconds + 0.05) continue;

            var partCount = (int)Math.Ceiling(duration / MaximumSeconds);
            var partDuration = duration / partCount;
            var tempDirectory = Path.Combine(Path.GetDirectoryName(source)!, $".segmentando-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            var outputs = new List<string>();

            Console.WriteLine($"Recortando {Path.GetFileName(source)} ({duration:0.0}s) em {partCount} partes...");
            for (var index = 0; index < partCount; index++)
            {
                var start = index * partDuration;
                var length = Math.Min(partDuration, duration - start);
                var output = Path.Combine(tempDirectory,
                    $"{Path.GetFileNameWithoutExtension(source)}-parte-{index + 1:D2}-de-{partCount:D2}.mp4");
                var arguments = $"-hide_banner -loglevel error -y -ss {start.ToString("0.###", CultureInfo.InvariantCulture)} " +
                    $"-i \"{source}\" -t {length.ToString("0.###", CultureInfo.InvariantCulture)} " +
                    "-map 0:v:0 -map 0:a? -c:v libx264 -preset veryfast -crf 20 -c:a aac -b:a 128k -movflags +faststart " +
                    $"\"{output}\"";
                await RunAsync(ffmpeg, arguments);
                outputs.Add(output);
            }

            foreach (var output in outputs)
                File.Move(output, Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileName(output)));
            Directory.Delete(tempDirectory);

            var originals = Path.Combine(channelRoot, "processados", "originais-longos");
            Directory.CreateDirectory(originals);
            var archived = UniquePath(originals, Path.GetFileName(source));
            File.Move(source, archived);
            Console.WriteLine($"Original longo arquivado: {archived}");
        }
    }

    private static bool IsVideo(string path) => new[] { ".mp4", ".mov", ".m4v", ".webm" }
        .Contains(Path.GetExtension(path).ToLowerInvariant());

    private static async Task<double> DurationAsync(string ffprobe, string path)
    {
        var output = await RunAsync(ffprobe, $"-v error -show_entries format=duration -of json \"{path}\"");
        var json = JsonNode.Parse(output)!;
        return double.Parse(json["format"]!["duration"]!.GetValue<string>(), CultureInfo.InvariantCulture);
    }

    private static async Task<string> RunAsync(string executable, string arguments)
    {
        var startInfo = new ProcessStartInfo(executable, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Não foi possível iniciar {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} falhou: {await stderr}");
        return await stdout;
    }

    private static string? FindTool(string name)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(candidate)) return candidate;
        }
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");
        return Directory.Exists(packages)
            ? Directory.EnumerateFiles(packages, name, SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    private static string UniquePath(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        return !File.Exists(path) ? path : Path.Combine(directory,
            $"{Path.GetFileNameWithoutExtension(name)}-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(name)}");
    }
}
