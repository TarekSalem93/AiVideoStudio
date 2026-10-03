using AiVideoStudio.Services;

namespace AiVideoStudio.Views;

public partial class MainPage : ContentPage
{
    private readonly IOllamaService _ollama;
    private readonly IComfyUiService _comfy;
    private readonly IVideoMergeService _merge;
    private readonly IVoiceoverService _voice;
    private readonly GenerationStore _store;
    private readonly List<IPublisher> _publishers;
    private IDispatcherTimer? _healthTimer;
    private string _lastStoryProse = "";
    private List<string> _lastOutputs = new();
    private string _lastVideo = "";

    // ponytail: resolve via service locator so Shell DataTemplate needs no DI wiring
    public MainPage()
    {
        InitializeComponent();
        var sp = Application.Current?.Handler?.MauiContext?.Services;
        T Get<T>(Func<T> fallback) where T : class => sp?.GetService<T>() ?? fallback();
        _ollama = Get<IOllamaService>(() => new OllamaService());
        _comfy = Get<IComfyUiService>(() => new ComfyUiService());
        _merge = Get<IVideoMergeService>(() => new VideoMergeService());
        _voice = Get<IVoiceoverService>(() => new VoiceoverService());
        _store = Get<GenerationStore>(() => new GenerationStore());
        _publishers = new List<IPublisher>
        {
            Get<YouTubePublisher>(() => new YouTubePublisher()),
            Get<TikTokPublisher>(() => new TikTokPublisher()),
            Get<FacebookPublisher>(() => new FacebookPublisher())
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            var models = await _ollama.GetModelsAsync();
            ModelPicker.ItemsSource = models;
            var savedModel = string.IsNullOrWhiteSpace(AppSettings.Model) ? null : AppSettings.Model;
            ModelPicker.SelectedIndex = savedModel != null ? models.IndexOf(savedModel) : 0;
            if (ModelPicker.SelectedIndex < 0 && models.Count > 0) ModelPicker.SelectedIndex = 0;

            var templates = _comfy.ListTemplates();
            TemplatePicker.ItemsSource = templates;
            TemplatePicker.SelectedIndex = Math.Max(0, templates.IndexOf(AppSettings.Template));
            StepsEntry.Text = AppSettings.Steps.ToString();
            await _store.LoadAsync();
            await RefreshHealthAsync();
        }
        catch (Exception ex) { HealthLabel.Text = $"Servers unreachable: {ex.Message}"; }
        _healthTimer?.Stop();
        _healthTimer = Dispatcher.CreateTimer();
        _healthTimer.Interval = TimeSpan.FromSeconds(30);
        _healthTimer.Tick += async (_, _) => await RefreshHealthAsync();
        _healthTimer.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _healthTimer?.Stop();
    }

    private async Task RefreshHealthAsync()
    {
        try
        {
            var models = await _ollama.GetModelsAsync();
            var comfyUp = await _comfy.IsRunningAsync();
            var ff = await Task.Run(() =>
            {
                try { using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = AppSettings.FfmpegPath, Arguments = "-version", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true }); return p != null; }
                catch { return false; }
            });
            HealthLabel.Text = $"Ollama: OK ({models.Count}) | ComfyUI: {(comfyUp ? "OK" : "DOWN")} | ffmpeg: {(ff ? "OK" : "MISSING - set path in Settings")}";
        }
        catch { HealthLabel.Text = "Servers unreachable."; }
    }

    private async void OnGenerateClicked(object? sender, EventArgs e)
    {
        var prompt = PromptEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prompt)) { StatusLabel.Text = "Type a prompt first."; return; }
        try
        {
            StatusLabel.Text = "Generating...";
            var model = ModelPicker.SelectedItem as string;
            if (!string.IsNullOrWhiteSpace(model)) Preferences.Set("model", model);
            ResponseEditor.Text = await _ollama.GenerateResponseAsync(prompt, model);
            _lastStoryProse = ResponseEditor.Text;
            StatusLabel.Text = "Story ready — edit it above, then Build H3 Prompt.";
        }
        catch (Exception ex) { StatusLabel.Text = $"Error: {ex.Message}"; }
    }

    private List<string> _photoPaths = new();
    private List<string> _videoPaths = new();
    private List<string> _audioPaths = new();

    private async Task PickMedia(FilePickerFileType type, int max, List<string> store, Label label, string kind)
    {
        try
        {
            var files = await FilePicker.PickMultipleAsync(new PickOptions { FileTypes = type });
            if (files == null) return;
            store.Clear();
            store.AddRange(files.Select(f => f.FullPath).OfType<string>().Take(max));
            label.Text = store.Count == 0 ? $"No {kind} selected." : $"{store.Count} {kind} selected: " + string.Join(", ", store.Select(Path.GetFileName));
        }
        catch (Exception ex) { StatusLabel.Text = $"Pick failed: {ex.Message}"; }
    }

    private async void OnPickPhotosClicked(object? sender, EventArgs e)
        => await PickMedia(FilePickerFileType.Images, 9, _photoPaths, PhotosLabel, "photos");

    private async void OnPickVideosClicked(object? sender, EventArgs e)
        => await PickMedia(FilePickerFileType.Videos, 3, _videoPaths, VideosLabel, "videos");

    private async void OnPickAudiosClicked(object? sender, EventArgs e)
        => await PickMedia(new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            { DevicePlatform.WinUI, new[] { ".mp3", ".wav", ".ogg", ".flac", ".m4a" } },
            { DevicePlatform.MacCatalyst, new[] { "public.audio" } }
        }), 3, _audioPaths, AudiosLabel, "audios");

    private async void OnBuildPromptClicked(object? sender, EventArgs e)
    {
        var story = ResponseEditor.Text?.Trim();
        if (string.IsNullOrWhiteSpace(story)) { StatusLabel.Text = "Generate (or paste) a story first."; return; }
        if (!story.Contains("subject_definitions", StringComparison.OrdinalIgnoreCase)) _lastStoryProse = story;
        try
        {
            StatusLabel.Text = "Building H3 video prompt...";
            ResponseEditor.Text = await _ollama.BuildVideoPromptAsync(story, _photoPaths.Count, _videoPaths.Count, _audioPaths.Count, ModelPicker.SelectedItem as string);
            var warn = ComfyUiService.ValidateVideoPrompt(ResponseEditor.Text ?? "", _photoPaths.Count, _videoPaths.Count, _audioPaths.Count);
            StatusLabel.Text = "Video prompt ready — check it, edit, then Generate Video."
                + (string.IsNullOrEmpty(warn) ? "" : "\n⚠ " + warn.Replace("\n", "\n⚠ "));
        }
        catch (Exception ex) { StatusLabel.Text = $"Error: {ex.Message}"; }
    }

    private async void OnVideoClicked(object? sender, EventArgs e)
    {
        var story = ResponseEditor.Text?.Trim();
        if (string.IsNullOrWhiteSpace(story)) { StatusLabel.Text = "Generate (or paste) a story first, then queue video."; return; }
        if (!int.TryParse(StepsEntry.Text, out var steps) || steps < 1) steps = AppSettings.Steps;
        var template = TemplatePicker.SelectedItem as string ?? AppSettings.Template;
        Preferences.Set("template", template);
        Preferences.Set("steps", steps);
        try
        {
            var preWarn = ComfyUiService.ValidateVideoPrompt(story, _photoPaths.Count, _videoPaths.Count, _audioPaths.Count);
            StatusLabel.Text = "Queueing video..." + (string.IsNullOrEmpty(preWarn) ? "" : "\n⚠ " + preWarn.Replace("\n", "\n⚠ "));
            var seed = Random.Shared.Next();
            var files = await _comfy.QueueStoryAsync(story, template, seed, steps, _photoPaths, _videoPaths, _audioPaths,
                s => MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = s));
            var saved = new List<string>();
            foreach (var f in files)
            {
                var parts = f.Split('|');
                saved.Add(await _comfy.DownloadOutputAsync(parts[2], parts[0], parts[1]));
            }
            _lastOutputs = saved;
            _lastVideo = saved.FirstOrDefault(p => p.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) ?? saved.FirstOrDefault() ?? "";
            await _store.AddAsync(new Generation(Guid.NewGuid().ToString("N"), DateTime.Now, template, seed, steps,
                story.Length > 200 ? story[..200] : story, story, saved,
                _photoPaths.Concat(_videoPaths).Concat(_audioPaths).ToList()));
            StatusLabel.Text = saved.Count == 0 ? "Done but no output files found. Check ComfyUI output folder." : "Saved:\n" + string.Join("\n", saved);
        }
        catch (Exception ex) { StatusLabel.Text = $"Error: {ex.Message}"; }
    }

    private async void OnOpenClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastVideo) || !File.Exists(_lastVideo)) { StatusLabel.Text = "No video yet — generate one first."; return; }
        // ponytail: OS player instead of MediaElement dep; in-app preview graduates in only if this ever feels lacking
        await Launcher.Default.OpenAsync(new OpenFileRequest { File = new ReadOnlyFile(_lastVideo) });
    }

    private async void OnDubClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastVideo) || !File.Exists(_lastVideo)) { StatusLabel.Text = "No video yet — generate one first."; return; }
        var prose = _lastStoryProse;
        if (string.IsNullOrWhiteSpace(prose) && !ResponseEditor.Text.Contains("subject_definitions", StringComparison.OrdinalIgnoreCase))
            prose = ResponseEditor.Text;
        if (string.IsNullOrWhiteSpace(prose)) { StatusLabel.Text = "No narration text — generate a story first."; return; }
        try
        {
            if (!await _voice.IsAvailableAsync())
            { StatusLabel.Text = "edge-tts not found. Install it: pip install edge-tts (needs Python), then retry."; return; }
            StatusLabel.Text = "Synthesizing Arabic voiceover...";
            var dir = Path.Combine(FileSystem.CacheDirectory, "dub");
            Directory.CreateDirectory(dir);
            var audio = Path.Combine(dir, $"voice_{DateTime.Now:yyyyMMdd_HHmmss}.mp3");
            await _voice.SpeakAsync(prose, AppSettings.Voice, audio);
            StatusLabel.Text = "Muxing voiceover...";
            var outPath = Path.Combine(dir, $"dubbed_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            await _voice.MuxAsync(_lastVideo, audio, outPath);
            _lastVideo = outPath;
            StatusLabel.Text = "Dubbed:\n" + outPath;
        }
        catch (Exception ex) { StatusLabel.Text = $"Error: {ex.Message}"; }
    }

    private async void OnPublishClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastVideo) || !File.Exists(_lastVideo)) { StatusLabel.Text = "No video yet — generate one first."; return; }
        var picked = new List<IPublisher>();
        if (YouTubeCheck.IsChecked) picked.Add(_publishers.OfType<YouTubePublisher>().First());
        if (TikTokCheck.IsChecked) picked.Add(_publishers.OfType<TikTokPublisher>().First());
        if (FacebookCheck.IsChecked) picked.Add(_publishers.OfType<FacebookPublisher>().First());
        if (picked.Count == 0) { StatusLabel.Text = "Tick at least one platform."; return; }
        var title = string.IsNullOrWhiteSpace(TitleEntry.Text) ? "AiVideoStudio" : TitleEntry.Text.Trim();
        var state = picked.ToDictionary(p => p.Name, _ => "starting...");
        void Render() => MainThread.BeginInvokeOnMainThread(() =>
            StatusLabel.Text = string.Join("\n", state.Select(kv => $"{kv.Key}: {kv.Value}")));
        Render();
        // ponytail: fan-out with Task.WhenAll per roadmap; one status line per platform instead of per-bar widgets
        await Task.WhenAll(picked.Select(async p =>
        {
            var prog = new Progress<double>(v => { state[p.Name] = $"{v:P0}"; Render(); });
            try
            {
                if (!await p.IsConfiguredAsync()) state[p.Name] = "not configured — see Settings → " + p.Name;
                else state[p.Name] = await p.PublishAsync(_lastVideo, title, _lastStoryProse, prog);
            }
            catch (Exception ex) { state[p.Name] = "failed: " + ex.Message.Split('\n')[0]; }
            Render();
        }));
    }

    private async void OnComfyClicked(object? sender, EventArgs e)
    {
        try { StatusLabel.Text = "Ensuring ComfyUI is running..."; await _comfy.EnsureRunningAsync(); StatusLabel.Text = "ComfyUI is up."; await RefreshHealthAsync(); }
        catch (Exception ex) { StatusLabel.Text = $"Error: {ex.Message}"; }
    }
}
