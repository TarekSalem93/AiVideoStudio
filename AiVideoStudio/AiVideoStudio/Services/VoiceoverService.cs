using System.Diagnostics;

namespace AiVideoStudio.Services;

public interface IVoiceoverService
{
    Task<bool> IsAvailableAsync();
    Task<string> SpeakAsync(string text, string? voice, string outPath, CancellationToken ct = default);
    Task<string> MuxAsync(string videoPath, string audioPath, string outPath, CancellationToken ct = default);
}

public class VoiceoverService : IVoiceoverService
{
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var pr = Process.Start(new ProcessStartInfo
            {
                FileName = "edge-tts",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (pr == null) return false;
            await pr.WaitForExitAsync();
            return pr.ExitCode == 0;
        }
        catch { return false; }
    }

    public async Task<string> SpeakAsync(string text, string? voice, string outPath, CancellationToken ct = default)
    {
        voice = string.IsNullOrWhiteSpace(voice) ? AppSettings.Voice : voice;
        var tmp = Path.GetTempFileName() + ".txt";
        await File.WriteAllTextAsync(tmp, text, ct);
        try
        {
            using var pr = Process.Start(new ProcessStartInfo
            {
                FileName = "edge-tts",
                Arguments = $"--voice {voice} --file \"{tmp}\" --write-media \"{outPath}\"",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Could not start edge-tts. Install it: pip install edge-tts");
            await pr.WaitForExitAsync(ct);
            if (pr.ExitCode != 0)
                throw new InvalidOperationException($"edge-tts exit {pr.ExitCode}: {await pr.StandardError.ReadToEndAsync()}");
            return outPath;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public async Task<string> MuxAsync(string videoPath, string audioPath, string outPath, CancellationToken ct = default)
    {
        using var pr = Process.Start(new ProcessStartInfo
        {
            FileName = AppSettings.FfmpegPath,
            Arguments = $"-y -i \"{videoPath}\" -i \"{audioPath}\" -c:v copy -c:a aac -shortest \"{outPath}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start ffmpeg. Set its path in Settings.");
        await pr.WaitForExitAsync(ct);
        if (pr.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exit {pr.ExitCode}: {await pr.StandardError.ReadToEndAsync()}");
        return outPath;
    }
}
