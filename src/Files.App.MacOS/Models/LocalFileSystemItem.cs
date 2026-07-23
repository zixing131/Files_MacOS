using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace Files.App.MacOS.Models;

public sealed partial class LocalFileSystemItem(
	string path,
	string name,
	bool isDirectory,
	bool isHidden,
	long? size,
	DateTimeOffset modified,
	bool isPackage = false,
	DateTimeOffset? created = null,
	DateTimeOffset? lastOpened = null,
	DateTimeOffset? added = null,
	IReadOnlyList<string>? tags = null,
	string? version = null,
	string? comments = null,
	string? kind = null) : ObservableObject
{
	public string Path { get; } = path;

	public string Name { get; } = name;

	public string AccessibilityName { get; internal set; } = name;

	public bool IsDirectory { get; } = isDirectory;

	public bool IsPackage { get; } = isPackage;

	public bool IsNavigableDirectory => IsDirectory && !IsPackage;

	public bool IsHidden { get; } = isHidden;

	public long? Size { get; } = size;

	public DateTimeOffset Modified { get; } = modified;

	public DateTimeOffset Created { get; } = created ?? modified;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(LastOpenedText))]
	public partial DateTimeOffset LastOpened { get; set; } = lastOpened ?? modified;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(AddedText))]
	public partial DateTimeOffset Added { get; set; } = added ?? created ?? modified;

	[ObservableProperty]
	public partial string Kind { get; set; } = kind ?? (isPackage
		? "app"
		: isDirectory
			? "folder"
			: System.IO.Path.GetExtension(path).TrimStart('.'));

	[ObservableProperty]
	public partial string TagsText { get; set; } = string.Join(", ", tags ?? []);

	[ObservableProperty]
	public partial string Version { get; set; } = version ?? string.Empty;

	[ObservableProperty]
	public partial string Comments { get; set; } = comments ?? string.Empty;

	internal void ApplySortMetadata(
		DateTimeOffset? enrichedLastOpened,
		DateTimeOffset? enrichedAdded,
		string? enrichedKind,
		string? enrichedVersion,
		string? enrichedComments,
		IReadOnlyList<string>? enrichedTags)
	{
		if (enrichedLastOpened is { } opened)
		{
			LastOpened = opened;
		}
		if (enrichedAdded is { } addedToFolder)
		{
			Added = addedToFolder;
		}
		if (!string.IsNullOrWhiteSpace(enrichedKind))
		{
			Kind = enrichedKind;
		}
		if (!string.IsNullOrWhiteSpace(enrichedVersion))
		{
			Version = enrichedVersion;
		}
		if (!string.IsNullOrWhiteSpace(enrichedComments))
		{
			Comments = enrichedComments;
		}
		if (enrichedTags is { Count: > 0 })
		{
			TagsText = string.Join(", ", enrichedTags);
		}
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasThumbnail))]
	public partial ImageSource? Thumbnail { get; set; }

	public bool HasThumbnail => Thumbnail is not null;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsSearchResult))]
	public partial string? SearchLocation { get; set; }

	public bool IsSearchResult => !string.IsNullOrEmpty(SearchLocation);

	// Set while the item name is being edited in place; the templates swap the
	// read-only label for an edit box based on this flag.
	[ObservableProperty]
	public partial bool IsInlineEditing { get; set; }

	// Marks the item that leads to the current path in a column view ancestor column.
	// The ancestor template paints its own highlight from this flag instead of using
	// ListView selection, whose ScrollIntoView would pin the row to the viewport top.
	[ObservableProperty]
	public partial bool IsColumnViewChainSelected { get; set; }

	// Hierarchy state for the details view tree: Depth drives the name column
	// indent, IsExpanded mirrors the disclosure triangle, and ShowDisclosure
	// marks directories that can be expanded in place.
	[ObservableProperty]
	public partial int Depth { get; set; }

	[ObservableProperty]
	public partial bool IsExpanded { get; set; }

	[ObservableProperty]
	public partial bool ShowDisclosure { get; set; }

	public string ModifiedText => Modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

	public string CreatedText => Created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

	public string LastOpenedText => LastOpened.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

	public string AddedText => Added.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

	public string SizeText => IsDirectory || Size is null ? string.Empty : FormatSize(Size.Value);

	public static string FormatSize(long value)
	{
		ReadOnlySpan<string> units = ["B", "KB", "MB", "GB", "TB"];
		double displayValue = value;
		int unitIndex = 0;

		while (displayValue >= 1024 && unitIndex < units.Length - 1)
		{
			displayValue /= 1024;
			unitIndex++;
		}

		string format = unitIndex is 0 ? "0" : "0.#";
		return $"{displayValue.ToString(format, CultureInfo.CurrentCulture)} {units[unitIndex]}";
	}
}
