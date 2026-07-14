namespace Files.App.MacOS.Services;

internal static class InternalFileArtifact
{
	private static readonly string[] ReservedPrefixes =
	[
		".Files-transfer-",
		".Files-undo-",
		".Files-replaced-",
		".files-rename-",
	];

	public static bool IsPath(string path)
	{
		return Path.GetFullPath(path)
			.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
			.Any(component => ReservedPrefixes.Any(prefix => component.StartsWith(prefix, StringComparison.Ordinal)));
	}
}
