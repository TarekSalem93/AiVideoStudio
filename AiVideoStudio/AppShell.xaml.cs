namespace AiVideoStudio;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();
		Routing.RegisterRoute("projectDetail", typeof(Views.ProjectDetailPage));
	}
}
