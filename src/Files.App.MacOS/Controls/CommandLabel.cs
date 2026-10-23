using Microsoft.UI.Xaml.Media;

namespace Files.App.MacOS.Controls;

public sealed class CommandLabel : Grid
{
	public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(
		nameof(Content),
		typeof(string),
		typeof(CommandLabel),
		new PropertyMetadata(string.Empty, OnContentChanged));

	public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
		nameof(IconData),
		typeof(Geometry),
		typeof(CommandLabel),
		new PropertyMetadata(null, OnIconDataChanged));

	public static readonly DependencyProperty IsLabelVisibleProperty = DependencyProperty.Register(
		nameof(IsLabelVisible),
		typeof(bool),
		typeof(CommandLabel),
		new PropertyMetadata(true, OnIsLabelVisibleChanged));

	private readonly PathIcon icon = new()
	{
		Width = 16,
		Height = 16,
	};

	public double IconCanvasSize { set { icon.Width = value; icon.Height = value; } }

	private readonly TextBlock label = new()
	{
		VerticalAlignment = VerticalAlignment.Center,
	};

	public CommandLabel()
	{
		ColumnSpacing = 7;
		ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		Children.Add(new Viewbox { Width = 16, Height = 16, Child = icon, Stretch = Stretch.Uniform });
		SetColumn(label, 1);
		Children.Add(label);
	}

	public string Content
	{
		get => (string)GetValue(ContentProperty);
		set => SetValue(ContentProperty, value);
	}

	public Geometry? IconData
	{
		get => (Geometry?)GetValue(IconDataProperty);
		set => SetValue(IconDataProperty, value);
	}

	public bool IsLabelVisible
	{
		get => (bool)GetValue(IsLabelVisibleProperty);
		set => SetValue(IsLabelVisibleProperty, value);
	}

	private static void OnIsLabelVisibleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
	{
		if (sender is CommandLabel control)
		{
			bool visible = (bool)args.NewValue;
			control.label.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
			control.ColumnSpacing = visible ? 7 : 0;
		}
	}

	private static void OnContentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
	{
		if (sender is CommandLabel control)
		{
			control.label.Text = args.NewValue as string ?? string.Empty;
		}
	}

	private static void OnIconDataChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
	{
		if (sender is CommandLabel control)
		{
			control.icon.Data = args.NewValue as Geometry;
		}
	}
}
