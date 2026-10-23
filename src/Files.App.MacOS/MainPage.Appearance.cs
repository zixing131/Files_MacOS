using Files.App.MacOS.Controls;
using Files.App.MacOS.Models;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Files.App.MacOS;

public sealed partial class MainPage
{

	internal void ApplyAppearance(AppThemePreference theme, AppVisualStyle style)
	{
		currentSettings = currentSettings with { Theme = theme, VisualStyle = style };
		persistedSettingsBaseline = persistedSettingsBaseline with { Theme = theme, VisualStyle = style };
		ApplyTheme(theme);
		ViewModel.ApplyVisualStyle(style);
		bool windows = style == AppVisualStyle.Windows11;
		bool light = style == AppVisualStyle.Light;
		SidebarBorder.Margin = light ? new Thickness(10, 8, 8, 10) : new Thickness(0);
		SidebarBorder.CornerRadius = new CornerRadius(light ? 12 : 0);
		SidebarBorder.BorderThickness = light ? new Thickness(1) : new Thickness(0, 0, 1, 0);
		FontSize = style == AppVisualStyle.MacOS ? 13 : 14;
		Grid parent = windows ? RootLayout : ContentArea;
		if (CommandToolbarBorder.Parent != parent)
		{
			((Panel)CommandToolbarBorder.Parent).Children.Remove(CommandToolbarBorder);
			parent.Children.Add(CommandToolbarBorder);
		}
		Grid.SetRow(CommandToolbarBorder, windows ? 2 : 0);
		ContentArea.RowDefinitions[0].Height = new GridLength(windows ? 0 : 44);
		CommandToolbarBorder.MinHeight = windows ? 48 : 38;
		CommandToolbarBorder.Margin = windows ? new Thickness(0) : new Thickness(0, 0, 0, 6);
		CommandToolbarBorder.CornerRadius = new CornerRadius(light ? 8 : 0);
		CommandToolbarBorder.BorderThickness = windows ? new Thickness(0, 1, 0, 1) : new Thickness(0);
		ContentArea.Padding = windows ? new Thickness(0) : new Thickness(6, 4, 10, 0);
		PrimaryPaneBorder.CornerRadius = SecondaryPaneBorder.CornerRadius = new CornerRadius(light ? 8 : 0);
		AddressBarBorder.CornerRadius = SearchContainer.CornerRadius = new CornerRadius(windows ? 4 : 8);
		RootLayout.RowDefinitions[1].Height = new GridLength(windows ? 52 : 48);
		Tabs.TabWidthMode = windows ? TabViewWidthMode.SizeToContent : TabViewWidthMode.Equal;
		foreach (Button command in new[] { SortCommandButton, WindowsViewButton, MoreCommandsButton })
		{
			((Panel)command.Parent).Children.Remove(command);
			(windows ? FileCommandStrip : ViewCommandStrip).Children.Add(command);
		}
		if (!windows)
		{
			ViewCommandStrip.Children.Remove(MoreCommandsButton);
			ViewCommandStrip.Children.Insert(0, MoreCommandsButton);
		}
		UndoButton.Visibility = RedoButton.Visibility = HistoryCommandSeparator.Visibility = windows ? Visibility.Collapsed : Visibility.Visible;
		SplitViewButton.Visibility = windows ? Visibility.Collapsed : Visibility.Visible;
		ToggleSidebarButton.Visibility = windows ? Visibility.Collapsed : Visibility.Visible;
		SearchLayout.ColumnDefinitions[0].Width = windows ? new GridLength(1, GridUnitType.Star) : new GridLength(28);
		SearchLayout.ColumnDefinitions[1].Width = windows ? new GridLength(28) : new GridLength(1, GridUnitType.Star);
		Grid.SetColumn(SearchBox, windows ? 0 : 1);
		Grid.SetColumn(SearchGlyph, windows ? 1 : 0);
		SearchBox.Margin = new Thickness(windows ? 12 : 0, 0, 0, 0);
		foreach (var (button, icon) in new (Button, string)[]
		{
			(NewCommandButton, "add"), (CutButton, "cut"), (CopyButton, "copy"), (PasteButton, "clipboard_paste"),
			(RenameButton, "rename"), (ShareButton, "share"), (DeleteButton, "delete"), (SortCommandButton, "arrow_sort"),
			(UndoButton, "arrow_undo"), (RedoButton, "arrow_redo"), (PreviewPaneButton, "panel_right"),
			(OpenFolderButton, "folder"), (ConnectServerButton, "globe"), (ClearRecentButton, "clock"), (SettingsButton, "settings")
		})
		{
			if (button.Content is not CommandLabel label) continue;
			label.IconData = IconGeometry(icon);
			label.IconCanvasSize = 24;
			button.CornerRadius = new CornerRadius(windows ? 4 : 7);
		}
		foreach (var (button, name) in new (Button, string)[] { (BackButton, "arrow_left"), (ForwardButton, "arrow_right"), (UpButton, "arrow_up"), (RefreshButton, "arrow_clockwise"), (ToggleSidebarButton, "panel_left"), (MoreCommandsButton, "more_horizontal") })
		{
			button.Content = new Viewbox { Width = 18, Height = 18, Stretch = Stretch.Uniform, Child = new PathIcon { Width = 24, Height = 24, Data = IconGeometry(name) } };
		}
		foreach (var list in new[] { DetailsItems, SecondaryDetailsItems })
			for (int index = 0; index < list.Items.Count; index++)
				if (list.ContainerFromIndex(index) is ListViewItem row) ApplyDetailsRow(row, index);
		UpdateCommandToolbarLayout(CommandToolbarBorder.ActualWidth);
		UpdatePaneVisuals();
		if (accessibilityDisplayOptions.HasFlag(MacOSAccessibilityDisplayOptions.IncreaseContrast)) ApplyAccessibilityDisplayOptions(accessibilityDisplayOptions);
	}

	private void DetailsContainer_ContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
	{
		if (args.ItemContainer is not ListViewItem container) return;
		ApplyDetailsRow(container, args.ItemIndex);
	}

	private void ApplyDetailsRow(ListViewItem container, int index)
	{
		container.MinHeight = currentSettings.VisualStyle == AppVisualStyle.Windows11 ? 36 : 30;
		container.Padding = new Thickness(0);
		container.Background = index % 2 == 1 && currentSettings.VisualStyle != AppVisualStyle.Windows11
			? (Brush)Application.Current.Resources["FilesSubtleBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
	}

	private async void WindowsTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (currentSettings.VisualStyle != AppVisualStyle.Windows11 || IsTextInputFocused()) return;
		args.Handled = true;
		if (sender.Key == Windows.System.VirtualKey.T) await ViewModel.NewTabAsync();
		else if (ViewModel.ActiveTab is { } tab) ViewModel.CloseTab(tab);
	}

	private static Geometry IconGeometry(string name) => FluentIconData.Geometry(name);
}
