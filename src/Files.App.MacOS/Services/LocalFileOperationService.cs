namespace Files.App.MacOS.Services;

public sealed class LocalFileOperationService : IFileOperationService
{
	public Task<string> CreateFolderAsync(string parentPath, string desiredName, CancellationToken cancellationToken = default)
	{
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			ValidateName(desiredName);

			string destination = GetUniquePath(parentPath, desiredName);
			Directory.CreateDirectory(destination);
			return destination;
		}, cancellationToken);
	}

	public Task<string> RenameAsync(string sourcePath, string desiredName, CancellationToken cancellationToken = default)
	{
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			ValidateName(desiredName);

			string? parentPath = Path.GetDirectoryName(sourcePath);
			if (string.IsNullOrEmpty(parentPath))
			{
				throw new FileOperationException(FileOperationError.MissingParent);
			}

			string destination = Path.Combine(parentPath, desiredName);
			if (File.Exists(destination) || Directory.Exists(destination))
			{
				throw new FileOperationException(FileOperationError.AlreadyExists, desiredName);
			}

			if (Directory.Exists(sourcePath))
			{
				Directory.Move(sourcePath, destination);
			}
			else if (File.Exists(sourcePath))
			{
				File.Move(sourcePath, destination);
			}
			else
			{
				throw new FileOperationException(FileOperationError.ItemNotFound, Path.GetFileName(sourcePath));
			}

			return destination;
		}, cancellationToken);
	}

	public Task<string> CreateFileAsync(string parentPath, string desiredName, CancellationToken cancellationToken = default)
	{
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			ValidateName(desiredName);

			string destination = GetUniqueFilePath(parentPath, desiredName);
			using (File.Create(destination))
			{
			}
			return destination;
		}, cancellationToken);
	}

	private static string GetUniquePath(string parentPath, string desiredName)
	{
		string destination = Path.Combine(parentPath, desiredName);
		if (!File.Exists(destination) && !Directory.Exists(destination))
		{
			return destination;
		}

		for (int suffix = 2; ; suffix++)
		{
			destination = Path.Combine(parentPath, $"{desiredName} ({suffix})");
			if (!File.Exists(destination) && !Directory.Exists(destination))
			{
				return destination;
			}
		}
	}

	private static string GetUniqueFilePath(string parentPath, string desiredName)
	{
		string destination = Path.Combine(parentPath, desiredName);
		if (!File.Exists(destination) && !Directory.Exists(destination))
		{
			return destination;
		}

		string name = Path.GetFileNameWithoutExtension(desiredName);
		string extension = Path.GetExtension(desiredName);
		for (int suffix = 2; ; suffix++)
		{
			destination = Path.Combine(parentPath, $"{name} ({suffix}){extension}");
			if (!File.Exists(destination) && !Directory.Exists(destination))
			{
				return destination;
			}
		}
	}

	private static void ValidateName(string name)
	{
		if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
		{
			throw new FileOperationException(FileOperationError.InvalidName);
		}

		if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
			name.Contains(Path.DirectorySeparatorChar) ||
			name.Contains(Path.AltDirectorySeparatorChar))
		{
			throw new FileOperationException(FileOperationError.InvalidCharacters);
		}
	}
}
