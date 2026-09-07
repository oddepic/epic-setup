# epic setup

<p align="left">
  <img alt="release" src="https://img.shields.io/github/v/release/oddepic/epic-setup?include_prereleases&label=release&color=blue">
  <img alt="platform" src="https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-lightgrey">
  <img alt=".net" src="https://img.shields.io/badge/.NET-10.0-purple">
  <img alt="c#" src="https://img.shields.io/badge/C%23-13-green">
  <img alt="ui" src="https://img.shields.io/badge/UI-WPF%20%2B%20MVVM-blueviolet">
</p>

epic setup is a compact Ninite-style installer for Windows. Pick apps from a curated catalog, hit install once, and every entry is acquired through its best available channel: the winget CLI where a verified package ID exists, or a direct vendor/GitHub download as fallback. Silent switches, no telemetry, no banners, no winget dependency for the app itself.

## Install

Requirements: Windows 10/11 x64. The published exe is self-contained. Building from source needs the .NET 10 SDK:

```pwsh
git clone https://github.com/oddepic/epic-setup.git
cd epic-setup

# one-time user-local SDK install, no admin:
& ([scriptblock]::Create((iwr -UseBasicParsing https://dot.net/v1/dotnet-install.ps1).Content)) -Channel '10.0' -InstallDir "$env:LOCALAPPDATA\Microsoft\DotNet"
$env:Path = "$env:LOCALAPPDATA\Microsoft\DotNet;$env:Path"
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\DotNet"

# debug build and tests:
dotnet build src/epic-setup/epic-setup.csproj -c Debug -p:Platform=x64
dotnet test tests/epic-setup.Tests/epic-setup.Tests.csproj -c Debug -p:Platform=x64

# single-file release exe into dist\:
dotnet publish src/epic-setup/epic-setup.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
  -p:SatelliteResourceLanguages=en -o dist
```

Tagged `v*` pushes also produce the release exe through CI (`.github/workflows/release.yml`).

## Usage

```pwsh
tools\run-embedded.cmd   # dev run with the embedded catalog, closes old instances first
.\dist\epic-setup.exe    # published exe, loads the remote catalog first
```

Catalog source overrides:

```pwsh
$env:EPICSETUP_CATALOG_SOURCE = "embedded"        # force embedded catalog
$env:EPICSETUP_CATALOG_OWNER   = "your-user"      # remote catalog override
$env:EPICSETUP_CATALOG_REPO    = "your-fork"
$env:EPICSETUP_CATALOG_BRANCH  = "main"
$env:EPICSETUP_GH_TOKEN        = "<token>"        # higher GitHub API rate limits
```

Add an app by editing [`catalog.json`](./catalog.json). Entries declare an ordered sources chain; verify winget IDs live before adding them (`winget show --id <id> --exact`):

```json
{
  "id": "7-zip",
  "name": "7-Zip",
  "installerType": "Nsis",
  "sources": [
    { "kind": "winget", "id": "7zip.7zip" },
    { "kind": "github", "github": { "repo": "ip7z/7zip", "asset": "^7z[0-9]+-x64\\.exe$" } },
    { "kind": "url", "url": "https://www.7-zip.org/a/7z2408-x64.exe" }
  ]
}
```

Logs land in `%LOCALAPPDATA%\epic-setup\install.log`, plus per-installer logs in `%LOCALAPPDATA%\epic-setup\logs\`.

## How it works

Each catalog entry declares an ordered `sources` chain (`winget` then `github` then `url`). The engine tries sources top-down until one succeeds, so download failures and non-zero installer exits fall through to the next channel instead of failing the install. The log shows which backend served each app. `installerType` and `silentArgs` apply to downloaded artifacts; delegated installs use the package manager's own silent flags.

## Stack

| Piece | Choice |
|---|---|
| UI | WPF with MVVM |
| Runtime | .NET 10, C# 13 |
| Tests | xunit |
| Catalog | `catalog.json` at the repo root |
| Releases | GitHub Actions on `v*` tags |

## Structure

```
src/epic-setup/          WPF app (Models, ViewModels, Views, Services)
tests/epic-setup.Tests/  xunit suite, catalog included as content
tools/                   dev scripts (run-embedded.cmd, verify-catalog.ps1)
catalog.json             curated app catalog with sources chains
prototypes/              UI experiments, not shipped
```

## Requirements

Windows 10/11 x64. The .NET 10 SDK is needed for builds only; the published exe carries its own runtime.

## Troubleshooting

Missing winget is not an error: the fallback channel takes over automatically. For failed installs, attach `%LOCALAPPDATA%\epic-setup\install.log` when opening an issue.

## Contributing

Bugs and requests go in a GitHub issue with the install log attached. Code branches off `main` with `<type>/<description>` names and keeps `dotnet test` green. Catalog additions follow the `sources` schema above with evidence the URL or package ID is correct.

## License

No license file yet. All rights reserved by the repository owner until one is chosen.
