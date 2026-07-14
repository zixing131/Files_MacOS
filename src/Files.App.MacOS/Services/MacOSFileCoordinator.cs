using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Files.App.MacOS.Interop;

namespace Files.App.MacOS.Services;

internal static unsafe class MacOSFileCoordinator
{
	public static void Coordinate(
		string sourcePath,
		string destinationPath,
		bool isMove,
		Func<string, string, Task> operation,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(operation);
		cancellationToken.ThrowIfCancellationRequested();
		var state = new CoordinationState(operation, cancellationToken);
		GCHandle handle = GCHandle.Alloc(state);
		string? nativeError = null;
		try
		{
			nint errorPointer = MacOSNativeMethods.CoordinateFileOperation(
				sourcePath,
				destinationPath,
				isMove ? 1 : 0,
				&ExecuteOperation,
				GCHandle.ToIntPtr(handle));
			if (errorPointer is not 0)
			{
				try
				{
					nativeError = Marshal.PtrToStringUTF8(errorPointer);
				}
				finally
				{
					MacOSNativeMethods.Free(errorPointer);
				}
			}
		}
		finally
		{
			handle.Free();
		}

		state.Exception?.Throw();
		if (!state.WasInvoked)
		{
			throw new IOException(nativeError ?? "The coordinated file operation wasn't invoked.");
		}
		if (nativeError is not null)
		{
			throw new IOException(nativeError);
		}
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static int ExecuteOperation(nint context, nint sourcePath, nint destinationPath)
	{
		if (GCHandle.FromIntPtr(context).Target is not CoordinationState state)
		{
			return 0;
		}

		state.WasInvoked = true;
		try
		{
			state.CancellationToken.ThrowIfCancellationRequested();
			string source = Marshal.PtrToStringUTF8(sourcePath) ?? throw new IOException("The coordinated source path is invalid.");
			string destination = Marshal.PtrToStringUTF8(destinationPath) ?? throw new IOException("The coordinated destination path is invalid.");
			state.Operation(source, destination).GetAwaiter().GetResult();
			return 1;
		}
		catch (Exception ex)
		{
			state.Exception = ExceptionDispatchInfo.Capture(ex);
			return 0;
		}
	}

	private sealed class CoordinationState(
		Func<string, string, Task> operation,
		CancellationToken cancellationToken)
	{
		public Func<string, string, Task> Operation { get; } = operation;

		public CancellationToken CancellationToken { get; } = cancellationToken;

		public bool WasInvoked { get; set; }

		public ExceptionDispatchInfo? Exception { get; set; }
	}
}
