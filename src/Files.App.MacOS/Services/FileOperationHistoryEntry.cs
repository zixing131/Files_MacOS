namespace Files.App.MacOS.Services;

internal sealed record FileOperationHistoryEntry(
	FilePathRename[] Renames,
	string? CreatedPath = null,
	bool CreatedDirectory = false,
	FileTransferHistoryEntry? Transfer = null,
	FileTrashHistoryEntry? Trash = null);

internal sealed class FileTrashHistoryEntry(IReadOnlyList<TrashedItemResult> items)
{
	public IReadOnlyList<TrashedItemResult> Items { get; set; } = items;
}
