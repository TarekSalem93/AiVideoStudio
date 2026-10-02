namespace AiVideoStudio.Services;

// ponytail: real OAuth/chunked-upload code lands here per platform once dev apps are registered; stubs fail loudly instead of faking success
public interface IPublisher
{
    string Name { get; }
    string SetupHint { get; }
    Task<bool> IsConfiguredAsync();
    Task<string> PublishAsync(string videoPath, string title, string description, IProgress<double>? progress = null, CancellationToken ct = default);
}

public abstract class PublisherStub : IPublisher
{
    public abstract string Name { get; }
    public abstract string SetupHint { get; }
    public Task<bool> IsConfiguredAsync() => Task.FromResult(false);
    public Task<string> PublishAsync(string videoPath, string title, string description, IProgress<double>? progress = null, CancellationToken ct = default)
        => throw new InvalidOperationException($"{Name} not configured. {SetupHint}");
}

public class YouTubePublisher : PublisherStub
{
    public override string Name => "YouTube";
    public override string SetupHint => "Add Google.Apis.YouTube.v3, create an OAuth client in Google Cloud, paste the client JSON in Settings.";
}

public class TikTokPublisher : PublisherStub
{
    public override string Name => "TikTok";
    public override string SetupHint => "Register an app at TikTok for Developers with video.upload scope, paste keys in Settings.";
}

public class FacebookPublisher : PublisherStub
{
    public override string Name => "Facebook";
    public override string SetupHint => "Create a Meta app with pages_manage_posts + publish_video, paste the token in Settings.";
}
