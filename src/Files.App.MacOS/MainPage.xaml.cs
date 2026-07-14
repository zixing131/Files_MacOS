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
	private MacOSMainMenuService MainMenuService { get; } = new();
	private readonly ResourceLoader resourceLoader = ResourceLoader.GetForViewIndependentUse();
	private IReadOnlyList<LocalFileSystemItem> selectedItems = [];
	private CancellationTokenSource? fileTransferCancellation;
	private CancellationTokenSource? searchInputCancellation;
	private CancellationTokenSource? settingsSaveCancellation;
	private readonly SemaphoreSlim settingsSaveLock = new(1, 1);
	private readonly Stack<FileOperationHistoryEntry> undoHistory = new();
	private readonly Stack<FileOperationHistoryEntry> redoHistory = new();
	private AppSettings currentSettings = new();
	private bool isResizingSplit;
	private bool isResizingSidebar;
	private bool isConnectingServer;
	private bool isHistoryOperationRunning;
	private bool isUpdatingSelection;
	private bool isUpdatingSidebarSelection;
	private bool isEditingAddress;
	private bool isSidebarOpen = true;
	private double sidebarWidth = 228;
	private bool isPreviewPaneOpen;

	public MainPage()
	{
		FileTransferHistoryService = new(FileTransferService, FileRenameService);
		FileTrashHistoryService = new(WorkspaceService, FileTransferHistoryService);
		InitializeComponent();
		DataContext = ViewModel;
		MoreSelectionSubItem.Text = GetResource("MoreSelectionSubItem/Text");
		MoreArchiveSubItem.Text = GetResource("MoreArchiveSubItem/Text");
		ToolTipService.SetToolTip(GridViewStatusButton, GetResource("GridViewTooltip"));
		ToolTipService.SetToolTip(DetailsViewStatusButton, GetResource("DetailsViewTooltip"));
		ToolTipService.SetToolTip(SearchBox, GetResource("SearchSyntaxHelp"));
		ToolTipService.SetToolTip(SearchOptionsButton, GetResource("SearchOptionsTooltip"));
		Loaded += MainPage_Loaded;
		Unloaded += MainPage_Unloaded;
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
		isSidebarOpen = currentSettings.IsSidebarOpen;
		sidebarWidth = currentSettings.SidebarWidth;
		isPreviewPaneOpen = currentSettings.IsPreviewPaneOpen;
		ViewModel.ApplySettings(currentSettings);
		ApplyTheme(currentSettings.Theme);
		await ViewModel.InitializeAsync();
		ViewModel.WorkspaceChanged += ViewModel_WorkspaceChanged;
		UpdatePaneVisuals();
		UpdateSidebarVisuals();
		UpdateSidebarSelection();
		UpdatePreviewPaneVisuals();
		UpdateCommandStates();
		MainMenuService.Install(
			this,
			string.Equals(Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride, "zh-Hans", StringComparison.Ordinal));
		MainMenuService.UpdateValidationSnapshot(this);
		ScheduleWorkspaceSave();
		if (string.Equals(Environment.GetEnvironmentVariable("FILES_MACOS_PERF_DIAGNOSTICS"), "1", StringComparison.Ordinal))
		{
			_ = ReportPerformanceDiagnosticsAsync();
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
		sidebarWidth = 312;
		UpdateSidebarVisuals();
		bool sidebarResizeRoundtrip = Math.Abs(SidebarColumn.Width.Value - 312) < 0.1 && Math.Abs(SidebarDividerColumn.Width.Value - SidebarDividerWidth) < 0.1;
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
		UpdateCommandToolbarLayout(1400);
		bool toolbarWide = MoreCommandsButton.Visibility is Visibility.Collapsed && RevealButton.Visibility is Visibility.Visible && RenameButton.Visibility is Visibility.Visible;
		UpdateCommandToolbarLayout(1100);
		bool toolbarOverflow = MoreCommandsButton.Visibility is Visibility.Visible && RevealButton.Visibility is Visibility.Collapsed && RenameButton.Visibility is Visibility.Visible;
		UpdateCommandToolbarLayout(800);
		bool toolbarCompact = MoreCommandsButton.Visibility is Visibility.Visible && RenameButton.Visibility is Visibility.Collapsed && MoreRenameItem.Visibility is Visibility.Visible;
		UpdateCommandToolbarLayout(CommandToolbarBorder.ActualWidth);
		bool toolbarBreakpoints = toolbarWide && toolbarOverflow && toolbarCompact;
		FileSortField initialSortField = browser.SortField;
		FileSortDirection initialSortDirection = browser.SortDirection;
		FileSortField diagnosticSortField = initialSortField is FileSortField.Modified ? FileSortField.Size : FileSortField.Modified;
		ApplyDetailsSort(browser, diagnosticSortField);
		bool diagnosticIsSecondary = ReferenceEquals(browser, ViewModel.ActiveTab?.SecondaryBrowser);
		TextBlock diagnosticIndicator = (diagnosticIsSecondary, diagnosticSortField) switch
		{
			(true, FileSortField.Modified) => SecondaryModifiedSortIndicator,
			(true, _) => SecondarySizeSortIndicator,
			(false, FileSortField.Modified) => PrimaryModifiedSortIndicator,
			_ => PrimarySizeSortIndicator,
		};
		bool sortHeaderRoundtrip = browser.SortField == diagnosticSortField && !string.IsNullOrEmpty(diagnosticIndicator.Text);
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
		bool initialLibrariesExpanded = ViewModel.Locations.First(static location => location is { IsHeader: true, SectionId: "Libraries" }).IsExpanded;
		ViewModel.ToggleSidebarSection("Libraries");
		bool sidebarSectionChanged = ViewModel.Locations.Count != initialSidebarLocationCount &&
			ViewModel.Locations.First(static location => location is { IsHeader: true, SectionId: "Libraries" }).IsExpanded != initialLibrariesExpanded;
		ViewModel.ToggleSidebarSection("Libraries");
		bool sidebarSectionRoundtrip = sidebarSectionChanged && ViewModel.Locations.Count == initialSidebarLocationCount &&
			ViewModel.Locations.First(static location => location is { IsHeader: true, SectionId: "Libraries" }).IsExpanded == initialLibrariesExpanded;
		UpdateSidebarSelection();
		await Task.Delay(250);
		bool sidebarLabels = ViewModel.Locations.Count > 0 && ViewModel.Locations.All(static location => !string.IsNullOrWhiteSpace(location.Name));
		var sidebarLabelNames = ViewModel.Locations.Select(static location => location.Name).ToHashSet(StringComparer.CurrentCulture);
		int renderedSidebarLabels = CountRenderedTextBlocks(SidebarList, sidebarLabelNames);
		using System.Text.Json.JsonDocument menuDescription = System.Text.Json.JsonDocument.Parse(MainMenuService.Describe());
		bool nativeMenuInstalled = menuDescription.RootElement.GetProperty("installed").GetBoolean() &&
			menuDescription.RootElement.GetProperty("rootCount").GetInt32() >= 6 &&
			menuDescription.RootElement.GetProperty("commandCount").GetInt32() >= 20;
		bool menuInitialSidebarState = isSidebarOpen;
		bool menuFirstInvoke = MainMenuService.InvokeForDiagnostics(MacOSMenuCommand.ToggleSidebar);
		await Task.Delay(100);
		bool menuChangedSidebar = isSidebarOpen != menuInitialSidebarState;
		bool menuSecondInvoke = MainMenuService.InvokeForDiagnostics(MacOSMenuCommand.ToggleSidebar);
		await Task.Delay(100);
		bool nativeMenuRouting = menuFirstInvoke && menuSecondInvoke && menuChangedSidebar && isSidebarOpen == menuInitialSidebarState;
		int commandAccelerators = KeyboardAccelerators.Count(accelerator =>
			accelerator.Modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Windows));

		using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
		Console.WriteLine(
			$"FILES_MACOS_PERF view={(browser.IsGridView ? "grid" : "details")} " +
			$"items={browser.Items.Count} realized={realizedContainers} selection_roundtrip={selectionRoundtrip} " +
			$"breadcrumbs={BreadcrumbPanel.Children.OfType<Button>().Count()} sidebar_sections={ViewModel.Locations.Count(static location => location.IsHeader)} " +
			$"sidebar_roundtrip={sidebarRoundtrip} sidebar_resize={sidebarResizeRoundtrip} sidebar_active={sidebarActiveSync} sidebar_sections_toggle={sidebarSectionRoundtrip} sidebar_labels={sidebarLabels} sidebar_rendered_labels={renderedSidebarLabels} locale={System.Globalization.CultureInfo.CurrentUICulture.Name} language_override={Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride} home_label={GetResource("SidebarHomeButton/Content")} address_roundtrip={addressRoundtrip} preview_roundtrip={previewRoundtrip} " +
			$"toolbar_breakpoints={toolbarBreakpoints} empty_folder={browser.IsEmptyFolder} no_results={browser.HasNoSearchResults} " +
			$"sort_headers={sortHeaderRoundtrip} view_switch={viewModeRoundtrip} native_menu={nativeMenuInstalled} native_menu_routing={nativeMenuRouting} command_accelerators={commandAccelerators} " +
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
			MainMenuService.Dispose();
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
			MacOSMenuCommand.CloseTab => isIdle && ViewModel.Tabs.Count > 1,
			MacOSMenuCommand.Properties or MacOSMenuCommand.MoveToTrash or MacOSMenuCommand.Rename or
				MacOSMenuCommand.Cut or MacOSMenuCommand.Copy or MacOSMenuCommand.CopyPath => isIdle && selectedItems.Count > 0,
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
			case MacOSMenuCommand.Properties:
				PropertiesButton_Click(PropertiesButton, args);
				break;
			case MacOSMenuCommand.MoveToTrash:
				DeleteButton_Click(DeleteButton, args);
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
				Content = item.IsHome ? "⌂" : item.Title,
				Tag = item.Path,
				Style = (Style)Application.Current.Resources["BreadcrumbButtonStyle"],
			};
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
			return;
		}
		if (SidebarList.SelectedItem is SidebarLocation location && Browser is not null)
		{
			if (location.IsNetworkServer)
			{
				await ConnectToServerAsync(location.Path);
			}
			else
			{
				await Browser.NavigateAsync(location.Path);
			}
		}
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

		sidebarWidth = Math.Clamp(
			e.GetCurrentPoint(WorkspaceGrid).Position.X,
			MinimumSidebarWidth,
			MaximumSidebarWidth);
		SidebarColumn.Width = new GridLength(sidebarWidth);
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
			if (item.IsDirectory)
			{
				await browser.NavigateAsync(item.Path);
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
		if (selectedItems.Count > 0 && fileTransferCancellation is null)
		{
			args.Handled = true;
			await SetFileClipboardAsync(FileTransferMode.Copy);
		}
	}

	private async void CutAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (selectedItems.Count > 0 && fileTransferCancellation is null)
		{
			args.Handled = true;
			await SetFileClipboardAsync(FileTransferMode.Move);
		}
	}

	private async void PasteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (fileTransferCancellation is null)
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

	private async Task TransferItemsAsync(FileClipboardContent clipboard, bool clearClipboardAfterMove)
	{
		if (Browser is null || fileTransferCancellation is not null || clipboard.Paths.Count is 0)
		{
			return;
		}

		DirectoryBrowserViewModel targetBrowser = Browser;
		FileConflictResolution conflictResolution = FileConflictResolution.KeepBoth;
		if (HasDestinationConflicts(clipboard.Paths, targetBrowser.CurrentPath, clipboard.Mode))
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
		FavoriteButton.IsEnabled = isIdle && selectedItems is [LocalFileSystemItem { IsDirectory: true }];
		SettingsButton.IsEnabled = isIdle;
		ConnectServerButton.IsEnabled = isIdle;
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
		MainMenuService.UpdateValidationSnapshot(this);
	}

	private void CommandToolbarBorder_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		UpdateCommandToolbarLayout(e.NewSize.Width);
	}

	private void UpdateCommandToolbarLayout(double width)
	{
		bool useOverflow = width < 1260;
		bool compact = width < 940;
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
					if (item.IsDirectory)
					{
						await Browser.NavigateAsync(item.Path);
					}
					else
					{
						await WorkspaceService.OpenAsync(item.Path);
					}
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

	private async void TerminalButton_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is null)
		{
			return;
		}

		string path = selectedItems is [LocalFileSystemItem item]
			? item.IsDirectory ? item.Path : Path.GetDirectoryName(item.Path) ?? Browser.CurrentPath
			: Browser.CurrentPath;
		try
		{
			await WorkspaceService.OpenTerminalAsync(path);
		}
		catch (IOException)
		{
			await ShowErrorAsync(GetResource("OpenTerminalErrorMessage"));
		}
	}

	private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
	{
		if (selectedItems is not [LocalFileSystemItem { IsDirectory: true } item])
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
		if (Browser is null || selectedItems is not [LocalFileSystemItem item] || !IsZipArchive(item) || fileTransferCancellation is not null)
		{
			await ShowErrorAsync(GetResource("SelectZipArchiveMessage"));
			return;
		}

		string folderName = Path.GetFileNameWithoutExtension(item.Name);
		var input = new TextBox { Text = folderName };
		ContentDialog dialog = CreateTextInputDialog("ExtractArchiveDialogTitle", "ExtractButtonText", input);
		if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
		{
			return;
		}

		string destination = Browser.CurrentPath;
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
		var showHiddenToggle = new ToggleSwitch
		{
			Header = GetResource("ShowHiddenFilesSetting"),
			IsOn = currentSettings.ShowHiddenFiles,
		};
		var defaultGridToggle = new ToggleSwitch
		{
			Header = GetResource("DefaultGridViewSetting"),
			IsOn = currentSettings.UseGridViewForNewTabs,
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
		content.Children.Add(showHiddenToggle);
		content.Children.Add(defaultGridToggle);
		FolderAccessGrant[] existingGrants = currentSettings.AccessGrants ?? [];
		CheckBox[] grantToggles = existingGrants.Select(grant => new CheckBox
		{
			Content = grant.Path,
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
			Content = content,
			PrimaryButtonText = GetResource("SaveButtonText"),
			CloseButtonText = GetResource("CancelButtonText"),
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = XamlRoot,
		};
		if (await dialog.ShowAsync() is not ContentDialogResult.Primary)
		{
			return;
		}

		AppLanguagePreference previousLanguage = currentSettings.Language;
		var newSettings = currentSettings with
		{
			Theme = (AppThemePreference)Math.Clamp(themePicker.SelectedIndex, 0, 2),
			Language = (AppLanguagePreference)Math.Clamp(languagePicker.SelectedIndex, 0, 2),
			ShowHiddenFiles = showHiddenToggle.IsOn,
			UseGridViewForNewTabs = defaultGridToggle.IsOn,
			AccessGrants = grantToggles
				.Where(static toggle => toggle.IsChecked is true)
				.Select(static toggle => (FolderAccessGrant)toggle.Tag)
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
			var dialog = new ContentDialog
			{
				Title = GetResource("PropertiesDialogTitle"),
				Content = CreatePropertiesContent(summary),
				CloseButtonText = GetResource("CloseButtonText"),
				XamlRoot = XamlRoot,
			};
			await dialog.ShowAsync();
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

	private FrameworkElement CreatePropertiesContent(FilePropertiesSummary summary)
	{
		var panel = new StackPanel
		{
			Spacing = 10,
			MinWidth = 420,
		};

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
		if (summary.UnixMode is not null)
		{
			string octalMode = Convert.ToString((int)summary.UnixMode.Value, 8).PadLeft(3, '0');
			AddPropertyRow(panel, GetResource("PropertyPermissionsLabel"), $"{octalMode}  ({summary.UnixMode.Value})");
		}
		if (summary.LinkTarget is not null)
		{
			AddPropertyRow(panel, GetResource("PropertyLinkTargetLabel"), summary.LinkTarget);
		}

		return new ScrollViewer
		{
			Content = panel,
			MaxHeight = 480,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
		};
	}

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

	private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		selectedItems = [];
		Browser?.UpdateSelection(selectedItems);
		UpdatePaneVisuals();
		UpdateCommandStates();
	}

	private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
	{
		if (fileTransferCancellation is null && args.Item is BrowserTabViewModel tab)
		{
			ViewModel.CloseTab(tab);
		}
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
		Brush defaultBorder = (Brush)Application.Current.Resources["FilesCardBorderBrush"];
		Brush activeBorder = (Brush)Application.Current.Resources["FilesAccentBrush"];
		PrimaryPaneBorder.BorderThickness = new Thickness(secondaryIsActive ? 1 : 2);
		SecondaryPaneBorder.BorderThickness = new Thickness(secondaryIsActive ? 2 : 1);
		PrimaryPaneBorder.BorderBrush = secondaryIsActive ? defaultBorder : activeBorder;
		SecondaryPaneBorder.BorderBrush = secondaryIsActive ? activeBorder : defaultBorder;
		SplitViewButton.Content = GetResource(ViewModel.ActiveTab?.IsSplitView is true ? "CloseSplitViewButtonText" : "SplitViewButton.Content");
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

	private void ApplyDetailsSort(DirectoryBrowserViewModel browser, FileSortField field)
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
	}

	private void UpdateSortHeaderVisuals()
	{
		SetSortIndicators(
			ViewModel.ActiveTab?.Browser,
			PrimaryNameSortIndicator,
			PrimaryModifiedSortIndicator,
			PrimarySizeSortIndicator);
		SetSortIndicators(
			ViewModel.ActiveTab?.SecondaryBrowser,
			SecondaryNameSortIndicator,
			SecondaryModifiedSortIndicator,
			SecondarySizeSortIndicator);
	}

	private static void SetSortIndicators(
		DirectoryBrowserViewModel? browser,
		TextBlock nameIndicator,
		TextBlock modifiedIndicator,
		TextBlock sizeIndicator)
	{
		nameIndicator.Text = string.Empty;
		modifiedIndicator.Text = string.Empty;
		sizeIndicator.Text = string.Empty;
		if (browser is null)
		{
			return;
		}

		TextBlock indicator = browser.SortField switch
		{
			FileSortField.Modified => modifiedIndicator,
			FileSortField.Size => sizeIndicator,
			_ => nameIndicator,
		};
		indicator.Text = browser.SortDirection is FileSortDirection.Ascending ? "↑" : "↓";
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
		UpdateAddressBar();
		UpdateSidebarSelection();
		UpdateSortHeaderVisuals();
		UpdateViewModeVisuals();
		ScheduleWorkspaceSave();
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
		await settingsSaveLock.WaitAsync(cancellationToken);
		try
		{
			AppSettings updatedSettings = settings with
			{
				Workspace = ViewModel.CaptureWorkspaceState(),
				SidebarWidth = sidebarWidth,
				SchemaVersion = 8,
			};
			await SettingsService.SaveAsync(updatedSettings, cancellationToken);
			currentSettings = updatedSettings;
			return updatedSettings;
		}
		finally
		{
			settingsSaveLock.Release();
		}
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
			_ => GetResource("UnknownFileOperationErrorMessage"),
		};
	}
}
