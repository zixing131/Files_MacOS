using Files.App.MacOS.Models;

namespace Files.App.MacOS.Services;

public interface IDirectoryService
{
	Task<IReadOnlyList<LocalFileSystemItem>> GetItemsAsync(string path, CancellationToken cancellationToken);
}
