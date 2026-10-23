using Files.App.MacOS.Controls;
using Files.App.MacOS.Models;
using Files.App.MacOS.Services;
using Microsoft.UI.Xaml.Media;

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

}
