using Microsoft.UI.Xaml.Media;

namespace Files.App.MacOS.Controls;

public sealed class SettingsCategoryView : Grid
{
	private readonly ListView navigation = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(8), BorderThickness = new Thickness(0) };
	private readonly ScrollViewer scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Enabled, HorizontalScrollMode = ScrollMode.Disabled, Padding = new Thickness(22, 10, 18, 18) };
	private readonly List<StackPanel> pages = [];
	internal ScrollViewer ScrollContainer => scroller;
	public SettingsCategoryView()
	{
		ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(156) });
		ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		navigation.Background = (Brush)Application.Current.Resources["FilesSidebarBrush"];
		Children.Add(navigation);
		SetColumn(scroller, 1);
		Children.Add(scroller);
		navigation.SelectionChanged += (_, _) =>
		{
			if (navigation.SelectedIndex < 0 || navigation.SelectedIndex >= pages.Count) return;
			scroller.Content = pages[navigation.SelectedIndex];
			scroller.ChangeView(null, 0, null, true);
		};
	}
	public StackPanel AddCategory(string title)
	{
		var page = new StackPanel { Spacing = 12 };
		page.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
		pages.Add(page);
		navigation.Items.Add(new ListViewItem { Content = title, MinHeight = 38, Padding = new Thickness(10, 6, 8, 6), CornerRadius = new CornerRadius(7) });
		if (pages.Count == 1) navigation.SelectedIndex = 0;
		return page;
	}
	public void FinishLayout()
	{
		foreach (var page in pages)
		{
			// 每个设置独立承载，避免长标签挤压开关，保持一致的卡片留白。
			for (int i = 1; i < page.Children.Count; i++)
			{
				if (page.Children[i] is TextBlock) continue;
				UIElement child = page.Children[i];
				page.Children.RemoveAt(i);
				page.Children.Insert(i, new Border { Child = child, Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(9), Background = (Brush)Application.Current.Resources["FilesSubtleBrush"] });
			}
		}
	}
}
