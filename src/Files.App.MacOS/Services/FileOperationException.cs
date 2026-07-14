namespace Files.App.MacOS.Services;

public enum FileOperationError
{
	InvalidName,
	InvalidCharacters,
	MissingParent,
	AlreadyExists,
	ItemNotFound,
	OverlappingSelection,
	UndoDataUnavailable,
	HistoryTransferIncomplete,
	HistoryRollbackFailed,
}

public sealed class FileOperationException(FileOperationError error, string? itemName = null) : IOException(error.ToString())
{
	public FileOperationError Error { get; } = error;

	public string? ItemName { get; } = itemName;
}
