# epic setup

epic setup is a compact, Ninite-style Windows app installer. It presents a
catalog of curated applications, lets users select and review them, then
downloads and runs their installers with installer-specific silent switches.
It has no telemetry, advertising, or winget dependency.

The current application targets Windows 10/11 x64 and is distributed as a
self-contained single-file executable. The main UI requests administrator
access through UAC.

## Project Structure

```text
catalog.json                 # App catalog and installer metadata
src/epic-setup/
  Models/                    # Catalog and domain models
  Services/                  # Catalog, download, release, icon, and install services
  ViewModels/                # MVVM application state
  Windows/                   # Main WPF window
  Controls/                  # Custom layout controls
  Themes/                   # Dark-theme XAML styles
  Assets/Icons/             # Bundled application icons
  Fonts/                   # Bundled JetBrains Mono fonts
tools/
  fetch-icons.ps1            # Icon download and normalization tool
  verify-catalog.ps1         # Catalog source and URL checks
  run-embedded.cmd           # Launches the executable with embedded catalog data
```

## Built With

- **C# 13** for application and service code
- **.NET 10** with `net10.0-windows`
- **WPF** and **XAML** for the desktop interface
- **CommunityToolkit.Mvvm 8.4.0** for MVVM support; the only NuGet dependency
- **PowerShell 7** for build-time tooling and catalog checks
- **JSON** for the application catalog and **MSBuild/XML** for project configuration
- **PNG** icons and bundled **JetBrains Mono** fonts
- **GitHub Actions** for release builds on `v*` tags

## Catalog

The catalog currently contains 48 apps organized into Apps and Games tabs.
Edit [`catalog.json`](./catalog.json) to add or update entries. Apps can use a
static vendor URL or a GitHub release asset, and support installer types such
as MSI, NSIS, Inno Setup, Burn, generic EXE, portable, ZIP, and script-based
installers.

At startup, the application loads the catalog from the remote GitHub copy,
then falls back to a local cache and finally to the catalog embedded in the
executable. The source can be overridden with `EPICSETUP_CATALOG_OWNER`,
`EPICSETUP_CATALOG_REPO`, `EPICSETUP_CATALOG_BRANCH`, or
`EPICSETUP_CATALOG_SOURCE=embedded`.

## Build

Install the .NET 10 SDK, then run:

```pwsh
dotnet build src/epic-setup/epic-setup.csproj -c Debug -p:Platform=x64

dotnet publish src/epic-setup/epic-setup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:SatelliteResourceLanguages=en -o dist
```

Use `tools\run-embedded.cmd` to test the catalog compiled into the executable.
GitHub Actions builds and attaches the executable to releases created from
`v*` tags.

## Current Security Status

The previous Authenticode, publisher, and SHA-256 verification layer was
removed as part of an ongoing security redesign. The current engine downloads
and runs installers without an install-time security gate, and the remote
catalog is not authenticated or integrity-pinned. This build must not be
treated as the final secure distribution model.

The executable is currently unsigned, so Windows SmartScreen may show a
warning on first launch.
