using System.Text.Json;
using Files.App.MacOS.Models;

namespace Files.App.MacOS.Services;

public sealed class JsonAppSettingsService : IAppSettingsService
{
	internal static string DefaultSettingsPath => Environment.GetEnvironmentVariable("FILES_MACOS_SETTINGS_PATH") is { Length: > 0 } diagnosticPath
		? diagnosticPath
		: Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			"Library",
			"Application Support",
			"io.filescommunity.files.macos",
			"settings.json");

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
	};

	private readonly string settingsPath;

	public JsonAppSettingsService(string? settingsPath = null)
	{
		this.settingsPath = settingsPath ?? DefaultSettingsPath;
	}

	public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
	{
		if (!File.Exists(settingsPath))
		{
			return new AppSettings();
		}

		try
		{
			await using FileStream stream = File.OpenRead(settingsPath);
			AppSettings settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken) ?? new AppSettings();
			return Normalize(settings);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			return new AppSettings();
		}
	}

	public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(settings);
		string? directory = Path.GetDirectoryName(settingsPath);
		if (string.IsNullOrEmpty(directory))
		{
			throw new IOException("The settings directory isn't available.");
		}

		Directory.CreateDirectory(directory);
		string temporaryPath = Path.Combine(directory, $"settings-{Guid.NewGuid():N}.tmp");
		try
		{
			await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
				await stream.FlushAsync(cancellationToken);
			}
			File.Move(temporaryPath, settingsPath, overwrite: true);
			File.SetUnixFileMode(settingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
		}
	}

	private static AppSettings Normalize(AppSettings settings)
	{
		AppThemePreference theme = Enum.IsDefined(settings.Theme) ? settings.Theme : AppThemePreference.System;
		AppLanguagePreference language = Enum.IsDefined(settings.Language) ? settings.Language : AppLanguagePreference.System;
		string[] favoritePaths = NormalizeStrings(settings.FavoritePaths, 100, StringComparer.OrdinalIgnoreCase);
		string[] recentPaths = NormalizeStrings(settings.RecentPaths, 8, StringComparer.Ordinal);
		string[] recentServers = NormalizeStrings(settings.RecentServers, 20, StringComparer.OrdinalIgnoreCase);
		string[] searchHistory = NormalizeStrings(settings.SearchHistory, 20, StringComparer.CurrentCultureIgnoreCase);
		string[] collapsedSidebarSections = NormalizeStrings(settings.CollapsedSidebarSections, 5, StringComparer.Ordinal)
			.Where(static section => section is "Favorites" or "Recent" or "Libraries" or "Network" or "Drives")
			.ToArray();
		SavedSearch[] savedSearches = (settings.SavedSearches ?? [])
			.Where(static search => search is not null &&
				!string.IsNullOrWhiteSpace(search.Name) &&
				!string.IsNullOrWhiteSpace(search.Query) &&
				!string.IsNullOrWhiteSpace(search.RootPath))
			.DistinctBy(static search => search.Name, StringComparer.CurrentCultureIgnoreCase)
			.Take(20)
			.ToArray();
		FolderAccessGrant[] accessGrants = (settings.AccessGrants ?? [])
			.Where(static grant => grant is not null &&
				!string.IsNullOrWhiteSpace(grant.Path) &&
				!string.IsNullOrWhiteSpace(grant.Bookmark))
			.Select(static grant => new FolderAccessGrant(grant.Path.Trim(), grant.Bookmark.Trim()))
			.DistinctBy(static grant => grant.Path, StringComparer.Ordinal)
			.Take(100)
			.ToArray();
		WorkspaceState? workspace = NormalizeWorkspace(settings.Workspace);
		WorkspaceState[] additionalWindowWorkspaces = (settings.AdditionalWindowWorkspaces ?? [])
			.Select(NormalizeWorkspace)
			.OfType<WorkspaceState>()
			.Take(7)
			.ToArray();
		int windowCount = additionalWindowWorkspaces.Length + 1;
		WindowPlacementState? windowPlacement = NormalizeWindowPlacement(settings.WindowPlacement);
		WindowPlacementState?[] additionalWindowPlacements = Enumerable.Range(0, additionalWindowWorkspaces.Length)
			.Select(index => settings.AdditionalWindowPlacements is { } placements && index < placements.Length
				? NormalizeWindowPlacement(placements[index])
				: null)
			.ToArray();
		return settings with
		{
			Theme = theme,
			Language = language,
			FavoritePaths = favoritePaths,
			RecentPaths = recentPaths,
			RecentServers = recentServers,
			SearchHistory = searchHistory,
			CollapsedSidebarSections = collapsedSidebarSections,
			SavedSearches = savedSearches,
			Workspace = workspace,
			AdditionalWindowWorkspaces = additionalWindowWorkspaces,
			ActiveWindowIndex = Math.Clamp(settings.ActiveWindowIndex, 0, windowCount - 1),
			WindowPlacement = windowPlacement,
			AdditionalWindowPlacements = additionalWindowPlacements,
			AccessGrants = accessGrants,
			IsSidebarOpen = settings.SchemaVersion < 4 || settings.IsSidebarOpen,
			SidebarWidth = Math.Clamp(settings.SidebarWidth, 180, 420),
			SchemaVersion = 11,
		};
	}

	private static WindowPlacementState? NormalizeWindowPlacement(WindowPlacementState? placement)
	{
		if (placement is null ||
			!double.IsFinite(placement.X) ||
			!double.IsFinite(placement.Y) ||
			!double.IsFinite(placement.Width) ||
			!double.IsFinite(placement.Height) ||
			Math.Abs(placement.X) > 100_000 ||
			Math.Abs(placement.Y) > 100_000)
		{
			return null;
		}

		return placement with
		{
			Width = Math.Clamp(placement.Width, 640, 20_000),
			Height = Math.Clamp(placement.Height, 480, 20_000),
		};
	}

	private static WorkspaceState? NormalizeWorkspace(WorkspaceState? workspace)
	{
		if (workspace?.Tabs is not { Length: > 0 } tabs)
		{
			return null;
		}

		BrowserTabState[] normalizedTabs = tabs
			.Where(static tab => tab is not null && tab.Primary is not null && !string.IsNullOrWhiteSpace(tab.Primary.Path))
			.Take(20)
			.Select(NormalizeTab)
			.ToArray();
		return normalizedTabs.Length is 0
			? null
			: new(normalizedTabs, Math.Clamp(workspace.ActiveTabIndex, 0, normalizedTabs.Length - 1));
	}

	private static BrowserTabState NormalizeTab(BrowserTabState tab)
	{
		BrowserPaneState? secondary = tab.Secondary is { Path.Length: > 0 } pane ? NormalizePane(pane) : null;
		return tab with
		{
			Primary = NormalizePane(tab.Primary),
			Secondary = secondary,
			SplitRatio = double.IsFinite(tab.SplitRatio) ? Math.Clamp(tab.SplitRatio, 0.2, 0.8) : 0.5,
			IsSecondaryActive = secondary is not null && tab.IsSecondaryActive,
		};
	}

	private static BrowserPaneState NormalizePane(BrowserPaneState pane)
	{
		string sortField = pane.SortField is "Name" or "Modified" or "Size" ? pane.SortField : "Name";
		string sortDirection = pane.SortDirection is "Ascending" or "Descending" ? pane.SortDirection : "Ascending";
		return pane with { SortField = sortField, SortDirection = sortDirection };
	}

	private static string[] NormalizeStrings(string[]? values, int maximumCount, IEqualityComparer<string> comparer)
	{
		return (values ?? [])
			.Where(static value => !string.IsNullOrWhiteSpace(value))
			.Select(static value => value.Trim())
			.Distinct(comparer)
			.Take(maximumCount)
			.ToArray();
	}
}
