using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Files.App.MacOS.Converters;

public sealed class SidebarGlyphToIconConverter : IValueConverter
{
	private static readonly IReadOnlyDictionary<string, string> IconData = new Dictionary<string, string>(StringComparer.Ordinal)
	{
		["⌄"] = "M2,4 H14 L8,11 Z",
		["›"] = "M4,2 L11,8 L4,14 Z",
		["⌂"] = "M2,9 L10,2 L18,9 V18 H12 V12 H8 V18 H2 Z",
		["▣"] = "M2,3 H18 V15 H11 V17 H15 V19 H5 V17 H9 V15 H2 Z M4,5 V13 H16 V5 Z",
		["↓"] = "M9,2 H11 V12 L15,8 L17,10 L10,17 L3,10 L5,8 L9,12 Z",
		["▤"] = "M4,2 H13 L17,6 V18 H4 Z M13,4 V7 H16 Z M6,9 H15 V11 H6 Z M6,13 H15 V15 H6 Z",
		["▧"] = "M2,3 H18 V17 H2 Z M4,5 V15 H16 V5 Z M6,7 A2,2 0 1 0 6,11 A2,2 0 0 0 6,7 Z M4,15 L9,10 L12,13 L14,11 L16,15 Z",
		["♫"] = "M9,3 V13 A3,3 0 1 1 7,10 V5 L17,3 V12 A3,3 0 1 1 15,9 V5 Z",
		["▶"] = "M3,3 H17 V17 H3 Z M5,5 V15 H7 V5 Z M9,7 L14,10 L9,13 Z",
		["★"] = "M10,2 L12.4,7 L18,7.6 L14,11.5 L15,17 L10,14.3 L5,17 L6,11.5 L2,7.6 L7.6,7 Z",
		["☁"] = "M6,16 H16 A4,4 0 0 0 16,8 A6,6 0 0 0 5,7 A4.5,4.5 0 0 0 6,16 Z",
		["◉"] = "M3,2 H17 V18 H3 Z M5,4 V16 H15 V4 Z M10,6 A4,4 0 1 0 10,14 A4,4 0 0 0 10,6 Z M10,9 A1,1 0 1 0 10,11 A1,1 0 0 0 10,9 Z",
		["◷"] = "M8,1 A7,7 0 1 1 1,8 A0.5,0.5 0 0 1 2,8 A6,6 0 1 0 3.5,4.03 V6.5 H7 A0.5,0.5 0 0 1 7,7.5 H3 A0.5,0.5 0 0 1 2.5,7 V3 A0.5,0.5 0 0 1 3.5,3 V5.32 A7,7 0 0 1 8,1 Z M8.5,4 A0.5,0.5 0 0 1 9,4.5 V7.79 L11.35,10.14 A0.5,0.5 0 0 1 10.65,10.86 L8.15,8.36 A0.5,0.5 0 0 1 8,8 V4.5 A0.5,0.5 0 0 1 8.5,4 Z",
	};

	public object? Convert(object value, Type targetType, object parameter, string language)
	{
		string key = value as string ?? string.Empty;
		string path = IconData.TryGetValue(key, out string? iconData)
			? iconData
			: "M2,4 H8 L10,6 H18 V17 H2 Z";
		return XamlBindingHelper.ConvertValue(typeof(Geometry), path);
	}

	public object ConvertBack(object value, Type targetType, object parameter, string language) =>
		throw new NotSupportedException();
}
