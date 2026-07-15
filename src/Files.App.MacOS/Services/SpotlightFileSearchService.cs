using System.Runtime.InteropServices;
using System.Text.Json;
using Files.App.MacOS.Interop;
using Files.App.MacOS.Models;

namespace Files.App.MacOS.Services;

public sealed class SpotlightFileSearchService(IFileSearchService? fallback = null) : IFileSearchService
{
	private const int QueryTimeoutMilliseconds = 15_000;
	private readonly IFileSearchService fallback = fallback ?? new LocalFileSearchService();

	public async Task<IReadOnlyList<LocalFileSystemItem>> SearchAsync(
		string rootPath,
		FileSearchQuery query,
		bool includeHidden,
		IProgress<IReadOnlyList<LocalFileSystemItem>>? progress = null,
		CancellationToken cancellationToken = default)
	{
		string fullRootPath = Path.GetFullPath(rootPath);
		Task<IReadOnlyList<LocalFileSystemItem>> recursiveSearch = fallback.SearchAsync(
			fullRootPath,
			query,
			includeHidden,
			progress,
			cancellationToken);
		try
		{
			IReadOnlyList<string>? paths = await GetIndexedPathsAsync(fullRootPath, query, includeHidden, cancellationToken);
			if (paths is not null)
			{
				IReadOnlyList<LocalFileSystemItem> indexedItems = await Task.Run<IReadOnlyList<LocalFileSystemItem>>(
					() => CreateItems(paths, fullRootPath, query, includeHidden, cancellationToken),
					cancellationToken);
				if (indexedItems.Count > 0)
				{
					progress?.Report(indexedItems);
				}
				IReadOnlyList<LocalFileSystemItem> recursiveItems = await recursiveSearch;
				return indexedItems
					.Concat(recursiveItems)
					.DistinctBy(static item => item.Path, StringComparer.Ordinal)
					.ToArray();
			}
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or JsonException)
		{
		}

		cancellationToken.ThrowIfCancellationRequested();
		return await recursiveSearch;
	}

	private static async Task<IReadOnlyList<string>?> GetIndexedPathsAsync(
		string rootPath,
		FileSearchQuery query,
		bool includeHidden,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		nint context = MacOSNativeMethods.CreateSpotlightSearch();
		if (context is 0)
		{
			return null;
		}

		CancellationTokenRegistration registration = cancellationToken.Register(
			static state => MacOSNativeMethods.CancelSpotlightSearch((nint)state!),
			context);
		try
		{
			nint resultPointer = await Task.Run(
				() => MacOSNativeMethods.SearchSpotlight(context, rootPath, query.ToSpotlightJson(), includeHidden ? 1 : 0, QueryTimeoutMilliseconds),
				CancellationToken.None);
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (resultPointer is 0)
				{
					return null;
				}

				string json = Marshal.PtrToStringUTF8(resultPointer) ?? "[]";
				return JsonSerializer.Deserialize<string[]>(json) ?? [];
			}
			finally
			{
				if (resultPointer is not 0)
				{
					MacOSNativeMethods.Free(resultPointer);
				}
			}
		}
		finally
		{
			registration.Dispose();
			MacOSNativeMethods.Free(context);
		}
	}

	private static IReadOnlyList<LocalFileSystemItem> CreateItems(
		IReadOnlyList<string> paths,
		string rootPath,
		FileSearchQuery query,
		bool includeHidden,
		CancellationToken cancellationToken)
	{
		string rootPrefix = Path.TrimEndingDirectorySeparator(rootPath) + Path.DirectorySeparatorChar;
		var items = new List<LocalFileSystemItem>();
		foreach (string path in paths.Distinct(StringComparer.Ordinal))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (InternalFileArtifact.IsPath(path))
			{
				continue;
			}
			try
			{
				string fullPath = Path.GetFullPath(path);
				if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
				{
					continue;
				}
				if (MacOSFilePackage.IsInsidePackage(fullPath, rootPath))
				{
					continue;
				}

				System.IO.FileAttributes attributes = File.GetAttributes(fullPath);
				bool isDirectory = attributes.HasFlag(System.IO.FileAttributes.Directory);
				FileSystemInfo info = isDirectory ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
				bool isPackage = isDirectory && MacOSFilePackage.IsPackage(info);
				IReadOnlyList<string>? finderTags = query.RequiresFinderTags
					? MacOSFinderTagService.GetTags(fullPath)
					: null;
				if (!query.MatchesMetadata(info, isDirectory && !isPackage, finderTags))
				{
					continue;
				}
				bool isHidden = attributes.HasFlag(System.IO.FileAttributes.Hidden) ||
					fullPath[rootPrefix.Length..].Split(Path.DirectorySeparatorChar).Any(static component => component.StartsWith('.'));
				if (!includeHidden && isHidden)
				{
					continue;
				}

				items.Add(new(
					fullPath,
					info.Name,
					isDirectory,
					isHidden,
					info is FileInfo fileInfo ? fileInfo.Length : null,
					info.LastWriteTimeUtc,
					isPackage));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}

		return items;
	}
}
