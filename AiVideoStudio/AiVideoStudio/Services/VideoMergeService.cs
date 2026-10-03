using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiVideoStudio.Services;

public interface IVideoMergeService
{
    Task<string> MergeVideosAsync(IEnumerable<string> videoPaths, string outputPath, CancellationToken ct = default);
    Task<string> AssembleFilmAsync(IReadOnlyList<string> videoSegments, IReadOnlyList<(string clip, double delaySec)> audioParts, string outputPath, CancellationToken ct = default);
    Task<double?> GetAudioPlayableDurationAsync(string path, CancellationToken ct = default);
    Task<double?> GetDurationAsync(string path, CancellationToken ct = default);
    Task<(double? video, double? audio)> GetStreamDurationsAsync(string path, CancellationToken ct = default);
    Task<string> ShiftAudioAsync(string path, double delaySec, CancellationToken ct = default);
    Task<string> TrimStartAsync(string path, double startSeconds, CancellationToken ct = default);
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

    private static async Task<bool> HasAudioAsync(string path, CancellationToken ct)
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
            using var _ = ct.Register(() => { try { if (!pr.HasExited) pr.Kill(); } catch { } });
            var json = await pr.StandardOutput.ReadToEndAsync();
            await pr.WaitForExitAsync();
            ct.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(json);
            foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
                if (s.TryGetProperty("codec_type", out var c) && c.GetString() == "audio")
                    return true;
            return false;
        }
        catch { return true; } // ponytail: if probing fails, concat as-is; a broken merge errors loudly, silence never silently
    }

    private static async Task<string> RunFfmpegCaptureAsync(string args, CancellationToken ct = default)
    {
        using var pr = Process.Start(new ProcessStartInfo
        {
            FileName = Ffmpeg,
            Arguments = args,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException($"Could not start '{Ffmpeg}'. Install ffmpeg or set its path in Settings.");
        // ponytail: Cancel kills the child; drain stderr concurrently (progress spam fills the pipe and deadlocks WaitForExit on long re-encodes)
        using var _ = ct.Register(() => { try { if (!pr.HasExited) pr.Kill(); } catch { } });
        var errTask = pr.StandardError.ReadToEndAsync();
        await pr.WaitForExitAsync();
        var errFull = await errTask;
        ct.ThrowIfCancellationRequested();
        if (pr.ExitCode != 0)
        {
            // ponytail: last lines carry the cause; the version banner never does
            var err = errFull;
            if (err.Length > 800) err = "[...]" + err[^800..];
            throw new InvalidOperationException($"ffmpeg exit {pr.ExitCode}: {err}");
        }
        return errFull;
    }

    private static Task RunFfmpegAsync(string args, CancellationToken ct = default)
        => RunFfmpegCaptureAsync(args, ct);

    // ponytail: container metadata can claim a full-length track holding 8s of samples; decode to null and read the real played length from time=
    public async Task<double?> GetAudioPlayableDurationAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var err = await RunFfmpegCaptureAsync($"-v info -i \"{path}\" -map 0:a -f null -", ct);
            Match? last = null;
            foreach (Match m in Regex.Matches(err, @"time=(\d+):(\d+):([\d.]+)")) last = m;
            if (last == null) return null;
            return int.Parse(last.Groups[1].Value) * 3600 + int.Parse(last.Groups[2].Value) * 60
                + double.Parse(last.Groups[3].Value, CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    // ponytail: ffprobe duration so callers can do timeline math; null when unknown, callers fall back to untrimmed behavior
    public async Task<double?> GetDurationAsync(string path, CancellationToken ct = default)
    {
        try
        {
            using var pr = Process.Start(new ProcessStartInfo
            {
                FileName = Ffprobe,
                Arguments = $"-v error -show_entries format=duration -of json \"{path}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (pr == null) return null;
            using var _ = ct.Register(() => { try { if (!pr.HasExited) pr.Kill(); } catch { } });
            var json = await pr.StandardOutput.ReadToEndAsync();
            await pr.WaitForExitAsync();
            ct.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("format", out var fmt) &&
                fmt.TryGetProperty("duration", out var dd) &&
                double.TryParse(dd.GetRawText().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
            return null;
        }
        catch { return null; }
    }

    // ponytail: per-stream durations tell short-audio (stitch picked gen-only) apart from silent-audio (TTS produced nothing); "N/A" parses as missing, not zero
    public async Task<(double? video, double? audio)> GetStreamDurationsAsync(string path, CancellationToken ct = default)
    {
        double? video = null, audio = null;
        try
        {
            using var pr = Process.Start(new ProcessStartInfo
            {
                FileName = Ffprobe,
                Arguments = $"-v error -show_entries stream=codec_type,duration -of json \"{path}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (pr == null) return (null, null);
            using var _ = ct.Register(() => { try { if (!pr.HasExited) pr.Kill(); } catch { } });
            var json = await pr.StandardOutput.ReadToEndAsync();
            await pr.WaitForExitAsync();
            ct.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("streams", out var streams))
                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.TryGetProperty("codec_type", out var c) ? c.GetString() : null;
                    double? dur = s.TryGetProperty("duration", out var dd) &&
                        double.TryParse(dd.GetRawText().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
                    if (type == "video") video ??= dur;
                    else if (type == "audio") audio ??= dur;
                }
        }
        catch { }
        return (video, audio);
    }

    // ponytail: continuation clips carry the new segment's audio at 0s; push it to the gen offset and backfill silence so the clip plays correctly on its own
    public async Task<string> ShiftAudioAsync(string path, double delaySec, CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "_shift.mp4");
        await RunFfmpegAsync($"-i \"{path}\" -itsoffset {delaySec.ToString("0.###", CultureInfo.InvariantCulture)} -i \"{path}\" -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -shortest \"{tmp}\"", ct);
        return tmp;
    }

    // ponytail: re-encode (not -c copy) so the cut lands on the exact frame; caller deletes the temp file
    public async Task<string> TrimStartAsync(string path, double startSeconds, CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "_tail.mp4");
        // ponytail: veryfast preset; intermediate tails get re-encoded once more at final concat only if they lack audio
        await RunFfmpegAsync($"-y -ss {startSeconds.ToString("0.###", CultureInfo.InvariantCulture)} -i \"{path}\" -c:v libx264 -preset veryfast -crf 16 -pix_fmt yuv420p -c:a aac \"{tmp}\"", ct);
        return tmp;
    }

    // ponytail: continuation templates leave each new segment's audio at 0s of its own clip while the video stitches; rebuild the audio timeline with delays (scene1 full + each later clip's audio at its segment offset), video stays a stream-copy concat
    public async Task<string> AssembleFilmAsync(IReadOnlyList<string> videoSegments, IReadOnlyList<(string clip, double delaySec)> audioParts, string outputPath, CancellationToken ct = default)
    {
        var list = Path.GetTempFileName() + ".txt";
        var videoOnly = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "_v.mp4");
        var audioOnly = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "_a.m4a");
        try
        {
            ct.ThrowIfCancellationRequested();
            await File.WriteAllLinesAsync(list, videoSegments.Select(p => $"file '{p.Replace("'", "'\\''")}'"), ct);
            await RunFfmpegAsync($"-y -f concat -safe 0 -i \"{list}\" -c copy -an \"{videoOnly}\"", ct);
            if (audioParts.Count == 0)
            {
                File.Move(videoOnly, outputPath, overwrite: true);
                return outputPath;
            }
            var inputs = string.Join(" ", audioParts.Select(a => $"-i \"{a.clip}\""));
            var filter = string.Join(";", audioParts.Select((a, i) => $"[{i}:a]adelay={(int)Math.Round(a.delaySec * 1000)}:all=1[a{i}]"));
            var outLabel = "a0";
            if (audioParts.Count > 1)
            {
                filter += ";" + string.Concat(audioParts.Select((_, i) => $"[a{i}]")) + $"amix=inputs={audioParts.Count}:normalize=0[aout]";
                outLabel = "aout";
            }
            await RunFfmpegAsync($"-y {inputs} -filter_complex \"{filter}\" -map \"[{outLabel}]\" -c:a aac \"{audioOnly}\"", ct);
            await RunFfmpegAsync($"-y -i \"{videoOnly}\" -i \"{audioOnly}\" -c copy -shortest \"{outputPath}\"", ct);
            return outputPath;
        }
        finally
        {
            if (File.Exists(list)) try { File.Delete(list); } catch { }
            foreach (var t in new[] { videoOnly, audioOnly }) try { if (File.Exists(t)) File.Delete(t); } catch { }
        }
    }

    public async Task<string> MergeVideosAsync(IEnumerable<string> videoPaths, string outputPath, CancellationToken ct = default)
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
                ct.ThrowIfCancellationRequested();
                if (await HasAudioAsync(p, ct)) { effective.Add(p); continue; }
                var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "_silence.mp4");
                await RunFfmpegAsync($"-y -i \"{p}\" -f lavfi -i anullsrc=r=44100:cl=stereo -shortest -c:v copy -c:a aac \"{tmp}\"", ct);
                effective.Add(tmp);
                temps.Add(tmp);
            }
            var list = Path.GetTempFileName() + ".txt";
            try
            {
                await File.WriteAllLinesAsync(list, effective.Select(p => $"file '{p.Replace("'", "'\\''")}'"), ct);
                await RunFfmpegAsync($"-y -f concat -safe 0 -i \"{list}\" -c copy \"{outputPath}\"", ct);
            }
            finally { if (File.Exists(list)) File.Delete(list); }
            return outputPath;
        }
        finally { foreach (var t in temps) try { File.Delete(t); } catch { } }
    }
}
