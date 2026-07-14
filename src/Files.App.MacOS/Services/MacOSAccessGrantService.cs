using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Files.App.MacOS.Interop;
using Files.App.MacOS.Models;

namespace Files.App.MacOS.Services;

public sealed class MacOSAccessGrantService : IMacOSAccessGrantService
{
	private readonly object syncRoot = new();
	private readonly Dictionary<string, nint> activeAccess = new(StringComparer.Ordinal);
	private bool isDisposed;

	public Task<FolderAccessGrant?> PickFolderAsync(string initialPath, CancellationToken cancellationToken = default)
	{
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			nint resultPointer = MacOSNativeMethods.PickFolder(initialPath);
			NativeBookmarkResult result = ReadResult(resultPointer);
			if (result.Canceled)
			{
				return null;
			}
			if (!result.Success || string.IsNullOrWhiteSpace(result.Path) || string.IsNullOrWhiteSpace(result.Bookmark))
			{
				throw new IOException(string.IsNullOrWhiteSpace(result.Error) ? "The selected folder couldn't be authorized." : result.Error);
			}

			return Activate(new(result.Path, result.Bookmark), throwOnFailure: true);
		}, cancellationToken);
	}

	public Task<IReadOnlyList<RestoredFolderAccessGrant>> RestoreAsync(
		IEnumerable<FolderAccessGrant> grants,
		CancellationToken cancellationToken = default)
	{
		FolderAccessGrant[] requestedGrants = grants
			.Where(static grant => grant is not null)
			.DistinctBy(static grant => grant.Path, StringComparer.Ordinal)
			.ToArray();
		return Task.Run(() =>
		{
			var restored = new List<RestoredFolderAccessGrant>(requestedGrants.Length);
			foreach (FolderAccessGrant grant in requestedGrants)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (Activate(grant, throwOnFailure: false) is FolderAccessGrant activeGrant)
				{
					restored.Add(new(grant.Path, activeGrant));
				}
			}
			return (IReadOnlyList<RestoredFolderAccessGrant>)restored;
		}, cancellationToken);
	}

	public void Dispose()
	{
		nint[] handles;
		lock (syncRoot)
		{
			if (isDisposed)
			{
				return;
			}
			isDisposed = true;
			handles = activeAccess.Values.ToArray();
			activeAccess.Clear();
		}

		foreach (nint handle in handles)
		{
			MacOSNativeMethods.StopSecurityBookmarkAccess(handle);
		}
	}

	public void Revoke(string path)
	{
		nint handle;
		lock (syncRoot)
		{
			ObjectDisposedException.ThrowIf(isDisposed, this);
			if (!activeAccess.Remove(path, out handle))
			{
				return;
			}
		}
		MacOSNativeMethods.StopSecurityBookmarkAccess(handle);
	}

	private FolderAccessGrant? Activate(FolderAccessGrant grant, bool throwOnFailure)
	{
		nint resultPointer = 0;
		nint accessHandle = MacOSNativeMethods.AccessSecurityBookmark(grant.Bookmark, out resultPointer);
		NativeBookmarkResult result;
		try
		{
			result = ReadResult(resultPointer);
		}
		catch
		{
			if (accessHandle is not 0)
			{
				MacOSNativeMethods.StopSecurityBookmarkAccess(accessHandle);
			}
			throw;
		}

		if (accessHandle is 0 || !result.Success || string.IsNullOrWhiteSpace(result.Path) || string.IsNullOrWhiteSpace(result.Bookmark))
		{
			if (accessHandle is not 0)
			{
				MacOSNativeMethods.StopSecurityBookmarkAccess(accessHandle);
			}
			if (throwOnFailure)
			{
				throw new IOException(string.IsNullOrWhiteSpace(result.Error) ? "The folder access bookmark couldn't be restored." : result.Error);
			}
			return null;
		}

		nint previousHandle = 0;
		lock (syncRoot)
		{
			if (isDisposed)
			{
				MacOSNativeMethods.StopSecurityBookmarkAccess(accessHandle);
				throw new ObjectDisposedException(nameof(MacOSAccessGrantService));
			}
			activeAccess.Remove(result.Path, out previousHandle);
			activeAccess[result.Path] = accessHandle;
		}

		if (previousHandle is not 0)
		{
			MacOSNativeMethods.StopSecurityBookmarkAccess(previousHandle);
		}
		return new(result.Path, result.Bookmark);
	}

	private static NativeBookmarkResult ReadResult(nint resultPointer)
	{
		if (resultPointer is 0)
		{
			return new(false, false, null, null, "The native bookmark operation didn't return a result.");
		}

		try
		{
			string json = Marshal.PtrToStringUTF8(resultPointer) ?? string.Empty;
			return JsonSerializer.Deserialize<NativeBookmarkResult>(json) ??
				new(false, false, null, null, "The native bookmark result is invalid.");
		}
		catch (JsonException ex)
		{
			throw new IOException("The native bookmark result is invalid.", ex);
		}
		finally
		{
			MacOSNativeMethods.Free(resultPointer);
		}
	}

	private sealed record NativeBookmarkResult(
		[property: JsonPropertyName("success")] bool Success,
		[property: JsonPropertyName("canceled")] bool Canceled,
		[property: JsonPropertyName("path")] string? Path,
		[property: JsonPropertyName("bookmark")] string? Bookmark,
		[property: JsonPropertyName("error")] string? Error);
}
