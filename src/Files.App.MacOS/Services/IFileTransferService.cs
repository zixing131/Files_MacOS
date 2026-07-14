namespace Files.App.MacOS.Services;

public enum FileTransferMode
{
	Copy,
	Move,
}

public enum FileConflictResolution
{
	KeepBoth,
	Replace,
	Skip,
}

public sealed record FileTransferRequest(
	IReadOnlyList<string> SourcePaths,
	string DestinationDirectory,
	FileTransferMode Mode,
	FileConflictResolution ConflictResolution,
	IReadOnlyDictionary<string, string>? DestinationNames = null,
	bool PreserveReplacedItems = false);

public sealed record FileTransferProgress(
	int CompletedItems,
	int TotalItems,
	long CompletedBytes,
	long TotalBytes,
	string CurrentItem);

public sealed record FileTransferRootResult(
	string SourcePath,
	string DestinationPath,
	string? ReplacedItemBackupPath = null);

public sealed record FileTransferResult(
	int CompletedRootItems,
	int SkippedRootItems,
	FileTransferRootResult[] CompletedRoots);

public sealed class FileTransferCanceledException(
	FileTransferRootResult[] completedRoots,
	CancellationToken cancellationToken) : OperationCanceledException("The file transfer was canceled after completing some items.", cancellationToken)
{
	public FileTransferRootResult[] CompletedRoots { get; } = completedRoots;
}

public sealed class FileTransferPartialException(
	FileTransferRootResult[] completedRoots,
	Exception operationException) : IOException("The file transfer stopped after completing some items.", operationException)
{
	public FileTransferRootResult[] CompletedRoots { get; } = completedRoots;
}

public interface IFileTransferService
{
	Task<FileTransferResult> TransferAsync(
		FileTransferRequest request,
		IProgress<FileTransferProgress>? progress = null,
		CancellationToken cancellationToken = default);
}
