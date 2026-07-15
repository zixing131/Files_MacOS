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
	bool isPackage = false) : ObservableObject
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

	[ObservableProperty]
	public partial ImageSource? Thumbnail { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsSearchResult))]
	public partial string? SearchLocation { get; set; }

	public bool IsSearchResult => !string.IsNullOrEmpty(SearchLocation);

	public string ModifiedText => Modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

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
