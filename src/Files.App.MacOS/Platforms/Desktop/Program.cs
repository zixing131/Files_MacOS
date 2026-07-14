using Uno.UI.Hosting;

namespace Files.App.MacOS;

internal static class Program
{
	[STAThread]
	public static void Main(string[] args)
	{
		App.InitializeLogging();

		var host = UnoPlatformHostBuilder.Create()
		  .App(static () => new App())
		  .UseMacOS()
		  .Build();

		host.Run();
	}
}
