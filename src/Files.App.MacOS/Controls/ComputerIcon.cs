using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace Files.App.MacOS.Controls;

public sealed class ComputerIcon : Viewbox
{
	public ComputerIcon()
	{
		Stretch = Stretch.Uniform;
		var canvas = new Canvas { Width = 32, Height = 32 };
		canvas.Children.Add(new ShapePath { Data = Geometry("M2,4 H30 V24 H2 Z M12,24 H20 V28 H24 V30 H8 V28 H12 Z"), Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 139, 155, 166)) });
		canvas.Children.Add(new ShapePath
		{
			Data = Geometry("M3,5 H29 V22 H3 Z"),
			Fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), GradientStops = { new GradientStop { Color = Windows.UI.Color.FromArgb(255, 77, 218, 232), Offset = 0 }, new GradientStop { Color = Windows.UI.Color.FromArgb(255, 23, 130, 182), Offset = 1 } } }
		});
		Child = canvas;
		IsHitTestVisible = false;
	}
	private static Geometry Geometry(string path) => (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), path);
}
