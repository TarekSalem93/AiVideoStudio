using AiVideoStudio.Services;

namespace AiVideoStudio.Views;

[QueryProperty(nameof(ProjectId), "ProjectId")]
public partial class ProjectDetailPage : ContentPage
{
    private readonly ProjectStore _store;
    private readonly GenerationStore _history;
    private readonly IOllamaService _ollama;
    private readonly IComfyUiService _comfy;
    private readonly IVideoMergeService _merge;
    private CancellationTokenSource? _renderCts;
    private readonly List<string> _audioWarnings = new();
    // template nodes 297 (Overlap Frames) @ 169 (Default FPS 24)
    private const double ContinuationOverlapSec = 22.0 / 24.0;
    private Project? _project;

    public string ProjectId { get; set; } = "";

    public ProjectDetailPage()
    {
        InitializeComponent();
        var sp = Application.Current?.Handler?.MauiContext?.Services;
        _store = sp?.GetService<ProjectStore>() ?? new ProjectStore();
        _history = sp?.GetService<GenerationStore>() ?? new GenerationStore();
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

    private async Task<bool> RenderOneAsync(string sceneId, CancellationToken ct)
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
            // ponytail: warn don't block; malformed <d> tags are the usual reason gen segments come back silent
            var preWarn = ComfyUiService.ValidateVideoPrompt(scene.H3Prompt, photos.Count, videos.Count, audios.Count);
            if (!string.IsNullOrWhiteSpace(preWarn))
                MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = $"Scene {scene.Index} prompt warnings (rendering anyway):\n{preWarn}");
            var files = await _comfy.QueueSceneAsync(scene.H3Prompt, _project.Template, scene.Seed, _project.Steps,
                _project.Aspect, _project.Mp, photos, videos, audios,
                s => MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = $"Scene {scene.Index}: {s}"), ct);
            var local = new List<string>();
            foreach (var f in files)
            {
                var parts = f.Split('|');
                local.Add(await _comfy.DownloadOutputAsync(parts[2], parts[0], parts[1], ct));
            }
            var chosen = local.FirstOrDefault(p => p.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) ?? local.FirstOrDefault() ?? "";
            // ponytail: never rewrite the downloaded clip. The old audio-shift here applied the same offset AssembleFilmAsync applies again, and its -shortest truncated the video (measured 16s -> 14.04s) before File.Move overwrote the original.
            // ponytail: gen-only scenes must carry sound through their own length; a full-length track that goes silent early is a failed render, not a merge problem
            if (!string.IsNullOrEmpty(chosen) && File.Exists(chosen))
            {
                var (vd, ad) = await _merge.GetStreamDurationsAsync(chosen, ct);
                // ponytail: measure where sound stops rather than where the track ends - ComfyUI pads a mute segment to full length, which is exactly the case a length check misses
                var audible = await _merge.GetAudibleEndAsync(chosen, ct);
                if (vd.HasValue && audible.HasValue && vd.Value > 1.5 && audible.Value < vd.Value - 1.0)
                {
                    var w = $"Scene {scene.Index}: sound stops at {audible:0.0}s of a {vd:0.0}s clip — this segment came back mute (track is {ad:0.0}s). "
                        + $"Give the scene a real <d>[English] line</d> in its own shots, or drop any 'sudden silence' wording from overall_soundscape.";
                    _audioWarnings.Add(w);
                    MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = w);
                }
            }
            SetScene(scene with { Status = local.Count == 0 ? "failed: no outputs" : "done", ClipPaths = local, ChosenClip = chosen });
            await _store.UpdateAsync(_project);
            if (local.Count > 0 && _project != null)
            {
                // ponytail: History only knew single generations; project scenes are generations too
                await _history.LoadAsync();
                await _history.AddAsync(new Generation(Guid.NewGuid().ToString("N"), DateTime.Now, _project.Template,
                    unchecked((int)scene.Seed), _project.Steps,
                    $"Scene {scene.Index}: {(scene.Beats.Length > 200 ? scene.Beats[..200] : scene.Beats)}",
                    scene.H3Prompt, local, photos.Concat(videos).Concat(audios).ToList()));
            }
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

    // ponytail: hand-written one-liners skip the builder's section/tag rules and come back silent; redraft through it instead of hand-fixing tags
    private async void OnFixH3Clicked(object? sender, EventArgs e)
    {
        if (_project == null || _project.Scenes.Count == 0) { StatusLabel.Text = "No scenes — split a story first."; return; }
        if (!await DisplayAlert("AI-fix H3", $"Rewrite {_project.Scenes.Count} H3 prompts with AI? Current H3 text will be replaced.", "Yes", "No")) return;
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        try
        {
            var (photos, videos, audios) = PartitionMedia();
            var n = 0;
            foreach (var s in _project.Scenes.OrderBy(x => x.Index).ToList())
            {
                if (_renderCts.Token.IsCancellationRequested) { StatusLabel.Text = "Cancelled."; return; }
                if (string.IsNullOrWhiteSpace(s.Beats)) continue;
                StatusLabel.Text = $"Drafting H3 for scene {s.Index}/{_project.Scenes.Count}...";
                var h3 = await _ollama.BuildVideoPromptAsync(
                    $"Scene {s.Index} of {_project.Scenes.Count}. Beats: {s.Beats}. Write the spoken lines for this scene as <d>[English] exact words</d> unless the beats explicitly ask for silence.",
                    photos.Count, videos.Count, audios.Count);
                SetScene(s with { H3Prompt = h3.Trim() });
                n++;
            }
            await _store.UpdateAsync(_project);
            Refresh();
            if (_renderCts.Token.IsCancellationRequested) { StatusLabel.Text = "Cancelled."; return; }
            var stillBad = _project.Scenes.Count(s => !string.IsNullOrWhiteSpace(
                ComfyUiService.ValidateVideoPrompt(s.H3Prompt, photos.Count, videos.Count, audios.Count)));
            StatusLabel.Text = $"{n} H3 prompts rewritten — review each, then render."
                + (stillBad > 0 ? $" {stillBad} still show warnings." : " No warnings.");
        }
        catch (Exception ex) { StatusLabel.Text = $"Error: {ex.Message}"; }
    }

    private async void OnRenderSceneClicked(object? sender, EventArgs e)
    {
        if (_project == null || sender is not Button b || b.BindingContext is not Scene s) return;
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        await RenderOneAsync(s.Id, _renderCts.Token);
        StatusLabel.Text = "Scene render finished.";
    }

    private async void OnRenderAllClicked(object? sender, EventArgs e)
    {
        if (_project == null || _project.Scenes.Count == 0) { StatusLabel.Text = "No scenes — split a story first."; return; }
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        _audioWarnings.Clear();
        foreach (var s in _project.Scenes.OrderBy(x => x.Index).ToList())
        {
            if (_renderCts.Token.IsCancellationRequested) break;
            StatusLabel.Text = $"Scene {s.Index}/{_project.Scenes.Count}...";
            var ok = await RenderOneAsync(s.Id, _renderCts.Token);
            if (_renderCts.Token.IsCancellationRequested) { StatusLabel.Text = "Cancelled."; break; }
            if (!ok) { StatusLabel.Text = $"Stopped: scene {s.Index} failed."; break; }
        }
        if (!_renderCts.Token.IsCancellationRequested)
            StatusLabel.Text = "All scenes done — Assemble film when ready."
                + (_audioWarnings.Count > 0 ? "\n" + string.Join("\n", _audioWarnings) : "");
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
        _renderCts?.Cancel();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;
        try
        {
            StatusLabel.Text = $"Stitching {clips.Count} clips...";
            var dir = Path.Combine(FileSystem.CacheDirectory, "films");
            Directory.CreateDirectory(dir);
            // ponytail: sanitize the file name only; the old code stripped /\: from the whole path and the film landed nowhere findable
            var safe = string.Concat(_project.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Replace(" ", "_");
            if (string.IsNullOrWhiteSpace(safe)) safe = "film";
            var outPath = Path.Combine(dir, $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            // ponytail: continuation scenes already contain all previous scenes (minus the 22-frame overlap); concat only each new tail or the film repeats itself
            const double overlapSec = ContinuationOverlapSec;
            var segments = new List<string>();
            var segDurs = new List<double>();
            var audioDelay = new List<double>();
            var audioOf = new List<double?>();
            var report = new List<string>();
            var rebuilt = false;
            var temps = new List<string>();
            try
            {
                double? prevDur = null;
                double offset = 0;
                foreach (var c in clips)
                {
                    ct.ThrowIfCancellationRequested();
                    StatusLabel.Text = $"Checking audio in clip {segments.Count + 1}/{clips.Count}...";
                    var (vd, _) = await _merge.GetStreamDurationsAsync(c, ct);
                    var d = vd ?? await _merge.GetDurationAsync(c, ct);
                    // ponytail: playable length, not metadata; containers can claim 16s holding 8s of samples
                    var ad = await _merge.GetAudioPlayableDurationAsync(c, ct);
                    // ponytail: and where sound actually stops - a mute segment still measures full length, so this is the only number that reveals it
                    var audible = await _merge.GetAudibleEndAsync(c, ct);
                    var mute = audible.HasValue && ad.HasValue && audible.Value < ad.Value - 0.75;
                    report.Add(mute
                        ? $"clip{segments.Count + 1}: video {d?.ToString("0.0") ?? "?"}s, audio track {ad:0.0}s but SILENT after {audible:0.0}s"
                        : $"clip{segments.Count + 1}: video {d?.ToString("0.0") ?? "?"}s, audio {(ad?.ToString("0.0") ?? "none")}s");
                    if (mute) _audioWarnings.Add($"clip{segments.Count + 1} is silent after {audible:0.0}s - no voice for that stretch.");
                    if (!d.HasValue) report.Add($"clip{segments.Count + 1}: unknown duration — later audio offsets may be off");
                    // ponytail: sanity-guard the cut point; a bogus duration must fall back to plain concat, never to -ss <garbage>
                    var start = prevDur.HasValue && d.HasValue && d.Value > prevDur.Value + 0.5
                        ? prevDur.Value - overlapSec : -1;
                    if (start > 0 && d.HasValue && start < d.Value - 0.5)
                    {
                        // ponytail: routing still keys off track length. "Sound stops early" and "only the new segment was recorded" look identical in the audio alone, so guessing between them can only make it worse - trim cuts video and audio together, so plain concat is the safe default and the mute case gets a warning instead.
                        var reachesEnd = ad.HasValue && ad.Value >= d.Value - 1.0;
                        StatusLabel.Text = $"Cutting new part of clip {segments.Count + 1}/{clips.Count}...";
                        var cut = await _merge.TrimStartAsync(c, start, ct);
                        segments.Add(cut);
                        temps.Add(cut);
                        segDurs.Add(d.Value - start);
                        audioDelay.Add(reachesEnd ? 0 : start);
                    }
                    else
                    {
                        segments.Add(c);
                        segDurs.Add(d ?? 0);
                        audioDelay.Add(offset);
                    }
                    audioOf.Add(ad);
                    if (d.HasValue) prevDur = d;
                    offset += segDurs[^1];
                }
                // ponytail: rebuild only when a clip parks a short, new-segment-only audio at 0s; then placing each track explicitly is the only way to line it up. Fully cumulative audio just concats with the video.
                rebuilt = audioDelay.Skip(1).Any(x => x > 0.001);
                if (rebuilt)
                {
                    StatusLabel.Text = "Rebuilding audio timeline...";
                    var parts = new List<(string clip, double delaySec)>();
                    for (var i = 0; i < clips.Count; i++)
                        if (audioOf[i].HasValue) parts.Add((clips[i], audioDelay[i]));
                    await _merge.AssembleFilmAsync(segments, parts, outPath, ct);
                }
                else
                {
                    StatusLabel.Text = $"Merging {segments.Count} clips...";
                    await _merge.MergeVideosAsync(segments, outPath, ct);
                }
            }
            finally { foreach (var t in temps) try { File.Delete(t); } catch { } }
            _project = _project with { FinalPath = outPath };
            await _store.UpdateAsync(_project);
            StatusLabel.Text = $"Film ready ({(rebuilt ? "audio rebuilt" : "plain merge")}):\n" + outPath
                + "\n" + string.Join("\n", report);
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Cancelled."; }
        catch (Exception ex) { StatusLabel.Text = $"Assemble failed: {ex.Message}"; }
    }
}
