using Files.App.MacOS.Models;

namespace Files.App.MacOS.Services;

public sealed class LocalFileSearchService : IFileSearchService
{
	private const long MaximumContentSearchFileSize = 64L * 1024 * 1024;
	private static readonly HashSet<string> ContentSearchExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".adoc", ".bash", ".c", ".cc", ".cfg", ".conf", ".cpp", ".cs", ".csproj", ".css",
		".csv", ".entitlements", ".fish", ".fs", ".go", ".h", ".hpp", ".htm", ".html", ".ini",
		".java", ".js", ".json", ".jsonl", ".jsx", ".kt", ".kts", ".less", ".log", ".m", ".markdown",
		".md", ".mjs", ".mm", ".php", ".plist", ".properties", ".props", ".ps1", ".psd1", ".psm1",
		".py", ".rb", ".resw", ".rs", ".rst", ".rtf", ".scss", ".sh", ".sln", ".slnx", ".sql",
		".swift", ".targets", ".tex", ".toml", ".ts", ".tsv", ".tsx", ".txt", ".vb", ".xaml",
		".xml", ".yaml", ".yml", ".zsh",
	};

	public Task<IReadOnlyList<LocalFileSystemItem>> SearchAsync(
		string rootPath,
		FileSearchQuery query,
		bool includeHidden,
		IProgress<IReadOnlyList<LocalFileSystemItem>>? progress = null,
		CancellationToken cancellationToken = default)
	{
		return Task.Run<IReadOnlyList<LocalFileSystemItem>>(() =>
		{
			var results = new List<LocalFileSystemItem>();
			var pendingResults = new List<LocalFileSystemItem>(32);
			SearchDirectory(Path.GetFullPath(rootPath), query, includeHidden, results, pendingResults, progress, cancellationToken, isRoot: true);
			ReportPendingResults(pendingResults, progress);
			return results;
		}, cancellationToken);
	}

	private static void SearchDirectory(
		string directoryPath,
		FileSearchQuery query,
		bool includeHidden,
		List<LocalFileSystemItem> results,
		List<LocalFileSystemItem> pendingResults,
		IProgress<IReadOnlyList<LocalFileSystemItem>>? progress,
		CancellationToken cancellationToken,
		bool isRoot)
	{
		try
		{
			foreach (string itemPath in Directory.EnumerateFileSystemEntries(directoryPath))
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (InternalFileArtifact.IsPath(itemPath))
				{
					continue;
				}
				try
				{
					System.IO.FileAttributes attributes = File.GetAttributes(itemPath);
					bool isDirectory = attributes.HasFlag(System.IO.FileAttributes.Directory);
					FileSystemInfo info = isDirectory ? new DirectoryInfo(itemPath) : new FileInfo(itemPath);
					bool isPackage = isDirectory && MacOSFilePackage.IsPackage(info);
					bool isHidden = attributes.HasFlag(System.IO.FileAttributes.Hidden) || info.Name.StartsWith('.');
					if (!includeHidden && isHidden)
					{
						continue;
					}
					string? linkTarget = isDirectory ? new DirectoryInfo(itemPath).LinkTarget : new FileInfo(itemPath).LinkTarget;

					IReadOnlyList<string> contentTerms = query.GetContentTerms(info.Name);
					IReadOnlyList<string>? finderTags = query.RequiresFinderTags
						? MacOSFinderTagService.GetTags(itemPath)
						: null;
					bool metadataMatches = query.MatchesMetadata(info, isDirectory && !isPackage, finderTags);
					bool matches = metadataMatches &&
						(contentTerms.Count is 0 ||
						(!isDirectory &&
						info is FileInfo contentFile &&
						ShouldSearchContent(contentFile) &&
						FileContainsAllText(contentFile.FullName, contentTerms, cancellationToken)));
					if (matches)
					{
						var item = new LocalFileSystemItem(
							itemPath,
							info.Name,
							isDirectory,
							isHidden,
							info is FileInfo fileInfo ? fileInfo.Length : null,
							info.LastWriteTimeUtc,
							isPackage);
						results.Add(item);
						pendingResults.Add(item);
						if (pendingResults.Count >= 32)
						{
							ReportPendingResults(pendingResults, progress);
						}
					}

					if (isDirectory && !isPackage && linkTarget is null)
					{
						SearchDirectory(itemPath, query, includeHidden, results, pendingResults, progress, cancellationToken, isRoot: false);
					}
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

	private static void ReportPendingResults(
		List<LocalFileSystemItem> pendingResults,
		IProgress<IReadOnlyList<LocalFileSystemItem>>? progress)
	{
		if (pendingResults.Count is 0)
		{
			return;
		}

		progress?.Report(pendingResults.ToArray());
		pendingResults.Clear();
	}

	private static bool ShouldSearchContent(FileInfo file)
	{
		return file.Length <= MaximumContentSearchFileSize &&
			(string.IsNullOrEmpty(file.Extension) || ContentSearchExtensions.Contains(file.Extension));
	}

	private static bool FileContainsAllText(string path, IReadOnlyList<string> terms, CancellationToken cancellationToken)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
		char[] buffer = new char[16 * 1024];
		var remainingTerms = new HashSet<string>(terms, StringComparer.CurrentCultureIgnoreCase);
		int maximumTermLength = remainingTerms.Max(static term => term.Length);
		string carry = string.Empty;
		int read;
		while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string candidate = carry + new string(buffer, 0, read);
			remainingTerms.RemoveWhere(term => candidate.Contains(term, StringComparison.CurrentCultureIgnoreCase));
			if (remainingTerms.Count is 0)
			{
				return true;
			}

			int carryLength = Math.Min(Math.Max(0, maximumTermLength - 1), candidate.Length);
			carry = carryLength is 0 ? string.Empty : candidate[^carryLength..];
		}

		return false;
	}
}
