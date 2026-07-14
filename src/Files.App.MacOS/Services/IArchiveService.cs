namespace Files.App.MacOS.Services;

public sealed record ArchiveProgress(
	int CompletedItems,
	int TotalItems,
	long CompletedBytes,
	long TotalBytes,
	string CurrentItem);

public interface IArchiveService
{
	Task<string> CreateZipAsync(
		IReadOnlyList<string> sourcePaths,
		string destinationDirectory,
		string archiveName,
		IProgress<ArchiveProgress>? progress = null,
		CancellationToken cancellationToken = default);

	Task<string> ExtractZipAsync(
		string archivePath,
		string destinationDirectory,
		string folderName,
		IProgress<ArchiveProgress>? progress = null,
		CancellationToken cancellationToken = default);
}
