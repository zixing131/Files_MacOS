using System.Runtime.InteropServices;
using Files.App.MacOS.Interop;

namespace Files.App.MacOS.Services;

internal static class MacOSFileMetadata
{
	public static void Copy(string sourcePath, string destinationPath)
	{
		nint errorPointer = MacOSNativeMethods.CopyMetadata(sourcePath, destinationPath);
		if (errorPointer is 0)
		{
			return;
		}

		try
		{
			string detail = Marshal.PtrToStringUTF8(errorPointer) ?? "Unknown error.";
			throw new IOException($"Couldn't copy metadata for '{Path.GetFileName(sourcePath)}': {detail}");
		}
		finally
		{
			MacOSNativeMethods.Free(errorPointer);
		}
	}
}
