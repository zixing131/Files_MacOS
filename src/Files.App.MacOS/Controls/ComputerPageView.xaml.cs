using System.ComponentModel;
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
		AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Page_PointerPressed), true);
		DataContextChanged += (_, _) => AttachBrowser();
		Loaded += (_, _) => AttachBrowser();
		Unloaded += (_, _) => { if (observedBrowser is not null) observedBrowser.PropertyChanged -= Browser_Changed; observedBrowser = null; ClearSelection(); };
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

	private DirectoryBrowserViewModel? observedBrowser;
	private ToggleButton? selectedCard;
	private ToggleButton? hoveredCard;
	private bool? gridMode;
	internal int SelectedCardCount => VisibleCards(this).Count(card => card.IsChecked == true);
	internal bool IsGridLayout => gridMode == true;
	private void AttachBrowser()
	{
		if (observedBrowser is not null) observedBrowser.PropertyChanged -= Browser_Changed;
		observedBrowser = DataContext as DirectoryBrowserViewModel;
		if (observedBrowser is not null) observedBrowser.PropertyChanged += Browser_Changed;
		ClearSelection();
		UpdateViewMode();
	}
	private void Browser_Changed(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(DirectoryBrowserViewModel.IsGridView) or nameof(DirectoryBrowserViewModel.IsColumnView)) UpdateViewMode();
		if (e.PropertyName is nameof(DirectoryBrowserViewModel.CurrentPath) or nameof(DirectoryBrowserViewModel.ComputerFavorites) or nameof(DirectoryBrowserViewModel.LocalVolumes)) ClearSelection();
	}
	private void UpdateViewMode()
	{
		bool grid = observedBrowser?.IsGridView == true && observedBrowser?.IsColumnView != true;
		if (gridMode == grid) return;
		ClearSelection();
		gridMode = grid;
		FavoriteCards.ItemTemplate = (DataTemplate)Resources[grid ? "FavoriteTemplate" : "FavoriteDetailsTemplate"];
		DeviceCards.ItemTemplate = NetworkCards.ItemTemplate = (DataTemplate)Resources[grid ? "VolumeTemplate" : "VolumeDetailsTemplate"];
		foreach (var items in new[] { FavoriteCards, DeviceCards, NetworkCards }) items.ItemsPanel = (ItemsPanelTemplate)Resources[grid ? "VolumesPanel" : "DetailsPanel"];
	}
	internal void ClearSelection()
	{
		if (selectedCard is { } previous) { previous.IsChecked = false; UpdateCardVisual(previous); }
		selectedCard = null;
		hoveredCard = null;
		foreach (var card in VisibleCards(this)) { card.IsChecked = false; UpdateCardVisual(card); }
	}
	private void Page_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
		for (DependencyObject? node = e.OriginalSource as DependencyObject; node is not null && node != this; node = VisualTreeHelper.GetParent(node))
			if (node is ButtonBase or ScrollBar) return;
		ClearSelection();
	}
	// 状态直接由当前选中项和悬停项决定，避免不同视觉状态组写入同一背景后残留。
	private void UpdateCardVisual(ToggleButton card)
	{
		card.Background = card.IsChecked == true || ReferenceEquals(card, hoveredCard) ? (Brush)Application.Current.Resources[card.IsChecked == true ? "FilesAccentSubtleBrush" : "FilesSubtleBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
		card.BorderBrush = card.IsChecked == true ? (Brush)Application.Current.Resources["FilesAccentBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
	}
	private void Card_Click(object sender, RoutedEventArgs e) { if (sender is ToggleButton card) card.IsChecked = true; }
	private void Card_Loaded(object sender, RoutedEventArgs e) { if (sender is ToggleButton card) UpdateCardVisual(card); }
	private void Card_PointerEntered(object sender, PointerRoutedEventArgs e) { if (sender is ToggleButton card) { var old = hoveredCard; hoveredCard = card; if (old is not null) UpdateCardVisual(old); UpdateCardVisual(card); } }
	private void Card_PointerExited(object sender, PointerRoutedEventArgs e) { if (sender is ToggleButton card) { if (hoveredCard == card) hoveredCard = null; UpdateCardVisual(card); } }
	private void Card_Unchecked(object sender, RoutedEventArgs e) { if (sender is ToggleButton card) { if (selectedCard == card) selectedCard = null; UpdateCardVisual(card); } }
	private void Card_Checked(object sender, RoutedEventArgs e)
	{
		if (sender is not ToggleButton card) return;
		if (selectedCard is not null && selectedCard != card) selectedCard.IsChecked = false;
		selectedCard = card;
		UpdateCardVisual(card);
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
		int columns = gridMode != true ? 1 : Math.Max(1, (int)Math.Floor((PageScroller.ViewportWidth - 24) / 300));
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
