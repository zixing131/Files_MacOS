using Files.App.MacOS.Models;

namespace Files.App.MacOS.Services;

public interface IFileSearchService
{
	Task<IReadOnlyList<LocalFileSystemItem>> SearchAsync(
		string rootPath,
		FileSearchQuery query,
		bool includeHidden,
		IProgress<IReadOnlyList<LocalFileSystemItem>>? progress = null,
		CancellationToken cancellationToken = default);
}
