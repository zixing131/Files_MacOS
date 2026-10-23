using Files.App.MacOS.Models;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Files.App.MacOS.Services;

internal static class AppearanceService
{
	internal static AppVisualStyle CurrentStyle { get; private set; }
	internal static event EventHandler? Changed;

	internal static void Apply(AppVisualStyle style)
	{
		var resources = Application.Current.Resources;
		string[] keys = ["FilesBackgroundBrush", "FilesSidebarBrush", "FilesToolbarBrush", "FilesCardBrush", "FilesCardBorderBrush", "FilesSubtleBrush", "FilesAccentBrush", "FilesAccentSubtleBrush", "TabViewItemHeaderBackgroundSelected", "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed"];
		foreach (string theme in new[] { "Light", "Dark", "Default" })
		{
			var dictionary = (ResourceDictionary)resources.ThemeDictionaries[theme];
			bool light = theme == "Light";
			uint[] colors = light
				? [0xFFF3F3F3, 0xFFFAFAFA, 0xFFF9F9F9, 0xFFFFFFFF, 0xFFE5E5E5, 0x09000000, 0xFF0067C0, 0xFFE5F1FB, 0xFFF9F9F9, 0xFFE5F1FB, 0xFFD6EAF9, 0xFFC6E2F7]
				: [0xFF0B1528, 0xFF191919, 0xFF202632, 0xFF191919, 0xFF383838, 0x12FFFFFF, 0xFF60CDFF, 0xFF303030, 0xFF202632, 0xFF303030, 0xFF383838, 0xFF404040];
			if (style == AppVisualStyle.Light)
				colors = light
					? [0xFFF7F7F8, 0xFFF3F4F6, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFE1E4E9, 0xFFF7F8FA, 0xFF2463D4, 0xFFE5EEFC, 0xFFFFFFFF, 0xFFE5EEFC, 0xFFD8E6FA, 0xFFCDDEF5]
					: [0xFF202126, 0xFF27282E, 0xFF202126, 0xFF202126, 0xFF3B3D45, 0xFF292B31, 0xFF80ABF0, 0xFF35455F, 0xFF292B31, 0xFF35455F, 0xFF3A4C69, 0xFF415776];
			if (style == AppVisualStyle.MacOS)
				colors = light
					? [0xFFF4F4F4, 0xFFEBEBED, 0xFFF4F4F4, 0xFFFFFFFF, 0xFFDADADD, 0xFFF4F4F4, 0xFF007AFF, 0xFFDCE8F8, 0xFFFFFFFF, 0xFFDCE8F8, 0xFFD4E3F6, 0xFFCCDCF2]
					: [0xFF252525, 0xFF2D2D2F, 0xFF292929, 0xFF1E1E1E, 0xFF414143, 0xFF282828, 0xFF0A84FF, 0xFF35485F, 0xFF383838, 0xFF35485F, 0xFF3C516B, 0xFF435C77];
			for (int i = 0; i < keys.Length; i++)
			{
				Color color = ColorFromArgb(colors[i]);
				if (dictionary.TryGetValue(keys[i], out object? value) && value is SolidColorBrush brush) brush.Color = color;
				else dictionary[keys[i]] = new SolidColorBrush(color);
			}
		}
		CurrentStyle = style;
		Changed?.Invoke(null, EventArgs.Empty);
	}

	internal static Color ColorFromArgb(uint value) => Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
}
