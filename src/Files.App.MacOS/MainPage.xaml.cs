using Files.App.MacOS.Controls;
using Files.App.MacOS.Models;
using Files.App.MacOS.Services;
using Files.App.MacOS.ViewModels;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.Resources;
using Windows.Storage;
using Windows.System;

namespace Files.App.MacOS;

public sealed partial class MainPage : Page, IMacOSMenuCommandTarget
{
	private const double SplitDividerWidth = 8;
	private const double MinimumPaneWidth = 240;
	private const double SidebarDividerWidth = 6;
	private const double MinimumSidebarWidth = 180;
	private const double MaximumSidebarWidth = 420;
	private const double KeyboardResizeStep = 16;
	private const string HomeBreadcrumbIconData = "M7.07934 1.22258C7.60474 0.797737 8.35525 0.797737 8.88065 1.22258L15.4689 6.55068C16.5623 7.43475 15.9402 9.20232 14.5276 9.20232H14.25V13.75C14.25 14.7165 13.4665 15.5 12.5 15.5H10.5C9.5335 15.5 8.75 14.7165 8.75 13.75V11.25C8.75 10.8358 8.41421 10.5 8 10.5C7.58579 10.5 7.25 10.8358 7.25 11.25V13.75C7.25 14.7165 6.4665 15.5 5.5 15.5H3.5C2.5335 15.5 1.75 14.7165 1.75 13.75V9.20232H1.43239C0.0198307 9.20232 -0.602245 7.43475 0.491105 6.55068L7.07934 1.22258ZM8.25178 2.0001C8.09322 1.87188 7.86677 1.87188 7.70821 2.0001L1.11996 7.3282C0.756928 7.62179 0.963431 8.20232 1.43239 8.20232H2.75V13.75C2.75 14.1642 3.08579 14.5 3.5 14.5H5.5C5.91421 14.5 6.25 14.1642 6.25 13.75V11.25C6.25 10.2835 7.0335 9.5 8 9.5C8.9665 9.5 9.75 10.2835 9.75 11.25V13.75C9.75 14.1642 10.0858 14.5 10.5 14.5H12.5C12.9142 14.5 13.25 14.1642 13.25 13.75V8.20232H14.5276C14.9966 8.20232 15.2031 7.62179 14.84 7.3282L8.25178 2.0001Z";
	private MainPageViewModel ViewModel { get; } = new();
	private IFileOperationService FileOperationService { get; } = new LocalFileOperationService();
	private FileRenameService FileRenameService { get; } = new();
	private IFileTransferService FileTransferService { get; } = new LocalFileTransferService();
	private FileTransferHistoryService FileTransferHistoryService { get; }
	private FileTrashHistoryService FileTrashHistoryService { get; }
	private IFileClipboardService FileClipboardService { get; } = new MacOSFileClipboardService();
	private IFilePropertiesService FilePropertiesService { get; } = new LocalFilePropertiesService();
	private IAppSettingsService SettingsService { get; } = new JsonAppSettingsService();
	private IArchiveService ArchiveService { get; } = new ZipArchiveService();
	private IMacOSWorkspaceService WorkspaceService { get; } = new MacOSWorkspaceService();
	private IMacOSAccessGrantService AccessGrantService { get; } = new MacOSAccessGrantService();
	private MacOSMainMenuService MainMenuService => ((App)Application.Current).MainMenuService;
	private readonly ResourceLoader resourceLoader = ResourceLoader.GetForViewIndependentUse();
	private static readonly SemaphoreSlim SettingsSaveLock = new(1, 1);
	private IReadOnlyList<LocalFileSystemItem> selectedItems = [];
	private CancellationTokenSource? fileTransferCancellation;
	private CancellationTokenSource? searchInputCancellation;
	private CancellationTokenSource? settingsSaveCancellation;
	private CancellationTokenSource? accessibilityAnnouncementCancellation;
	private readonly Stack<FileOperationHistoryEntry> undoHistory = new();
	private readonly Stack<FileOperationHistoryEntry> redoHistory = new();
	private AppSettings currentSettings = new();
	private AppSettings persistedSettingsBaseline = new();
	private bool isResizingSplit;
	private bool isResizingSidebar;
	private bool isConnectingServer;
	private bool isHistoryOperationRunning;
	private bool isUpdatingSelection;
	private bool isUpdatingSidebarSelection;
	private bool isEditingAddress;
	private bool isPointerOverPrimaryGrid;
	private bool isPointerOverSecondaryGrid;
	private bool isSidebarOpen = true;
	private double sidebarWidth = 228;
	private bool isPreviewPaneOpen;
	private MacOSAccessibilityDisplayOptions accessibilityDisplayOptions;
	private readonly bool restoresWorkspace;
	private readonly WorkspaceState? initialWorkspace;
	private readonly WindowPlacementState? initialPlacement;
	private readonly TaskCompletionSource initializationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private string? lastRecordedRecentPath;
	private long statusTextCallbackToken;
	private long previewSelectionCallbackToken;
	private bool accessibilityAnnouncementsRegistered;
	private string? lastAccessibilityAnnouncement;
	private long lastContentWheelTimestamp;
	private double contentWheelAcceleration = 1;

	public MainPage()
		: this(restoresWorkspace: true)
	{
	}

	internal MainPage(
		bool restoresWorkspace,
		WorkspaceState? initialWorkspace = null,
		WindowPlacementState? initialPlacement = null)
	{
		this.restoresWorkspace = restoresWorkspace;
		this.initialWorkspace = initialWorkspace;
		this.initialPlacement = initialPlacement;
		FileTransferHistoryService = new(FileTransferService, FileRenameService);
		FileTrashHistoryService = new(WorkspaceService, FileTransferHistoryService);
		InitializeComponent();
		DataContext = ViewModel;
		AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Page_PointerPressed), handledEventsToo: true);
		RegisterContentWheelHandler(GridItems);
		RegisterContentWheelHandler(DetailsItems);
		RegisterContentWheelHandler(SecondaryGridItems);
		RegisterContentWheelHandler(SecondaryDetailsItems);
		GridItems.PointerEntered += (_, _) => SetGridPointerState(isPrimary: true, isPointerOver: true);
		GridItems.PointerExited += (_, _) => SetGridPointerState(isPrimary: true, isPointerOver: false);
		SecondaryGridItems.PointerEntered += (_, _) => SetGridPointerState(isPrimary: false, isPointerOver: true);
		SecondaryGridItems.PointerExited += (_, _) => SetGridPointerState(isPrimary: false, isPointerOver: false);
		PrimaryPaneBorder.AddHandler(UIElement.RightTappedEvent, new RightTappedEventHandler(PrimaryPane_RightTapped), handledEventsToo: true);
		SecondaryPaneBorder.AddHandler(UIElement.RightTappedEvent, new RightTappedEventHandler(SecondaryPane_RightTapped), handledEventsToo: true);
		RegisterDividerPointerHandlers(SidebarDivider, SidebarDivider_PointerPressed, SidebarDivider_PointerMoved, SidebarDivider_PointerReleased, SidebarDivider_PointerCaptureLost);
		RegisterDividerPointerHandlers(SplitDivider, SplitDivider_PointerPressed, SplitDivider_PointerMoved, SplitDivider_PointerReleased, SplitDivider_PointerCaptureLost);
		MoreSelectionSubItem.Text = GetResource("MoreSelectionSubItem/Text");
		MoreArchiveSubItem.Text = GetResource("MoreArchiveSubItem/Text");
		LocalizeContextMenuSubItems();
		ConfigureIconButton(ToggleSidebarButton, "ToggleSidebarTooltip");
		ConfigureIconButton(BackButton, "BackNavigationTooltip");
		ConfigureIconButton(ForwardButton, "ForwardNavigationTooltip");
		ConfigureIconButton(UpButton, "UpNavigationTooltip");
		ConfigureIconButton(RefreshButton, "RefreshNavigationTooltip");
		ConfigureIconButton(NewTabButton, "NewTabTooltip");
		ConfigureIconButton(ClosePreviewPaneButton, "ClosePreviewPaneTooltip");
		ConfigureIconButton(GridViewStatusButton, "GridViewTooltip");
		ConfigureIconButton(DetailsViewStatusButton, "DetailsViewTooltip");
		ConfigureIconButton(SearchOptionsButton, "SearchOptionsTooltip");
		ConfigureIconButton(MoreCommandsButton, "MoreCommandsTooltip");
		ConfigureIconButton(SidebarDivider, "SidebarResizeTooltip");
		ConfigureIconButton(SplitDivider, "SplitResizeTooltip");
		ConfigureAccessibleName(Tabs, "TabStripAutomationName");
		ConfigureAccessibleName(SidebarList, "LocationsAutomationName");
		ConfigureAccessibleName(AddressBox, "AddressBarAutomationName");
		ConfigureAccessibleName(SearchBox, "SearchBoxAutomationName");
		ConfigureAccessibleName(GridItems, "PrimaryFilesAutomationName");
		ConfigureAccessibleName(DetailsItems, "PrimaryFilesAutomationName");
		ConfigureAccessibleName(SecondaryGridItems, "SecondaryFilesAutomationName");
		ConfigureAccessibleName(SecondaryDetailsItems, "SecondaryFilesAutomationName");
		ConfigureAccessibleName(FileOperationProgressBar, "FileOperationProgressAutomationName");
		ConfigureAccessibleName(StatusTextBlock, "StatusBarAutomationName");
		ConfigureAccessibleName(PreviewSelectionSummary, "PreviewSelectionAutomationName");
		statusTextCallbackToken = StatusTextBlock.RegisterPropertyChangedCallback(TextBlock.TextProperty, AccessibilityStatusText_PropertyChanged);
		previewSelectionCallbackToken = PreviewSelectionSummary.RegisterPropertyChangedCallback(TextBlock.TextProperty, AccessibilityStatusText_PropertyChanged);
		accessibilityAnnouncementsRegistered = true;
		ConfigureCommandButtonAccessibility(
			UndoButton,
			RedoButton,
			NewCommandButton,
			CutButton,
			CopyButton,
			PasteButton,
			RenameButton,
			ShareButton,
			DeleteButton,
			RevealButton,
			PropertiesButton,
			SelectionButton,
			ArchiveButton,
			FavoriteButton,
			TerminalButton,
			PreviewPaneButton,
			SplitViewButton,
			SortCommandButton,
			ViewCommandButton);
		ToolTipService.SetToolTip(SearchBox, GetResource("SearchSyntaxHelp"));
		Loaded += MainPage_Loaded;
		Unloaded += MainPage_Unloaded;
	}

	private void ConfigureIconButton(Button button, string resourceKey)
	{
		string label = GetResource(resourceKey);
		ToolTipService.SetToolTip(button, label);
		Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
	}

	private void ConfigureAccessibleName(DependencyObject element, string resourceKey)
	{
		Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, GetResource(resourceKey));
	}

	private static void ConfigureCommandButtonAccessibility(params Button[] buttons)
	{
		foreach (Button button in buttons)
		{
			if (button.Content is CommandLabel { Content.Length: > 0 } label)
			{
				Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label.Content);
			}
		}
	}

	private static void RegisterDividerPointerHandlers(
		UIElement divider,
		PointerEventHandler pressed,
		PointerEventHandler moved,
		PointerEventHandler released,
		PointerEventHandler captureLost)
	{
		divider.AddHandler(UIElement.PointerPressedEvent, pressed, handledEventsToo: true);
		divider.AddHandler(UIElement.PointerMovedEvent, moved, handledEventsToo: true);
		divider.AddHandler(UIElement.PointerReleasedEvent, released, handledEventsToo: true);
		divider.AddHandler(UIElement.PointerCaptureLostEvent, captureLost, handledEventsToo: true);
	}

	private void AccessibilityStatusText_PropertyChanged(DependencyObject sender, DependencyProperty property)
	{
		if (IsInitialized && sender is TextBlock { Text.Length: > 0 } textBlock)
		{
			ScheduleAccessibilityAnnouncement(textBlock.Text);
		}
	}

	private async void ScheduleAccessibilityAnnouncement(string announcement)
	{
		if (string.Equals(announcement, lastAccessibilityAnnouncement, StringComparison.Ordinal))
		{
			return;
		}

		accessibilityAnnouncementCancellation?.Cancel();
		accessibilityAnnouncementCancellation?.Dispose();
		var cancellation = new CancellationTokenSource();
		accessibilityAnnouncementCancellation = cancellation;
		try
		{
			await Task.Delay(400, cancellation.Token);
			if (!cancellation.IsCancellationRequested && MacOSAccessibilityAnnouncementService.Announce(announcement))
			{
				lastAccessibilityAnnouncement = announcement;
			}
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
		}
	}

	private DirectoryBrowserViewModel? Browser => ViewModel.ActiveBrowser;

	private async void MainPage_Loaded(object sender, RoutedEventArgs e)
	{
		Loaded -= MainPage_Loaded;
		try
		{
			await FileTransferHistoryService.CleanupOrphanedStagingAsync();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
		currentSettings = await SettingsService.LoadAsync();
		IReadOnlyList<RestoredFolderAccessGrant> restoredGrants = await AccessGrantService.RestoreAsync(
			currentSettings.AccessGrants ?? []);
		currentSettings = AccessGrantSettingsMapper.Apply(currentSettings, restoredGrants);
		persistedSettingsBaseline = currentSettings;
		isSidebarOpen = currentSettings.IsSidebarOpen;
		sidebarWidth = currentSettings.SidebarWidth;
		isPreviewPaneOpen = currentSettings.IsPreviewPaneOpen;
		((App)Application.Current).ApplyWindowPlacement(
			this,
			restoresWorkspace ? currentSettings.WindowPlacement : initialPlacement);
		ViewModel.ApplySettings(currentSettings with
		{
			Workspace = restoresWorkspace ? currentSettings.Workspace : initialWorkspace,
		});
		ApplyTheme(currentSettings.Theme);
		await ViewModel.InitializeAsync();
		ViewModel.WorkspaceChanged += ViewModel_WorkspaceChanged;
		RecordRecentLocation();
		UpdatePaneVisuals();
		UpdateSidebarVisuals();
		UpdateSidebarSelection();
		UpdatePreviewPaneVisuals();
		UpdateCommandStates();
		App app = (App)Application.Current;
		app.UpdateMainMenu(this);
		if (restoresWorkspace)
		{
			await app.RestoreWindowSessionAsync(currentSettings);
		}
		initializationCompletion.TrySetResult();
		if (restoresWorkspace || initialWorkspace is null)
		{
			ScheduleWorkspaceSave();
		}
		if (restoresWorkspace && string.Equals(Environment.GetEnvironmentVariable("FILES_MACOS_PERF_DIAGNOSTICS"), "1", StringComparison.Ordinal))
		{
			_ = ReportPerformanceDiagnosticsWithErrorReportingAsync();
		}
	}

	internal Task InitializationTask => initializationCompletion.Task;

	internal MacOSAccessibilityDisplayOptions AccessibilityDisplayOptions => accessibilityDisplayOptions;

	internal bool IsInitialized => initializationCompletion.Task.IsCompleted;

	internal WorkspaceState CaptureWorkspaceState() => ViewModel.CaptureWorkspaceState();

	internal bool TryCaptureTabState(BrowserTabViewModel tab, out BrowserTabState state) => ViewModel.TryCaptureTabState(tab, out state);

	internal bool DetachTabForTransfer(BrowserTabViewModel tab) => ViewModel.DetachTabForTransfer(tab);

	internal void ScheduleSessionSave() => ScheduleWorkspaceSave();

	private async Task ReportPerformanceDiagnosticsWithErrorReportingAsync()
	{
		try
		{
			await ReportPerformanceDiagnosticsAsync();
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"FILES_MACOS_PERF_ERROR type={ex.GetType().Name} message={ex.Message}");
		}
	}

	private async Task ReportPerformanceDiagnosticsAsync()
	{
		await Task.Delay(1500);
		DirectoryBrowserViewModel? browser = ViewModel.ActiveTab?.Browser;
		if (browser is null)
		{
			return;
		}

		FrameworkElement visibleControl = browser.IsGridView ? GridItems : DetailsItems;
		int realizedContainers = CountRealizedContainers(visibleControl, browser.Items.Count);
		bool selectionRoundtrip = VerifySelectionRoundtrip(visibleControl, browser.Items.Count);
		bool initialSidebarState = isSidebarOpen;
		isSidebarOpen = !initialSidebarState;
		UpdateSidebarVisuals();
		bool sidebarChanged = SidebarBorder.Visibility == (isSidebarOpen ? Visibility.Visible : Visibility.Collapsed);
		isSidebarOpen = initialSidebarState;
		UpdateSidebarVisuals();
		bool sidebarRoundtrip = sidebarChanged && SidebarBorder.Visibility == (initialSidebarState ? Visibility.Visible : Visibility.Collapsed);
		double initialSidebarWidth = sidebarWidth;
		isSidebarOpen = true;
		SetSidebarWidth(312, persist: false);
		UpdateSidebarVisuals();
		bool sidebarResizeRoundtrip = Math.Abs(SidebarColumn.Width.Value - 312) < 0.1 && Math.Abs(SidebarDividerColumn.Width.Value - SidebarDividerWidth) < 0.1;
		SetSidebarWidth(sidebarWidth - KeyboardResizeStep, persist: false);
		bool keyboardResize = Math.Abs(sidebarWidth - (312 - KeyboardResizeStep)) < 0.1;
		SetSidebarWidth(sidebarWidth + KeyboardResizeStep, persist: false);
		double splitTestWidth = 1000;
		keyboardResize &= GetSplitRatioForKey(0.5, splitTestWidth, VirtualKey.Left) is double leftRatio && leftRatio < 0.5 &&
			GetSplitRatioForKey(leftRatio, splitTestWidth, VirtualKey.Right) is double restoredRatio && Math.Abs(restoredRatio - 0.5) < 0.001 &&
			GetSplitRatioForKey(0.5, splitTestWidth, VirtualKey.Home) is double minimumRatio && minimumRatio >= MinimumPaneWidth / splitTestWidth &&
			GetSplitRatioForKey(0.5, splitTestWidth, VirtualKey.End) is double maximumRatio && maximumRatio <= 1 - (MinimumPaneWidth / splitTestWidth);
		sidebarWidth = initialSidebarWidth;
		isSidebarOpen = initialSidebarState;
		UpdateSidebarVisuals();
		BeginAddressEdit();
		bool addressEditVisible = AddressBox.Visibility is Visibility.Visible && BreadcrumbScrollViewer.Visibility is Visibility.Collapsed;
		EndAddressEdit();
		bool addressRoundtrip = addressEditVisible && AddressBox.Visibility is Visibility.Collapsed && BreadcrumbScrollViewer.Visibility is Visibility.Visible;
		bool initialPreviewState = isPreviewPaneOpen;
		isPreviewPaneOpen = !initialPreviewState;
		UpdatePreviewPaneVisuals();
		bool previewChanged = PreviewPaneBorder.Visibility == (isPreviewPaneOpen ? Visibility.Visible : Visibility.Collapsed);
		isPreviewPaneOpen = initialPreviewState;
		UpdatePreviewPaneVisuals();
		bool previewRoundtrip = previewChanged && PreviewPaneBorder.Visibility == (initialPreviewState ? Visibility.Visible : Visibility.Collapsed);
		UpdateCommandToolbarLayout(2000);
		bool toolbarWide = MoreCommandsButton.Visibility is Visibility.Collapsed && RevealButton.Visibility is Visibility.Visible && RenameButton.Visibility is Visibility.Visible;
		UpdateCommandToolbarLayout(1400);
		bool toolbarOverflow = MoreCommandsButton.Visibility is Visibility.Visible && RevealButton.Visibility is Visibility.Collapsed && RenameButton.Visibility is Visibility.Visible;
		UpdateCommandToolbarLayout(900);
		bool toolbarCompact = MoreCommandsButton.Visibility is Visibility.Visible && RenameButton.Visibility is Visibility.Collapsed && MoreRenameItem.Visibility is Visibility.Visible;
		UpdateCommandToolbarLayout(CommandToolbarBorder.ActualWidth);
		bool toolbarBreakpoints = toolbarWide && toolbarOverflow && toolbarCompact;
		Button[] iconCommandButtons =
		[
			UndoButton,
			RedoButton,
			NewCommandButton,
			CutButton,
			CopyButton,
			PasteButton,
			RenameButton,
			ShareButton,
			DeleteButton,
			RevealButton,
			PropertiesButton,
			SelectionButton,
			ArchiveButton,
			FavoriteButton,
			TerminalButton,
			PreviewPaneButton,
			SplitViewButton,
			SortCommandButton,
			ViewCommandButton,
		];
		bool toolbarIcons = iconCommandButtons.All(static button =>
			button.Content is CommandLabel { IconData: not null, Content.Length: > 0 });
		if (!toolbarIcons)
		{
			Console.Error.WriteLine(
				$"FILES_MACOS_TOOLBAR_ICON_ERROR buttons={string.Join(',', iconCommandButtons.Where(static button => button.Content is not CommandLabel { IconData: not null, Content.Length: > 0 }).Select(static button => button.Name))}");
		}
		Button[] navigationIconButtons =
		[
			ToggleSidebarButton,
			BackButton,
			ForwardButton,
			UpButton,
			RefreshButton,
			NewTabButton,
			SearchOptionsButton,
			ClosePreviewPaneButton,
			GridViewStatusButton,
			DetailsViewStatusButton,
			MoreCommandsButton,
		];
		bool navigationIcons = navigationIconButtons.All(static button => button.Content is PathIcon { Data: not null }) &&
			navigationIconButtons.All(static button => !string.IsNullOrWhiteSpace(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button)));
		Button[] primaryNavigationButtons = [ToggleSidebarButton, BackButton, ForwardButton, UpButton, RefreshButton];
		double refreshRight = RefreshButton.TransformToVisual(AddressToolbarGrid).TransformPoint(default).X + RefreshButton.ActualWidth;
		double addressLeft = AddressBarBorder.TransformToVisual(AddressToolbarGrid).TransformPoint(default).X;
		bool navigationIconLayout = primaryNavigationButtons.All(static button =>
			button.ActualWidth >= 40 && button.ActualHeight >= 34 &&
			button.Content is PathIcon { ActualWidth: >= 20, ActualHeight: >= 20 }) &&
			addressLeft - refreshRight >= 6;
		bool tabIconLayout = NewTabButton is { ActualWidth: >= 36, ActualHeight: >= 32, Content: PathIcon { Data: not null, ActualWidth: >= 16, ActualHeight: >= 16 } };
		bool breadcrumbHomeIcon = BreadcrumbPanel.Children.OfType<Button>().FirstOrDefault()?.Content is PathIcon { Data: not null, Width: >= 16, Height: >= 16 };
		DependencyObject[] accessibilityRegions =
		[
			Tabs,
			SidebarList,
			AddressBox,
			SearchBox,
			GridItems,
			DetailsItems,
			SecondaryGridItems,
			SecondaryDetailsItems,
			FileOperationProgressBar,
			SidebarDivider,
			SplitDivider,
			StatusTextBlock,
			PreviewSelectionSummary,
		];
		bool accessibilityLabels = accessibilityRegions.All(static element =>
			!string.IsNullOrWhiteSpace(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(element))) &&
			iconCommandButtons.All(static button =>
				!string.IsNullOrWhiteSpace(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button)));
		LocalFileSystemItem[] accessibilitySampleItems = browser.Items.Take(8).ToArray();
		var realizedItemNames = accessibilitySampleItems.Select(static item => item.AccessibilityName).ToHashSet(StringComparer.Ordinal);
		bool accessibleFileItems = realizedItemNames.Count is 0 ||
			CountNamedAutomationElements(PrimaryPaneBorder, realizedItemNames) > 0;
		string folderAutomationType = GetResource("FolderItemAutomationType");
		string fileAutomationType = GetResource("FileItemAutomationType");
		string packageAutomationType = GetResource("PackageItemAutomationType");
		bool itemAccessibility = accessibilitySampleItems.All(item =>
			item.AccessibilityName.Contains(item.Name, StringComparison.Ordinal) &&
			item.AccessibilityName.Contains(item.IsPackage ? packageAutomationType : item.IsDirectory ? folderAutomationType : fileAutomationType, StringComparison.Ordinal) &&
			item.AccessibilityName.Contains(item.ModifiedText, StringComparison.Ordinal));
		var accessibilitySearchSample = new LocalFileSystemItem(
			Path.Combine(Path.GetTempPath(), ".files-accessibility-sample.txt"),
			".files-accessibility-sample.txt",
			isDirectory: false,
			isHidden: true,
			size: 1024,
			modified: DateTimeOffset.UtcNow)
		{
			SearchLocation = Path.GetTempPath(),
		};
		browser.PrepareAccessibilityNames([accessibilitySearchSample]);
		itemAccessibility &= accessibilitySearchSample.AccessibilityName.Contains(GetResource("HiddenItemAutomationState"), StringComparison.Ordinal) &&
			accessibilitySearchSample.AccessibilityName.Contains(accessibilitySearchSample.SizeText, StringComparison.Ordinal) &&
			accessibilitySearchSample.AccessibilityName.Contains(accessibilitySearchSample.SearchLocation!, StringComparison.Ordinal);
		bool keyboardFocusNavigation = KeyboardAccelerators.Count(accelerator =>
			accelerator.Key is Windows.System.VirtualKey.F6) == 2 && CycleFocus(reverse: false);
		bool accessibilityAnnouncements = accessibilityAnnouncementsRegistered &&
			MacOSAccessibilityAnnouncementService.Probe();
		MacOSAccessibilityDisplayOptions nativeAccessibilityOptions = MacOSAccessibilityDisplayService.GetCurrentOptions();
		MacOSAccessibilityDisplayOptions originalAccessibilityOptions = accessibilityDisplayOptions;
		ApplyAccessibilityDisplayOptions(
			MacOSAccessibilityDisplayOptions.IncreaseContrast |
			MacOSAccessibilityDisplayOptions.ReduceTransparency |
			MacOSAccessibilityDisplayOptions.ReduceMotion);
		bool accessibilityDisplay = AddressBarBorder.BorderThickness.Left == 2 &&
			CommandToolbarBorder.BorderThickness.Left == 2 &&
			PrimaryEmptyFolderIcon.Opacity > 0.5 &&
			SidebarDividerLine.Width == 2 && SplitDividerLine.Width == 2 &&
			TitleBarBackground.Opacity == 1 &&
			((App)Application.Current).AccessibilityDisplayOptions == nativeAccessibilityOptions;
		ApplyAccessibilityDisplayOptions(originalAccessibilityOptions);
		double restoredBorderWidth = originalAccessibilityOptions.HasFlag(MacOSAccessibilityDisplayOptions.IncreaseContrast) ? 2 : 1;
		accessibilityDisplay &= AddressBarBorder.BorderThickness.Left == restoredBorderWidth &&
			accessibilityDisplayOptions == originalAccessibilityOptions;
		Button[] sidebarFooterButtons = [OpenFolderButton, ConnectServerButton, ClearRecentButton, SettingsButton];
		bool sidebarFooterIcons = sidebarFooterButtons.All(static button =>
			button.Content is CommandLabel { IconData: not null, Content.Length: > 0 });
		bool emptyStateIcons = PrimaryEmptyFolderIcon.Data is not null && PrimaryNoResultsIcon.Data is not null &&
			SecondaryEmptyFolderIcon.Data is not null && SecondaryNoResultsIcon.Data is not null;
		var itemIconConverter = new Converters.FileSystemItemToIconConverter();
		bool itemFallbackIcons = itemIconConverter.Convert(true, typeof(Microsoft.UI.Xaml.Media.Geometry), null!, string.Empty) is Microsoft.UI.Xaml.Media.Geometry &&
			itemIconConverter.Convert(false, typeof(Microsoft.UI.Xaml.Media.Geometry), null!, string.Empty) is Microsoft.UI.Xaml.Media.Geometry;
		string expectedSplitViewLabel = GetResource(ViewModel.ActiveTab?.IsSplitView is true
			? "CloseSplitViewButtonText"
			: "SplitViewButton/Content");
		bool dynamicCommandLabels = SplitViewButton.Content is CommandLabel { Content: var splitViewLabel } &&
			splitViewLabel == expectedSplitViewLabel &&
			MoreSelectionSubItem.Text == GetResource("MoreSelectionSubItem/Text") &&
			MoreArchiveSubItem.Text == GetResource("MoreArchiveSubItem/Text");
		MenuFlyout itemContextFlyout = (MenuFlyout)Resources["ItemContextFlyout"];
		string[] itemContextActions = EnumerateMenuFlyoutItems(itemContextFlyout.Items)
			.OfType<MenuFlyoutItem>()
			.Select(static item => item.Tag as string)
			.Where(static action => action is not null)
			.Cast<string>()
			.ToArray();
		string[] expectedItemContextActions =
		[
			"Open", "OpenInNewTab", "Preview", "Cut", "Copy", "Rename", "Delete", "Properties",
			"OpenWith", "Reveal", "Terminal", "Duplicate", "CreateSymbolicLink", "CopyPath", "Share",
			"Compress", "Extract", "Favorite", "PermanentDelete",
		];
		bool compactItemContextMenu = itemContextFlyout.Items.Count <= 11 &&
			expectedItemContextActions.All(itemContextActions.Contains);
		string[] expectedBackgroundActions =
		[
			"NewFolder", "NewTextFile", "Paste", "Terminal", "Refresh", "Name", "Modified", "Size",
			"Ascending", "Descending", "Grid", "Details",
		];
		bool backgroundContextMenu =
			expectedBackgroundActions.All(GetMenuActionTags(PrimaryBackgroundContextFlyout).Contains) &&
			expectedBackgroundActions.All(GetMenuActionTags(SecondaryBackgroundContextFlyout).Contains);
		int itemContextTargets = CountItemContextTargets(PrimaryPaneBorder) + CountItemContextTargets(SecondaryPaneBorder);
		bool itemContextHitTargets = browser.Items.Count is 0 || itemContextTargets > 0;
		bool aliasApplicationThumbnail = await RunAliasApplicationThumbnailDiagnosticsAsync();
		bool thumbnailDoubleBuffer = RunThumbnailDoubleBufferDiagnostics();
		bool packageSemantics = await RunPackageSemanticsDiagnosticsAsync();
		FileSortField initialSortField = browser.SortField;
		FileSortDirection initialSortDirection = browser.SortDirection;
		FileSortField diagnosticSortField = initialSortField is FileSortField.Modified ? FileSortField.Size : FileSortField.Modified;
		ApplyDetailsSort(browser, diagnosticSortField, announce: false);
		bool diagnosticIsSecondary = ReferenceEquals(browser, ViewModel.ActiveTab?.SecondaryBrowser);
		PathIcon diagnosticIndicator = (diagnosticIsSecondary, diagnosticSortField) switch
		{
			(true, FileSortField.Modified) => SecondaryModifiedSortIndicator,
			(true, _) => SecondarySizeSortIndicator,
			(false, FileSortField.Modified) => PrimaryModifiedSortIndicator,
			_ => PrimarySizeSortIndicator,
		};
		bool sortHeaderRoundtrip = browser.SortField == diagnosticSortField &&
			diagnosticIndicator is { Data: not null, Visibility: Visibility.Visible };
		Button diagnosticHeaderButton = (diagnosticIsSecondary, diagnosticSortField) switch
		{
			(true, FileSortField.Modified) => SecondaryModifiedHeaderButton,
			(true, _) => SecondarySizeHeaderButton,
			(false, FileSortField.Modified) => PrimaryModifiedHeaderButton,
			_ => PrimarySizeHeaderButton,
		};
		string diagnosticDirectionLabel = GetResource(browser.SortDirection is FileSortDirection.Ascending
			? "SortAscendingItem/Text"
			: "SortDescendingItem/Text");
		bool sortAccessibility = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(diagnosticHeaderButton)
			.Contains(diagnosticDirectionLabel, StringComparison.Ordinal) &&
			new[]
			{
				PrimaryNameHeaderButton,
				PrimaryModifiedHeaderButton,
				PrimarySizeHeaderButton,
				SecondaryNameHeaderButton,
				SecondaryModifiedHeaderButton,
				SecondarySizeHeaderButton,
			}.All(static button => !string.IsNullOrWhiteSpace(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button)));
		browser.SetSort(initialSortField, initialSortDirection);
		UpdateSortHeaderVisuals();
		bool initialGridView = browser.IsGridView;
		SetViewMode(browser, !initialGridView);
		bool viewChanged = browser.IsGridView != initialGridView;
		SetViewMode(browser, initialGridView);
		bool viewModeRoundtrip = viewChanged && browser.IsGridView == initialGridView;
		UpdateSidebarSelection();
		bool sidebarActiveSync = SidebarList.SelectedItem is SidebarLocation activeLocation &&
			!activeLocation.IsHeader && IsSameOrDescendantPath(browser.CurrentPath, activeLocation.Path);
		int initialSidebarLocationCount = ViewModel.Locations.Count;
		SidebarLocation librariesHeader = ViewModel.Locations.First(static location => location is { IsHeader: true, SectionId: "Libraries" });
		bool initialLibrariesExpanded = librariesHeader.IsExpanded;
		SidebarList.SelectedItem = librariesHeader;
		bool sidebarKeyboardActivation = ViewModel.Locations.First(static location => location is { IsHeader: true, SectionId: "Libraries" }).IsExpanded == initialLibrariesExpanded;
		SidebarList.SelectedItem = null;
		ToggleSidebarHeader(librariesHeader);
		bool sidebarSectionChanged = ViewModel.Locations.Count != initialSidebarLocationCount &&
			ViewModel.Locations.First(static location => location is { IsHeader: true, SectionId: "Libraries" }).IsExpanded != initialLibrariesExpanded;
		SidebarLocation changedLibrariesHeader = ViewModel.Locations.First(static location => location is { IsHeader: true, SectionId: "Libraries" });
		sidebarKeyboardActivation &= sidebarSectionChanged && !string.IsNullOrWhiteSpace(changedLibrariesHeader.AccessibilityState);
		ToggleSidebarHeader(changedLibrariesHeader);
		bool sidebarSectionRoundtrip = sidebarSectionChanged && ViewModel.Locations.Count == initialSidebarLocationCount &&
			ViewModel.Locations.First(static location => location is { IsHeader: true, SectionId: "Libraries" }).IsExpanded == initialLibrariesExpanded;
		UpdateSidebarSelection();
		await Task.Delay(750);
		App app = (App)Application.Current;
		MainPage renderedSidebarPage = app.ActivePage ?? this;
		bool unifiedTitleBar = app.WindowsUseUnifiedTitleBars;
		bool titleBarLayout = Math.Abs(TitleBarBackground.ActualHeight - 44) < 0.1 &&
			Tabs.Margin.Left >= 72 && Math.Abs(AddressToolbarGrid.ActualHeight - 48) < 0.1;
		bool sidebarLabels = renderedSidebarPage.ViewModel.Locations.Count > 0 &&
			renderedSidebarPage.ViewModel.Locations.All(static location => !string.IsNullOrWhiteSpace(location.Name));
		var sidebarLabelNames = renderedSidebarPage.ViewModel.Locations.Select(static location => location.Name).ToHashSet(StringComparer.CurrentCulture);
		int renderedSidebarLabels = CountRenderedTextBlocks(renderedSidebarPage.SidebarList, sidebarLabelNames);
		int renderedSidebarIcons = CountRenderedPathIcons(renderedSidebarPage.SidebarList);
		int renderedEjectButtons = CountVisibleEjectButtons(renderedSidebarPage.SidebarList);
		bool sidebarIcons = renderedSidebarIcons == renderedSidebarLabels + renderedEjectButtons && renderedSidebarIcons > 0;
		bool sidebarHeaderSpacing = AreSidebarHeadersSeparated(renderedSidebarPage.SidebarList);
		using System.Text.Json.JsonDocument menuDescription = System.Text.Json.JsonDocument.Parse(MainMenuService.Describe());
		bool nativeMenuInstalled = menuDescription.RootElement.GetProperty("installed").GetBoolean() &&
			menuDescription.RootElement.GetProperty("rootCount").GetInt32() >= 6 &&
			menuDescription.RootElement.GetProperty("commandCount").GetInt32() >= 20;
		MainPage menuTargetPage = app.ActivePage ?? this;
		bool menuInitialSidebarState = menuTargetPage.isSidebarOpen;
		bool menuFirstInvoke = MainMenuService.InvokeForDiagnostics(MacOSMenuCommand.ToggleSidebar);
		await Task.Delay(100);
		bool menuChangedSidebar = menuTargetPage.isSidebarOpen != menuInitialSidebarState;
		bool menuSecondInvoke = MainMenuService.InvokeForDiagnostics(MacOSMenuCommand.ToggleSidebar);
		await Task.Delay(100);
		bool nativeMenuRouting = menuFirstInvoke && menuSecondInvoke && menuChangedSidebar && menuTargetPage.isSidebarOpen == menuInitialSidebarState;
		int initialWindowCount = app.WindowCount;
		WindowSessionState initialWindowSession = app.CaptureWindowSession(this);
		int expectedRestoredWindowCount = 1 + (currentSettings.AdditionalWindowWorkspaces?.Length ?? 0);
		bool windowSessionRestore = initialWindowCount == expectedRestoredWindowCount &&
			initialWindowSession.ActiveWindowIndex == Math.Clamp(currentSettings.ActiveWindowIndex, 0, expectedRestoredWindowCount - 1);
		bool windowPlacementRestore = initialWindowSession.PrimaryWindowPlacement is { Width: >= 640, Height: >= 480 } &&
			initialWindowSession.AdditionalWindowPlacements.Length == initialWindowCount - 1 &&
			initialWindowSession.AdditionalWindowPlacements.All(static placement => placement is { Width: >= 640, Height: >= 480 }) &&
			PlacementMatchesOrWasConstrained(currentSettings.WindowPlacement, initialWindowSession.PrimaryWindowPlacement) &&
			initialWindowSession.AdditionalWindowPlacements.Select((placement, index) =>
				PlacementMatchesOrWasConstrained(
					currentSettings.AdditionalWindowPlacements is { } savedPlacements && index < savedPlacements.Length ? savedPlacements[index] : null,
					placement)).All(static matches => matches);
		bool newWindowInvoked = MainMenuService.InvokeForDiagnostics(MacOSMenuCommand.NewWindow);
		await Task.Delay(500);
		MainPage? secondaryPage = app.ActivePage;
		bool secondaryCreated = newWindowInvoked && app.WindowCount == initialWindowCount + 1 &&
			secondaryPage is not null && !ReferenceEquals(secondaryPage, this) && !secondaryPage.restoresWorkspace;
		bool secondarySidebarState = secondaryPage?.isSidebarOpen ?? false;
		bool secondaryMenuInvoked = secondaryCreated && MainMenuService.InvokeForDiagnostics(MacOSMenuCommand.ToggleSidebar);
		await Task.Delay(100);
		bool activeWindowMenuRouting = secondaryMenuInvoked && secondaryPage is not null &&
			secondaryPage.isSidebarOpen != secondarySidebarState && menuTargetPage.isSidebarOpen == menuInitialSidebarState;
		if (secondaryPage is not null && !ReferenceEquals(secondaryPage, this))
		{
			app.CloseWindow(secondaryPage);
			await Task.Delay(250);
		}
		bool multiWindowRoundtrip = secondaryCreated && activeWindowMenuRouting && app.WindowCount == initialWindowCount &&
			ReferenceEquals(app.ActivePage, menuTargetPage);
		int transferSourceTabCount = ViewModel.Tabs.Count;
		bool closedHistoryBeforeTransfer = ViewModel.CanReopenClosedTab;
		await ViewModel.NewTabAsync(browser.CurrentPath);
		bool tabWindowTransfer = false;
		if (ViewModel.ActiveTab is BrowserTabViewModel transferTab)
		{
			transferTab.Browser.IsGridView = false;
			transferTab.Browser.SetSort(FileSortField.Size, FileSortDirection.Descending);
			await ViewModel.ToggleSplitViewAsync();
			transferTab.SecondaryBrowser!.IsGridView = true;
			transferTab.SecondaryBrowser.SetSort(FileSortField.Modified, FileSortDirection.Ascending);
			transferTab.SplitRatio = 0.65;
			ViewModel.TryCaptureTabState(transferTab, out BrowserTabState expectedTransferredState);
			int transferWindowCount = app.WindowCount;
			bool transferInvoked = await app.MoveTabToNewWindowAsync(this, transferTab);
			MainPage? destinationPage = app.ActivePage;
			tabWindowTransfer = transferInvoked && app.WindowCount == transferWindowCount + 1 &&
				ViewModel.Tabs.Count == transferSourceTabCount && ViewModel.CanReopenClosedTab == closedHistoryBeforeTransfer &&
				destinationPage is not null && !ReferenceEquals(destinationPage, this) &&
				destinationPage.CaptureWorkspaceState() is { ActiveTabIndex: 0, Tabs: [var transferredState] } &&
				transferredState == expectedTransferredState;
			if (destinationPage is not null && !ReferenceEquals(destinationPage, this))
			{
				app.CloseWindow(destinationPage);
				await Task.Delay(250);
			}
			tabWindowTransfer &= app.WindowCount == transferWindowCount && ReferenceEquals(app.ActivePage, this);
		}
		int switchSourceTabCount = ViewModel.Tabs.Count;
		await ViewModel.NewTabAsync(browser.CurrentPath);
		BrowserTabViewModel switchTab = ViewModel.ActiveTab!;
		BrowserTabViewModel firstTab = ViewModel.Tabs[0];
		await Task.Delay(100);
		bool tabChrome = switchTab.IsActive && !firstTab.IsActive && CountVisibleTabCloseButtons(Tabs) == 1;
		firstTab.IsPointerOver = true;
		await Task.Delay(50);
		bool tabCloseAlignment = AreVisibleTabCloseButtonsTrailing(Tabs);
		tabChrome &= firstTab.ShowCloseButton && CountVisibleTabCloseButtons(Tabs) == 2 && tabCloseAlignment;
		firstTab.IsPointerOver = false;
		bool tabSwitching = Tabs.CanDragTabs && Tabs.CanReorderTabs &&
			KeyboardAccelerators.Count(accelerator => accelerator.Key is Windows.System.VirtualKey.Tab &&
				accelerator.Modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) == 2 &&
			KeyboardAccelerators.Count(accelerator => accelerator.Key is >= Windows.System.VirtualKey.Number1 and <= Windows.System.VirtualKey.Number9 &&
				accelerator.Modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) == 9 &&
			SelectNumberedTab(1) && ReferenceEquals(ViewModel.ActiveTab, firstTab) &&
			SelectNumberedTab(9) && ReferenceEquals(ViewModel.ActiveTab, switchTab) &&
			SelectRelativeTab(1, wrap: true) && ReferenceEquals(ViewModel.ActiveTab, ViewModel.Tabs[0]) &&
			SelectRelativeTab(-1, wrap: true) && ReferenceEquals(ViewModel.ActiveTab, switchTab) &&
			!SelectRelativeTab(1, wrap: false) && ReferenceEquals(ViewModel.ActiveTab, switchTab);
		tabSwitching &= ViewModel.DetachTabForTransfer(switchTab) && ViewModel.Tabs.Count == switchSourceTabCount;
		int commandAccelerators = KeyboardAccelerators.Count(accelerator =>
			accelerator.Modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Windows));
		var mergeBaseline = new AppSettings(FavoritePaths: ["baseline-favorite"], RecentPaths: ["baseline-recent"]);
		AppSettings mergedConcurrentSettings = MergeChangedSettings(
			mergeBaseline with { FavoritePaths = ["newer-favorite"], ReverseTabScrollDirection = true },
			mergeBaseline,
			mergeBaseline with { RecentPaths = ["requested-recent"] });
		bool multiWindowSettingsMerge = mergedConcurrentSettings.FavoritePaths is ["newer-favorite"] &&
			mergedConcurrentSettings.RecentPaths is ["requested-recent"] && mergedConcurrentSettings.ReverseTabScrollDirection;
		(bool permanentDeleteRoundtrip, bool metadataEditRoundtrip, bool securityPropertiesRoundtrip, bool openWithRoundtrip, bool recentLocationsRoundtrip, bool duplicateRoundtrip, bool newTabRoundtrip, bool tabLabelsRoundtrip, bool tabHistoryRoundtrip, bool tabManagementRoundtrip, bool symbolicLinkRoundtrip) = await RunFileMutationDiagnosticsAsync();

		using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
		Console.WriteLine(
			$"FILES_MACOS_PERF view={(browser.IsGridView ? "grid" : "details")} " +
			$"items={browser.Items.Count} realized={realizedContainers} selection_roundtrip={selectionRoundtrip} " +
			$"breadcrumbs={BreadcrumbPanel.Children.OfType<Button>().Count()} sidebar_sections={ViewModel.Locations.Count(static location => location.IsHeader)} " +
			$"sidebar_roundtrip={sidebarRoundtrip} sidebar_resize={sidebarResizeRoundtrip} keyboard_resize={keyboardResize} sidebar_active={sidebarActiveSync} sidebar_keyboard={sidebarKeyboardActivation} sidebar_sections_toggle={sidebarSectionRoundtrip} sidebar_labels={sidebarLabels} sidebar_rendered_labels={renderedSidebarLabels} sidebar_icons={sidebarIcons} sidebar_rendered_icons={renderedSidebarIcons} sidebar_eject_buttons={renderedEjectButtons} sidebar_header_spacing={sidebarHeaderSpacing} locale={System.Globalization.CultureInfo.CurrentUICulture.Name} language_override={Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride} home_label={GetResource("SidebarHomeButton/Content")} address_roundtrip={addressRoundtrip} preview_roundtrip={previewRoundtrip} " +
			$"toolbar_breakpoints={toolbarBreakpoints} toolbar_icons={toolbarIcons} navigation_icons={navigationIcons} navigation_icon_layout={navigationIconLayout} tab_icon_layout={tabIconLayout} breadcrumb_home_icon={breadcrumbHomeIcon} sidebar_footer_icons={sidebarFooterIcons} empty_state_icons={emptyStateIcons} item_fallback_icons={itemFallbackIcons} dynamic_labels={dynamicCommandLabels} item_context_compact={compactItemContextMenu} background_context_menu={backgroundContextMenu} item_context_hit_targets={itemContextHitTargets} item_context_targets={itemContextTargets} alias_app_thumbnail={aliasApplicationThumbnail} thumbnail_double_buffer={thumbnailDoubleBuffer} package_semantics={packageSemantics} unified_titlebar={unifiedTitleBar} titlebar_layout={titleBarLayout} empty_folder={browser.IsEmptyFolder} no_results={browser.HasNoSearchResults} " +
			$"sort_headers={sortHeaderRoundtrip} sort_accessibility={sortAccessibility} view_switch={viewModeRoundtrip} accessibility_labels={accessibilityLabels} accessible_items={accessibleFileItems} item_accessibility={itemAccessibility} accessibility_announcements={accessibilityAnnouncements} focus_cycle={keyboardFocusNavigation} accessibility_display={accessibilityDisplay} native_accessibility={(int)nativeAccessibilityOptions} native_menu={nativeMenuInstalled} native_menu_routing={nativeMenuRouting} window_session_restore={windowSessionRestore} window_placement_restore={windowPlacementRestore} restored_windows={initialWindowCount} multi_window={multiWindowRoundtrip} tab_window_transfer={tabWindowTransfer} tab_switching={tabSwitching} tab_chrome={tabChrome} tab_close_alignment={tabCloseAlignment} multi_window_settings_merge={multiWindowSettingsMerge} command_accelerators={commandAccelerators} permanent_delete={permanentDeleteRoundtrip} metadata_edit={metadataEditRoundtrip} security_properties={securityPropertiesRoundtrip} open_with={openWithRoundtrip} recent_locations={recentLocationsRoundtrip} duplicate={duplicateRoundtrip} new_tab={newTabRoundtrip} tab_labels={tabLabelsRoundtrip} tab_history={tabHistoryRoundtrip} tab_management={tabManagementRoundtrip} symbolic_link={symbolicLinkRoundtrip} " +
			$"working_set_mb={process.WorkingSet64 / 1024d / 1024:F1} " +
			$"managed_mb={GC.GetTotalMemory(forceFullCollection: false) / 1024d / 1024:F1}");

		var selectionTimer = System.Diagnostics.Stopwatch.StartNew();
		SelectItems(invert: false);
		int selectedCount = selectedItems.Count;
		SelectItems(invert: true);
		selectionTimer.Stop();
		Console.WriteLine(
			$"FILES_MACOS_SELECTION items={browser.Items.Count} selected={selectedCount} " +
			$"inverted_count={selectedItems.Count} elapsed_ms={selectionTimer.Elapsed.TotalMilliseconds:F1}");
	}

	private async Task<bool> RunPackageSemanticsDiagnosticsAsync()
	{
		string root = Path.Combine(Path.GetTempPath(), $"files-macos-package-diagnostics-{Guid.NewGuid():N}");
		try
		{
			string packagePath = Path.Combine(root, "Sample.app");
			string internalPath = Path.Combine(packagePath, "Contents", "Resources", "package-internal-marker.txt");
			Directory.CreateDirectory(Path.GetDirectoryName(internalPath)!);
			Directory.CreateDirectory(Path.Combine(root, "Ordinary Folder"));
			await File.WriteAllTextAsync(internalPath, "package-internal-marker");
			bool nativePackageProbe =
				MacOSFilePackage.TryGetNativePackageState(packagePath, out bool nativePackage) && nativePackage &&
				MacOSFilePackage.TryGetNativePackageState(Path.Combine(root, "Ordinary Folder"), out bool nativeFolderPackage) && !nativeFolderPackage;

			IReadOnlyList<LocalFileSystemItem> items = await new LocalDirectoryService().GetItemsAsync(root, CancellationToken.None);
			LocalFileSystemItem? package = items.SingleOrDefault(static item => item.Name == "Sample.app");
			if (package is not { IsDirectory: true, IsPackage: true, IsNavigableDirectory: false })
			{
				return false;
			}

			var search = new LocalFileSearchService();
			IReadOnlyList<LocalFileSystemItem> packageResults = await search.SearchAsync(
				root,
				FileSearchQuery.Parse("ext:app"),
				includeHidden: true);
			IReadOnlyList<LocalFileSystemItem> folderResults = await search.SearchAsync(
				root,
				FileSearchQuery.Parse("kind:folder"),
				includeHidden: true);
			IReadOnlyList<LocalFileSystemItem> nestedResults = await search.SearchAsync(
				root,
				FileSearchQuery.Parse("package-internal-marker"),
				includeHidden: true);

			Browser?.PrepareAccessibilityNames([package]);
			var iconConverter = new Converters.FileSystemItemToIconConverter();
			return nativePackageProbe &&
				packageResults is [{ IsPackage: true, Name: "Sample.app" }] &&
				folderResults.Any(static item => item.Name == "Ordinary Folder") &&
				folderResults.All(static item => !item.IsPackage) &&
				nestedResults.Count is 0 &&
				MacOSFilePackage.IsInsidePackage(internalPath, root) &&
				package.AccessibilityName.Contains(GetResource("PackageItemAutomationType"), StringComparison.Ordinal) &&
				iconConverter.Convert(package, typeof(Geometry), null!, string.Empty) is Geometry;
		}
		finally
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
	}

	private async Task<bool> RunAliasApplicationThumbnailDiagnosticsAsync()
	{
		string? applicationPath = new[]
		{
			"/System/Library/CoreServices/Finder.app",
			"/System/Applications/TextEdit.app",
			"/System/Applications/Utilities/Terminal.app",
		}.FirstOrDefault(Directory.Exists);
		if (applicationPath is null)
		{
			return false;
		}

		string root = Path.Combine(Path.GetTempPath(), $"files-macos-alias-thumbnail-{Guid.NewGuid():N}");
		try
		{
			Directory.CreateDirectory(root);
			string aliasPath = Path.Combine(root, "Application Alias");
			if (Interop.MacOSNativeMethods.CreateAliasFile(applicationPath, aliasPath) is 0)
			{
				return false;
			}

			byte[]? applicationThumbnail = await WorkspaceService.GetThumbnailPngAsync(
				applicationPath,
				64,
				64,
				1,
				CancellationToken.None);
			byte[]? aliasThumbnail = await WorkspaceService.GetThumbnailPngAsync(
				aliasPath,
				64,
				64,
				1,
				CancellationToken.None);
			return applicationThumbnail is { Length: > 512 } &&
				aliasThumbnail is { Length: > 512 } &&
				applicationThumbnail.AsSpan().SequenceEqual(aliasThumbnail);
		}
		finally
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
	}

	private static bool RunThumbnailDoubleBufferDiagnostics()
	{
		string path = Path.Combine(Path.GetTempPath(), "files-thumbnail-buffer-item.app");
		DateTimeOffset modified = DateTimeOffset.UtcNow;
		var thumbnail = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
		var existingItem = new LocalFileSystemItem(path, "Buffered.app", true, false, null, modified, isPackage: true)
		{
			Thumbnail = thumbnail,
		};
		var replacementItem = new LocalFileSystemItem(path, "Buffered.app", true, false, null, modified, isPackage: true);
		return DirectoryBrowserViewModel.ReuseExistingThumbnails([existingItem], [replacementItem]) == 1 &&
			ReferenceEquals(replacementItem.Thumbnail, thumbnail);
	}

	private async Task<(bool PermanentDelete, bool MetadataEdit, bool SecurityProperties, bool OpenWith, bool RecentLocations, bool Duplicate, bool NewTab, bool TabLabels, bool TabHistory, bool TabManagement, bool SymbolicLink)> RunFileMutationDiagnosticsAsync()
	{
		string root = Path.Combine(Path.GetTempPath(), $"files-macos-diagnostics-{Guid.NewGuid():N}");
		try
		{
			string deleteRoot = Path.Combine(root, "delete-root");
			string nestedFile = Path.Combine(deleteRoot, "nested", "item.txt");
			Directory.CreateDirectory(Path.GetDirectoryName(nestedFile)!);
			await File.WriteAllTextAsync(nestedFile, "diagnostic");
			await FileOperationService.DeletePermanentlyAsync([deleteRoot, nestedFile]);
			bool permanentDelete = !Directory.Exists(deleteRoot) && !File.Exists(nestedFile);
			string completedPath = Path.Combine(root, "a");
			string missingPath = Path.Combine(root, "missing-item");
			Directory.CreateDirectory(root);
			await File.WriteAllTextAsync(completedPath, "diagnostic");
			try
			{
				await FileOperationService.DeletePermanentlyAsync([completedPath, missingPath]);
				permanentDelete = false;
			}
			catch (PermanentDeletePartialException ex)
			{
				permanentDelete &= ex.CompletedPaths.SequenceEqual([completedPath]) && ex.FailedPath == missingPath && !File.Exists(completedPath);
			}
			string linkTarget = Path.Combine(root, "link-target");
			string directoryLink = Path.Combine(root, "directory-link");
			Directory.CreateDirectory(linkTarget);
			Directory.CreateSymbolicLink(directoryLink, linkTarget);
			await FileOperationService.DeletePermanentlyAsync([directoryLink]);
			permanentDelete &= !Directory.Exists(directoryLink) && Directory.Exists(linkTarget);

			string metadataPath = Path.Combine(root, "metadata.txt");
			await File.WriteAllTextAsync(metadataPath, "diagnostic");
			UnixFileMode expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
			string[] expectedTags = ["Files Diagnostic", "Blue"];
			MacOSFileSecurityInfo baselineSecurity = MacOSFileSecurityService.GetInfo(metadataPath) ??
				throw new IOException("The diagnostic security information isn't available.");
			await FilePropertiesService.UpdateAsync(
				metadataPath,
				new FilePropertyUpdate(
					expectedMode,
					expectedTags,
					IsHidden: true,
					IsLocked: false,
					baselineSecurity.Owner,
					baselineSecurity.Group,
					baselineSecurity.Acl));
			FilePropertiesSummary summary = await FilePropertiesService.GetSummaryAsync([metadataPath]);
			bool metadataEdit = summary.UnixMode == expectedMode && summary.FinderTags is not null &&
				expectedTags.All(tag => summary.FinderTags.Contains(tag, StringComparer.Ordinal));
			IReadOnlyList<LocalFileSystemItem> metadataItems = await new LocalDirectoryService().GetItemsAsync(root, CancellationToken.None);
			bool securityProperties = summary is
			{
				Owner.Length: > 0,
				Group.Length: > 0,
				UserId: not null,
				GroupId: not null,
				AccessControlList: not null,
				IsHidden: true,
				IsLocked: false,
			} && metadataItems.Single(item => item.Path == metadataPath).IsHidden;
			const string diagnosticAcl = "!#acl 1\ngroup:ABCDEFAB-CDEF-ABCD-EFAB-CDEF0000000C:everyone:12:allow:readattr\n";
			await FilePropertiesService.UpdateAsync(
				metadataPath,
				new FilePropertyUpdate(
					expectedMode,
					expectedTags,
					IsHidden: false,
					IsLocked: true,
					baselineSecurity.Owner,
					baselineSecurity.Group,
					diagnosticAcl));
			FilePropertiesSummary aclSummary = await FilePropertiesService.GetSummaryAsync([metadataPath]);
			securityProperties &= aclSummary.AccessControlList?.Contains("allow:readattr", StringComparison.Ordinal) is true;
			try
			{
				await FilePropertiesService.UpdateAsync(
					metadataPath,
					new FilePropertyUpdate(
						(UnixFileMode)int.MaxValue,
						expectedTags,
						IsHidden: false,
						IsLocked: false,
						baselineSecurity.Owner,
						baselineSecurity.Group,
						diagnosticAcl));
				securityProperties = false;
			}
			catch (ArgumentException)
			{
				FilePropertiesSummary rollbackSummary = await FilePropertiesService.GetSummaryAsync([metadataPath]);
				securityProperties &= rollbackSummary.UnixMode == expectedMode && rollbackSummary.IsLocked is true &&
					rollbackSummary.Owner == baselineSecurity.Owner && rollbackSummary.Group == baselineSecurity.Group &&
					rollbackSummary.AccessControlList?.Contains("allow:readattr", StringComparison.Ordinal) is true;
			}
			await FilePropertiesService.UpdateAsync(
				metadataPath,
				new FilePropertyUpdate(
					expectedMode,
					expectedTags,
					IsHidden: false,
					IsLocked: false,
					baselineSecurity.Owner,
					baselineSecurity.Group,
					baselineSecurity.Acl));
			IReadOnlyList<OpenWithApplication> applications = await WorkspaceService.GetOpenWithApplicationsAsync(metadataPath);
			bool openWith = applications.Count > 0 && applications.Any(static application => application.IsDefault) &&
				applications.All(static application => !string.IsNullOrWhiteSpace(application.Name) && Directory.Exists(application.ApplicationPath));
			var recentViewModel = new MainPageViewModel();
			recentViewModel.ApplySettings(new AppSettings(RecentPaths: [root]));
			int expandedCount = recentViewModel.Locations.Count;
			bool recentLocations = recentViewModel.HasRecentLocations && recentViewModel.Locations.Any(location =>
				location is { IsHeader: false, SectionId: "Recent" } && string.Equals(location.Path, root, StringComparison.Ordinal));
			recentViewModel.ToggleSidebarSection("Recent");
			recentLocations &= recentViewModel.HasRecentLocations && recentViewModel.Locations.Count < expandedCount &&
				!recentViewModel.Locations.Any(static location => location is { IsHeader: false, SectionId: "Recent" }) &&
				recentViewModel.Locations.Any(static location => location is { IsHeader: true, SectionId: "Recent", IsExpanded: false });
			var diagnosticSettingsService = new JsonAppSettingsService(Path.Combine(root, "settings.json"));
			await diagnosticSettingsService.SaveAsync(new AppSettings(
				RecentPaths: [root, root],
				CollapsedSidebarSections: ["Recent", "Invalid"],
				Workspace: new([new(new(root))]),
				AdditionalWindowWorkspaces:
				[
					new([new(new(root), SplitRatio: 0.9)]),
					new([]),
				],
				ActiveWindowIndex: 99,
				WindowPlacement: new(200_000, 0, 1, 1),
				AdditionalWindowPlacements: [new(20, 30, 800, 600), new(0, 0, 0, 0)],
				ReverseTabScrollDirection: true,
				SchemaVersion: 10));
			AppSettings restoredSettings = await diagnosticSettingsService.LoadAsync();
			recentLocations &= restoredSettings is
			{
				SchemaVersion: 13,
				RecentPaths: [var restoredRecentPath],
				CollapsedSidebarSections: ["Recent"],
				AdditionalWindowWorkspaces: [{ Tabs: [{ SplitRatio: 0.8 }] }],
				ActiveWindowIndex: 1,
				WindowPlacement: null,
				AdditionalWindowPlacements: [{ Width: 800, Height: 600 }],
				ReverseTabScrollDirection: true,
			} && restoredRecentPath == root;
			string originalGrantPath = Path.Combine(root, "old-grant");
			string restoredGrantPath = Path.Combine(root, "restored-grant");
			AppSettings remappedWindowSettings = AccessGrantSettingsMapper.Apply(
				new AppSettings(
					Workspace: new([new(new(Path.Combine(originalGrantPath, "primary")))]),
					AdditionalWindowWorkspaces: [new([new(new(Path.Combine(originalGrantPath, "secondary")))])]),
				[new RestoredFolderAccessGrant(originalGrantPath, new(restoredGrantPath, "bookmark"))]);
			recentLocations &= remappedWindowSettings.Workspace?.Tabs?[0].Primary.Path == Path.Combine(restoredGrantPath, "primary") &&
				remappedWindowSettings.AdditionalWindowWorkspaces?[0].Tabs?[0].Primary.Path == Path.Combine(restoredGrantPath, "secondary");

			string requestedDuplicatePath = Path.Combine(root, "metadata - Copy.txt");
			await File.WriteAllTextAsync(requestedDuplicatePath, "existing");
			FileTransferResult duplicateResult = await FileTransferService.TransferAsync(new(
				[metadataPath],
				root,
				FileTransferMode.Copy,
				FileConflictResolution.KeepBoth,
				new Dictionary<string, string>(StringComparer.Ordinal) { [metadataPath] = Path.GetFileName(requestedDuplicatePath) }));
			string expectedDuplicatePath = Path.Combine(root, "metadata - Copy (2).txt");
			bool duplicate = duplicateResult.CompletedRoots is [FileTransferRootResult duplicateRoot] &&
				duplicateRoot.DestinationPath == expectedDuplicatePath && File.ReadAllText(expectedDuplicatePath) == "diagnostic" &&
				MacOSFinderTagService.GetTags(expectedDuplicatePath).Contains("Files Diagnostic", StringComparer.Ordinal);
			var duplicateHistory = new FileTransferHistoryEntry(FileTransferMode.Copy, duplicateResult.CompletedRoots);
			var diagnosticHistoryService = new FileTransferHistoryService(
				FileTransferService,
				FileRenameService,
				Path.Combine(root, "undo-artifacts.json"));
			await diagnosticHistoryService.ReplayAsync(duplicateHistory, isUndo: true);
			duplicate &= !File.Exists(expectedDuplicatePath);
			await diagnosticHistoryService.ReplayAsync(duplicateHistory, isUndo: false);
			duplicate &= File.Exists(expectedDuplicatePath) && File.ReadAllText(expectedDuplicatePath) == "diagnostic";

			var tabViewModel = new MainPageViewModel();
			await tabViewModel.InitializeAsync();
			bool tabLabels = tabViewModel.ActiveTab?.Header == GetResource("HomeTabHeader");
			if (Directory.Exists("/Applications"))
			{
				await tabViewModel.NewTabAsync("/Applications");
				tabLabels &= tabViewModel.ActiveTab?.Header == GetResource("ApplicationsFolderDisplayName");
				if (tabViewModel.ActiveTab is BrowserTabViewModel applicationsTab)
				{
					tabViewModel.CloseTab(applicationsTab);
				}
			}
			int initialTabCount = tabViewModel.Tabs.Count;
			await tabViewModel.NewTabAsync(root);
			bool newTab = tabViewModel.Tabs.Count == initialTabCount + 1 &&
				tabViewModel.ActiveTab?.Browser.CurrentPath == root;
			bool tabHistory = false;
			bool tabManagement = false;
			if (tabViewModel.ActiveTab is BrowserTabViewModel diagnosticTab)
			{
				diagnosticTab.Browser.IsGridView = false;
				diagnosticTab.Browser.SetSort(FileSortField.Size, FileSortDirection.Descending);
				await tabViewModel.ToggleSplitViewAsync();
				diagnosticTab.SplitRatio = 0.7;
				BrowserTabState expectedState = tabViewModel.CaptureWorkspaceState().Tabs![^1];
				tabViewModel.CloseTab(diagnosticTab);
				tabHistory = tabViewModel.CanReopenClosedTab && tabViewModel.Tabs.Count == initialTabCount;
				await tabViewModel.ReopenClosedTabAsync();
				if (tabViewModel.ActiveTab is BrowserTabViewModel reopenedTab)
				{
					tabHistory &= tabViewModel.Tabs.Count == initialTabCount + 1 &&
						tabViewModel.CaptureWorkspaceState().Tabs![^1] == expectedState &&
						ReferenceEquals(reopenedTab.ActiveBrowser, reopenedTab.SecondaryBrowser);
					int reopenedIndex = tabViewModel.Tabs.IndexOf(reopenedTab);
					await tabViewModel.DuplicateTabAsync(reopenedTab);
					if (tabViewModel.ActiveTab is BrowserTabViewModel duplicatedTab)
					{
						tabManagement = tabViewModel.Tabs.IndexOf(duplicatedTab) == reopenedIndex + 1 &&
							tabViewModel.CaptureWorkspaceState().Tabs![reopenedIndex + 1] == expectedState;
						tabViewModel.MoveTab(duplicatedTab, 0);
						tabManagement &= ReferenceEquals(tabViewModel.Tabs[0], duplicatedTab) &&
							tabViewModel.CaptureWorkspaceState().ActiveTabIndex == 0;
						tabViewModel.CloseTab(duplicatedTab);
					}
					tabViewModel.CloseTab(reopenedTab);
				}
			}
			newTab &= tabViewModel.Tabs.Count == initialTabCount;
			string adjacentTabPath = Path.Combine(root, "adjacent-tab");
			Directory.CreateDirectory(adjacentTabPath);
			await tabViewModel.NewTabAsync(root);
			BrowserTabViewModel retainedTab = tabViewModel.ActiveTab!;
			await tabViewModel.NewTabAsync(adjacentTabPath);
			tabViewModel.CloseOtherTabs(retainedTab);
			tabHistory &= tabViewModel.Tabs is [var remainingTab] && ReferenceEquals(remainingTab, retainedTab);
			await tabViewModel.ReopenClosedTabAsync();
			tabHistory &= tabViewModel.Tabs.Count == 2 && tabViewModel.ActiveBrowser?.CurrentPath == adjacentTabPath;
			if (tabViewModel.ActiveTab is BrowserTabViewModel rightmostTab)
			{
				tabViewModel.CloseTabsToLeft(rightmostTab);
				tabManagement &= tabViewModel.Tabs is [var remainingRightmost] && ReferenceEquals(remainingRightmost, rightmostTab);
				await tabViewModel.ReopenClosedTabAsync();
				tabManagement &= tabViewModel.Tabs.Count == 2 && tabViewModel.ActiveBrowser?.CurrentPath == root;
				tabViewModel.CloseTabsToRight(rightmostTab);
				tabManagement &= tabViewModel.Tabs is [var remainingLeftmost] && ReferenceEquals(remainingLeftmost, rightmostTab);
				await tabViewModel.ReopenClosedTabAsync();
				tabManagement &= tabViewModel.Tabs.Count == 2 && tabViewModel.ActiveBrowser?.CurrentPath == root;
			}
			foreach (BrowserTabViewModel tab in tabViewModel.Tabs.ToArray())
			{
				tab.Dispose();
			}

			string ephemeralSource = Path.Combine(root, "ephemeral.txt");
			await File.WriteAllTextAsync(ephemeralSource, "temporary");
			string rollbackLinkPath = Path.Combine(root, "rollback - Link.txt");
			bool symbolicLink = false;
			try
			{
				await FileOperationService.CreateSymbolicLinksAsync(
				[
					new(metadataPath, Path.GetFileName(rollbackLinkPath)),
					new(ephemeralSource, "invalid/name"),
				],
				root);
			}
			catch (FileOperationException ex) when (ex.Error is FileOperationError.InvalidCharacters)
			{
				symbolicLink = !File.Exists(rollbackLinkPath) && new FileInfo(rollbackLinkPath).LinkTarget is null;
			}
			IReadOnlyList<CreatedSymbolicLink> links = await FileOperationService.CreateSymbolicLinksAsync(
			[
				new(metadataPath, "metadata - Link.txt"),
				new(linkTarget, "directory - Link"),
				new(ephemeralSource, "broken - Link.txt"),
			],
			root);
			File.Delete(ephemeralSource);
			symbolicLink &= links is [var fileLink, var directoryLinkResult, var brokenLink] &&
				File.ReadAllText(fileLink.Path) == "diagnostic" && Directory.Exists(directoryLinkResult.Path) &&
				new FileInfo(brokenLink.Path).LinkTarget == brokenLink.LinkTarget;
			await FileOperationService.ReplaySymbolicLinksAsync(links, isUndo: true);
			symbolicLink &= links.All(link => !File.Exists(link.Path) && !Directory.Exists(link.Path) && new FileInfo(link.Path).LinkTarget is null) &&
				File.Exists(metadataPath) && Directory.Exists(linkTarget);
			await File.WriteAllTextAsync(links[0].Path, "conflict");
			try
			{
				await FileOperationService.ReplaySymbolicLinksAsync(links, isUndo: false);
				symbolicLink = false;
			}
			catch (FileOperationException ex) when (ex.Error is FileOperationError.AlreadyExists)
			{
				symbolicLink &= !File.Exists(links[1].Path) && !Directory.Exists(links[1].Path);
			}
			File.Delete(links[0].Path);
			await FileOperationService.ReplaySymbolicLinksAsync(links, isUndo: false);
			File.Delete(links[0].Path);
			File.CreateSymbolicLink(links[0].Path, "settings.json");
			try
			{
				await FileOperationService.ReplaySymbolicLinksAsync(links, isUndo: true);
				symbolicLink = false;
			}
			catch (FileOperationException ex) when (ex.Error is FileOperationError.CreatedItemChanged)
			{
				symbolicLink &= File.Exists(links[1].Path) || Directory.Exists(links[1].Path);
			}
			return (permanentDelete, metadataEdit, securityProperties, openWith, recentLocations, duplicate, newTab, tabLabels, tabHistory, tabManagement, symbolicLink);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"FILES_MACOS_MUTATION_ERROR type={ex.GetType().Name} message={ex.Message}");
			return (false, false, false, false, false, false, false, false, false, false, false);
		}
		finally
		{
			try
			{
				if (Directory.Exists(root))
				{
					Directory.Delete(root, recursive: true);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}
	}

	private static bool VerifySelectionRoundtrip(FrameworkElement control, int itemCount)
	{
		if (itemCount is 0)
		{
			return true;
		}

		if (control is ItemsView view)
		{
			view.DeselectAll();
			view.Select(0);
			bool selected = view.IsSelected(0) && view.SelectedItems.Count is 1;
			view.DeselectAll();
			return selected && view.SelectedItems.Count is 0;
		}
		if (control is ListViewBase list)
		{
			list.SelectedIndex = 0;
			bool selected = list.SelectedItems.Count is 1;
			list.SelectedIndex = -1;
			return selected && list.SelectedItems.Count is 0;
		}

		return false;
	}

	private static int CountRenderedTextBlocks(DependencyObject root, IReadOnlySet<string> labels)
	{
		int count = root is TextBlock { ActualWidth: > 0 } textBlock && labels.Contains(textBlock.Text) ? 1 : 0;
		for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
		{
			count += CountRenderedTextBlocks(VisualTreeHelper.GetChild(root, index), labels);
		}
		return count;
	}

	private static int CountRenderedPathIcons(DependencyObject root)
	{
		int count = root is PathIcon { ActualWidth: > 0, Data: not null } ? 1 : 0;
		for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
		{
			count += CountRenderedPathIcons(VisualTreeHelper.GetChild(root, index));
		}
		return count;
	}

	private static int CountVisibleEjectButtons(DependencyObject root)
	{
		int count = root is Button
		{
			Visibility: Visibility.Visible,
			Tag: SidebarLocation { CanEject: true },
		} ? 1 : 0;
		for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
		{
			count += CountVisibleEjectButtons(VisualTreeHelper.GetChild(root, index));
		}
		return count;
	}

	private static bool AreSidebarHeadersSeparated(DependencyObject root)
	{
		bool foundHeader = false;
		bool hasSpacing = true;
		Inspect(root);
		return foundHeader && hasSpacing;

		void Inspect(DependencyObject element)
		{
			if (element is Grid { DataContext: SidebarLocation { IsHeader: true } } grid &&
				FindVisualDescendant<PathIcon>(grid) is PathIcon { ActualWidth: > 0 } icon &&
				FindVisualDescendant<TextBlock>(grid) is TextBlock { ActualWidth: > 0 } label)
			{
				foundHeader = true;
				double iconRight = icon.TransformToVisual(grid).TransformPoint(default).X + icon.ActualWidth;
				double labelLeft = label.TransformToVisual(grid).TransformPoint(default).X;
				hasSpacing &= labelLeft - iconRight >= 6;
			}

			for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
			{
				Inspect(VisualTreeHelper.GetChild(element, index));
			}
		}
	}

	private static int CountNamedAutomationElements(DependencyObject root, IReadOnlySet<string> names)
	{
		string name = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(root);
		int count = names.Contains(name) ? 1 : 0;
		for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
		{
			count += CountNamedAutomationElements(VisualTreeHelper.GetChild(root, index), names);
		}
		return count;
	}

	private static int CountVisibleTabCloseButtons(DependencyObject root)
	{
		int count = root is Button
		{
			Tag: BrowserTabViewModel,
			Visibility: Visibility.Visible,
			ActualWidth: > 0,
			ActualHeight: > 0,
		} ? 1 : 0;
		for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
		{
			count += CountVisibleTabCloseButtons(VisualTreeHelper.GetChild(root, index));
		}
		return count;
	}

	private static bool AreVisibleTabCloseButtonsTrailing(DependencyObject root)
	{
		bool foundButton = false;
		bool isTrailing = true;
		Inspect(root);
		return foundButton && isTrailing;

		void Inspect(DependencyObject element)
		{
			if (element is Button
				{
					Tag: BrowserTabViewModel,
					Visibility: Visibility.Visible,
					ActualWidth: > 0,
				} button)
			{
				DependencyObject? ancestor = VisualTreeHelper.GetParent(button);
				while (ancestor is not null && ancestor is not TabViewItem)
				{
					ancestor = VisualTreeHelper.GetParent(ancestor);
				}

				if (ancestor is TabViewItem { ActualWidth: > 0 } tab)
				{
					foundButton = true;
					double buttonRight = button.TransformToVisual(tab).TransformPoint(default).X + button.ActualWidth;
					double trailingGap = tab.ActualWidth - buttonRight;
					isTrailing &= trailingGap is >= 0 and <= 32;
				}
			}

			for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
			{
				Inspect(VisualTreeHelper.GetChild(element, index));
			}
		}
	}

	private static bool PlacementMatchesOrWasConstrained(WindowPlacementState? requested, WindowPlacementState? actual)
	{
		if (actual is not { Width: >= 640, Height: >= 480 })
		{
			return false;
		}
		if (requested is null)
		{
			return true;
		}

		bool matches = Math.Abs(requested.X - actual.X) < 2 &&
			Math.Abs(requested.Y - actual.Y) < 2 &&
			Math.Abs(requested.Width - actual.Width) < 2 &&
			Math.Abs(requested.Height - actual.Height) < 2 &&
			requested.IsMaximized == actual.IsMaximized;
		bool wasConstrained = actual.Width <= requested.Width && actual.Height <= requested.Height &&
			(Math.Abs(requested.X - actual.X) >= 2 || Math.Abs(requested.Y - actual.Y) >= 2 ||
				Math.Abs(requested.Width - actual.Width) >= 2 || Math.Abs(requested.Height - actual.Height) >= 2);
		return matches || wasConstrained;
	}

	private static int CountRealizedContainers(FrameworkElement control, int itemCount)
	{
		int count = 0;
		if (control is ItemsView itemsView && FindVisualDescendant<ItemsRepeater>(itemsView) is ItemsRepeater repeater)
		{
			for (int index = 0; index < itemCount; index++)
			{
				if (repeater.TryGetElement(index) is not null)
				{
					count++;
				}
			}
		}
		else if (control is ListViewBase list)
		{
			for (int index = 0; index < itemCount; index++)
			{
				if (list.ContainerFromIndex(index) is not null)
				{
					count++;
				}
			}
		}

		return count;
	}

	private static T? FindVisualDescendant<T>(DependencyObject parent)
		where T : DependencyObject
	{
		for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(parent, index);
			if (child is T match)
			{
				return match;
			}
			if (FindVisualDescendant<T>(child) is T descendant)
			{
				return descendant;
			}
		}

		return default;
	}

	private async void MainPage_Unloaded(object sender, RoutedEventArgs e)
	{
		ViewModel.WorkspaceChanged -= ViewModel_WorkspaceChanged;
		if (accessibilityAnnouncementsRegistered)
		{
			StatusTextBlock.UnregisterPropertyChangedCallback(TextBlock.TextProperty, statusTextCallbackToken);
			PreviewSelectionSummary.UnregisterPropertyChangedCallback(TextBlock.TextProperty, previewSelectionCallbackToken);
			accessibilityAnnouncementsRegistered = false;
		}
		accessibilityAnnouncementCancellation?.Cancel();
		accessibilityAnnouncementCancellation?.Dispose();
		accessibilityAnnouncementCancellation = null;
		settingsSaveCancellation?.Cancel();
		settingsSaveCancellation?.Dispose();
		settingsSaveCancellation = null;
		try
		{
			await FileTransferHistoryService.CleanupStagingAsync(undoHistory
				.Concat(redoHistory)
				.Select(static entry => entry.Transfer)
				.OfType<FileTransferHistoryEntry>());
			undoHistory.Clear();
			redoHistory.Clear();
			await FileTransferHistoryService.PersistStagingJournalAsync([]);
			await PersistSettingsAsync(currentSettings);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
		finally
		{
			AccessGrantService.Dispose();
		}
	}

	void IMacOSMenuCommandTarget.ExecuteMenuCommand(MacOSMenuCommand command)
	{
		_ = ExecuteMenuCommandAsync(command);
	}

	bool IMacOSMenuCommandTarget.CanExecuteMenuCommand(MacOSMenuCommand command)
	{
		bool isIdle = fileTransferCancellation is null && !isHistoryOperationRunning && !isConnectingServer;
		return command switch
		{
			MacOSMenuCommand.NewWindow => true,
			MacOSMenuCommand.CloseWindow => true,
			MacOSMenuCommand.CloseTab => isIdle && ViewModel.Tabs.Count > 1,
			MacOSMenuCommand.ReopenClosedTab => isIdle && ViewModel.CanReopenClosedTab,
			MacOSMenuCommand.CloseOtherTabs => isIdle && ViewModel.Tabs.Count > 1,
			MacOSMenuCommand.DuplicateTab => isIdle && ViewModel.ActiveTab is not null,
			MacOSMenuCommand.CloseTabsToLeft => isIdle && ViewModel.ActiveTab is BrowserTabViewModel leftTab && ViewModel.Tabs.IndexOf(leftTab) > 0,
			MacOSMenuCommand.CloseTabsToRight => isIdle && ViewModel.ActiveTab is BrowserTabViewModel rightTab && ViewModel.Tabs.IndexOf(rightTab) < ViewModel.Tabs.Count - 1,
			MacOSMenuCommand.MoveTabToNewWindow => isIdle && ViewModel.Tabs.Count > 1,
			MacOSMenuCommand.NextTab or MacOSMenuCommand.PreviousTab => isIdle && ViewModel.Tabs.Count > 1,
			MacOSMenuCommand.Properties or MacOSMenuCommand.MoveToTrash or MacOSMenuCommand.DeletePermanently or MacOSMenuCommand.Rename or
				MacOSMenuCommand.Cut or MacOSMenuCommand.Copy or MacOSMenuCommand.CopyPath => isIdle && selectedItems.Count > 0,
			MacOSMenuCommand.OpenWith => isIdle && selectedItems is [LocalFileSystemItem { IsNavigableDirectory: false }],
			MacOSMenuCommand.OpenInNewTab => isIdle && selectedItems is [LocalFileSystemItem { IsNavigableDirectory: true }],
			MacOSMenuCommand.Duplicate => isIdle && CanDuplicateSelection(),
			MacOSMenuCommand.CreateSymbolicLink => isIdle && selectedItems.Count > 0,
			MacOSMenuCommand.Paste => PasteButton.IsEnabled,
			MacOSMenuCommand.Undo => isIdle && undoHistory.Count > 0,
			MacOSMenuCommand.Redo => isIdle && redoHistory.Count > 0,
			MacOSMenuCommand.Back => isIdle && Browser?.CanGoBack is true,
			MacOSMenuCommand.Forward => isIdle && Browser?.CanGoForward is true,
			MacOSMenuCommand.TogglePreview => isIdle && selectedItems.Count <= 1,
			_ => isIdle,
		};
	}

	private async Task ExecuteMenuCommandAsync(MacOSMenuCommand command)
	{
		var args = new RoutedEventArgs();
		switch (command)
		{
			case MacOSMenuCommand.NewWindow:
				((App)Application.Current).CreateWindow();
				break;
			case MacOSMenuCommand.CloseWindow:
				((App)Application.Current).CloseWindow(this);
				break;
			case MacOSMenuCommand.Settings:
				SettingsButton_Click(SettingsButton, args);
				break;
			case MacOSMenuCommand.NewTab:
				await ViewModel.NewTabAsync();
				break;
			case MacOSMenuCommand.NewFolder:
				NewButton_Click(this, args);
				break;
			case MacOSMenuCommand.CloseTab when ViewModel.ActiveTab is BrowserTabViewModel tab:
				ViewModel.CloseTab(tab);
				break;
			case MacOSMenuCommand.ReopenClosedTab:
				await ReopenClosedTabAsync();
				break;
			case MacOSMenuCommand.CloseOtherTabs when ViewModel.ActiveTab is BrowserTabViewModel retainedTab:
				ViewModel.CloseOtherTabs(retainedTab);
				UpdateCommandStates();
				break;
			case MacOSMenuCommand.DuplicateTab when ViewModel.ActiveTab is BrowserTabViewModel duplicateTab:
				await DuplicateTabAsync(duplicateTab);
				break;
			case MacOSMenuCommand.CloseTabsToLeft when ViewModel.ActiveTab is BrowserTabViewModel leftTab:
				ViewModel.CloseTabsToLeft(leftTab);
				UpdateCommandStates();
				break;
			case MacOSMenuCommand.CloseTabsToRight when ViewModel.ActiveTab is BrowserTabViewModel rightTab:
				ViewModel.CloseTabsToRight(rightTab);
				UpdateCommandStates();
				break;
			case MacOSMenuCommand.MoveTabToNewWindow when ViewModel.ActiveTab is BrowserTabViewModel movedTab:
				await MoveTabToNewWindowAsync(movedTab);
				break;
			case MacOSMenuCommand.NextTab:
				SelectRelativeTab(1, wrap: true);
				break;
			case MacOSMenuCommand.PreviousTab:
				SelectRelativeTab(-1, wrap: true);
				break;
			case MacOSMenuCommand.Properties:
				PropertiesButton_Click(PropertiesButton, args);
				break;
			case MacOSMenuCommand.MoveToTrash:
				DeleteButton_Click(DeleteButton, args);
				break;
			case MacOSMenuCommand.DeletePermanently:
				await DeletePermanentlyAsync();
				break;
			case MacOSMenuCommand.OpenWith:
				await ShowOpenWithDialogAsync();
				break;
			case MacOSMenuCommand.OpenInNewTab:
				await OpenInNewTabAsync();
				break;
			case MacOSMenuCommand.Duplicate:
				await DuplicateSelectedItemsAsync();
				break;
			case MacOSMenuCommand.CreateSymbolicLink:
				await CreateSymbolicLinksAsync();
				break;
			case MacOSMenuCommand.Rename:
				RenameButton_Click(RenameButton, args);
				break;
			case MacOSMenuCommand.Undo:
				await ReplayHistoryAsync(isUndo: true);
				break;
			case MacOSMenuCommand.Redo:
				await ReplayHistoryAsync(isUndo: false);
				break;
			case MacOSMenuCommand.Cut:
				await SetFileClipboardAsync(FileTransferMode.Move);
				break;
			case MacOSMenuCommand.Copy:
				await SetFileClipboardAsync(FileTransferMode.Copy);
				break;
			case MacOSMenuCommand.Paste:
				await PasteAsync();
				break;
			case MacOSMenuCommand.SelectAll:
				SelectItems(invert: false);
				break;
			case MacOSMenuCommand.CopyPath:
				CopySelectedPaths();
				break;
			case MacOSMenuCommand.Search:
				SearchBox.Focus(FocusState.Keyboard);
				SearchBox.SelectAll();
				break;
			case MacOSMenuCommand.EditAddress:
				BeginAddressEdit();
				break;
			case MacOSMenuCommand.GridView when Browser is DirectoryBrowserViewModel gridBrowser:
				SetViewMode(gridBrowser, isGridView: true);
				break;
			case MacOSMenuCommand.DetailsView when Browser is DirectoryBrowserViewModel detailsBrowser:
				SetViewMode(detailsBrowser, isGridView: false);
				break;
			case MacOSMenuCommand.TogglePreview:
				SetPreviewPaneOpen(!isPreviewPaneOpen);
				break;
			case MacOSMenuCommand.ToggleSidebar:
				ToggleSidebarButton_Click(ToggleSidebarButton, args);
				break;
			case MacOSMenuCommand.Back when Browser is not null:
				await Browser.GoBackAsync();
				break;
			case MacOSMenuCommand.Forward when Browser is not null:
				await Browser.GoForwardAsync();
				break;
			case MacOSMenuCommand.Up when Browser is not null:
				await Browser.GoUpAsync();
				break;
			case MacOSMenuCommand.Home when Browser is not null:
				await Browser.NavigateHomeAsync();
				break;
			case MacOSMenuCommand.OpenFolder:
				OpenFolderButton_Click(OpenFolderButton, args);
				break;
			case MacOSMenuCommand.ConnectServer:
				ConnectServerButton_Click(ConnectServerButton, args);
				break;
			case MacOSMenuCommand.OpenTerminal:
				TerminalButton_Click(TerminalButton, args);
				break;
		}
	}

	private async void BackButton_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is not null)
		{
			await Browser.GoBackAsync();
		}
	}

	private async void Page_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		switch (e.GetCurrentPoint(this).Properties.PointerUpdateKind)
		{
			case Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed when Browser is not null:
				e.Handled = true;
				await Browser.GoBackAsync();
				break;
			case Microsoft.UI.Input.PointerUpdateKind.XButton2Pressed when Browser is not null:
				e.Handled = true;
				await Browser.GoForwardAsync();
				break;
		}
	}

	internal async void HandleAuxiliaryMouseButton(int buttonNumber)
	{
		switch (buttonNumber)
		{
			case 2 when fileTransferCancellation is null:
				BrowserTabViewModel? hoveredTab = ViewModel.Tabs.FirstOrDefault(static tab => tab.IsPointerOver);
				if (hoveredTab is not null)
				{
					ViewModel.CloseTab(hoveredTab);
				}
				break;
			case 3 when Browser?.CanGoBack is true:
				await Browser.GoBackAsync();
				break;
			case 4 when Browser?.CanGoForward is true:
				await Browser.GoForwardAsync();
				break;
		}
	}

	internal bool HandleNativeScrollWheel(double deltaX, double deltaY, bool hasPreciseDeltas)
	{
		BrowserTabViewModel? activeTab = ViewModel.ActiveTab;
		(DirectoryBrowserViewModel? browser, ItemsView? view) = isPointerOverPrimaryGrid && activeTab?.Browser is { IsGridView: true } primaryBrowser
			? (primaryBrowser, GridItems)
			: isPointerOverSecondaryGrid && activeTab?.SecondaryBrowser is { IsGridView: true } secondaryBrowser
				? (secondaryBrowser, SecondaryGridItems)
				: (null, null);
		if (browser is null || view is null)
		{
			return false;
		}

		ScrollView? scrollView = view.ScrollView ?? FindVisualDescendant<ScrollView>(view);
		double sourceDelta = Math.Abs(deltaY) >= Math.Abs(deltaX) ? deltaY : deltaX;
		if (scrollView is null || sourceDelta is 0 || scrollView.ExtentHeight <= scrollView.ViewportHeight)
		{
			return false;
		}

		ActivateBrowser(browser, view);
		double distance = -sourceDelta * (hasPreciseDeltas ? 1 : 40);
		double maximumOffset = Math.Max(0, scrollView.ExtentHeight - scrollView.ViewportHeight);
		double targetOffset = Math.Clamp(scrollView.VerticalOffset + distance, 0, maximumOffset);
		scrollView.ScrollTo(
			scrollView.HorizontalOffset,
			targetOffset,
			new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
		return true;
	}

	private void SetGridPointerState(bool isPrimary, bool isPointerOver)
	{
		if (isPrimary)
		{
			isPointerOverPrimaryGrid = isPointerOver;
		}
		else
		{
			isPointerOverSecondaryGrid = isPointerOver;
		}
		MacOSAuxiliaryMouseService.SetGridScrollCapture(isPointerOverPrimaryGrid || isPointerOverSecondaryGrid);
	}

	private async void UpButton_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is not null)
		{
			await Browser.GoUpAsync();
		}
	}

	private async void RefreshButton_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is not null)
		{
			await Browser.RefreshAsync();
		}
	}

	private async void AddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key is VirtualKey.Enter && Browser is not null)
		{
			e.Handled = true;
			await Browser.NavigateAsync(AddressBox.Text);
			EndAddressEdit();
		}
		else if (e.Key is VirtualKey.Escape)
		{
			e.Handled = true;
			EndAddressEdit();
		}
	}

	private void AddressAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		args.Handled = true;
		BeginAddressEdit();
	}

	private void AddressBarBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		if (isEditingAddress)
		{
			return;
		}

		DependencyObject? current = e.OriginalSource as DependencyObject;
		while (current is not null && !ReferenceEquals(current, AddressBarBorder))
		{
			if (current is Button)
			{
				return;
			}
			current = VisualTreeHelper.GetParent(current);
		}
		BeginAddressEdit();
		e.Handled = true;
	}

	private void AddressBox_LostFocus(object sender, RoutedEventArgs e)
	{
		EndAddressEdit();
	}

	private async void BreadcrumbButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: string path } && Browser is DirectoryBrowserViewModel browser)
		{
			await browser.NavigateAsync(path);
		}
	}

	private void BeginAddressEdit()
	{
		if (Browser is null)
		{
			return;
		}
		isEditingAddress = true;
		AddressBox.Text = Browser.CurrentPath;
		BreadcrumbScrollViewer.Visibility = Visibility.Collapsed;
		AddressBox.Visibility = Visibility.Visible;
		AddressBox.Focus(FocusState.Programmatic);
		AddressBox.SelectAll();
	}

	private void EndAddressEdit()
	{
		if (!isEditingAddress)
		{
			return;
		}
		isEditingAddress = false;
		AddressBox.Visibility = Visibility.Collapsed;
		BreadcrumbScrollViewer.Visibility = Visibility.Visible;
		UpdateAddressBar();
	}

	private void UpdateAddressBar()
	{
		if (isEditingAddress || Browser is not DirectoryBrowserViewModel browser)
		{
			return;
		}

		PathBreadcrumbItem[] items = PathBreadcrumbBuilder.Build(
			browser.CurrentPath,
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			GetResource("SidebarHomeButton/Content"));
		BreadcrumbPanel.Children.Clear();
		for (int index = 0; index < items.Length; index++)
		{
			PathBreadcrumbItem item = items[index];
			if (index > 0)
			{
				BreadcrumbPanel.Children.Add(new TextBlock
				{
					Text = "›",
					Margin = new Thickness(1, 0),
					VerticalAlignment = VerticalAlignment.Center,
					Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
				});
			}

			var button = new Button
			{
				Content = item.IsHome
					? new PathIcon
					{
						Width = 16,
						Height = 16,
						Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), HomeBreadcrumbIconData),
					}
					: item.Title,
				Tag = item.Path,
				Style = (Style)Application.Current.Resources["BreadcrumbButtonStyle"],
			};
			Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, item.Title);
			ToolTipService.SetToolTip(button, item.Title);
			button.Click += BreadcrumbButton_Click;
			BreadcrumbPanel.Children.Add(button);
		}

		AddressBox.Text = browser.CurrentPath;
		DispatcherQueue.TryEnqueue(() => BreadcrumbScrollViewer.ChangeView(BreadcrumbScrollViewer.ScrollableWidth, null, null, true));
	}

	private async void SidebarList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (isUpdatingSidebarSelection)
		{
			return;
		}

		if (SidebarList.SelectedItem is SidebarLocation { IsHeader: true } header)
		{
			return;
		}
		if (SidebarList.SelectedItem is SidebarLocation location && Browser is not null)
		{
			if (!string.IsNullOrWhiteSpace(location.ExternalUrl))
			{
				try
				{
					await WorkspaceService.OpenUrlAsync(location.ExternalUrl);
				}
				catch (IOException)
				{
					await ShowErrorAsync(GetResource("OpenItemErrorMessage"));
				}
			}
			else if (location.IsNetworkServer)
			{
				await ConnectToServerAsync(location.Path);
			}
			else
			{
				await Browser.NavigateAsync(location.Path);
			}
		}
	}

	private void SidebarList_ItemClick(object sender, ItemClickEventArgs e)
	{
		if (e.ClickedItem is SidebarLocation { IsHeader: true } header)
		{
			ToggleSidebarHeader(header);
		}
	}

	private async void EjectVolumeButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Button { Tag: SidebarLocation { CanEject: true } volume })
		{
			return;
		}
		var dialog = new ContentDialog
		{
			Title = GetResource("EjectVolumeDialogTitle"),
			Content = string.Format(GetResource("EjectVolumeDialogMessageFormat"), volume.Name),
			PrimaryButtonText = GetResource("EjectVolumeButtonText"),
			CloseButtonText = GetResource("CancelButtonText"),
			DefaultButton = ContentDialogButton.Close,
			XamlRoot = XamlRoot,
		};
		if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
		{
			return;
		}

		try
		{
			if (Browser is DirectoryBrowserViewModel browser && IsSameOrDescendantPath(browser.CurrentPath, volume.Path))
			{
				await browser.NavigateHomeAsync();
			}
			await WorkspaceService.EjectVolumeAsync(volume.Path);
			ViewModel.RefreshLocations();
			if (Browser is not null)
			{
				Browser.StatusText = string.Format(GetResource("EjectVolumeCompletedFormat"), volume.Name);
			}
		}
		catch (IOException)
		{
			await ShowErrorAsync(GetResource("EjectVolumeErrorMessage"));
		}
	}

	private void SidebarList_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key is VirtualKey.Enter or VirtualKey.Space && SidebarList.SelectedItem is SidebarLocation { IsHeader: true } header)
		{
			ToggleSidebarHeader(header);
			e.Handled = true;
		}
	}

	private void ToggleSidebarHeader(SidebarLocation header)
	{
		isUpdatingSidebarSelection = true;
		try
		{
			string[] collapsedSections = ViewModel.ToggleSidebarSection(header.SectionId);
			currentSettings = currentSettings with { CollapsedSidebarSections = collapsedSections };
			SidebarList.SelectedItem = null;
		}
		finally
		{
			isUpdatingSidebarSelection = false;
		}
		UpdateSidebarSelection();
		ScheduleWorkspaceSave();
	}

	private void UpdateSidebarSelection()
	{
		if (Browser is not DirectoryBrowserViewModel browser)
		{
			return;
		}

		SidebarLocation? location = ViewModel.Locations
			.Where(static item => !item.IsHeader && !item.IsNetworkServer && !string.IsNullOrEmpty(item.Path))
			.Where(item => IsSameOrDescendantPath(browser.CurrentPath, item.Path))
			.OrderByDescending(static item => item.Path.Length)
			.FirstOrDefault();
		isUpdatingSidebarSelection = true;
		try
		{
			SidebarList.SelectedItem = location;
			if (location is not null)
			{
				SidebarList.ScrollIntoView(location);
			}
		}
		finally
		{
			isUpdatingSidebarSelection = false;
		}
	}

	private static bool IsSameOrDescendantPath(string path, string candidateParent)
	{
		string parent = Path.TrimEndingDirectorySeparator(candidateParent);
		if (parent.Length is 0)
		{
			parent = Path.DirectorySeparatorChar.ToString();
		}
		string current = Path.TrimEndingDirectorySeparator(path);
		return string.Equals(current, parent, StringComparison.Ordinal) ||
			(parent == Path.DirectorySeparatorChar.ToString()
				? current.StartsWith(parent, StringComparison.Ordinal)
				: current.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal));
	}

	private void ToggleSidebarButton_Click(object sender, RoutedEventArgs e)
	{
		isSidebarOpen = !isSidebarOpen;
		currentSettings = currentSettings with { IsSidebarOpen = isSidebarOpen };
		UpdateSidebarVisuals();
		ScheduleWorkspaceSave();
	}

	private void UpdateSidebarVisuals()
	{
		SidebarColumn.Width = new GridLength(isSidebarOpen ? sidebarWidth : 0);
		SidebarDividerColumn.Width = new GridLength(isSidebarOpen ? SidebarDividerWidth : 0);
		SidebarBorder.Visibility = isSidebarOpen ? Visibility.Visible : Visibility.Collapsed;
		SidebarDivider.Visibility = isSidebarOpen ? Visibility.Visible : Visibility.Collapsed;
		ToggleSidebarButton.Opacity = isSidebarOpen ? 1 : 0.72;
	}

	private void SidebarDivider_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		if (!isSidebarOpen)
		{
			return;
		}

		isResizingSidebar = SidebarDivider.CapturePointer(e.Pointer);
		e.Handled = isResizingSidebar;
	}

	private void SidebarDivider_PointerMoved(object sender, PointerRoutedEventArgs e)
	{
		if (!isResizingSidebar)
		{
			return;
		}

		SetSidebarWidth(e.GetCurrentPoint(WorkspaceGrid).Position.X, persist: false);
		e.Handled = true;
	}

	private void SidebarDivider_PointerReleased(object sender, PointerRoutedEventArgs e)
	{
		if (!isResizingSidebar)
		{
			return;
		}

		isResizingSidebar = false;
		SidebarDivider.ReleasePointerCapture(e.Pointer);
		currentSettings = currentSettings with { SidebarWidth = sidebarWidth };
		ScheduleWorkspaceSave();
		e.Handled = true;
	}

	private void SidebarDivider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
	{
		isResizingSidebar = false;
	}

	private void SidebarDivider_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		double requestedWidth = e.Key switch
		{
			VirtualKey.Left => sidebarWidth - KeyboardResizeStep,
			VirtualKey.Right => sidebarWidth + KeyboardResizeStep,
			VirtualKey.Home => MinimumSidebarWidth,
			VirtualKey.End => MaximumSidebarWidth,
			_ => double.NaN,
		};
		if (!double.IsNaN(requestedWidth))
		{
			SetSidebarWidth(requestedWidth, persist: true);
			e.Handled = true;
		}
	}

	private void SetSidebarWidth(double requestedWidth, bool persist)
	{
		sidebarWidth = Math.Clamp(requestedWidth, MinimumSidebarWidth, MaximumSidebarWidth);
		SidebarColumn.Width = new GridLength(sidebarWidth);
		if (persist)
		{
			currentSettings = currentSettings with { SidebarWidth = sidebarWidth };
			ScheduleWorkspaceSave();
		}
	}

	private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			FolderAccessGrant? grant = await AccessGrantService.PickFolderAsync(
				Browser?.CurrentPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
			if (grant is null)
			{
				return;
			}

			FolderAccessGrant[] grants = (currentSettings.AccessGrants ?? [])
				.Where(existing => !string.Equals(existing.Path, grant.Path, StringComparison.Ordinal))
				.Append(grant)
				.OrderBy(static existing => existing.Path, StringComparer.CurrentCultureIgnoreCase)
				.ToArray();
			await PersistSettingsAsync(currentSettings with { AccessGrants = grants });
			if (Browser is DirectoryBrowserViewModel browser)
			{
				await browser.NavigateAsync(grant.Path);
			}
			else
			{
				await ViewModel.NewTabAsync(grant.Path);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowErrorAsync(string.IsNullOrWhiteSpace(ex.Message) ? GetResource("OpenFolderErrorMessage") : ex.Message);
		}
	}

	private async void Items_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
	{
		if (sender is ListViewBase { SelectedItem: LocalFileSystemItem item } list && GetBrowserForItemsControl(list) is DirectoryBrowserViewModel browser)
		{
			ActivateBrowser(browser, list);
			await OpenItemAsync(browser, item);
		}
	}

	private async void GridItems_ItemInvoked(ItemsView sender, ItemsViewItemInvokedEventArgs e)
	{
		if (e.InvokedItem is LocalFileSystemItem item && GetBrowserForItemsControl(sender) is DirectoryBrowserViewModel browser)
		{
			ActivateBrowser(browser, sender);
			await OpenItemAsync(browser, item);
		}
	}

	private async Task OpenItemAsync(DirectoryBrowserViewModel browser, LocalFileSystemItem item)
	{
		try
		{
			if (item.IsNavigableDirectory)
			{
				await browser.NavigateAsync(item.Path);
			}
			else if (IsZipArchive(item))
			{
				await ExtractZipArchiveAsync(browser, item);
			}
			else
			{
				await WorkspaceService.OpenAsync(item.Path);
			}
		}
		catch (IOException ex)
		{
			await ShowErrorAsync(string.IsNullOrEmpty(ex.Message) ? GetResource("OpenItemErrorMessage") : ex.Message);
		}
	}

	private void ClearRecentButton_Click(object sender, RoutedEventArgs e)
	{
		currentSettings = currentSettings with { RecentPaths = [] };
		ViewModel.ApplySettings(currentSettings);
		UpdateSidebarSelection();
		ScheduleWorkspaceSave();
	}

	private async Task ShowOpenWithDialogAsync()
	{
		if (selectedItems is not [LocalFileSystemItem { IsNavigableDirectory: false } item] || fileTransferCancellation is not null)
		{
			return;
		}

		try
		{
			IReadOnlyList<OpenWithApplication> applications = await WorkspaceService.GetOpenWithApplicationsAsync(item.Path);
			var choices = new ComboBox
			{
				HorizontalAlignment = HorizontalAlignment.Stretch,
				MinWidth = 360,
			};
			foreach (OpenWithApplication application in applications)
			{
				choices.Items.Add(new ComboBoxItem
				{
					Content = application.IsDefault
						? $"{application.Name} — {GetResource("DefaultApplicationLabel")}"
						: application.Name,
					Tag = application,
				});
			}
			if (choices.Items.Count > 0)
			{
				choices.SelectedIndex = 0;
			}

			var content = new StackPanel { Spacing = 12, MinWidth = 400 };
			content.Children.Add(new TextBlock
			{
				Text = string.Format(GetResource("OpenWithDescriptionFormat"), item.Name),
				TextWrapping = TextWrapping.Wrap,
			});
			content.Children.Add(choices);
			var dialog = new ContentDialog
			{
				Title = GetResource("OpenWithDialogTitle"),
				Content = content,
				PrimaryButtonText = GetResource("OpenButtonText"),
				SecondaryButtonText = GetResource("ChooseApplicationButtonText"),
				CloseButtonText = GetResource("CancelButtonText"),
				DefaultButton = choices.Items.Count > 0 ? ContentDialogButton.Primary : ContentDialogButton.Secondary,
				IsPrimaryButtonEnabled = choices.Items.Count > 0,
				XamlRoot = XamlRoot,
			};

			ContentDialogResult result = await dialog.ShowAsync();
			if (result is ContentDialogResult.Primary && choices.SelectedItem is ComboBoxItem { Tag: OpenWithApplication selectedApplication })
			{
				await WorkspaceService.OpenWithAsync(item.Path, selectedApplication.ApplicationPath);
			}
			else if (result is ContentDialogResult.Secondary)
			{
				string? applicationPath = await WorkspaceService.PickApplicationAsync();
				if (applicationPath is not null)
				{
					await WorkspaceService.OpenWithAsync(item.Path, applicationPath);
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowErrorAsync(string.IsNullOrWhiteSpace(ex.Message) ? GetResource("OpenWithErrorMessage") : ex.Message);
		}
	}

	private async Task OpenInNewTabAsync()
	{
		if (selectedItems is [LocalFileSystemItem { IsNavigableDirectory: true } item] && fileTransferCancellation is null)
		{
			await ViewModel.NewTabAsync(item.Path);
		}
	}

	private async void OpenInNewTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (!IsTextInputFocused() && selectedItems is [LocalFileSystemItem { IsNavigableDirectory: true }] && fileTransferCancellation is null)
		{
			args.Handled = true;
			await OpenInNewTabAsync();
		}
	}

	private async void DuplicateAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (!IsTextInputFocused() && CanDuplicateSelection() && fileTransferCancellation is null)
		{
			args.Handled = true;
			await DuplicateSelectedItemsAsync();
		}
	}

	private async void CreateSymbolicLinkAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (!IsTextInputFocused() && selectedItems.Count > 0 && fileTransferCancellation is null)
		{
			args.Handled = true;
			await CreateSymbolicLinksAsync();
		}
	}

	private void NewWindowAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		args.Handled = true;
		((App)Application.Current).CreateWindow();
	}

	private void CloseWindowAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		args.Handled = true;
		((App)Application.Current).CloseWindow(this);
	}

	private async void ReopenClosedTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (!IsTextInputFocused() && ViewModel.CanReopenClosedTab && fileTransferCancellation is null)
		{
			args.Handled = true;
			await ReopenClosedTabAsync();
		}
	}

	private async void DuplicateTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (!IsTextInputFocused() && ViewModel.ActiveTab is BrowserTabViewModel tab && fileTransferCancellation is null)
		{
			args.Handled = true;
			await DuplicateTabAsync(tab);
		}
	}

	private void NextTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		args.Handled = SelectRelativeTab(1, wrap: true);
	}

	private void PreviousTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		args.Handled = SelectRelativeTab(-1, wrap: true);
	}

	private void CycleFocusAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		args.Handled = CycleFocus(sender.Modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift));
	}

	private bool CycleFocus(bool reverse)
	{
		if (Browser is not DirectoryBrowserViewModel browser || XamlRoot is null)
		{
			return false;
		}

		bool isSecondary = ReferenceEquals(browser, ViewModel.ActiveTab?.SecondaryBrowser);
		Control itemView = (isSecondary, browser.IsGridView) switch
		{
			(true, true) => SecondaryGridItems,
			(true, false) => SecondaryDetailsItems,
			(false, true) => GridItems,
			_ => DetailsItems,
		};
		var targets = new List<Control> { Tabs };
		if (isSidebarOpen)
		{
			targets.Add(SidebarList);
			targets.Add(SidebarDivider);
		}
		if (BreadcrumbPanel.Children.OfType<Button>().FirstOrDefault() is Button breadcrumb)
		{
			targets.Add(breadcrumb);
		}
		targets.Add(SearchBox);
		targets.Add(NewCommandButton);
		targets.Add(itemView);
		if (ViewModel.ActiveTab?.IsSplitView is true)
		{
			targets.Add(SplitDivider);
		}
		if (isPreviewPaneOpen)
		{
			targets.Add(PreviewPaneQuickLookButton);
		}
		targets.Add(GridViewStatusButton);
		targets.RemoveAll(static control => !control.IsEnabled || !IsEffectivelyVisible(control));
		if (targets.Count is 0)
		{
			return false;
		}

		DependencyObject? focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
		int currentIndex = targets.FindIndex(target => IsDescendantOf(focused, target));
		int targetIndex = reverse
			? (currentIndex <= 0 ? targets.Count - 1 : currentIndex - 1)
			: (currentIndex + 1) % targets.Count;
		return targets[targetIndex].Focus(FocusState.Keyboard);
	}

	private static bool IsEffectivelyVisible(DependencyObject element)
	{
		for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
		{
			if (current is FrameworkElement { Visibility: not Visibility.Visible })
			{
				return false;
			}
		}
		return true;
	}

	private static bool IsDescendantOf(DependencyObject? element, DependencyObject ancestor)
	{
		for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
		{
			if (ReferenceEquals(current, ancestor))
			{
				return true;
			}
		}
		return false;
	}

	private void SelectNumberedTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		int number = (int)sender.Key - (int)Windows.System.VirtualKey.Number0;
		args.Handled = SelectNumberedTab(number);
	}

	private bool SelectNumberedTab(int number)
	{
		if (number is < 1 or > 9 || ViewModel.Tabs.Count is 0)
		{
			return false;
		}

		int targetIndex = number is 9 ? ViewModel.Tabs.Count - 1 : number - 1;
		if (targetIndex < ViewModel.Tabs.Count)
		{
			ViewModel.ActiveTab = ViewModel.Tabs[targetIndex];
			return true;
		}

		return false;
	}

	private bool CanDuplicateSelection()
	{
		if (Browser is not DirectoryBrowserViewModel browser || selectedItems.Count is 0)
		{
			return false;
		}

		return selectedItems.All(item => string.Equals(
			Path.GetDirectoryName(item.Path),
			browser.CurrentPath,
			StringComparison.Ordinal));
	}

	private async Task DuplicateSelectedItemsAsync()
	{
		if (Browser is not DirectoryBrowserViewModel browser || !CanDuplicateSelection() || fileTransferCancellation is not null)
		{
			return;
		}

		LocalFileSystemItem[] items = selectedItems.ToArray();
		var destinationNames = items.ToDictionary(
			static item => item.Path,
			item => GetDuplicateName(item),
			StringComparer.Ordinal);
		await TransferItemsAsync(
			new FileClipboardContent(items.Select(static item => item.Path).ToArray(), FileTransferMode.Copy, 0),
			clearClipboardAfterMove: false,
			forcedConflictResolution: FileConflictResolution.KeepBoth,
			destinationNames: destinationNames);

		string GetDuplicateName(LocalFileSystemItem item)
		{
			if (item.IsNavigableDirectory)
			{
				return string.Format(GetResource("DuplicateNameFormat"), item.Name);
			}

			string name = Path.GetFileNameWithoutExtension(item.Name);
			string extension = Path.GetExtension(item.Name);
			return string.Format(GetResource("DuplicateNameFormat"), name) + extension;
		}
	}

	private async Task CreateSymbolicLinksAsync()
	{
		if (Browser is not DirectoryBrowserViewModel browser || selectedItems.Count is 0 || fileTransferCancellation is not null)
		{
			return;
		}

		LocalFileSystemItem[] items = selectedItems.ToArray();
		SymbolicLinkRequest[] requests = items.Select(item => new SymbolicLinkRequest(item.Path, GetLinkName(item))).ToArray();
		try
		{
			IReadOnlyList<CreatedSymbolicLink> links = await FileOperationService.CreateSymbolicLinksAsync(requests, browser.CurrentPath);
			RecordSymbolicLinkHistory(links.ToArray());
			await browser.RefreshAsync();
			browser.StatusText = string.Format(GetResource("SymbolicLinksCreatedFormat"), links.Count);
		}
		catch (FileOperationException ex)
		{
			await ShowErrorAsync(GetFileOperationError(ex));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowErrorAsync(string.IsNullOrWhiteSpace(ex.Message) ? GetResource("CreateSymbolicLinkErrorMessage") : ex.Message);
		}

		string GetLinkName(LocalFileSystemItem item)
		{
			if (item.IsNavigableDirectory)
			{
				return string.Format(GetResource("SymbolicLinkNameFormat"), item.Name);
			}
			string name = Path.GetFileNameWithoutExtension(item.Name);
			string extension = Path.GetExtension(item.Name);
			return string.Format(GetResource("SymbolicLinkNameFormat"), name) + extension;
		}
	}

	private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (isUpdatingSelection)
		{
			return;
		}
		if (sender is ListViewBase list && GetBrowserForItemsControl(list) is DirectoryBrowserViewModel browser)
		{
			ActivateBrowser(browser, list);
		}
	}

	private void GridItems_SelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs e)
	{
		if (isUpdatingSelection)
		{
			return;
		}
		if (GetBrowserForItemsControl(sender) is DirectoryBrowserViewModel browser)
		{
			ActivateBrowser(browser, sender);
		}
	}

	private void SelectAllMenuItem_Click(object sender, RoutedEventArgs e)
	{
		SelectItems(invert: false);
	}

	private void InvertSelectionMenuItem_Click(object sender, RoutedEventArgs e)
	{
		SelectItems(invert: true);
	}

	private void SelectAllAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (!IsTextInputFocused() && fileTransferCancellation is null)
		{
			args.Handled = true;
			SelectItems(invert: false);
		}
	}

	private void InvertSelectionAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (!IsTextInputFocused() && fileTransferCancellation is null)
		{
			args.Handled = true;
			SelectItems(invert: true);
		}
	}

	private void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
	{
		CopySelectedPaths();
	}

	private void CopyPathAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (!IsTextInputFocused() && selectedItems.Count > 0)
		{
			args.Handled = true;
			CopySelectedPaths();
		}
	}

	private void SelectItems(bool invert)
	{
		if (Browser is not DirectoryBrowserViewModel browser)
		{
			return;
		}

		FrameworkElement control = GetVisibleItemsControl(browser);
		var previousSelection = selectedItems.ToHashSet();
		LocalFileSystemItem[] resultingSelection = invert
			? browser.Items.Where(item => !previousSelection.Contains(item)).ToArray()
			: browser.Items.ToArray();
		isUpdatingSelection = true;
		try
		{
			if (control is ItemsView view)
			{
				var resultingSet = resultingSelection.ToHashSet();
				int[] selectedIndices = browser.Items
					.Select((item, index) => (item, index))
					.Where(entry => resultingSet.Contains(entry.item))
					.Select(static entry => entry.index)
					.ToArray();
				if (!ItemsViewSelectionAdapter.TrySelectIndices(view, selectedIndices))
				{
					if (invert)
					{
						view.InvertSelection();
					}
					else
					{
						view.SelectAll();
					}
				}
			}
			else if (control is ListViewBase list)
			{
				if (!invert)
				{
					list.SelectedItems.Clear();
					foreach (LocalFileSystemItem item in browser.Items)
					{
						list.SelectedItems.Add(item);
					}
				}
				else
				{
					var previouslySelected = list.SelectedItems.OfType<LocalFileSystemItem>().ToHashSet();
					list.SelectedItems.Clear();
					foreach (LocalFileSystemItem item in browser.Items.Where(item => !previouslySelected.Contains(item)))
					{
						list.SelectedItems.Add(item);
					}
				}
			}
		}
		finally
		{
			isUpdatingSelection = false;
		}
		ViewModel.SetActiveBrowser(browser);
		selectedItems = resultingSelection;
		browser.UpdateSelection(selectedItems);
		UpdatePaneVisuals();
		UpdateCommandStates();
	}

	private void CopySelectedPaths()
	{
		if (selectedItems.Count is 0)
		{
			return;
		}

		var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
		data.SetText(string.Join(Environment.NewLine, selectedItems.Select(static item => item.Path)));
		Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
		if (Browser is DirectoryBrowserViewModel browser)
		{
			browser.StatusText = string.Format(GetResource("PathsCopiedFormat"), selectedItems.Count);
		}
	}

	private bool IsTextInputFocused()
	{
		return XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is TextBox;
	}

	private async void CopyButton_Click(object sender, RoutedEventArgs e)
	{
		await SetFileClipboardAsync(FileTransferMode.Copy);
	}

	private async void CutButton_Click(object sender, RoutedEventArgs e)
	{
		await SetFileClipboardAsync(FileTransferMode.Move);
	}

	private async void PasteButton_Click(object sender, RoutedEventArgs e)
	{
		await PasteAsync();
	}

	private async void CopyAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (!IsTextInputFocused() && selectedItems.Count > 0 && fileTransferCancellation is null)
		{
			args.Handled = true;
			await SetFileClipboardAsync(FileTransferMode.Copy);
		}
	}

	private async void CutAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (!IsTextInputFocused() && selectedItems.Count > 0 && fileTransferCancellation is null)
		{
			args.Handled = true;
			await SetFileClipboardAsync(FileTransferMode.Move);
		}
	}

	private async void PasteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (!IsTextInputFocused() && fileTransferCancellation is null)
		{
			args.Handled = true;
			await PasteAsync();
		}
	}

	private async Task SetFileClipboardAsync(FileTransferMode mode)
	{
		if (selectedItems.Count is 0)
		{
			return;
		}

		try
		{
			_ = await FileClipboardService.WriteAsync(
				selectedItems.Select(static item => item.Path).ToArray(),
				mode);
			if (Browser is not null)
			{
				Browser.StatusText = string.Format(
				GetResource(mode is FileTransferMode.Copy ? "CopiedToClipboardFormat" : "CutToClipboardFormat"),
				selectedItems.Count);
			}
		}
		catch (IOException ex)
		{
			await ShowErrorAsync(string.IsNullOrEmpty(ex.Message) ? GetResource("ClipboardWriteErrorMessage") : ex.Message);
		}
		UpdateCommandStates();
	}

	private async Task PasteAsync()
	{
		if (Browser is null || fileTransferCancellation is not null)
		{
			return;
		}
		DirectoryBrowserViewModel targetBrowser = Browser;
		FileClipboardContent clipboard;
		try
		{
			clipboard = await FileClipboardService.ReadAsync();
		}
		catch (IOException ex)
		{
			await ShowErrorAsync(string.IsNullOrEmpty(ex.Message) ? GetResource("ClipboardReadErrorMessage") : ex.Message);
			return;
		}

		if (clipboard.Paths.Count is 0)
		{
			targetBrowser.StatusText = GetResource("ClipboardHasNoFilesMessage");
			return;
		}

		await TransferItemsAsync(clipboard, clearClipboardAfterMove: true);
	}

	private async Task TransferItemsAsync(
		FileClipboardContent clipboard,
		bool clearClipboardAfterMove,
		FileConflictResolution? forcedConflictResolution = null,
		IReadOnlyDictionary<string, string>? destinationNames = null)
	{
		if (Browser is null || fileTransferCancellation is not null || clipboard.Paths.Count is 0)
		{
			return;
		}

		DirectoryBrowserViewModel targetBrowser = Browser;
		FileConflictResolution conflictResolution = forcedConflictResolution ?? FileConflictResolution.KeepBoth;
		if (forcedConflictResolution is null && HasDestinationConflicts(clipboard.Paths, targetBrowser.CurrentPath, clipboard.Mode))
		{
			FileConflictResolution? selectedResolution = await ShowConflictResolutionAsync();
			if (selectedResolution is null)
			{
				return;
			}
			conflictResolution = selectedResolution.Value;
		}

		var cancellation = new CancellationTokenSource();
		fileTransferCancellation = cancellation;
		targetBrowser.IsFileOperationRunning = true;
		targetBrowser.FileOperationProgress = 0;
		UpdateCommandStates();
		string? finalStatus = null;

		var progress = new Progress<FileTransferProgress>(value =>
		{
			double ratio = value.TotalBytes > 0
				? (double)value.CompletedBytes / value.TotalBytes
				: (double)value.CompletedItems / Math.Max(1, value.TotalItems);
			targetBrowser.FileOperationProgress = Math.Clamp(ratio * 100, 0, 100);
			FileOperationProgressBar.Value = targetBrowser.FileOperationProgress;
			targetBrowser.StatusText = string.Format(
				GetResource("FileTransferProgressFormat"),
				value.CurrentItem,
				value.CompletedItems,
				value.TotalItems);
		});

		try
		{
			bool preserveReplacedItems = conflictResolution is FileConflictResolution.Replace;
			FileTransferResult result = await FileTransferService.TransferAsync(
				new FileTransferRequest(
					clipboard.Paths,
					targetBrowser.CurrentPath,
					clipboard.Mode,
					conflictResolution,
					DestinationNames: destinationNames,
					PreserveReplacedItems: preserveReplacedItems),
				progress,
				cancellation.Token);
			RecordTransferHistory(clipboard.Mode, result.CompletedRoots);

			if (clearClipboardAfterMove && clipboard.Mode is FileTransferMode.Move)
			{
				await FileClipboardService.ClearAsync(clipboard.ChangeCount);
			}

			finalStatus = string.Format(GetResource("FileTransferCompletedFormat"), result.CompletedRootItems, result.SkippedRootItems);
		}
		catch (FileTransferCanceledException ex)
		{
			RecordTransferHistory(clipboard.Mode, ex.CompletedRoots);
			finalStatus = string.Format(GetResource("FileTransferCanceledWithCompletedFormat"), ex.CompletedRoots.Length);
		}
		catch (OperationCanceledException)
		{
			finalStatus = GetResource("FileTransferCanceledMessage");
		}
		catch (FileTransferPartialException ex)
		{
			RecordTransferHistory(clipboard.Mode, ex.CompletedRoots);
			if (ex.InnerException is FileOperationException operationException)
			{
				await ShowErrorAsync(GetFileOperationError(operationException));
			}
			else
			{
				await ShowErrorAsync(ex.InnerException?.Message ?? ex.Message);
			}
			finalStatus = string.Format(GetResource("FileTransferFailedWithCompletedFormat"), ex.CompletedRoots.Length);
		}
		catch (FileOperationException ex)
		{
			await ShowErrorAsync(GetFileOperationError(ex));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowErrorAsync(ex.Message);
		}
		finally
		{
			targetBrowser.IsFileOperationRunning = false;
			targetBrowser.FileOperationProgress = 0;
			fileTransferCancellation.Dispose();
			fileTransferCancellation = null;
			selectedItems = [];
			await targetBrowser.RefreshAsync();
			if (finalStatus is not null)
			{
				targetBrowser.StatusText = finalStatus;
			}
			UpdateCommandStates();
		}
	}

	private void CancelOperationButton_Click(object sender, RoutedEventArgs e)
	{
		fileTransferCancellation?.Cancel();
	}

	private async Task<FileConflictResolution?> ShowConflictResolutionAsync()
	{
		var choices = new ComboBox
		{
			ItemsSource = new[]
			{
				GetResource("ConflictKeepBothOption"),
				GetResource("ConflictReplaceOption"),
				GetResource("ConflictSkipOption"),
			},
			SelectedIndex = 0,
			HorizontalAlignment = HorizontalAlignment.Stretch,
		};
		var content = new StackPanel { Spacing = 12 };
		content.Children.Add(new TextBlock { Text = GetResource("ConflictDialogMessage"), TextWrapping = TextWrapping.Wrap });
		content.Children.Add(choices);

		var dialog = new ContentDialog
		{
			Title = GetResource("ConflictDialogTitle"),
			Content = content,
			PrimaryButtonText = GetResource("ContinueButtonText"),
			CloseButtonText = GetResource("CancelButtonText"),
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = XamlRoot,
		};

		if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
		{
			return null;
		}

		return choices.SelectedIndex switch
		{
			1 => FileConflictResolution.Replace,
			2 => FileConflictResolution.Skip,
			_ => FileConflictResolution.KeepBoth,
		};
	}

	private static bool HasDestinationConflicts(IReadOnlyList<string> paths, string destinationDirectory, FileTransferMode mode)
	{
		foreach (string path in paths)
		{
			string destination = Path.Combine(destinationDirectory, Path.GetFileName(path));
			if (mode is FileTransferMode.Move && string.Equals(Path.GetFullPath(path), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			try
			{
				_ = File.GetAttributes(destination);
				return true;
			}
			catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
			{
			}
		}

		return false;
	}

	private void UpdateCommandStates()
	{
		bool isFileOperationRunning = fileTransferCancellation is not null;
		bool isIdle = !isFileOperationRunning && !isConnectingServer && !isHistoryOperationRunning;
		UndoButton.IsEnabled = isIdle && undoHistory.Count > 0;
		RedoButton.IsEnabled = isIdle && redoHistory.Count > 0;
		CopyButton.IsEnabled = isIdle && selectedItems.Count > 0;
		CutButton.IsEnabled = isIdle && selectedItems.Count > 0;
		PasteButton.IsEnabled = isIdle;
		RenameButton.IsEnabled = isIdle && selectedItems.Count > 0;
		RevealButton.IsEnabled = isIdle && selectedItems.Count is 1;
		DeleteButton.IsEnabled = isIdle && selectedItems.Count > 0;
		PropertiesButton.IsEnabled = isIdle && selectedItems.Count > 0;
		ShareButton.IsEnabled = isIdle && selectedItems.Count > 0;
		ArchiveButton.IsEnabled = isIdle && selectedItems.Count > 0;
		SelectionButton.IsEnabled = isIdle;
		CopyPathMenuItem.IsEnabled = isIdle && selectedItems.Count > 0;
		FavoriteButton.IsEnabled = isIdle && selectedItems is [LocalFileSystemItem { IsNavigableDirectory: true }];
		SettingsButton.IsEnabled = isIdle;
		ConnectServerButton.IsEnabled = isIdle;
		ClearRecentButton.IsEnabled = isIdle;
		SplitViewButton.IsEnabled = isIdle;
		PreviewPaneButton.IsEnabled = isIdle;
		TerminalButton.IsEnabled = isIdle;
		FileOperationProgressBar.Visibility = isFileOperationRunning ? Visibility.Visible : Visibility.Collapsed;
		CancelOperationButton.Visibility = isFileOperationRunning ? Visibility.Visible : Visibility.Collapsed;
		if (!isFileOperationRunning)
		{
			FileOperationProgressBar.Value = 0;
		}
		UpdatePreviewPaneContent();
		((App)Application.Current).UpdateMainMenu(this);
	}

	private void CommandToolbarBorder_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		UpdateCommandToolbarLayout(e.NewSize.Width);
	}

	private void UpdateCommandToolbarLayout(double width)
	{
		bool useOverflow = width < 1800;
		bool compact = width < 1120;
		RenameButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
		ShareButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
		DeleteButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
		EditCommandSeparator.Visibility = useOverflow ? Visibility.Collapsed : Visibility.Visible;
		SecondaryCommandSeparator.Visibility = useOverflow ? Visibility.Collapsed : Visibility.Visible;
		RevealButton.Visibility = useOverflow ? Visibility.Collapsed : Visibility.Visible;
		PropertiesButton.Visibility = useOverflow ? Visibility.Collapsed : Visibility.Visible;
		SelectionButton.Visibility = useOverflow ? Visibility.Collapsed : Visibility.Visible;
		ArchiveButton.Visibility = useOverflow ? Visibility.Collapsed : Visibility.Visible;
		FavoriteButton.Visibility = useOverflow ? Visibility.Collapsed : Visibility.Visible;
		TerminalButton.Visibility = useOverflow ? Visibility.Collapsed : Visibility.Visible;
		MoreCommandsButton.Visibility = useOverflow ? Visibility.Visible : Visibility.Collapsed;
		MoreRenameItem.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
		MoreShareItem.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
		MoreDeleteItem.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
		MoreCompactSeparator.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
	}

	private void MoreCommandsFlyout_Opening(object sender, object e)
	{
		MoreRenameItem.IsEnabled = RenameButton.IsEnabled;
		MoreShareItem.IsEnabled = ShareButton.IsEnabled;
		MoreDeleteItem.IsEnabled = DeleteButton.IsEnabled;
		MorePermanentDeleteItem.IsEnabled = DeleteButton.IsEnabled;
		MoreRevealItem.IsEnabled = RevealButton.IsEnabled;
		MorePropertiesItem.IsEnabled = PropertiesButton.IsEnabled;
		MoreSelectionSubItem.IsEnabled = SelectionButton.IsEnabled;
		MoreCopyPathItem.IsEnabled = CopyPathMenuItem.IsEnabled;
		MoreArchiveSubItem.IsEnabled = ArchiveButton.IsEnabled;
		MoreExtractArchiveItem.IsEnabled = selectedItems is [LocalFileSystemItem item] && IsZipArchive(item);
		MoreFavoriteItem.IsEnabled = FavoriteButton.IsEnabled;
		MoreTerminalItem.IsEnabled = TerminalButton.IsEnabled;
	}

	private void PreviewPaneButton_Click(object sender, RoutedEventArgs e)
	{
		SetPreviewPaneOpen(!isPreviewPaneOpen);
	}

	private void ClosePreviewPaneButton_Click(object sender, RoutedEventArgs e)
	{
		SetPreviewPaneOpen(false);
	}

	private async void PreviewPaneQuickLookButton_Click(object sender, RoutedEventArgs e)
	{
		if (selectedItems is not [LocalFileSystemItem item])
		{
			return;
		}
		try
		{
			await WorkspaceService.PreviewAsync(item.Path);
		}
		catch (IOException)
		{
			await ShowErrorAsync(GetResource("PreviewItemErrorMessage"));
		}
	}

	private void SetPreviewPaneOpen(bool value)
	{
		if (isPreviewPaneOpen == value)
		{
			return;
		}
		isPreviewPaneOpen = value;
		currentSettings = currentSettings with { IsPreviewPaneOpen = value };
		UpdatePreviewPaneVisuals();
		ScheduleWorkspaceSave();
	}

	private void UpdatePreviewPaneVisuals()
	{
		PreviewPaneBorder.Visibility = isPreviewPaneOpen ? Visibility.Visible : Visibility.Collapsed;
		PaneGrid.Margin = new Thickness(0, 0, isPreviewPaneOpen ? 320 : 0, 0);
		PreviewPaneButton.Opacity = isPreviewPaneOpen ? 1 : 0.72;
		UpdatePreviewPaneContent();
	}

	private void UpdatePreviewPaneContent()
	{
		if (selectedItems is [LocalFileSystemItem item])
		{
			PreviewPaneContent.DataContext = item;
			PreviewPaneContent.Visibility = Visibility.Visible;
			PreviewSelectionSummary.Visibility = Visibility.Collapsed;
			PreviewPaneQuickLookButton.IsEnabled = true;
			return;
		}

		PreviewPaneContent.DataContext = null;
		PreviewPaneContent.Visibility = Visibility.Collapsed;
		PreviewSelectionSummary.Visibility = Visibility.Visible;
		PreviewSelectionSummary.Text = selectedItems.Count is 0
			? GetResource("PreviewPaneEmptyMessage")
			: string.Format(GetResource("PreviewPaneMultipleFormat"), selectedItems.Count);
		PreviewPaneQuickLookButton.IsEnabled = false;
	}

	private void Items_DragOver(object sender, DragEventArgs e)
	{
		if (e.DataView.Contains(StandardDataFormats.StorageItems) && fileTransferCancellation is null)
		{
			e.AcceptedOperation = DataPackageOperation.Copy;
			e.DragUIOverride.IsCaptionVisible = true;
			e.DragUIOverride.Caption = GetResource("DropCopyCaption");
			e.Handled = true;
		}
	}

	private void Items_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
	{
		if (sender is ListViewBase list && GetBrowserForItemsControl(list) is DirectoryBrowserViewModel browser)
		{
			ActivateBrowser(browser, list);
		}

		string[] paths = e.Items
			.OfType<LocalFileSystemItem>()
			.Select(static item => item.Path)
			.ToArray();
		if (!ConfigureOutboundDrag(e.Data, paths))
		{
			e.Cancel = true;
		}
	}

	private void GridItem_DragStarting(UIElement sender, DragStartingEventArgs e)
	{
		if (sender is not FrameworkElement { DataContext: LocalFileSystemItem item } ||
			GetBrowserForItem(item) is not DirectoryBrowserViewModel browser ||
			GetVisibleItemsControl(browser) is not ItemsView view)
		{
			e.Cancel = true;
			return;
		}

		int index = browser.Items.IndexOf(item);
		if (index < 0)
		{
			e.Cancel = true;
			return;
		}
		if (!view.IsSelected(index))
		{
			view.DeselectAll();
			view.Select(index);
		}
		ActivateBrowser(browser, view);
		if (!ConfigureOutboundDrag(e.Data, selectedItems.Select(static selected => selected.Path).ToArray()))
		{
			e.Cancel = true;
		}
	}

	private static bool ConfigureOutboundDrag(DataPackage data, IReadOnlyList<string> paths)
	{
		if (paths.Count is 0)
		{
			return false;
		}

		data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
		{
			var deferral = request.GetDeferral();
			try
			{
				var storageItems = new List<IStorageItem>(paths.Count);
				foreach (string path in paths)
				{
					IStorageItem item = Directory.Exists(path)
						? await StorageFolder.GetFolderFromPathAsync(path)
						: await StorageFile.GetFileFromPathAsync(path);
					storageItems.Add(item);
				}
				request.SetData(storageItems);
			}
			finally
			{
				deferral.Complete();
			}
		});
		data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move | DataPackageOperation.Link;
		return true;
	}

	private async void Items_Drop(object sender, DragEventArgs e)
	{
		if (!e.DataView.Contains(StandardDataFormats.StorageItems) || fileTransferCancellation is not null)
		{
			return;
		}

		var deferral = e.GetDeferral();
		try
		{
			if (GetBrowserForItemsControl(sender) is DirectoryBrowserViewModel browser)
			{
				ActivateBrowser(browser, sender as FrameworkElement);
			}

			var storageItems = await e.DataView.GetStorageItemsAsync();
			string[] paths = storageItems
				.Select(static item => item.Path)
				.Where(static path => !string.IsNullOrWhiteSpace(path))
				.ToArray();
			if (paths.Length is 0)
			{
				return;
			}

			await TransferItemsAsync(new FileClipboardContent(paths, FileTransferMode.Copy, 0), clearClipboardAfterMove: false);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowErrorAsync(string.IsNullOrEmpty(ex.Message) ? GetResource("DropItemsErrorMessage") : ex.Message);
		}
		finally
		{
			deferral.Complete();
		}
	}

	private void Item_RightTapped(object sender, RightTappedRoutedEventArgs e)
	{
		if (sender is not FrameworkElement { DataContext: LocalFileSystemItem item } || GetBrowserForItem(item) is not DirectoryBrowserViewModel browser)
		{
			return;
		}

		FrameworkElement control = GetVisibleItemsControl(browser);
		if (control is ItemsView view)
		{
			int index = browser.Items.IndexOf(item);
			if (index >= 0 && !view.IsSelected(index))
			{
				view.DeselectAll();
				view.Select(index);
			}
		}
		else if (control is ListViewBase list && !list.SelectedItems.Contains(item))
		{
			list.SelectedItem = item;
		}
		ActivateBrowser(browser, control);
		MenuFlyout flyout = CreateItemContextFlyout();
		PrepareItemContextFlyout(flyout);
		flyout.ShowAt((FrameworkElement)sender);
		e.Handled = true;
	}

	private void PrimaryPane_RightTapped(object sender, RightTappedRoutedEventArgs e) =>
		ShowBackgroundContextMenu(e, ViewModel.ActiveTab?.Browser, PrimaryPaneBorder, PrimaryBackgroundContextFlyout);

	private void SecondaryPane_RightTapped(object sender, RightTappedRoutedEventArgs e) =>
		ShowBackgroundContextMenu(e, ViewModel.ActiveTab?.SecondaryBrowser, SecondaryPaneBorder, SecondaryBackgroundContextFlyout);

	private void ShowBackgroundContextMenu(
		RightTappedRoutedEventArgs e,
		DirectoryBrowserViewModel? browser,
		FrameworkElement pane,
		MenuFlyout flyout)
	{
		if (browser is null || HasFileItemAncestor(e.OriginalSource as DependencyObject, pane))
		{
			return;
		}

		FrameworkElement control = GetVisibleItemsControl(browser);
		ActivateBrowser(browser, control);
		if (control is ItemsView itemsView)
		{
			itemsView.DeselectAll();
		}
		else if (control is ListViewBase listView)
		{
			listView.SelectedItems.Clear();
		}
		PrepareBackgroundContextMenu(flyout, browser);
		flyout.ShowAt(pane, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(pane) });
		e.Handled = true;
	}

	private static bool HasFileItemAncestor(DependencyObject? source, DependencyObject pane)
	{
		for (DependencyObject? current = source; current is not null && !ReferenceEquals(current, pane); current = VisualTreeHelper.GetParent(current))
		{
			if (current is FrameworkElement { DataContext: LocalFileSystemItem })
			{
				return true;
			}
		}
		return false;
	}

	private MenuFlyout CreateItemContextFlyout()
	{
		var flyout = new MenuFlyout();
		flyout.Items.Add(CreateItemContextMenuItem("ContextOpenItem/Text", "Open"));
		flyout.Items.Add(CreateItemContextMenuItem("ContextOpenInNewTabItem/Text", "OpenInNewTab"));
		flyout.Items.Add(CreateItemContextMenuItem("ContextPreviewItem/Text", "Preview"));
		flyout.Items.Add(new MenuFlyoutSeparator());
		flyout.Items.Add(CreateItemContextMenuItem("ContextCutItem/Text", "Cut"));
		flyout.Items.Add(CreateItemContextMenuItem("ContextCopyItem/Text", "Copy"));
		flyout.Items.Add(CreateItemContextMenuItem("ContextRenameItem/Text", "Rename"));
		flyout.Items.Add(CreateItemContextMenuItem("ContextDeleteItem/Text", "Delete"));
		flyout.Items.Add(new MenuFlyoutSeparator());
		flyout.Items.Add(CreateItemContextMenuItem("ContextPropertiesItem/Text", "Properties"));

		var moreActions = new MenuFlyoutSubItem
		{
			Text = GetResource("ContextMoreActionsSubItem/Text"),
			Tag = "MoreActions",
		};
		moreActions.Items.Add(CreateItemContextMenuItem("ContextOpenWithItem/Text", "OpenWith"));
		moreActions.Items.Add(CreateItemContextMenuItem("ContextRevealItem/Text", "Reveal"));
		moreActions.Items.Add(CreateItemContextMenuItem("ContextTerminalItem/Text", "Terminal"));
		moreActions.Items.Add(new MenuFlyoutSeparator());
		moreActions.Items.Add(CreateItemContextMenuItem("ContextDuplicateItem/Text", "Duplicate"));
		moreActions.Items.Add(CreateItemContextMenuItem("ContextCreateSymbolicLinkItem/Text", "CreateSymbolicLink"));
		moreActions.Items.Add(CreateItemContextMenuItem("ContextCopyPathItem/Text", "CopyPath"));
		moreActions.Items.Add(CreateItemContextMenuItem("ContextShareItem/Text", "Share"));
		moreActions.Items.Add(new MenuFlyoutSeparator());
		moreActions.Items.Add(CreateItemContextMenuItem("ContextCompressItem/Text", "Compress"));
		moreActions.Items.Add(CreateItemContextMenuItem("ContextExtractItem/Text", "Extract"));
		moreActions.Items.Add(CreateItemContextMenuItem("ContextFavoriteItem/Text", "Favorite"));
		moreActions.Items.Add(CreateItemContextMenuItem("ContextPermanentDeleteItem/Text", "PermanentDelete"));
		flyout.Items.Add(moreActions);
		return flyout;
	}

	private MenuFlyoutItem CreateItemContextMenuItem(string resourceKey, string action)
	{
		var item = new MenuFlyoutItem
		{
			Text = GetResource(resourceKey),
			Tag = action,
		};
		item.Click += ContextAction_Click;
		return item;
	}

	private async void ContextAction_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not MenuFlyoutItem { Tag: string action } || Browser is null)
		{
			return;
		}

		try
		{
			switch (action)
			{
				case "Open" when selectedItems is [LocalFileSystemItem item]:
					if (item.IsNavigableDirectory)
					{
						await Browser.NavigateAsync(item.Path);
					}
					else
					{
						await WorkspaceService.OpenAsync(item.Path);
					}
					break;
				case "OpenWith":
					await ShowOpenWithDialogAsync();
					break;
				case "OpenInNewTab":
					await OpenInNewTabAsync();
					break;
				case "Preview" when selectedItems is [LocalFileSystemItem item]:
					await WorkspaceService.PreviewAsync(item.Path);
					break;
				case "Reveal":
					RevealButton_Click(sender, e);
					break;
				case "Terminal":
					TerminalButton_Click(sender, e);
					break;
				case "Cut":
					await SetFileClipboardAsync(FileTransferMode.Move);
					break;
				case "Copy":
					await SetFileClipboardAsync(FileTransferMode.Copy);
					break;
				case "Duplicate":
					await DuplicateSelectedItemsAsync();
					break;
				case "CreateSymbolicLink":
					await CreateSymbolicLinksAsync();
					break;
				case "CopyPath":
					CopySelectedPaths();
					break;
				case "Rename":
					RenameButton_Click(sender, e);
					break;
				case "Share":
					ShareButton_Click(sender, e);
					break;
				case "Delete":
					DeleteButton_Click(sender, e);
					break;
				case "PermanentDelete":
					await DeletePermanentlyAsync();
					break;
				case "Properties":
					PropertiesButton_Click(sender, e);
					break;
				case "Compress":
					CompressArchiveMenuItem_Click(sender, e);
					break;
				case "Extract":
					ExtractArchiveMenuItem_Click(sender, e);
					break;
				case "Favorite":
					FavoriteButton_Click(sender, e);
					break;
			}
		}
		catch (IOException ex)
		{
			await ShowErrorAsync(string.IsNullOrEmpty(ex.Message) ? GetResource("OpenItemErrorMessage") : ex.Message);
		}
	}

	private void ItemContextFlyout_Opening(object sender, object e)
	{
		if (sender is not MenuFlyout flyout)
		{
			return;
		}
		PrepareItemContextFlyout(flyout);
	}

	private void PrepareItemContextFlyout(MenuFlyout flyout)
	{
		bool isIdle = fileTransferCancellation is null && !isHistoryOperationRunning && !isConnectingServer;
		bool isSingleFile = selectedItems is [LocalFileSystemItem { IsNavigableDirectory: false }];
		bool isSingleFolder = selectedItems is [LocalFileSystemItem { IsNavigableDirectory: true }];
		bool isSingleZip = selectedItems is [LocalFileSystemItem selectedArchive] && IsZipArchive(selectedArchive);
		foreach (MenuFlyoutItem item in EnumerateMenuFlyoutItems(flyout.Items).OfType<MenuFlyoutItem>())
		{
			item.Visibility = item.Tag switch
			{
				"OpenWith" => isSingleFile ? Visibility.Visible : Visibility.Collapsed,
				"OpenInNewTab" or "Favorite" => isSingleFolder ? Visibility.Visible : Visibility.Collapsed,
				"Extract" => isSingleZip ? Visibility.Visible : Visibility.Collapsed,
				_ => Visibility.Visible,
			};
			item.IsEnabled = item.Tag switch
			{
				"Open" or "Preview" or "Reveal" => isIdle && selectedItems.Count is 1,
				"OpenWith" => isIdle && selectedItems is [LocalFileSystemItem { IsNavigableDirectory: false }],
				"OpenInNewTab" => isIdle && selectedItems is [LocalFileSystemItem { IsNavigableDirectory: true }],
				"Terminal" => isIdle && selectedItems.Count > 0,
				"Duplicate" => isIdle && CanDuplicateSelection(),
				"CreateSymbolicLink" => isIdle && selectedItems.Count > 0,
				"Cut" or "Copy" or "CopyPath" or "Rename" or "Share" or "Delete" or
					"PermanentDelete" or "Properties" or "Compress" => isIdle && selectedItems.Count > 0,
				"Extract" => isIdle && selectedItems is [LocalFileSystemItem archive] && IsZipArchive(archive),
				"Favorite" => isIdle && selectedItems is [LocalFileSystemItem { IsNavigableDirectory: true }],
				_ => item.IsEnabled,
			};
		}
	}

	private void PrimaryBackgroundContextFlyout_Opening(object sender, object e) =>
		PrepareBackgroundContextMenu(sender, ViewModel.ActiveTab?.Browser);

	private void SecondaryBackgroundContextFlyout_Opening(object sender, object e) =>
		PrepareBackgroundContextMenu(sender, ViewModel.ActiveTab?.SecondaryBrowser);

	private void PrepareBackgroundContextMenu(object sender, DirectoryBrowserViewModel? browser)
	{
		if (sender is not MenuFlyout flyout || browser is null)
		{
			return;
		}

		FrameworkElement control = GetVisibleItemsControl(browser);
		if (control is ItemsView itemsView)
		{
			itemsView.DeselectAll();
		}
		else if (control is ListViewBase listView)
		{
			listView.SelectedItems.Clear();
		}
		ActivateBrowser(browser, control);

		bool isIdle = fileTransferCancellation is null && !isHistoryOperationRunning && !isConnectingServer;
		foreach (MenuFlyoutItemBase item in EnumerateMenuFlyoutItems(flyout.Items))
		{
			if (item is ToggleMenuFlyoutItem { Tag: string option } toggle)
			{
				toggle.IsChecked = option switch
				{
					"Name" => browser.SortField is FileSortField.Name,
					"Modified" => browser.SortField is FileSortField.Modified,
					"Size" => browser.SortField is FileSortField.Size,
					"Ascending" => browser.SortDirection is FileSortDirection.Ascending,
					"Descending" => browser.SortDirection is FileSortDirection.Descending,
					"Grid" => browser.IsGridView,
					"Details" => !browser.IsGridView,
					_ => toggle.IsChecked,
				};
				toggle.IsEnabled = isIdle;
			}
			else if (item is MenuFlyoutItem { Tag: string action } command)
			{
				command.IsEnabled = action switch
				{
					"NewFolder" or "NewTextFile" or "Paste" or "Terminal" or "Refresh" => isIdle,
					_ => command.IsEnabled,
				};
			}
		}
	}

	private static string[] GetMenuActionTags(MenuFlyout flyout) =>
		EnumerateMenuFlyoutItems(flyout.Items)
			.Select(static item => item.Tag as string)
			.Where(static action => action is not null)
			.Cast<string>()
			.ToArray();

	private void LocalizeContextMenuSubItems()
	{
		MenuFlyout itemContextFlyout = (MenuFlyout)Resources["ItemContextFlyout"];
		foreach (MenuFlyoutSubItem subItem in new[] { itemContextFlyout, PrimaryBackgroundContextFlyout, SecondaryBackgroundContextFlyout }
			.SelectMany(static flyout => EnumerateMenuFlyoutItems(flyout.Items))
			.OfType<MenuFlyoutSubItem>())
		{
			subItem.Text = subItem.Tag switch
			{
				"MoreActions" => GetResource("ContextMoreActionsSubItem/Text"),
				"New" => GetResource("BackgroundNewSubItem/Text"),
				"Sort" => GetResource("BackgroundSortSubItem/Text"),
				"View" => GetResource("BackgroundViewSubItem/Text"),
				_ => subItem.Text,
			};
		}
	}

	private static int CountItemContextTargets(DependencyObject root)
	{
		int count = root is FrameworkElement { DataContext: LocalFileSystemItem } element &&
			!string.IsNullOrEmpty(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(element)) ? 1 : 0;
		for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
		{
			count += CountItemContextTargets(VisualTreeHelper.GetChild(root, index));
		}
		return count;
	}

	private static IEnumerable<MenuFlyoutItemBase> EnumerateMenuFlyoutItems(IEnumerable<MenuFlyoutItemBase> items)
	{
		foreach (MenuFlyoutItemBase item in items)
		{
			yield return item;
			if (item is MenuFlyoutSubItem subItem)
			{
				foreach (MenuFlyoutItemBase child in EnumerateMenuFlyoutItems(subItem.Items))
				{
					yield return child;
				}
			}
		}
	}

	private async void TerminalButton_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is null)
		{
			return;
		}

		string path = selectedItems is [LocalFileSystemItem item]
			? item.IsNavigableDirectory ? item.Path : Path.GetDirectoryName(item.Path) ?? Browser.CurrentPath
			: Browser.CurrentPath;
		try
		{
			await WorkspaceService.OpenTerminalAsync(path, currentSettings.Terminal);
		}
		catch (IOException)
		{
			await ShowErrorAsync(GetResource("OpenTerminalErrorMessage"));
		}
	}

	private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
	{
		if (selectedItems is not [LocalFileSystemItem { IsNavigableDirectory: true } item])
		{
			return;
		}

		string path = Path.GetFullPath(item.Path);
		var favorites = new HashSet<string>(currentSettings.FavoritePaths ?? [], StringComparer.OrdinalIgnoreCase);
		bool added = favorites.Add(path);
		if (!added)
		{
			favorites.Remove(path);
		}

		var newSettings = currentSettings with { FavoritePaths = favorites.Order(StringComparer.CurrentCultureIgnoreCase).ToArray() };
		try
		{
			newSettings = await PersistSettingsAsync(newSettings);
			ViewModel.ApplySettings(newSettings);
			if (Browser is not null)
			{
				Browser.StatusText = GetResource(added ? "FavoriteAddedMessage" : "FavoriteRemovedMessage");
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowErrorAsync(string.IsNullOrEmpty(ex.Message) ? GetResource("SaveSettingsErrorMessage") : ex.Message);
		}
	}

	private void ArchiveFlyout_Opening(object sender, object e)
	{
		ExtractArchiveMenuItem.IsEnabled = selectedItems is [LocalFileSystemItem item] && IsZipArchive(item);
	}

	private async void CompressArchiveMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is not DirectoryBrowserViewModel targetBrowser || selectedItems.Count is 0 || fileTransferCancellation is not null)
		{
			return;
		}

		string defaultStem = selectedItems is [LocalFileSystemItem item]
			? item.IsDirectory ? item.Name : Path.GetFileNameWithoutExtension(item.Name)
			: GetResource("DefaultArchiveName");
		if (string.IsNullOrWhiteSpace(defaultStem))
		{
			defaultStem = GetResource("DefaultArchiveName");
		}

		var input = new TextBox { Text = $"{defaultStem}.zip" };
		ContentDialog dialog = CreateTextInputDialog("CreateArchiveDialogTitle", "CreateButtonText", input);
		if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
		{
			return;
		}

		string[] paths = selectedItems.Select(static selected => selected.Path).ToArray();
		string destination = targetBrowser.CurrentPath;
		string archiveName = input.Text.Trim();
		await RunArchiveOperationAsync(
			(progress, cancellationToken) => ArchiveService.CreateZipAsync(paths, destination, archiveName, progress, cancellationToken),
			"CompressingArchiveProgressFormat",
			"ArchiveCreatedFormat");
	}

	private async void ExtractArchiveMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is not DirectoryBrowserViewModel browser || selectedItems is not [LocalFileSystemItem item] || !IsZipArchive(item) || fileTransferCancellation is not null)
		{
			await ShowErrorAsync(GetResource("SelectZipArchiveMessage"));
			return;
		}
		await ExtractZipArchiveAsync(browser, item);
	}

	private async Task ExtractZipArchiveAsync(DirectoryBrowserViewModel browser, LocalFileSystemItem item)
	{
		string folderName = Path.GetFileNameWithoutExtension(item.Name);
		var input = new TextBox { Text = folderName };
		ContentDialog dialog = CreateTextInputDialog("ExtractArchiveDialogTitle", "ExtractButtonText", input);
		if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
		{
			return;
		}

		string destination = browser.CurrentPath;
		await RunArchiveOperationAsync(
			(progress, cancellationToken) => ArchiveService.ExtractZipAsync(item.Path, destination, input.Text.Trim(), progress, cancellationToken),
			"ExtractingArchiveProgressFormat",
			"ArchiveExtractedFormat");
	}

	private async Task RunArchiveOperationAsync(
		Func<IProgress<ArchiveProgress>, CancellationToken, Task<string>> operation,
		string progressResource,
		string completedResource)
	{
		if (Browser is not DirectoryBrowserViewModel targetBrowser || fileTransferCancellation is not null)
		{
			return;
		}

		var cancellation = new CancellationTokenSource();
		fileTransferCancellation = cancellation;
		targetBrowser.IsFileOperationRunning = true;
		targetBrowser.FileOperationProgress = 0;
		UpdateCommandStates();
		string? finalStatus = null;
		var progress = new Progress<ArchiveProgress>(value =>
		{
			double ratio = value.TotalBytes > 0
				? (double)value.CompletedBytes / value.TotalBytes
				: (double)value.CompletedItems / Math.Max(1, value.TotalItems);
			targetBrowser.FileOperationProgress = Math.Clamp(ratio * 100, 0, 100);
			FileOperationProgressBar.Value = targetBrowser.FileOperationProgress;
			targetBrowser.StatusText = string.Format(
				GetResource(progressResource),
				value.CurrentItem,
				value.CompletedItems,
				value.TotalItems);
		});

		try
		{
			string resultPath = await operation(progress, cancellation.Token);
			finalStatus = string.Format(GetResource(completedResource), Path.GetFileName(resultPath));
		}
		catch (OperationCanceledException)
		{
			finalStatus = GetResource("FileTransferCanceledMessage");
		}
		catch (FileOperationException ex)
		{
			await ShowErrorAsync(GetFileOperationError(ex));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			await ShowErrorAsync(string.IsNullOrEmpty(ex.Message) ? GetResource("ArchiveOperationErrorMessage") : ex.Message);
		}
		finally
		{
			targetBrowser.IsFileOperationRunning = false;
			targetBrowser.FileOperationProgress = 0;
			fileTransferCancellation.Dispose();
			fileTransferCancellation = null;
			selectedItems = [];
			await targetBrowser.RefreshAsync();
			if (finalStatus is not null)
			{
				targetBrowser.StatusText = finalStatus;
			}
			UpdateCommandStates();
		}
	}

	private static bool IsZipArchive(LocalFileSystemItem item)
	{
		return !item.IsDirectory && item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
	}

	private async void ForwardButton_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is not null)
		{
			await Browser.GoForwardAsync();
		}
	}

	private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		SearchBox.Focus(FocusState.Keyboard);
		SearchBox.SelectAll();
		args.Handled = true;
	}

	private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (Browser is not DirectoryBrowserViewModel browser)
		{
			return;
		}

		searchInputCancellation?.Cancel();
		searchInputCancellation?.Dispose();
		var cancellation = new CancellationTokenSource();
		searchInputCancellation = cancellation;
		string query = SearchBox.Text;
		try
		{
			await Task.Delay(300, cancellation.Token);
			await browser.SearchAsync(query);
			if (!cancellation.IsCancellationRequested &&
				ReferenceEquals(searchInputCancellation, cancellation) &&
				string.Equals(SearchBox.Text, query, StringComparison.Ordinal) &&
				!string.IsNullOrWhiteSpace(query))
			{
				await RecordSearchHistoryAsync(query.Trim());
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private void SearchOptionsButton_Click(object sender, RoutedEventArgs e)
	{
		var flyout = new MenuFlyout();
		if (!string.IsNullOrWhiteSpace(SearchBox.Text))
		{
			var saveItem = new MenuFlyoutItem { Text = GetResource("SaveCurrentSearchMenuItem") };
			saveItem.Click += SaveCurrentSearchMenuItem_Click;
			flyout.Items.Add(saveItem);
			flyout.Items.Add(new MenuFlyoutSeparator());
		}

		var savedSearches = new MenuFlyoutSubItem { Text = GetResource("SavedSearchesMenu") };
		foreach (SavedSearch savedSearch in currentSettings.SavedSearches ?? [])
		{
			var item = new MenuFlyoutItem { Text = savedSearch.Name, Tag = savedSearch };
			item.Click += SavedSearchMenuItem_Click;
			savedSearches.Items.Add(item);
		}
		if (savedSearches.Items.Count is 0)
		{
			savedSearches.Items.Add(new MenuFlyoutItem { Text = GetResource("NoSavedSearchesMenuItem"), IsEnabled = false });
		}
		flyout.Items.Add(savedSearches);

		var history = new MenuFlyoutSubItem { Text = GetResource("SearchHistoryMenu") };
		foreach (string query in currentSettings.SearchHistory ?? [])
		{
			var item = new MenuFlyoutItem { Text = query, Tag = query };
			item.Click += SearchHistoryMenuItem_Click;
			history.Items.Add(item);
		}
		if (history.Items.Count is 0)
		{
			history.Items.Add(new MenuFlyoutItem { Text = GetResource("NoSearchHistoryMenuItem"), IsEnabled = false });
		}
		flyout.Items.Add(history);

		if ((currentSettings.SearchHistory?.Length ?? 0) > 0 || (currentSettings.SavedSearches?.Length ?? 0) > 0)
		{
			flyout.Items.Add(new MenuFlyoutSeparator());
			if ((currentSettings.SearchHistory?.Length ?? 0) > 0)
			{
				var clearHistory = new MenuFlyoutItem { Text = GetResource("ClearSearchHistoryMenuItem") };
				clearHistory.Click += ClearSearchHistoryMenuItem_Click;
				flyout.Items.Add(clearHistory);
			}
			if ((currentSettings.SavedSearches?.Length ?? 0) > 0)
			{
				var clearSaved = new MenuFlyoutItem { Text = GetResource("ClearSavedSearchesMenuItem") };
				clearSaved.Click += ClearSavedSearchesMenuItem_Click;
				flyout.Items.Add(clearSaved);
			}
		}

		flyout.ShowAt(SearchOptionsButton);
	}

	private async void SaveCurrentSearchMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is not DirectoryBrowserViewModel browser || string.IsNullOrWhiteSpace(SearchBox.Text))
		{
			return;
		}

		string query = SearchBox.Text.Trim();
		var input = new TextBox
		{
			PlaceholderText = GetResource("SavedSearchNamePlaceholder"),
			Text = query.Length <= 48 ? query : query[..48],
		};
		ContentDialog dialog = CreateTextInputDialog("SaveSearchDialogTitle", "SaveButtonText", input);
		if (await dialog.ShowAsync() is not ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text))
		{
			return;
		}

		var savedSearch = new SavedSearch(input.Text.Trim(), query, browser.CurrentPath);
		SavedSearch[] savedSearches = new[] { savedSearch }
			.Concat(currentSettings.SavedSearches ?? [])
			.Where((item, index) => index is 0 || !string.Equals(item.Name, savedSearch.Name, StringComparison.CurrentCultureIgnoreCase))
			.Take(20)
			.ToArray();
		try
		{
			await PersistSettingsAsync(currentSettings with { SavedSearches = savedSearches });
			browser.StatusText = GetResource("SearchSavedMessage");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowErrorAsync(string.IsNullOrWhiteSpace(ex.Message) ? GetResource("SaveSettingsErrorMessage") : ex.Message);
		}
	}

	private async void SavedSearchMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not MenuFlyoutItem { Tag: SavedSearch savedSearch } || Browser is not DirectoryBrowserViewModel browser)
		{
			return;
		}

		if (Directory.Exists(savedSearch.RootPath) && !string.Equals(browser.CurrentPath, savedSearch.RootPath, StringComparison.Ordinal))
		{
			await browser.NavigateAsync(savedSearch.RootPath);
		}
		SearchBox.Text = savedSearch.Query;
		SearchBox.Focus(FocusState.Keyboard);
	}

	private void SearchHistoryMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (sender is MenuFlyoutItem { Tag: string query })
		{
			SearchBox.Text = query;
			SearchBox.Focus(FocusState.Keyboard);
		}
	}

	private async void ClearSearchHistoryMenuItem_Click(object sender, RoutedEventArgs e)
	{
		await ClearSearchSettingsAsync(clearHistory: true);
	}

	private async void ClearSavedSearchesMenuItem_Click(object sender, RoutedEventArgs e)
	{
		await ClearSearchSettingsAsync(clearHistory: false);
	}

	private async Task ClearSearchSettingsAsync(bool clearHistory)
	{
		try
		{
			AppSettings settings = clearHistory
				? currentSettings with { SearchHistory = [] }
				: currentSettings with { SavedSearches = [] };
			await PersistSettingsAsync(settings);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowErrorAsync(string.IsNullOrWhiteSpace(ex.Message) ? GetResource("SaveSettingsErrorMessage") : ex.Message);
		}
	}

	private async Task RecordSearchHistoryAsync(string query)
	{
		string[] history = new[] { query }
			.Concat(currentSettings.SearchHistory ?? [])
			.Distinct(StringComparer.CurrentCultureIgnoreCase)
			.Take(20)
			.ToArray();
		try
		{
			await PersistSettingsAsync(currentSettings with { SearchHistory = history });
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	private async void Items_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (GetBrowserForItemsControl(sender) is DirectoryBrowserViewModel browser)
		{
			ActivateBrowser(browser, sender as FrameworkElement);
		}

		if (e.Key is not VirtualKey.Space || selectedItems is not [LocalFileSystemItem item])
		{
			return;
		}

		e.Handled = true;
		try
		{
			await WorkspaceService.PreviewAsync(item.Path);
		}
		catch (IOException)
		{
			await ShowErrorAsync(GetResource("QuickLookErrorMessage"));
		}
	}

	private async void NewButton_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is null)
		{
			return;
		}

		var input = new TextBox
		{
			Text = GetResource("NewFolderDefaultName"),
		};
		var dialog = CreateTextInputDialog("CreateFolderDialogTitle", "CreateButtonText", input);

		if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
		{
			return;
		}

		try
		{
			string path = await FileOperationService.CreateFolderAsync(Browser.CurrentPath, input.Text.Trim());
			RecordCreatedItemHistory(path, isDirectory: true);
			await Browser.RefreshAsync();
		}
		catch (FileOperationException ex)
		{
			await ShowErrorAsync(GetFileOperationError(ex));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowErrorAsync(ex.Message);
		}
	}

	private async void RenameButton_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is null || selectedItems.Count is 0)
		{
			return;
		}
		if (selectedItems.Count > 1)
		{
			await BulkRenameAsync();
			return;
		}

		LocalFileSystemItem item = selectedItems[0];

		var input = new TextBox
		{
			Text = item.Name,
		};
		var dialog = CreateTextInputDialog("RenameDialogTitle", "RenameButtonText", input);

		if (await dialog.ShowAsync() is not ContentDialogResult.Primary || string.Equals(input.Text.Trim(), item.Name, StringComparison.Ordinal))
		{
			return;
		}

		try
		{
			FilePathRename rename = await FileRenameService.RenameAsync(item.Path, input.Text.Trim());
			RecordRenameHistory([rename]);
			await Browser.RefreshAsync();
		}
		catch (FileOperationException ex)
		{
			await ShowErrorAsync(GetFileOperationError(ex));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowErrorAsync(ex.Message);
		}
	}

	private async Task BulkRenameAsync()
	{
		if (Browser is not DirectoryBrowserViewModel targetBrowser || selectedItems.Count < 2)
		{
			return;
		}

		var input = new TextBox { PlaceholderText = GetResource("BulkRenamePlaceholder") };
		var content = new StackPanel { Spacing = 12, MinWidth = 420 };
		content.Children.Add(new TextBlock
		{
			Text = string.Format(GetResource("BulkRenameDescriptionFormat"), selectedItems.Count),
			TextWrapping = TextWrapping.Wrap,
		});
		content.Children.Add(input);
		var dialog = new ContentDialog
		{
			Title = GetResource("BulkRenameDialogTitle"),
			Content = content,
			PrimaryButtonText = GetResource("RenameButtonText"),
			CloseButtonText = GetResource("CancelButtonText"),
			DefaultButton = ContentDialogButton.Primary,
			IsPrimaryButtonEnabled = false,
			XamlRoot = XamlRoot,
		};
		input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = FileRenameService.IsValidBaseName(input.Text.Trim());
		if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
		{
			return;
		}

		IReadOnlyList<LocalFileSystemItem> items = selectedItems;
		try
		{
			FilePathRename[] renames = await FileRenameService.BulkRenameAsync(
				items.Select(static item => item.Path).ToArray(),
				input.Text.Trim());
			RecordRenameHistory(renames);
			await targetBrowser.ReloadAsync();
			targetBrowser.StatusText = string.Format(GetResource("BulkRenameCompletedFormat"), renames.Length);
		}
		catch (FileOperationException ex)
		{
			await ShowErrorAsync(GetFileOperationError(ex));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowErrorAsync(ex.Message);
		}
	}

	private void RecordRenameHistory(FilePathRename[] renames)
	{
		if (renames.Length is 0)
		{
			return;
		}

		undoHistory.Push(new(renames));
		DiscardRedoHistory();
		UpdateCommandStates();
	}

	private void RecordCreatedItemHistory(string path, bool isDirectory)
	{
		undoHistory.Push(new([], path, isDirectory));
		DiscardRedoHistory();
		UpdateCommandStates();
	}

	private void RecordSymbolicLinkHistory(CreatedSymbolicLink[] links)
	{
		if (links.Length is 0)
		{
			return;
		}
		undoHistory.Push(new([], SymbolicLinks: links));
		DiscardRedoHistory();
		UpdateCommandStates();
	}

	private void RecordTransferHistory(FileTransferMode mode, FileTransferRootResult[] roots)
	{
		if (roots.Length is 0)
		{
			return;
		}

		undoHistory.Push(new([], Transfer: new(mode, roots)));
		DiscardRedoHistory();
		_ = PersistTransferHistoryJournalAsync();
		UpdateCommandStates();
	}

	private void RecordTrashHistory(IReadOnlyList<TrashedItemResult> items)
	{
		if (items.Count is 0)
		{
			return;
		}

		undoHistory.Push(new([], Trash: new(items)));
		DiscardRedoHistory();
		UpdateCommandStates();
	}

	private void DiscardRedoHistory()
	{
		if (redoHistory.Count is 0)
		{
			return;
		}

		FileTransferHistoryEntry[] discardedTransfers = redoHistory
			.Select(static entry => entry.Transfer)
			.OfType<FileTransferHistoryEntry>()
			.ToArray();
		redoHistory.Clear();
		_ = CleanupDiscardedTransferHistoryAsync(discardedTransfers);
	}

	private async Task CleanupDiscardedTransferHistoryAsync(FileTransferHistoryEntry[] discardedTransfers)
	{
		await FileTransferHistoryService.CleanupStagingAsync(discardedTransfers);
		await PersistTransferHistoryJournalAsync();
	}

	private Task PersistTransferHistoryJournalAsync()
	{
		FileTransferHistoryEntry[] transfers = undoHistory
			.Concat(redoHistory)
			.Select(static entry => entry.Transfer)
			.OfType<FileTransferHistoryEntry>()
			.ToArray();
		return FileTransferHistoryService.PersistStagingJournalAsync(transfers);
	}

	private async void UndoButton_Click(object sender, RoutedEventArgs e)
	{
		await ReplayHistoryAsync(isUndo: true);
	}

	private async void RedoButton_Click(object sender, RoutedEventArgs e)
	{
		await ReplayHistoryAsync(isUndo: false);
	}

	private async void UndoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (undoHistory.Count > 0 && !isHistoryOperationRunning && fileTransferCancellation is null)
		{
			args.Handled = true;
			await ReplayHistoryAsync(isUndo: true);
		}
	}

	private async void RedoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (redoHistory.Count > 0 && !isHistoryOperationRunning && fileTransferCancellation is null)
		{
			args.Handled = true;
			await ReplayHistoryAsync(isUndo: false);
		}
	}

	private async Task ReplayHistoryAsync(bool isUndo)
	{
		Stack<FileOperationHistoryEntry> source = isUndo ? undoHistory : redoHistory;
		Stack<FileOperationHistoryEntry> destination = isUndo ? redoHistory : undoHistory;
		if (source.Count is 0 || isHistoryOperationRunning || fileTransferCancellation is not null)
		{
			return;
		}

		FileOperationHistoryEntry historyEntry = source.Peek();
		isHistoryOperationRunning = true;
		UpdateCommandStates();
		try
		{
			if (historyEntry.Transfer is FileTransferHistoryEntry transfer)
			{
				await FileTransferHistoryService.ReplayAsync(transfer, isUndo);
			}
			else if (historyEntry.Trash is FileTrashHistoryEntry trash)
			{
				await FileTrashHistoryService.ReplayAsync(trash, isUndo);
			}
			else if (historyEntry.SymbolicLinks is CreatedSymbolicLink[] links)
			{
				await FileOperationService.ReplaySymbolicLinksAsync(links, isUndo);
			}
			else if (historyEntry.CreatedPath is string createdPath)
			{
				await ReplayCreatedItemAsync(createdPath, historyEntry.CreatedDirectory, isUndo);
			}
			else
			{
				FilePathRename[] operation = isUndo
					? historyEntry.Renames.Select(static rename => new FilePathRename(rename.DestinationPath, rename.SourcePath)).ToArray()
					: historyEntry.Renames;
				await FileRenameService.RenamePathsAsync(operation);
			}
			source.Pop();
			destination.Push(historyEntry);
			await PersistTransferHistoryJournalAsync();
			if (Browser is DirectoryBrowserViewModel browser)
			{
				await browser.ReloadAsync();
				browser.StatusText = GetResource(isUndo ? "UndoCompletedMessage" : "RedoCompletedMessage");
			}
		}
		catch (FileOperationException ex)
		{
			await ShowErrorAsync(GetFileOperationError(ex));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowErrorAsync(string.IsNullOrWhiteSpace(ex.Message) ? GetResource("UnknownFileOperationErrorMessage") : ex.Message);
		}
		finally
		{
			isHistoryOperationRunning = false;
			UpdateCommandStates();
		}
	}

	private Task ReplayCreatedItemAsync(string path, bool isDirectory, bool isUndo)
	{
		return Task.Run(() =>
		{
			if (isUndo)
			{
				if (isDirectory)
				{
					if (!Directory.Exists(path))
					{
						throw new FileOperationException(FileOperationError.ItemNotFound, Path.GetFileName(path));
					}
					if (Directory.EnumerateFileSystemEntries(path).Any())
					{
						throw new IOException(GetResource("CreatedItemChangedUndoErrorMessage"));
					}
					Directory.Delete(path);
				}
				else
				{
					if (!File.Exists(path))
					{
						throw new FileOperationException(FileOperationError.ItemNotFound, Path.GetFileName(path));
					}
					if (new FileInfo(path).Length is not 0)
					{
						throw new IOException(GetResource("CreatedItemChangedUndoErrorMessage"));
					}
					File.Delete(path);
				}
				return;
			}

			if (File.Exists(path) || Directory.Exists(path))
			{
				throw new FileOperationException(FileOperationError.AlreadyExists, Path.GetFileName(path));
			}
			if (isDirectory)
			{
				Directory.CreateDirectory(path);
			}
			else
			{
				using (File.Create(path))
				{
				}
			}
		});
	}

	private async void DeleteButton_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is null || selectedItems.Count is 0)
		{
			return;
		}

		var dialog = new ContentDialog
		{
			Title = GetResource("MoveToTrashDialogTitle"),
			Content = string.Format(GetResource("MoveToTrashDialogMessageFormat"), selectedItems.Count),
			PrimaryButtonText = GetResource("MoveToTrashButtonText"),
			CloseButtonText = GetResource("CancelButtonText"),
			DefaultButton = ContentDialogButton.Close,
			XamlRoot = XamlRoot,
		};

		if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
		{
			return;
		}

		try
		{
			IReadOnlyList<TrashedItemResult> results = await WorkspaceService.MoveToTrashAsync(
				selectedItems.Select(static item => item.Path).ToArray());
			RecordTrashHistory(results);
		}
		catch (TrashOperationPartialException ex)
		{
			RecordTrashHistory(ex.CompletedItems);
			await ShowErrorAsync(string.IsNullOrEmpty(ex.Message) ? GetResource("MoveToTrashErrorMessage") : ex.Message);
		}
		catch (IOException ex)
		{
			await ShowErrorAsync(string.IsNullOrEmpty(ex.Message) ? GetResource("MoveToTrashErrorMessage") : ex.Message);
		}
		finally
		{
			await Browser.RefreshAsync();
		}
	}

	private async void PermanentDeleteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (selectedItems.Count > 0 && fileTransferCancellation is null)
		{
			args.Handled = true;
			await DeletePermanentlyAsync();
		}
	}

	private async Task DeletePermanentlyAsync()
	{
		if (Browser is not DirectoryBrowserViewModel targetBrowser || selectedItems.Count is 0 || fileTransferCancellation is not null)
		{
			return;
		}

		IReadOnlyList<LocalFileSystemItem> items = selectedItems;
		var dialog = new ContentDialog
		{
			Title = GetResource("PermanentDeleteDialogTitle"),
			Content = string.Format(GetResource("PermanentDeleteDialogMessageFormat"), items.Count),
			PrimaryButtonText = GetResource("PermanentDeleteButtonText"),
			CloseButtonText = GetResource("CancelButtonText"),
			DefaultButton = ContentDialogButton.Close,
			XamlRoot = XamlRoot,
		};

		if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
		{
			return;
		}

		try
		{
			await FileOperationService.DeletePermanentlyAsync(items.Select(static item => item.Path).ToArray());
			targetBrowser.StatusText = string.Format(GetResource("PermanentDeleteCompletedFormat"), items.Count);
		}
		catch (PermanentDeletePartialException ex)
		{
			await ShowErrorAsync(string.Format(
				GetResource("PermanentDeletePartialErrorFormat"),
				ex.CompletedPaths.Count,
				Path.GetFileName(ex.FailedPath),
				ex.Message));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowErrorAsync(string.IsNullOrEmpty(ex.Message) ? GetResource("PermanentDeleteErrorMessage") : ex.Message);
		}
		finally
		{
			await targetBrowser.RefreshAsync();
		}
	}

	private async void RevealButton_Click(object sender, RoutedEventArgs e)
	{
		if (selectedItems is not [LocalFileSystemItem item])
		{
			return;
		}

		try
		{
			await WorkspaceService.RevealAsync(item.Path);
		}
		catch (IOException)
		{
			await ShowErrorAsync(GetResource("RevealItemErrorMessage"));
		}
	}

	private async void NewTextFileButton_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is null)
		{
			return;
		}

		var input = new TextBox
		{
			Text = GetResource("NewTextFileDefaultName"),
		};
		var dialog = CreateTextInputDialog("CreateTextFileDialogTitle", "CreateButtonText", input);
		if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
		{
			return;
		}

		try
		{
			string path = await FileOperationService.CreateFileAsync(Browser.CurrentPath, input.Text.Trim());
			RecordCreatedItemHistory(path, isDirectory: false);
			await Browser.RefreshAsync();
		}
		catch (FileOperationException ex)
		{
			await ShowErrorAsync(GetFileOperationError(ex));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowErrorAsync(ex.Message);
		}
	}

	private async void ShareButton_Click(object sender, RoutedEventArgs e)
	{
		if (selectedItems.Count is 0)
		{
			return;
		}

		try
		{
			await WorkspaceService.ShareAsync(selectedItems.Select(static item => item.Path).ToArray());
		}
		catch (IOException ex)
		{
			await ShowErrorAsync(string.IsNullOrEmpty(ex.Message) ? GetResource("ShareItemsErrorMessage") : ex.Message);
		}
	}

	private async void SettingsButton_Click(object sender, RoutedEventArgs e)
	{
		var themePicker = new ComboBox
		{
			ItemsSource = new[]
			{
				GetResource("ThemeSystemOption"),
				GetResource("ThemeLightOption"),
				GetResource("ThemeDarkOption"),
			},
			SelectedIndex = (int)currentSettings.Theme,
			HorizontalAlignment = HorizontalAlignment.Stretch,
		};
		var languagePicker = new ComboBox
		{
			ItemsSource = new[]
			{
				GetResource("LanguageSystemOption"),
				"English",
				"简体中文",
			},
			SelectedIndex = (int)currentSettings.Language,
			HorizontalAlignment = HorizontalAlignment.Stretch,
		};
		var terminalPicker = new ComboBox
		{
			ItemsSource = new[] { "Terminal", "iTerm2", "Warp", "kitty", "Alacritty", "WezTerm" },
			SelectedIndex = (int)currentSettings.Terminal,
			HorizontalAlignment = HorizontalAlignment.Stretch,
		};
		var showHiddenToggle = new ToggleSwitch
		{
			Header = GetResource("ShowHiddenFilesSetting"),
			IsOn = currentSettings.ShowHiddenFiles,
			OnContent = GetResource("ToggleOnText"),
			OffContent = GetResource("ToggleOffText"),
		};
		var defaultGridToggle = new ToggleSwitch
		{
			Header = GetResource("DefaultGridViewSetting"),
			IsOn = currentSettings.UseGridViewForNewTabs,
			OnContent = GetResource("ToggleOnText"),
			OffContent = GetResource("ToggleOffText"),
		};
		var reverseTabScrollToggle = new ToggleSwitch
		{
			Header = GetResource("ReverseTabScrollDirectionSetting"),
			IsOn = currentSettings.ReverseTabScrollDirection,
			OnContent = GetResource("ToggleOnText"),
			OffContent = GetResource("ToggleOffText"),
		};
		var content = new StackPanel
		{
			Spacing = 16,
			MinWidth = 380,
		};
		content.Children.Add(new TextBlock { Text = GetResource("ThemeSettingLabel") });
		content.Children.Add(themePicker);
		content.Children.Add(new TextBlock { Text = GetResource("LanguageSettingLabel") });
		content.Children.Add(languagePicker);
		content.Children.Add(new TextBlock { Text = GetResource("TerminalSettingLabel") });
		content.Children.Add(terminalPicker);
		content.Children.Add(showHiddenToggle);
		content.Children.Add(defaultGridToggle);
		content.Children.Add(reverseTabScrollToggle);
		SidebarLocationOption[] defaultSidebarLocations = ViewModel.GetDefaultSidebarLocations();
		var hiddenSidebarLocations = (currentSettings.HiddenDefaultSidebarLocations ?? []).ToHashSet(StringComparer.Ordinal);
		CheckBox[] defaultLocationToggles = defaultSidebarLocations.Select(location => new CheckBox
		{
			Content = location.Name,
			IsChecked = !hiddenSidebarLocations.Contains(location.Id),
			Tag = location.Id,
		}).ToArray();
		content.Children.Add(new TextBlock { Text = GetResource("SidebarLocationsSettingLabel") });
		var defaultLocationList = new StackPanel { Spacing = 4 };
		foreach (CheckBox locationToggle in defaultLocationToggles)
		{
			defaultLocationList.Children.Add(locationToggle);
		}
		content.Children.Add(defaultLocationList);

		var customLocationToggles = new List<CheckBox>();
		var customLocationList = new StackPanel { Spacing = 4 };
		foreach (string favoritePath in currentSettings.FavoritePaths ?? [])
		{
			var toggle = new CheckBox
			{
				Content = new TextBlock { Text = favoritePath, TextWrapping = TextWrapping.Wrap, MaxWidth = 330 },
				IsChecked = true,
				Tag = favoritePath,
			};
			customLocationToggles.Add(toggle);
			customLocationList.Children.Add(toggle);
		}
		var pendingLocationGrants = new List<FolderAccessGrant>();
		var addLocationButton = new Button
		{
			Content = GetResource("AddSidebarLocationButtonText"),
			HorizontalAlignment = HorizontalAlignment.Left,
		};
		addLocationButton.Click += async (_, _) =>
		{
			FolderAccessGrant? grant = await AccessGrantService.PickFolderAsync(
				Browser?.CurrentPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
			if (grant is null || customLocationToggles.Any(toggle => string.Equals(toggle.Tag as string, grant.Path, StringComparison.OrdinalIgnoreCase)))
			{
				return;
			}
			var toggle = new CheckBox
			{
				Content = new TextBlock { Text = grant.Path, TextWrapping = TextWrapping.Wrap, MaxWidth = 330 },
				IsChecked = true,
				Tag = grant.Path,
			};
			customLocationToggles.Add(toggle);
			customLocationList.Children.Add(toggle);
			pendingLocationGrants.Add(grant);
		};
		content.Children.Add(new TextBlock { Text = GetResource("CustomSidebarLocationsSettingLabel") });
		content.Children.Add(customLocationList);
		content.Children.Add(addLocationButton);
		FolderAccessGrant[] existingGrants = currentSettings.AccessGrants ?? [];
		CheckBox[] grantToggles = existingGrants.Select(grant => new CheckBox
		{
			Content = new TextBlock
			{
				Text = grant.Path,
				TextWrapping = TextWrapping.Wrap,
				MaxWidth = 330,
			},
			IsChecked = true,
			Tag = grant,
			HorizontalAlignment = HorizontalAlignment.Stretch,
		}).ToArray();
		if (grantToggles.Length > 0)
		{
			content.Children.Add(new TextBlock { Text = GetResource("FolderAccessSettingLabel") });
			var grantList = new StackPanel { Spacing = 6 };
			foreach (CheckBox grantToggle in grantToggles)
			{
				grantList.Children.Add(grantToggle);
			}
			content.Children.Add(new ScrollViewer
			{
				MaxHeight = 160,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				Content = grantList,
			});
			content.Children.Add(new TextBlock
			{
				Text = GetResource("FolderAccessSettingDescription"),
				TextWrapping = TextWrapping.Wrap,
				Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
			});
		}

		var dialog = new ContentDialog
		{
			Title = GetResource("SettingsDialogTitle"),
			Content = new ScrollViewer
			{
				MaxHeight = 560,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
				HorizontalScrollMode = ScrollMode.Disabled,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				VerticalScrollMode = ScrollMode.Enabled,
				Content = content,
			},
			PrimaryButtonText = GetResource("SaveButtonText"),
			CloseButtonText = GetResource("CancelButtonText"),
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = XamlRoot,
		};
		if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
		{
			foreach (FolderAccessGrant pendingGrant in pendingLocationGrants)
			{
				AccessGrantService.Revoke(pendingGrant.Path);
			}
			return;
		}

		AppLanguagePreference previousLanguage = currentSettings.Language;
		var newSettings = currentSettings with
		{
			Theme = (AppThemePreference)Math.Clamp(themePicker.SelectedIndex, 0, 2),
			Language = (AppLanguagePreference)Math.Clamp(languagePicker.SelectedIndex, 0, 2),
			Terminal = (TerminalPreference)Math.Clamp(terminalPicker.SelectedIndex, 0, 5),
			ShowHiddenFiles = showHiddenToggle.IsOn,
			UseGridViewForNewTabs = defaultGridToggle.IsOn,
			ReverseTabScrollDirection = reverseTabScrollToggle.IsOn,
			HiddenDefaultSidebarLocations = defaultLocationToggles
				.Where(static toggle => toggle.IsChecked is not true)
				.Select(static toggle => (string)toggle.Tag)
				.Order(StringComparer.Ordinal)
				.ToArray(),
			FavoritePaths = customLocationToggles
				.Where(static toggle => toggle.IsChecked is true)
				.Select(static toggle => (string)toggle.Tag)
				.Order(StringComparer.CurrentCultureIgnoreCase)
				.ToArray(),
			AccessGrants = grantToggles
				.Where(static toggle => toggle.IsChecked is true)
				.Select(static toggle => (FolderAccessGrant)toggle.Tag)
				.Concat(pendingLocationGrants)
				.DistinctBy(static grant => grant.Path, StringComparer.Ordinal)
				.ToArray(),
		};
		try
		{
			newSettings = await PersistSettingsAsync(newSettings);
			foreach (FolderAccessGrant revokedGrant in existingGrants.ExceptBy(
				newSettings.AccessGrants?.Select(static grant => grant.Path) ?? [],
				static grant => grant.Path,
				StringComparer.Ordinal))
			{
				AccessGrantService.Revoke(revokedGrant.Path);
			}
			ViewModel.ApplySettings(newSettings);
			ApplyTheme(newSettings.Theme);
			if (newSettings.Language != previousLanguage)
			{
				AppLanguageManager.Apply(newSettings.Language);
				var restartDialog = new ContentDialog
				{
					Title = GetResource("LanguageRestartTitle"),
					Content = GetResource("LanguageRestartMessage"),
					CloseButtonText = GetResource("CloseButtonText"),
					XamlRoot = XamlRoot,
				};
				await restartDialog.ShowAsync();
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			foreach (FolderAccessGrant pendingGrant in pendingLocationGrants)
			{
				AccessGrantService.Revoke(pendingGrant.Path);
			}
			await ShowErrorAsync(string.IsNullOrEmpty(ex.Message) ? GetResource("SaveSettingsErrorMessage") : ex.Message);
		}
	}

	private async void ConnectServerButton_Click(object sender, RoutedEventArgs e)
	{
		var input = new TextBox
		{
			PlaceholderText = GetResource("ConnectServerAddressPlaceholder"),
			Text = currentSettings.RecentServers?.FirstOrDefault() ?? "smb://",
		};
		ContentDialog dialog = CreateTextInputDialog("ConnectServerDialogTitle", "ConnectButtonText", input);
		if (await dialog.ShowAsync() is ContentDialogResult.Primary)
		{
			await ConnectToServerAsync(input.Text);
		}
	}

	private async Task ConnectToServerAsync(string address)
	{
		if (Browser is not DirectoryBrowserViewModel targetBrowser)
		{
			return;
		}
		if (!NetworkServerAddress.TryNormalize(address, out string normalizedAddress, out NetworkServerAddressError error))
		{
			await ShowErrorAsync(GetNetworkAddressError(error));
			return;
		}

		targetBrowser.StatusText = GetResource("ConnectingServerMessage");
		isConnectingServer = true;
		UpdateCommandStates();
		try
		{
			NetworkConnectionResult result = await WorkspaceService.ConnectServerAsync(normalizedAddress);
			string[] recentServers = new[] { normalizedAddress }
				.Concat(currentSettings.RecentServers ?? [])
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.Take(10)
				.ToArray();
			AppSettings newSettings = currentSettings with { RecentServers = recentServers };
			try
			{
				newSettings = await PersistSettingsAsync(newSettings);
				ViewModel.ApplySettings(newSettings);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				await ShowErrorAsync(string.IsNullOrWhiteSpace(ex.Message) ? GetResource("SaveSettingsErrorMessage") : ex.Message);
			}

			string? mountPath = result.MountPaths.FirstOrDefault(Directory.Exists);
			if (mountPath is not null)
			{
				await targetBrowser.NavigateAsync(mountPath);
			}
			else
			{
				targetBrowser.StatusText = GetResource(result.OpenedExternally ? "ServerOpenedExternallyMessage" : "ServerConnectedMessage");
			}
		}
		catch (OperationCanceledException)
		{
			targetBrowser.StatusText = GetResource("ServerConnectionCanceledMessage");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowErrorAsync(string.IsNullOrWhiteSpace(ex.Message) ? GetResource("ConnectServerErrorMessage") : ex.Message);
			targetBrowser.StatusText = GetResource("ConnectServerErrorMessage");
		}
		finally
		{
			isConnectingServer = false;
			UpdateCommandStates();
		}
	}

	private string GetNetworkAddressError(NetworkServerAddressError error)
	{
		return GetResource(error switch
		{
			NetworkServerAddressError.Required => "ServerAddressRequiredMessage",
			NetworkServerAddressError.UnsupportedScheme => "ServerSchemeUnsupportedMessage",
			NetworkServerAddressError.MissingHost => "ServerHostRequiredMessage",
			NetworkServerAddressError.CredentialsNotAllowed => "ServerCredentialsNotAllowedMessage",
			NetworkServerAddressError.QueryOrFragmentNotAllowed => "ServerQueryNotAllowedMessage",
			_ => "ServerAddressInvalidMessage",
		});
	}

	private void ApplyTheme(AppThemePreference theme)
	{
		RequestedTheme = theme switch
		{
			AppThemePreference.Light => ElementTheme.Light,
			AppThemePreference.Dark => ElementTheme.Dark,
			_ => ElementTheme.Default,
		};
		if (PrimaryPaneBorder is not null)
		{
			UpdatePaneVisuals();
		}
	}

	internal void ApplyAccessibilityDisplayOptions(MacOSAccessibilityDisplayOptions options)
	{
		accessibilityDisplayOptions = options;
		bool increaseContrast = options.HasFlag(MacOSAccessibilityDisplayOptions.IncreaseContrast);
		double borderWidth = increaseContrast ? 2 : 1;
		AddressBarBorder.BorderThickness = new Thickness(borderWidth);
		CommandToolbarBorder.BorderThickness = new Thickness(borderWidth);
		PreviewPaneBorder.BorderThickness = new Thickness(borderWidth);
		SidebarBorder.BorderThickness = new Thickness(0, borderWidth, borderWidth, 0);
		SidebarDividerLine.Width = borderWidth;
		SplitDividerLine.Width = borderWidth;
		PrimaryEmptyFolderIcon.Opacity = increaseContrast ? 0.52 : 0.32;
		SecondaryEmptyFolderIcon.Opacity = increaseContrast ? 0.52 : 0.32;
		PrimaryNoResultsIcon.Opacity = increaseContrast ? 0.52 : 0.32;
		SecondaryNoResultsIcon.Opacity = increaseContrast ? 0.52 : 0.32;

		if (options.HasFlag(MacOSAccessibilityDisplayOptions.ReduceTransparency))
		{
			TitleBarBackground.Opacity = 1;
			SidebarBorder.Opacity = 1;
			CommandToolbarBorder.Opacity = 1;
			PrimaryPaneBorder.Opacity = 1;
			SecondaryPaneBorder.Opacity = 1;
			PreviewPaneBorder.Opacity = 1;
		}

		UpdatePaneVisuals();
	}

	private async void PropertiesButton_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is not DirectoryBrowserViewModel targetBrowser || selectedItems.Count is 0 || fileTransferCancellation is not null)
		{
			return;
		}

		IReadOnlyList<LocalFileSystemItem> items = selectedItems;
		targetBrowser.StatusText = GetResource("CalculatingPropertiesMessage");
		PropertiesButton.IsEnabled = false;
		try
		{
			FilePropertiesSummary summary = await FilePropertiesService.GetSummaryAsync(items.Select(static item => item.Path).ToArray());
			FrameworkElement content = CreatePropertiesContent(
				summary,
				out TextBox? permissionsBox,
				out TextBox? tagsBox,
				out CheckBox? hiddenBox,
				out CheckBox? lockedBox,
				out TextBox? ownerBox,
				out TextBox? groupBox,
				out TextBox? aclBox);
			bool canEdit = summary.Path is not null && permissionsBox is not null && tagsBox is not null &&
				hiddenBox is not null && lockedBox is not null && ownerBox is not null && groupBox is not null && aclBox is not null;
			var dialog = new ContentDialog
			{
				Title = GetResource("PropertiesDialogTitle"),
				Content = content,
				PrimaryButtonText = canEdit ? GetResource("SaveButtonText") : string.Empty,
				CloseButtonText = GetResource("CloseButtonText"),
				DefaultButton = canEdit ? ContentDialogButton.Primary : ContentDialogButton.Close,
				IsPrimaryButtonEnabled = canEdit,
				XamlRoot = XamlRoot,
			};
			if (canEdit)
			{
				void ValidateEditor(object? sender, TextChangedEventArgs args) =>
					dialog.IsPrimaryButtonEnabled = TryParseUnixMode(permissionsBox!.Text, out _) &&
						AreFinderTagsValid(tagsBox!.Text) && AreSecurityFieldsValid(ownerBox!.Text, groupBox!.Text, aclBox!.Text);
				permissionsBox!.TextChanged += ValidateEditor;
				tagsBox!.TextChanged += ValidateEditor;
				ownerBox!.TextChanged += ValidateEditor;
				groupBox!.TextChanged += ValidateEditor;
				aclBox!.TextChanged += ValidateEditor;
			}

			if (await dialog.ShowAsync() is ContentDialogResult.Primary &&
				summary.Path is not null && permissionsBox is not null && tagsBox is not null &&
				hiddenBox is not null && lockedBox is not null && ownerBox is not null && groupBox is not null && aclBox is not null &&
				TryParseUnixMode(permissionsBox.Text, out UnixFileMode unixMode))
			{
				await FilePropertiesService.UpdateAsync(
					summary.Path,
					new FilePropertyUpdate(
						unixMode,
						ParseFinderTags(tagsBox.Text),
						hiddenBox.IsChecked is true,
						lockedBox.IsChecked is true,
						ownerBox.Text.Trim(),
						groupBox.Text.Trim(),
						aclBox.Text.Trim()));
				targetBrowser.StatusText = GetResource("PropertiesSavedMessage");
				await targetBrowser.RefreshAsync();
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowErrorAsync(ex.Message);
		}
		finally
		{
			targetBrowser.UpdateSelection(items);
			UpdateCommandStates();
		}
	}

	private FrameworkElement CreatePropertiesContent(
		FilePropertiesSummary summary,
		out TextBox? permissionsBox,
		out TextBox? tagsBox,
		out CheckBox? hiddenBox,
		out CheckBox? lockedBox,
		out TextBox? ownerBox,
		out TextBox? groupBox,
		out TextBox? aclBox)
	{
		permissionsBox = null;
		tagsBox = null;
		hiddenBox = null;
		lockedBox = null;
		ownerBox = null;
		groupBox = null;
		aclBox = null;
		var panel = new StackPanel
		{
			Spacing = 10,
			MinWidth = 460,
		};
		AddPropertySectionHeader(panel, GetResource("GeneralPropertiesSection"));

		AddPropertyRow(
			panel,
			GetResource("PropertyNameLabel"),
			summary.Name ?? string.Format(GetResource("SelectedItemCountFormat"), summary.RootItemCount));

		if (summary.Path is not null)
		{
			AddPropertyRow(panel, GetResource("PropertyLocationLabel"), summary.Path);
		}

		AddPropertyRow(panel, GetResource("PropertySizeLabel"), LocalFileSystemItem.FormatSize(summary.TotalSize));
		AddPropertyRow(
			panel,
			GetResource("PropertyContainsLabel"),
			string.Format(GetResource("PropertyContainsFormat"), summary.FileCount, summary.FolderCount));

		if (summary.Created is not null)
		{
			AddPropertyRow(panel, GetResource("PropertyCreatedLabel"), summary.Created.Value.ToLocalTime().ToString("F"));
		}
		if (summary.Modified is not null)
		{
			AddPropertyRow(panel, GetResource("PropertyModifiedLabel"), summary.Modified.Value.ToLocalTime().ToString("F"));
		}
		if (summary.FinderTags is not null)
		{
			string tags = string.Join(", ", summary.FinderTags);
			if (summary.Path is not null && summary.LinkTarget is null)
			{
				tagsBox = AddEditablePropertyRow(panel, GetResource("PropertyFinderTagsLabel"), tags);
				tagsBox.PlaceholderText = GetResource("PropertyFinderTagsPlaceholder");
				hiddenBox = AddCheckBoxPropertyRow(panel, GetResource("PropertyHiddenLabel"), summary.IsHidden is true);
				lockedBox = AddCheckBoxPropertyRow(panel, GetResource("PropertyLockedLabel"), summary.IsLocked is true);
			}
			else
			{
				AddPropertyRow(panel, GetResource("PropertyFinderTagsLabel"), tags);
				AddPropertyRow(panel, GetResource("PropertyHiddenLabel"), GetResource(summary.IsHidden is true ? "YesText" : "NoText"));
				AddPropertyRow(panel, GetResource("PropertyLockedLabel"), GetResource(summary.IsLocked is true ? "YesText" : "NoText"));
			}
		}
		if (summary.LinkTarget is not null)
		{
			AddPropertyRow(panel, GetResource("PropertyLinkTargetLabel"), summary.LinkTarget);
		}

		if (summary.UnixMode is not null || summary.Owner is not null || summary.Group is not null)
		{
			AddPropertySectionHeader(panel, GetResource("PermissionsPropertiesSection"));
		}
		if (summary.UnixMode is not null)
		{
			string octalMode = Convert.ToString((int)summary.UnixMode.Value, 8).PadLeft(3, '0');
			if (summary.Path is not null && summary.LinkTarget is null)
			{
				permissionsBox = AddEditablePropertyRow(panel, GetResource("PropertyPermissionsLabel"), octalMode);
				permissionsBox.PlaceholderText = GetResource("PropertyPermissionsPlaceholder");
			}
			else
			{
				AddPropertyRow(panel, GetResource("PropertyPermissionsLabel"), $"{octalMode}  ({summary.UnixMode.Value})");
			}
		}
		if (summary.Owner is not null)
		{
			if (summary.Path is not null && summary.LinkTarget is null)
			{
				ownerBox = AddEditablePropertyRow(
					panel,
					string.Format(GetResource("PropertyOwnerWithIdFormat"), summary.UserId),
					summary.Owner);
			}
			else
			{
				AddPropertyRow(panel, GetResource("PropertyOwnerLabel"), $"{summary.Owner} ({summary.UserId})");
			}
		}
		if (summary.Group is not null)
		{
			if (summary.Path is not null && summary.LinkTarget is null)
			{
				groupBox = AddEditablePropertyRow(
					panel,
					string.Format(GetResource("PropertyGroupWithIdFormat"), summary.GroupId),
					summary.Group);
			}
			else
			{
				AddPropertyRow(panel, GetResource("PropertyGroupLabel"), $"{summary.Group} ({summary.GroupId})");
			}
		}
		if (summary.AccessControlList is not null)
		{
			if (summary.Path is not null && summary.LinkTarget is null)
			{
				aclBox = AddEditablePropertyRow(panel, GetResource("PropertyAclLabel"), summary.AccessControlList.Trim(), multiline: true);
				aclBox.PlaceholderText = GetResource("PropertyAclPlaceholder");
				AddPropertyHelpText(panel, GetResource("PropertySecurityHelpText"));
			}
			else
			{
				AddPropertyRow(
					panel,
					GetResource("PropertyAclLabel"),
					string.IsNullOrWhiteSpace(summary.AccessControlList)
						? GetResource("NoAccessControlEntriesText")
						: summary.AccessControlList.Trim());
			}
		}

		return new ScrollViewer
		{
			Content = panel,
			MaxHeight = 480,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
		};
	}

	private static void AddPropertySectionHeader(Panel panel, string text)
	{
		panel.Children.Add(new TextBlock
		{
			Text = text,
			FontSize = 16,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
			Margin = new Thickness(0, panel.Children.Count is 0 ? 0 : 8, 0, 2),
		});
	}

	private static CheckBox AddCheckBoxPropertyRow(Panel panel, string label, bool value)
	{
		var row = new Grid();
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
		var input = new CheckBox { IsChecked = value, HorizontalAlignment = HorizontalAlignment.Left };
		Grid.SetColumn(input, 1);
		row.Children.Add(input);
		panel.Children.Add(row);
		return input;
	}

	private static TextBox AddEditablePropertyRow(Panel panel, string label, string value, bool multiline = false)
	{
		var row = new Grid();
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
		var input = new TextBox
		{
			Text = value,
			AcceptsReturn = multiline,
			TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
			MinHeight = multiline ? 96 : 0,
			FontFamily = multiline ? new FontFamily("Menlo") : null,
		};
		Grid.SetColumn(input, 1);
		row.Children.Add(input);
		panel.Children.Add(row);
		return input;
	}

	private static void AddPropertyHelpText(Panel panel, string text)
	{
		panel.Children.Add(new TextBlock
		{
			Text = text,
			TextWrapping = TextWrapping.Wrap,
			Opacity = 0.7,
			FontSize = 12,
			Margin = new Thickness(120, -4, 0, 2),
		});
	}

	private static bool TryParseUnixMode(string value, out UnixFileMode mode)
	{
		mode = default;
		string text = value.Trim();
		if (text.Length is < 3 or > 4 || text.Any(static character => character is < '0' or > '7'))
		{
			return false;
		}

		int parsed = 0;
		foreach (char character in text)
		{
			parsed = (parsed * 8) + (character - '0');
		}
		mode = (UnixFileMode)parsed;
		return true;
	}

	private static bool AreFinderTagsValid(string value) =>
		ParseFinderTags(value).All(static tag => tag.Length <= 255 && tag.IndexOfAny(['\r', '\n']) < 0);

	private static bool AreSecurityFieldsValid(string owner, string group, string accessControlList) =>
		IsIdentityValid(owner) && IsIdentityValid(group) && accessControlList.Length <= 32_768;

	private static bool IsIdentityValid(string value) =>
		!string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 255 && value.IndexOfAny(['\r', '\n']) < 0;

	private static string[] ParseFinderTags(string value) => value
		.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
		.Distinct(StringComparer.CurrentCultureIgnoreCase)
		.ToArray();

	private static void AddPropertyRow(Panel panel, string label, string value)
	{
		var row = new Grid();
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

		var labelText = new TextBlock
		{
			Text = label,
			VerticalAlignment = VerticalAlignment.Top,
		};
		var valueText = new TextBlock
		{
			Text = value,
			TextWrapping = TextWrapping.Wrap,
		};
		Grid.SetColumn(valueText, 1);
		row.Children.Add(labelText);
		row.Children.Add(valueText);
		panel.Children.Add(row);
	}

	private async void Tabs_AddTabButtonClick(TabView sender, object args)
	{
		await ViewModel.NewTabAsync();
	}

	private async void NewTabButton_Click(object sender, RoutedEventArgs e)
	{
		await ViewModel.NewTabAsync();
	}

	private void TabHeaderCloseButton_Click(object sender, RoutedEventArgs e)
	{
		if (fileTransferCancellation is null && sender is Button { Tag: BrowserTabViewModel tab })
		{
			ViewModel.CloseTab(tab);
		}
	}

	private void TabHeaderCloseButton_Loaded(object sender, RoutedEventArgs e)
	{
		if (sender is Button button)
		{
			ConfigureIconButton(button, "CloseTabTooltip");
		}
	}

	private void TabItem_PointerEntered(object sender, PointerRoutedEventArgs e)
	{
		if (sender is TabViewItem { Tag: BrowserTabViewModel tab })
		{
			tab.IsPointerOver = true;
		}
	}

	private void TabItem_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		if (fileTransferCancellation is null &&
			e.GetCurrentPoint(this).Properties.PointerUpdateKind is Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed &&
			sender is TabViewItem { Tag: BrowserTabViewModel tab })
		{
			e.Handled = true;
			ViewModel.CloseTab(tab);
		}
	}

	private void TabItem_PointerExited(object sender, PointerRoutedEventArgs e)
	{
		if (sender is TabViewItem { Tag: BrowserTabViewModel tab })
		{
			tab.IsPointerOver = false;
		}
	}

	private void TabContextFlyout_Opening(object sender, object e)
	{
		if (sender is MenuFlyout { Items.Count: >= 10 } flyout)
		{
			bool isIdle = fileTransferCancellation is null && !isHistoryOperationRunning && !isConnectingServer;
			BrowserTabViewModel? tab = (flyout.Items[1] as MenuFlyoutItem)?.Tag as BrowserTabViewModel;
			int tabIndex = tab is null ? -1 : ViewModel.Tabs.IndexOf(tab);
			flyout.Items[0].IsEnabled = isIdle;
			flyout.Items[1].IsEnabled = isIdle && tabIndex >= 0;
			flyout.Items[2].IsEnabled = isIdle && tabIndex >= 0 && ViewModel.Tabs.Count > 1;
			flyout.Items[4].IsEnabled = isIdle && ViewModel.Tabs.Count > 1;
			flyout.Items[5].IsEnabled = isIdle && tabIndex > 0;
			flyout.Items[6].IsEnabled = isIdle && tabIndex >= 0 && tabIndex < ViewModel.Tabs.Count - 1;
			flyout.Items[7].IsEnabled = isIdle && ViewModel.Tabs.Count > 1;
			flyout.Items[9].IsEnabled = isIdle && ViewModel.CanReopenClosedTab;
		}
	}

	private async void NewTabMenuItem_Click(object sender, RoutedEventArgs e)
	{
		await ViewModel.NewTabAsync();
		UpdateCommandStates();
	}

	private async void DuplicateTabMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (sender is MenuFlyoutItem { Tag: BrowserTabViewModel tab })
		{
			await DuplicateTabAsync(tab);
		}
	}

	private async void MoveTabToNewWindowMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (sender is MenuFlyoutItem { Tag: BrowserTabViewModel tab })
		{
			await MoveTabToNewWindowAsync(tab);
		}
	}

	private async void ReopenClosedTabMenuItem_Click(object sender, RoutedEventArgs e)
	{
		await ReopenClosedTabAsync();
	}

	private void CloseTabMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (fileTransferCancellation is null && sender is MenuFlyoutItem { Tag: BrowserTabViewModel tab })
		{
			ViewModel.CloseTab(tab);
			UpdateCommandStates();
		}
	}

	private void CloseOtherTabsMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (fileTransferCancellation is null && sender is MenuFlyoutItem { Tag: BrowserTabViewModel tab })
		{
			ViewModel.CloseOtherTabs(tab);
			UpdateCommandStates();
		}
	}

	private void CloseTabsToLeftMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (fileTransferCancellation is null && sender is MenuFlyoutItem { Tag: BrowserTabViewModel tab })
		{
			ViewModel.CloseTabsToLeft(tab);
			UpdateCommandStates();
		}
	}

	private void CloseTabsToRightMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (fileTransferCancellation is null && sender is MenuFlyoutItem { Tag: BrowserTabViewModel tab })
		{
			ViewModel.CloseTabsToRight(tab);
			UpdateCommandStates();
		}
	}

	private async Task DuplicateTabAsync(BrowserTabViewModel tab)
	{
		if (fileTransferCancellation is null && !isHistoryOperationRunning && !isConnectingServer)
		{
			await ViewModel.DuplicateTabAsync(tab);
			UpdateCommandStates();
		}
	}

	private async Task MoveTabToNewWindowAsync(BrowserTabViewModel tab)
	{
		if (fileTransferCancellation is not null || isHistoryOperationRunning || isConnectingServer || ViewModel.Tabs.Count <= 1)
		{
			return;
		}

		try
		{
			if (!await ((App)Application.Current).MoveTabToNewWindowAsync(this, tab))
			{
				await ShowErrorAsync(GetResource("MoveTabToNewWindowErrorMessage"));
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			await ShowErrorAsync(GetResource("MoveTabToNewWindowErrorMessage"));
		}
	}

	private async Task ReopenClosedTabAsync()
	{
		if (fileTransferCancellation is null && !isHistoryOperationRunning && !isConnectingServer && ViewModel.CanReopenClosedTab)
		{
			await ViewModel.ReopenClosedTabAsync();
			UpdateCommandStates();
		}
	}

	private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		selectedItems = [];
		Browser?.UpdateSelection(selectedItems);
		UpdatePaneVisuals();
		UpdateCommandStates();
		DispatcherQueue.TryEnqueue(() =>
		{
			if (ViewModel.ActiveTab is not BrowserTabViewModel tab)
			{
				return;
			}
			ResetItemsScrollPosition(GetVisibleItemsControl(tab.Browser));
			if (tab.IsSplitView && tab.SecondaryBrowser is not null)
			{
				ResetItemsScrollPosition(GetVisibleItemsControl(tab.SecondaryBrowser));
			}
		});
	}

	private static void ResetItemsScrollPosition(FrameworkElement control)
	{
		if (control is ItemsView { ScrollView: ScrollView scrollView })
		{
			scrollView.ScrollTo(
				scrollView.HorizontalOffset,
				0,
				new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
		}
		else if (FindVisualDescendant<ScrollViewer>(control) is ScrollViewer scrollViewer)
		{
			scrollViewer.ChangeView(null, 0, null, disableAnimation: true);
		}
	}

	private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
	{
		if (fileTransferCancellation is null && args.Item is BrowserTabViewModel tab)
		{
			ViewModel.CloseTab(tab);
		}
	}

	private void Tabs_TabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args)
	{
		if (args.Item is BrowserTabViewModel tab)
		{
			int targetIndex = sender.TabItems.IndexOf(tab);
			if (targetIndex >= 0)
			{
				ViewModel.MoveTab(tab, targetIndex);
			}
		}
	}

	private async void Tabs_TabDroppedOutside(TabView sender, TabViewTabDroppedOutsideEventArgs args)
	{
		if (args.Item is BrowserTabViewModel tab && ViewModel.Tabs.Count > 1)
		{
			await MoveTabToNewWindowAsync(tab);
		}
	}

	private void Tabs_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
	{
		int delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
		if (delta is 0)
		{
			return;
		}

		int offset = (delta > 0) == !currentSettings.ReverseTabScrollDirection ? 1 : -1;
		SelectRelativeTab(offset, wrap: false);
		e.Handled = true;
	}

	private void Items_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
	{
		if (sender is not FrameworkElement control)
		{
			return;
		}

		int delta = e.GetCurrentPoint(control).Properties.MouseWheelDelta;
		if (delta is 0)
		{
			return;
		}

		double distance = GetAcceleratedWheelDistance(delta);
		if (control is ItemsView { ScrollView: ScrollView scrollView })
		{
			double maximumOffset = Math.Max(0, scrollView.ExtentHeight - scrollView.ViewportHeight);
			if (maximumOffset > 0)
			{
				double targetOffset = Math.Clamp(scrollView.VerticalOffset + distance, 0, maximumOffset);
				scrollView.ScrollTo(
					scrollView.HorizontalOffset,
					targetOffset,
					new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
				e.Handled = true;
			}
		}
		else if (FindVisualDescendant<ScrollViewer>(control) is ScrollViewer scrollViewer && scrollViewer.ScrollableHeight > 0)
		{
			double targetOffset = Math.Clamp(scrollViewer.VerticalOffset + distance, 0, scrollViewer.ScrollableHeight);
			scrollViewer.ChangeView(null, targetOffset, null, disableAnimation: true);
			e.Handled = true;
		}
	}

	private double GetAcceleratedWheelDistance(int delta)
	{
		long timestamp = Environment.TickCount64;
		long elapsed = timestamp - lastContentWheelTimestamp;
		contentWheelAcceleration = elapsed is > 0 and < 150
			? Math.Min(8, contentWheelAcceleration * 1.35 + (150 - elapsed) / 90d)
			: 1;
		lastContentWheelTimestamp = timestamp;
		double wheelSteps = Math.Clamp(Math.Abs(delta) / 120d, 0.35, 3);
		return -Math.Sign(delta) * 104 * wheelSteps * contentWheelAcceleration;
	}

	private void RegisterContentWheelHandler(UIElement element)
	{
		element.AddHandler(
			UIElement.PointerWheelChangedEvent,
			new PointerEventHandler(Items_PointerWheelChanged),
			handledEventsToo: true);
	}

	private bool SelectRelativeTab(int offset, bool wrap)
	{
		if (ViewModel.Tabs.Count <= 1 || ViewModel.ActiveTab is not BrowserTabViewModel activeTab)
		{
			return false;
		}

		int currentIndex = ViewModel.Tabs.IndexOf(activeTab);
		if (currentIndex < 0)
		{
			return false;
		}

		int targetIndex = wrap
			? (currentIndex + offset + ViewModel.Tabs.Count) % ViewModel.Tabs.Count
			: Math.Clamp(currentIndex + offset, 0, ViewModel.Tabs.Count - 1);
		if (targetIndex == currentIndex)
		{
			return false;
		}

		ViewModel.ActiveTab = ViewModel.Tabs[targetIndex];
		return true;
	}

	private void ViewButton_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is DirectoryBrowserViewModel browser)
		{
			SetViewMode(browser, !browser.IsGridView);
		}
	}

	private void GridViewStatusButton_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is DirectoryBrowserViewModel browser)
		{
			SetViewMode(browser, isGridView: true);
		}
	}

	private void DetailsViewStatusButton_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is DirectoryBrowserViewModel browser)
		{
			SetViewMode(browser, isGridView: false);
		}
	}

	private void SetViewMode(DirectoryBrowserViewModel browser, bool isGridView)
	{
		if (browser.IsGridView == isGridView)
		{
			return;
		}

		IReadOnlyList<LocalFileSystemItem> selection = selectedItems;
		browser.IsGridView = isGridView;
		FrameworkElement targetControl = GetVisibleItemsControl(browser);
		RestoreSelection(browser, targetControl, selection);
		ActivateBrowser(browser, targetControl);
	}

	private async void SplitViewButton_Click(object sender, RoutedEventArgs e)
	{
		if (fileTransferCancellation is not null)
		{
			return;
		}

		await ViewModel.ToggleSplitViewAsync();
		selectedItems = [];
		if (Browser is DirectoryBrowserViewModel browser)
		{
			ActivateBrowser(browser, GetVisibleItemsControl(browser));
		}
		else
		{
			UpdatePaneVisuals();
			UpdateCommandStates();
		}
	}

	private async void SplitViewAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (fileTransferCancellation is null)
		{
			args.Handled = true;
			await ViewModel.ToggleSplitViewAsync();
			selectedItems = [];
			if (Browser is DirectoryBrowserViewModel browser)
			{
				ActivateBrowser(browser, GetVisibleItemsControl(browser));
			}
		}
	}

	private void PaneBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		DirectoryBrowserViewModel? browser = sender is FrameworkElement { Tag: "Secondary" }
			? ViewModel.ActiveTab?.SecondaryBrowser
			: ViewModel.ActiveTab?.Browser;
		if (browser is not null)
		{
			ActivateBrowser(browser, GetVisibleItemsControl(browser));
		}
	}

	private void SplitDivider_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		if (ViewModel.ActiveTab?.IsSplitView is not true)
		{
			return;
		}

		isResizingSplit = SplitDivider.CapturePointer(e.Pointer);
		e.Handled = isResizingSplit;
	}

	private void SplitDivider_PointerMoved(object sender, PointerRoutedEventArgs e)
	{
		if (!isResizingSplit || ViewModel.ActiveTab is not BrowserTabViewModel tab)
		{
			return;
		}

		double availableWidth = Math.Max(0, PaneGrid.ActualWidth - SplitDividerWidth);
		if (availableWidth <= 0)
		{
			return;
		}

		double pointerX = e.GetCurrentPoint(PaneGrid).Position.X;
		tab.SplitRatio = ClampSplitRatio(pointerX / availableWidth, availableWidth);
		ApplySplitLayout();
		e.Handled = true;
	}

	private void SplitDivider_PointerReleased(object sender, PointerRoutedEventArgs e)
	{
		if (!isResizingSplit)
		{
			return;
		}

		isResizingSplit = false;
		SplitDivider.ReleasePointerCapture(e.Pointer);
		e.Handled = true;
	}

	private void SplitDivider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
	{
		isResizingSplit = false;
	}

	private void SplitDivider_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (ViewModel.ActiveTab is not BrowserTabViewModel { IsSplitView: true } tab)
		{
			return;
		}

		double availableWidth = Math.Max(0, PaneGrid.ActualWidth - SplitDividerWidth);
		if (GetSplitRatioForKey(tab.SplitRatio, availableWidth, e.Key) is double ratio)
		{
			tab.SplitRatio = ratio;
			ApplySplitLayout();
			e.Handled = true;
		}
	}

	private void PaneGrid_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		ApplySplitLayout();
	}

	private DirectoryBrowserViewModel? GetBrowserForItemsControl(object? control)
	{
		return ReferenceEquals(control, SecondaryGridItems) || ReferenceEquals(control, SecondaryDetailsItems)
			? ViewModel.ActiveTab?.SecondaryBrowser
			: ViewModel.ActiveTab?.Browser;
	}

	private DirectoryBrowserViewModel? GetBrowserForItem(LocalFileSystemItem item)
	{
		DirectoryBrowserViewModel? secondaryBrowser = ViewModel.ActiveTab?.SecondaryBrowser;
		return secondaryBrowser?.Items.Contains(item) is true ? secondaryBrowser : ViewModel.ActiveTab?.Browser;
	}

	private FrameworkElement GetVisibleItemsControl(DirectoryBrowserViewModel browser)
	{
		bool isSecondary = ReferenceEquals(browser, ViewModel.ActiveTab?.SecondaryBrowser);
		return (isSecondary, browser.IsGridView) switch
		{
			(true, true) => SecondaryGridItems,
			(true, false) => SecondaryDetailsItems,
			(false, true) => GridItems,
			_ => DetailsItems,
		};
	}

	private void ActivateBrowser(DirectoryBrowserViewModel browser, FrameworkElement? sourceControl = null)
	{
		ViewModel.SetActiveBrowser(browser);
		FrameworkElement control = sourceControl ?? GetVisibleItemsControl(browser);
		selectedItems = control switch
		{
			ItemsView view => view.SelectedItems.OfType<LocalFileSystemItem>().ToArray(),
			ListViewBase list => list.SelectedItems.OfType<LocalFileSystemItem>().ToArray(),
			_ => [],
		};
		browser.UpdateSelection(selectedItems);
		UpdatePaneVisuals();
		UpdateCommandStates();
	}

	private static void RestoreSelection(
		DirectoryBrowserViewModel browser,
		FrameworkElement control,
		IReadOnlyList<LocalFileSystemItem> selection)
	{
		if (control is ItemsView view)
		{
			view.DeselectAll();
			foreach (LocalFileSystemItem item in selection)
			{
				int index = browser.Items.IndexOf(item);
				if (index >= 0)
				{
					view.Select(index);
				}
			}
		}
		else if (control is ListViewBase list)
		{
			list.SelectedItems.Clear();
			foreach (LocalFileSystemItem item in selection.Where(browser.Items.Contains))
			{
				list.SelectedItems.Add(item);
			}
		}
	}

	private void UpdatePaneVisuals()
	{
		bool secondaryIsActive = ReferenceEquals(Browser, ViewModel.ActiveTab?.SecondaryBrowser);
		bool increaseContrast = accessibilityDisplayOptions.HasFlag(MacOSAccessibilityDisplayOptions.IncreaseContrast);
		double inactiveBorderWidth = increaseContrast ? 2 : 1;
		double activeBorderWidth = increaseContrast ? 3 : 2;
		Brush defaultBorder = (Brush)Application.Current.Resources["FilesCardBorderBrush"];
		Brush activeBorder = (Brush)Application.Current.Resources["FilesAccentBrush"];
		PrimaryPaneBorder.BorderThickness = new Thickness(secondaryIsActive ? inactiveBorderWidth : activeBorderWidth);
		SecondaryPaneBorder.BorderThickness = new Thickness(secondaryIsActive ? activeBorderWidth : inactiveBorderWidth);
		PrimaryPaneBorder.BorderBrush = secondaryIsActive ? defaultBorder : activeBorder;
		SecondaryPaneBorder.BorderBrush = secondaryIsActive ? activeBorder : defaultBorder;
		if (SplitViewButton.Content is CommandLabel splitViewLabel)
		{
			splitViewLabel.Content = GetResource(ViewModel.ActiveTab?.IsSplitView is true ? "CloseSplitViewButtonText" : "SplitViewButton/Content");
		}
		UpdateSortHeaderVisuals();
		UpdateViewModeVisuals();
		UpdateAddressBar();
		ApplySplitLayout();
	}

	private void ApplySplitLayout()
	{
		BrowserTabViewModel? tab = ViewModel.ActiveTab;
		if (tab?.IsSplitView is not true)
		{
			PrimaryPaneColumn.Width = new GridLength(1, GridUnitType.Star);
			SplitDividerColumn.Width = new GridLength(0);
			SecondaryPaneColumn.Width = new GridLength(0);
			return;
		}

		double availableWidth = Math.Max(0, PaneGrid.ActualWidth - SplitDividerWidth);
		if (availableWidth <= 0)
		{
			return;
		}

		double ratio = ClampSplitRatio(tab.SplitRatio, availableWidth);
		if (!ratio.Equals(tab.SplitRatio))
		{
			tab.SplitRatio = ratio;
		}

		PrimaryPaneColumn.Width = new GridLength(availableWidth * ratio);
		SplitDividerColumn.Width = new GridLength(SplitDividerWidth);
		SecondaryPaneColumn.Width = new GridLength(availableWidth * (1 - ratio));
	}

	private static double ClampSplitRatio(double ratio, double availableWidth)
	{
		if (availableWidth < MinimumPaneWidth * 2)
		{
			return 0.5;
		}

		double minimumRatio = MinimumPaneWidth / availableWidth;
		return Math.Clamp(ratio, minimumRatio, 1 - minimumRatio);
	}

	private static double? GetSplitRatioForKey(double ratio, double availableWidth, VirtualKey key)
	{
		if (availableWidth <= 0)
		{
			return null;
		}

		double requestedRatio = key switch
		{
			VirtualKey.Left => ratio - (KeyboardResizeStep / availableWidth),
			VirtualKey.Right => ratio + (KeyboardResizeStep / availableWidth),
			VirtualKey.Home => 0,
			VirtualKey.End => 1,
			_ => double.NaN,
		};
		return double.IsNaN(requestedRatio) ? null : ClampSplitRatio(requestedRatio, availableWidth);
	}

	private void SortFieldMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is null || sender is not ToggleMenuFlyoutItem { Tag: string value } || !Enum.TryParse(value, out FileSortField field))
		{
			return;
		}

		Browser.SetSort(field, Browser.SortDirection);
		SortNameItem.IsChecked = field is FileSortField.Name;
		SortModifiedItem.IsChecked = field is FileSortField.Modified;
		SortSizeItem.IsChecked = field is FileSortField.Size;
		AnnounceSortState(Browser);
	}

	private void DetailsHeaderButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Button { Tag: string value } button || !Enum.TryParse(value, out FileSortField field))
		{
			return;
		}

		DirectoryBrowserViewModel? browser = button.Name.StartsWith("Secondary", StringComparison.Ordinal)
			? ViewModel.ActiveTab?.SecondaryBrowser
			: ViewModel.ActiveTab?.Browser;
		if (browser is null)
		{
			return;
		}

		ActivateBrowser(browser, GetVisibleItemsControl(browser));
		ApplyDetailsSort(browser, field);
	}

	private void ApplyDetailsSort(DirectoryBrowserViewModel browser, FileSortField field, bool announce = true)
	{
		FileSortDirection direction = browser.SortField == field
			? browser.SortDirection is FileSortDirection.Ascending ? FileSortDirection.Descending : FileSortDirection.Ascending
			: FileSortDirection.Ascending;
		IReadOnlyList<LocalFileSystemItem> selection = selectedItems;
		browser.SetSort(field, direction);
		FrameworkElement targetControl = GetVisibleItemsControl(browser);
		RestoreSelection(browser, targetControl, selection);
		browser.UpdateSelection(selection);
		UpdateSortHeaderVisuals();
		if (announce)
		{
			AnnounceSortState(browser);
		}
	}

	private void UpdateSortHeaderVisuals()
	{
		SetSortIndicators(
			ViewModel.ActiveTab?.Browser,
			(PrimaryNameHeaderButton, PrimaryNameSortIndicator, FileSortField.Name, "NameColumn/Text"),
			(PrimaryModifiedHeaderButton, PrimaryModifiedSortIndicator, FileSortField.Modified, "ModifiedColumn/Text"),
			(PrimarySizeHeaderButton, PrimarySizeSortIndicator, FileSortField.Size, "SizeColumn/Text"));
		SetSortIndicators(
			ViewModel.ActiveTab?.SecondaryBrowser,
			(SecondaryNameHeaderButton, SecondaryNameSortIndicator, FileSortField.Name, "NameColumn/Text"),
			(SecondaryModifiedHeaderButton, SecondaryModifiedSortIndicator, FileSortField.Modified, "ModifiedColumn/Text"),
			(SecondarySizeHeaderButton, SecondarySizeSortIndicator, FileSortField.Size, "SizeColumn/Text"));
	}

	private void SetSortIndicators(
		DirectoryBrowserViewModel? browser,
		params (Button Header, PathIcon Indicator, FileSortField Field, string LabelResource)[] columns)
	{
		foreach ((Button header, PathIcon indicator, FileSortField field, string labelResource) in columns)
		{
			indicator.Data = null;
			indicator.Visibility = Visibility.Collapsed;
			string label = GetResource(labelResource);
			string accessibilityName = browser?.SortField == field
				? string.Format(
					GetResource("SortedColumnAutomationFormat"),
					label,
					GetResource(browser.SortDirection is FileSortDirection.Ascending ? "SortAscendingItem/Text" : "SortDescendingItem/Text"))
				: string.Format(GetResource("UnsortedColumnAutomationFormat"), label);
			Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(header, accessibilityName);
			ToolTipService.SetToolTip(header, accessibilityName);
		}
		if (browser is null)
		{
			return;
		}

		PathIcon activeIndicator = columns.First(column => column.Field == browser.SortField).Indicator;
		string path = browser.SortDirection is FileSortDirection.Ascending
			? "M10,2 L18,10 L16,12 L12,8 V18 H8 V8 L4,12 L2,10 Z"
			: "M8,2 H12 V12 L16,8 L18,10 L10,18 L2,10 L4,8 L8,12 Z";
		activeIndicator.Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), path);
		activeIndicator.Visibility = Visibility.Visible;
	}

	private static string GetSortFieldResource(FileSortField field) => field switch
	{
		FileSortField.Modified => "ModifiedColumn/Text",
		FileSortField.Size => "SizeColumn/Text",
		_ => "NameColumn/Text",
	};

	private void AnnounceSortState(DirectoryBrowserViewModel browser)
	{
		ScheduleAccessibilityAnnouncement(string.Format(
			GetResource("SortChangedAnnouncementFormat"),
			GetResource(GetSortFieldResource(browser.SortField)),
			GetResource(browser.SortDirection is FileSortDirection.Ascending ? "SortAscendingItem/Text" : "SortDescendingItem/Text")));
	}

	private void UpdateViewModeVisuals()
	{
		Brush selectedBrush = (Brush)Application.Current.Resources["FilesAccentSubtleBrush"];
		Brush inactiveBrush = (Brush)Application.Current.Resources["FilesSubtleBrush"];
		bool isGridView = Browser?.IsGridView is true;
		GridViewStatusButton.Background = isGridView ? selectedBrush : inactiveBrush;
		DetailsViewStatusButton.Background = isGridView ? inactiveBrush : selectedBrush;
	}

	private void SortFlyout_Opening(object sender, object e)
	{
		if (Browser is null)
		{
			return;
		}

		SortNameItem.IsChecked = Browser.SortField is FileSortField.Name;
		SortModifiedItem.IsChecked = Browser.SortField is FileSortField.Modified;
		SortSizeItem.IsChecked = Browser.SortField is FileSortField.Size;
		SortAscendingItem.IsChecked = Browser.SortDirection is FileSortDirection.Ascending;
		SortDescendingItem.IsChecked = Browser.SortDirection is FileSortDirection.Descending;
	}

	private void SortDirectionMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is null || sender is not ToggleMenuFlyoutItem { Tag: string value } || !Enum.TryParse(value, out FileSortDirection direction))
		{
			return;
		}

		Browser.SetSort(Browser.SortField, direction);
		SortAscendingItem.IsChecked = direction is FileSortDirection.Ascending;
		SortDescendingItem.IsChecked = direction is FileSortDirection.Descending;
		AnnounceSortState(Browser);
	}

	private ContentDialog CreateTextInputDialog(string titleResource, string primaryButtonResource, TextBox input)
	{
		return new ContentDialog
		{
			Title = GetResource(titleResource),
			Content = input,
			PrimaryButtonText = GetResource(primaryButtonResource),
			CloseButtonText = GetResource("CancelButtonText"),
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = XamlRoot,
		};
	}

	private void ViewModel_WorkspaceChanged(object? sender, EventArgs e)
	{
		RecordRecentLocation();
		UpdateAddressBar();
		UpdateSidebarSelection();
		UpdateSortHeaderVisuals();
		UpdateViewModeVisuals();
		ScheduleWorkspaceSave();
	}

	private void RecordRecentLocation()
	{
		string? path = ViewModel.ActiveBrowser?.CurrentPath;
		if (string.IsNullOrWhiteSpace(path) || string.Equals(lastRecordedRecentPath, path, StringComparison.Ordinal))
		{
			return;
		}
		lastRecordedRecentPath = path;
		if (!Directory.Exists(path) || IsBuiltInSidebarPath(path))
		{
			return;
		}

		string[] recentPaths = (currentSettings.RecentPaths ?? [])
			.Where(existing => !string.Equals(existing, path, StringComparison.Ordinal))
			.Prepend(path)
			.Take(8)
			.ToArray();
		if ((currentSettings.RecentPaths ?? []).SequenceEqual(recentPaths, StringComparer.Ordinal))
		{
			return;
		}

		currentSettings = currentSettings with { RecentPaths = recentPaths };
		ViewModel.ApplySettings(currentSettings);
		UpdateSidebarSelection();
	}

	private static bool IsBuiltInSidebarPath(string path)
	{
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string[] builtInPaths =
		[
			home,
			Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
			Path.Combine(home, "Downloads"),
			Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
			Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
			Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
			Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
		];
		return builtInPaths.Any(candidate => string.Equals(candidate, path, StringComparison.Ordinal));
	}

	private void ScheduleWorkspaceSave()
	{
		settingsSaveCancellation?.Cancel();
		settingsSaveCancellation?.Dispose();
		var cancellation = new CancellationTokenSource();
		settingsSaveCancellation = cancellation;
		_ = SaveWorkspaceAfterDelayAsync(cancellation);
	}

	private async Task SaveWorkspaceAfterDelayAsync(CancellationTokenSource cancellation)
	{
		try
		{
			await Task.Delay(600, cancellation.Token);
			await PersistSettingsAsync(currentSettings, cancellation.Token);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
		finally
		{
			if (ReferenceEquals(settingsSaveCancellation, cancellation))
			{
				settingsSaveCancellation = null;
				cancellation.Dispose();
			}
		}
	}

	private async Task<AppSettings> PersistSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
	{
		await SettingsSaveLock.WaitAsync(cancellationToken);
		try
		{
			AppSettings latestSettings = await SettingsService.LoadAsync(cancellationToken);
			AppSettings mergedSettings = MergeChangedSettings(latestSettings, persistedSettingsBaseline, settings);
			WindowSessionState windowSession = ((App)Application.Current).CaptureWindowSession(this);
			AppSettings updatedSettings = mergedSettings with
			{
				Workspace = windowSession.PrimaryWorkspace,
				AdditionalWindowWorkspaces = windowSession.AdditionalWindowWorkspaces,
				ActiveWindowIndex = windowSession.ActiveWindowIndex,
				WindowPlacement = windowSession.PrimaryWindowPlacement,
				AdditionalWindowPlacements = windowSession.AdditionalWindowPlacements,
				SidebarWidth = mergedSettings.SidebarWidth,
				SchemaVersion = 13,
			};
			await SettingsService.SaveAsync(updatedSettings, cancellationToken);
			currentSettings = updatedSettings;
			persistedSettingsBaseline = updatedSettings;
			return updatedSettings;
		}
		finally
		{
			SettingsSaveLock.Release();
		}
	}

	private static AppSettings MergeChangedSettings(AppSettings latest, AppSettings baseline, AppSettings requested)
	{
		return latest with
		{
			Theme = requested.Theme != baseline.Theme ? requested.Theme : latest.Theme,
			ShowHiddenFiles = requested.ShowHiddenFiles != baseline.ShowHiddenFiles ? requested.ShowHiddenFiles : latest.ShowHiddenFiles,
			UseGridViewForNewTabs = requested.UseGridViewForNewTabs != baseline.UseGridViewForNewTabs ? requested.UseGridViewForNewTabs : latest.UseGridViewForNewTabs,
			ReverseTabScrollDirection = requested.ReverseTabScrollDirection != baseline.ReverseTabScrollDirection ? requested.ReverseTabScrollDirection : latest.ReverseTabScrollDirection,
			FavoritePaths = HasSequenceChanged(requested.FavoritePaths, baseline.FavoritePaths, StringComparer.OrdinalIgnoreCase) ? requested.FavoritePaths : latest.FavoritePaths,
			RecentPaths = HasSequenceChanged(requested.RecentPaths, baseline.RecentPaths, StringComparer.Ordinal) ? requested.RecentPaths : latest.RecentPaths,
			RecentServers = HasSequenceChanged(requested.RecentServers, baseline.RecentServers, StringComparer.OrdinalIgnoreCase) ? requested.RecentServers : latest.RecentServers,
			SearchHistory = HasSequenceChanged(requested.SearchHistory, baseline.SearchHistory, StringComparer.CurrentCultureIgnoreCase) ? requested.SearchHistory : latest.SearchHistory,
			SavedSearches = HasSequenceChanged(requested.SavedSearches, baseline.SavedSearches) ? requested.SavedSearches : latest.SavedSearches,
			AccessGrants = HasSequenceChanged(requested.AccessGrants, baseline.AccessGrants) ? requested.AccessGrants : latest.AccessGrants,
			IsSidebarOpen = requested.IsSidebarOpen != baseline.IsSidebarOpen ? requested.IsSidebarOpen : latest.IsSidebarOpen,
			IsPreviewPaneOpen = requested.IsPreviewPaneOpen != baseline.IsPreviewPaneOpen ? requested.IsPreviewPaneOpen : latest.IsPreviewPaneOpen,
			SidebarWidth = !requested.SidebarWidth.Equals(baseline.SidebarWidth) ? requested.SidebarWidth : latest.SidebarWidth,
			Language = requested.Language != baseline.Language ? requested.Language : latest.Language,
			CollapsedSidebarSections = HasSequenceChanged(requested.CollapsedSidebarSections, baseline.CollapsedSidebarSections, StringComparer.Ordinal) ? requested.CollapsedSidebarSections : latest.CollapsedSidebarSections,
			HiddenDefaultSidebarLocations = HasSequenceChanged(requested.HiddenDefaultSidebarLocations, baseline.HiddenDefaultSidebarLocations, StringComparer.Ordinal) ? requested.HiddenDefaultSidebarLocations : latest.HiddenDefaultSidebarLocations,
			Terminal = requested.Terminal != baseline.Terminal ? requested.Terminal : latest.Terminal,
		};
	}

	private static bool HasSequenceChanged<T>(T[]? requested, T[]? baseline, IEqualityComparer<T>? comparer = null)
	{
		return !(requested ?? []).SequenceEqual(baseline ?? [], comparer ?? EqualityComparer<T>.Default);
	}

	private async Task ShowErrorAsync(string message)
	{
		var dialog = new ContentDialog
		{
			Title = GetResource("FileOperationErrorTitle"),
			Content = message,
			CloseButtonText = GetResource("CloseButtonText"),
			XamlRoot = XamlRoot,
		};
		await dialog.ShowAsync();
	}

	private string GetResource(string name)
	{
		string? value = resourceLoader.GetString(name);
		return string.IsNullOrWhiteSpace(value) ? name : value;
	}

	private string GetFileOperationError(FileOperationException exception)
	{
		return exception.Error switch
		{
			FileOperationError.InvalidName => GetResource("InvalidItemNameMessage"),
			FileOperationError.InvalidCharacters => GetResource("InvalidItemNameCharactersMessage"),
			FileOperationError.MissingParent => GetResource("MissingParentFolderMessage"),
			FileOperationError.AlreadyExists => string.Format(GetResource("ItemAlreadyExistsFormat"), exception.ItemName),
			FileOperationError.ItemNotFound => GetResource("ItemNotFoundMessage"),
			FileOperationError.OverlappingSelection => GetResource("OverlappingRenameSelectionMessage"),
			FileOperationError.UndoDataUnavailable => GetResource("UndoDataUnavailableMessage"),
			FileOperationError.HistoryTransferIncomplete => GetResource("HistoryTransferIncompleteMessage"),
			FileOperationError.HistoryRollbackFailed => GetResource("HistoryRollbackFailedMessage"),
			FileOperationError.CreatedItemChanged => GetResource("CreatedItemChangedUndoErrorMessage"),
			_ => GetResource("UnknownFileOperationErrorMessage"),
		};
	}
}
