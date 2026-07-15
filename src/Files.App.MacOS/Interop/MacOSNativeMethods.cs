using System.Runtime.InteropServices;

namespace Files.App.MacOS.Interop;

internal static partial class MacOSNativeMethods
{
	private const string LibraryName = "FilesMacOSBridge";

	[LibraryImport(LibraryName, EntryPoint = "files_macos_open_path", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int OpenPath(string path);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_is_file_package", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int IsFilePackage(string path);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_get_open_with_applications", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint GetOpenWithApplications(string path);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_open_path_with_application", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint OpenPathWithApplication(string path, string applicationPath);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_pick_application")]
	internal static partial nint PickApplication();

	[LibraryImport(LibraryName, EntryPoint = "files_macos_install_main_menu", StringMarshalling = StringMarshalling.Utf8)]
	internal static unsafe partial void InstallMainMenu(
		string language,
		delegate* unmanaged[Cdecl]<nint, int, void> execute,
		delegate* unmanaged[Cdecl]<nint, int, int> validate,
		nint context);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_uninstall_main_menu")]
	internal static partial void UninstallMainMenu();

	[LibraryImport(LibraryName, EntryPoint = "files_macos_describe_main_menu")]
	internal static partial nint DescribeMainMenu();

	[LibraryImport(LibraryName, EntryPoint = "files_macos_invoke_main_menu_command")]
	internal static partial int InvokeMainMenuCommand(int command);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_get_accessibility_display_options")]
	internal static partial int GetAccessibilityDisplayOptions();

	[LibraryImport(LibraryName, EntryPoint = "files_macos_announce_accessibility", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int AnnounceAccessibility(string announcement, int highPriority);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_register_window", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int RegisterWindow(nint windowHandle, string identifier);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_get_window_placement", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint GetWindowPlacement(string identifier);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_set_window_placement", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int SetWindowPlacement(
		string identifier,
		double x,
		double y,
		double width,
		double height,
		int isMaximized);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_fsevents_create", StringMarshalling = StringMarshalling.Utf8)]
	internal static unsafe partial nint CreateDirectoryChangeMonitor(
		string path,
		int includeSubdirectories,
		delegate* unmanaged[Cdecl]<nint, void> callback,
		nint callbackContext);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_fsevents_destroy")]
	internal static partial void DestroyDirectoryChangeMonitor(nint monitor);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_create_security_bookmark", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint CreateSecurityBookmark(string path);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_pick_folder", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint PickFolder(string initialPath);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_access_security_bookmark", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint AccessSecurityBookmark(string bookmark, out nint resultJson);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_stop_security_bookmark_access")]
	internal static partial void StopSecurityBookmarkAccess(nint accessContext);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_reveal_path", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int RevealPath(string path);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_open_terminal", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int OpenTerminal(string path);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_connect_server", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint ConnectServer(string address);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_move_to_trash", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint MoveToTrash(string path);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_move_to_trash_with_result", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint MoveToTrashWithResult(string path);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_preview_path", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int PreviewPath(string path);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_write_file_clipboard", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint WriteFileClipboard(string pathsJson, int isCut);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_read_file_clipboard")]
	internal static partial nint ReadFileClipboard();

	[LibraryImport(LibraryName, EntryPoint = "files_macos_clear_file_clipboard")]
	internal static partial int ClearFileClipboard(long expectedChangeCount);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_copy_metadata", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint CopyMetadata(string sourcePath, string destinationPath);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_get_finder_tags", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint GetFinderTags(string path);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_set_finder_tags", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint SetFinderTags(string path, string tagsJson);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_get_file_security", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint GetFileSecurity(string path);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_set_file_flags", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint SetFileFlags(string path, int isHidden, int isLocked);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_set_file_security", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint SetFileSecurity(string path, string owner, string group, string accessControlList);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_coordinate_file_operation", StringMarshalling = StringMarshalling.Utf8)]
	internal static unsafe partial nint CoordinateFileOperation(
		string sourcePath,
		string destinationPath,
		int isMove,
		delegate* unmanaged[Cdecl]<nint, nint, nint, int> operation,
		nint operationContext);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_spotlight_create")]
	internal static partial nint CreateSpotlightSearch();

	[LibraryImport(LibraryName, EntryPoint = "files_macos_spotlight_cancel")]
	internal static partial void CancelSpotlightSearch(nint searchContext);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_spotlight_search", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint SearchSpotlight(
		nint searchContext,
		string rootPath,
		string queryJson,
		int includeHidden,
		int timeoutMilliseconds);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_share_files", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial nint ShareFiles(string pathsJson);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_generate_thumbnail", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int GenerateThumbnail(
		string path,
		double width,
		double height,
		double scale,
		out nint output,
		out nuint outputLength);

	[LibraryImport(LibraryName, EntryPoint = "files_macos_free")]
	internal static partial void Free(nint pointer);
}
