namespace Files.App.MacOS.Services;

internal sealed record FilePathRename(string SourcePath, string DestinationPath);

internal sealed class FileRenameService
{
	public static bool IsValidBaseName(string name)
	{
		return !string.IsNullOrWhiteSpace(name) &&
			name is not "." and not ".." &&
			!name.Contains('.') &&
			name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
			!name.Contains(Path.DirectorySeparatorChar) &&
			!name.Contains(Path.AltDirectorySeparatorChar);
	}

	public Task<FilePathRename> RenameAsync(
		string sourcePath,
		string desiredName,
		CancellationToken cancellationToken = default)
	{
		return Task.Run(() =>
		{
			ValidateName(desiredName);
			string source = Path.GetFullPath(sourcePath);
			string parent = Path.GetDirectoryName(source) ?? throw new FileOperationException(FileOperationError.MissingParent);
			var rename = new FilePathRename(source, Path.Combine(parent, desiredName));
			Execute([rename], cancellationToken);
			return rename;
		}, cancellationToken);
	}

	public Task<FilePathRename[]> BulkRenameAsync(
		IReadOnlyList<string> sourcePaths,
		string baseName,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(sourcePaths);
		return Task.Run(() =>
		{
			ValidateBaseName(baseName);
			string[] sources = sourcePaths
				.Select(Path.GetFullPath)
				.Distinct(StringComparer.Ordinal)
				.ToArray();
			EnsureNoOverlappingDirectories(sources);

			var sourceSet = sources.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var renames = new List<FilePathRename>(sources.Length);
			foreach (string source in sources)
			{
				cancellationToken.ThrowIfCancellationRequested();
				bool isDirectory = Directory.Exists(source);
				if (!isDirectory && !File.Exists(source))
				{
					throw new FileOperationException(FileOperationError.ItemNotFound, Path.GetFileName(source));
				}

				string parent = Path.GetDirectoryName(source) ?? throw new FileOperationException(FileOperationError.MissingParent);
				string extension = isDirectory ? string.Empty : Path.GetExtension(source);
				string destination = GetUniquePath(parent, baseName, extension, sourceSet, reserved);
				reserved.Add(destination);
				renames.Add(new(source, destination));
			}

			FilePathRename[] result = renames.ToArray();
			Execute(result, cancellationToken);
			return result;
		}, cancellationToken);
	}

	public Task RenamePathsAsync(FilePathRename[] renames, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(renames);
		return Task.Run(() => Execute(renames, cancellationToken), cancellationToken);
	}

	private static void Execute(FilePathRename[] requestedRenames, CancellationToken cancellationToken)
	{
		FilePathRename[] renames = requestedRenames
			.Where(static rename => !string.Equals(rename.SourcePath, rename.DestinationPath, StringComparison.Ordinal))
			.ToArray();
		if (renames.Length is 0)
		{
			return;
		}

		EnsureNoOverlappingDirectories(renames.Select(static rename => rename.SourcePath).ToArray());
		var sourcePaths = renames.Select(static rename => Path.GetFullPath(rename.SourcePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (FilePathRename rename in renames)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!PathExists(rename.SourcePath))
			{
				throw new FileOperationException(FileOperationError.ItemNotFound, Path.GetFileName(rename.SourcePath));
			}
			if (!destinations.Add(Path.GetFullPath(rename.DestinationPath)) ||
				(PathExists(rename.DestinationPath) && !sourcePaths.Contains(Path.GetFullPath(rename.DestinationPath))))
			{
				throw new FileOperationException(FileOperationError.AlreadyExists, Path.GetFileName(rename.DestinationPath));
			}
		}

		var staged = new List<StagedRename>(renames.Length);
		try
		{
			foreach (FilePathRename rename in renames)
			{
				cancellationToken.ThrowIfCancellationRequested();
				bool isDirectory = Directory.Exists(rename.SourcePath);
				string temporaryPath = GetTemporarySiblingPath(rename.SourcePath);
				Move(rename.SourcePath, temporaryPath, isDirectory);
				staged.Add(new(rename.SourcePath, temporaryPath, rename.DestinationPath, isDirectory));
			}

			foreach (StagedRename item in staged)
			{
				cancellationToken.ThrowIfCancellationRequested();
				Move(item.CurrentPath, item.DestinationPath, item.IsDirectory);
				item.CurrentPath = item.DestinationPath;
			}
		}
		catch
		{
			for (int index = staged.Count - 1; index >= 0; index--)
			{
				StagedRename item = staged[index];
				try
				{
					if (PathExists(item.CurrentPath) && !PathExists(item.OriginalPath))
					{
						Move(item.CurrentPath, item.OriginalPath, item.IsDirectory);
					}
				}
				catch (IOException)
				{
				}
			}
			throw;
		}
	}

	private static string GetUniquePath(
		string parentPath,
		string baseName,
		string extension,
		ISet<string> sourcePaths,
		ISet<string> reservedPaths)
	{
		for (int suffix = 1; ; suffix++)
		{
			string name = suffix is 1 ? $"{baseName}{extension}" : $"{baseName} ({suffix}){extension}";
			string destination = Path.Combine(parentPath, name);
			bool occupiedByUnselectedItem = PathExists(destination) && !sourcePaths.Contains(destination);
			if (!occupiedByUnselectedItem && !reservedPaths.Contains(destination))
			{
				return destination;
			}
		}
	}

	private static void EnsureNoOverlappingDirectories(IReadOnlyList<string> paths)
	{
		string[] selectedDirectories = paths
			.Where(Directory.Exists)
			.Select(static path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar)
			.ToArray();
		foreach (string path in paths)
		{
			string fullPath = Path.GetFullPath(path);
			if (selectedDirectories.Any(directory => fullPath.StartsWith(directory, StringComparison.Ordinal)))
			{
				throw new FileOperationException(FileOperationError.OverlappingSelection);
			}
		}
	}

	private static string GetTemporarySiblingPath(string sourcePath)
	{
		string parent = Path.GetDirectoryName(sourcePath) ?? throw new FileOperationException(FileOperationError.MissingParent);
		while (true)
		{
			string candidate = Path.Combine(parent, $".files-rename-{Guid.NewGuid():N}.tmp");
			if (!PathExists(candidate))
			{
				return candidate;
			}
		}
	}

	private static void Move(string source, string destination, bool isDirectory)
	{
		if (isDirectory)
		{
			Directory.Move(source, destination);
		}
		else
		{
			File.Move(source, destination);
		}
	}

	private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

	private static void ValidateBaseName(string name)
	{
		if (!IsValidBaseName(name))
		{
			throw new FileOperationException(string.IsNullOrWhiteSpace(name) ? FileOperationError.InvalidName : FileOperationError.InvalidCharacters);
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

	private sealed class StagedRename(
		string originalPath,
		string currentPath,
		string destinationPath,
		bool isDirectory)
	{
		public string OriginalPath { get; } = originalPath;

		public string CurrentPath { get; set; } = currentPath;

		public string DestinationPath { get; } = destinationPath;

		public bool IsDirectory { get; } = isDirectory;
	}
}
