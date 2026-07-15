using Files.App.MacOS.Interop;

namespace Files.App.MacOS.Services;

internal static class MacOSFilePackage
{
	private static int nativeProbeAvailability;
	private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".action", ".app", ".appex", ".band", ".bundle", ".dictionary", ".framework",
		".garageband", ".imovielibrary", ".kext", ".key", ".logicx", ".mdimporter",
		".numbers", ".pages", ".photolibrary", ".photoslibrary", ".playground",
		".playgroundbook", ".plugin", ".prefpane", ".qlgenerator", ".rtfd", ".saver",
		".workflow", ".xcworkspace", ".xcodeproj",
	};

	public static bool IsPackage(FileSystemInfo info)
	{
		if (info is not DirectoryInfo)
		{
			return false;
		}

		return Extensions.Contains(info.Extension) ||
			TryGetNativePackageState(info.FullName, out bool isPackage) && isPackage;
	}

	internal static bool TryGetNativePackageState(string path, out bool isPackage)
	{
		isPackage = false;
		if (Volatile.Read(ref nativeProbeAvailability) < 0)
		{
			return false;
		}

		try
		{
			int result = MacOSNativeMethods.IsFilePackage(Path.GetFullPath(path));
			Volatile.Write(ref nativeProbeAvailability, 1);
			if (result < 0)
			{
				return false;
			}

			isPackage = result > 0;
			return true;
		}
		catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
		{
			Volatile.Write(ref nativeProbeAvailability, -1);
			return false;
		}
	}

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
