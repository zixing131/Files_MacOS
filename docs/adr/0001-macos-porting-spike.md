# ADR 0001: Start the macOS Port with an Isolated Uno Desktop Spike

## Status

Accepted for the stage 0 spike on 2026-07-14. The production UI framework remains subject to the Go/No-Go criteria in the macOS porting plan.

## Context

Files is a WinUI 3 application with substantial dependencies on Windows App SDK, WinRT, Windows Shell, COM and Win32. A macOS build cannot reference those platform implementations. Replacing the Windows UI before measuring compatibility would also put the existing Windows product at unnecessary risk.

Uno Platform exposes the WinUI 3 API shape on Skia Desktop and supports .NET 10 desktop targets. It therefore offers the lowest-cost way to measure how much of the existing XAML, custom controls and MVVM code can be reused. This is a hypothesis to test, not a commitment to migrate the whole product to Uno.

## Decision

- Add an isolated `src/Files.App.MacOS` Uno Skia Desktop project targeting `net10.0-desktop`.
- Keep it in a separate `Files.MacOS.slnx` so the existing Windows solution build does not include the spike.
- Keep the current `Files.App` WinUI 3 project and Windows release pipeline unchanged.
- Target `osx-arm64` and `osx-x64`; do not add Windows or mobile heads to the spike.
- Implement one vertical slice first: local directory enumeration, navigation and a virtualized file list.
- Keep the spike's file-system code behind a small service boundary so it can move to a shared core or be replaced by a macOS platform implementation.
- Use Uno 6.5.36 for the initial measurement and pin it on the macOS project SDK reference.
- Reassess Uno after testing existing controls, input, drag/drop, accessibility and large-directory performance. Move to Avalonia if the Go criteria in `docs/macos-porting-plan.md` are not met.

## Consequences

- The repository gains a macOS-buildable application without changing the Windows application.
- Some UI will temporarily be duplicated while reusable business and presentation logic are extracted.
- The first slice uses portable `System.IO`; Finder, Quick Look, Trash, security-scoped bookmarks and FSEvents remain separate platform work.
- A macOS build agent and signing/notarization flow are still required before distribution.

## Verification for this decision

- Restore and build `Files.App.MacOS` for `osx-arm64`.
- Cross-publish or build for `osx-x64` and verify that native assets support that RID.
- Run the arm64 build on macOS and exercise home-directory navigation.
- Record compatibility findings before moving existing Files XAML into the spike.
