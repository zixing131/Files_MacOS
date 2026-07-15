using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Files.App.MacOS.Models;

namespace Files.App.MacOS.Converters;

public sealed class FileSystemItemToIconConverter : IValueConverter
{
	private const string FolderIconData = "M2,4 H8 L10,6 H18 V17 H2 Z M4,8 V15 H16 V8 Z";
	private const string FileIconData = "M4,2 H13 L17,6 V18 H4 Z M13,4 V7 H16 Z M7,10 H14 V12 H7 Z M7,14 H14 V16 H7 Z";
	private const string ArchiveIconData = "M3,2 H17 V6 H16 V18 H4 V6 H3 Z M5,4 H15 V5 H5 Z M8,8 H12 V10 H8 Z M9,11 H11 V13 H9 Z M8,14 H12 V16 H8 Z";
	private const string CodeIconData = "M4,2 H13 L17,6 V18 H4 Z M13,4 V7 H16 Z M8,9 L5.5,12 L8,15 L9.3,13.8 L7.8,12 L9.3,10.2 Z M12,9 L10.7,10.2 L12.2,12 L10.7,13.8 L12,15 L14.5,12 Z";
	private const string ImageIconData = "M3,3 H17 V17 H3 Z M5,5 V15 H15 V5 Z M7,7 A1.5,1.5 0 1 0 7,10 A1.5,1.5 0 0 0 7,7 Z M5,15 L9,10.5 L11.5,13 L13,11.5 L15,15 Z";
	private const string AudioIconData = "M8,4 V14 A3,3 0 1 1 6,11 V6 L17,4 V13 A3,3 0 1 1 15,10 V6 Z";
	private const string VideoIconData = "M3,4 H17 V16 H3 Z M5,6 V14 H7 V6 Z M9,7 L14,10 L9,13 Z";
	private const string PackageIconData = "M4,3 H16 A2,2 0 0 1 18,5 V15 A2,2 0 0 1 16,17 H4 A2,2 0 0 1 2,15 V5 A2,2 0 0 1 4,3 Z M5,6 A1,1 0 1 0 5,8 A1,1 0 0 0 5,6 Z M10,6 A1,1 0 1 0 10,8 A1,1 0 0 0 10,6 Z M15,6 A1,1 0 1 0 15,8 A1,1 0 0 0 15,6 Z M5,11 A1,1 0 1 0 5,13 A1,1 0 0 0 5,11 Z M10,11 A1,1 0 1 0 10,13 A1,1 0 0 0 10,11 Z M15,11 A1,1 0 1 0 15,13 A1,1 0 0 0 15,11 Z";
	private static readonly HashSet<string> ArchiveExtensions = CreateExtensions(".7z .bz2 .gz .rar .tar .tgz .xz .zip");
	private static readonly HashSet<string> CodeExtensions = CreateExtensions(".c .cc .cpp .cs .css .go .h .hpp .html .java .js .json .kt .md .php .plist .py .rb .rs .sh .swift .ts .xml .yaml .yml");
	private static readonly HashSet<string> ImageExtensions = CreateExtensions(".avif .bmp .gif .heic .jpeg .jpg .png .svg .tif .tiff .webp");
	private static readonly HashSet<string> AudioExtensions = CreateExtensions(".aac .aiff .flac .m4a .mp3 .ogg .wav");
	private static readonly HashSet<string> VideoExtensions = CreateExtensions(".avi .m4v .mkv .mov .mp4 .mpeg .webm");

	public object? Convert(object value, Type targetType, object parameter, string language)
	{
		string iconData = value switch
		{
			LocalFileSystemItem { IsPackage: true } => PackageIconData,
			LocalFileSystemItem { IsDirectory: true } => FolderIconData,
			LocalFileSystemItem item => GetFileIconData(item.Name),
			true => FolderIconData,
			_ => FileIconData,
		};
		return XamlBindingHelper.ConvertValue(typeof(Geometry), iconData);
	}

	private static string GetFileIconData(string name)
	{
		string extension = Path.GetExtension(name);
		return extension.ToLowerInvariant() switch
		{
			var value when ArchiveExtensions.Contains(value) => ArchiveIconData,
			var value when ImageExtensions.Contains(value) => ImageIconData,
			var value when AudioExtensions.Contains(value) => AudioIconData,
			var value when VideoExtensions.Contains(value) => VideoIconData,
			var value when CodeExtensions.Contains(value) => CodeIconData,
			_ => FileIconData,
		};
	}

	private static HashSet<string> CreateExtensions(string values) =>
		values.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

	public object ConvertBack(object value, Type targetType, object parameter, string language) =>
		throw new NotSupportedException();
}
