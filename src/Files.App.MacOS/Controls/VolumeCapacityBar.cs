using Microsoft.UI.Xaml.Media;

namespace Files.App.MacOS.Controls;

public sealed class VolumeCapacityBar : Grid
{
	public static readonly DependencyProperty PercentageProperty = DependencyProperty.Register(nameof(Percentage), typeof(double), typeof(VolumeCapacityBar), new PropertyMetadata(0d, (d, _) => ((VolumeCapacityBar)d).UpdateFill()));
	public double Percentage { get => (double)GetValue(PercentageProperty); set => SetValue(PercentageProperty, value); }
	private readonly Border fill = new() { HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 38, 160, 211)) };
	public VolumeCapacityBar()
	{
		Height = 12;
		Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
		Children.Add(fill);
		SizeChanged += (_, _) => UpdateFill();
	}
	private void UpdateFill()
	{
		fill.Width = Math.Max(0, ActualWidth) * Math.Clamp(Percentage, 0, 100) / 100;
		fill.Background = new SolidColorBrush(Percentage >= 90 ? Windows.UI.Color.FromArgb(255, 211, 58, 52) : Windows.UI.Color.FromArgb(255, 38, 160, 211));
	}
}
