using AiVideoStudio.Services;

namespace AiVideoStudio.Views;

[QueryProperty(nameof(ProjectId), "ProjectId")]
public partial class ProjectDetailPage : ContentPage
{
    private readonly ProjectStore _store;
    private readonly IOllamaService _ollama;
    private readonly IComfyUiService _comfy;
    private readonly IVideoMergeService _merge;
    private CancellationTokenSource? _renderCts;
    private Project? _project;

    public string ProjectId { get; set; } = "";

    public ProjectDetailPage()
    {
        InitializeComponent();
        var sp = Application.Current?.Handler?.MauiContext?.Services;
        _store = sp?.GetService<ProjectStore>() ?? new ProjectStore();
        _ollama = sp?.GetService<IOllamaService>() ?? new OllamaService();
        _comfy = sp?.GetService<IComfyUiService>() ?? new ComfyUiService();
        _merge = sp?.GetService<IVideoMergeService>() ?? new VideoMergeService();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _store.LoadAsync();
        _project = _store.Items.FirstOrDefault(p => p.Id == ProjectId);
        if (_project == null) { await Shell.Current.GoToAsync(".."); return; }
        TitleLabel.Text = _project.Title;
        Refresh();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        if (_project != null) await _store.UpdateAsync(_project);
    }

    private void Refresh()
    {
        if (_project == null) return;
        var done = _project.Scenes.Count(s => s.Status == "done");
        InfoLabel.Text = $"{_project.Scenes.Count} scenes, {done} done | {_project.Template} | {_project.Steps} steps";
        var pics = _project.Media.Count(m => m.StartsWith("picture|"));
        var vids = _project.Media.Count(m => m.StartsWith("video|"));
        var auds = _project.Media.Count(m => m.StartsWith("audio|"));
        MediaLabel.Text = _project.Media.Count == 0 ? "No project media." : $"{pics} photos, {vids} videos, {auds} audios";
        ScenesView.ItemsSource = null;
        ScenesView.ItemsSource = _project.Scenes;
    }

    private async void OnSplitClicked(object? sender, EventArgs e)
    {
        if (_project == null) return;
        var story = SplitEditor.Text?.Trim();
        if (string.IsNullOrWhiteSpace(story)) { StatusLabel.Text = "Paste a story first."; return; }
        if (!int.TryParse(CountEntry.Text, out var n) || n < 1) n = 3;
        if (_project.Scenes.Count > 0 && !await DisplayAlert("Split", $"Replace {_project.Scenes.Count} existing scenes?", "Yes", "No")) return;
        try
        {
            StatusLabel.Text = "Splitting story...";
            var scenes = await _ollama.SplitScenesAsync(story, n);
            _project = _project with
            {
                Scenes = scenes.Select((s, i) => new Scene(Guid.NewGuid().ToString("N"), i + 1, s.Beats, s.H3, Random.Shared.NextInt64(), "draft", new List<string>(), "")).ToList()
            };
            await _store.UpdateAsync(_project);
            Refresh();
            StatusLabel.Text = $"{scenes.Count} scenes ready — review each H3 prompt, then render (next step).";
        }
        catch (Exception ex) { StatusLabel.Text = $"Error: {ex.Message}"; }
    }

    private async void OnAddSceneClicked(object? sender, EventArgs e)
    {
        if (_project == null) return;
        var scenes = _project.Scenes.ToList();
        scenes.Add(new Scene(Guid.NewGuid().ToString("N"), scenes.Count + 1, "", "", Random.Shared.NextInt64(), "draft", new List<string>(), ""));
        _project = _project with { Scenes = scenes };
        await _store.UpdateAsync(_project);
        Refresh();
    }

    private async void OnRemoveSceneClicked(object? sender, EventArgs e)
    {
        if (_project == null || sender is not Button b || b.BindingContext is not Scene s) return;
        var scenes = _project.Scenes.Where(x => x.Id != s.Id).ToList();
        for (var i = 0; i < scenes.Count; i++) scenes[i] = scenes[i] with { Index = i + 1 };
        _project = _project with { Scenes = scenes };
        await _store.UpdateAsync(_project);
        Refresh();
    }

    // ponytail: records are immutable so editors write through here; persisted on leave, not per keystroke
    private void OnSceneEdited(object? sender, TextChangedEventArgs e)
    {
        if (_project == null || sender is not Editor ed || ed.BindingContext is not Scene s) return;
        var i = _project.Scenes.FindIndex(x => x.Id == s.Id);
        if (i < 0) return;
        _project.Scenes[i] = ed.ClassId == "H3"
            ? s with { H3Prompt = e.NewTextValue ?? "" }
            : s with { Beats = e.NewTextValue ?? "" };
    }

    private async Task PickProjectMedia(FilePickerFileType type, string kind, int max)
    {
        if (_project == null) return;
        try
        {
            var files = await FilePicker.PickMultipleAsync(new PickOptions { FileTypes = type });
            if (files == null) return;
            var media = _project.Media.Where(m => !m.StartsWith(kind + "|")).ToList();
            media.AddRange(files.Select(f => f.FullPath).OfType<string>().Where(File.Exists).Take(max).Select(p => $"{kind}|{p}"));
            _project = _project with { Media = media };
            await _store.UpdateAsync(_project);
            Refresh();
        }
        catch (Exception ex) { StatusLabel.Text = $"Pick failed: {ex.Message}"; }
    }

    private async void OnMediaPhotosClicked(object? sender, EventArgs e)
        => await PickProjectMedia(FilePickerFileType.Images, "picture", 9);

    private async void OnMediaVideosClicked(object? sender, EventArgs e)
        => await PickProjectMedia(FilePickerFileType.Videos, "video", 3);

    private async void OnMediaAudiosClicked(object? sender, EventArgs e)
        => await PickProjectMedia(new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            { DevicePlatform.WinUI, new[] { ".mp3", ".wav", ".ogg", ".flac", ".m4a" } },
            { DevicePlatform.MacCatalyst, new[] { "public.audio" } }
        }), "audio", 3);

    private (List<string> Photos, List<string> Videos, List<string> Audios) PartitionMedia()
    {
        List<string> Kind(string k) => _project?.Media.Where(m => m.StartsWith(k + "|")).Select(m => m[(k.Length + 1)..]).Where(File.Exists).ToList() ?? new();
        return (Kind("picture"), Kind("video"), Kind("audio"));
    }

    private void SetScene(Scene updated)
    {
        if (_project == null) return;
        var i = _project.Scenes.FindIndex(x => x.Id == updated.Id);
        if (i >= 0) _project.Scenes[i] = updated;
    }

    private async Task<bool> RenderOneAsync(string sceneId, string? prevClip, CancellationToken ct)
    {
        if (_project == null) return false;
        var scene = _project.Scenes.FirstOrDefault(x => x.Id == sceneId);
        if (scene == null || string.IsNullOrWhiteSpace(scene.H3Prompt))
        {
            if (scene != null) { SetScene(scene with { Status = "failed: empty prompt" }); await _store.UpdateAsync(_project); Refresh(); }
            return false;
        }
        SetScene(scene with { Status = "rendering" });
        await _store.UpdateAsync(_project);
        Refresh();
        try
        {
            var (photos, videos, audios) = PartitionMedia();
            var files = await _comfy.QueueSceneAsync(scene.H3Prompt, _project.Template, scene.Seed, _project.Steps,
                _project.Aspect, _project.Mp, photos, videos, audios, prevClip,
                s => MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = $"Scene {scene.Index}: {s}"), ct);
            var local = new List<string>();
            foreach (var f in files)
            {
                var parts = f.Split('|');
                local.Add(await _comfy.DownloadOutputAsync(parts[2], parts[0], parts[1], ct));
            }
            var chosen = local.FirstOrDefault(p => p.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) ?? local.FirstOrDefault() ?? "";
            SetScene(scene with { Status = local.Count == 0 ? "failed: no outputs" : "done", ClipPaths = local, ChosenClip = chosen });
            await _store.UpdateAsync(_project);
            Refresh();
            return local.Count > 0;
        }
        catch (OperationCanceledException)
        {
            SetScene(scene with { Status = "cancelled" });
            await _store.UpdateAsync(_project);
            Refresh();
            return false;
        }
        catch (Exception ex)
        {
            SetScene(scene with { Status = $"failed: {ex.Message.Split('\n')[0]}" });
            await _store.UpdateAsync(_project);
            Refresh();
            return false;
        }
    }

    private async void OnPickClipClicked(object? sender, EventArgs e)
    {
        if (_project == null || sender is not Button b || b.BindingContext is not Scene s) return;
        if (s.ClipPaths.Count == 0) { StatusLabel.Text = "No clips yet — render the scene first."; return; }
        var names = s.ClipPaths.Select(Path.GetFileName).ToArray();
        var pick = await DisplayActionSheet("Use which clip?", "Cancel", null, names!);
        if (pick == null || pick == "Cancel") return;
        var chosen = s.ClipPaths.First(p => Path.GetFileName(p) == pick);
        SetScene(s with { ChosenClip = chosen, Status = "done" });
        await _store.UpdateAsync(_project);
        Refresh();
        StatusLabel.Text = "Scene will assemble with: " + pick;
    }

    private async void OnRenderSceneClicked(object? sender, EventArgs e)
    {
        if (_project == null || sender is not Button b || b.BindingContext is not Scene s) return;
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var prev = _project.Scenes.Where(x => x.Index < s.Index && x.Status == "done" && File.Exists(x.ChosenClip))
            .OrderByDescending(x => x.Index).FirstOrDefault()?.ChosenClip;
        await RenderOneAsync(s.Id, prev, _renderCts.Token);
        StatusLabel.Text = "Scene render finished.";
    }

    private async void OnRenderAllClicked(object? sender, EventArgs e)
    {
        if (_project == null || _project.Scenes.Count == 0) { StatusLabel.Text = "No scenes — split a story first."; return; }
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        string? prev = null;
        foreach (var s in _project.Scenes.OrderBy(x => x.Index).ToList())
        {
            if (_renderCts.Token.IsCancellationRequested) break;
            StatusLabel.Text = $"Scene {s.Index}/{_project.Scenes.Count}...";
            var ok = await RenderOneAsync(s.Id, prev, _renderCts.Token);
            if (_renderCts.Token.IsCancellationRequested) { StatusLabel.Text = "Cancelled."; break; }
            if (!ok) { StatusLabel.Text = $"Stopped: scene {s.Index} failed."; break; }
            prev = _project.Scenes.First(x => x.Id == s.Id).ChosenClip;
        }
        if (!_renderCts.Token.IsCancellationRequested) StatusLabel.Text = "All scenes done — Assemble film when ready.";
    }

    private void OnCancelClicked(object? sender, EventArgs e)
    {
        _renderCts?.Cancel();
        StatusLabel.Text = "Cancelling...";
    }

    private async void OnAssembleClicked(object? sender, EventArgs e)
    {
        if (_project == null) return;
        var clips = _project.Scenes.OrderBy(x => x.Index)
            .Select(x => x.ChosenClip).Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();
        if (clips.Count == 0) { StatusLabel.Text = "No rendered clips to assemble."; return; }
        try
        {
            StatusLabel.Text = $"Stitching {clips.Count} clips...";
            var dir = Path.Combine(FileSystem.CacheDirectory, "films");
            Directory.CreateDirectory(dir);
            var outPath = Path.Combine(dir, $"{_project.Title.Replace(" ", "_")}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            foreach (var c in "Invalid:<>\"/\\|?*".ToCharArray()) outPath = outPath.Replace(c.ToString(), "");
            await _merge.MergeVideosAsync(clips, outPath);
            _project = _project with { FinalPath = outPath };
            await _store.UpdateAsync(_project);
            StatusLabel.Text = "Film ready:\n" + outPath;
        }
        catch (Exception ex) { StatusLabel.Text = $"Assemble failed: {ex.Message}"; }
    }
}
