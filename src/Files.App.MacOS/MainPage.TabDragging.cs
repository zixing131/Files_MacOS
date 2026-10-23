using Files.App.MacOS.ViewModels;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Files.App.MacOS;

public sealed partial class MainPage
{
	private BrowserTabViewModel? pointerDragTab;
	private TabViewItem? pointerDragContainer;
	private Pointer? tabDragPointer;
	private Point tabDragOrigin;
	private bool tabDragStarted;
	private int tabDropIndex = -1;
	private MainPage? externalTabDropPage;
	internal bool CanTransferTabs => IsInitialized && fileTransferCancellation is null && !isHistoryOperationRunning && !isConnectingServer;

	private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
	{
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, i);
			yield return child;
			foreach (DependencyObject descendant in VisualDescendants(child)) yield return descendant;
		}
	}

	private void Tabs_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		for (DependencyObject? source = e.OriginalSource as DependencyObject; source is not null && source != Tabs; source = VisualTreeHelper.GetParent(source))
		{
			if (source is Button) return;
			if (source is TabViewItem item) { TabItem_PointerPressed(item, e); return; }
		}
	}

	private void BeginTabPointerDrag(TabViewItem container, BrowserTabViewModel tab, PointerRoutedEventArgs e)
	{
		if (fileTransferCancellation is not null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
		for (DependencyObject? source = e.OriginalSource as DependencyObject; source is not null && source != container; source = VisualTreeHelper.GetParent(source))
		{
			if (source is Button) return;
		}
		CancelTabPointerDrag();
		ViewModel.ActiveTab = tab;
		if (!Tabs.CapturePointer(e.Pointer)) return;
		pointerDragTab = tab;
		pointerDragContainer = container;
		tabDragPointer = e.Pointer;
		tabDragOrigin = e.GetCurrentPoint(RootLayout).Position;
		Tabs.Focus(FocusState.Programmatic);
		e.Handled = true;
	}

	private void Tabs_PointerMoved(object sender, PointerRoutedEventArgs e)
	{
		if (pointerDragTab is null || tabDragPointer?.PointerId != e.Pointer.PointerId) return;
		if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
		{
			CancelTabPointerDrag();
			return;
		}
		Point point = e.GetCurrentPoint(RootLayout).Position;
		if (!tabDragStarted && Math.Abs(point.X - tabDragOrigin.X) < 6 && Math.Abs(point.Y - tabDragOrigin.Y) < 6) return;
		tabDragStarted = true;
		pointerDragContainer!.Opacity = 0.55;
		UpdateDragDestination(point);
		e.Handled = true;
	}

	private void UpdateDragDestination(Point point)
	{
		if (externalTabDropPage is not null) externalTabDropPage.TabInsertionMarker.Visibility = Visibility.Collapsed;
		externalTabDropPage = null;
		UpdateTabDropTarget(point);
		if (tabDropIndex >= 0) return;
		var candidate = ((App)Application.Current).FindTabDropTarget(this, out Point targetPoint);
		if (candidate is null || !candidate.CanTransferTabs) return;
		candidate.UpdateTabDropTarget(targetPoint);
		if (candidate.tabDropIndex >= 0) externalTabDropPage = candidate;
	}

	private void UpdateTabDropTarget(Point point)
	{
		tabDropIndex = -1;
		TabInsertionMarker.Visibility = Visibility.Collapsed;
		// 同窗口重排与跨窗口合并共用落点算法，插入线始终对应真实标签位置。
		if (point.Y < 0 || point.Y > TitleBarBackground.ActualHeight || point.X < 76 || point.X > RootLayout.ActualWidth) return;
		TabViewItem[] containers = VisualDescendants(Tabs).OfType<TabViewItem>()
			.Where(item => item.Tag is BrowserTabViewModel model && ViewModel.Tabs.Contains(model))
			.OrderBy(item => item.TransformToVisual(RootLayout).TransformPoint(default).X).ToArray();
		int slot = 0;
		double markerX = 76;
		foreach (TabViewItem item in containers)
		{
			Point origin = item.TransformToVisual(RootLayout).TransformPoint(default);
			if (ReferenceEquals(item.Tag, pointerDragTab)) continue;
			if (point.X < origin.X + item.ActualWidth / 2)
			{
				markerX = origin.X;
				break;
			}
			slot++;
			markerX = origin.X + item.ActualWidth;
		}
		tabDropIndex = Math.Clamp(slot, 0, ViewModel.Tabs.Count - (pointerDragTab is null ? 0 : 1));
		TabInsertionMarker.Margin = new Thickness(Math.Clamp(markerX, 76, Math.Max(76, RootLayout.ActualWidth - 4)), 0, 0, 0);
		TabInsertionMarker.Visibility = Visibility.Visible;
	}

	private async void Tabs_PointerReleased(object sender, PointerRoutedEventArgs e)
	{
		if (pointerDragTab is not { } tab || tabDragPointer?.PointerId != e.Pointer.PointerId) return;
		Point point = e.GetCurrentPoint(RootLayout).Position;
		bool detach = tabDragStarted && (point.X < 0 || point.X > RootLayout.ActualWidth || point.Y < 0 || point.Y > TitleBarBackground.ActualHeight + 24);
		if (tabDragStarted) UpdateDragDestination(point);
		MainPage? destination = externalTabDropPage;
		int target = tabDragStarted ? destination?.tabDropIndex ?? tabDropIndex : -1;
		CancelTabPointerDrag();
		e.Handled = true;
		try
		{
			var app = (App)Application.Current;
			if (destination is not null && target >= 0)
			{
				if (!await app.MoveTabToWindowAsync(this, destination, tab, target))
					await ShowErrorAsync(GetResource("MoveTabToNewWindowErrorMessage"));
			}
			else if (target >= 0) ViewModel.MoveTab(tab, target);
			else if (detach && CanTransferTabs)
			{
				if (ViewModel.Tabs.Count == 1) app.PositionWindowAtPointer(this);
				else if (!await app.MoveTabToNewWindowAsync(this, tab, positionAtPointer: true))
					await ShowErrorAsync(GetResource("MoveTabToNewWindowErrorMessage"));
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			await ShowErrorAsync(GetResource("MoveTabToNewWindowErrorMessage"));
		}
	}

	private void Tabs_PointerCanceled(object sender, PointerRoutedEventArgs e) => CancelTabPointerDrag();

	private void Tabs_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key == VirtualKey.Escape && pointerDragTab is not null)
		{
			CancelTabPointerDrag();
			e.Handled = true;
		}
	}

	private void CancelTabPointerDrag()
	{
		if (externalTabDropPage is not null) externalTabDropPage.TabInsertionMarker.Visibility = Visibility.Collapsed;
		externalTabDropPage = null;
		Pointer? pointer = tabDragPointer;
		tabDragPointer = null;
		pointerDragTab = null;
		if (pointerDragContainer is not null) pointerDragContainer.Opacity = 1;
		pointerDragContainer = null;
		tabDragStarted = false;
		tabDropIndex = -1;
		TabInsertionMarker.Visibility = Visibility.Collapsed;
		if (pointer is not null) Tabs.ReleasePointerCapture(pointer);
	}
}
