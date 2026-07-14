namespace Files.App.MacOS.Services;

public interface IFileOperationService
{
	Task<string> CreateFolderAsync(string parentPath, string desiredName, CancellationToken cancellationToken = default);

	Task<string> CreateFileAsync(string parentPath, string desiredName, CancellationToken cancellationToken = default);

	Task<string> RenameAsync(string sourcePath, string desiredName, CancellationToken cancellationToken = default);

}
