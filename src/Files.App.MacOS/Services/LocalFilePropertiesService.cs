namespace Files.App.MacOS.Services;

public sealed class LocalFilePropertiesService : IFilePropertiesService
{
	public Task<FilePropertiesSummary> GetSummaryAsync(
		IReadOnlyList<string> paths,
		CancellationToken cancellationToken = default)
	{
		return Task.Run(() => GetSummary(paths, cancellationToken), cancellationToken);
	}

	private static FilePropertiesSummary GetSummary(IReadOnlyList<string> paths, CancellationToken cancellationToken)
	{
		if (paths.Count is 0)
		{
			throw new ArgumentException("At least one path is required.", nameof(paths));
		}

		var totals = new PropertyTotals();
		string[] fullPaths = paths.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
		foreach (string path in fullPaths)
		{
			CountEntry(path, totals, cancellationToken, isRoot: true);
		}

		if (fullPaths is not [string singlePath])
		{
			return new(fullPaths.Length, totals.FileCount, totals.FolderCount, totals.TotalSize, null, null, null, null, null, null);
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
			linkTarget);
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
				}
			}
		}
		catch (Exception ex) when (!isRoot && ex is (IOException or UnauthorizedAccessException))
		{
		}
	}

	private sealed class PropertyTotals
	{
		public long FileCount { get; set; }

		public long FolderCount { get; set; }

		public long TotalSize { get; set; }
	}
}
