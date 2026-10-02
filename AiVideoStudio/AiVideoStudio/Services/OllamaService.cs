using System.Text;
using System.Text.Json;

namespace AiVideoStudio.Services;

public interface IOllamaService
{
    Task<List<string>> GetModelsAsync();
    Task<string> GenerateResponseAsync(string prompt, string? model = null);
    Task<string> BuildVideoPromptAsync(string story, int photos, int videos, int audios, string? model = null);
    Task<List<(string Beats, string H3)>> SplitScenesAsync(string story, int count, string? model = null);
}

public class OllamaService : IOllamaService
{
    private readonly HttpClient _httpClient;
    private static string Url(string path) => AppSettings.OllamaUrl + path;

    public OllamaService()
    {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(180);
    }

    public async Task<List<string>> GetModelsAsync()
    {
        var res = await _httpClient.GetAsync(Url("/api/tags"));
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();
        var names = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("models", out var models))
            foreach (var m in models.EnumerateArray())
                if (m.TryGetProperty("name", out var n))
                    names.Add(n.GetString() ?? "");
        return names.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    // ponytail: auto-pick first usable local model instead of hardcoding one that may not exist
    public async Task<string> GenerateResponseAsync(string prompt, string? model = null)
    {
        model ??= await PickModelAsync();
        var request = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = "Reply in the same language as the user's prompt. Never mix languages or scripts in one response." },
                new { role = "user", content = prompt }
            },
            stream = false
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(Url("/api/chat"), content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama {(int)response.StatusCode} with model '{model}': {body}");
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("message", out var msg) &&
            msg.TryGetProperty("content", out var txt))
            return txt.GetString() ?? "";
        return body;
    }

    // ponytail: one director pass turning free prose into the H3PromptIDE structure; spoken lines forced to English because H3 audio mangles anything else
    public async Task<string> BuildVideoPromptAsync(string story, int photos, int videos, int audios, string? model = null)
    {
        model ??= await PickModelAsync();
        var refs = new List<string>();
        for (var i = 1; i <= photos; i++) refs.Add($"<Picture {i}>");
        for (var i = 1; i <= videos; i++) refs.Add($"<Video {i}>");
        for (var i = 1; i <= audios; i++) refs.Add($"<Audio {i}>");
        var refLine = refs.Count == 0
            ? "No reference media. Pure text-to-video: describe subject appearance fully in words."
            : $"Reference media available: {string.Join(", ", refs)}. Reference each one by tag where it appears; never invent <Picture>/<Video>/<Audio> tags beyond these.";
        var request = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content =
                    "Convert the story into a MiniMax H3 video prompt. Output ONLY the prompt, plain section names, no markdown, no explanations. Follow this shape exactly:\n" +
                    "subject_definitions:\n<Subject 1> is a girl named Lin, 8, black hair in a braid.\n" +
                    "summary:\n[story] A girl learns to paint and shows her glowing tree painting at the village fair.\n" +
                    "detailed_description:\nThe video is watercolor style. [Shot 1] Morning over a seaside village. Lin (S1) walks with a sketchbook. Her grandfather says: <d>[English] Art is felt with the heart, not seen with the eyes.</d> [Shot 2] At night she dreams of painting a tree made of light.\n" +
                    "overall_soundscape:\nGentle waves, soft pencil scratches, the grandfather's warm voice. No music.\n" +
                    "non_diegetic_music:\nN/A\n" +
                    "Rules: narration and camera NEVER go inside <d> tags. Spoken words MUST be wrapped character-for-character like the example: <d>[English] exact words</d> — the angle brackets and [English] are mandatory machine syntax the video renderer parses, omitting them breaks the audio. The words inside are ALWAYS English even if the story is another language. If nobody speaks, use no <d> tags and write 'Silent. No dialogue.' in overall_soundscape." },
                new { role = "user", content = refLine + "\n\nStory:\n" + story }
            },
            stream = false
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(Url("/api/chat"), content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama {(int)response.StatusCode} with model '{model}': {body}");
        using var doc2 = JsonDocument.Parse(body);
        if (doc2.RootElement.TryGetProperty("message", out var msg2) &&
            msg2.TryGetProperty("content", out var txt2))
            return txt2.GetString() ?? "";
        return body;
    }

    // ponytail: one LLM call returns both beats and ready H3 blocks as JSON; strict shape + fence-stripping because small models decorate output
    public async Task<List<(string Beats, string H3)>> SplitScenesAsync(string story, int count, string? model = null)
    {
        model ??= await PickModelAsync();
        count = Math.Clamp(count, 1, 12);
        var request = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content =
                    $"Split the story into exactly {count} scenes for vertical 9:16 phone video (tight portrait framing, one subject, center-weighted). " +
                    "Output ONLY a JSON array, no fences, no commentary: [{\"beats\": \"2-3 sentence visual beats\", \"h3\": \"ONE string holding all five sections\"}]. " +
                    "Each h3 holds subject_definitions, summary, detailed_description ([Shot] beats, spoken words ONLY as <d>[English] exact words</d> in English; no <d> tags if silent), overall_soundscape, non_diegetic_music: N/A." },
                new { role = "user", content = story }
            },
            stream = false
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(Url("/api/chat"), content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama {(int)response.StatusCode} with model '{model}': {body}");
        var text = JsonDocument.Parse(body).RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        text = text.Trim();
        if (text.StartsWith("```")) text = text.Trim('`', 'j', 's', 'o', 'n', ' ', '\n', '\r');
        var out_ = new List<(string, string)>();
        foreach (var el in JsonDocument.Parse(text).RootElement.EnumerateArray())
            out_.Add(NormalizeScene(el));
        if (out_.Count == 0) throw new InvalidOperationException("Director returned no scenes. Retry or split manually with Add scene.");
        return out_;
    }

    // ponytail: small models return sections flat instead of nested under h3 — reassemble rather than re-prompt
    private static (string Beats, string H3) NormalizeScene(JsonElement el)
    {
        string Str(string k) => el.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
        var h3 = Str("h3");
        if (h3.Length < 100 || !h3.Contains("detailed_description"))
        {
            var joined = string.Join("\n", new[] { "subject_definitions", "summary", "detailed_description", "overall_soundscape", "non_diegetic_music" }
                .Where(k => !string.IsNullOrWhiteSpace(Str(k)))
                .Select(k => k + ":\n" + Str(k)));
            if (!string.IsNullOrWhiteSpace(joined)) h3 = joined;
        }
        var beats = Str("beats");
        if (string.IsNullOrWhiteSpace(beats)) beats = Str("summary");
        if (string.IsNullOrWhiteSpace(beats) && Str("detailed_description").Length > 0)
            beats = Str("detailed_description")[..Math.Min(200, Str("detailed_description").Length)];
        return (beats, h3);
    }

    private async Task<string> PickModelAsync()
    {
        var preferred = new[] { "llama3.1", "qwen2.5", "qwen3-coder", "mistral", "qwen" };
        try
        {
            var available = await GetModelsAsync();
            if (available.Count == 0) throw new InvalidOperationException("Ollama has no models. Run: ollama pull llama3.1");
            if (!string.IsNullOrWhiteSpace(AppSettings.Model))
            {
                var saved = available.FirstOrDefault(a => a.Equals(AppSettings.Model, StringComparison.OrdinalIgnoreCase));
                if (saved != null) return saved;
            }
            foreach (var p in preferred)
            {
                var hit = available.FirstOrDefault(a => a.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            return available[0];
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Cannot reach Ollama at {AppSettings.OllamaUrl}. Is Ollama running? {ex.Message}");
        }
    }
}
