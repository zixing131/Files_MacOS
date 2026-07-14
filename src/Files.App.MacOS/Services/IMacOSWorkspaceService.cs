using Files.App.MacOS.Models;

namespace Files.App.MacOS.Services;

public interface IMacOSWorkspaceService
{
	Task OpenAsync(string path, CancellationToken cancellationToken = default);

	Task RevealAsync(string path, CancellationToken cancellationToken = default);

	Task OpenTerminalAsync(string path, CancellationToken cancellationToken = default);

	Task<NetworkConnectionResult> ConnectServerAsync(string address, CancellationToken cancellationToken = default);

	Task<IReadOnlyList<TrashedItemResult>> MoveToTrashAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

	Task PreviewAsync(string path, CancellationToken cancellationToken = default);

	Task ShareAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

	Task<byte[]?> GetThumbnailPngAsync(string path, int width, int height, double scale, CancellationToken cancellationToken = default);
}

public sealed record TrashedItemResult(string OriginalPath, string TrashPath);

public sealed class TrashOperationPartialException(
	string message,
	IReadOnlyList<TrashedItemResult> completedItems) : IOException(message)
{
	public IReadOnlyList<TrashedItemResult> CompletedItems { get; } = completedItems;
}
