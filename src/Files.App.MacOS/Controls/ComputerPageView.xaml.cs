using Files.App.MacOS.Models;
using Files.App.MacOS.ViewModels;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.System;
using Microsoft.UI.Xaml.Media;

namespace Files.App.MacOS.Controls;

public sealed partial class ComputerPageView : UserControl
{
	public ComputerPageView()
	{
		InitializeComponent();
		SizeChanged += (_, _) => { PageScroller.Height = ActualHeight; PageScroller.Width = ActualWidth; };
		Loaded += (_, _) => { PageScroller.Height = ActualHeight; PageScroller.Width = ActualWidth; };
		AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Page_Wheel), true);
	}

	internal double ScrollOffset => PageScroller.VerticalOffset;
	internal double ScrollLimit => PageScroller.ScrollableHeight;

	internal bool ScrollBy(double distance)
	{
		if (Environment.GetEnvironmentVariable("FILES_MACOS_POINTER_DIAGNOSTICS") == "1")
			Console.WriteLine($"[computer-wheel] delta={distance} extent={PageScroller.ExtentHeight} viewport={PageScroller.ViewportHeight} offset={PageScroller.VerticalOffset}");
		if (PageScroller.ScrollableHeight <= 0 || distance == 0) return false;
		PageScroller.ChangeView(null, Math.Clamp(PageScroller.VerticalOffset + distance, 0, PageScroller.ScrollableHeight), null, true);
		return true;
	}

	private void Page_Wheel(object sender, PointerRoutedEventArgs e)
	{
		if (!e.Handled) e.Handled = ScrollBy(-e.GetCurrentPoint(this).Properties.MouseWheelDelta / 120d * 60);
	}

	internal void RevealSection(string section)
	{
		if (DataContext is not DirectoryBrowserViewModel browser) return;
		FrameworkElement heading;
		switch (section)
		{
			case "Locations": browser.ComputerDevicesExpanded = true; heading = DevicesHeading; break;
			case "Network": browser.ComputerNetworkExpanded = true; heading = NetworkHeading; break;
			default: browser.ComputerFavoritesExpanded = true; heading = FavoritesHeading; break;
		}
		UpdateLayout();
		double offset = heading.TransformToVisual(PageScroller.Content as UIElement).TransformPoint(new Windows.Foundation.Point()).Y;
		PageScroller.ChangeView(null, Math.Clamp(offset, 0, PageScroller.ScrollableHeight), null, true);
	}

	private Microsoft.UI.Xaml.Controls.Primitives.ToggleButton? selectedCard;
	private void Card_Checked(object sender, RoutedEventArgs e)
	{
		if (sender is not Microsoft.UI.Xaml.Controls.Primitives.ToggleButton card) return;
		if (selectedCard is not null && selectedCard != card) selectedCard.IsChecked = false;
		selectedCard = card;
	}

	private void ToggleGroup_Click(object sender, RoutedEventArgs e)
	{
		if (DataContext is not DirectoryBrowserViewModel browser || sender is not Button button) return;
		switch (button.Tag)
		{
			case "Devices": browser.ComputerDevicesExpanded = !browser.ComputerDevicesExpanded; break;
			case "Network": browser.ComputerNetworkExpanded = !browser.ComputerNetworkExpanded; break;
			case "Favorites": browser.ComputerFavoritesExpanded = !browser.ComputerFavoritesExpanded; break;
		}
	}
	private static string? GetPath(object? item) => item switch { ComputerVolume volume => volume.Path, ComputerFolder folder => folder.Path, _ => null };

	private async void Card_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
	{
		if (sender is FrameworkElement card && GetPath(card.DataContext) is string path && DataContext is DirectoryBrowserViewModel browser)
		{
			e.Handled = true;
			await browser.NavigateAsync(path);
		}
	}

	private static IEnumerable<ToggleButton> VisibleCards(DependencyObject parent)
	{
		if (parent is UIElement { Visibility: Visibility.Collapsed }) yield break;
		if (parent is ToggleButton card && GetPath(card.DataContext) is not null) yield return card;
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
			foreach (var child in VisibleCards(VisualTreeHelper.GetChild(parent, i))) yield return child;
	}

	private async void Card_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (sender is not ToggleButton card || DataContext is not DirectoryBrowserViewModel browser) return;
		if (e.Key == VirtualKey.Enter && GetPath(card.DataContext) is string path)
		{
			e.Handled = true;
			await browser.NavigateAsync(path);
			return;
		}
		var cards = VisibleCards(this).ToArray();
		int index = Array.IndexOf(cards, card);
		int columns = Math.Max(1, (int)Math.Floor((PageScroller.ViewportWidth - 24) / 300));
		int next = e.Key switch
		{
			VirtualKey.Left => index - 1, VirtualKey.Right => index + 1,
			VirtualKey.Up => index - columns, VirtualKey.Down => index + columns,
			VirtualKey.Home => 0, VirtualKey.End => cards.Length - 1, _ => -1,
		};
		if (next < 0 || next >= cards.Length) return;
		cards[next].IsChecked = true;
		cards[next].Focus(FocusState.Keyboard);
		double top = cards[next].TransformToVisual((UIElement)PageScroller.Content).TransformPoint(default).Y;
		double bottom = top + cards[next].ActualHeight;
		if (top < PageScroller.VerticalOffset) ScrollBy(top - PageScroller.VerticalOffset);
		else if (bottom > PageScroller.VerticalOffset + PageScroller.ViewportHeight) ScrollBy(bottom - PageScroller.VerticalOffset - PageScroller.ViewportHeight);
		e.Handled = true;
	}
}
