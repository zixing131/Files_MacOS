# macOS Publishing

The macOS project creates a self-contained, architecture-specific `Files.app` after a Release publish. This flow is isolated from the Windows solution and release pipeline.

## Local ad-hoc packages

```shell
dotnet publish src/Files.App.MacOS/Files.App.MacOS.csproj \
  -f net10.0-desktop \
  -p:Configuration=Release \
  -p:RuntimeIdentifier=osx-arm64

dotnet publish src/Files.App.MacOS/Files.App.MacOS.csproj \
  -f net10.0-desktop \
  -p:Configuration=Release \
  -p:RuntimeIdentifier=osx-x64
```

The bundles are written beside their publish directories:

- `src/Files.App.MacOS/bin/Release/net10.0-desktop/osx-arm64/Files.app`
- `src/Files.App.MacOS/bin/Release/net10.0-desktop/osx-x64/Files.app`

Patch releases use a three-part semantic version such as `0.1.1`, `0.1.2` or `0.1.10`. Update `Version` and `ApplicationDisplayVersion` together, and increase the numeric `ApplicationVersion` for every published build. A release can also override them without editing the project:

```shell
dotnet publish src/Files.App.MacOS/Files.App.MacOS.csproj \
  -f net10.0-desktop \
  -c Release \
  -r osx-arm64 \
  -p:Version=0.1.2 \
  -p:ApplicationDisplayVersion=0.1.2 \
  -p:ApplicationVersion=10102
```

Release bundles omit managed symbols and .NET diagnostic payloads. Keep symbols as separate CI artifacts when crash symbolication is required; do not copy them back into the distributed app.

Local builds use an ad-hoc signature and `Files.AdHoc.entitlements`. The local entitlement disables library validation because separately ad-hoc-signed .NET runtime libraries do not share a Team ID. It must not be used for Developer ID distribution.

## Developer ID packages

CI or a release operator supplies a valid Developer ID Application identity and the strict entitlement file:

```shell
dotnet publish src/Files.App.MacOS/Files.App.MacOS.csproj \
  -f net10.0-desktop \
  -p:Configuration=Release \
  -p:RuntimeIdentifier=osx-arm64 \
  -p:MacOSCodeSignIdentity="Developer ID Application: Example (TEAMID)" \
  -p:MacOSCodeSignTimestampArgument=--timestamp \
  -p:MacOSCodeSignEntitlements="$PWD/src/Files.App.MacOS/Packaging/Files.entitlements"
```

The same command is used with `osx-x64`. Release automation must then package the bundle with `ditto`, submit it with `notarytool`, staple the accepted ticket and run Gatekeeper assessment. Those credentialed steps are intentionally not executed by local builds.

## Verification

```shell
plutil -lint Files.app/Contents/Info.plist
codesign --verify --deep --strict --verbose=4 Files.app
spctl --assess --type execute --verbose=4 Files.app
```

The bundle keeps native code as real files in `Contents/MacOS`. Managed assemblies and other runtime resources live in `Contents/Resources/Runtime`, with relative links that preserve .NET probing behavior without violating macOS nested-code layout rules.
