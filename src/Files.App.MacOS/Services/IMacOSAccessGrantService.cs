using Files.App.MacOS.Models;

namespace Files.App.MacOS.Services;

public interface IMacOSAccessGrantService : IDisposable
{
	Task<FolderAccessGrant?> PickFolderAsync(string initialPath, CancellationToken cancellationToken = default);

	Task<IReadOnlyList<RestoredFolderAccessGrant>> RestoreAsync(
		IEnumerable<FolderAccessGrant> grants,
		CancellationToken cancellationToken = default);

	void Revoke(string path);
}

public sealed record RestoredFolderAccessGrant(string OriginalPath, FolderAccessGrant Grant);
