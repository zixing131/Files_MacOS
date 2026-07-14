using CommunityToolkit.Mvvm.ComponentModel;

namespace Files.App.MacOS.ViewModels;

public sealed class BrowserTabViewModel : ObservableObject, IDisposable
{
	private string header;
	private DirectoryBrowserViewModel activeBrowser;
	private DirectoryBrowserViewModel? secondaryBrowser;
	private double splitRatio = 0.5;

	public BrowserTabViewModel(DirectoryBrowserViewModel browser, string defaultHeader)
	{
		Browser = browser;
		activeBrowser = browser;
		header = defaultHeader;
		Browser.PropertyChanged += Browser_PropertyChanged;
	}

	public event EventHandler? StateChanged;

	public DirectoryBrowserViewModel Browser { get; }

	public DirectoryBrowserViewModel? SecondaryBrowser
	{
		get => secondaryBrowser;
		private set
		{
			if (ReferenceEquals(secondaryBrowser, value))
			{
				return;
			}

			if (secondaryBrowser is not null)
			{
				secondaryBrowser.PropertyChanged -= Browser_PropertyChanged;
			}
			if (SetProperty(ref secondaryBrowser, value))
			{
				if (secondaryBrowser is not null)
				{
					secondaryBrowser.PropertyChanged += Browser_PropertyChanged;
				}
				OnPropertyChanged(nameof(IsSplitView));
				StateChanged?.Invoke(this, EventArgs.Empty);
			}
		}
	}

	public DirectoryBrowserViewModel ActiveBrowser
	{
		get => activeBrowser;
		private set
		{
			if (SetProperty(ref activeBrowser, value))
			{
				StateChanged?.Invoke(this, EventArgs.Empty);
			}
		}
	}

	public bool IsSplitView => SecondaryBrowser is not null;

	public double SplitRatio
	{
		get => splitRatio;
		set
		{
			if (SetProperty(ref splitRatio, Math.Clamp(value, 0.2, 0.8)))
			{
				StateChanged?.Invoke(this, EventArgs.Empty);
			}
		}
	}

	public string Header
	{
		get => header;
		private set => SetProperty(ref header, value);
	}

	public void Dispose()
	{
		Browser.PropertyChanged -= Browser_PropertyChanged;
		StateChanged = null;
		Browser.Dispose();
		SecondaryBrowser?.Dispose();
		SecondaryBrowser = null;
	}

	public void EnableSplitView(DirectoryBrowserViewModel browser)
	{
		if (SecondaryBrowser is not null)
		{
			throw new InvalidOperationException("The tab already has a secondary pane.");
		}

		SecondaryBrowser = browser;
		ActiveBrowser = browser;
	}

	public void DisableSplitView()
	{
		DirectoryBrowserViewModel? browser = SecondaryBrowser;
		ActiveBrowser = Browser;
		SecondaryBrowser = null;
		browser?.Dispose();
	}

	public bool ActivateBrowser(DirectoryBrowserViewModel browser)
	{
		if (!ReferenceEquals(browser, Browser) && !ReferenceEquals(browser, SecondaryBrowser))
		{
			return false;
		}

		ActiveBrowser = browser;
		return true;
	}

	private void Browser_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (ReferenceEquals(sender, Browser) && e.PropertyName is nameof(DirectoryBrowserViewModel.CurrentPath))
		{
			string name = Path.GetFileName(Browser.CurrentPath.TrimEnd(Path.DirectorySeparatorChar));
			Header = string.IsNullOrEmpty(name) ? Browser.CurrentPath : name;
		}

		if (e.PropertyName is nameof(DirectoryBrowserViewModel.CurrentPath) or
			nameof(DirectoryBrowserViewModel.IsGridView) or
			nameof(DirectoryBrowserViewModel.SortField) or
			nameof(DirectoryBrowserViewModel.SortDirection))
		{
			StateChanged?.Invoke(this, EventArgs.Empty);
		}
	}
}
