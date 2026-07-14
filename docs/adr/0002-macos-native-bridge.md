# ADR 0002: Isolate macOS Desktop APIs Behind a Native C ABI

## Status

Accepted for the macOS port on 2026-07-14.

## Context

The Uno Skia Desktop target runs on standard .NET and does not directly expose all AppKit, Quick Look and Finder APIs needed by a file manager. Calling command-line tools would lose structured errors and would not provide thumbnails or a Quick Look panel. Binding Objective-C objects directly throughout the C# application would also spread platform and lifetime concerns into view models.

## Decision

- Implement macOS desktop integration in `Native/FilesMacOSBridge.m`.
- Export a narrow C ABI for opening, revealing, previewing, trashing, generating thumbnails, metadata handling, Spotlight, FSEvents directory monitoring, folder selection, security-scoped bookmarks, network mounting and coordinated file transactions.
- Keep all Objective-C object ownership inside the bridge and return copied buffers or UTF-8 errors to C#.
- Wrap the ABI in source-generated `LibraryImport` declarations and `IMacOSWorkspaceService`.
- Build the bridge separately for `arm64` and `x86_64` and copy the matching dylib beside the .NET executable.
- Dispatch AppKit UI work to the main queue and execute blocking file/thumbnail work away from the UI thread.

## Consequences

- Cross-platform view models depend on a service contract rather than AppKit types.
- Native libraries must be signed in the correct order when app packaging and notarization are added.
- C ABI changes require coordinated native and managed updates.
- Managed copy/move transactions enter `NSFileCoordinator` through an unmanaged callback, use the coordinator-provided source and destination paths, and keep exception ownership in managed code.
- Trash operations use `NSFileManager.trashItemAtURL`, preserving the system Trash instead of permanently deleting files.
- Per-pane directory monitoring uses dispatch-queue-backed FSEvent streams; native teardown stops, invalidates and drains the stream before managed callback ownership is released.
- User-selected folders produce security-scoped bookmark data; restoration starts scoped access before workspace navigation, refreshes stale bookmarks and retains native URL ownership until explicit revocation or app shutdown.
- Quick Look thumbnail generation remains asynchronous internally and requires an active macOS event loop.

## Verification

- Both bridge binaries must be Mach-O dylibs with their expected architecture.
- Exported symbols are checked with `nm`.
- A generated thumbnail must contain a valid PNG signature.
- A uniquely named temporary file must move to Trash and be removed from Trash after the smoke test.
- Direct and recursively nested temporary-file changes must reach the managed monitor through FSEvents, including paths whose system canonical form differs from the input path.
- A temporary-directory bookmark must create, resolve, enter and leave security-scoped access; malformed bookmark data must be rejected without retaining a native access context.
- Coordinated copy, replace and move operations must preserve rollback, cancellation, symbolic links and extended metadata in an isolated tree.
- The app must build and launch for `osx-arm64`; `osx-x64` must cross-build successfully.
