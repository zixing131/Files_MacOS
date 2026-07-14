using Files.App.MacOS.Models;

namespace Files.App.MacOS.Services;

internal static class PathBreadcrumbBuilder
{
	public static PathBreadcrumbItem[] Build(string path, string homePath, string homeTitle)
	{
		string fullPath = Path.GetFullPath(path);
		string fullHomePath = Path.GetFullPath(homePath);
		string relativeToHome = Path.GetRelativePath(fullHomePath, fullPath);
		bool isInHome = relativeToHome is "." ||
			(!Path.IsPathRooted(relativeToHome) &&
				relativeToHome is not ".." &&
				!relativeToHome.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
		var items = new List<PathBreadcrumbItem>();
		if (isInHome)
		{
			items.Add(new(homeTitle, fullHomePath, IsHome: true));
			AppendRelativeSegments(items, fullHomePath, relativeToHome);
			return items.ToArray();
		}

		string root = Path.GetPathRoot(fullPath) ?? Path.DirectorySeparatorChar.ToString();
		items.Add(new(root, root));
		AppendRelativeSegments(items, root, Path.GetRelativePath(root, fullPath));
		return items.ToArray();
	}

	private static void AppendRelativeSegments(
		List<PathBreadcrumbItem> items,
		string startingPath,
		string relativePath)
	{
		if (relativePath is ".")
		{
			return;
		}

		string currentPath = startingPath;
		foreach (string segment in relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
		{
			currentPath = Path.Combine(currentPath, segment);
			items.Add(new(segment, currentPath));
		}
	}
}
