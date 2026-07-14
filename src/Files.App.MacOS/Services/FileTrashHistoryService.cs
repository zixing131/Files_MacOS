namespace Files.App.MacOS.Services;

internal sealed class FileTrashHistoryService(
	IMacOSWorkspaceService workspaceService,
	FileTransferHistoryService transferHistoryService)
{
	public async Task ReplayAsync(FileTrashHistoryEntry history, bool isUndo)
	{
		if (isUndo)
		{
			await RestoreAsync(history.Items);
			return;
		}

		try
		{
			history.Items = await workspaceService.MoveToTrashAsync(
				history.Items.Select(static item => item.OriginalPath).ToArray());
		}
		catch (TrashOperationPartialException ex)
		{
			if (ex.CompletedItems.Count > 0)
			{
				await RestoreAsync(ex.CompletedItems);
			}
			throw;
		}
	}

	private Task RestoreAsync(IReadOnlyList<TrashedItemResult> items)
	{
		return transferHistoryService.MoveMappedItemsAsync(items
			.Select(static item => new FileTransferRootResult(item.TrashPath, item.OriginalPath))
			.ToArray());
	}
}
