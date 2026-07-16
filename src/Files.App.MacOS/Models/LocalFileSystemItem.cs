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

	public DateTimeOffset LastOpened { get; } = lastOpened ?? modified;

	public DateTimeOffset Added { get; } = added ?? created ?? modified;

	public string Kind { get; } = kind ?? (isPackage
		? "app"
		: isDirectory
			? "folder"
			: System.IO.Path.GetExtension(path).TrimStart('.'));

	public string TagsText { get; } = string.Join(", ", tags ?? []);

	public string Version { get; } = version ?? string.Empty;

	public string Comments { get; } = comments ?? string.Empty;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasThumbnail))]
	public partial ImageSource? Thumbnail { get; set; }

	public bool HasThumbnail => Thumbnail is not null;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsSearchResult))]
	public partial string? SearchLocation { get; set; }

	public bool IsSearchResult => !string.IsNullOrEmpty(SearchLocation);

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
