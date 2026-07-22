using CommunityToolkit.Mvvm.ComponentModel;

namespace Files.App.MacOS.ViewModels;

public sealed class BrowserTabViewModel : ObservableObject, IDisposable
{
	private readonly Func<string, string> pathDisplayNameResolver;
	private string header;
	private DirectoryBrowserViewModel activeBrowser;
	private DirectoryBrowserViewModel? secondaryBrowser;
	private double splitRatio = 0.5;
	private bool isActive;
	private bool isPointerOver;

	public BrowserTabViewModel(
		DirectoryBrowserViewModel browser,
		string defaultHeader,
		Func<string, string> pathDisplayNameResolver)
	{
		Browser = browser;
		this.pathDisplayNameResolver = pathDisplayNameResolver;
		activeBrowser = browser;
		header = defaultHeader;
		Browser.PropertyChanged += Browser_PropertyChanged;
		Browser.ItemsReplaced += Browser_ItemsReplaced;
	}

	public event EventHandler? StateChanged;

	public event EventHandler<ItemsReplacedEventArgs>? ItemsReplaced;

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
				secondaryBrowser.ItemsReplaced -= Browser_ItemsReplaced;
			}
			if (SetProperty(ref secondaryBrowser, value))
			{
				if (secondaryBrowser is not null)
				{
					secondaryBrowser.PropertyChanged += Browser_PropertyChanged;
					secondaryBrowser.ItemsReplaced += Browser_ItemsReplaced;
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

	public bool IsActive
	{
		get => isActive;
		set
		{
			if (SetProperty(ref isActive, value))
			{
				OnPropertyChanged(nameof(ShowCloseButton));
			}
		}
	}

	public bool IsPointerOver
	{
		get => isPointerOver;
		set
		{
			if (SetProperty(ref isPointerOver, value))
			{
				OnPropertyChanged(nameof(ShowCloseButton));
			}
		}
	}

	public bool ShowCloseButton => IsActive || IsPointerOver;

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
		Browser.ItemsReplaced -= Browser_ItemsReplaced;
		StateChanged = null;
		Browser.Dispose();
		SecondaryBrowser?.Dispose();
		SecondaryBrowser = null;
	}

	private void Browser_ItemsReplaced(object? sender, ItemsReplacedEventArgs e) =>
		ItemsReplaced?.Invoke(sender, e);

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
			Header = pathDisplayNameResolver(Browser.CurrentPath);
		}

		if (e.PropertyName is nameof(DirectoryBrowserViewModel.CurrentPath) or
			nameof(DirectoryBrowserViewModel.IsGridView) or
			nameof(DirectoryBrowserViewModel.IsColumnView) or
			nameof(DirectoryBrowserViewModel.SortField) or
			nameof(DirectoryBrowserViewModel.SortDirection))
		{
			StateChanged?.Invoke(this, EventArgs.Empty);
		}
	}
}
