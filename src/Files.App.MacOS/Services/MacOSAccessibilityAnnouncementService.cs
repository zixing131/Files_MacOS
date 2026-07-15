using Files.App.MacOS.Interop;

namespace Files.App.MacOS.Services;

internal static class MacOSAccessibilityAnnouncementService
{
	public static bool Probe()
	{
		return MacOSNativeMethods.AnnounceAccessibility(string.Empty, 0) == 0;
	}

	public static bool Announce(string announcement, bool highPriority = false)
	{
		return !string.IsNullOrWhiteSpace(announcement) &&
			MacOSNativeMethods.AnnounceAccessibility(announcement, highPriority ? 1 : 0) != 0;
	}
}
