using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Files.App.MacOS.Converters;

public sealed class FileSystemItemToIconConverter : IValueConverter
{
	private const string FolderIconData = "M2,4 H8 L10,6 H18 V17 H2 Z M4,8 V15 H16 V8 Z";
	private const string FileIconData = "M4,2 H13 L17,6 V18 H4 Z M13,4 V7 H16 Z M7,10 H14 V12 H7 Z M7,14 H14 V16 H7 Z";

	public object? Convert(object value, Type targetType, object parameter, string language) =>
		XamlBindingHelper.ConvertValue(typeof(Geometry), value is true ? FolderIconData : FileIconData);

	public object ConvertBack(object value, Type targetType, object parameter, string language) =>
		throw new NotSupportedException();
}
