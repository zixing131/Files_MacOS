using System.Runtime.InteropServices;
using System.Text.Json;
using Files.App.MacOS.Interop;

namespace Files.App.MacOS.Services;

internal sealed record MacOSFileSecurityInfo(
	[property: System.Text.Json.Serialization.JsonPropertyName("owner")] string Owner,
	[property: System.Text.Json.Serialization.JsonPropertyName("group")] string Group,
	[property: System.Text.Json.Serialization.JsonPropertyName("userId")] uint UserId,
	[property: System.Text.Json.Serialization.JsonPropertyName("groupId")] uint GroupId,
	[property: System.Text.Json.Serialization.JsonPropertyName("acl")] string Acl,
	[property: System.Text.Json.Serialization.JsonPropertyName("isHidden")] bool IsHidden,
	[property: System.Text.Json.Serialization.JsonPropertyName("isLocked")] bool IsLocked);

internal static class MacOSFileSecurityService
{
	public static MacOSFileSecurityInfo? GetInfo(string path)
	{
		nint pointer = MacOSNativeMethods.GetFileSecurity(path);
		if (pointer is 0)
		{
			return null;
		}
		try
		{
			string json = Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
			return JsonSerializer.Deserialize<MacOSFileSecurityInfo>(json);
		}
		catch (JsonException)
		{
			return null;
		}
		finally
		{
			MacOSNativeMethods.Free(pointer);
		}
	}

	public static void SetFlags(string path, bool isHidden, bool isLocked)
	{
		nint errorPointer = MacOSNativeMethods.SetFileFlags(path, isHidden ? 1 : 0, isLocked ? 1 : 0);
		if (errorPointer is 0)
		{
			return;
		}
		try
		{
			throw new IOException(Marshal.PtrToStringUTF8(errorPointer) ?? "The macOS file flags couldn't be saved.");
		}
		finally
		{
			MacOSNativeMethods.Free(errorPointer);
		}
	}

	public static void SetSecurity(string path, string owner, string group, string accessControlList)
	{
		nint errorPointer = MacOSNativeMethods.SetFileSecurity(path, owner, group, accessControlList);
		if (errorPointer is 0)
		{
			return;
		}
		try
		{
			throw new IOException(Marshal.PtrToStringUTF8(errorPointer) ?? "The macOS file security settings couldn't be saved.");
		}
		finally
		{
			MacOSNativeMethods.Free(errorPointer);
		}
	}
}
