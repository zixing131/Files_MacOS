using Microsoft.Extensions.Logging;
using Uno.Resizetizer;
using Files.App.MacOS.Models;
using Files.App.MacOS.Services;

namespace Files.App.MacOS;

public partial class App : Application, IMacOSMenuCommandTarget
{
	private readonly Dictionary<Window, MainPage> windows = [];
	private readonly List<Window> windowOrder = [];
	private readonly List<Window> activationOrder = [];
	private readonly MacOSMainMenuService mainMenuService = new();
	private MainPage? activePage;
	private WorkspaceState? lastClosedWorkspace;
	private bool isMainMenuInstalled;
	private bool hasRestoredWindowSession;
	private bool isRestoringWindowSession;

	public App()
	{
		AppLanguageManager.Apply(AppLanguageManager.LoadPreference());
		InitializeComponent();
	}

	protected Window? MainWindow { get; private set; }

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		MainWindow = CreateWindow(restoreWorkspace: true);
	}

	internal MacOSMainMenuService MainMenuService => mainMenuService;

	internal int WindowCount => windows.Count;

	internal MainPage? ActivePage => activePage;

	internal Window CreateWindow(bool restoreWorkspace = false, WorkspaceState? initialWorkspace = null)
	{
		var page = new MainPage(restoreWorkspace, initialWorkspace);
		var window = new Window { Content = page };
		windows.Add(window, page);
		windowOrder.Add(window);
		lastClosedWorkspace = null;
		window.Activated += Window_Activated;
		window.Closed += Window_Closed;
		window.SetWindowIcon();

		if (!isMainMenuInstalled)
		{
			mainMenuService.Install(
				this,
				string.Equals(Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride, "zh-Hans", StringComparison.Ordinal),
				page.DispatcherQueue);
			isMainMenuInstalled = true;
		}

		activePage = page;
		window.Activate();
		UpdateMainMenu(page);
		return window;
	}

	internal async Task RestoreWindowSessionAsync(AppSettings settings)
	{
		if (hasRestoredWindowSession)
		{
			return;
		}
		hasRestoredWindowSession = true;
		isRestoringWindowSession = true;
		try
		{
			foreach (WorkspaceState workspace in (settings.AdditionalWindowWorkspaces ?? []).Take(7))
			{
				Window window = CreateWindow(initialWorkspace: workspace);
				if (windows.TryGetValue(window, out MainPage? page))
				{
					await page.InitializationTask;
				}
			}

			if (windowOrder.Count > 0)
			{
				windowOrder[Math.Clamp(settings.ActiveWindowIndex, 0, windowOrder.Count - 1)].Activate();
			}
		}
		finally
		{
			isRestoringWindowSession = false;
		}
	}

	internal WindowSessionState CaptureWindowSession(MainPage fallbackPage)
	{
		WorkspaceState[] workspaces = windowOrder
			.Where(windows.ContainsKey)
			.Select(window => windows[window].CaptureWorkspaceState())
			.ToArray();
		if (workspaces.Length is 0)
		{
			workspaces = [lastClosedWorkspace ?? fallbackPage.CaptureWorkspaceState()];
		}

		int activeWindowIndex = 0;
		if (activePage is not null)
		{
			int pageIndex = windowOrder.FindIndex(window => windows.TryGetValue(window, out MainPage? page) && ReferenceEquals(page, activePage));
			activeWindowIndex = Math.Clamp(pageIndex, 0, workspaces.Length - 1);
		}

		return new(workspaces[0], workspaces.Skip(1).ToArray(), activeWindowIndex);
	}

	internal void CloseWindow(MainPage page)
	{
		Window? window = windows.FirstOrDefault(pair => ReferenceEquals(pair.Value, page)).Key;
		window?.Close();
	}

	internal void UpdateMainMenu(MainPage page)
	{
		if (ReferenceEquals(activePage, page))
		{
			mainMenuService.UpdateValidationSnapshot(this);
		}
	}

	private void Window_Activated(object sender, WindowActivatedEventArgs args)
	{
		if (args.WindowActivationState is Windows.UI.Core.CoreWindowActivationState.Deactivated || sender is not Window window || !windows.TryGetValue(window, out MainPage? page))
		{
			return;
		}

		activePage = page;
		activationOrder.Remove(window);
		activationOrder.Add(window);
		mainMenuService.UpdateValidationSnapshot(this);
		if (hasRestoredWindowSession && !isRestoringWindowSession && page.IsInitialized)
		{
			page.ScheduleSessionSave();
		}
	}

	private void Window_Closed(object sender, WindowEventArgs args)
	{
		if (sender is not Window window || !windows.Remove(window, out MainPage? page))
		{
			return;
		}

		window.Activated -= Window_Activated;
		window.Closed -= Window_Closed;
		lastClosedWorkspace = page.CaptureWorkspaceState();
		windowOrder.Remove(window);
		activationOrder.Remove(window);
		if (ReferenceEquals(activePage, page))
		{
			activePage = activationOrder.LastOrDefault() is Window previousWindow && windows.TryGetValue(previousWindow, out MainPage? previousPage)
				? previousPage
				: windows.Values.FirstOrDefault();
		}
		if (ReferenceEquals(MainWindow, window))
		{
			MainWindow = windows.Keys.FirstOrDefault();
		}

		if (activePage is not null)
		{
			mainMenuService.UpdateValidationSnapshot(this);
		}
		else
		{
			mainMenuService.Dispose();
			isMainMenuInstalled = false;
		}
	}

	void IMacOSMenuCommandTarget.ExecuteMenuCommand(MacOSMenuCommand command)
	{
		switch (command)
		{
			case MacOSMenuCommand.NewWindow:
				CreateWindow();
				break;
			case MacOSMenuCommand.CloseWindow when activePage is not null:
				CloseWindow(activePage);
				break;
			default:
				(activePage as IMacOSMenuCommandTarget)?.ExecuteMenuCommand(command);
				break;
		}
	}

	bool IMacOSMenuCommandTarget.CanExecuteMenuCommand(MacOSMenuCommand command)
	{
		return command switch
		{
			MacOSMenuCommand.NewWindow => true,
			MacOSMenuCommand.CloseWindow => activePage is not null,
			_ => (activePage as IMacOSMenuCommandTarget)?.CanExecuteMenuCommand(command) is true,
		};
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

internal sealed record WindowSessionState(
	WorkspaceState PrimaryWorkspace,
	WorkspaceState[] AdditionalWindowWorkspaces,
	int ActiveWindowIndex);
