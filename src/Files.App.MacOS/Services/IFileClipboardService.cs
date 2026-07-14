namespace Files.App.MacOS.Services;

public sealed record FileClipboardContent(
	IReadOnlyList<string> Paths,
	FileTransferMode Mode,
	long ChangeCount);

public interface IFileClipboardService
{
	Task<FileClipboardContent> WriteAsync(
		IReadOnlyList<string> paths,
		FileTransferMode mode,
		CancellationToken cancellationToken = default);

	Task<FileClipboardContent> ReadAsync(CancellationToken cancellationToken = default);

	Task<bool> ClearAsync(long expectedChangeCount, CancellationToken cancellationToken = default);
}
