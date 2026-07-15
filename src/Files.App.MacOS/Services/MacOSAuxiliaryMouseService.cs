using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Files.App.MacOS.Interop;
using Microsoft.UI.Dispatching;

namespace Files.App.MacOS.Services;

internal sealed class MacOSAuxiliaryMouseService : IDisposable
{
	private readonly GCHandle callbackHandle;
	private readonly DispatcherQueue dispatcherQueue;
	private readonly Action<int> callback;
	private bool isDisposed;

	public unsafe MacOSAuxiliaryMouseService(DispatcherQueue dispatcherQueue, Action<int> callback)
	{
		this.dispatcherQueue = dispatcherQueue;
		this.callback = callback;
		callbackHandle = GCHandle.Alloc(this);
		MacOSNativeMethods.InstallAuxiliaryMouseHandler(
			&HandleAuxiliaryMouseButton,
			GCHandle.ToIntPtr(callbackHandle));
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void HandleAuxiliaryMouseButton(nint context, int buttonNumber)
	{
		if (GCHandle.FromIntPtr(context).Target is MacOSAuxiliaryMouseService service)
		{
			service.dispatcherQueue.TryEnqueue(() => service.callback(buttonNumber));
		}
	}

	public void Dispose()
	{
		if (isDisposed)
		{
			return;
		}
		isDisposed = true;
		MacOSNativeMethods.UninstallAuxiliaryMouseHandler();
		callbackHandle.Free();
	}
}
