using System.Reflection;
using Microsoft.UI.Xaml.Controls;

namespace Files.App.MacOS.Services;

internal static class ItemsViewSelectionAdapter
{
	private static readonly MethodInfo? GetSelectionModelMethod = typeof(ItemsView).GetMethod(
		"GetSelectionModel",
		BindingFlags.Instance | BindingFlags.NonPublic);

	public static bool TrySelectIndices(ItemsView view, IReadOnlyList<int> selectedIndices)
	{
		if (GetSelectionModelMethod?.Invoke(view, null) is not SelectionModel selectionModel)
		{
			return false;
		}

		selectionModel.ClearSelection();
		if (selectedIndices.Count is 0)
		{
			return true;
		}

		int rangeStart = selectedIndices[0];
		int rangeEnd = rangeStart;
		for (int index = 1; index < selectedIndices.Count; index++)
		{
			int selectedIndex = selectedIndices[index];
			if (selectedIndex == rangeEnd + 1)
			{
				rangeEnd = selectedIndex;
				continue;
			}

			SelectRange(selectionModel, rangeStart, rangeEnd);
			rangeStart = selectedIndex;
			rangeEnd = selectedIndex;
		}
		SelectRange(selectionModel, rangeStart, rangeEnd);
		return true;
	}

	private static void SelectRange(SelectionModel selectionModel, int start, int end)
	{
		selectionModel.SelectRange(IndexPath.CreateFrom(start), IndexPath.CreateFrom(end));
	}
}
