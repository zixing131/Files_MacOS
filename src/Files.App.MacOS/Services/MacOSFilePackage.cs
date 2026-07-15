namespace Files.App.MacOS.Services;

internal static class MacOSFilePackage
{
	private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".action", ".app", ".appex", ".band", ".bundle", ".dictionary", ".framework",
		".garageband", ".imovielibrary", ".kext", ".key", ".logicx", ".mdimporter",
		".numbers", ".pages", ".photolibrary", ".photoslibrary", ".playground",
		".playgroundbook", ".plugin", ".prefpane", ".qlgenerator", ".rtfd", ".saver",
		".workflow", ".xcworkspace", ".xcodeproj",
	};

	public static bool IsPackage(FileSystemInfo info) =>
		info is DirectoryInfo && Extensions.Contains(info.Extension);

	public static bool IsInsidePackage(string path, string rootPath)
	{
		string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
		DirectoryInfo? parent = Directory.GetParent(Path.GetFullPath(path));
		while (parent is not null && parent.FullName.StartsWith(root, StringComparison.Ordinal))
		{
			if (IsPackage(parent))
			{
				return true;
			}
			if (string.Equals(parent.FullName, root, StringComparison.Ordinal))
			{
				break;
			}
			parent = parent.Parent;
		}

		return false;
	}
}
