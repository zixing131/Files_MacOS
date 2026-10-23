using Files.App.MacOS.Controls;
using Files.App.MacOS.Models;
using Files.App.MacOS.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Files.App.MacOS;

public sealed partial class MainPage
{
	private async Task ReportInteractionDiagnosticsAsync()
	{
		if (ViewModel.ActiveTab is not { } tab) return;
		var browser = tab.Browser;
		string originalPath = browser.CurrentPath;
		var originalLanguage = currentSettings.Language;
		var app = (App)Application.Current;
		var originalItems = browser.Items;
		int tabCount = ViewModel.Tabs.Count;
		app.ApplyLanguage(AppLanguagePreference.SimplifiedChinese);
		bool chinese = ((CommandLabel)NewCommandButton.Content).Content == "新建" &&
			EnumerateMenuFlyoutItems(PrimaryBackgroundContextFlyout.Items).OfType<MenuFlyoutItem>().First(item => Equals(item.Tag, "Paste")).Text == GetResource("BackgroundPasteItem/Text");
		app.ApplyLanguage(AppLanguagePreference.English);
		bool english = ((CommandLabel)NewCommandButton.Content).Content == "New";
		bool statePreserved = ReferenceEquals(originalItems, browser.Items) && browser.CurrentPath == originalPath && ViewModel.Tabs.Count == tabCount;
		app.ApplyLanguage(originalLanguage);
		await browser.NavigateAsync(ComputerLocation.Path);
		PrimaryComputerPage.Height = 240;
		UpdateLayout();
		await Task.Delay(150);
		UpdateLayout();
		bool scrollable = PrimaryComputerPage.ScrollLimit > 0;
		PrimaryComputerPage.ScrollBy(140);
		await Task.Delay(150);
		bool wheelRoute = PrimaryComputerPage.ScrollOffset > 0;
		PrimaryComputerPage.RevealSection("Locations");
		await Task.Delay(100);
		bool sectionNavigation = browser.IsComputerPage && browser.ComputerDevicesExpanded && PrimaryComputerPage.ScrollOffset > 0;
		var favorites = browser.ComputerFavorites.ToArray();
		bool favoriteIcons = favorites.Any(folder => folder.Path == "/Applications" && folder.Glyph == "A") && favorites.All(folder => !string.IsNullOrWhiteSpace(folder.Glyph));
		bool systemVolumesHidden = !browser.LocalVolumes.Any(volume => volume.Name is "Recovery" or "Preboot" or "VM" or "Update");
		Console.WriteLine($"FILES_MACOS_INTERACTION language_zh={chinese} language_en={english} language_preserves_state={statePreserved} computer_scroll_extent={scrollable} computer_scroll_moves={wheelRoute} section_navigation={sectionNavigation} favorite_icons={favoriteIcons} system_volumes_hidden={systemVolumesHidden}");
		PrimaryComputerPage.Height = double.NaN;
		await browser.NavigateAsync(originalPath);
		await ReportContextAndTreeDiagnosticsAsync();
		await ReportRegressionDiagnosticsAsync();
	}
	private async Task ReportContextAndTreeDiagnosticsAsync()
	{
		if (Browser is not { } browser) return;
		string originalPath = browser.CurrentPath;
		double originalNameWidth = DetailColumnWidths.Name;
		var fixture = Directory.CreateTempSubdirectory("files-tree-layout-");
		try
		{
			string parent = Path.Combine(fixture.FullName, "Parent");
			string nested = Path.Combine(parent, "Nested");
			Directory.CreateDirectory(Path.Combine(nested, "Folder"));
			await File.WriteAllTextAsync(Path.Combine(nested, "sample.txt"), "layout");
			await browser.NavigateAsync(ComputerLocation.Path);
			var menu = new MenuFlyout();
			AddSidebarMenuItem(menu, "ContextOpenItem/Text", "Open", new SidebarLocation("Fixture", fixture.FullName, "★"));
			menu.ShowAt(PrimaryComputerPage);
			await Task.Delay(50);
			bool menuOpened = menu.IsOpen;
			await SidebarMenuActionAsync(menu, menu.Items[0]);
			await Task.Delay(100);
			bool menuClosed = menuOpened && !menu.IsOpen && !PrimaryBackgroundContextFlyout.IsOpen &&
				!SecondaryBackgroundContextFlyout.IsOpen && browser.CurrentPath == fixture.FullName &&
				PrimaryPaneBorder.ContextFlyout is null && SecondaryPaneBorder.ContextFlyout is null;
			MenuFlyoutItem paste = EnumerateMenuFlyoutItems(PrimaryBackgroundContextFlyout.Items).OfType<MenuFlyoutItem>().First(item => Equals(item.Tag, "Paste"));
			PrepareBackgroundContextMenu(PrimaryBackgroundContextFlyout, browser, false);
			bool emptyPasteHidden = paste.Visibility == Visibility.Collapsed;
			PrepareBackgroundContextMenu(PrimaryBackgroundContextFlyout, browser, true);
			bool filePasteVisible = paste.Visibility == Visibility.Visible;
			PrepareBackgroundContextMenu(PrimaryBackgroundContextFlyout, browser, false);
			bool pasteReset = paste.Visibility == Visibility.Collapsed;
			browser.IsGridView = false;
			browser.IsColumnView = false;
			await browser.ToggleExpandedAsync(browser.Items.First(item => item.Path == parent));
			await browser.ToggleExpandedAsync(browser.Items.First(item => item.Path == nested));
			DetailColumnWidths.Name = 120;
			UpdateLayout();
			await Task.Delay(100);
			UpdateLayout();
			var panels = VisualDescendants(DetailsItems).OfType<TreeNamePanel>().ToArray();
			bool treeIcons = panels.Any(panel => panel.Depth == 2) && panels.All(panel =>
			{
				var icon = VisualDescendants(panel).OfType<FileItemIcon>().First();
				var point = icon.TransformToVisual(panel).TransformPoint(default);
				return icon.ActualWidth >= 24 && point.X >= 0 && point.X + icon.ActualWidth <= panel.ActualWidth;
			});
			Console.WriteLine($"FILES_MACOS_CONTEXT_TREE menu_closes_before_navigation={menuClosed} empty_paste_hidden={emptyPasteHidden} file_paste_visible={filePasteVisible} paste_state_resets={pasteReset} nested_icons_unclipped={treeIcons}");
		}
		finally
		{
			DetailColumnWidths.Name = originalNameWidth;
			await browser.NavigateAsync(originalPath);
			fixture.Delete(recursive: true);
		}
	}

	// 使用真实控件布局和临时目录验证复用、视图切换、窄窗格及窗口事务。
	private async Task ReportRegressionDiagnosticsAsync()
	{
		if (Browser is not { } browser || ViewModel.ActiveTab is not { } originalTab) return;
		var app = (App)Application.Current;
		string originalPath = browser.CurrentPath;
		var originalMode = GetViewMode(browser);
		var placement = app.GetPagePlacement(this);
		var fixture = Directory.CreateTempSubdirectory("files-regression-");
		bool views = true, selection = true, hoverReset = true, layout = true, menus = true, dragRollback = false, dragCommit = false, dragMerge = false, dragPositionStable = false;
		MainPage? floating = null;
		try
		{
			await browser.NavigateAsync(ComputerLocation.Path);
			foreach (var mode in new[] { BrowserViewMode.Grid, BrowserViewMode.Details, BrowserViewMode.Grid })
			{
				SetViewMode(browser, mode);
				UpdateLayout();
				await Task.Delay(80);
				var cards = VisualDescendants(PrimaryComputerPage).OfType<ToggleButton>().Where(card => card.DataContext is ComputerFolder or ComputerVolume).ToArray();
				views &= PrimaryComputerPage.IsGridLayout == (mode == BrowserViewMode.Grid) && cards.Length > 0 && WindowsViewButton.IsEnabled;
				foreach (var card in cards.Take(3)) card.IsChecked = true;
				selection &= PrimaryComputerPage.SelectedCardCount == 1;
				PrimaryComputerPage.ClearSelection();
				selection &= PrimaryComputerPage.SelectedCardCount == 0 && cards.All(card => card.BorderBrush is SolidColorBrush brush && brush.Color.A == 0);
				var menu = new MenuFlyout();
				AddSidebarMenuItem(menu, "ContextOpenItem/Text", "Open", new SidebarLocation("Fixture", fixture.FullName, "★"));
				ShowManagedContextMenu(menu, PrimaryComputerPage, new(20, 20));
				await Task.Delay(30);
				await SidebarMenuActionAsync(menu, menu.Items[0]);
				menus &= !menu.IsOpen && browser.CurrentPath == fixture.FullName;
				await browser.NavigateAsync(ComputerLocation.Path);
			}
			Directory.CreateDirectory(Path.Combine(fixture.FullName, "Empty"));
			Directory.CreateDirectory(Path.Combine(fixture.FullName, "Folder"));
			await browser.NavigateAsync(fixture.FullName);
			SetViewMode(browser, BrowserViewMode.Grid);
			for (int iteration = 0; iteration < 4; iteration++)
			{
				UpdateLayout();
				await Task.Delay(60);
				var containers = VisualDescendants(GridItems).OfType<ItemContainer>().ToArray();
				hoverReset &= containers.Length > 0;
				if (containers.FirstOrDefault() is { } container) FileGridContainer_PointerEntered(container, null!);
				await browser.RefreshAsync();
				UpdateLayout();
				await Task.Delay(60);
				hoverReset &= hoveredGridContainer is null && VisualDescendants(GridItems).OfType<ItemContainer>().All(item => item.Background is SolidColorBrush brush && brush.Color.A == 0);
				await browser.NavigateAsync(Path.Combine(fixture.FullName, "Empty"));
				await browser.RefreshAsync();
				hoverReset &= browser.Items.Count == 0;
				await browser.GoBackAsync();
			}
			await ViewModel.ToggleSplitViewAsync();
			isPreviewPaneOpen = true;
			if (placement is not null)
			foreach (double width in new[] { 640d, 900d, 1100d })
			{
				app.ApplyWindowPlacement(this, placement with { Width = width, Height = 600 });
				await Task.Delay(160);
				UpdateLayout();
				ApplySplitLayout();
				UpdateLayout();
				layout &= PrimaryPaneBorder.ActualWidth >= MinimumPaneWidth - 4 && SecondaryPaneBorder.ActualWidth >= MinimumPaneWidth - 4;
				foreach (var mode in new[] { BrowserViewMode.Grid, BrowserViewMode.Details, BrowserViewMode.Columns })
				{
					SetViewMode(browser, mode);
					SetViewMode(originalTab.SecondaryBrowser!, mode);
					UpdateLayout();
					foreach (var pane in new[] { PrimaryPaneBorder, SecondaryPaneBorder })
					{
						var icons = VisualDescendants(pane).OfType<FileItemIcon>().Where(icon => icon.ActualWidth > 0 && icon.ActualHeight > 0).ToArray();
						layout &= icons.All(icon => icon.ActualWidth >= 20 && icon.ActualHeight >= 20);
					}
				}
			}
			isPreviewPaneOpen = currentSettings.IsPreviewPaneOpen;
			await ViewModel.ToggleSplitViewAsync();
			if (placement is not null) app.ApplyWindowPlacement(this, placement);
			await ViewModel.NewTabAsync(fixture.FullName);
			var transferTab = ViewModel.ActiveTab!;
			int count = ViewModel.Tabs.Count;
			if (placement is not null)
			{
				floating = app.CreateTabDragWindow(this, transferTab, placement);
				if (floating is not null)
				{
					double expectedX = placement.X + 80, expectedY = placement.Y + 40;
					app.MoveDragWindow(floating, new(expectedX + 100, expectedY + placement.Height - 24), 100, 24);
					await floating.InitializationTask;
					// 模拟拖动中异步设置恢复晚到，必须保留刚刚跟随到的位置。
					app.ApplyWindowPlacement(floating, placement);
					await Task.Delay(80);
					var moved = app.GetPagePlacement(floating);
					dragPositionStable = moved is not null && Math.Abs(moved.X - expectedX) < 3 && Math.Abs(moved.Y - expectedY) < 3;
					int before = app.CaptureWindowSession(this).AdditionalWindowWorkspaces.Length;
					app.CloseWindow(floating); floating = null;
					dragRollback = ViewModel.Tabs.Contains(transferTab) && ViewModel.Tabs.Count == count && app.CaptureWindowSession(this).AdditionalWindowWorkspaces.Length == before;
				}
				floating = app.CreateTabDragWindow(this, transferTab, placement);
				if (floating is not null)
				{
					dragCommit = await app.CommitTabDragWindowAsync(this, floating, transferTab) && !ViewModel.Tabs.Contains(transferTab) && floating.ViewModel.Tabs.Count == 1;
					dragMerge = await app.MoveTabToWindowAsync(floating, this, floating.ViewModel.ActiveTab!, 0) && ViewModel.Tabs.Count == count;
					floating = null;
				}
			}
			foreach (var tab in ViewModel.Tabs.Where(tab => tab != originalTab).ToArray())
				if (tab.Browser.CurrentPath == fixture.FullName) ViewModel.CloseTab(tab);
			Console.WriteLine($"FILES_MACOS_REGRESSION style={currentSettings.VisualStyle} theme={currentSettings.Theme} computer_views={views} computer_selection_clear={selection} recycled_hover_reset={hoverReset} narrow_split_layout={layout} menu_navigation={menus} drag_cancel_transaction={dragRollback} drag_detach_transaction={dragCommit} drag_merge_transaction={dragMerge} drag_initialization_position_stable={dragPositionStable}");
		}
		finally
		{
			if (floating is not null) app.CloseWindow(floating);
			ViewModel.ActiveTab = originalTab;
			if (originalTab.IsSplitView) await ViewModel.ToggleSplitViewAsync();
			if (placement is not null) app.ApplyWindowPlacement(this, placement);
			await browser.NavigateAsync(originalPath);
			SetViewMode(browser, originalMode);
			fixture.Delete(recursive: true);
		}
	}

}
