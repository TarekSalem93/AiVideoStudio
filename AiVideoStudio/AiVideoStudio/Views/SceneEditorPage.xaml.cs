using AiVideoStudio.Services;

namespace AiVideoStudio.Views;

public partial class SceneEditorPage : ContentPage
{
    private readonly GenerationStore _store;

    public SceneEditorPage()
    {
        InitializeComponent();
        var sp = Application.Current?.Handler?.MauiContext?.Services;
        _store = sp?.GetService<GenerationStore>() ?? new GenerationStore();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _store.LoadAsync();
        HistoryView.ItemsSource = _store.Items;
        EmptyLabel.IsVisible = _store.Items.Count == 0;
    }

    private static string? FirstVideo(Generation g)
        => g.Outputs.FirstOrDefault(p => p.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
           ?? g.Outputs.FirstOrDefault(File.Exists);

    private async void OnOpenVideoClicked(object? sender, EventArgs e)
    {
        if (sender is not Button b || b.BindingContext is not Generation g) return;
        var v = FirstVideo(g);
        if (v == null) { await DisplayAlert("History", "File no longer on disk.", "OK"); return; }
        await Launcher.Default.OpenAsync(new OpenFileRequest { File = new ReadOnlyFile(v) });
    }

    private void OnOpenFolderClicked(object? sender, EventArgs e)
    {
        if (sender is not Button b || b.BindingContext is not Generation g) return;
        var v = FirstVideo(g);
        if (v == null) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.GetDirectoryName(v) ?? FileSystem.CacheDirectory,
            UseShellExecute = true
        });
    }
}
