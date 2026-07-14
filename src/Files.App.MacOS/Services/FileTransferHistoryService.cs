using System.Text.Json;

namespace Files.App.MacOS.Services;

internal sealed class FileTransferHistoryService(
	IFileTransferService fileTransferService,
	FileRenameService fileRenameService,
	string? journalPath = null)
{
	private readonly SemaphoreSlim journalLock = new(1, 1);
	private readonly string journalPath = journalPath ?? Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		"Library",
		"Application Support",
		"io.filescommunity.files.macos",
		"undo-artifacts.json");

	public async Task ReplayAsync(FileTransferHistoryEntry history, bool isUndo)
	{
		if (history.Mode is FileTransferMode.Copy)
		{
			await ReplayCopyAsync(history, isUndo);
			return;
		}

		await ReplayMoveAsync(history, isUndo);
	}

	public Task CleanupStagingAsync(IEnumerable<FileTransferHistoryEntry> entries)
	{
		string[] paths = GetArtifactPaths(entries);
		return Task.Run(() =>
		{
			foreach (string path in paths)
			{
				try
				{
					DeleteEntry(path);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
				}
			}
		});
	}

	public async Task CleanupOrphanedStagingAsync()
	{
		await journalLock.WaitAsync();
		try
		{
			if (!File.Exists(journalPath))
			{
				return;
			}

			string[] paths;
			try
			{
				await using FileStream stream = File.OpenRead(journalPath);
				paths = await JsonSerializer.DeserializeAsync<string[]>(stream) ?? [];
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
			{
				paths = [];
			}

			await Task.Run(() => DeleteArtifacts(paths));
			File.Delete(journalPath);
		}
		finally
		{
			journalLock.Release();
		}
	}

	public async Task PersistStagingJournalAsync(IEnumerable<FileTransferHistoryEntry> entries)
	{
		string[] paths = GetArtifactPaths(entries);
		await journalLock.WaitAsync();
		try
		{
			if (paths.Length is 0)
			{
				if (File.Exists(journalPath))
				{
					File.Delete(journalPath);
				}
				return;
			}

			string directory = Path.GetDirectoryName(journalPath) ?? throw new IOException("The undo journal has no parent directory.");
			Directory.CreateDirectory(directory);
			string temporaryPath = Path.Combine(directory, $"undo-artifacts-{Guid.NewGuid():N}.tmp");
			try
			{
				await using FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
				await JsonSerializer.SerializeAsync(stream, paths);
				await stream.FlushAsync();
				File.Move(temporaryPath, journalPath, overwrite: true);
			}
			finally
			{
				if (File.Exists(temporaryPath))
				{
					File.Delete(temporaryPath);
				}
			}
		}
		finally
		{
			journalLock.Release();
		}
	}

	private async Task ReplayCopyAsync(FileTransferHistoryEntry history, bool isUndo)
	{
		if (isUndo)
		{
			if (history.Roots
				.Select(static root => root.ReplacedItemBackupPath)
				.OfType<string>()
				.Any(static path => !File.Exists(path) && !Directory.Exists(path)))
			{
				throw new FileOperationException(FileOperationError.UndoDataUnavailable);
			}

			FilePathRename[] staging = history.Roots
				.Select(static root => new FilePathRename(root.DestinationPath, GetUndoStagingPath(root.DestinationPath)))
				.ToArray();
			FilePathRename[] restoreReplacedItems = history.Roots
				.Where(static root => root.ReplacedItemBackupPath is not null)
				.Select(static root => new FilePathRename(root.ReplacedItemBackupPath!, root.DestinationPath))
				.ToArray();
			await fileRenameService.RenamePathsAsync(staging.Concat(restoreReplacedItems).ToArray());
			history.CopyUndoStaging = staging;
			return;
		}

		FilePathRename[] savedItems = history.CopyUndoStaging ?? throw new FileOperationException(FileOperationError.UndoDataUnavailable);
		if (savedItems.Any(static item => !File.Exists(item.DestinationPath) && !Directory.Exists(item.DestinationPath)))
		{
			throw new FileOperationException(FileOperationError.UndoDataUnavailable);
		}
		FilePathRename[] saveReplacedItems = history.Roots
			.Where(static root => root.ReplacedItemBackupPath is not null)
			.Select(static root => new FilePathRename(root.DestinationPath, root.ReplacedItemBackupPath!))
			.ToArray();
		FilePathRename[] restoreCopiedItems = savedItems
			.Select(static rename => new FilePathRename(rename.DestinationPath, rename.SourcePath))
			.ToArray();
		await fileRenameService.RenamePathsAsync(saveReplacedItems.Concat(restoreCopiedItems).ToArray());
		history.CopyUndoStaging = null;
	}

	public Task MoveMappedItemsAsync(FileTransferRootResult[] mappings)
	{
		return TransferMappedItemsAsync(mappings);
	}

	private async Task ReplayMoveAsync(FileTransferHistoryEntry history, bool isUndo)
	{
		FilePathRename[] replacedItems = history.Roots
			.Where(static root => root.ReplacedItemBackupPath is not null)
			.Select(root => isUndo
				? new FilePathRename(root.ReplacedItemBackupPath!, root.DestinationPath)
				: new FilePathRename(root.DestinationPath, root.ReplacedItemBackupPath!))
			.ToArray();
		FileTransferRootResult[] movedItems = isUndo
			? history.Roots.Select(static root => new FileTransferRootResult(root.DestinationPath, root.SourcePath)).ToArray()
			: history.Roots;

		if (isUndo)
		{
			await TransferMappedItemsAsync(movedItems);
			try
			{
				await fileRenameService.RenamePathsAsync(replacedItems);
			}
			catch
			{
				await RollbackMappedItemsAsync(movedItems);
				throw;
			}
			return;
		}

		await fileRenameService.RenamePathsAsync(replacedItems);
		try
		{
			await TransferMappedItemsAsync(movedItems);
		}
		catch
		{
			await fileRenameService.RenamePathsAsync(replacedItems
				.Select(static rename => new FilePathRename(rename.DestinationPath, rename.SourcePath))
				.ToArray());
			throw;
		}
	}

	private async Task TransferMappedItemsAsync(FileTransferRootResult[] mappings)
	{
		var completedMappings = new List<FileTransferRootResult>(mappings.Length);
		try
		{
			await TransferMappedItemsCoreAsync(mappings, completedMappings);
		}
		catch
		{
			if (completedMappings.Count > 0)
			{
				await RollbackMappedItemsAsync(completedMappings.ToArray());
			}
			throw;
		}
	}

	private async Task TransferMappedItemsCoreAsync(
		FileTransferRootResult[] mappings,
		List<FileTransferRootResult> completedMappings)
	{
		foreach (FileTransferRootResult mapping in mappings)
		{
			if (File.Exists(mapping.DestinationPath) || Directory.Exists(mapping.DestinationPath))
			{
				throw new FileOperationException(FileOperationError.AlreadyExists, Path.GetFileName(mapping.DestinationPath));
			}
		}

		foreach (IGrouping<string, FileTransferRootResult> group in mappings.GroupBy(
			static mapping => Path.GetDirectoryName(mapping.DestinationPath) ?? string.Empty,
			StringComparer.Ordinal))
		{
			if (string.IsNullOrEmpty(group.Key))
			{
				throw new FileOperationException(FileOperationError.MissingParent);
			}

			FileTransferRootResult[] items = group.ToArray();
			var destinationNames = items.ToDictionary(
				static item => Path.GetFullPath(item.SourcePath),
				static item => Path.GetFileName(item.DestinationPath),
				StringComparer.Ordinal);
			FileTransferResult result;
			try
			{
				result = await fileTransferService.TransferAsync(new(
					items.Select(static item => item.SourcePath).ToArray(),
					group.Key,
					FileTransferMode.Move,
					FileConflictResolution.Skip,
					destinationNames));
			}
			catch (FileTransferCanceledException ex)
			{
				completedMappings.AddRange(ex.CompletedRoots);
				throw new FileOperationException(FileOperationError.HistoryTransferIncomplete);
			}
			catch (FileTransferPartialException ex)
			{
				completedMappings.AddRange(ex.CompletedRoots);
				throw new FileOperationException(FileOperationError.HistoryTransferIncomplete);
			}
			completedMappings.AddRange(result.CompletedRoots);
			if (result.CompletedRootItems != items.Length)
			{
				throw new FileOperationException(FileOperationError.HistoryTransferIncomplete);
			}
		}
	}

	private async Task RollbackMappedItemsAsync(FileTransferRootResult[] completedMappings)
	{
		FileTransferRootResult[] rollbackMappings = completedMappings
			.Select(static mapping => new FileTransferRootResult(mapping.DestinationPath, mapping.SourcePath))
			.ToArray();
		try
		{
			await TransferMappedItemsCoreAsync(rollbackMappings, []);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			throw new FileOperationException(FileOperationError.HistoryRollbackFailed);
		}
	}

	private static string GetUndoStagingPath(string path)
	{
		string parent = Path.GetDirectoryName(path) ?? throw new FileOperationException(FileOperationError.MissingParent);
		string candidate;
		do
		{
			candidate = Path.Combine(parent, $".Files-undo-{Guid.NewGuid():N}");
		}
		while (File.Exists(candidate) || Directory.Exists(candidate));
		return candidate;
	}

	private static void DeleteEntry(string path)
	{
		if (!File.Exists(path) && !Directory.Exists(path))
		{
			return;
		}

		System.IO.FileAttributes attributes = File.GetAttributes(path);
		if (attributes.HasFlag(System.IO.FileAttributes.Directory) && new DirectoryInfo(path).LinkTarget is null)
		{
			Directory.Delete(path, recursive: true);
		}
		else
		{
			File.Delete(path);
		}
	}

	private static string[] GetArtifactPaths(IEnumerable<FileTransferHistoryEntry> entries)
	{
		return entries
			.SelectMany(static entry =>
				(entry.CopyUndoStaging?.Select(static rename => rename.DestinationPath) ?? [])
				.Concat(entry.Roots
					.Select(static root => root.ReplacedItemBackupPath)
					.OfType<string>()))
			.Where(InternalFileArtifact.IsPath)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
	}

	private static void DeleteArtifacts(IEnumerable<string> paths)
	{
		foreach (string path in paths.Where(InternalFileArtifact.IsPath))
		{
			try
			{
				DeleteEntry(path);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}
	}
}
