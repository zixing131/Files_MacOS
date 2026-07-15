using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Files.App.MacOS.Models;
using Files.App.MacOS.Services;
using Windows.ApplicationModel.Resources;

namespace Files.App.MacOS.ViewModels;

public sealed class MainPageViewModel : ObservableObject
{
	private const int MaximumClosedTabHistory = 20;
	private readonly ResourceLoader resources = ResourceLoader.GetForViewIndependentUse();
	private readonly List<BrowserTabState> closedTabs = [];
	private BrowserTabViewModel? activeTab;
	private AppSettings settings = new();
	private bool isRestoringWorkspace;
	private bool hasRecentLocations;

	public MainPageViewModel()
	{
		RefreshLocations();
	}

	public ObservableCollection<BrowserTabViewModel> Tabs { get; } = [];

	public ObservableCollection<SidebarLocation> Locations { get; } = [];

	public bool HasRecentLocations => hasRecentLocations;

	public bool CanReopenClosedTab => closedTabs.Count > 0;

	public event EventHandler? WorkspaceChanged;

	public BrowserTabViewModel? ActiveTab
	{
		get => activeTab;
		set
		{
			if (ReferenceEquals(activeTab, value))
			{
				return;
			}

			if (activeTab is not null)
			{
				activeTab.IsActive = false;
			}
			if (SetProperty(ref activeTab, value))
			{
				if (activeTab is not null)
				{
					activeTab.IsActive = true;
				}
				OnPropertyChanged(nameof(ActiveBrowser));
				OnWorkspaceChanged();
			}
		}
	}

	public DirectoryBrowserViewModel? ActiveBrowser => ActiveTab?.ActiveBrowser;

	public void SetActiveBrowser(DirectoryBrowserViewModel browser)
	{
		if (ActiveTab?.ActivateBrowser(browser) is true)
		{
			OnPropertyChanged(nameof(ActiveBrowser));
		}
	}

	public async Task InitializeAsync()
	{
		WorkspaceState? workspace = settings.Workspace;
		if (workspace?.Tabs is not { Length: > 0 } savedTabs)
		{
			await NewTabAsync();
			return;
		}

		isRestoringWorkspace = true;
		try
		{
			foreach (BrowserTabState state in savedTabs.Take(20))
			{
				await RestoreTabAsync(state);
			}

			if (Tabs.Count is 0)
			{
				await NewTabAsync();
			}
			else
			{
				ActiveTab = Tabs[Math.Clamp(workspace.ActiveTabIndex, 0, Tabs.Count - 1)];
			}
		}
		finally
		{
			isRestoringWorkspace = false;
		}
	}

	public async Task NewTabAsync(string? path = null)
	{
		var browser = CreateBrowser();
		var tab = new BrowserTabViewModel(browser, GetResource("HomeTabHeader"), GetPathDisplayName);
		tab.StateChanged += Tab_StateChanged;
		Tabs.Add(tab);
		ActiveTab = tab;
		await browser.NavigateAsync(path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
		OnWorkspaceChanged();
	}

	public async Task DuplicateTabAsync(BrowserTabViewModel tab)
	{
		int index = Tabs.IndexOf(tab);
		if (index < 0)
		{
			return;
		}

		BrowserTabViewModel duplicate = await RestoreTabAsync(CaptureTabState(tab), index + 1);
		ActiveTab = duplicate;
		OnWorkspaceChanged();
	}

	public void MoveTab(BrowserTabViewModel tab, int targetIndex)
	{
		int currentIndex = Tabs.IndexOf(tab);
		if (currentIndex < 0 || Tabs.Count is 0)
		{
			return;
		}

		targetIndex = Math.Clamp(targetIndex, 0, Tabs.Count - 1);
		if (currentIndex != targetIndex)
		{
			Tabs.Move(currentIndex, targetIndex);
		}

		OnWorkspaceChanged();
	}

	public bool TryCaptureTabState(BrowserTabViewModel tab, out BrowserTabState state)
	{
		if (!Tabs.Contains(tab))
		{
			state = null!;
			return false;
		}

		state = CaptureTabState(tab);
		return true;
	}

	public bool DetachTabForTransfer(BrowserTabViewModel tab)
	{
		if (Tabs.Count <= 1)
		{
			return false;
		}

		int index = Tabs.IndexOf(tab);
		if (index < 0)
		{
			return false;
		}

		bool wasActive = ReferenceEquals(ActiveTab, tab);
		Tabs.RemoveAt(index);
		tab.StateChanged -= Tab_StateChanged;
		tab.Dispose();
		if (wasActive)
		{
			ActiveTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
		}
		else
		{
			OnWorkspaceChanged();
		}

		return true;
	}

	public async Task ToggleSplitViewAsync()
	{
		if (ActiveTab is not BrowserTabViewModel tab)
		{
			return;
		}

		if (tab.SecondaryBrowser is not null)
		{
			tab.DisableSplitView();
			OnPropertyChanged(nameof(ActiveBrowser));
			return;
		}

		DirectoryBrowserViewModel secondaryBrowser = CreateBrowser();
		tab.EnableSplitView(secondaryBrowser);
		OnPropertyChanged(nameof(ActiveBrowser));
		try
		{
			await secondaryBrowser.NavigateAsync(tab.Browser.CurrentPath);
		}
		catch
		{
			tab.DisableSplitView();
			OnPropertyChanged(nameof(ActiveBrowser));
			throw;
		}
	}

	private DirectoryBrowserViewModel CreateBrowser()
	{
		return new DirectoryBrowserViewModel(
			new LocalDirectoryService(),
			new SpotlightFileSearchService(),
			new MacOSWorkspaceService(),
			new LocalDirectoryChangeMonitor())
		{
			IsGridView = settings.UseGridViewForNewTabs,
			ShowHiddenFiles = settings.ShowHiddenFiles,
		};
	}

	public void ApplySettings(AppSettings value)
	{
		settings = value;
		RefreshLocations();
		foreach (BrowserTabViewModel tab in Tabs)
		{
			tab.Browser.ShowHiddenFiles = value.ShowHiddenFiles;
			if (tab.SecondaryBrowser is not null)
			{
				tab.SecondaryBrowser.ShowHiddenFiles = value.ShowHiddenFiles;
			}
		}
	}

	public void CloseTab(BrowserTabViewModel tab)
	{
		if (Tabs.Count <= 1)
		{
			return;
		}

		int index = Tabs.IndexOf(tab);
		if (index < 0)
		{
			return;
		}

		RememberClosedTab(CaptureTabState(tab));
		Tabs.Remove(tab);
		tab.StateChanged -= Tab_StateChanged;
		tab.Dispose();
		ActiveTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
		OnWorkspaceChanged();
	}

	public void CloseOtherTabs(BrowserTabViewModel tab)
	{
		int retainedIndex = Tabs.IndexOf(tab);
		if (retainedIndex < 0 || Tabs.Count <= 1)
		{
			return;
		}

		BrowserTabViewModel[] tabsToClose = Tabs.Where(candidate => !ReferenceEquals(candidate, tab)).ToArray();
		IEnumerable<BrowserTabViewModel> reopenOrder = tabsToClose
			.Where(candidate => Tabs.IndexOf(candidate) > retainedIndex)
			.Concat(tabsToClose.Where(candidate => Tabs.IndexOf(candidate) < retainedIndex).Reverse());
		CloseTabs(tab, tabsToClose, reopenOrder);
	}

	public void CloseTabsToLeft(BrowserTabViewModel tab)
	{
		int retainedIndex = Tabs.IndexOf(tab);
		if (retainedIndex <= 0)
		{
			return;
		}

		BrowserTabViewModel[] tabsToClose = Tabs.Take(retainedIndex).ToArray();
		CloseTabs(tab, tabsToClose, tabsToClose.Reverse());
	}

	public void CloseTabsToRight(BrowserTabViewModel tab)
	{
		int retainedIndex = Tabs.IndexOf(tab);
		if (retainedIndex < 0 || retainedIndex >= Tabs.Count - 1)
		{
			return;
		}

		BrowserTabViewModel[] tabsToClose = Tabs.Skip(retainedIndex + 1).ToArray();
		CloseTabs(tab, tabsToClose, tabsToClose);
	}

	private void CloseTabs(
		BrowserTabViewModel retainedTab,
		IReadOnlyCollection<BrowserTabViewModel> tabsToClose,
		IEnumerable<BrowserTabViewModel> reopenOrder)
	{
		foreach (BrowserTabViewModel closedTab in reopenOrder.Reverse())
		{
			RememberClosedTab(CaptureTabState(closedTab));
		}

		foreach (BrowserTabViewModel closedTab in tabsToClose)
		{
			Tabs.Remove(closedTab);
			closedTab.StateChanged -= Tab_StateChanged;
			closedTab.Dispose();
		}

		ActiveTab = retainedTab;
		OnWorkspaceChanged();
	}

	public async Task ReopenClosedTabAsync()
	{
		if (closedTabs.Count is 0)
		{
			return;
		}

		int historyIndex = closedTabs.Count - 1;
		BrowserTabState state = closedTabs[historyIndex];
		int initialTabCount = Tabs.Count;
		closedTabs.RemoveAt(historyIndex);
		OnPropertyChanged(nameof(CanReopenClosedTab));
		try
		{
			ActiveTab = await RestoreTabAsync(state);
			OnWorkspaceChanged();
		}
		catch
		{
			while (Tabs.Count > initialTabCount)
			{
				BrowserTabViewModel incompleteTab = Tabs[^1];
				Tabs.RemoveAt(Tabs.Count - 1);
				incompleteTab.StateChanged -= Tab_StateChanged;
				incompleteTab.Dispose();
			}

			closedTabs.Add(state);
			OnPropertyChanged(nameof(CanReopenClosedTab));
			throw;
		}
	}

	public WorkspaceState CaptureWorkspaceState()
	{
		BrowserTabState[] tabs = Tabs.Select(CaptureTabState).ToArray();
		return new(tabs, Math.Max(0, ActiveTab is null ? 0 : Tabs.IndexOf(ActiveTab)));
	}

	private static BrowserTabState CaptureTabState(BrowserTabViewModel tab)
	{
		return new(
			CapturePane(tab.Browser),
			tab.SecondaryBrowser is null ? null : CapturePane(tab.SecondaryBrowser),
			tab.SplitRatio,
			ReferenceEquals(tab.ActiveBrowser, tab.SecondaryBrowser));
	}

	private void RememberClosedTab(BrowserTabState state)
	{
		closedTabs.Add(state);
		if (closedTabs.Count > MaximumClosedTabHistory)
		{
			closedTabs.RemoveAt(0);
		}

		OnPropertyChanged(nameof(CanReopenClosedTab));
	}

	private async Task<BrowserTabViewModel> RestoreTabAsync(BrowserTabState state, int? insertIndex = null)
	{
		DirectoryBrowserViewModel primary = CreateBrowser();
		ApplyPaneState(primary, state.Primary);
		var tab = new BrowserTabViewModel(primary, GetResource("HomeTabHeader"), GetPathDisplayName);
		tab.StateChanged += Tab_StateChanged;
		if (insertIndex is int index)
		{
			Tabs.Insert(Math.Clamp(index, 0, Tabs.Count), tab);
		}
		else
		{
			Tabs.Add(tab);
		}
		try
		{
			await primary.NavigateAsync(GetRestorablePath(state.Primary.Path));

			if (state.Secondary is BrowserPaneState secondaryState)
			{
				DirectoryBrowserViewModel secondary = CreateBrowser();
				ApplyPaneState(secondary, secondaryState);
				tab.EnableSplitView(secondary);
				await secondary.NavigateAsync(GetRestorablePath(secondaryState.Path, primary.CurrentPath));
				if (!state.IsSecondaryActive)
				{
					tab.ActivateBrowser(primary);
				}
			}

			tab.SplitRatio = state.SplitRatio;
			return tab;
		}
		catch
		{
			Tabs.Remove(tab);
			tab.StateChanged -= Tab_StateChanged;
			tab.Dispose();
			throw;
		}
	}

	private static BrowserPaneState CapturePane(DirectoryBrowserViewModel browser)
	{
		return new(
			browser.CurrentPath,
			browser.IsGridView,
			browser.SortField.ToString(),
			browser.SortDirection.ToString());
	}

	private static void ApplyPaneState(DirectoryBrowserViewModel browser, BrowserPaneState state)
	{
		browser.IsGridView = state.IsGridView;
		if (Enum.TryParse(state.SortField, ignoreCase: true, out FileSortField sortField) &&
			Enum.TryParse(state.SortDirection, ignoreCase: true, out FileSortDirection sortDirection))
		{
			browser.SetSort(sortField, sortDirection);
		}
	}

	private static string GetRestorablePath(string? path, string? fallback = null)
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(path))
			{
				string fullPath = Path.GetFullPath(path);
				if (Directory.Exists(fullPath))
				{
					return fullPath;
				}
			}
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
		}

		return fallback ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
	}

	private string GetPathDisplayName(string path)
	{
		try
		{
			string fullPath = Path.GetFullPath(path);
			string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			(string Path, string Resource)[] knownFolders =
			[
				(home, "HomeTabHeader"),
				(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SidebarDesktopButton/Content"),
				(Path.Combine(home, "Downloads"), "SidebarDownloadsButton/Content"),
				(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SidebarDocumentsButton/Content"),
				(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SidebarPicturesButton/Content"),
				(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "SidebarMusicButton/Content"),
				(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "SidebarMoviesButton/Content"),
				("/Applications", "ApplicationsFolderDisplayName"),
				(Path.Combine(home, "Applications"), "ApplicationsFolderDisplayName"),
				("/Users", "UsersFolderDisplayName"),
				("/Library", "LibraryFolderDisplayName"),
				(Path.Combine(home, "Library"), "LibraryFolderDisplayName"),
				(Path.Combine(home, "Public"), "PublicFolderDisplayName"),
			];
			foreach ((string knownPath, string resource) in knownFolders)
			{
				if (!string.IsNullOrWhiteSpace(knownPath) && string.Equals(fullPath, knownPath, StringComparison.Ordinal))
				{
					return GetResource(resource);
				}
			}

			if (fullPath is "/")
			{
				return GetResource("SystemVolumeName");
			}

			string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
			return string.IsNullOrWhiteSpace(name) ? fullPath : name;
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return path;
		}
	}

	private void Tab_StateChanged(object? sender, EventArgs e)
	{
		OnWorkspaceChanged();
	}

	private void OnWorkspaceChanged()
	{
		if (!isRestoringWorkspace)
		{
			WorkspaceChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	public void RefreshLocations()
	{
		SidebarLocationOption[] defaultLocations = GetDefaultSidebarLocations();
		var hiddenLocations = (settings.HiddenDefaultSidebarLocations ?? []).ToHashSet(StringComparer.Ordinal);
		List<SidebarLocation> pinnedLocations = defaultLocations
			.Where(static location => location.Id is "Home" or "Applications" or "Shared" or "ICloud" or "Trash")
			.Where(location => !hiddenLocations.Contains(location.Id))
			.Select(static location => new SidebarLocation(location.Name, location.Path, location.Glyph, ExternalUrl: location.ExternalUrl))
			.ToList();
		List<SidebarLocation> libraryLocations = defaultLocations
			.Where(static location => location.Id is "Desktop" or "Downloads" or "Documents" or "Pictures" or "Music" or "Movies")
			.Where(location => !hiddenLocations.Contains(location.Id))
			.Select(static location => new SidebarLocation(location.Name, location.Path, location.Glyph))
			.ToList();

		var knownPaths = new HashSet<string>(pinnedLocations.Concat(libraryLocations).Select(static location => location.Path), StringComparer.OrdinalIgnoreCase);
		foreach (string configuredPath in settings.FavoritePaths ?? [])
		{
			string favoritePath;
			try
			{
				favoritePath = Path.GetFullPath(configuredPath);
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
			{
				continue;
			}

			if (!Directory.Exists(favoritePath) || !knownPaths.Add(favoritePath))
			{
				continue;
			}

			string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(favoritePath));
			pinnedLocations.Add(new(string.IsNullOrEmpty(name) ? favoritePath : name, favoritePath, "★"));
		}

		var networkLocations = new List<SidebarLocation>();
		foreach (string configuredServer in settings.RecentServers ?? [])
		{
			if (!NetworkServerAddress.TryNormalize(configuredServer, out string normalizedServer, out _) ||
				!knownPaths.Add(normalizedServer))
			{
				continue;
			}

			networkLocations.Add(new(NetworkServerAddress.GetDisplayName(normalizedServer), normalizedServer, "☁", IsNetworkServer: true));
		}

		var driveLocations = new List<SidebarLocation>();
		foreach (DriveInfo drive in DriveInfo.GetDrives())
		{
			try
			{
				string path = drive.RootDirectory.FullName;
				if (!drive.IsReady || path != "/" && !path.StartsWith("/Volumes/", StringComparison.Ordinal) || !knownPaths.Add(path))
				{
					continue;
				}

				string name = path == "/"
					? GetResource("SystemVolumeName")
					: string.IsNullOrWhiteSpace(drive.VolumeLabel) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : drive.VolumeLabel;
				bool isNetworkDrive = drive.DriveType is DriveType.Network;
				driveLocations.Add(new(
					name,
					path,
					isNetworkDrive ? "☁" : "◉",
					IsNetworkServer: isNetworkDrive,
					CanEject: !isNetworkDrive && path.StartsWith("/Volumes/", StringComparison.Ordinal)));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}

		var recentLocations = new List<SidebarLocation>();
		foreach (string configuredPath in settings.RecentPaths ?? [])
		{
			string recentPath;
			try
			{
				recentPath = Path.GetFullPath(configuredPath);
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
			{
				continue;
			}
			if (!Directory.Exists(recentPath) || !knownPaths.Add(recentPath))
			{
				continue;
			}
			string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(recentPath));
			recentLocations.Add(new(string.IsNullOrEmpty(name) ? recentPath : name, recentPath, "◷"));
		}

		var collapsedSections = (settings.CollapsedSidebarSections ?? []).ToHashSet(StringComparer.Ordinal);
		hasRecentLocations = recentLocations.Count > 0;
		var locations = new List<SidebarLocation>();
		AppendSection(locations, "Favorites", GetResource("SidebarFavoritesHeading"), pinnedLocations, collapsedSections);
		AppendSection(locations, "Recent", GetResource("SidebarRecentHeading"), recentLocations, collapsedSections);
		AppendSection(locations, "Libraries", GetResource("SidebarLibrariesHeading"), libraryLocations, collapsedSections);
		AppendSection(locations, "Network", GetResource("SidebarNetworkHeading"), networkLocations, collapsedSections);
		AppendSection(locations, "Drives", GetResource("SidebarDrivesHeading"), driveLocations, collapsedSections);

		Locations.Clear();
		foreach (SidebarLocation location in locations)
		{
			Locations.Add(location);
		}
		OnPropertyChanged(nameof(HasRecentLocations));
	}

	public SidebarLocationOption[] GetDefaultSidebarLocations()
	{
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		return new[]
		{
			new SidebarLocationOption("Home", GetResource("SidebarHomeButton/Content"), home, "⌂"),
			new SidebarLocationOption("Applications", GetResource("ApplicationsFolderDisplayName"), "/Applications", "A"),
			new SidebarLocationOption("Shared", GetResource("SidebarSharedLocationName"), "/Users/Shared", "S"),
			new SidebarLocationOption("ICloud", GetResource("SidebarICloudLocationName"), Path.Combine(home, "Library/Mobile Documents/com~apple~CloudDocs"), "☁"),
			new SidebarLocationOption(
				"Trash",
				GetResource("SidebarTrashLocationName"),
				Path.Combine(home, ".Trash"),
				"T",
				new Uri(Path.Combine(home, ".Trash")).AbsoluteUri),
			new SidebarLocationOption("Desktop", GetResource("SidebarDesktopButton/Content"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "▣"),
			new SidebarLocationOption("Downloads", GetResource("SidebarDownloadsButton/Content"), Path.Combine(home, "Downloads"), "↓"),
			new SidebarLocationOption("Documents", GetResource("SidebarDocumentsButton/Content"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "▤"),
			new SidebarLocationOption("Pictures", GetResource("SidebarPicturesButton/Content"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "▧"),
			new SidebarLocationOption("Music", GetResource("SidebarMusicButton/Content"), Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "♫"),
			new SidebarLocationOption("Movies", GetResource("SidebarMoviesButton/Content"), Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "▶"),
		}.Where(static location => !string.IsNullOrWhiteSpace(location.ExternalUrl) || Directory.Exists(location.Path)).ToArray();
	}

	public string[] ToggleSidebarSection(string sectionId)
	{
		if (sectionId is not ("Favorites" or "Recent" or "Libraries" or "Network" or "Drives"))
		{
			return settings.CollapsedSidebarSections ?? [];
		}

		var collapsedSections = (settings.CollapsedSidebarSections ?? []).ToHashSet(StringComparer.Ordinal);
		if (!collapsedSections.Add(sectionId))
		{
			collapsedSections.Remove(sectionId);
		}

		string[] result = collapsedSections.Order(StringComparer.Ordinal).ToArray();
		settings = settings with { CollapsedSidebarSections = result };
		RefreshLocations();
		return result;
	}

	private void AppendSection(
		List<SidebarLocation> target,
		string sectionId,
		string title,
		IReadOnlyCollection<SidebarLocation> items,
		IReadOnlySet<string> collapsedSections)
	{
		if (items.Count is 0)
		{
			return;
		}
		bool isExpanded = !collapsedSections.Contains(sectionId);
		target.Add(SidebarLocation.Header(
			title,
			sectionId,
			isExpanded,
			GetResource(isExpanded ? "SidebarSectionExpandedHelpText" : "SidebarSectionCollapsedHelpText")));
		if (isExpanded)
		{
			target.AddRange(items.Select(item => item with { SectionId = sectionId }));
		}
	}

	private string GetResource(string name)
	{
		string? value = resources.GetString(name);
		return string.IsNullOrWhiteSpace(value) ? name : value;
	}
}
