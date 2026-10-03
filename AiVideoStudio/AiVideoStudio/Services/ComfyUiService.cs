using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AiVideoStudio.Services;

public interface IComfyUiService
{
    Task<bool> IsRunningAsync();
    Task EnsureRunningAsync();
    List<string> ListTemplates();
    Task<string> QueueWorkflowAsync(string workflowJson, int? seed = null);
    Task<List<string>> QueueStoryAsync(string storyPrompt, string? templateName = null, int? seed = null, int steps = 6, List<string>? photoPaths = null, List<string>? videoPaths = null, List<string>? audioPaths = null, Action<string>? onProgress = null, CancellationToken ct = default);
    Task<List<string>> QueueSceneAsync(string h3prompt, string? templateName, long seed, int steps, string? aspect, double? megapixels, List<string>? photoPaths, List<string>? videoPaths, List<string>? audioPaths, string? prevClipLocal, Action<string>? onProgress = null, CancellationToken ct = default);
    Task<string> UploadImageAsync(string localPath, CancellationToken ct = default);
    Task<string> DownloadOutputAsync(string filename, string subfolder, string type, CancellationToken ct = default);
}

public class ComfyUiService : IComfyUiService
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly SemaphoreSlim _renderLock = new(1, 1); // ponytail: 1 render at a time; VRAM crashes cost more than any queue UI would
    private static readonly string[] TemplateDirs =
    {
        Path.Combine(AppContext.BaseDirectory, "Workflows"),
        @"C:\DEV\ai\AiVideoStudio\AiVideoStudio\Workflows"
    };

    private static string Url(string path) => AppSettings.ComfyUrl + path;

    public async Task<bool> IsRunningAsync()
    {
        try
        {
            var res = await _httpClient.GetAsync(Url("/system_stats"));
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ponytail: launch desktop shortcut if ComfyUI is down; manual start is the fallback, not a watcher service
    public async Task EnsureRunningAsync()
    {
        if (await IsRunningAsync()) return;
        var lnk = FindShortcut("ComfyUI-KA");
        if (lnk == null)
            throw new InvalidOperationException("ComfyUI not running and no ComfyUI-KA shortcut found on Desktop. Please start ComfyUI manually.");
        Process.Start(new ProcessStartInfo { FileName = lnk, UseShellExecute = true });
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(2000);
            if (await IsRunningAsync()) return;
        }
        throw new InvalidOperationException("Launched ComfyUI-KA but it did not respond within 60s. Check the ComfyUI window.");
    }

    private static string? FindShortcut(string name)
    {
        foreach (var d in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Desktop), @"C:\Users\Public\Desktop" })
        {
            try
            {
                var hit = Directory.GetFiles(d, name + "*.lnk").FirstOrDefault();
                if (hit != null) return hit;
            }
            catch { }
        }
        return null;
    }

    public List<string> ListTemplates()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in TemplateDirs)
        {
            try { foreach (var f in Directory.GetFiles(d, "*.json")) names.Add(Path.GetFileName(f)); }
            catch { }
        }
        return names.OrderBy(n => n).ToList();
    }

    private static string ResolveTemplate(string name)
    {
        foreach (var d in TemplateDirs)
        {
            var p = Path.Combine(d, name);
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        throw new FileNotFoundException($"Template '{name}' not found. Export API format from ComfyUI (Dev Mode) and drop it in a Workflows folder.");
    }

    private static JsonObject? FindNode(JsonObject template, string classType)
    {
        foreach (var kv in template)
            if (kv.Value is JsonObject o && o["class_type"]?.GetValue<string>() == classType)
                return o;
        return null;
    }

    // ponytail: mutate by class_type so single_pass/continuation templates work without per-file node-id maps; id fallbacks cover the seed_hunter file only
    public Task<List<string>> QueueStoryAsync(string storyPrompt, string? templateName = null, int? seed = null, int steps = 6, List<string>? photoPaths = null, List<string>? videoPaths = null, List<string>? audioPaths = null, Action<string>? onProgress = null, CancellationToken ct = default)
        => QueueCore(storyPrompt, templateName, seed ?? Random.Shared.Next(), steps, null, null, photoPaths, videoPaths, audioPaths, onProgress, ct);

    // ponytail: chaining = previous clip rides video slot 1 through the media loader into ref_video_0; no workflow surgery
    public Task<List<string>> QueueSceneAsync(string h3prompt, string? templateName, long seed, int steps, string? aspect, double? megapixels, List<string>? photoPaths, List<string>? videoPaths, List<string>? audioPaths, string? prevClipLocal, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var ordered = new List<string>();
        if (!string.IsNullOrWhiteSpace(prevClipLocal) && File.Exists(prevClipLocal)) ordered.Add(prevClipLocal);
        if (videoPaths != null) ordered.AddRange(videoPaths.Where(File.Exists));
        return QueueCore(h3prompt, templateName, seed, steps, aspect, megapixels, photoPaths, ordered.Take(3).ToList(), audioPaths, onProgress, ct);
    }

    private async Task<List<string>> QueueCore(string storyPrompt, string? templateName, long seed, int steps, string? aspect, double? megapixels, List<string>? photoPaths, List<string>? videoPaths, List<string>? audioPaths, Action<string>? onProgress, CancellationToken ct)
    {
        onProgress?.Invoke("Waiting for render slot (1 at a time)...");
        await _renderLock.WaitAsync(ct);
        try
        {
            await EnsureRunningAsync();
            var node = JsonNode.Parse(ResolveTemplate(templateName ?? AppSettings.Template))!.AsObject();

            var promptNode = FindNode(node, "H3PromptIDE") ?? node["365"] as JsonObject;
            if (promptNode?["inputs"] is JsonObject pi) pi["prompt"] = storyPrompt;

            var k = 0;
            var seeded = false;
            foreach (var kv in node)
                if (kv.Value is JsonObject s && s["class_type"]?.GetValue<string>() == "easy seed" && s["inputs"] is JsonObject si)
                { si["seed"] = seed + k++; seeded = true; }
            if (!seeded)
                foreach (var id in new[] { "16", "103" })
                    if (node[id] is JsonObject s2 && s2["inputs"] is JsonObject si2)
                        si2["seed"] = seed + (id == "103" ? 1 : 0);

            if (aspect != null || megapixels.HasValue)
            {
                var rs = FindNode(node, "ResolutionSelector");
                if (rs?["inputs"] is JsonObject ri)
                {
                    if (aspect != null) ri["aspect_ratio"] = aspect;
                    if (megapixels.HasValue) ri["megapixels"] = megapixels.Value;
                }
            }
            if (node["22:8"] is JsonObject legacy && legacy["inputs"] is JsonObject legacyIn)
                legacyIn["value"] = steps;
            else
                foreach (var kv in node)
                    if (kv.Value is JsonObject b && b["class_type"]?.GetValue<string>() == "BasicScheduler"
                        && b["inputs"] is JsonObject bi && bi["steps"] is JsonValue)
                        bi["steps"] = steps;

            // media loader: slots map to <Picture N>/<Video N>/<Audio N> by position
            // ponytail: scene 1 has no previous clip but continuation templates crash on empty video slots (.shape); seed a blank clip and render gen-only so it just works
            var videos = new List<string>(videoPaths ?? new());
            var freshStart = false;
            string? seedError = null;
            if (FindVideoNeedyNodes(node).Count > 0 && !TemplateDefaultHasVideo(node) && !videos.Any(File.Exists))
            {
                try
                {
                    onProgress?.Invoke("Scene 1: no reference clip — seeding a blank one...");
                    videos.Add(await EnsureFreshStartClipAsync(ct));
                    freshStart = true;
                }
                catch (Exception ex) { seedError = ex.Message; }
            }
            PreflightMediaCheck(node, TemplateDefaultHasVideo(node) || videos.Any(File.Exists), seedError);
            var media = new JsonArray();
            async Task AddMedia(List<string>? paths, string kind, int max)
            {
                if (paths == null) return;
                var n = 0;
                foreach (var p in paths.Take(max))
                {
                    ct.ThrowIfCancellationRequested();
                    onProgress?.Invoke($"Uploading {kind} {++n}/{Math.Min(paths.Count, max)}...");
                    var name = await UploadImageAsync(p, ct);
                    int w = 1088, h = 1928;
                    double? dur = null;
                    if (kind == "picture") (w, h) = GetImageDimensions(p);
                    else (w, h, dur) = await ProbeMediaAsync(p);
                    media.Add(new JsonObject
                    {
                        ["kind"] = kind,
                        ["file"] = $"{name} [input]",
                        ["name"] = name,
                        ["duration"] = dur.HasValue ? JsonValue.Create(dur.Value) : null,
                        ["width"] = w,
                        ["height"] = h,
                        ["has_audio"] = kind == "video",
                        ["audio_mode"] = "off",
                        ["uid"] = RandomUid()
                    });
                }
            }
            await AddMedia(photoPaths, "picture", 9);
            await AddMedia(videos, "video", 3);
            await AddMedia(audioPaths, "audio", 3);
            if (media.Count > 0)
            {
                var loader = FindNode(node, "MiniMaxH3MediaLoader") ?? node["416"] as JsonObject;
                if (loader?["inputs"] is JsonObject li) li["media_state"] = media.ToJsonString();
            }
            if (freshStart) BypassContinuationStitch(node);

            var classMap = node
                .Where(kv => kv.Value is JsonObject o && o["class_type"]?.GetValue<string>() != null)
                .ToDictionary(kv => kv.Key, kv => ((JsonObject)kv.Value!)["class_type"]!.GetValue<string>());
            var clientId = Guid.NewGuid().ToString("N");
            var payload = JsonSerializer.Serialize(new { prompt = node, client_id = clientId });
            onProgress?.Invoke("Queueing workflow...");
            var res = await _httpClient.PostAsync(Url("/prompt"), new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"ComfyUI {(int)res.StatusCode}: {body}");
            var promptId = JsonDocument.Parse(body).RootElement.GetProperty("prompt_id").GetString()!;

            using var wsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var wsTask = ListenWsAsync(clientId, promptId, classMap, onProgress, wsCts.Token);
            try { return await PollHistoryAsync(promptId, onProgress, ct); }
            finally { wsCts.Cancel(); try { await wsTask; } catch { } }
        }
        finally { _renderLock.Release(); }
    }

    // ponytail: fail in milliseconds with a readable reason instead of burning GPU minutes on a graph that can never succeed
    private static void PreflightMediaCheck(JsonObject template, bool mediaHasVideo, string? seedError = null)
    {
        var needy = FindVideoNeedyNodes(template);
        // splitter outputs 0-8 are pictures, 9+ are video/audio; with no video loaded those outputs are None and crash downstream (.shape)
        if (needy.Count > 0 && !mediaHasVideo)
            throw new InvalidOperationException(
                $"Template needs a reference video but none is loaded (nodes {string.Join(",", needy.Distinct())} read empty video slots and ComfyUI would fail with 'NoneType has no attribute shape'). " +
                "Fix one way: load any video in the app's Videos picker (scene 2+ gets the previous clip automatically), or in ComfyUI run the workflow once with continuation OFF and re-export API format."
                + (seedError != null ? $" (auto blank-seed also failed: {seedError})" : ""));
    }

    private static List<string> FindVideoNeedyNodes(JsonObject template)
    {
        var splitters = template
            .Where(kv => kv.Value is JsonObject o && o["class_type"]?.GetValue<string>() == "MiniMaxH3ReferenceSplitter")
            .Select(kv => kv.Key).ToHashSet();
        if (splitters.Count == 0) return new();
        // reference-passing nodes tolerate empty slots by design; image/latent math nodes crash on None (.shape)
        static bool Tolerant(string cls) => cls is "MiniMaxH3ReferenceToVideo" or "H3PromptReferenceInputs" || cls.Contains("Switch");
        var needy = new List<string>();
        foreach (var kv in template)
        {
            if (kv.Value is not JsonObject o || o["inputs"] is not JsonObject ins) continue;
            if (Tolerant(o["class_type"]?.GetValue<string>() ?? "")) continue;
            foreach (var iv in ins)
            {
                if (iv.Value is JsonArray link && link.Count == 2
                    && link[0]?.GetValue<string>() is string src && splitters.Contains(src)
                    && link[1]?.GetValue<int>() is int slot && slot >= 9)
                { needy.Add($"{kv.Key}"); break; }
            }
        }
        return needy;
    }

    // ponytail: blank seed keeps OG overlap math alive (.shape) but must not leak into the output; OFF exports keep only the gen branch, so do the same (match by title, not id)
    private static void BypassContinuationStitch(JsonObject template)
    {
        foreach (var kv in template)
        {
            if (kv.Value is not JsonObject o || o["class_type"]?.GetValue<string>() != "Any Switch (rgthree)") continue;
            if (o["inputs"] is not JsonObject ins || !ins.ContainsKey("any_01") || !ins.ContainsKey("any_02")) continue;
            var title = o["_meta"]?["title"]?.GetValue<string>() ?? "";
            if (title.Contains("Final Video Selection") || title.Contains("Final Audio Selection"))
                ins.Remove("any_01");
        }
    }

    // ponytail: one cached black+silence clip (3s > 22-frame overlap so trim math stays positive); ffmpeg already required for merging so no new dep
    private static async Task<string> EnsureFreshStartClipAsync(CancellationToken ct)
    {
        var dir = Path.Combine(FileSystem.CacheDirectory, "comfyui");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "_freshstart_seed.mp4");
        if (File.Exists(path)) return path;
        using var pr = Process.Start(new ProcessStartInfo
        {
            FileName = AppSettings.FfmpegPath,
            Arguments = $"-y -f lavfi -i color=c=black:s=1088x1928:r=24:d=3 -f lavfi -i anullsrc=r=44100:cl=stereo:d=3 -shortest -c:v libx264 -pix_fmt yuv420p -c:a aac \"{path}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException($"Could not start '{AppSettings.FfmpegPath}'.");
        await pr.WaitForExitAsync(ct);
        if (pr.ExitCode != 0 || !File.Exists(path))
            throw new InvalidOperationException($"ffmpeg exit {pr.ExitCode}");
        return path;
    }

    private static bool TemplateDefaultHasVideo(JsonObject template)
    {
        try
        {
            var loader = FindNode(template, "MiniMaxH3MediaLoader");
            var state = loader?["inputs"]?["media_state"]?.GetValue<string>() ?? "";
            return state.Contains("\"kind\":\"video\"");
        }
        catch { return false; }
    }
    // ponytail: fire-and-forget enrichment; history polling stays the source of truth if the socket drops
    private async Task ListenWsAsync(string clientId, string promptId, Dictionary<string, string> classMap, Action<string>? onProgress, CancellationToken ct)
    {
        try
        {
            using var ws = new ClientWebSocket();
            var wsBase = AppSettings.ComfyUrl.Replace("http://", "ws://").Replace("https://", "wss://");
            await ws.ConnectAsync(new Uri($"{wsBase}/ws?clientId={clientId}"), ct);
            var buf = new byte[8192];
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType != WebSocketMessageType.Text) break;
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, r.Count));
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || !root.TryGetProperty("data", out var d)) continue;
                if (d.TryGetProperty("prompt_id", out var pid) && pid.GetString() != promptId) continue;
                switch (t.GetString())
                {
                    case "status" when d.TryGetProperty("status", out var s)
                        && s.TryGetProperty("exec_info", out var e) && e.TryGetProperty("queue_remaining", out var q)
                        && q.GetInt32() > 0:
                        onProgress?.Invoke($"Queued ({q.GetInt32()} ahead)..."); break;
                    case "executing" when d.TryGetProperty("node", out var n):
                        var id = n.GetString();
                        onProgress?.Invoke(id == null ? "Finishing..."
                            : classMap.TryGetValue(id, out var cls) ? $"Running {cls}..." : "Rendering..."); break;
                    case "progress" when d.TryGetProperty("value", out var v) && d.TryGetProperty("max", out var m):
                        onProgress?.Invoke($"Sampling {v.GetInt32()}/{m.GetInt32()}..."); break;
                }
            }
        }
        catch { }
    }

    private async Task<List<string>> PollHistoryAsync(string promptId, Action<string>? onProgress, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(5000, ct);
            var h = await _httpClient.GetAsync(Url($"/history/{promptId}"), ct);
            if (!h.IsSuccessStatusCode) continue;
            var hist = JsonDocument.Parse(await h.Content.ReadAsStringAsync(ct)).RootElement;
            if (!hist.TryGetProperty(promptId, out var entry)) { onProgress?.Invoke("Queued, waiting for worker..."); continue; }
            if (entry.TryGetProperty("status", out var st))
            {
                if (st.TryGetProperty("status_str", out var ss) && ss.GetString() == "error")
                    throw new InvalidOperationException("ComfyUI render failed: " + ParseComfyError(st));
                if (st.TryGetProperty("completed", out var done) && !done.GetBoolean())
                { onProgress?.Invoke("Rendering..."); continue; }
            }
            var files = new List<string>();
            if (entry.TryGetProperty("outputs", out var outputs))
                foreach (var o in outputs.EnumerateObject())
                    foreach (var arr in new[] { "gifs", "videos", "images" })
                        if (o.Value.TryGetProperty(arr, out var list))
                            foreach (var f in list.EnumerateArray())
                                files.Add($"{f.GetProperty("subfolder").GetString()}|{f.GetProperty("type").GetString()}|{f.GetProperty("filename").GetString()}");
            onProgress?.Invoke($"Done: {files.Count} file(s).");
            return files;
        }
    }

    // ponytail: one human line beats a 3KB message dump; full JSON stays in ComfyUI's console
    private static string ParseComfyError(JsonElement status)
    {
        try
        {
            if (status.TryGetProperty("messages", out var msgs))
                foreach (var m in msgs.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Array || m.GetArrayLength() < 2) continue;
                    if (m[0].GetString() != "execution_error") continue;
                    var d = m[1];
                    var node = d.TryGetProperty("node_id", out var nid) ? nid.GetString() : "?";
                    var type = d.TryGetProperty("node_type", out var nt) ? nt.GetString() : "?";
                    var msg = d.TryGetProperty("exception_message", out var em) ? (em.GetString() ?? "").Trim() : "";
                    return $"{type} (node {node}): {msg}";
                }
        }
        catch { }
        return status.ToString();
    }

    public async Task<string> DownloadOutputAsync(string filename, string subfolder, string type, CancellationToken ct = default)
    {
        var url = Url($"/view?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}");
        var data = await _httpClient.GetByteArrayAsync(url, ct);
        var dir = Path.Combine(FileSystem.CacheDirectory, "comfyui");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, filename);
        await File.WriteAllBytesAsync(path, data, ct);
        return path;
    }

    public async Task<string> UploadImageAsync(string localPath, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(localPath);
        form.Add(new StreamContent(fs), "image", Path.GetFileName(localPath));
        form.Add(new StringContent("true"), "overwrite");
        var res = await _httpClient.PostAsync(Url("/upload/image"), form, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"ComfyUI upload failed: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!;
    }

    // ponytail: cheap text checks that catch the failures that actually ruin renders (bad dialogue tags, invented media slots); the human reviews the rest
    public static string ValidateVideoPrompt(string prompt, int photos, int videos, int audios)
    {
        var warns = new List<string>();
        foreach (var s in new[] { "subject_definitions", "summary", "overall_soundscape", "non_diegetic_music" })
            if (!prompt.Contains(s, StringComparison.OrdinalIgnoreCase))
                warns.Add($"missing section: {s}");
        if (!prompt.Contains("detailed_description", StringComparison.OrdinalIgnoreCase)
            && !prompt.Contains("integrated_multimodal_description", StringComparison.OrdinalIgnoreCase))
            warns.Add("missing section: detailed_description (or integrated_multimodal_description)");
        var tags = Regex.Matches(prompt, @"<d>.*?</d>", RegexOptions.Singleline);
        if (tags.Any(m => !m.Value.Contains("[English]")))
            warns.Add("bare <d> tag without [English] — audio will be wrong, use <d>[English] words</d>");
        if (tags.Count == 0 && Regex.IsMatch(prompt, @"\b(says|said|replies|replied|asks|asked|shouts|whispers|tells|speaks)\b", RegexOptions.IgnoreCase))
            warns.Add("someone speaks but no <d>[English] tag — wrap the exact English words or the audio will babble");
        foreach (var (tag, max) in new[] { ("Picture", photos), ("Video", videos), ("Audio", audios) })
            foreach (Match m in Regex.Matches(prompt, $@"<{tag} (\d+)>"))
                if (int.Parse(m.Groups[1].Value) > max)
                { warns.Add($"references <{tag} {m.Groups[1].Value}> but only {max} {tag.ToLower()} loaded — renumber or load more"); break; }
        return string.Join("\n", warns);
    }

    private static string RandomUid()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        return new string(Enumerable.Range(0, 10).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
    }

    // ponytail: header-only PNG/JPEG size read, zero deps; anything else falls back to portrait and ComfyUI re-resolves at queue time
    private static (int w, int h) GetImageDimensions(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[32];
            if (fs.Read(buf, 0, 32) < 24) return (1088, 1928);
            if (buf[0] == 0x89 && buf[1] == 0x50) // PNG: width/height big-endian at 16/20
                return ((buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19],
                        (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23]);
            if (buf[0] == 0xFF && buf[1] == 0xD8) // JPEG: scan for SOF marker
            {
                fs.Position = 2;
                var seg = new byte[8];
                while (fs.Position < fs.Length)
                {
                    if (fs.Read(seg, 0, 4) < 4) break;
                    if (seg[0] != 0xFF) continue;
                    var marker = seg[1];
                    var len = (seg[2] << 8) | seg[3];
                    if (marker >= 0xC0 && marker <= 0xC3)
                    {
                        if (fs.Read(seg, 0, 5) < 5) break;
                        return ((seg[3] << 8) | seg[4], (seg[1] << 8) | seg[2]);
                    }
                    fs.Position += len - 2;
                }
            }
        }
        catch { }
        return (1088, 1928);
    }

    // ponytail: ffprobe probe when present, static fallback when not; bundle ffprobe/ffmpeg with the app later for exact metadata
    private static async Task<(int w, int h, double? dur)> ProbeMediaAsync(string path)
    {
        try
        {
            using var pr = Process.Start(new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v error -show_entries format=duration:stream=width,height -of json \"{path}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (pr == null) return (1088, 1928, null);
            var json = await pr.StandardOutput.ReadToEndAsync();
            await pr.WaitForExitAsync();
            using var doc = JsonDocument.Parse(json);
            int w = 0, h = 0;
            foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
            {
                if (s.TryGetProperty("width", out var ww)) w = ww.GetInt32();
                if (s.TryGetProperty("height", out var hh)) h = hh.GetInt32();
                if (w > 0) break;
            }
            double? dur = doc.RootElement.TryGetProperty("format", out var fmt) &&
                fmt.TryGetProperty("duration", out var dd) && double.TryParse(dd.GetRawText().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            return (w > 0 ? w : 1088, h > 0 ? h : 1928, dur);
        }
        catch { return (1088, 1928, null); }
    }

    public async Task<string> QueueWorkflowAsync(string workflowJson, int? seed = null)
    {
        if (workflowJson.Contains("\"nodes\"") && workflowJson.Contains("\"links\""))
            throw new InvalidOperationException("That JSON is the UI graph format. In ComfyUI: enable Dev Mode (gear icon) -> Save (API Format). Paste that file instead.");
        var request = new { prompt = JsonSerializer.Deserialize<JsonElement>(workflowJson), client_id = Guid.NewGuid().ToString("N") };
        var response = await _httpClient.PostAsync(Url("/prompt"), new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"ComfyUI {(int)response.StatusCode}: {body}");
        return body;
    }
}
