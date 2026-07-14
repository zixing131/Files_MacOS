namespace Files.App.MacOS.Models;

public enum AppThemePreference
{
	System,
	Light,
	Dark,
}

public enum AppLanguagePreference
{
	System,
	English,
	SimplifiedChinese,
}

public sealed record AppSettings(
	AppThemePreference Theme = AppThemePreference.System,
	bool ShowHiddenFiles = false,
	bool UseGridViewForNewTabs = true,
	string[]? FavoritePaths = null,
	string[]? RecentServers = null,
	string[]? SearchHistory = null,
	SavedSearch[]? SavedSearches = null,
	WorkspaceState? Workspace = null,
	FolderAccessGrant[]? AccessGrants = null,
	bool IsSidebarOpen = true,
	bool IsPreviewPaneOpen = false,
	double SidebarWidth = 228,
	AppLanguagePreference Language = AppLanguagePreference.System,
	string[]? CollapsedSidebarSections = null,
	int SchemaVersion = 8);

public sealed record FolderAccessGrant(string Path, string Bookmark);

public sealed record SavedSearch(string Name, string Query, string RootPath);

public sealed record BrowserPaneState(
	string Path,
	bool IsGridView = true,
	string SortField = "Name",
	string SortDirection = "Ascending");

public sealed record BrowserTabState(
	BrowserPaneState Primary,
	BrowserPaneState? Secondary = null,
	double SplitRatio = 0.5,
	bool IsSecondaryActive = false);

public sealed record WorkspaceState(BrowserTabState[]? Tabs = null, int ActiveTabIndex = 0);
