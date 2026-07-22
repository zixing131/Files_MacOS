using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Files.App.MacOS.Models;
using Files.App.MacOS.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.Resources;
using Windows.Storage.Streams;

namespace Files.App.MacOS.ViewModels;

public enum FileSortField
{
	Name,
	Modified,
	Created,
	LastOpened,
	Added,
	Size,
	Kind,
	Version,
	Comments,
	Tags,
}

public enum FileSortDirection
{
	Ascending,
	Descending,
}

public sealed class ItemsReplacedEventArgs(IReadOnlyList<string> selectedPaths) : EventArgs
{
	public IReadOnlyList<string> SelectedPaths { get; } = selectedPaths;
}

public sealed partial class DirectoryBrowserViewModel : ObservableObject, IDisposable
{
	private const int MaximumThumbnailCacheEntries = 256;
	private readonly IDirectoryService directoryService;
	private readonly IFileSearchService searchService;
	private readonly IMacOSWorkspaceService workspaceService;
	private readonly IDirectoryChangeMonitor directoryChangeMonitor;
	private readonly DispatcherQueue dispatcherQueue;
	private readonly Stack<string> backStack = new();
	private readonly Stack<string> forwardStack = new();
	private readonly ResourceLoader resources = ResourceLoader.GetForViewIndependentUse();
	private readonly object thumbnailCacheLock = new();
	private readonly Dictionary<ThumbnailCacheKey, ImageSource> thumbnailCache = [];
	private readonly Queue<ThumbnailCacheKey> thumbnailCacheOrder = [];
	private readonly HashSet<LocalFileSystemItem> pendingThumbnailLoads = [];
	private readonly SemaphoreSlim thumbnailConcurrency = new(4);
	private CancellationTokenSource? navigationCancellation;
	private CancellationTokenSource? searchCancellation;
	private CancellationTokenSource? thumbnailCancellation;
	private CancellationTokenSource? metadataCancellation;
	private bool searchRefreshPending;
	private volatile bool isDisposed;
	private string itemCountStatus = string.Empty;
	private IReadOnlyList<LocalFileSystemItem> sourceItems = [];
	private string[] rememberedSelectedPaths = [];
	private readonly record struct ThumbnailCacheKey(string Path, DateTimeOffset Modified, long? Size);

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

	public event EventHandler<ItemsReplacedEventArgs>? ItemsReplaced;

	[ObservableProperty]
	public partial string CurrentPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

	[ObservableProperty]
	public partial string StatusText { get; set; } = string.Empty;

	[ObservableProperty]
	public partial bool IsLoading { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowGridView))]
	[NotifyPropertyChangedFor(nameof(ShowDetailsView))]
	[NotifyPropertyChangedFor(nameof(ShowColumnView))]
	public partial bool IsGridView { get; set; } = true;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowGridView))]
	[NotifyPropertyChangedFor(nameof(ShowDetailsView))]
	[NotifyPropertyChangedFor(nameof(ShowColumnView))]
	public partial bool IsColumnView { get; set; }

	public bool ShowGridView => IsGridView && !IsColumnView;

	public bool ShowDetailsView => !IsGridView && !IsColumnView;

	public bool ShowColumnView => IsColumnView;

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

	// Stack.ToArray() returns entries from the top, so the most recent destination comes first.
	public IReadOnlyList<string> BackHistory => backStack.ToArray();

	public IReadOnlyList<string> ForwardHistory => forwardStack.ToArray();

	public bool IsEmptyFolder => DirectoryEmptyState.IsEmptyFolder(IsLoading, Items.Count, SearchText);

	public bool HasNoSearchResults => DirectoryEmptyState.HasNoSearchResults(IsLoading, Items.Count, SearchText);

	public Task NavigateHomeAsync()
	{
		return NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
	}

	public async Task NavigateAsync(string path, bool addToHistory = true)
	{
		if (isDisposed)
		{
			return;
		}

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

		if (!Directory.Exists(fullPath) && !IsUserTrashPath(fullPath))
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
		metadataCancellation?.Cancel();
		metadataCancellation?.Dispose();
		metadataCancellation = null;
		var cancellation = new CancellationTokenSource();
		navigationCancellation = cancellation;

		string previousPath = CurrentPath;
		IsLoading = true;
		StatusText = GetResource("LoadingStatus");

		try
		{
			IReadOnlyList<LocalFileSystemItem> items = await directoryService.GetItemsAsync(fullPath, cancellation.Token);
			if (isDisposed || cancellation.IsCancellationRequested)
			{
				return;
			}

			StartThumbnailLoading();
			ReplaceItems(items);
			StartMetadataEnrichment(Items);

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

	private static bool IsUserTrashPath(string path)
	{
		string trashPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
		return string.Equals(
			Path.TrimEndingDirectorySeparator(path),
			Path.TrimEndingDirectorySeparator(Path.GetFullPath(trashPath)),
			StringComparison.Ordinal);
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

	public Task GoBackToAsync(string path)
	{
		return JumpInHistoryAsync(backStack, forwardStack, path);
	}

	public Task GoForwardToAsync(string path)
	{
		return JumpInHistoryAsync(forwardStack, backStack, path);
	}

	// Jumps directly to a history entry: the current path and every entry passed over
	// move to the opposite stack so a single navigation reaches the target.
	private async Task JumpInHistoryAsync(Stack<string> source, Stack<string> destination, string path)
	{
		var skipped = new List<string>();
		while (source.Count > 0 && !string.Equals(source.Peek(), path, StringComparison.Ordinal))
		{
			skipped.Add(source.Pop());
		}
		if (source.Count == 0)
		{
			for (int index = skipped.Count - 1; index >= 0; index--)
			{
				source.Push(skipped[index]);
			}
			return;
		}

		source.Pop();
		destination.Push(CurrentPath);
		foreach (string entry in skipped)
		{
			destination.Push(entry);
		}
		OnPropertyChanged(nameof(CanGoBack));
		OnPropertyChanged(nameof(CanGoForward));
		await NavigateAsync(path, addToHistory: false);
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

	// Raw directory listing for auxiliary UI (column view ancestor columns); does not navigate.
	public Task<IReadOnlyList<LocalFileSystemItem>> GetItemsAsync(string path, CancellationToken cancellationToken) =>
		directoryService.GetItemsAsync(path, cancellationToken);

	public Task ReloadAsync()
	{
		return string.IsNullOrWhiteSpace(SearchText) ? RefreshAsync() : SearchAsync(SearchText);
	}

	public void UpdateSelection(IReadOnlyCollection<LocalFileSystemItem> selectedItems)
	{
		rememberedSelectedPaths = selectedItems.Select(static item => item.Path).ToArray();
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
		if (isDisposed)
		{
			return;
		}

		searchCancellation?.Cancel();
		searchCancellation?.Dispose();
		searchRefreshPending = false;
		metadataCancellation?.Cancel();
		metadataCancellation?.Dispose();
		metadataCancellation = null;

		if (string.IsNullOrWhiteSpace(query))
		{
			searchCancellation = null;
			await RefreshAsync();
			return;
		}

		var cancellation = new CancellationTokenSource();
		searchCancellation = cancellation;
		StartThumbnailLoading();
		string rootPath = CurrentPath;
		var incrementalItems = new Dictionary<string, LocalFileSystemItem>(StringComparer.Ordinal);
		bool searchCompleted = false;
		var progress = new Progress<IReadOnlyList<LocalFileSystemItem>>(batch =>
		{
			if (isDisposed || searchCompleted ||
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
			if (isDisposed || cancellation.IsCancellationRequested || !string.Equals(rootPath, CurrentPath, StringComparison.Ordinal))
			{
				return;
			}

			foreach (LocalFileSystemItem item in results)
			{
				item.SearchLocation = Path.GetDirectoryName(item.Path) ?? rootPath;
			}

			ReplaceItems(results);
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

		if (!isDisposed && searchRefreshPending &&
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
		LocalFileSystemItem[] replacementItems = items.ToArray();
		ReuseExistingThumbnails(sourceItems, replacementItems);
		RestoreCachedThumbnails(replacementItems);
		sourceItems = replacementItems;
		PrepareAccessibilityNames(sourceItems);
		ApplyView();
		UpdateEmptyState();
	}

	internal void PrepareAccessibilityNames(IReadOnlyList<LocalFileSystemItem> items)
	{
		string separator = GetResource("ItemAutomationSeparator");
		string folderType = GetResource("FolderItemAutomationType");
		string fileType = GetResource("FileItemAutomationType");
		string packageType = GetResource("PackageItemAutomationType");
		string hiddenState = GetResource("HiddenItemAutomationState");
		string sizeFormat = GetResource("ItemSizeAutomationFormat");
		string modifiedFormat = GetResource("ItemModifiedAutomationFormat");
		string locationFormat = GetResource("ItemLocationAutomationFormat");
		var builder = new StringBuilder(160);
		foreach (LocalFileSystemItem item in items)
		{
			builder.Clear();
			string itemType = item.IsPackage ? packageType : item.IsDirectory ? folderType : fileType;
			builder.Append(item.Name).Append(separator).Append(itemType);
			if (item.IsHidden)
			{
				builder.Append(separator).Append(hiddenState);
			}
			if (!string.IsNullOrWhiteSpace(item.SizeText))
			{
				builder.Append(separator).AppendFormat(sizeFormat, item.SizeText);
			}
			builder.Append(separator).AppendFormat(modifiedFormat, item.ModifiedText);
			if (!string.IsNullOrWhiteSpace(item.SearchLocation))
			{
				builder.Append(separator).AppendFormat(locationFormat, item.SearchLocation);
			}
			item.AccessibilityName = builder.ToString();
		}
	}

	private void ApplyView()
	{
		IEnumerable<LocalFileSystemItem> visibleItems = ShowHiddenFiles
			? sourceItems
			: sourceItems.Where(static item => !item.IsHidden);
		IOrderedEnumerable<LocalFileSystemItem> orderedItems = visibleItems.OrderByDescending(static item => item.IsNavigableDirectory);
		orderedItems = (SortField, SortDirection) switch
		{
			(FileSortField.Modified, FileSortDirection.Ascending) => orderedItems.ThenBy(static item => item.Modified),
			(FileSortField.Modified, FileSortDirection.Descending) => orderedItems.ThenByDescending(static item => item.Modified),
			(FileSortField.Created, FileSortDirection.Ascending) => orderedItems.ThenBy(static item => item.Created),
			(FileSortField.Created, FileSortDirection.Descending) => orderedItems.ThenByDescending(static item => item.Created),
			(FileSortField.LastOpened, FileSortDirection.Ascending) => orderedItems.ThenBy(static item => item.LastOpened),
			(FileSortField.LastOpened, FileSortDirection.Descending) => orderedItems.ThenByDescending(static item => item.LastOpened),
			(FileSortField.Added, FileSortDirection.Ascending) => orderedItems.ThenBy(static item => item.Added),
			(FileSortField.Added, FileSortDirection.Descending) => orderedItems.ThenByDescending(static item => item.Added),
			(FileSortField.Size, FileSortDirection.Ascending) => orderedItems.ThenBy(static item => item.Size ?? 0),
			(FileSortField.Size, FileSortDirection.Descending) => orderedItems.ThenByDescending(static item => item.Size ?? 0),
			(FileSortField.Kind, FileSortDirection.Ascending) => orderedItems.ThenBy(static item => item.Kind, StringComparer.CurrentCultureIgnoreCase),
			(FileSortField.Kind, FileSortDirection.Descending) => orderedItems.ThenByDescending(static item => item.Kind, StringComparer.CurrentCultureIgnoreCase),
			(FileSortField.Version, FileSortDirection.Ascending) => orderedItems.ThenBy(static item => item.Version, StringComparer.CurrentCultureIgnoreCase),
			(FileSortField.Version, FileSortDirection.Descending) => orderedItems.ThenByDescending(static item => item.Version, StringComparer.CurrentCultureIgnoreCase),
			(FileSortField.Comments, FileSortDirection.Ascending) => orderedItems.ThenBy(static item => item.Comments, StringComparer.CurrentCultureIgnoreCase),
			(FileSortField.Comments, FileSortDirection.Descending) => orderedItems.ThenByDescending(static item => item.Comments, StringComparer.CurrentCultureIgnoreCase),
			(FileSortField.Tags, FileSortDirection.Ascending) => orderedItems.ThenBy(static item => item.TagsText, StringComparer.CurrentCultureIgnoreCase),
			(FileSortField.Tags, FileSortDirection.Descending) => orderedItems.ThenByDescending(static item => item.TagsText, StringComparer.CurrentCultureIgnoreCase),
			(FileSortField.Name, FileSortDirection.Descending) => orderedItems.ThenByDescending(static item => item.Name, StringComparer.CurrentCultureIgnoreCase),
			_ => orderedItems.ThenBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase),
		};

		string[] pathsToRestore = rememberedSelectedPaths;
		Items = new ObservableCollection<LocalFileSystemItem>(orderedItems);
		OnPropertyChanged(nameof(Items));
		if (pathsToRestore.Length > 0)
		{
			ItemsReplaced?.Invoke(this, new ItemsReplacedEventArgs(pathsToRestore));
		}
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
		if (isDisposed)
		{
			return;
		}

		isDisposed = true;
		directoryChangeMonitor.Changed -= DirectoryChangeMonitor_Changed;
		navigationCancellation?.Cancel();
		navigationCancellation?.Dispose();
		navigationCancellation = null;
		searchCancellation?.Cancel();
		searchCancellation?.Dispose();
		searchCancellation = null;
		thumbnailCancellation?.Cancel();
		thumbnailCancellation?.Dispose();
		thumbnailCancellation = null;
		metadataCancellation?.Cancel();
		metadataCancellation?.Dispose();
		metadataCancellation = null;
		lock (thumbnailCacheLock)
		{
			pendingThumbnailLoads.Clear();
			thumbnailCache.Clear();
			thumbnailCacheOrder.Clear();
		}
		directoryChangeMonitor.Dispose();
	}

	private void DirectoryChangeMonitor_Changed(object? sender, EventArgs e)
	{
		if (isDisposed)
		{
			return;
		}

		dispatcherQueue.TryEnqueue(async () =>
		{
			if (isDisposed || IsFileOperationRunning)
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

	private void StartThumbnailLoading()
	{
		if (isDisposed)
		{
			return;
		}

		thumbnailCancellation?.Cancel();
		thumbnailCancellation?.Dispose();
		var cancellation = new CancellationTokenSource();
		thumbnailCancellation = cancellation;
	}

	internal void EnsureThumbnailLoaded(LocalFileSystemItem item)
	{
		CancellationTokenSource? cancellation = thumbnailCancellation;
		if (isDisposed || item.Thumbnail is not null || cancellation is null || cancellation.IsCancellationRequested)
		{
			return;
		}

		lock (thumbnailCacheLock)
		{
			if (item.Thumbnail is not null || !pendingThumbnailLoads.Add(item))
			{
				return;
			}
		}

		_ = LoadThumbnailOnDemandAsync(item, cancellation.Token);
	}

	private void StartMetadataEnrichment(IReadOnlyList<LocalFileSystemItem> items)
	{
		if (isDisposed || items.Count is 0)
		{
			return;
		}

		metadataCancellation?.Cancel();
		metadataCancellation?.Dispose();
		var cancellation = new CancellationTokenSource();
		metadataCancellation = cancellation;
		_ = EnrichMetadataAsync(items, cancellation);
	}

	private async Task EnrichMetadataAsync(IReadOnlyList<LocalFileSystemItem> items, CancellationTokenSource cancellation)
	{
		try
		{
			const int batchSize = 96;
			for (int offset = 0; offset < items.Count && !cancellation.IsCancellationRequested; offset += batchSize)
			{
				LocalFileSystemItem[] batch = items.Skip(offset).Take(batchSize).ToArray();
				using var concurrency = new SemaphoreSlim(8);
				var results = await Task.WhenAll(batch.Select(async item =>
				{
					await concurrency.WaitAsync(cancellation.Token);
					try
					{
						MacOSFinderTagService.SortMetadata metadata = await Task.Run(
							() => MacOSFinderTagService.GetSortMetadata(item.Path),
							cancellation.Token);
						return (item, metadata);
					}
					finally
					{
						concurrency.Release();
					}
				}));
				if (cancellation.IsCancellationRequested || isDisposed)
				{
					return;
				}

				dispatcherQueue.TryEnqueue(() =>
				{
					if (cancellation.IsCancellationRequested || isDisposed)
					{
						return;
					}
					foreach ((LocalFileSystemItem item, MacOSFinderTagService.SortMetadata metadata) in results)
					{
						item.ApplySortMetadata(
							metadata.LastOpened,
							metadata.Added,
							metadata.Kind,
							metadata.Version,
							metadata.Comments,
							metadata.Tags);
					}
				});
			}

			if (!cancellation.IsCancellationRequested && !isDisposed && SortField is
				FileSortField.LastOpened or FileSortField.Added or FileSortField.Kind or
				FileSortField.Version or FileSortField.Comments or FileSortField.Tags)
			{
				dispatcherQueue.TryEnqueue(() =>
				{
					if (!cancellation.IsCancellationRequested && !isDisposed)
					{
						ApplyView();
					}
				});
			}
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			if (ReferenceEquals(metadataCancellation, cancellation))
			{
				metadataCancellation = null;
				cancellation.Dispose();
			}
		}
	}

	private async Task LoadThumbnailOnDemandAsync(LocalFileSystemItem item, CancellationToken cancellationToken)
	{
		try
		{
			await LoadThumbnailAsync(item, thumbnailConcurrency, cancellationToken);
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			lock (thumbnailCacheLock)
			{
				pendingThumbnailLoads.Remove(item);
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
			byte[]? png = await workspaceService.GetThumbnailPngAsync(item.Path, 128, 128, 2, cancellationToken);
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
			CacheThumbnail(item, bitmap);
			item.Thumbnail = bitmap;
		}
		finally
		{
			concurrency.Release();
		}
	}

	internal static int ReuseExistingThumbnails(
		IReadOnlyList<LocalFileSystemItem> existingItems,
		IReadOnlyList<LocalFileSystemItem> replacementItems)
	{
		var existingThumbnails = new Dictionary<string, ImageSource>(StringComparer.Ordinal);
		foreach (LocalFileSystemItem item in existingItems)
		{
			if (item.Thumbnail is ImageSource thumbnail)
			{
				existingThumbnails.TryAdd(item.Path, thumbnail);
			}
		}
		int reusedCount = 0;
		foreach (LocalFileSystemItem item in replacementItems)
		{
			if (existingThumbnails.TryGetValue(item.Path, out ImageSource? thumbnail))
			{
				item.Thumbnail = thumbnail;
				reusedCount++;
			}
		}

		return reusedCount;
	}

	private void RestoreCachedThumbnails(IReadOnlyList<LocalFileSystemItem> items)
	{
		lock (thumbnailCacheLock)
		{
			foreach (LocalFileSystemItem item in items)
			{
				if (item.Thumbnail is null && thumbnailCache.TryGetValue(CreateThumbnailCacheKey(item), out ImageSource? thumbnail))
				{
					item.Thumbnail = thumbnail;
				}
			}
		}
	}

	private void CacheThumbnail(LocalFileSystemItem item, ImageSource thumbnail)
	{
		ThumbnailCacheKey key = CreateThumbnailCacheKey(item);
		lock (thumbnailCacheLock)
		{
			if (thumbnailCache.TryAdd(key, thumbnail))
			{
				thumbnailCacheOrder.Enqueue(key);
			}
			else
			{
				thumbnailCache[key] = thumbnail;
			}

			while (thumbnailCache.Count > MaximumThumbnailCacheEntries && thumbnailCacheOrder.TryDequeue(out ThumbnailCacheKey oldestKey))
			{
				thumbnailCache.Remove(oldestKey);
			}
		}
	}

	private static ThumbnailCacheKey CreateThumbnailCacheKey(LocalFileSystemItem item) =>
		new(item.Path, item.Modified, item.Size);

	private string GetResource(string name)
	{
		return resources.GetString(name) ?? name;
	}
}
