using Files.App.MacOS.Converters;
using Files.App.MacOS.Models;
using Files.App.MacOS.Services;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Files.App.MacOS.Controls;

public sealed class SidebarItemIcon : Grid
{
	public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SidebarItemIcon), new PropertyMetadata(string.Empty, (d, _) => ((SidebarItemIcon)d).Render()));
	public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
	public SidebarItemIcon()
	{
		IsHitTestVisible = false;
		Loaded += (_, _) => { AppearanceService.Changed += Changed; Render(); };
		Unloaded += (_, _) => AppearanceService.Changed -= Changed;
	}
	private void Changed(object? sender, EventArgs e) => Render();
	private void Render()
	{
		Children.Clear();
		bool windows = AppearanceService.CurrentStyle == AppVisualStyle.Windows11;
		string name = Glyph switch { "⌂" => "home", "▣" => "desktop", "PC" => "desktop", "↓" => "arrow_download", "▤" => "document", "▧" => "image", "♫" => "music_note_2", "▶" => "video", "☁" => "cloud", "◉" => "hard_drive", "◷" => "clock", "S" => "people", "T" => "delete", "A" => "grid", _ => "folder" };
		if (windows && name == "folder") { Children.Add(new FileItemIcon { IsFolder = true }); return; }
		Geometry? geometry = FluentIconData.Geometry(name);
		uint color = windows ? name switch { "home" => 0xFF9470C9, "image" => 0xFF9470C9, "music_note_2" => 0xFFE071A3, "video" => 0xFF9E77CC, "arrow_download" => 0xFF4AA385, _ => 0xFF4C97CD } : 0xFF7B8897;
		Brush foreground = !windows && AppearanceService.CurrentStyle == AppVisualStyle.MacOS
			? (Brush)Application.Current.Resources["FilesAccentBrush"] : new SolidColorBrush(AppearanceService.ColorFromArgb(color));
		Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = new PathIcon { Data = geometry, Width = 24, Height = 24, Foreground = foreground } });
	}
}
