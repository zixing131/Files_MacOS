namespace Files.App.MacOS.Services;

public sealed class LocalFileTransferService : IFileTransferService
{
	private const int BufferSize = 1024 * 1024;

	public Task<FileTransferResult> TransferAsync(
		FileTransferRequest request,
		IProgress<FileTransferProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		return Task.Run(() => TransferCore(request, progress, cancellationToken), cancellationToken);
	}

	private static FileTransferResult TransferCore(
		FileTransferRequest request,
		IProgress<FileTransferProgress>? progress,
		CancellationToken cancellationToken)
	{
		string destinationDirectory = Path.GetFullPath(request.DestinationDirectory);
		if (!Directory.Exists(destinationDirectory))
		{
			throw new DirectoryNotFoundException(destinationDirectory);
		}

		string[] sourcePaths = request.SourcePaths
			.Where(static path => !string.IsNullOrWhiteSpace(path))
			.Select(static path => Path.GetFullPath(path))
			.Distinct(StringComparer.Ordinal)
			.ToArray();

		if (sourcePaths.Length is 0)
		{
			return new FileTransferResult(0, 0, []);
		}

		var totals = new TransferTotals();
		foreach (string sourcePath in sourcePaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			EnsureSourceExists(sourcePath);
			EnsureDestinationIsOutsideSource(sourcePath, destinationDirectory);
			CountTree(sourcePath, totals, cancellationToken);
		}

		var state = new TransferState(totals.Items, totals.Bytes, progress);
		int completedRoots = 0;
		int skippedRoots = 0;
		var completedRootPaths = new List<FileTransferRootResult>(sourcePaths.Length);

		try
		{
			foreach (string sourcePath in sourcePaths)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string destinationName = GetDestinationName(request, sourcePath);
				string desiredPath = Path.Combine(destinationDirectory, destinationName);

				if (request.Mode is FileTransferMode.Move && PathsEqual(sourcePath, desiredPath))
				{
					skippedRoots++;
					continue;
				}

				string? finalPath = ResolveDestination(desiredPath, request.ConflictResolution);
				if (finalPath is null)
				{
					skippedRoots++;
					continue;
				}

				string committedFinalPath = finalPath;
				string? replacedItemBackupPath = null;
				MacOSFileCoordinator.Coordinate(
				sourcePath,
				finalPath,
				request.Mode is FileTransferMode.Move,
				async (coordinatedSourcePath, coordinatedFinalPath) =>
				{
					string? coordinatedDestinationDirectory = Path.GetDirectoryName(coordinatedFinalPath);
					if (string.IsNullOrEmpty(coordinatedDestinationDirectory))
					{
						throw new IOException("The coordinated destination has no parent directory.");
					}

					string stagingPath = GetStagingPath(coordinatedDestinationDirectory);
					try
					{
						await CopyEntryAsync(coordinatedSourcePath, stagingPath, state, cancellationToken);
						replacedItemBackupPath = CommitStagedEntry(
							stagingPath,
							coordinatedFinalPath,
							request.ConflictResolution,
							request.PreserveReplacedItems);
						committedFinalPath = coordinatedFinalPath;

						if (request.Mode is FileTransferMode.Move)
						{
							DeleteEntry(coordinatedSourcePath);
						}
					}
					catch
					{
						TryDeleteEntry(stagingPath);
						throw;
					}
				},
					cancellationToken);
				completedRoots++;
				completedRootPaths.Add(new(sourcePath, committedFinalPath, replacedItemBackupPath));
			}
		}
		catch (OperationCanceledException) when (completedRootPaths.Count > 0)
		{
			throw new FileTransferCanceledException(completedRootPaths.ToArray(), cancellationToken);
		}
		catch (Exception ex) when (completedRootPaths.Count > 0)
		{
			throw new FileTransferPartialException(completedRootPaths.ToArray(), ex);
		}

		return new FileTransferResult(completedRoots, skippedRoots, completedRootPaths.ToArray());
	}

	private static string GetDestinationName(FileTransferRequest request, string sourcePath)
	{
		string name = request.DestinationNames is not null &&
			request.DestinationNames.TryGetValue(sourcePath, out string? requestedName)
			? requestedName
			: Path.GetFileName(sourcePath);
		if (string.IsNullOrWhiteSpace(name) ||
			name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
			name.Contains(Path.DirectorySeparatorChar) ||
			name.Contains(Path.AltDirectorySeparatorChar))
		{
			throw new FileOperationException(FileOperationError.InvalidName, name);
		}

		return name;
	}

	private static async Task CopyEntryAsync(
		string sourcePath,
		string destinationPath,
		TransferState state,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		System.IO.FileAttributes attributes = File.GetAttributes(sourcePath);
		bool isDirectory = attributes.HasFlag(System.IO.FileAttributes.Directory);
		string? linkTarget = GetLinkTarget(sourcePath, isDirectory);

		if (linkTarget is not null)
		{
			if (isDirectory)
			{
				Directory.CreateSymbolicLink(destinationPath, linkTarget);
			}
			else
			{
				File.CreateSymbolicLink(destinationPath, linkTarget);
			}

			MacOSFileMetadata.Copy(sourcePath, destinationPath);
			state.CompleteItem(sourcePath, 0);
			return;
		}

		if (isDirectory)
		{
			Directory.CreateDirectory(destinationPath);

			foreach (string childPath in Directory.EnumerateFileSystemEntries(sourcePath))
			{
				await CopyEntryAsync(childPath, Path.Combine(destinationPath, Path.GetFileName(childPath)), state, cancellationToken);
			}

			Directory.SetLastWriteTimeUtc(destinationPath, Directory.GetLastWriteTimeUtc(sourcePath));
			CopyUnixMode(sourcePath, destinationPath);
			MacOSFileMetadata.Copy(sourcePath, destinationPath);
			state.CompleteItem(sourcePath, 0);
			return;
		}

		long copiedBytes = 0;
		await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
		await using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
		{
			byte[] buffer = new byte[BufferSize];
			int bytesRead;
			while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
			{
				await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
				copiedBytes += bytesRead;
				state.ReportBytes(sourcePath, bytesRead);
			}
		}

		File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
		CopyUnixMode(sourcePath, destinationPath);
		MacOSFileMetadata.Copy(sourcePath, destinationPath);
		state.CompleteItem(sourcePath, copiedBytes);
	}

	private static void CountTree(string path, TransferTotals totals, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		System.IO.FileAttributes attributes = File.GetAttributes(path);
		bool isDirectory = attributes.HasFlag(System.IO.FileAttributes.Directory);
		string? linkTarget = GetLinkTarget(path, isDirectory);

		totals.Items++;
		if (linkTarget is not null)
		{
			return;
		}

		if (!isDirectory)
		{
			totals.Bytes += new FileInfo(path).Length;
			return;
		}

		foreach (string childPath in Directory.EnumerateFileSystemEntries(path))
		{
			CountTree(childPath, totals, cancellationToken);
		}
	}

	private static string? ResolveDestination(string desiredPath, FileConflictResolution resolution)
	{
		if (!EntryExists(desiredPath))
		{
			return desiredPath;
		}

		return resolution switch
		{
			FileConflictResolution.KeepBoth => GetUniquePath(desiredPath),
			FileConflictResolution.Replace => desiredPath,
			FileConflictResolution.Skip => null,
			_ => throw new ArgumentOutOfRangeException(nameof(resolution)),
		};
	}

	private static string GetUniquePath(string desiredPath)
	{
		string? directory = Path.GetDirectoryName(desiredPath);
		if (string.IsNullOrEmpty(directory))
		{
			throw new IOException("The destination has no parent directory.");
		}

		string name = Path.GetFileNameWithoutExtension(desiredPath);
		string extension = Path.GetExtension(desiredPath);
		for (int suffix = 2; ; suffix++)
		{
			string candidate = Path.Combine(directory, $"{name} ({suffix}){extension}");
			if (!EntryExists(candidate))
			{
				return candidate;
			}
		}
	}

	private static string GetStagingPath(string destinationDirectory)
	{
		string path;
		do
		{
			path = Path.Combine(destinationDirectory, $".Files-transfer-{Guid.NewGuid():N}");
		}
		while (EntryExists(path));

		return path;
	}

	private static string? CommitStagedEntry(
		string stagingPath,
		string finalPath,
		FileConflictResolution resolution,
		bool preserveReplacedItem)
	{
		string? backupPath = null;
		if (EntryExists(finalPath))
		{
			if (resolution is not FileConflictResolution.Replace)
			{
				throw new IOException($"The destination already exists: {finalPath}");
			}

			string? parentPath = Path.GetDirectoryName(finalPath);
			if (string.IsNullOrEmpty(parentPath))
			{
				throw new IOException("The destination has no parent directory.");
			}

			backupPath = preserveReplacedItem ? GetReplacementBackupPath(parentPath) : GetStagingPath(parentPath);
			MoveEntry(finalPath, backupPath);
		}

		try
		{
			MoveEntry(stagingPath, finalPath);
		}
		catch
		{
			if (backupPath is not null && EntryExists(backupPath) && !EntryExists(finalPath))
			{
				MoveEntry(backupPath, finalPath);
			}
			throw;
		}

		if (backupPath is not null && !preserveReplacedItem)
		{
			DeleteEntry(backupPath);
			backupPath = null;
		}

		return backupPath;
	}

	private static string GetReplacementBackupPath(string destinationDirectory)
	{
		string path;
		do
		{
			path = Path.Combine(destinationDirectory, $".Files-replaced-{Guid.NewGuid():N}");
		}
		while (EntryExists(path));
		return path;
	}

	private static void MoveEntry(string sourcePath, string destinationPath)
	{
		if (File.GetAttributes(sourcePath).HasFlag(System.IO.FileAttributes.Directory))
		{
			Directory.Move(sourcePath, destinationPath);
		}
		else
		{
			File.Move(sourcePath, destinationPath);
		}
	}

	private static void EnsureSourceExists(string path)
	{
		try
		{
			_ = File.GetAttributes(path);
		}
		catch (FileNotFoundException)
		{
			throw new FileOperationException(FileOperationError.ItemNotFound, Path.GetFileName(path));
		}
		catch (DirectoryNotFoundException)
		{
			throw new FileOperationException(FileOperationError.ItemNotFound, Path.GetFileName(path));
		}
	}

	private static void EnsureDestinationIsOutsideSource(string sourcePath, string destinationDirectory)
	{
		if (!File.GetAttributes(sourcePath).HasFlag(System.IO.FileAttributes.Directory))
		{
			return;
		}

		string sourceWithSeparator = Path.TrimEndingDirectorySeparator(sourcePath) + Path.DirectorySeparatorChar;
		string destinationWithSeparator = Path.TrimEndingDirectorySeparator(destinationDirectory) + Path.DirectorySeparatorChar;
		if (destinationWithSeparator.StartsWith(sourceWithSeparator, StringComparison.OrdinalIgnoreCase))
		{
			throw new IOException("A folder can't be copied or moved into itself.");
		}
	}

	private static string? GetLinkTarget(string path, bool isDirectory)
	{
		return isDirectory ? new DirectoryInfo(path).LinkTarget : new FileInfo(path).LinkTarget;
	}

	private static void CopyUnixMode(string sourcePath, string destinationPath)
	{
		try
		{
			UnixFileMode mode = File.GetUnixFileMode(sourcePath);
			File.SetUnixFileMode(destinationPath, mode);
		}
		catch (PlatformNotSupportedException)
		{
		}
	}

	private static bool PathsEqual(string left, string right)
	{
		return string.Equals(
			Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
			Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
			StringComparison.OrdinalIgnoreCase);
	}

	private static bool EntryExists(string path)
	{
		try
		{
			_ = File.GetAttributes(path);
			return true;
		}
		catch (FileNotFoundException)
		{
			return false;
		}
		catch (DirectoryNotFoundException)
		{
			return false;
		}
	}

	private static void DeleteEntry(string path)
	{
		if (File.GetAttributes(path).HasFlag(System.IO.FileAttributes.Directory) && GetLinkTarget(path, isDirectory: true) is null)
		{
			Directory.Delete(path, recursive: true);
		}
		else
		{
			File.Delete(path);
		}
	}

	private static void TryDeleteEntry(string path)
	{
		try
		{
			if (EntryExists(path))
			{
				DeleteEntry(path);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	private sealed class TransferTotals
	{
		public int Items { get; set; }

		public long Bytes { get; set; }
	}

	private sealed class TransferState(int totalItems, long totalBytes, IProgress<FileTransferProgress>? progress)
	{
		private int completedItems;
		private long completedBytes;

		public void ReportBytes(string path, int bytes)
		{
			completedBytes += bytes;
			Report(path);
		}

		public void CompleteItem(string path, long copiedBytes)
		{
			completedItems++;
			Report(path);
		}

		private void Report(string path)
		{
			progress?.Report(new FileTransferProgress(
				completedItems,
				totalItems,
				completedBytes,
				totalBytes,
				Path.GetFileName(path)));
		}
	}
}
