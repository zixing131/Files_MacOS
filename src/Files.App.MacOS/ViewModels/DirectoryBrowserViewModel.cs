using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Files.App.MacOS.Models;
using Files.App.MacOS.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.Resources;
using Windows.Storage.Streams;

namespace Files.App.MacOS.ViewModels;

public enum FileSortField
{
	Name,
	Modified,
	Size,
}

public enum FileSortDirection
{
	Ascending,
	Descending,
}

public sealed partial class DirectoryBrowserViewModel : ObservableObject, IDisposable
{
	private readonly IDirectoryService directoryService;
	private readonly IFileSearchService searchService;
	private readonly IMacOSWorkspaceService workspaceService;
	private readonly IDirectoryChangeMonitor directoryChangeMonitor;
	private readonly DispatcherQueue dispatcherQueue;
	private readonly Stack<string> backStack = new();
	private readonly Stack<string> forwardStack = new();
	private readonly ResourceLoader resources = ResourceLoader.GetForViewIndependentUse();
	private CancellationTokenSource? navigationCancellation;
	private CancellationTokenSource? searchCancellation;
	private CancellationTokenSource? thumbnailCancellation;
	private bool searchRefreshPending;
	private string itemCountStatus = string.Empty;
	private IReadOnlyList<LocalFileSystemItem> sourceItems = [];

	public DirectoryBrowserViewModel(
		IDirectoryService directoryService,
		IFileSearchService searchService,
		IMacOSWorkspaceService workspaceService,
		IDirectoryChangeMonitor directoryChangeMonitor)
	{
		this.directoryService = directoryService;
		this.searchService = searchService;
		this.workspaceService = workspaceService;
		this.directoryChangeMonitor = directoryChangeMonitor;
		dispatcherQueue = DispatcherQueue.GetForCurrentThread();
		directoryChangeMonitor.Changed += DirectoryChangeMonitor_Changed;
	}

	public ObservableCollection<LocalFileSystemItem> Items { get; private set; } = [];

	[ObservableProperty]
	public partial string CurrentPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

	[ObservableProperty]
	public partial string StatusText { get; set; } = string.Empty;

	[ObservableProperty]
	public partial bool IsLoading { get; set; }

	[ObservableProperty]
	public partial bool IsGridView { get; set; } = true;

	[ObservableProperty]
	public partial bool IsFileOperationRunning { get; set; }

	[ObservableProperty]
	public partial double FileOperationProgress { get; set; }

	[ObservableProperty]
	public partial FileSortField SortField { get; set; } = FileSortField.Name;

	[ObservableProperty]
	public partial FileSortDirection SortDirection { get; set; } = FileSortDirection.Ascending;

	[ObservableProperty]
	public partial string SearchText { get; set; } = string.Empty;

	[ObservableProperty]
	public partial bool ShowHiddenFiles { get; set; }

	public bool CanGoBack => backStack.Count > 0;

	public bool CanGoForward => forwardStack.Count > 0;

	public bool IsEmptyFolder => DirectoryEmptyState.IsEmptyFolder(IsLoading, Items.Count, SearchText);

	public bool HasNoSearchResults => DirectoryEmptyState.HasNoSearchResults(IsLoading, Items.Count, SearchText);

	public Task NavigateHomeAsync()
	{
		return NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
	}

	public async Task NavigateAsync(string path, bool addToHistory = true)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			StatusText = GetResource("PathRequired");
			return;
		}

		string fullPath;
		try
		{
			fullPath = Path.GetFullPath(path.Trim());
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			StatusText = ex.Message;
			return;
		}

		if (!Directory.Exists(fullPath))
		{
			StatusText = string.Format(GetResource("FolderNotFoundFormat"), fullPath);
			return;
		}

		navigationCancellation?.Cancel();
		navigationCancellation?.Dispose();
		searchCancellation?.Cancel();
		searchCancellation?.Dispose();
		searchRefreshPending = false;
		searchCancellation = null;
		var cancellation = new CancellationTokenSource();
		navigationCancellation = cancellation;

		string previousPath = CurrentPath;
		IsLoading = true;
		StatusText = GetResource("LoadingStatus");

		try
		{
			IReadOnlyList<LocalFileSystemItem> items = await directoryService.GetItemsAsync(fullPath, cancellation.Token);

			ReplaceItems(items);
			StartThumbnailLoading(Items);

			if (addToHistory && !string.Equals(previousPath, fullPath, StringComparison.Ordinal))
			{
				backStack.Push(previousPath);
				forwardStack.Clear();
				OnPropertyChanged(nameof(CanGoBack));
				OnPropertyChanged(nameof(CanGoForward));
			}

			CurrentPath = fullPath;
			directoryChangeMonitor.Watch(fullPath);
			SearchText = string.Empty;
			itemCountStatus = string.Format(GetResource("ItemCountFormat"), Items.Count);
			StatusText = itemCountStatus;
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			StatusText = ex.Message;
		}
		finally
		{
			if (ReferenceEquals(navigationCancellation, cancellation))
			{
				IsLoading = false;
			}
		}
	}

	public async Task GoBackAsync()
	{
		if (backStack.TryPop(out string? path))
		{
			forwardStack.Push(CurrentPath);
			OnPropertyChanged(nameof(CanGoBack));
			OnPropertyChanged(nameof(CanGoForward));
			await NavigateAsync(path, addToHistory: false);
		}
	}

	public async Task GoForwardAsync()
	{
		if (forwardStack.TryPop(out string? path))
		{
			backStack.Push(CurrentPath);
			OnPropertyChanged(nameof(CanGoBack));
			OnPropertyChanged(nameof(CanGoForward));
			await NavigateAsync(path, addToHistory: false);
		}
	}

	public Task GoUpAsync()
	{
		var parent = Directory.GetParent(CurrentPath);
		return parent is null ? Task.CompletedTask : NavigateAsync(parent.FullName);
	}

	public Task RefreshAsync()
	{
		return NavigateAsync(CurrentPath, addToHistory: false);
	}

	public Task ReloadAsync()
	{
		return string.IsNullOrWhiteSpace(SearchText) ? RefreshAsync() : SearchAsync(SearchText);
	}

	public void UpdateSelection(IReadOnlyCollection<LocalFileSystemItem> selectedItems)
	{
		if (selectedItems.Count is 0)
		{
			StatusText = itemCountStatus;
			return;
		}

		long size = selectedItems.Where(static item => item.Size.HasValue).Sum(static item => item.Size!.Value);
		StatusText = size is 0
			? string.Format(GetResource("SelectedItemCountFormat"), selectedItems.Count)
			: string.Format(GetResource("SelectedItemCountAndSizeFormat"), selectedItems.Count, LocalFileSystemItem.FormatSize(size));
	}

	public async Task SearchAsync(string query)
	{
		searchCancellation?.Cancel();
		searchCancellation?.Dispose();
		searchRefreshPending = false;

		if (string.IsNullOrWhiteSpace(query))
		{
			searchCancellation = null;
			await RefreshAsync();
			return;
		}

		var cancellation = new CancellationTokenSource();
		searchCancellation = cancellation;
		string rootPath = CurrentPath;
		var incrementalItems = new Dictionary<string, LocalFileSystemItem>(StringComparer.Ordinal);
		bool searchCompleted = false;
		var progress = new Progress<IReadOnlyList<LocalFileSystemItem>>(batch =>
		{
			if (searchCompleted ||
				!ReferenceEquals(searchCancellation, cancellation) ||
				!string.Equals(rootPath, CurrentPath, StringComparison.Ordinal))
			{
				return;
			}

			foreach (LocalFileSystemItem item in batch)
			{
				item.SearchLocation = Path.GetDirectoryName(item.Path) ?? rootPath;
				incrementalItems[item.Path] = item;
			}
			ReplaceItems(incrementalItems.Values);
			StatusText = string.Format(GetResource("SearchingStatusWithCountFormat"), Items.Count);
		});
		IsLoading = true;
		StatusText = GetResource("SearchingStatus");
		ReplaceItems([]);
		directoryChangeMonitor.Watch(rootPath, includeSubdirectories: true);

		try
		{
			FileSearchQuery searchQuery = FileSearchQuery.Parse(query.Trim());
			IReadOnlyList<LocalFileSystemItem> results = await searchService.SearchAsync(rootPath, searchQuery, ShowHiddenFiles, progress, cancellation.Token);
			searchCompleted = true;
			if (!string.Equals(rootPath, CurrentPath, StringComparison.Ordinal))
			{
				return;
			}

			foreach (LocalFileSystemItem item in results)
			{
				item.SearchLocation = Path.GetDirectoryName(item.Path) ?? rootPath;
			}

			ReplaceItems(results);
			StartThumbnailLoading(Items);
			itemCountStatus = string.Format(GetResource("SearchResultCountFormat"), Items.Count);
			StatusText = itemCountStatus;
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			StatusText = ex.Message;
		}
		finally
		{
			searchCompleted = true;
			if (ReferenceEquals(searchCancellation, cancellation))
			{
				IsLoading = false;
			}
		}

		if (searchRefreshPending &&
			ReferenceEquals(searchCancellation, cancellation) &&
			!string.IsNullOrWhiteSpace(SearchText))
		{
			searchRefreshPending = false;
			dispatcherQueue.TryEnqueue(async () => await SearchAsync(SearchText));
		}
	}

	public void SetSort(FileSortField field, FileSortDirection direction)
	{
		SortField = field;
		SortDirection = direction;
		ApplyView();
	}

	private void ReplaceItems(IEnumerable<LocalFileSystemItem> items)
	{
		sourceItems = items.ToArray();
		ApplyView();
		UpdateEmptyState();
	}

	private void ApplyView()
	{
		IEnumerable<LocalFileSystemItem> visibleItems = ShowHiddenFiles
			? sourceItems
			: sourceItems.Where(static item => !item.IsHidden);
		IOrderedEnumerable<LocalFileSystemItem> orderedItems = visibleItems.OrderByDescending(static item => item.IsDirectory);
		orderedItems = (SortField, SortDirection) switch
		{
			(FileSortField.Modified, FileSortDirection.Ascending) => orderedItems.ThenBy(static item => item.Modified),
			(FileSortField.Modified, FileSortDirection.Descending) => orderedItems.ThenByDescending(static item => item.Modified),
			(FileSortField.Size, FileSortDirection.Ascending) => orderedItems.ThenBy(static item => item.Size ?? 0),
			(FileSortField.Size, FileSortDirection.Descending) => orderedItems.ThenByDescending(static item => item.Size ?? 0),
			(FileSortField.Name, FileSortDirection.Descending) => orderedItems.ThenByDescending(static item => item.Name, StringComparer.CurrentCultureIgnoreCase),
			_ => orderedItems.ThenBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase),
		};

		Items = new ObservableCollection<LocalFileSystemItem>(orderedItems);
		OnPropertyChanged(nameof(Items));
	}

	partial void OnShowHiddenFilesChanged(bool value)
	{
		ApplyView();
		UpdateEmptyState();
		itemCountStatus = string.Format(GetResource("ItemCountFormat"), Items.Count);
		StatusText = itemCountStatus;
	}

	partial void OnIsLoadingChanged(bool value)
	{
		UpdateEmptyState();
	}

	partial void OnSearchTextChanged(string value)
	{
		UpdateEmptyState();
	}

	private void UpdateEmptyState()
	{
		OnPropertyChanged(nameof(IsEmptyFolder));
		OnPropertyChanged(nameof(HasNoSearchResults));
	}

	public void Dispose()
	{
		navigationCancellation?.Cancel();
		navigationCancellation?.Dispose();
		searchCancellation?.Cancel();
		searchCancellation?.Dispose();
		thumbnailCancellation?.Cancel();
		thumbnailCancellation?.Dispose();
		directoryChangeMonitor.Changed -= DirectoryChangeMonitor_Changed;
		directoryChangeMonitor.Dispose();
	}

	private void DirectoryChangeMonitor_Changed(object? sender, EventArgs e)
	{
		dispatcherQueue.TryEnqueue(async () =>
		{
			if (IsFileOperationRunning)
			{
				return;
			}
			if (IsLoading)
			{
				searchRefreshPending = searchCancellation is not null && !string.IsNullOrWhiteSpace(SearchText);
				return;
			}

			if (string.IsNullOrWhiteSpace(SearchText))
			{
				await RefreshAsync();
			}
			else
			{
				await SearchAsync(SearchText);
			}
		});
	}

	private void StartThumbnailLoading(IReadOnlyList<LocalFileSystemItem> items)
	{
		thumbnailCancellation?.Cancel();
		thumbnailCancellation?.Dispose();
		var cancellation = new CancellationTokenSource();
		thumbnailCancellation = cancellation;
		_ = LoadThumbnailsAsync(items.Take(120).ToArray(), cancellation);
	}

	private async Task LoadThumbnailsAsync(IReadOnlyList<LocalFileSystemItem> items, CancellationTokenSource cancellation)
	{
		using var concurrency = new SemaphoreSlim(4);
		try
		{
			await Task.WhenAll(items.Select(item => LoadThumbnailAsync(item, concurrency, cancellation.Token)));
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			if (ReferenceEquals(thumbnailCancellation, cancellation))
			{
				thumbnailCancellation = null;
				cancellation.Dispose();
			}
		}
	}

	private async Task LoadThumbnailAsync(
		LocalFileSystemItem item,
		SemaphoreSlim concurrency,
		CancellationToken cancellationToken)
	{
		await concurrency.WaitAsync(cancellationToken);
		try
		{
			byte[]? png = await workspaceService.GetThumbnailPngAsync(item.Path, 128, 128, 1, cancellationToken);
			if (png is null)
			{
				return;
			}

			using var stream = new InMemoryRandomAccessStream();
			using (var writer = new DataWriter(stream))
			{
				writer.WriteBytes(png);
				await writer.StoreAsync();
				await writer.FlushAsync();
			}
			stream.Seek(0);
			var bitmap = new BitmapImage();
			await bitmap.SetSourceAsync(stream);
			cancellationToken.ThrowIfCancellationRequested();
			item.Thumbnail = bitmap;
		}
		finally
		{
			concurrency.Release();
		}
	}

	private string GetResource(string name)
	{
		return resources.GetString(name) ?? name;
	}
}
