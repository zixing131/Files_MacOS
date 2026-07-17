using Files.App.MacOS.Interop;

namespace Files.App.MacOS.Services;

internal enum FullDiskAccessStatus
{
	Unknown = -1,
	Denied,
	Granted,
}

internal static class MacOSPrivacyService
{
	private static int promptStarted;

	public static FullDiskAccessStatus GetFullDiskAccessStatus() =>
		(FullDiskAccessStatus)MacOSNativeMethods.GetFullDiskAccessStatus();

	public static bool TryBeginPrompt() => Interlocked.Exchange(ref promptStarted, 1) == 0;

	public static bool OpenFullDiskAccessSettings() => MacOSNativeMethods.OpenFullDiskAccessSettings() != 0;
}
