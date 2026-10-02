using AiVideoStudio.Services;

namespace AiVideoStudio.Views;

public partial class ProjectsPage : ContentPage
{
    private readonly ProjectStore _store;

    public ProjectsPage()
    {
        InitializeComponent();
        var sp = Application.Current?.Handler?.MauiContext?.Services;
        _store = sp?.GetService<ProjectStore>() ?? new ProjectStore();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _store.LoadAsync();
        ProjectsView.ItemsSource = _store.Items;
        EmptyLabel.IsVisible = _store.Items.Count == 0;
    }

    private async void OnAddClicked(object? sender, EventArgs e)
    {
        var title = TitleEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = $"Project {DateTime.Now:MM-dd HH:mm}";
        await _store.AddAsync(new Project(Guid.NewGuid().ToString("N"), title, DateTime.Now,
            AppSettings.Template, AppSettings.Steps, new List<string>(), new List<Scene>(), ""));
        TitleEntry.Text = "";
        ProjectsView.ItemsSource = _store.Items;
        EmptyLabel.IsVisible = false;
    }

    private async void OnOpenClicked(object? sender, EventArgs e)
    {
        if (sender is Button b && b.BindingContext is Project p)
            await Shell.Current.GoToAsync($"projectDetail?ProjectId={p.Id}");
    }

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        if (sender is not Button b || b.BindingContext is not Project p) return;
        if (!await DisplayAlert("Delete", $"Delete '{p.Title}'?", "Yes", "No")) return;
        await _store.DeleteAsync(p.Id);
        ProjectsView.ItemsSource = _store.Items;
        EmptyLabel.IsVisible = _store.Items.Count == 0;
    }
}
