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
        var layout = Math.Abs(post.Id.GetHashCode()) % 4;
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{account.Username}|{account.DisplayName}|{account.ProfilePictureUrl}|layout:{layout}")))[..16];
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
            var safeUsername = EscapeDrawText("@" + new string(account.Username.Where(character => char.IsLetterOrDigit(character) || character is '.' or '_').ToArray()));
            var safeName = EscapeDrawText(string.IsNullOrWhiteSpace(account.DisplayName) ? account.Username : account.DisplayName);
            var geometry = LayoutGeometry(layout);
            var drawText = font is null ? "" :
                $",drawtext=fontfile='{EscapeFilterPath(font)}':text='{safeName}':x={geometry.NameX}:y={geometry.NameY}:fontsize=30:fontcolor=white:shadowcolor=black@0.75:shadowx=2:shadowy=2" +
                $",drawtext=fontfile='{EscapeFilterPath(font)}':text='{safeUsername}':x={geometry.UserX}:y={geometry.UserY}:fontsize=24:fontcolor=white@0.86:shadowcolor=black@0.75:shadowx=2:shadowy=2";
            var filter = $"[1:v]scale=92:92[avatar];[0:v]drawbox=x={geometry.BoxX}:y={geometry.BoxY}:w={geometry.BoxW}:h=116:color=black@0.50:t=fill[base];[base][avatar]overlay={geometry.AvatarX}:{geometry.AvatarY}{drawText}[outv]";
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
    private static string EscapeDrawText(string text) => text.Replace("\\", "\\\\").Replace(":", "\\:").Replace("'", "\\'").Replace("%", "\\%");

    private static BrandingLayout LayoutGeometry(int layout) => layout switch
    {
        1 => new("iw-430", "ih-140", "410", "W-w-38", "w-285", "h-101", "w-285", "h-66"),
        2 => new("20", "24", "390", "38", "150", "52", "150", "87"),
        3 => new("iw-430", "24", "410", "W-w-38", "w-285", "52", "w-285", "87"),
        _ => new("20", "ih-140", "390", "38", "150", "h-101", "150", "h-66")
    };

    private sealed record BrandingLayout(string BoxX, string BoxY, string BoxW, string AvatarX, string NameX, string NameY, string UserX, string UserY)
    {
        public string AvatarY => BoxY == "24" ? "36" : "H-h-39";
    }

    private static async Task RunAsync(string executable, string arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar o editor de vídeo.");
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Editor de vídeo: {await error}");
    }
}
