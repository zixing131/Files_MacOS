namespace Files.App.MacOS.Controls;

// 缩进只占名称单元格的空间；始终为展开箭头和图标保留完整宽度。
public sealed class TreeNamePanel : Grid
{
	public static readonly DependencyProperty DepthProperty = DependencyProperty.Register(
		nameof(Depth), typeof(int), typeof(TreeNamePanel), new PropertyMetadata(0, OnDepthChanged));
	public int Depth { get => (int)GetValue(DepthProperty); set => SetValue(DepthProperty, value); }

	public TreeNamePanel()
	{
		ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
		ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
		ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		ColumnSpacing = 2;
		SizeChanged += (_, _) => UpdateIndent();
	}

	private static void OnDepthChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((TreeNamePanel)sender).UpdateIndent();
	private void UpdateIndent()
	{
		// 极深目录或窄名称列也不能挤掉图标，至少留下 20 像素给文件名。
		double indent = Math.Min(Math.Max(0, Depth) * 16d, Math.Max(0, ActualWidth - 64));
		Padding = new Thickness(indent, 0, 0, 0);
	}
}
