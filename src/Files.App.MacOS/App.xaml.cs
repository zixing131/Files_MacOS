using Microsoft.Extensions.Logging;
using Uno.Resizetizer;
using Files.App.MacOS.Interop;
using Files.App.MacOS.Models;
using Files.App.MacOS.Services;
using Files.App.MacOS.ViewModels;
using Microsoft.UI.Xaml.Media;

namespace Files.App.MacOS;

public partial class App : Application, IMacOSMenuCommandTarget
{
	private readonly Dictionary<Window, MainPage> windows = [];
	private readonly List<Window> windowOrder = [];
	private readonly List<Window> activationOrder = [];
	private readonly Dictionary<Window, string> nativeWindowIdentifiers = [];
	private readonly Dictionary<Window, nint> nativeWindowHandles = [];
	private readonly Dictionary<Window, WindowPlacementState?> pendingWindowPlacements = [];
	private readonly Dictionary<Window, WindowPlacementState?> lastKnownWindowPlacements = [];
	private readonly MacOSMainMenuService mainMenuService = new();
	private readonly MacOSFileManagerIntegrationService fileManagerIntegrationService = new();
	private MacOSAuxiliaryMouseService? auxiliaryMouseService;
	private MainPage? activePage;
	private WorkspaceState? lastClosedWorkspace;
	private WindowPlacementState? lastClosedWindowPlacement;
	private bool isMainMenuInstalled;
	private bool hasRestoredWindowSession;
	private bool isRestoringWindowSession;
	private MacOSAccessibilityDisplayOptions accessibilityDisplayOptions;
	private uint systemAccentColorArgb;

	public App()
	{
		AppLanguageManager.Apply(AppLanguageManager.LoadPreference());
		IsSymbolFontAvailable = MacOSNativeMethods.RegisterSymbolFont() != 0;
		InitializeComponent();
		RefreshSystemAccentColor();
	}

	internal bool IsSymbolFontAvailable { get; }

	protected Window? MainWindow { get; private set; }

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		MainWindow = CreateWindow(restoreWorkspace: true);
		if (activePage is not null)
		{
			fileManagerIntegrationService.Install(activePage.DispatcherQueue, OpenPathsFromSystemAsync);
		}
	}

	private async Task OpenPathsFromSystemAsync(IReadOnlyList<string> paths)
	{
		string? path = paths.FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path));
		if (path is null || (!File.Exists(path) && !Directory.Exists(path)))
		{
			return;
		}

		MainPage? page = activePage ?? windows.Values.FirstOrDefault();
		if (page is null)
		{
			Window window = CreateWindow();
			page = windows[window];
		}
		await page.InitializationTask;
		await page.OpenExternalPathAsync(path);
	}

	internal MacOSMainMenuService MainMenuService => mainMenuService;

	internal int WindowCount => windows.Count;

	internal MainPage? ActivePage => activePage;

	internal MacOSAccessibilityDisplayOptions AccessibilityDisplayOptions => accessibilityDisplayOptions;

	internal bool WindowsUseUnifiedTitleBars => nativeWindowIdentifiers.Values.All(MacOSWindowPlacementService.UsesUnifiedTitleBar);

	internal Window CreateWindow(
		bool restoreWorkspace = false,
		WorkspaceState? initialWorkspace = null,
		WindowPlacementState? initialPlacement = null)
	{
		var page = new MainPage(restoreWorkspace, initialWorkspace, initialPlacement);
		var window = new Window { Content = page };
		windows.Add(window, page);
		windowOrder.Add(window);
		lastClosedWorkspace = null;
		lastClosedWindowPlacement = null;
		nativeWindowIdentifiers.Add(window, $"files-window-{Guid.NewGuid():N}");
		pendingWindowPlacements.Add(window, initialPlacement);
		lastKnownWindowPlacements.Add(window, initialPlacement);
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
		auxiliaryMouseService ??= new(
			page.DispatcherQueue,
			buttonNumber => activePage?.HandleAuxiliaryMouseButton(buttonNumber),
			(deltaX, deltaY, hasPreciseDeltas) => activePage?.HandleNativeScrollWheel(deltaX, deltaY, hasPreciseDeltas) is true,
			(magnification, phase) => activePage?.HandleNativeMagnifyGesture(magnification, phase) is true,
			quickLookVisible => activePage?.HandleNativeSpaceKey(quickLookVisible) is true,
			() => activePage?.HandleNativeQuickLookClosed());

		activePage = page;
		window.Activate();
		nativeWindowHandles[window] = MacOSWindowPlacementService.GetNativeWindowHandle(window);
		ConfigureNativeWindow(window);
		RefreshSystemAccentColor();
		RefreshAccessibilityDisplayOptions();
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
			WorkspaceState[] additionalWorkspaces = (settings.AdditionalWindowWorkspaces ?? []).Take(7).ToArray();
			for (int index = 0; index < additionalWorkspaces.Length; index++)
			{
				WindowPlacementState? placement = settings.AdditionalWindowPlacements is { } placements && index < placements.Length
					? placements[index]
					: null;
				Window window = CreateWindow(initialWorkspace: additionalWorkspaces[index], initialPlacement: placement);
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

	internal void ApplyWindowPlacement(MainPage page, WindowPlacementState? placement)
	{
		Window? window = windows.FirstOrDefault(pair => ReferenceEquals(pair.Value, page)).Key;
		if (window is null)
		{
			return;
		}

		pendingWindowPlacements[window] = placement;
		lastKnownWindowPlacements[window] = placement;
		ConfigureNativeWindow(window);
	}

	internal WindowSessionState CaptureWindowSession(MainPage fallbackPage)
	{
		(Window Window, MainPage Page)[] windowPages = windowOrder
			.Where(windows.ContainsKey)
			.Select(window => (window, windows[window]))
			.ToArray();
		WorkspaceState[] workspaces = windowPages.Select(static item => item.Page.CaptureWorkspaceState()).ToArray();
		WindowPlacementState?[] placements = windowPages.Select(item => GetWindowPlacement(item.Window)).ToArray();
		if (workspaces.Length is 0)
		{
			workspaces = [lastClosedWorkspace ?? fallbackPage.CaptureWorkspaceState()];
			placements = [lastClosedWindowPlacement];
		}

		int activeWindowIndex = 0;
		if (activePage is not null)
		{
			int pageIndex = windowOrder.FindIndex(window => windows.TryGetValue(window, out MainPage? page) && ReferenceEquals(page, activePage));
			activeWindowIndex = Math.Clamp(pageIndex, 0, workspaces.Length - 1);
		}

		return new(
			workspaces[0],
			workspaces.Skip(1).ToArray(),
			activeWindowIndex,
			placements[0],
			placements.Skip(1).ToArray());
	}

	internal void CloseWindow(MainPage page)
	{
		Window? window = windows.FirstOrDefault(pair => ReferenceEquals(pair.Value, page)).Key;
		window?.Close();
	}

	internal async Task<bool> MoveTabToNewWindowAsync(MainPage sourcePage, BrowserTabViewModel tab)
	{
		if (!sourcePage.TryCaptureTabState(tab, out BrowserTabState state))
		{
			return false;
		}

		Window window = CreateWindow(initialWorkspace: new([state]));
		MainPage destinationPage = windows[window];
		try
		{
			await destinationPage.InitializationTask;
			if (!sourcePage.DetachTabForTransfer(tab))
			{
				CloseWindow(destinationPage);
				return false;
			}

			sourcePage.ScheduleSessionSave();
			destinationPage.ScheduleSessionSave();
			UpdateMainMenu(destinationPage);
			return true;
		}
		catch
		{
			CloseWindow(destinationPage);
			throw;
		}
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
		ConfigureNativeWindow(window);
		RefreshSystemAccentColor();
		RefreshAccessibilityDisplayOptions();
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
		lastClosedWindowPlacement = GetWindowPlacement(window);
		windowOrder.Remove(window);
		activationOrder.Remove(window);
		nativeWindowIdentifiers.Remove(window);
		nativeWindowHandles.Remove(window);
		pendingWindowPlacements.Remove(window);
		lastKnownWindowPlacements.Remove(window);
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

	private void ConfigureNativeWindow(Window window)
	{
		if (!nativeWindowIdentifiers.TryGetValue(window, out string? identifier) ||
			!nativeWindowHandles.TryGetValue(window, out nint windowHandle) ||
			!MacOSWindowPlacementService.RegisterWindow(windowHandle, identifier))
		{
			return;
		}

		if (pendingWindowPlacements.Remove(window, out WindowPlacementState? placement) && placement is not null)
		{
			MacOSWindowPlacementService.ApplyPlacement(identifier, placement);
		}
	}

	private void RefreshAccessibilityDisplayOptions()
	{
		MacOSAccessibilityDisplayOptions options = MacOSAccessibilityDisplayService.GetCurrentOptions();
		if (options == accessibilityDisplayOptions && windows.Values.All(page => page.AccessibilityDisplayOptions == options))
		{
			return;
		}

		accessibilityDisplayOptions = options;
		foreach (MainPage page in windows.Values)
		{
			page.ApplyAccessibilityDisplayOptions(options);
		}
	}

	private void RefreshSystemAccentColor()
	{
		uint argb = MacOSNativeMethods.GetAccentColorArgb();
		if (argb is 0 || argb == systemAccentColorArgb)
		{
			return;
		}

		systemAccentColorArgb = argb;
		Windows.UI.Color accent = Windows.UI.Color.FromArgb(
			(byte)(argb >> 24),
			(byte)(argb >> 16),
			(byte)(argb >> 8),
			(byte)argb);
		foreach (string theme in new[] { "Light", "Dark", "Default" })
		{
			if (Resources.ThemeDictionaries[theme] is not ResourceDictionary resources)
			{
				continue;
			}

			if (resources["FilesAccentBrush"] is SolidColorBrush accentBrush)
			{
				accentBrush.Color = accent;
			}
			if (resources["FilesAccentSubtleBrush"] is SolidColorBrush subtleBrush)
			{
				subtleBrush.Color = Windows.UI.Color.FromArgb(theme is "Light" ? (byte)38 : (byte)51, accent.R, accent.G, accent.B);
			}
		}
	}

	private WindowPlacementState? GetWindowPlacement(Window window)
	{
		if (!nativeWindowIdentifiers.TryGetValue(window, out string? identifier))
		{
			return null;
		}

		WindowPlacementState? placement = MacOSWindowPlacementService.GetPlacement(identifier) ??
			pendingWindowPlacements.GetValueOrDefault(window) ??
			lastKnownWindowPlacements.GetValueOrDefault(window);
		lastKnownWindowPlacements[window] = placement;
		return placement;
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
	int ActiveWindowIndex,
	WindowPlacementState? PrimaryWindowPlacement,
	WindowPlacementState?[] AdditionalWindowPlacements);
