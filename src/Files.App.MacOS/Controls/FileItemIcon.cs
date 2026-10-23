using System.ComponentModel;
using Files.App.MacOS.Converters;
using Files.App.MacOS.Models;
using Files.App.MacOS.Services;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace Files.App.MacOS.Controls;

public sealed class FileItemIcon : Grid
{
	public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(LocalFileSystemItem), typeof(FileItemIcon), new PropertyMetadata(null, OnItemChanged));
	public static readonly DependencyProperty IsFolderProperty = DependencyProperty.Register(nameof(IsFolder), typeof(bool), typeof(FileItemIcon), new PropertyMetadata(false, (d, _) => ((FileItemIcon)d).Render()));
	private bool subscribed;
	public LocalFileSystemItem? Item { get => (LocalFileSystemItem?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }
	public bool IsFolder { get => (bool)GetValue(IsFolderProperty); set => SetValue(IsFolderProperty, value); }

	public FileItemIcon()
	{
		IsHitTestVisible = false;
		Loaded += (_, _) => { subscribed = true; AppearanceService.Changed += AppearanceChanged; if (Item is { } item) item.PropertyChanged += ItemChanged; Render(); };
		Unloaded += (_, _) => { subscribed = false; AppearanceService.Changed -= AppearanceChanged; if (Item is { } item) item.PropertyChanged -= ItemChanged; };
	}

	private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		var icon = (FileItemIcon)d;
		if (icon.subscribed)
		{
			if (e.OldValue is LocalFileSystemItem oldItem) oldItem.PropertyChanged -= icon.ItemChanged;
			if (e.NewValue is LocalFileSystemItem newItem) newItem.PropertyChanged += icon.ItemChanged;
		}
		icon.Render();
	}
	private void AppearanceChanged(object? sender, EventArgs e) => Render();
	private void ItemChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(LocalFileSystemItem.Thumbnail)) Render(); }

	private void Render()
	{
		Children.Clear();
		bool folder = IsFolder || Item?.IsNavigableDirectory == true;
		bool windows = AppearanceService.CurrentStyle == AppVisualStyle.Windows11;
		// 保留文稿内容缩略图与应用图标，仅替换系统文件夹外观。
		if (Item?.Thumbnail is { } thumbnail && (!windows || !folder))
		{
			Children.Add(new Image { Source = thumbnail, Stretch = Stretch.Uniform });
			return;
		}
		if (folder && windows)
		{
			var canvas = new Canvas { Width = 32, Height = 32 };
			canvas.Children.Add(Path("M2 8 Q2 5 5 5 H12 L15 8 H27 Q30 8 30 11 V25 Q30 27 27 27 H5 Q2 27 2 24 Z", 0xFFE4A62C));
			canvas.Children.Add(Path("M3 11 H29 V15 H3 Z", 0xFFFFE8A3));
			var front = Path("M2 13 Q2 11 4 11 H12 L15 13 H29 Q31 13 30.7 15 L29 25 Q28.7 27 26 27 H5 Q3 27 2.8 25 Z", 0xFFFFD45C);
			front.Fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), GradientStops = { new GradientStop { Color = AppearanceService.ColorFromArgb(0xFFFFDC75), Offset = 0 }, new GradientStop { Color = AppearanceService.ColorFromArgb(0xFFF5B72D), Offset = 1 } } };
			canvas.Children.Add(front);
			Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform });
			return;
		}
		Geometry? data = windows ? (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), FluentIconData.Paths["document"])
			: (Geometry?)new FileSystemItemToIconConverter().Convert(Item is { } item ? item : folder, typeof(Geometry), null!, string.Empty);
		Children.Add(new Viewbox { Child = new PathIcon { Width = 24, Height = 24, Data = data, Foreground = new SolidColorBrush(AppearanceService.ColorFromArgb(windows ? 0xFF7F9CB3 : 0xFF699DCB)) }, Stretch = Stretch.Uniform });
	}

	private static ShapePath Path(string data, uint color) => new() { Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data), Fill = new SolidColorBrush(AppearanceService.ColorFromArgb(color)) };
}
