using Microsoft.Extensions.DependencyInjection;

namespace AiVideoStudio;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		// ponytail: single deterministic dark theme instead of maintaining two palettes
		Current!.UserAppTheme = AppTheme.Dark;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}