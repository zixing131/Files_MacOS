using Microsoft.Extensions.Logging;
using Uno.Resizetizer;
using Files.App.MacOS.Services;

namespace Files.App.MacOS;

public partial class App : Application
{
	public App()
	{
		AppLanguageManager.Apply(AppLanguageManager.LoadPreference());
		InitializeComponent();
	}

	protected Window? MainWindow { get; private set; }

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		MainWindow = new Window
		{
			Content = new MainPage(),
		};

		MainWindow.SetWindowIcon();
		MainWindow.Activate();
	}

	public static void InitializeLogging()
	{
#if DEBUG
		var factory = LoggerFactory.Create(builder =>
		{
			builder.AddConsole();
			builder.SetMinimumLevel(LogLevel.Information);
			builder.AddFilter("Uno", LogLevel.Warning);
			builder.AddFilter("Microsoft", LogLevel.Warning);
		});

		global::Uno.Extensions.LogExtensionPoint.AmbientLoggerFactory = factory;
		global::Uno.UI.Adapter.Microsoft.Extensions.Logging.LoggingAdapter.Initialize();
#endif
	}
}
