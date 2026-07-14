using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Files.App.MacOS.Interop;

namespace Files.App.MacOS.Services;

public sealed class LocalDirectoryChangeMonitor : IDirectoryChangeMonitor
{
	private readonly object syncRoot = new();
	private readonly GCHandle callbackHandle;
	private nint monitor;
	private CancellationTokenSource? debounceCancellation;
	private string? watchedPath;
	private bool includesSubdirectories;
	private bool isDisposed;

	public LocalDirectoryChangeMonitor()
	{
		callbackHandle = GCHandle.Alloc(this);
	}

	public event EventHandler? Changed;

	public unsafe void Watch(string path, bool includeSubdirectories = false)
	{
		ObjectDisposedException.ThrowIf(isDisposed, this);
		string fullPath = Path.GetFullPath(path);
		lock (syncRoot)
		{
			if (string.Equals(watchedPath, fullPath, StringComparison.Ordinal) &&
				includesSubdirectories == includeSubdirectories)
			{
				return;
			}
		}

		nint newMonitor = MacOSNativeMethods.CreateDirectoryChangeMonitor(
			fullPath,
			includeSubdirectories ? 1 : 0,
			&DirectoryChanged,
			GCHandle.ToIntPtr(callbackHandle));
		if (newMonitor is 0)
		{
			throw new IOException($"FSEvents couldn't monitor '{fullPath}'.");
		}

		nint previousMonitor = 0;
		bool disposed;
		lock (syncRoot)
		{
			disposed = isDisposed;
			if (!disposed)
			{
				previousMonitor = monitor;
				monitor = newMonitor;
				watchedPath = fullPath;
				includesSubdirectories = includeSubdirectories;
			}
		}

		if (disposed)
		{
			MacOSNativeMethods.DestroyDirectoryChangeMonitor(newMonitor);
			throw new ObjectDisposedException(nameof(LocalDirectoryChangeMonitor));
		}

		if (previousMonitor is not 0)
		{
			MacOSNativeMethods.DestroyDirectoryChangeMonitor(previousMonitor);
		}
	}

	public void Dispose()
	{
		nint monitorToDestroy;
		lock (syncRoot)
		{
			if (isDisposed)
			{
				return;
			}

			isDisposed = true;
			monitorToDestroy = monitor;
			monitor = 0;
			watchedPath = null;
			debounceCancellation?.Cancel();
			debounceCancellation?.Dispose();
			debounceCancellation = null;
		}

		if (monitorToDestroy is not 0)
		{
			MacOSNativeMethods.DestroyDirectoryChangeMonitor(monitorToDestroy);
		}
		callbackHandle.Free();
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void DirectoryChanged(nint context)
	{
		GCHandle handle = GCHandle.FromIntPtr(context);
		if (handle.Target is LocalDirectoryChangeMonitor target)
		{
			target.ScheduleNotification();
		}
	}

	private void ScheduleNotification()
	{
		CancellationTokenSource cancellation;
		lock (syncRoot)
		{
			if (isDisposed)
			{
				return;
			}

			debounceCancellation?.Cancel();
			debounceCancellation?.Dispose();
			cancellation = new CancellationTokenSource();
			debounceCancellation = cancellation;
		}

		_ = NotifyAfterDelayAsync(cancellation);
	}

	private async Task NotifyAfterDelayAsync(CancellationTokenSource cancellation)
	{
		try
		{
			await Task.Delay(250, cancellation.Token);
			Changed?.Invoke(this, EventArgs.Empty);
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			lock (syncRoot)
			{
				if (ReferenceEquals(debounceCancellation, cancellation))
				{
					debounceCancellation = null;
					cancellation.Dispose();
				}
			}
		}
	}
}
