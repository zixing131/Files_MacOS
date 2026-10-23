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
		bool chinese = ((CommandLabel)NewCommandButton.Content).Content == "新建";
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
	}
}
