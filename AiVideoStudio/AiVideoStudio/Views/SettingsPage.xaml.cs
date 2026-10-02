using AiVideoStudio.Services;

namespace AiVideoStudio.Views;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        OllamaUrlEntry.Text = AppSettings.OllamaUrl;
        ComfyUiUrlEntry.Text = AppSettings.ComfyUrl;
        StepsEntry.Text = AppSettings.Steps.ToString();
        FfmpegEntry.Text = AppSettings.FfmpegPath;
        VoiceEntry.Text = AppSettings.Voice;
        CredsLabel.Text = "YouTube: not configured\nTikTok: not configured\nFacebook: not configured";
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        if (!int.TryParse(StepsEntry.Text, out var steps) || steps < 1) steps = 6;
        AppSettings.Save(
            string.IsNullOrWhiteSpace(OllamaUrlEntry.Text) ? "http://localhost:11434" : OllamaUrlEntry.Text.Trim(),
            string.IsNullOrWhiteSpace(ComfyUiUrlEntry.Text) ? "http://127.0.0.1:8188" : ComfyUiUrlEntry.Text.Trim(),
            steps,
            AppSettings.Template,
            AppSettings.Model,
            string.IsNullOrWhiteSpace(FfmpegEntry.Text) ? "ffmpeg" : FfmpegEntry.Text.Trim(),
            string.IsNullOrWhiteSpace(VoiceEntry.Text) ? "ar-SA-ZariyahNeural" : VoiceEntry.Text.Trim());
        Preferences.Set("aspect_ratio", AspectRatioEntry.Text);
        SettingsStatusLabel.Text = "Saved. New server URLs apply immediately.";
    }
}
