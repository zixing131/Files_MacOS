namespace Files.App.MacOS.Services;

public sealed record FilePropertiesSummary(
	int RootItemCount,
	long FileCount,
	long FolderCount,
	long TotalSize,
	string? Name,
	string? Path,
	DateTimeOffset? Created,
	DateTimeOffset? Modified,
	UnixFileMode? UnixMode,
	string? LinkTarget,
	IReadOnlyList<string>? FinderTags,
	string? Owner,
	string? Group,
	uint? UserId,
	uint? GroupId,
	string? AccessControlList,
	bool? IsHidden,
	bool? IsLocked);

public sealed record FilePropertyUpdate(
	UnixFileMode UnixMode,
	IReadOnlyList<string> FinderTags,
	bool IsHidden,
	bool IsLocked);

public interface IFilePropertiesService
{
	Task<FilePropertiesSummary> GetSummaryAsync(
		IReadOnlyList<string> paths,
		CancellationToken cancellationToken = default);

	Task UpdateAsync(
		string path,
		FilePropertyUpdate update,
		CancellationToken cancellationToken = default);
}
