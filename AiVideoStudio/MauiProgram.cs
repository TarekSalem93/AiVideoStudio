using Microsoft.Extensions.Logging;
using AiVideoStudio.Services;

namespace AiVideoStudio;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		// Register services
		builder.Services.AddSingleton<IOllamaService, OllamaService>();
		builder.Services.AddSingleton<IComfyUiService, ComfyUiService>();
		builder.Services.AddSingleton<IVideoMergeService, VideoMergeService>();
		builder.Services.AddSingleton<IVoiceoverService, VoiceoverService>();
		builder.Services.AddSingleton<GenerationStore>();
		builder.Services.AddSingleton<ProjectStore>();
		builder.Services.AddSingleton<YouTubePublisher>();
		builder.Services.AddSingleton<TikTokPublisher>();
		builder.Services.AddSingleton<FacebookPublisher>();

		return builder.Build();
	}
}
