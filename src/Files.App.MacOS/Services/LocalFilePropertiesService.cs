namespace Files.App.MacOS.Services;

public sealed class LocalFilePropertiesService : IFilePropertiesService
{
	public Task<FilePropertiesSummary> GetSummaryAsync(
		IReadOnlyList<string> paths,
		CancellationToken cancellationToken = default)
	{
		return Task.Run(() => GetSummary(paths, cancellationToken), cancellationToken);
	}

	public Task<FilePropertiesSize> CalculateSizeAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
	{
		string[] fullPaths = paths.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
		return Task.Run(() =>
		{
			var totals = new PropertyTotals();
			foreach (string path in fullPaths) CountEntry(path, totals, cancellationToken, isRoot: true);
			return new FilePropertiesSize(totals.TotalSize, totals.FileCount, totals.FolderCount, totals.SkippedEntries);
		}, cancellationToken);
	}

	public Task UpdateAsync(
		string path,
		FilePropertyUpdate update,
		CancellationToken cancellationToken = default)
	{
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			string fullPath = Path.GetFullPath(path);
			UnixFileMode previousMode = File.GetUnixFileMode(fullPath);
			IReadOnlyList<string> previousTags = MacOSFinderTagService.GetTags(fullPath);
			MacOSFileSecurityInfo previousSecurity = MacOSFileSecurityService.GetInfo(fullPath) ??
				throw new IOException("The current macOS file security information isn't available.");
			try
			{
				if (previousSecurity.IsLocked)
				{
					MacOSFileSecurityService.SetFlags(fullPath, previousSecurity.IsHidden, isLocked: false);
				}
				MacOSFileSecurityService.SetSecurity(fullPath, update.Owner, update.Group, update.AccessControlList);
				File.SetUnixFileMode(fullPath, update.UnixMode);
				MacOSFinderTagService.SetTags(fullPath, update.FinderTags);
				MacOSFileSecurityService.SetFlags(fullPath, update.IsHidden, update.IsLocked);
			}
			catch (Exception originalException)
			{
				var rollbackErrors = new List<Exception>();
				TryRollback(() => MacOSFileSecurityService.SetFlags(fullPath, previousSecurity.IsHidden, isLocked: false));
				TryRollback(() => MacOSFileSecurityService.SetSecurity(fullPath, previousSecurity.Owner, previousSecurity.Group, previousSecurity.Acl));
				TryRollback(() => File.SetUnixFileMode(fullPath, previousMode));
				TryRollback(() => MacOSFinderTagService.SetTags(fullPath, previousTags));
				TryRollback(() => MacOSFileSecurityService.SetFlags(fullPath, previousSecurity.IsHidden, previousSecurity.IsLocked));
				if (rollbackErrors.Count > 0)
				{
					throw new IOException(
						"The properties couldn't be saved and one or more original values couldn't be restored.",
						new AggregateException([originalException, .. rollbackErrors]));
				}
				throw;

				void TryRollback(Action action)
				{
					try
					{
						action();
					}
					catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
					{
						rollbackErrors.Add(rollbackException);
					}
				}
			}
		}, cancellationToken);
	}

	private static FilePropertiesSummary GetSummary(IReadOnlyList<string> paths, CancellationToken cancellationToken)
	{
		if (paths.Count is 0)
		{
			throw new ArgumentException("At least one path is required.", nameof(paths));
		}

		var totals = new PropertyTotals();
		string[] fullPaths = paths.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
		bool isSizeCalculated = true;
		// 打开属性只读取选中项目本身，目录遍历必须由用户手动触发。
		foreach (string path in fullPaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			bool directory = File.GetAttributes(path).HasFlag(System.IO.FileAttributes.Directory);
			FileSystemInfo entry = directory ? new DirectoryInfo(path) : new FileInfo(path);
			if (directory && entry.LinkTarget is null)
			{
				totals.FolderCount++;
				isSizeCalculated = false;
			}
			else
			{
				totals.FileCount++;
				if (!directory && entry.LinkTarget is null) totals.TotalSize += ((FileInfo)entry).Length;
			}
		}

		if (fullPaths is not [string singlePath])
		{
			return new(fullPaths.Length, totals.FileCount, totals.FolderCount, totals.TotalSize, null, null, null, null, null, null, null, null, null, null, null, null, null, null, IsSizeCalculated: isSizeCalculated);
		}

		System.IO.FileAttributes attributes = File.GetAttributes(singlePath);
		bool isDirectory = attributes.HasFlag(System.IO.FileAttributes.Directory);
		FileSystemInfo info = isDirectory ? new DirectoryInfo(singlePath) : new FileInfo(singlePath);
		string? linkTarget = isDirectory ? new DirectoryInfo(singlePath).LinkTarget : new FileInfo(singlePath).LinkTarget;
		UnixFileMode? unixMode = null;
		try
		{
			unixMode = File.GetUnixFileMode(singlePath);
		}
		catch (PlatformNotSupportedException)
		{
		}
		MacOSFileSecurityInfo? security = MacOSFileSecurityService.GetInfo(singlePath);

		return new(
			1,
			totals.FileCount,
			totals.FolderCount,
			totals.TotalSize,
			info.Name,
			info.FullName,
			info.CreationTimeUtc,
			info.LastWriteTimeUtc,
			unixMode,
			linkTarget,
			MacOSFinderTagService.GetTags(singlePath),
			security?.Owner,
			security?.Group,
			security?.UserId,
			security?.GroupId,
			security?.Acl,
			security?.IsHidden,
			security?.IsLocked,
			MacOSFileDetailsService.Read(singlePath),
			isSizeCalculated);
	}

	private static void CountEntry(string path, PropertyTotals totals, CancellationToken cancellationToken, bool isRoot)
	{
		cancellationToken.ThrowIfCancellationRequested();
		System.IO.FileAttributes attributes = File.GetAttributes(path);
		bool isDirectory = attributes.HasFlag(System.IO.FileAttributes.Directory);
		string? linkTarget = isDirectory ? new DirectoryInfo(path).LinkTarget : new FileInfo(path).LinkTarget;

		if (!isDirectory || linkTarget is not null)
		{
			totals.FileCount++;
			if (!isDirectory && linkTarget is null)
			{
				totals.TotalSize += new FileInfo(path).Length;
			}
			return;
		}

		totals.FolderCount++;
		try
		{
			foreach (string childPath in Directory.EnumerateFileSystemEntries(path))
			{
				try
				{
					CountEntry(childPath, totals, cancellationToken, isRoot: false);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					totals.SkippedEntries++;
				}
			}
		}
		catch (Exception ex) when (!isRoot && ex is (IOException or UnauthorizedAccessException))
		{
			totals.SkippedEntries++;
		}
	}

	private sealed class PropertyTotals
	{
		public long FileCount { get; set; }

		public long FolderCount { get; set; }

		public long TotalSize { get; set; }

		public long SkippedEntries { get; set; }
	}
}
