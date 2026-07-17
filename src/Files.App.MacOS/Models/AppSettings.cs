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

public enum TerminalPreference
{
	Terminal,
	ITerm2,
	Warp,
	Kitty,
	Alacritty,
	WezTerm,
}

public enum ContextMenuLevel
{
	Hidden,
	Primary,
	Secondary,
}

public sealed record ContextMenuActionSetting(string Action, ContextMenuLevel Level)
{
	public static ContextMenuActionSetting[] CreateDefaults() =>
	[
		new("Open", ContextMenuLevel.Primary),
		new("OpenWith", ContextMenuLevel.Primary),
		new("OpenInNewTab", ContextMenuLevel.Primary),
		new("Preview", ContextMenuLevel.Primary),
		new("PutBack", ContextMenuLevel.Primary),
		new("Cut", ContextMenuLevel.Primary),
		new("Copy", ContextMenuLevel.Primary),
		new("Rename", ContextMenuLevel.Primary),
		new("MoveToTrash", ContextMenuLevel.Primary),
		new("Properties", ContextMenuLevel.Primary),
		new("Reveal", ContextMenuLevel.Secondary),
		new("Terminal", ContextMenuLevel.Secondary),
		new("Duplicate", ContextMenuLevel.Secondary),
		new("CreateSymbolicLink", ContextMenuLevel.Secondary),
		new("CopyPath", ContextMenuLevel.Secondary),
		new("Share", ContextMenuLevel.Secondary),
		new("AirDrop", ContextMenuLevel.Secondary),
		new("Compress", ContextMenuLevel.Secondary),
		new("Extract", ContextMenuLevel.Secondary),
		new("Favorite", ContextMenuLevel.Secondary),
		new("PermanentDelete", ContextMenuLevel.Secondary),
	];
}

public sealed record AppSettings(
	AppThemePreference Theme = AppThemePreference.System,
	bool ShowHiddenFiles = false,
	bool UseGridViewForNewTabs = true,
	string[]? FavoritePaths = null,
	string[]? RecentPaths = null,
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
	WorkspaceState[]? AdditionalWindowWorkspaces = null,
	int ActiveWindowIndex = 0,
	WindowPlacementState? WindowPlacement = null,
	WindowPlacementState?[]? AdditionalWindowPlacements = null,
	bool ReverseTabScrollDirection = false,
	string[]? HiddenDefaultSidebarLocations = null,
	TerminalPreference Terminal = TerminalPreference.Terminal,
	bool ConfirmMoveToTrash = true,
	string[]? DetailColumns = null,
	DetailColumnWidthSetting[]? DetailColumnWidths = null,
	ContextMenuActionSetting[]? ContextMenuActions = null,
	int SchemaVersion = 17);

public sealed record DetailColumnWidthSetting(string Column, double Width);

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

public sealed record WindowPlacementState(
	double X,
	double Y,
	double Width,
	double Height,
	bool IsMaximized = false);
