using Files.App.MacOS.ViewModels;
using Files.App.MacOS.Models;
using Files.App.MacOS.Interop;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Files.App.MacOS;

public sealed partial class MainPage
{
	private static MainPage? activeTabDragOwner;
	private static void TraceTabDrag(string message)
	{
		if (Environment.GetEnvironmentVariable("FILES_MACOS_POINTER_DIAGNOSTICS") == "1") Console.WriteLine($"[tab-drag] {message}");
	}
	private BrowserTabViewModel? pointerDragTab;
	private TabViewItem? pointerDragContainer;
	private Pointer? tabDragPointer;
	private DispatcherQueueTimer? tabDragTimer;
	private Point tabDragOrigin;
	private Point tabDragScreenOrigin;
	private WindowPlacementState? tabDragOriginalPlacement;
	private MainPage? floatingTabPage;
	private Transform? originalTabTransform;
	private readonly TranslateTransform tabDragTranslation = new();
	private ScrollViewer? tabDragScroller;
	private double floatingTabOffsetX;
	private bool tabDragStarted;
	private bool isCommittingTabDrag;
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
		if (isCommittingTabDrag || !CanTransferTabs || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || pointerDragTab is not null) return;
		for (DependencyObject? source = e.OriginalSource as DependencyObject; source is not null && source != container; source = VisualTreeHelper.GetParent(source))
			if (source is Button) return;
		activeTabDragOwner?.CancelTabPointerDrag();
		var app = (App)Application.Current;
		tabDragOriginalPlacement = app.GetPagePlacement(this);
		if (tabDragOriginalPlacement is null) return;
		ViewModel.ActiveTab = tab;
		pointerDragTab = tab;
		pointerDragContainer = container;
		originalTabTransform = container.RenderTransform;
		tabDragPointer = e.Pointer;
		tabDragOrigin = e.GetCurrentPoint(RootLayout).Position;
		floatingTabOffsetX = ViewModel.Tabs.Count == 1 ? tabDragOrigin.X : 76 + e.GetCurrentPoint(container).Position.X;
		MacOSNativeMethods.GetPointerPosition(out double x, out double y);
		tabDragScreenOrigin = new(x, y);
		activeTabDragOwner = this;
		TraceTabDrag($"begin tab={tab.Header} screen={tabDragScreenOrigin} local={tabDragOrigin}");
		app.BeginTabTracking(this);
		tabDragTranslation.X = 0;
		tabDragTimer ??= DispatcherQueue.CreateTimer();
		tabDragTimer.Interval = TimeSpan.FromMilliseconds(16);
		tabDragTimer.Tick -= TabDragTimer_Tick;
		tabDragTimer.Tick += TabDragTimer_Tick;
		tabDragTimer.Start();
		Tabs.CapturePointer(e.Pointer);
		e.Handled = true;
	}

	// 捕获仅负责本窗口路由，跨窗口生命周期由原生鼠标状态驱动。
	private void Tabs_PointerMoved(object sender, PointerRoutedEventArgs e) { if (pointerDragTab is not null) e.Handled = true; }
	private void Tabs_PointerReleased(object sender, PointerRoutedEventArgs e) { if (pointerDragTab is not null) e.Handled = true; }
	private void Tabs_PointerCanceled(object sender, PointerRoutedEventArgs e) { if (tabDragTimer?.IsRunning != true) CancelTabPointerDrag(); }

	private async void TabDragTimer_Tick(DispatcherQueueTimer sender, object args)
	{
		if (pointerDragTab is null || tabDragOriginalPlacement is null) return;
		int state = MacOSNativeMethods.GetTabTrackingState(out double x, out double y);
		Point screen = new(x, y);
		if (state < 0) { CancelTabPointerDrag(); return; }
		if (!tabDragStarted)
		{
			if (state == 0) { CancelTabPointerDrag(); return; }
			if (Math.Abs(x - tabDragScreenOrigin.X) < 6 && Math.Abs(y - tabDragScreenOrigin.Y) < 6) return;
			tabDragStarted = true;
			TraceTabDrag($"start screen={screen} window={((App)Application.Current).GetPagePlacement(this)}");
			pointerDragContainer!.RenderTransform = tabDragTranslation;
			pointerDragContainer!.Opacity = 0.55;
			CloseActiveContextMenu();
		}
		try
		{
			UpdateNativeTabDrag(screen);
			if (state == 0) await FinishNativeTabDragAsync(screen);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			CancelTabPointerDrag();
			await ShowErrorAsync(GetResource("MoveTabToNewWindowErrorMessage"));
		}
	}

	private void UpdateNativeTabDrag(Point screen)
	{
		var app = (App)Application.Current;
		var placement = tabDragOriginalPlacement!;
		Point local = new(screen.X - placement.X, placement.Y + placement.Height - screen.Y);
		if (floatingTabPage is null && local.Y >= -12 && local.Y <= TitleBarBackground.ActualHeight + 18 && local.X >= 60 && local.X <= RootLayout.ActualWidth)
		{
			UpdateTabDropTarget(new(local.X, Math.Clamp(local.Y, 0, TitleBarBackground.ActualHeight)));
			tabDragTranslation.X = screen.X - tabDragScreenOrigin.X;
			ScrollTabStripDuringDrag(local.X);
			return;
		}
		if (floatingTabPage is null)
		{
			pointerDragContainer!.RenderTransform = originalTabTransform;
			floatingTabPage = ViewModel.Tabs.Count == 1 ? this : app.CreateTabDragWindow(this, pointerDragTab!,
				placement with { X = screen.X - floatingTabOffsetX, Y = screen.Y - placement.Height + tabDragOrigin.Y, IsMaximized = false });
			if (floatingTabPage is null) return;
			app.FollowTabWindow(floatingTabPage, floatingTabOffsetX, tabDragOrigin.Y);
			TraceTabDrag($"floating created sourceTabs={ViewModel.Tabs.Count} screen={screen}");
		}
		tabDropIndex = -1;
		var candidate = app.FindTabDropTarget(floatingTabPage, out Point targetPoint);
		if (!ReferenceEquals(candidate, this)) TabInsertionMarker.Visibility = Visibility.Collapsed;
		if (externalTabDropPage is not null && !ReferenceEquals(externalTabDropPage, candidate))
			externalTabDropPage.TabInsertionMarker.Visibility = Visibility.Collapsed;
		externalTabDropPage = null;
		if (candidate is null || !candidate.CanTransferTabs) return;
		candidate.UpdateTabDropTarget(targetPoint);
		candidate.ScrollTabStripDuringDrag(targetPoint.X);
		if (candidate.tabDropIndex >= 0) externalTabDropPage = candidate;
	}

	private void ScrollTabStripDuringDrag(double x)
	{
		var scroller = tabDragScroller ??= VisualDescendants(Tabs).OfType<ScrollViewer>().FirstOrDefault(view => VisualDescendants(view).OfType<TabViewItem>().Any());
		if (scroller is null || scroller.ScrollableWidth <= 0) return;
		double step = x < 108 ? -14 : x > RootLayout.ActualWidth - 58 ? 14 : 0;
		if (step != 0) scroller.ChangeView(Math.Clamp(scroller.HorizontalOffset + step, 0, scroller.ScrollableWidth), null, null, true);
	}

	private void UpdateTabDropTarget(Point point)
	{
		tabDropIndex = -1;
		if (point.Y < 0 || point.Y > TitleBarBackground.ActualHeight || point.X < 76 || point.X > RootLayout.ActualWidth)
		{
			TabInsertionMarker.Visibility = Visibility.Collapsed;
			return;
		}
		var containers = VisualDescendants(Tabs).OfType<TabViewItem>()
			.Where(item => item.Tag is BrowserTabViewModel model && ViewModel.Tabs.Contains(model))
			.OrderBy(item => item.TransformToVisual(RootLayout).TransformPoint(default).X).ToArray();
		int slot = 0;
		double markerX = 76;
		foreach (var item in containers)
		{
			if (ReferenceEquals(item.Tag, pointerDragTab)) continue;
			Point origin = item.TransformToVisual(RootLayout).TransformPoint(default);
			if (point.X < origin.X + item.ActualWidth / 2) { markerX = origin.X; break; }
			slot++;
			markerX = origin.X + item.ActualWidth;
		}
		tabDropIndex = Math.Clamp(slot, 0, ViewModel.Tabs.Count - (pointerDragTab is null ? 0 : 1));
		TabInsertionMarker.Margin = new Thickness(Math.Clamp(markerX, 76, Math.Max(76, RootLayout.ActualWidth - 4)), 0, 0, 0);
		TabInsertionMarker.Visibility = Visibility.Visible;
	}

	private async Task FinishNativeTabDragAsync(Point releasePoint)
	{
		var tab = pointerDragTab!;
		var floating = floatingTabPage;
		var targetPage = externalTabDropPage;
		int index = targetPage?.tabDropIndex ?? tabDropIndex;
		double offsetX = floatingTabOffsetX, offsetY = tabDragOrigin.Y;
		TraceTabDrag($"release screen={releasePoint} index={index} destination={(targetPage is null ? "none" : ReferenceEquals(targetPage, this) ? "source" : "other")} floating={floating is not null} window={((App)Application.Current).GetPagePlacement(this)}");
		isCommittingTabDrag = true;
		ResetTabDragState();
		var app = (App)Application.Current;
		bool committed = false;
		try
		{
			if (targetPage is not null && !ReferenceEquals(targetPage, this))
				committed = await app.MoveTabToWindowAsync(this, targetPage, tab, index);
			else if (index >= 0) { ViewModel.MoveTab(tab, index); committed = true; }
			else if (floating is not null && !ReferenceEquals(floating, this))
			{
				committed = await app.CommitTabDragWindowAsync(this, floating, tab);
				if (committed) { app.MoveDragWindow(floating, releasePoint, offsetX, offsetY); floating = null; }
			}
			else
			{
				if (ReferenceEquals(floating, this)) app.MoveDragWindow(this, releasePoint, offsetX, offsetY);
				committed = true;
			}
			if (!committed) await ShowErrorAsync(GetResource("MoveTabToNewWindowErrorMessage"));
		}
		finally
		{
			if (floating is not null && !ReferenceEquals(floating, this)) app.CloseWindow(floating);
			isCommittingTabDrag = false;
			ScheduleSessionSave();
		}
	}

	private void Tabs_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key == VirtualKey.Escape && activeTabDragOwner is { } owner) { owner.CancelTabPointerDrag(); e.Handled = true; }
	}

	private void CancelTabPointerDrag()
	{
		if (pointerDragTab is not null) TraceTabDrag($"cancel started={tabDragStarted} floating={floatingTabPage is not null}");
		var floating = floatingTabPage;
		var placement = tabDragOriginalPlacement;
		ResetTabDragState();
		var app = (App)Application.Current;
		if (floating is not null && !ReferenceEquals(floating, this)) app.CloseWindow(floating);
		else if (ReferenceEquals(floating, this) && placement is not null) app.ApplyWindowPlacement(this, placement);
	}

	private void ResetTabDragState()
	{
		tabDragTimer?.Stop();
		tabDragScroller = null;
		if (ReferenceEquals(activeTabDragOwner, this)) { activeTabDragOwner = null; MacOSNativeMethods.EndTabTracking(); }
		if (externalTabDropPage is not null) externalTabDropPage.TabInsertionMarker.Visibility = Visibility.Collapsed;
		externalTabDropPage = null;
		Pointer? pointer = tabDragPointer;
		tabDragPointer = null;
		pointerDragTab = null;
		if (pointerDragContainer is not null) { pointerDragContainer.Opacity = 1; pointerDragContainer.RenderTransform = originalTabTransform; }
		pointerDragContainer = null;
		floatingTabPage = null;
		tabDragOriginalPlacement = null;
		tabDragStarted = false;
		tabDropIndex = -1;
		TabInsertionMarker.Visibility = Visibility.Collapsed;
		if (pointer is not null) Tabs.ReleasePointerCapture(pointer);
	}
}
