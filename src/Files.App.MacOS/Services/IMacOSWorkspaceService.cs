using Files.App.MacOS.Models;

namespace Files.App.MacOS.Services;

public interface IMacOSWorkspaceService
{
	Task OpenAsync(string path, CancellationToken cancellationToken = default);

	Task<IReadOnlyList<OpenWithApplication>> GetOpenWithApplicationsAsync(string path, CancellationToken cancellationToken = default);

	Task OpenWithAsync(string path, string applicationPath, CancellationToken cancellationToken = default);

	Task<string?> PickApplicationAsync(CancellationToken cancellationToken = default);

	Task RevealAsync(string path, CancellationToken cancellationToken = default);

	Task OpenTerminalAsync(string path, TerminalPreference terminal, CancellationToken cancellationToken = default);

	Task<NetworkConnectionResult> ConnectServerAsync(string address, CancellationToken cancellationToken = default);

	Task<IReadOnlyList<TrashedItemResult>> MoveToTrashAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

	Task PreviewAsync(string path, CancellationToken cancellationToken = default);

	Task ShareAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

	Task<byte[]?> GetThumbnailPngAsync(string path, int width, int height, double scale, CancellationToken cancellationToken = default);
}

public sealed record TrashedItemResult(string OriginalPath, string TrashPath);

public sealed record OpenWithApplication(
	[property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
	[property: System.Text.Json.Serialization.JsonPropertyName("applicationPath")] string ApplicationPath,
	[property: System.Text.Json.Serialization.JsonPropertyName("bundleIdentifier")] string BundleIdentifier,
	[property: System.Text.Json.Serialization.JsonPropertyName("isDefault")] bool IsDefault);

public sealed class TrashOperationPartialException(
	string message,
	IReadOnlyList<TrashedItemResult> completedItems) : IOException(message)
{
	public IReadOnlyList<TrashedItemResult> CompletedItems { get; } = completedItems;
}
