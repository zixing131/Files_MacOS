namespace Files.App.MacOS.Services;

internal static class DirectoryEmptyState
{
	public static bool IsEmptyFolder(bool isLoading, int visibleItemCount, string? searchText)
	{
		return !isLoading && visibleItemCount is 0 && string.IsNullOrWhiteSpace(searchText);
	}

	public static bool HasNoSearchResults(bool isLoading, int visibleItemCount, string? searchText)
	{
		return !isLoading && visibleItemCount is 0 && !string.IsNullOrWhiteSpace(searchText);
	}
}
