namespace AiVideoStudio.Services;

// ponytail: MAUI Preferences instead of a settings DB table; graduates to SQLite only if relational queries ever appear
public static class AppSettings
{
    public static string OllamaUrl => Preferences.Get("ollama_url", "http://localhost:11434").Trim().TrimEnd('/');
    public static string ComfyUrl => Preferences.Get("comfy_url", "http://127.0.0.1:8188").Trim().TrimEnd('/');
    public static int Steps => Preferences.Get("steps", 6);
    public static string Template => Preferences.Get("template", "minimax_h3_seed_hunter_v21_portrait_api.json");
    public static string Model => Preferences.Get("model", "");
    public static string FfmpegPath => Preferences.Get("ffmpeg_path", "ffmpeg");
    public static string Voice => Preferences.Get("voice", "ar-SA-ZariyahNeural");

    public static void Save(string ollamaUrl, string comfyUrl, int steps, string template, string model, string ffmpegPath, string voice)
    {
        Preferences.Set("ollama_url", ollamaUrl.Trim().TrimEnd('/'));
        Preferences.Set("comfy_url", comfyUrl.Trim().TrimEnd('/'));
        Preferences.Set("steps", steps);
        Preferences.Set("template", template);
        Preferences.Set("model", model);
        Preferences.Set("ffmpeg_path", ffmpegPath);
        Preferences.Set("voice", voice);
    }
}
