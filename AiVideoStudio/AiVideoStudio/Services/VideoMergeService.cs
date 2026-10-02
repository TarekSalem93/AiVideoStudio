using System.Diagnostics;
using System.Text.Json;

namespace AiVideoStudio.Services;

public interface IVideoMergeService
{
    Task<string> MergeVideosAsync(IEnumerable<string> videoPaths, string outputPath);
}

public class VideoMergeService : IVideoMergeService
{
    private static string Ffmpeg => AppSettings.FfmpegPath;

    private static string Ffprobe
    {
        get
        {
            var ff = Ffmpeg;
            try
            {
                var dir = Path.GetDirectoryName(ff);
                var exe = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
                if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir, exe);
            }
            catch { }
            return "ffprobe";
        }
    }

    private static async Task<bool> HasAudioAsync(string path)
    {
        try
        {
            using var pr = Process.Start(new ProcessStartInfo
            {
                FileName = Ffprobe,
                Arguments = $"-v error -show_entries stream=codec_type -of json \"{path}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (pr == null) return true;
            var json = await pr.StandardOutput.ReadToEndAsync();
            await pr.WaitForExitAsync();
            using var doc = JsonDocument.Parse(json);
            foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
                if (s.TryGetProperty("codec_type", out var c) && c.GetString() == "audio")
                    return true;
            return false;
        }
        catch { return true; } // ponytail: if probing fails, concat as-is; a broken merge errors loudly, silence never silently
    }

    private static async Task RunFfmpegAsync(string args)
    {
        using var pr = Process.Start(new ProcessStartInfo
        {
            FileName = Ffmpeg,
            Arguments = args,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException($"Could not start '{Ffmpeg}'. Install ffmpeg or set its path in Settings.");
        await pr.WaitForExitAsync();
        if (pr.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exit {pr.ExitCode}: {await pr.StandardError.ReadToEndAsync()}");
    }

    public async Task<string> MergeVideosAsync(IEnumerable<string> videoPaths, string outputPath)
    {
        var inputs = videoPaths.ToList();
        if (inputs.Count == 0) throw new ArgumentException("No clips to merge.");
        // ponytail: clips without an audio track desync stream-copy concat, so pad them with silence first
        var effective = new List<string>();
        var temps = new List<string>();
        try
        {
            foreach (var p in inputs)
            {
                if (await HasAudioAsync(p)) { effective.Add(p); continue; }
                var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "_silence.mp4");
                await RunFfmpegAsync($"-y -i \"{p}\" -f lavfi -i anullsrc=r=44100:cl=stereo -shortest -c:v copy -c:a aac \"{tmp}\"");
                effective.Add(tmp);
                temps.Add(tmp);
            }
            var list = Path.GetTempFileName() + ".txt";
            try
            {
                await File.WriteAllLinesAsync(list, effective.Select(p => $"file '{p.Replace("'", "'\\''")}'"));
                await RunFfmpegAsync($"-y -f concat -safe 0 -i \"{list}\" -c copy \"{outputPath}\"");
            }
            finally { if (File.Exists(list)) File.Delete(list); }
            return outputPath;
        }
        finally { foreach (var t in temps) try { File.Delete(t); } catch { } }
    }
}
