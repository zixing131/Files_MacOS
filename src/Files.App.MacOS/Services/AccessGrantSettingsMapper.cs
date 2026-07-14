using Files.App.MacOS.Models;

namespace Files.App.MacOS.Services;

internal static class AccessGrantSettingsMapper
{
	public static AppSettings Apply(
		AppSettings settings,
		IReadOnlyList<RestoredFolderAccessGrant> restoredGrants)
	{
		FolderAccessGrant[] activeGrants = restoredGrants.Select(static result => result.Grant).ToArray();
		(RestoredFolderAccessGrant Result, string Prefix)[] movedGrants = restoredGrants
			.Where(static result => !string.Equals(result.OriginalPath, result.Grant.Path, StringComparison.Ordinal))
			.OrderByDescending(static result => result.OriginalPath.Length)
			.Select(static result => (result, EnsureTrailingSeparator(result.OriginalPath)))
			.ToArray();
		if (movedGrants.Length is 0)
		{
			return settings with { AccessGrants = activeGrants };
		}

		string RemapPath(string path)
		{
			foreach ((RestoredFolderAccessGrant result, string prefix) in movedGrants)
			{
				if (string.Equals(path, result.OriginalPath, StringComparison.Ordinal))
				{
					return result.Grant.Path;
				}
				if (path.StartsWith(prefix, StringComparison.Ordinal))
				{
					return Path.Combine(result.Grant.Path, path[prefix.Length..]);
				}
			}
			return path;
		}

		BrowserPaneState RemapPane(BrowserPaneState pane) => pane with { Path = RemapPath(pane.Path) };
		WorkspaceState? workspace = settings.Workspace?.Tabs is { } tabs
			? settings.Workspace with
			{
				Tabs = tabs.Select(tab => tab with
				{
					Primary = RemapPane(tab.Primary),
					Secondary = tab.Secondary is null ? null : RemapPane(tab.Secondary),
				}).ToArray(),
			}
			: settings.Workspace;
		WorkspaceState[]? additionalWindowWorkspaces = settings.AdditionalWindowWorkspaces?.Select(windowWorkspace =>
			windowWorkspace.Tabs is { } windowTabs
				? windowWorkspace with
				{
					Tabs = windowTabs.Select(tab => tab with
					{
						Primary = RemapPane(tab.Primary),
						Secondary = tab.Secondary is null ? null : RemapPane(tab.Secondary),
					}).ToArray(),
				}
				: windowWorkspace).ToArray();
		return settings with
		{
			AccessGrants = activeGrants,
			FavoritePaths = settings.FavoritePaths?.Select(RemapPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
			SavedSearches = settings.SavedSearches?.Select(search => search with { RootPath = RemapPath(search.RootPath) }).ToArray(),
			Workspace = workspace,
			AdditionalWindowWorkspaces = additionalWindowWorkspaces,
		};
	}

	private static string EnsureTrailingSeparator(string path)
	{
		return Path.EndsInDirectorySeparator(path) ? path : string.Concat(path, Path.DirectorySeparatorChar);
	}
}
