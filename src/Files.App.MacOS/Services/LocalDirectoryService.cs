using Files.App.MacOS.Models;

namespace Files.App.MacOS.Services;

public sealed class LocalDirectoryService : IDirectoryService
{
	public Task<IReadOnlyList<LocalFileSystemItem>> GetItemsAsync(string path, CancellationToken cancellationToken)
	{
		return Task.Run<IReadOnlyList<LocalFileSystemItem>>(() =>
		{
			var items = new List<LocalFileSystemItem>();

			foreach (FileSystemInfo info in new DirectoryInfo(path).EnumerateFileSystemInfos())
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (InternalFileArtifact.IsPath(info.FullName))
				{
					continue;
				}

				try
				{
					System.IO.FileAttributes attributes = info.Attributes;
					bool isDirectory = attributes.HasFlag(System.IO.FileAttributes.Directory);
					bool isPackage = isDirectory && MacOSFilePackage.IsPackage(info);
					bool isHidden = attributes.HasFlag(System.IO.FileAttributes.Hidden) || info.Name.StartsWith('.');
					long? size = info is FileInfo fileInfo ? fileInfo.Length : null;

					items.Add(new(
						info.FullName,
						info.Name,
						isDirectory,
						isHidden,
						size,
						info.LastWriteTimeUtc,
						isPackage,
						info.CreationTimeUtc));
				}
				catch (IOException)
				{
				}
				catch (UnauthorizedAccessException)
				{
				}
			}

			return items.ToArray();
		}, cancellationToken);
	}
}
