using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Files.App.MacOS.Interop;

namespace Files.App.MacOS.Services;

internal enum MacOSMenuCommand
{
	Settings = 1,
	NewTab,
	NewFolder,
	CloseTab,
	Properties,
	MoveToTrash,
	Rename,
	Undo,
	Redo,
	Cut,
	Copy,
	Paste,
	SelectAll,
	CopyPath,
	Search,
	EditAddress,
	GridView,
	DetailsView,
	TogglePreview,
	ToggleSidebar,
	Back,
	Forward,
	Up,
	Home,
	OpenFolder,
	ConnectServer,
	OpenTerminal,
	DeletePermanently,
	OpenWith,
	OpenInNewTab,
	Duplicate,
	CreateSymbolicLink,
	NewWindow,
	CloseWindow,
	ReopenClosedTab,
	CloseOtherTabs,
	DuplicateTab,
	CloseTabsToLeft,
	CloseTabsToRight,
	MoveTabToNewWindow,
	NextTab,
	PreviousTab,
	ColumnView,
}

internal interface IMacOSMenuCommandTarget
{
	void ExecuteMenuCommand(MacOSMenuCommand command);

	bool CanExecuteMenuCommand(MacOSMenuCommand command);
}

internal sealed unsafe class MacOSMainMenuService : IDisposable
{
	private GCHandle contextHandle;
	private IMacOSMenuCommandTarget? target;
	private Microsoft.UI.Dispatching.DispatcherQueue? dispatcherQueue;
	private readonly int[] commandStates = Enumerable.Repeat(1, Enum.GetValues<MacOSMenuCommand>().Max(static command => (int)command) + 1).ToArray();

	public void Install(
		IMacOSMenuCommandTarget commandTarget,
		bool useSimplifiedChinese,
		Microsoft.UI.Dispatching.DispatcherQueue callbackDispatcherQueue)
	{
		ArgumentNullException.ThrowIfNull(commandTarget);
		Dispose();
		target = commandTarget;
		dispatcherQueue = callbackDispatcherQueue;
		contextHandle = GCHandle.Alloc(this);
		MacOSNativeMethods.InstallMainMenu(
			useSimplifiedChinese ? "zh-Hans" : "en",
			&ExecuteCallback,
			&ValidateCallback,
			GCHandle.ToIntPtr(contextHandle));
	}

	public void UpdateValidationSnapshot(IMacOSMenuCommandTarget commandTarget)
	{
		foreach (MacOSMenuCommand command in Enum.GetValues<MacOSMenuCommand>())
		{
			Volatile.Write(ref commandStates[(int)command], commandTarget.CanExecuteMenuCommand(command) ? 1 : 0);
		}
	}

	public void Dispose()
	{
		if (contextHandle.IsAllocated)
		{
			MacOSNativeMethods.UninstallMainMenu();
			contextHandle.Free();
		}
		target = null;
		dispatcherQueue = null;
	}

	public string Describe()
	{
		nint pointer = MacOSNativeMethods.DescribeMainMenu();
		try
		{
			return Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
		}
		finally
		{
			MacOSNativeMethods.Free(pointer);
		}
	}

	public bool InvokeForDiagnostics(MacOSMenuCommand command)
	{
		return MacOSNativeMethods.InvokeMainMenuCommand((int)command) != 0;
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void ExecuteCallback(nint context, int command)
	{
		if (GCHandle.FromIntPtr(context).Target is not MacOSMainMenuService service || service.target is null)
		{
			return;
		}

		IMacOSMenuCommandTarget target = service.target;
		service.dispatcherQueue?.TryEnqueue(() => target.ExecuteMenuCommand((MacOSMenuCommand)command));
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static int ValidateCallback(nint context, int command)
	{
		if (GCHandle.FromIntPtr(context).Target is not MacOSMainMenuService service ||
			command < 0 || command >= service.commandStates.Length)
		{
			return 0;
		}
		return Volatile.Read(ref service.commandStates[command]);
	}
}
