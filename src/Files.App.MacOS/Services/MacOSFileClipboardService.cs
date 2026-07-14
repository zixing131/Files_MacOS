using System.Runtime.InteropServices;
using System.Text.Json;
using Files.App.MacOS.Interop;

namespace Files.App.MacOS.Services;

public sealed class MacOSFileClipboardService : IFileClipboardService
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
	};

	public async Task<FileClipboardContent> WriteAsync(
		IReadOnlyList<string> paths,
		FileTransferMode mode,
		CancellationToken cancellationToken = default)
	{
		string[] fullPaths = paths.Select(Path.GetFullPath).ToArray();
		string pathsJson = JsonSerializer.Serialize(fullPaths);

		await Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			nint errorPointer = MacOSNativeMethods.WriteFileClipboard(pathsJson, mode is FileTransferMode.Move ? 1 : 0);
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

		return await ReadAsync(cancellationToken);
	}

	public Task<FileClipboardContent> ReadAsync(CancellationToken cancellationToken = default)
	{
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			nint resultPointer = MacOSNativeMethods.ReadFileClipboard();
			if (resultPointer is 0)
			{
				return new FileClipboardContent([], FileTransferMode.Copy, 0);
			}

			try
			{
				string json = Marshal.PtrToStringUTF8(resultPointer) ?? string.Empty;
				ClipboardPayload? payload = JsonSerializer.Deserialize<ClipboardPayload>(json, JsonOptions);
				return new FileClipboardContent(
					payload?.Paths ?? [],
					payload?.IsCut is true ? FileTransferMode.Move : FileTransferMode.Copy,
					payload?.ChangeCount ?? 0);
			}
			catch (JsonException ex)
			{
				throw new IOException("The macOS pasteboard returned invalid file data.", ex);
			}
			finally
			{
				MacOSNativeMethods.Free(resultPointer);
			}
		}, cancellationToken);
	}

	public Task<bool> ClearAsync(long expectedChangeCount, CancellationToken cancellationToken = default)
	{
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			return MacOSNativeMethods.ClearFileClipboard(expectedChangeCount) is not 0;
		}, cancellationToken);
	}

	private sealed record ClipboardPayload(string[] Paths, bool IsCut, long ChangeCount);
}
