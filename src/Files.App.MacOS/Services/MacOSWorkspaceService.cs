using System.Runtime.InteropServices;
using System.Text.Json;
using Files.App.MacOS.Interop;
using Files.App.MacOS.Models;

namespace Files.App.MacOS.Services;

public sealed class MacOSWorkspaceService : IMacOSWorkspaceService
{
	public Task OpenAsync(string path, CancellationToken cancellationToken = default)
	{
		return InvokeAsync(path, MacOSNativeMethods.OpenPath, cancellationToken);
	}

	public Task<IReadOnlyList<OpenWithApplication>> GetOpenWithApplicationsAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		return Task.Run<IReadOnlyList<OpenWithApplication>>(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			nint resultPointer = MacOSNativeMethods.GetOpenWithApplications(path);
			if (resultPointer is 0)
			{
				return [];
			}

			try
			{
				string json = Marshal.PtrToStringUTF8(resultPointer) ?? "[]";
				return JsonSerializer.Deserialize<OpenWithApplication[]>(json) ?? [];
			}
			catch (JsonException)
			{
				return [];
			}
			finally
			{
				MacOSNativeMethods.Free(resultPointer);
			}
		}, cancellationToken);
	}

	public Task OpenWithAsync(string path, string applicationPath, CancellationToken cancellationToken = default)
	{
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			nint errorPointer = MacOSNativeMethods.OpenPathWithApplication(path, applicationPath);
			if (errorPointer is 0)
			{
				return;
			}
			try
			{
				throw new IOException(Marshal.PtrToStringUTF8(errorPointer));
			}
			finally
			{
				MacOSNativeMethods.Free(errorPointer);
			}
		}, cancellationToken);
	}

	public Task<string?> PickApplicationAsync(CancellationToken cancellationToken = default)
	{
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			nint resultPointer = MacOSNativeMethods.PickApplication();
			if (resultPointer is 0)
			{
				throw new IOException("The application picker didn't return a result.");
			}
			try
			{
				string json = Marshal.PtrToStringUTF8(resultPointer) ?? string.Empty;
				NativeApplicationPickerResult? result = JsonSerializer.Deserialize<NativeApplicationPickerResult>(json);
				return result?.Canceled is true || string.IsNullOrWhiteSpace(result?.Path) ? null : result.Path;
			}
			finally
			{
				MacOSNativeMethods.Free(resultPointer);
			}
		}, cancellationToken);
	}

	public Task RevealAsync(string path, CancellationToken cancellationToken = default)
	{
		return InvokeAsync(path, MacOSNativeMethods.RevealPath, cancellationToken);
	}

	public Task OpenUrlAsync(string url, CancellationToken cancellationToken = default)
	{
		return InvokeAsync(url, MacOSNativeMethods.OpenUrl, cancellationToken);
	}

	public Task EjectVolumeAsync(string path, CancellationToken cancellationToken = default)
	{
		return InvokeAsync(path, MacOSNativeMethods.EjectVolume, cancellationToken);
	}

	public Task OpenTerminalAsync(string path, TerminalPreference terminal, CancellationToken cancellationToken = default)
	{
		string bundleIdentifier = terminal switch
		{
			TerminalPreference.ITerm2 => "com.googlecode.iterm2",
			TerminalPreference.Warp => "dev.warp.Warp-Stable",
			TerminalPreference.Kitty => "net.kovidgoyal.kitty",
			TerminalPreference.Alacritty => "org.alacritty",
			TerminalPreference.WezTerm => "com.github.wez.wezterm",
			_ => "com.apple.Terminal",
		};
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (MacOSNativeMethods.OpenTerminal(path, bundleIdentifier) is 0)
			{
				throw new IOException("The selected terminal application is unavailable.");
			}
		}, cancellationToken);
	}

	public Task<NetworkConnectionResult> ConnectServerAsync(string address, CancellationToken cancellationToken = default)
	{
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			nint resultPointer = MacOSNativeMethods.ConnectServer(address);
			if (resultPointer is 0)
			{
				throw new IOException("The server connection didn't return a result.");
			}

			try
			{
				string json = Marshal.PtrToStringUTF8(resultPointer) ?? string.Empty;
				NativeNetworkConnectionResult? result = JsonSerializer.Deserialize<NativeNetworkConnectionResult>(json);
				if (result?.Canceled is true)
				{
					throw new OperationCanceledException();
				}
				if (result?.Success is not true)
				{
					throw new IOException(string.IsNullOrWhiteSpace(result?.Error) ? "The server couldn't be connected." : result.Error);
				}

				cancellationToken.ThrowIfCancellationRequested();
				return new NetworkConnectionResult(result.MountPaths ?? [], result.OpenedExternally);
			}
			finally
			{
				MacOSNativeMethods.Free(resultPointer);
			}
		}, cancellationToken);
	}

	public Task PreviewAsync(string path, CancellationToken cancellationToken = default)
	{
		return InvokeAsync(path, MacOSNativeMethods.PreviewPath, cancellationToken);
	}

	public Task<IReadOnlyList<TrashedItemResult>> MoveToTrashAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
	{
		return Task.Run(() =>
		{
			var completedItems = new List<TrashedItemResult>(paths.Count);
			foreach (string path in paths)
			{
				cancellationToken.ThrowIfCancellationRequested();
				nint resultPointer = MacOSNativeMethods.MoveToTrashWithResult(path);
				if (resultPointer is 0)
				{
					throw new TrashOperationPartialException("The Trash operation didn't return a result.", completedItems);
				}

				try
				{
					string json = Marshal.PtrToStringUTF8(resultPointer) ?? string.Empty;
					NativeTrashResult? result = JsonSerializer.Deserialize<NativeTrashResult>(json);
					if (result?.Success is not true || string.IsNullOrWhiteSpace(result.TrashPath))
					{
						string message = string.IsNullOrWhiteSpace(result?.Error) ? "The item couldn't be moved to the Trash." : result.Error;
						throw new TrashOperationPartialException(message, completedItems.ToArray());
					}

					completedItems.Add(new(
						string.IsNullOrWhiteSpace(result.OriginalPath) ? Path.GetFullPath(path) : result.OriginalPath,
						result.TrashPath));
				}
				finally
				{
					MacOSNativeMethods.Free(resultPointer);
				}
			}

			return (IReadOnlyList<TrashedItemResult>)completedItems;
		}, cancellationToken);
	}

	public Task ShareAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
	{
		return ShareCoreAsync(paths, MacOSNativeMethods.ShareFiles, cancellationToken);
	}

	public Task ShareViaAirDropAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
	{
		return ShareCoreAsync(paths, MacOSNativeMethods.ShareFilesViaAirDrop, cancellationToken);
	}

	public Task RestoreFromTrashAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
	{
		return Task.Run(() =>
		{
			foreach (string path in paths)
			{
				cancellationToken.ThrowIfCancellationRequested();
				nint errorPointer = MacOSNativeMethods.RestoreFromTrash(Path.GetFullPath(path));
				if (errorPointer is 0)
				{
					continue;
				}

				try
				{
					throw new IOException(Marshal.PtrToStringUTF8(errorPointer));
				}
				finally
				{
					MacOSNativeMethods.Free(errorPointer);
				}
			}
		}, cancellationToken);
	}

	private static Task ShareCoreAsync(
		IReadOnlyList<string> paths,
		Func<string, nint> share,
		CancellationToken cancellationToken)
	{
		string pathsJson = JsonSerializer.Serialize(paths.Select(Path.GetFullPath));
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			nint errorPointer = share(pathsJson);
			if (errorPointer is 0)
			{
				return;
			}

			try
			{
				throw new IOException(Marshal.PtrToStringUTF8(errorPointer));
			}
			finally
			{
				MacOSNativeMethods.Free(errorPointer);
			}
		}, cancellationToken);
	}

	public Task<byte[]?> GetThumbnailPngAsync(
		string path,
		int width,
		int height,
		double scale,
		CancellationToken cancellationToken = default)
	{
		return GetThumbnailPngCoreAsync(path, width, height, scale, contentOnly: false, cancellationToken);
	}

	public Task<byte[]?> GetContentPreviewPngAsync(
		string path,
		int width,
		int height,
		double scale,
		CancellationToken cancellationToken = default)
	{
		return GetThumbnailPngCoreAsync(path, width, height, scale, contentOnly: true, cancellationToken);
	}

	private static Task<byte[]?> GetThumbnailPngCoreAsync(
		string path,
		int width,
		int height,
		double scale,
		bool contentOnly,
		CancellationToken cancellationToken)
	{
		return Task.Run<byte[]?>(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (MacOSNativeMethods.GenerateThumbnail(path, width, height, scale, contentOnly ? 1 : 0, out nint output, out nuint length) is 0 ||
				output is 0 ||
				length is 0 or > int.MaxValue)
			{
				return null;
			}

			try
			{
				var bytes = new byte[(int)length];
				Marshal.Copy(output, bytes, 0, bytes.Length);
				return bytes;
			}
			finally
			{
				MacOSNativeMethods.Free(output);
			}
		}, cancellationToken);
	}

	private static Task InvokeAsync(string path, Func<string, int> operation, CancellationToken cancellationToken)
	{
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (operation(path) is 0)
			{
				throw new IOException();
			}
		}, cancellationToken);
	}

	private sealed record NativeNetworkConnectionResult(
		[property: System.Text.Json.Serialization.JsonPropertyName("success")] bool Success,
		[property: System.Text.Json.Serialization.JsonPropertyName("canceled")] bool Canceled,
		[property: System.Text.Json.Serialization.JsonPropertyName("mountPaths")] string[]? MountPaths,
		[property: System.Text.Json.Serialization.JsonPropertyName("openedExternally")] bool OpenedExternally,
		[property: System.Text.Json.Serialization.JsonPropertyName("error")] string? Error);

	private sealed record NativeTrashResult(
		[property: System.Text.Json.Serialization.JsonPropertyName("success")] bool Success,
		[property: System.Text.Json.Serialization.JsonPropertyName("originalPath")] string? OriginalPath,
		[property: System.Text.Json.Serialization.JsonPropertyName("trashPath")] string? TrashPath,
		[property: System.Text.Json.Serialization.JsonPropertyName("error")] string? Error);

	private sealed record NativeApplicationPickerResult(
		[property: System.Text.Json.Serialization.JsonPropertyName("canceled")] bool Canceled,
		[property: System.Text.Json.Serialization.JsonPropertyName("path")] string? Path);
}
