using Windows.Foundation;

namespace Files.App.MacOS.Controls;

public sealed class VolumeWrapPanel : Panel
{
	private const double CardWidth = 300;
	protected override Size MeasureOverride(Size availableSize)
	{
		double width = double.IsFinite(availableSize.Width) ? availableSize.Width : CardWidth;
		foreach (UIElement child in Children) child.Measure(new Size(Math.Min(CardWidth, width), double.PositiveInfinity));
		return Layout(width, false);
	}
	protected override Size ArrangeOverride(Size finalSize)
	{
		Layout(finalSize.Width, true);
		return finalSize;
	}
	private Size Layout(double width, bool arrange)
	{
		int columns = Math.Max(1, (int)Math.Floor(width / CardWidth));
		double itemWidth = Math.Min(CardWidth, width), y = 0, rowHeight = 0;
		for (int index = 0; index < Children.Count; index++)
		{
			int column = index % columns;
			if (index > 0 && column == 0) { y += rowHeight; rowHeight = 0; }
			UIElement child = Children[index];
			double height = child.DesiredSize.Height;
			if (arrange) child.Arrange(new Rect(column * itemWidth, y, itemWidth, height));
			rowHeight = Math.Max(rowHeight, height);
		}
		return new Size(width, y + rowHeight);
	}
}
