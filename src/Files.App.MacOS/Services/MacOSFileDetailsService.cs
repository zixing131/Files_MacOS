using System.Runtime.InteropServices;
using System.Text.Json;
using Files.App.MacOS.Interop;

namespace Files.App.MacOS.Services;

public sealed record FileDetails
{
	public string? Kind { get; init; }
	public string? ContentType { get; init; }
	public string? Version { get; init; }
	public string? Build { get; init; }
	public string? Copyright { get; init; }
	public string? Comments { get; init; }
	public string? CapturedAt { get; init; }
	public long? PixelWidth { get; init; }
	public long? PixelHeight { get; init; }
	public double? DurationSeconds { get; init; }
	public double? LastOpenedUnixSeconds { get; init; }
	public string[] WhereFroms { get; init; } = [];
	public string[] Authors { get; init; } = [];
}

internal static class MacOSFileDetailsService
{
	public static FileDetails Read(string path)
	{
		nint pointer = MacOSNativeMethods.GetFileDetails(path);
		if (pointer == 0) return new();
		try { return JsonSerializer.Deserialize<FileDetails>(Marshal.PtrToStringUTF8(pointer) ?? "{}") ?? new(); }
		catch (JsonException) { return new(); }
		finally { MacOSNativeMethods.Free(pointer); }
	}
}
