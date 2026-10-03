using AiVideoStudio.Services;

namespace AiVideoStudio.Views;

public partial class SettingsPage : ContentPage
{
    private static readonly string[] Privacies = { "unlisted", "private", "public" };

    public SettingsPage()
    {
        InitializeComponent();
        YtPrivacyPicker.ItemsSource = Privacies.ToList();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        OllamaUrlEntry.Text = AppSettings.OllamaUrl;
        ComfyUiUrlEntry.Text = AppSettings.ComfyUrl;
        StepsEntry.Text = AppSettings.Steps.ToString();
        FfmpegEntry.Text = AppSettings.FfmpegPath;
        VoiceEntry.Text = AppSettings.Voice;
        YtIdEntry.Text = await AuthStore.GetAsync("yt_client_id") ?? "";
        YtPrivacyPicker.SelectedIndex = Math.Max(0, Array.IndexOf(Privacies, Preferences.Get("yt_privacy", "unlisted")));
        TtKeyEntry.Text = await AuthStore.GetAsync("tt_client_key") ?? "";
        FbPageEntry.Text = await AuthStore.GetAsync("fb_page_id") ?? "";
        await RefreshStatusesAsync();
    }

    private async Task RefreshStatusesAsync()
    {
        YtStatus.Text = await new YouTubePublisher().IsConfiguredAsync() ? "YouTube: connected." : "YouTube: not connected.";
        TtStatus.Text = await new TikTokPublisher().IsConfiguredAsync() ? "TikTok: token saved." : "TikTok: no token.";
        FbStatus.Text = await new FacebookPublisher().IsConfiguredAsync() ? "Facebook: token saved." : "Facebook: no token.";
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

    private async void OnYtSaveClicked(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(YtIdEntry.Text)) await AuthStore.SetAsync("yt_client_id", YtIdEntry.Text.Trim());
        if (!string.IsNullOrWhiteSpace(YtSecretEntry.Text)) await AuthStore.SetAsync("yt_client_secret", YtSecretEntry.Text.Trim());
        Preferences.Set("yt_privacy", YtPrivacyPicker.SelectedItem as string ?? "unlisted");
        YtStatus.Text = "Saved. Now tap Connect and approve in the browser.";
    }

    private async void OnYtConnectClicked(object? sender, EventArgs e)
    {
        try
        {
            YtStatus.Text = "Opening browser — approve, then return here.";
            await new YouTubePublisher().ConnectAsync();
            YtStatus.Text = "YouTube: connected.";
        }
        catch (Exception ex) { YtStatus.Text = $"Connect failed: {ex.Message.Split('\n')[0]}"; }
    }

    private async void OnTtSaveClicked(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TtKeyEntry.Text)) await AuthStore.SetAsync("tt_client_key", TtKeyEntry.Text.Trim());
        if (!string.IsNullOrWhiteSpace(TtSecretEntry.Text)) await AuthStore.SetAsync("tt_client_secret", TtSecretEntry.Text.Trim());
        if (!string.IsNullOrWhiteSpace(TtTokenEntry.Text)) await AuthStore.SetAsync("tt_token", TtTokenEntry.Text.Trim());
        TtTokenEntry.Text = "";
        await RefreshStatusesAsync();
    }

    private async void OnFbSaveClicked(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(FbPageEntry.Text)) await AuthStore.SetAsync("fb_page_id", FbPageEntry.Text.Trim());
        if (!string.IsNullOrWhiteSpace(FbTokenEntry.Text)) await AuthStore.SetAsync("fb_token", FbTokenEntry.Text.Trim());
        FbTokenEntry.Text = "";
        await RefreshStatusesAsync();
    }
}
