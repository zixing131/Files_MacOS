using Files.App.MacOS.Models;
using Files.App.MacOS.Services;
using Files.App.MacOS.ViewModels;

namespace Files.App.MacOS;

public sealed partial class MainPage
{
	private async Task ReportComputerDiagnosticsAsync()
	{
		string root = Directory.CreateTempSubdirectory("files-computer-diagnostics-").FullName;
		var model = new MainPageViewModel();
		var restored = new MainPageViewModel();
		try
		{
			model.ApplySettings(new AppSettings(VisualStyle: AppVisualStyle.Windows11, FavoritePaths: [root]));
			await model.InitializeAsync();
			DirectoryBrowserViewModel browser = model.ActiveBrowser!;
			bool initial = browser.IsComputerPage && !browser.ShowGridView && !browser.ShowDetailsView && !browser.ShowColumnView && !browser.IsEmptyFolder && browser.Items.Count == 0;
			ComputerVolume[] volumes = browser.LocalVolumes.Concat(browser.NetworkVolumes).ToArray();
			bool capacity = volumes.Any(volume => volume.Path == "/") && volumes.All(volume => !volume.HasCapacity || volume.TotalBytes > 0 && volume.AvailableBytes >= 0 && volume.UsedPercentage is >= 0 and <= 100);
			bool hierarchy = model.Locations[0] is { IsExpandableRoot: true, Depth: 0 } && model.Locations.Skip(1).All(location => location.Depth > 0);
			string[] collapsed = model.ToggleSidebarSection("Computer");
			hierarchy &= model.Locations.Count == 1 && !model.Locations[0].IsExpanded;
			model.ToggleSidebarSection("Computer");
			hierarchy &= model.Locations.Any(location => location.IsHeader && location.SectionId == "Favorites") && browser.ComputerFavorites.Any(folder => folder.Path == root);
			await browser.NavigateAsync(root);
			browser.SearchText = "no-volume-matches-this-query";
			await browser.NavigateAsync(ComputerLocation.Path);
			bool navigation = browser.IsComputerPage && browser.SearchText.Length == 0 && browser.LocalVolumes.Concat(browser.NetworkVolumes).Count() == volumes.Length;
			await browser.SearchAsync("no-volume-matches-this-query");
			bool search = !browser.LocalVolumes.Any() && !browser.NetworkVolumes.Any() && !browser.ComputerFavorites.Any();
			await browser.SearchAsync("");
			search &= browser.LocalVolumes.Concat(browser.NetworkVolumes).Count() == volumes.Length;
			browser.ComputerDevicesExpanded = false;
			await browser.RefreshAsync();
			bool groups = !browser.ComputerDevicesExpanded && browser.ComputerDevicesChevron == "›";
			await browser.GoBackAsync();
			navigation &= browser.CurrentPath == root && !browser.IsComputerPage;
			await browser.GoForwardAsync();
			navigation &= browser.IsComputerPage;
			var settingsService = new JsonAppSettingsService(Path.Combine(root, "settings.json"));
			await settingsService.SaveAsync(new AppSettings(Theme: AppThemePreference.Dark, VisualStyle: AppVisualStyle.Windows11, CollapsedSidebarSections: collapsed, Workspace: model.CaptureWorkspaceState()));
			AppSettings settings = await settingsService.LoadAsync();
			restored.ApplySettings(settings);
			await restored.InitializeAsync();
			bool persistence = settings.VisualStyle == AppVisualStyle.Windows11 && settings.Theme == AppThemePreference.Dark && settings.CollapsedSidebarSections!.Contains("Computer") && restored.ActiveBrowser!.IsComputerPage && restored.Locations.Count == 1;
			Console.WriteLine($"FILES_MACOS_COMPUTER initial={initial} real_capacity={capacity} hierarchy={hierarchy} history={navigation} search={search} collapse={groups} persistence={persistence}");
		}
		finally
		{
			foreach (BrowserTabViewModel tab in model.Tabs.Concat(restored.Tabs)) tab.Dispose();
			Directory.Delete(root, recursive: true);
		}
	}
}
