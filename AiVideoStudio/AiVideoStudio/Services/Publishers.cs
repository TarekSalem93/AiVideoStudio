using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using System.Text.Json;

namespace AiVideoStudio.Services;

// ponytail: SecureStorage for secrets/tokens; plaintext Preferences was never an option for credentials
public static class AuthStore
{
    public static async Task<string?> GetAsync(string key)
    {
        try { return await SecureStorage.GetAsync(key); }
        catch { return null; }
    }

    public static async Task SetAsync(string key, string value)
    {
        try { await SecureStorage.SetAsync(key, value); }
        catch { }
    }

    public static void Remove(string key)
    {
        try { SecureStorage.Remove(key); }
        catch { }
    }
}

public class SecureDataStore : Google.Apis.Util.Store.IDataStore
{
    private readonly string _prefix;
    public SecureDataStore(string prefix) => _prefix = prefix;
    public Task ClearAsync() => Task.CompletedTask;
    public Task DeleteAsync<T>(string key) { AuthStore.Remove(_prefix + key); return Task.CompletedTask; }
    public async Task<T?> GetAsync<T>(string key)
    {
        var raw = await AuthStore.GetAsync(_prefix + key);
        if (raw == null) return default;
        try { return JsonSerializer.Deserialize<T>(raw); }
        catch { return default; }
    }
    public Task StoreAsync<T>(string key, T value)
        => AuthStore.SetAsync(_prefix + key, JsonSerializer.Serialize(value));
}

public interface IPublisher
{
    string Name { get; }
    string SetupHint { get; }
    Task<bool> IsConfiguredAsync();
    Task ConnectAsync();
    Task<string> PublishAsync(string videoPath, string title, string description, IProgress<double>? progress = null, CancellationToken ct = default);
}

public class YouTubePublisher : IPublisher
{
    public string Name => "YouTube";
    public string SetupHint => "Google Cloud: enable YouTube Data API v3, create an OAuth Desktop client, paste Client ID + Secret in Settings, then Connect.";

    public async Task<bool> IsConfiguredAsync()
    {
        try { return await LoadCredentialAsync() != null; }
        catch { return false; }
    }

    public async Task ConnectAsync()
    {
        var secrets = await SecretsAsync();
        // loopback browser flow; tokens land in SecureStorage via SecureDataStore
        await GoogleWebAuthorizationBroker.AuthorizeAsync(secrets,
            new[] { YouTubeService.Scope.YoutubeUpload }, "user",
            CancellationToken.None, new SecureDataStore("yt_"));
    }

    private static async Task<ClientSecrets> SecretsAsync()
    {
        var id = await AuthStore.GetAsync("yt_client_id");
        var secret = await AuthStore.GetAsync("yt_client_secret");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("YouTube client not entered. " + new YouTubePublisher().SetupHint);
        return new ClientSecrets { ClientId = id, ClientSecret = secret };
    }

    private static async Task<UserCredential?> LoadCredentialAsync()
    {
        var secrets = await SecretsAsync();
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = secrets,
            Scopes = new[] { YouTubeService.Scope.YoutubeUpload },
            DataStore = new SecureDataStore("yt_")
        });
        var token = await flow.LoadTokenAsync("user", CancellationToken.None);
        return token == null || token.IsExpired(flow.Clock) ? null : new UserCredential(flow, "user", token);
    }

    public async Task<string> PublishAsync(string videoPath, string title, string description, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var cred = await LoadCredentialAsync()
            ?? throw new InvalidOperationException("YouTube not connected. Open Settings → YouTube → Connect first.");
        var svc = new YouTubeService(new BaseClientService.Initializer { HttpClientInitializer = cred, ApplicationName = "AiVideoStudio" });
        if (title.Length > 100) title = title[..100];
        var video = new Video
        {
            Snippet = new VideoSnippet { Title = title, Description = description, CategoryId = "22" },
            Status = new VideoStatus { PrivacyStatus = Preferences.Get("yt_privacy", "unlisted") }
        };
        await using var fs = File.OpenRead(videoPath);
        var req = svc.Videos.Insert(video, "snippet,status", fs, "video/*");
        req.ChunkSize = ResumableUpload.MinimumChunkSize * 4;
        req.ProgressChanged += p =>
        {
            if (p.Status == UploadStatus.Uploading && fs.Length > 0)
                progress?.Report((double)p.BytesSent / fs.Length);
        };
        var resp = await req.UploadAsync(ct);
        if (resp.Exception != null) throw new InvalidOperationException("YouTube upload failed: " + resp.Exception.Message);
        var id = req.ResponseBody?.Id ?? throw new InvalidOperationException("YouTube finished without a video id.");
        progress?.Report(1.0);
        return "https://youtube.com/watch?v=" + id;
    }
}

public class TikTokPublisher : IPublisher
{
    public string Name => "TikTok";
    public string SetupHint => "TikTok for Developers: approved app with video.upload scope, paste the access token in Settings.";

    public Task ConnectAsync()
        => throw new InvalidOperationException("TikTok has no desktop login loop — paste an access token in Settings. " + SetupHint);

    public async Task<bool> IsConfiguredAsync()
        => !string.IsNullOrWhiteSpace(await AuthStore.GetAsync("tt_token"));

    public async Task<string> PublishAsync(string videoPath, string title, string description, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var token = await AuthStore.GetAsync("tt_token")
            ?? throw new InvalidOperationException("TikTok token missing. " + SetupHint);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var size = new FileInfo(videoPath).Length;
        const int chunk = 10 * 1024 * 1024;
        var total = (int)((size + chunk - 1) / chunk);
        var init = new { source_info = new { source = "FILE_UPLOAD", video_size = size, chunk_size = chunk, total_chunk_count = total } };
        using var initRes = await client.PostAsync("https://open.tiktokapis.com/v2/post/publish/video/init/",
            new StringContent(JsonSerializer.Serialize(init), System.Text.Encoding.UTF8, "application/json"), ct);
        var initBody = await initRes.Content.ReadAsStringAsync(ct);
        if (!initRes.IsSuccessStatusCode) throw new InvalidOperationException($"TikTok init failed: {initBody}");
        using var doc = JsonDocument.Parse(initBody);
        if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("code", out var code) && code.GetString() != "ok")
            throw new InvalidOperationException($"TikTok init rejected: {initBody}");
        var data = doc.RootElement.GetProperty("data");
        var uploadUrl = data.GetProperty("upload_url").GetString()!;
        var publishId = data.GetProperty("publish_id").GetString()!;
        await using var fs = File.OpenRead(videoPath);
        var buf = new byte[chunk];
        for (var i = 0; i < total; i++)
        {
            var read = await fs.ReadAsync(buf.AsMemory(0, chunk), ct);
            var from = (long)i * chunk;
            using var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
            var part = new ByteArrayContent(buf, 0, read);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
            part.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, from + read - 1, size);
            req.Content = part;
            using var put = await client.SendAsync(req, ct);
            if (!put.IsSuccessStatusCode)
                throw new InvalidOperationException($"TikTok chunk {i + 1}/{total} failed: {await put.Content.ReadAsStringAsync(ct)}");
            progress?.Report((double)(i + 1) / total * 0.99);
        }
        progress?.Report(1.0);
        return $"TikTok accepted (publish_id {publishId}). Title/description are set from your TikTok draft inbox — check the app.";
    }
}

public class FacebookPublisher : IPublisher
{
    public string Name => "Facebook";
    public string SetupHint => "Meta app with pages_manage_posts + publish_video, paste Page ID + Page token in Settings.";

    public Task ConnectAsync()
        => throw new InvalidOperationException("Facebook needs a pasted Page token in Settings. " + SetupHint);

    public async Task<bool> IsConfiguredAsync()
        => !string.IsNullOrWhiteSpace(await AuthStore.GetAsync("fb_token"));

    public async Task<string> PublishAsync(string videoPath, string title, string description, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var token = await AuthStore.GetAsync("fb_token")
            ?? throw new InvalidOperationException("Facebook token missing. " + SetupHint);
        var page = (await AuthStore.GetAsync("fb_page_id"))?.Trim()
            ?? throw new InvalidOperationException("Facebook Page ID missing. " + SetupHint);
        using var client = new HttpClient();
        var size = new FileInfo(videoPath).Length;
        async Task<JsonElement> CallAsync(HttpContent content)
        {
            using var res = await client.PostAsync($"https://graph.facebook.com/v21.0/{page}/videos", content, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Facebook upload failed: {body}");
            return JsonDocument.Parse(body).RootElement;
        }
        var start = await CallAsync(new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["upload_phase"] = "start", ["file_size"] = size.ToString(), ["access_token"] = token
        }));
        var session = start.GetProperty("upload_session_id").GetString()!;
        var videoId = start.GetProperty("video_id").GetString()!;
        var offset = start.TryGetProperty("start_offset", out var so) ? long.Parse(so.GetString() ?? "0") : 0;
        const int chunk = 8 * 1024 * 1024;
        await using var fs = File.OpenRead(videoPath);
        fs.Position = offset;
        var buf = new byte[chunk];
        int read;
        while ((read = await fs.ReadAsync(buf.AsMemory(0, chunk), ct)) > 0)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("transfer"), "upload_phase");
            form.Add(new StringContent(session), "upload_session_id");
            form.Add(new StringContent(offset.ToString()), "start_offset");
            form.Add(new StringContent(token), "access_token");
            form.Add(new ByteArrayContent(buf, 0, read), "video_file", "chunk.mp4");
            var tr = await CallAsync(form);
            offset = tr.TryGetProperty("start_offset", out var no) ? long.Parse(no.GetString() ?? offset.ToString()) : offset + read;
            progress?.Report(Math.Min(0.99, (double)offset / size));
        }
        await CallAsync(new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["upload_phase"] = "finish", ["upload_session_id"] = session,
            ["title"] = title, ["description"] = description, ["access_token"] = token
        }));
        progress?.Report(1.0);
        return $"Facebook video {videoId} uploading — check your Page's video library.";
    }
}
