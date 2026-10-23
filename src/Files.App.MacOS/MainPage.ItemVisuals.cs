using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;

namespace Files.App.MacOS;

public sealed partial class MainPage
{
	private sealed class GridVisualRegistration { public long SelectionToken; }
	private readonly ConditionalWeakTable<ItemContainer, GridVisualRegistration> gridVisualRegistrations = new();
	private ItemContainer? hoveredGridContainer;
	private void FileGridContainer_Loaded(object sender, RoutedEventArgs e)
	{
		if (sender is not ItemContainer container) return;
		if (!gridVisualRegistrations.TryGetValue(container, out _))
			gridVisualRegistrations.Add(container, new() { SelectionToken = container.RegisterPropertyChangedCallback(ItemContainer.IsSelectedProperty, (obj, _) => UpdateGridContainerVisual((ItemContainer)obj)) });
		ResetGridContainerHover(container);
	}
	private void FileGridContainer_Unloaded(object sender, RoutedEventArgs e)
	{
		if (sender is not ItemContainer container) return;
		ResetGridContainerHover(container);
		if (gridVisualRegistrations.TryGetValue(container, out var registration))
		{
			container.UnregisterPropertyChangedCallback(ItemContainer.IsSelectedProperty, registration.SelectionToken);
			gridVisualRegistrations.Remove(container);
		}
	}
	private void FileGridContainer_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs e)
	{
		if (sender is ItemContainer container) ResetGridContainerHover(container);
	}
	private void ResetGridContainerHover(ItemContainer container)
	{
		if (ReferenceEquals(hoveredGridContainer, container)) hoveredGridContainer = null;
		UpdateGridContainerVisual(container);
	}
	private void FileGridContainer_PointerEntered(object sender, PointerRoutedEventArgs e)
	{
		if (sender is not ItemContainer container || ReferenceEquals(hoveredGridContainer, container)) return;
		var previous = hoveredGridContainer;
		hoveredGridContainer = container;
		if (previous is not null) UpdateGridContainerVisual(previous);
		UpdateGridContainerVisual(container);
	}
	private void FileGridContainer_PointerExited(object sender, PointerRoutedEventArgs e)
	{
		if (sender is ItemContainer container) ResetGridContainerHover(container);
	}
	private void UpdateGridContainerVisual(ItemContainer container)
	{
		// 选择状态仍由 ItemsView 管理；仅集中处理可回收容器的绘制状态。
		container.Background = container.IsSelected || ReferenceEquals(container, hoveredGridContainer)
			? (Brush)Application.Current.Resources[container.IsSelected ? "FilesAccentSubtleBrush" : "FilesSubtleBrush"]
			: new SolidColorBrush(Microsoft.UI.Colors.Transparent);
		container.BorderBrush = container.IsSelected ? (Brush)Application.Current.Resources["FilesAccentBrush"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
	}
}
