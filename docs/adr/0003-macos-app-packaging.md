# ADR 0003: Package the macOS Port as a Native App Bundle

## Status

Accepted for the technical preview.

## Context

The Uno desktop publish output is a Mach-O executable accompanied by managed assemblies and native libraries. macOS launch services, icon registration, document declarations, signing and notarization require the standard `Files.app/Contents` bundle layout instead of a loose executable directory. A framework-dependent hardened-runtime bundle cannot safely load an independently signed system .NET runtime because library validation requires compatible signing identities.

The file manager needs broad filesystem access and must browse paths that are not selected through a document picker. The technical preview is therefore distributed outside the Mac App Store and is not App Sandbox-enabled. The .NET runtime requires JIT-related hardened-runtime entitlements.

## Decision

- Build self-contained, architecture-specific `osx-arm64` and `osx-x64` Release publish outputs.
- Create `Files.app` after `dotnet publish` and place all runtime files plus `libFilesMacOSBridge.dylib` in `Contents/MacOS`.
- Generate an `.icns` from the existing Files brand asset and place it in `Contents/Resources`.
- Declare the bundle identity, minimum macOS version, utility category and folder-viewer role in `Info.plist`.
- Sign the embedded .NET/Uno native libraries before signing the application bundle.
- Default local builds to an ad-hoc hardened-runtime signature. Allow CI to override the signing identity and timestamp arguments for Developer ID distribution.
- Use a local-only ad-hoc entitlement that disables library validation because ad-hoc nested signatures have no common Team ID. Developer ID builds use the strict entitlement file and keep library validation enabled.
- Do not enable App Sandbox for the technical preview.

## Consequences

The project now produces a launch-services-compatible application bundle without changing the Windows project or release pipeline. Developer ID signing, stapled notarization, update delivery and universal-binary assembly remain release-engineering gates rather than local build defaults.
