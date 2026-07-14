using Microsoft.UI.Xaml.Data;

namespace Files.App.MacOS.Converters;

public sealed class BooleanToVisibilityConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, string language)
	{
		bool isVisible = value is true;
		if (parameter is string text && string.Equals(text, "Invert", StringComparison.Ordinal))
		{
			isVisible = !isVisible;
		}

		return isVisible ? Visibility.Visible : Visibility.Collapsed;
	}

	public object ConvertBack(object value, Type targetType, object parameter, string language)
	{
		throw new NotSupportedException();
	}
}
