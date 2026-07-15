using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Files.App.MacOS.Models;

namespace Files.App.MacOS.Converters;

public sealed class FileSystemItemToIconConverter : IValueConverter
{
	private const string FolderIconData = "M2,4 H8 L10,6 H18 V17 H2 Z M4,8 V15 H16 V8 Z";
	private const string FileIconData = "M4,2 H13 L17,6 V18 H4 Z M13,4 V7 H16 Z M7,10 H14 V12 H7 Z M7,14 H14 V16 H7 Z";
	private const string PackageIconData = "M4,3 H16 A2,2 0 0 1 18,5 V15 A2,2 0 0 1 16,17 H4 A2,2 0 0 1 2,15 V5 A2,2 0 0 1 4,3 Z M5,6 A1,1 0 1 0 5,8 A1,1 0 0 0 5,6 Z M10,6 A1,1 0 1 0 10,8 A1,1 0 0 0 10,6 Z M15,6 A1,1 0 1 0 15,8 A1,1 0 0 0 15,6 Z M5,11 A1,1 0 1 0 5,13 A1,1 0 0 0 5,11 Z M10,11 A1,1 0 1 0 10,13 A1,1 0 0 0 10,11 Z M15,11 A1,1 0 1 0 15,13 A1,1 0 0 0 15,11 Z";

	public object? Convert(object value, Type targetType, object parameter, string language)
	{
		string iconData = value switch
		{
			LocalFileSystemItem { IsPackage: true } => PackageIconData,
			LocalFileSystemItem { IsDirectory: true } => FolderIconData,
			LocalFileSystemItem => FileIconData,
			true => FolderIconData,
			_ => FileIconData,
		};
		return XamlBindingHelper.ConvertValue(typeof(Geometry), iconData);
	}

	public object ConvertBack(object value, Type targetType, object parameter, string language) =>
		throw new NotSupportedException();
}
