namespace Files.App.MacOS.Services;

internal sealed class FileTransferHistoryEntry(
	FileTransferMode mode,
	FileTransferRootResult[] roots)
{
	public FileTransferMode Mode { get; } = mode;

	public FileTransferRootResult[] Roots { get; } = roots;

	public FilePathRename[]? CopyUndoStaging { get; set; }
}
