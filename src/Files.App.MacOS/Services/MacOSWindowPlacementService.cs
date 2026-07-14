using System.Runtime.InteropServices;
using System.Text.Json;
using Files.App.MacOS.Interop;
using Files.App.MacOS.Models;

namespace Files.App.MacOS.Services;

internal static class MacOSWindowPlacementService
{
	public static nint GetNativeWindowHandle(Window window)
	{
		// Uno's public window handle is an AppWindow id; the backend handle is the NSWindow pointer.
		object? nativeWindow = window.GetType().GetProperty(
			"NativeWindow",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(window);
		object? handle = nativeWindow?.GetType().GetProperty(
			"Handle",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(nativeWindow);
		return handle is nint value ? value : 0;
	}

	public static bool RegisterWindow(nint windowHandle, string identifier)
	{
		return windowHandle != 0 && MacOSNativeMethods.RegisterWindow(windowHandle, identifier) != 0;
	}

	public static WindowPlacementState? GetPlacement(string identifier)
	{
		nint pointer = MacOSNativeMethods.GetWindowPlacement(identifier);
		if (pointer == 0)
		{
			return null;
		}

		try
		{
			string? json = Marshal.PtrToStringUTF8(pointer);
			return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<WindowPlacementState>(json);
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

	public static bool ApplyPlacement(string identifier, WindowPlacementState placement)
	{
		return MacOSNativeMethods.SetWindowPlacement(
			identifier,
			placement.X,
			placement.Y,
			placement.Width,
			placement.Height,
			placement.IsMaximized ? 1 : 0) != 0;
	}
}
