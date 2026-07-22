using Microsoft.UI.Xaml.Data;

namespace Files.App.MacOS.Converters;

public sealed class DepthToThicknessConverter : IValueConverter
{
	private const int IndentPerLevel = 16;

	public object Convert(object value, Type targetType, object parameter, string language)
	{
		int depth = value is int level ? Math.Max(level, 0) : 0;
		return new Thickness(depth * IndentPerLevel, 0, 0, 0);
	}

	public object ConvertBack(object value, Type targetType, object parameter, string language)
	{
		throw new NotSupportedException();
	}
}
