using System.IO.Compression;
using System.Text;

namespace Files.App.MacOS.Services;

public sealed class ZipArchiveService : IArchiveService
{
	private const int BufferSize = 1024 * 1024;
	private const int UnixFileTypeMask = 0xF000;
	private const int UnixRegularFile = 0x8000;
	private const int UnixDirectory = 0x4000;
	private const int UnixSymbolicLink = 0xA000;

	public Task<string> CreateZipAsync(
		IReadOnlyList<string> sourcePaths,
		string destinationDirectory,
		string archiveName,
		IProgress<ArchiveProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		return Task.Run(() => CreateZipCoreAsync(sourcePaths, destinationDirectory, archiveName, progress, cancellationToken), cancellationToken);
	}

	public Task<string> ExtractZipAsync(
		string archivePath,
		string destinationDirectory,
		string folderName,
		IProgress<ArchiveProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		return Task.Run(() => ExtractZipCoreAsync(archivePath, destinationDirectory, folderName, progress, cancellationToken), cancellationToken);
	}

	private static async Task<string> CreateZipCoreAsync(
		IReadOnlyList<string> sourcePaths,
		string destinationDirectory,
		string archiveName,
		IProgress<ArchiveProgress>? progress,
		CancellationToken cancellationToken)
	{
		ValidateName(archiveName);
		if (!archiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
		{
			archiveName += ".zip";
		}

		string destination = Path.GetFullPath(destinationDirectory);
		if (!Directory.Exists(destination))
		{
			throw new DirectoryNotFoundException(destination);
		}

		string[] sources = sourcePaths
			.Select(Path.GetFullPath)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		if (sources.Length is 0)
		{
			throw new ArgumentException("At least one source path is required.", nameof(sourcePaths));
		}

		var totals = new ArchiveTotals();
		foreach (string source in sources)
		{
			CountSource(source, totals, cancellationToken);
		}

		string finalPath = GetUniqueFilePath(destination, archiveName);
		string stagingPath = Path.Combine(destination, $".Files-archive-{Guid.NewGuid():N}.tmp");
		var state = new ArchiveState(totals.Items, totals.Bytes, progress);
		try
		{
			await using (var output = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
				{
					foreach (string source in sources)
					{
						await AddEntryAsync(archive, source, Path.GetFileName(Path.TrimEndingDirectorySeparator(source)), state, cancellationToken);
					}
				}
				await output.FlushAsync(cancellationToken);
			}
			File.Move(stagingPath, finalPath);
			return finalPath;
		}
		catch
		{
			TryDeleteFile(stagingPath);
			throw;
		}
	}

	private static async Task AddEntryAsync(
		ZipArchive archive,
		string sourcePath,
		string entryName,
		ArchiveState state,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		System.IO.FileAttributes attributes = File.GetAttributes(sourcePath);
		bool isDirectory = attributes.HasFlag(System.IO.FileAttributes.Directory);
		string? linkTarget = isDirectory ? new DirectoryInfo(sourcePath).LinkTarget : new FileInfo(sourcePath).LinkTarget;
		UnixFileMode mode = GetUnixMode(sourcePath);

		if (linkTarget is not null)
		{
			ZipArchiveEntry linkEntry = archive.CreateEntry(ToZipPath(entryName), CompressionLevel.NoCompression);
			linkEntry.ExternalAttributes = ToExternalAttributes(UnixSymbolicLink, mode);
			linkEntry.LastWriteTime = GetZipTimestamp(File.GetLastWriteTimeUtc(sourcePath));
			await using Stream linkStream = linkEntry.Open();
			byte[] targetBytes = Encoding.UTF8.GetBytes(linkTarget);
			await linkStream.WriteAsync(targetBytes, cancellationToken);
			state.CompleteItem(sourcePath);
			return;
		}

		if (isDirectory)
		{
			string directoryEntryName = ToZipPath(entryName).TrimEnd('/') + "/";
			ZipArchiveEntry directoryEntry = archive.CreateEntry(directoryEntryName, CompressionLevel.NoCompression);
			directoryEntry.ExternalAttributes = ToExternalAttributes(UnixDirectory, mode);
			directoryEntry.LastWriteTime = GetZipTimestamp(Directory.GetLastWriteTimeUtc(sourcePath));

			foreach (string childPath in Directory.EnumerateFileSystemEntries(sourcePath))
			{
				await AddEntryAsync(
					archive,
					childPath,
					$"{entryName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}/{Path.GetFileName(childPath)}",
					state,
					cancellationToken);
			}
			state.CompleteItem(sourcePath);
			return;
		}

		ZipArchiveEntry fileEntry = archive.CreateEntry(ToZipPath(entryName), CompressionLevel.Optimal);
		fileEntry.ExternalAttributes = ToExternalAttributes(UnixRegularFile, mode);
		fileEntry.LastWriteTime = GetZipTimestamp(File.GetLastWriteTimeUtc(sourcePath));
		await using Stream destination = fileEntry.Open();
		await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
		byte[] buffer = new byte[BufferSize];
		int bytesRead;
		while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
		{
			await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
			state.AddBytes(sourcePath, bytesRead);
		}
		state.CompleteItem(sourcePath);
	}

	private static async Task<string> ExtractZipCoreAsync(
		string archivePath,
		string destinationDirectory,
		string folderName,
		IProgress<ArchiveProgress>? progress,
		CancellationToken cancellationToken)
	{
		ValidateName(folderName);
		string sourceArchive = Path.GetFullPath(archivePath);
		string destination = Path.GetFullPath(destinationDirectory);
		if (!File.Exists(sourceArchive))
		{
			throw new FileNotFoundException("The archive no longer exists.", sourceArchive);
		}
		if (!Directory.Exists(destination))
		{
			throw new DirectoryNotFoundException(destination);
		}

		string finalPath = GetUniqueDirectoryPath(destination, folderName);
		string stagingPath = Path.Combine(destination, $".Files-extract-{Guid.NewGuid():N}");
		Directory.CreateDirectory(stagingPath);

		try
		{
			await using var input = new FileStream(sourceArchive, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.RandomAccess);
			using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true, Encoding.UTF8);
			IReadOnlyList<ExtractionPlan> plans = BuildExtractionPlans(archive, stagingPath);
			var state = new ArchiveState(plans.Count, plans.Where(static plan => !plan.IsDirectory && !plan.IsSymbolicLink).Sum(static plan => plan.Entry.Length), progress);

			foreach (ExtractionPlan plan in plans.Where(static plan => !plan.IsSymbolicLink))
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (plan.IsDirectory)
				{
					Directory.CreateDirectory(plan.DestinationPath);
					state.CompleteItem(plan.Entry.FullName);
					continue;
				}

				Directory.CreateDirectory(Path.GetDirectoryName(plan.DestinationPath)!);
				await using Stream source = plan.Entry.Open();
				await using var output = new FileStream(plan.DestinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
				byte[] buffer = new byte[BufferSize];
				int bytesRead;
				while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
				{
					await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
					state.AddBytes(plan.Entry.FullName, bytesRead);
				}
				await output.FlushAsync(cancellationToken);
				ApplyFileMetadata(plan);
				state.CompleteItem(plan.Entry.FullName);
			}

			foreach (ExtractionPlan plan in plans.Where(static plan => plan.IsSymbolicLink))
			{
				cancellationToken.ThrowIfCancellationRequested();
				await CreateSafeSymbolicLinkAsync(plan, stagingPath, cancellationToken);
				state.CompleteItem(plan.Entry.FullName);
			}

			foreach (ExtractionPlan plan in plans.Where(static plan => plan.IsDirectory).OrderByDescending(static plan => plan.DestinationPath.Length))
			{
				ApplyDirectoryMetadata(plan);
			}

			Directory.Move(stagingPath, finalPath);
			return finalPath;
		}
		catch
		{
			TryDeleteDirectory(stagingPath);
			throw;
		}
	}

	private static IReadOnlyList<ExtractionPlan> BuildExtractionPlans(ZipArchive archive, string stagingPath)
	{
		var plans = new List<ExtractionPlan>(archive.Entries.Count);
		var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string rootWithSeparator = Path.TrimEndingDirectorySeparator(stagingPath) + Path.DirectorySeparatorChar;

		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			string relativePath = NormalizeEntryPath(entry.FullName);
			if (string.IsNullOrEmpty(relativePath))
			{
				continue;
			}
			if (!paths.Add(relativePath))
			{
				throw new InvalidDataException($"The archive contains a duplicate path: {entry.FullName}");
			}

			string destinationPath = Path.GetFullPath(Path.Combine(stagingPath, relativePath));
			if (!destinationPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
			{
				throw new InvalidDataException($"The archive entry escapes the destination: {entry.FullName}");
			}

			int unixAttributes = entry.ExternalAttributes >> 16;
			int fileType = unixAttributes & UnixFileTypeMask;
			bool isSymbolicLink = fileType == UnixSymbolicLink;
			bool isDirectory = !isSymbolicLink && (entry.FullName.EndsWith('/') || fileType == UnixDirectory);
			plans.Add(new(entry, relativePath, destinationPath, isDirectory, isSymbolicLink, (UnixFileMode)(unixAttributes & 0xFFF)));
		}

		string[] symbolicLinkPaths = plans.Where(static plan => plan.IsSymbolicLink).Select(static plan => plan.RelativePath + Path.DirectorySeparatorChar).ToArray();
		foreach (ExtractionPlan plan in plans)
		{
			if (symbolicLinkPaths.Any(linkPath => plan.RelativePath.StartsWith(linkPath, StringComparison.OrdinalIgnoreCase)))
			{
				throw new InvalidDataException($"The archive contains an entry beneath a symbolic link: {plan.Entry.FullName}");
			}
		}

		return plans;
	}

	private static async Task CreateSafeSymbolicLinkAsync(ExtractionPlan plan, string stagingPath, CancellationToken cancellationToken)
	{
		if (plan.Entry.Length is < 1 or > 4096)
		{
			throw new InvalidDataException($"The symbolic link target is invalid: {plan.Entry.FullName}");
		}

		await using Stream source = plan.Entry.Open();
		byte[] bytes = new byte[plan.Entry.Length];
		await source.ReadExactlyAsync(bytes, cancellationToken);
		string target = Encoding.UTF8.GetString(bytes);
		if (target.Contains('\0') || Path.IsPathRooted(target))
		{
			throw new InvalidDataException($"The symbolic link target is unsafe: {plan.Entry.FullName}");
		}

		string parentPath = Path.GetDirectoryName(plan.DestinationPath)!;
		string resolvedTarget = Path.GetFullPath(Path.Combine(parentPath, target));
		string rootWithSeparator = Path.TrimEndingDirectorySeparator(stagingPath) + Path.DirectorySeparatorChar;
		if (!resolvedTarget.StartsWith(rootWithSeparator, StringComparison.Ordinal) && !string.Equals(resolvedTarget, stagingPath, StringComparison.Ordinal))
		{
			throw new InvalidDataException($"The symbolic link target escapes the destination: {plan.Entry.FullName}");
		}

		Directory.CreateDirectory(parentPath);
		File.CreateSymbolicLink(plan.DestinationPath, target);
	}

	private static string NormalizeEntryPath(string entryName)
	{
		if (entryName.Contains('\0'))
		{
			throw new InvalidDataException("The archive contains a null character in an entry name.");
		}

		string normalized = entryName.Replace('\\', '/').Trim('/');
		string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Any(static segment => segment is "." or ".."))
		{
			throw new InvalidDataException($"The archive contains an unsafe path: {entryName}");
		}
		return Path.Combine(segments);
	}

	private static void CountSource(string path, ArchiveTotals totals, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		System.IO.FileAttributes attributes = File.GetAttributes(path);
		bool isDirectory = attributes.HasFlag(System.IO.FileAttributes.Directory);
		string? linkTarget = isDirectory ? new DirectoryInfo(path).LinkTarget : new FileInfo(path).LinkTarget;
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

		foreach (string child in Directory.EnumerateFileSystemEntries(path))
		{
			CountSource(child, totals, cancellationToken);
		}
	}

	private static void ApplyFileMetadata(ExtractionPlan plan)
	{
		File.SetLastWriteTimeUtc(plan.DestinationPath, plan.Entry.LastWriteTime.UtcDateTime);
		SetUnixMode(plan.DestinationPath, plan.UnixMode);
	}

	private static void ApplyDirectoryMetadata(ExtractionPlan plan)
	{
		Directory.SetLastWriteTimeUtc(plan.DestinationPath, plan.Entry.LastWriteTime.UtcDateTime);
		SetUnixMode(plan.DestinationPath, plan.UnixMode);
	}

	private static UnixFileMode GetUnixMode(string path)
	{
		try
		{
			return File.GetUnixFileMode(path);
		}
		catch (PlatformNotSupportedException)
		{
			return UnixFileMode.UserRead | UnixFileMode.UserWrite;
		}
	}

	private static void SetUnixMode(string path, UnixFileMode mode)
	{
		if (mode is 0)
		{
			return;
		}
		try
		{
			File.SetUnixFileMode(path, mode);
		}
		catch (PlatformNotSupportedException)
		{
		}
	}

	private static int ToExternalAttributes(int fileType, UnixFileMode mode)
	{
		return (fileType | ((int)mode & 0xFFF)) << 16;
	}

	private static DateTimeOffset GetZipTimestamp(DateTime value)
	{
		DateTime utc = value.ToUniversalTime();
		if (utc.Year < 1980)
		{
			utc = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		}
		else if (utc.Year > 2107)
		{
			utc = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Utc);
		}
		return utc;
	}

	private static string ToZipPath(string path)
	{
		return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
	}

	private static string GetUniqueFilePath(string parentPath, string name)
	{
		string extension = Path.GetExtension(name);
		string stem = Path.GetFileNameWithoutExtension(name);
		string candidate = Path.Combine(parentPath, name);
		for (int suffix = 2; File.Exists(candidate) || Directory.Exists(candidate); suffix++)
		{
			candidate = Path.Combine(parentPath, $"{stem} ({suffix}){extension}");
		}
		return candidate;
	}

	private static string GetUniqueDirectoryPath(string parentPath, string name)
	{
		string candidate = Path.Combine(parentPath, name);
		for (int suffix = 2; File.Exists(candidate) || Directory.Exists(candidate); suffix++)
		{
			candidate = Path.Combine(parentPath, $"{name} ({suffix})");
		}
		return candidate;
	}

	private static void ValidateName(string name)
	{
		if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
		{
			throw new FileOperationException(FileOperationError.InvalidName);
		}
	}

	private static void TryDeleteFile(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	private sealed record ExtractionPlan(
		ZipArchiveEntry Entry,
		string RelativePath,
		string DestinationPath,
		bool IsDirectory,
		bool IsSymbolicLink,
		UnixFileMode UnixMode);

	private sealed class ArchiveTotals
	{
		public int Items { get; set; }

		public long Bytes { get; set; }
	}

	private sealed class ArchiveState(int totalItems, long totalBytes, IProgress<ArchiveProgress>? progress)
	{
		private int completedItems;
		private long completedBytes;

		public void AddBytes(string path, int bytes)
		{
			completedBytes += bytes;
			Report(path);
		}

		public void CompleteItem(string path)
		{
			completedItems++;
			Report(path);
		}

		private void Report(string path)
		{
			progress?.Report(new(completedItems, totalItems, completedBytes, totalBytes, Path.GetFileName(path)));
		}
	}
}
