using System.Runtime.InteropServices;
using System.Text.Json;
using Files.App.MacOS.Interop;
using Files.App.MacOS.Models;

namespace Files.App.MacOS.Services;

internal static class ComputerVolumeService
{
	internal static IReadOnlyList<ComputerVolume> GetVolumes(string systemName, CancellationToken cancellationToken, bool showHidden = false)
	{
		cancellationToken.ThrowIfCancellationRequested();
		nint pointer = MacOSNativeMethods.GetMountedVolumes(showHidden ? 1 : 0);
		if (pointer == 0) return [];
		try
		{
			var volumes = JsonSerializer.Deserialize<ComputerVolume[]>(Marshal.PtrToStringUTF8(pointer) ?? "[]") ?? [];
			cancellationToken.ThrowIfCancellationRequested();
			return volumes.DistinctBy(volume => volume.Path, StringComparer.Ordinal)
				.OrderBy(volume => volume.Path != "/")
				.ThenBy(volume => volume.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
		}
		finally { MacOSNativeMethods.Free(pointer); }
	}
}
