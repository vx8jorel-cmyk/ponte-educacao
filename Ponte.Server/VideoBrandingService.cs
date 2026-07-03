using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Ponte.Server;

public sealed class VideoBrandingService(HttpClient http, JsonStore store, IWebHostEnvironment environment, ILogger<VideoBrandingService> logger)
{
    private readonly string _projectRoot = Directory.GetParent(environment.ContentRootPath)!.FullName;

    public async Task<string> PrepareAsync(ScheduledPost post, CancellationToken ct)
    {
        if (post.Type != "REELS") return post.MediaPath;
        var account = (await store.GetConnectionsAsync()).SingleOrDefault(item => item.UserId == post.AccountId);
        if (account is null || string.IsNullOrWhiteSpace(account.ProfilePictureUrl)) return post.MediaPath;
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{account.Username}|{account.DisplayName}|{account.ProfilePictureUrl}")))[..16];
        if (post.BrandingStatus == "ready" && post.BrandingSignature == signature && File.Exists(post.MediaPath)) return post.MediaPath;

        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) { logger.LogWarning("FFmpeg não encontrado; Reel será publicado sem marca dinâmica."); return post.MediaPath; }
        var source = !string.IsNullOrWhiteSpace(post.SourceMediaPath) && File.Exists(post.SourceMediaPath) ? post.SourceMediaPath : post.MediaPath;
        var brandingDir = Path.Combine(store.UploadDirectory, "branding");
        Directory.CreateDirectory(brandingDir);
        var avatarPath = Path.Combine(brandingDir, $"avatar-{post.AccountId}-{signature}.jpg");
        if (!File.Exists(avatarPath))
        {
            var bytes = await http.GetByteArrayAsync(account.ProfilePictureUrl, ct);
            await File.WriteAllBytesAsync(avatarPath, bytes, ct);
        }
        var output = Path.Combine(store.UploadDirectory, $"{Path.GetFileNameWithoutExtension(source)}-brand-{post.AccountId}-{signature}.mp4");
        if (!File.Exists(output))
        {
            var font = FindFont();
            var safeUsername = new string(account.Username.Where(character => char.IsLetterOrDigit(character) || character is '.' or '_').ToArray());
            var drawText = font is null ? "" : $",drawtext=fontfile='{EscapeFilterPath(font)}':text='@{safeUsername}':x=150:y=h-88:fontsize=34:fontcolor=white:shadowcolor=black@0.7:shadowx=2:shadowy=2";
            var filter = $"[1:v]scale=92:92[avatar];[0:v]drawbox=x=20:y=ih-125:w=iw-40:h=105:color=black@0.52:t=fill[base];[base][avatar]overlay=38:H-h-27{drawText}[outv]";
            var arguments = $"-hide_banner -loglevel error -y -i \"{source}\" -i \"{avatarPath}\" -filter_complex \"{filter}\" -map \"[outv]\" -map 0:a? -c:v libx264 -preset veryfast -crf 20 -c:a aac -b:a 128k -movflags +faststart \"{output}\"";
            await RunAsync(ffmpeg, arguments, ct);
        }
        post.BrandingStatus = "ready";
        post.BrandingSignature = signature;
        return output;
    }

    private string? FindFfmpeg()
    {
        var local = Path.Combine(_projectRoot, ".tools");
        if (Directory.Exists(local))
        {
            var match = Directory.EnumerateFiles(local, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg", SearchOption.AllDirectories).FirstOrDefault();
            if (match is not null) return match;
        }
        var names = OperatingSystem.IsWindows() ? new[] { "ffmpeg.exe" } : new[] { "ffmpeg" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names) { var candidate = Path.Combine(directory.Trim('"'), name); if (File.Exists(candidate)) return candidate; }
        return null;
    }

    private static string? FindFont()
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { @"C:\Windows\Fonts\arialbd.ttf", @"C:\Windows\Fonts\segoeuib.ttf" }
            : new[] { "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", "/usr/share/fonts/truetype/liberation2/LiberationSans-Bold.ttf" };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string EscapeFilterPath(string path) => path.Replace("\\", "/").Replace(":", "\\:").Replace("'", "\\'");

    private static async Task RunAsync(string executable, string arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar o editor de vídeo.");
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Editor de vídeo: {await error}");
    }
}
