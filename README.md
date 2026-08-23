# epic setup

<p align="left">
  <img alt="release" src="https://img.shields.io/github/v/release/oddepic/epic-setup?include_prereleases&label=release&color=blue">
  <img alt="platform" src="https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-lightgrey">
  <img alt=".net" src="https://img.shields.io/badge/.NET-10.0-purple">
  <img alt="c#" src="https://img.shields.io/badge/C%23-13-green">
  <img alt="ui" src="https://img.shields.io/badge/UI-WPF%20%2B%20MVVM-blueviolet">
</p>

epic setup is a compact Ninite-style installer for Windows. Pick apps from a
curated catalog, hit install once, and every entry is acquired through its best
available channel: the winget CLI where a verified package ID exists, or a
direct vendor/GitHub download as fallback. Silent switches, no telemetry, no
banners, no winget dependency for the app itself.

## Key features

* One-click batch installs with silent switches per installer type (MSI,
  NSIS, Inno Setup, Burn, generic EXE, portable, ZIP, script)
* Multi-backend acquisition: entries declare an ordered `sources` chain
  (`winget` → `github` → `url`) and the engine falls back automatically when a
  source fails
* winget integration without winget dependency: package-manager installs are
  delegated through `winget --exact --silent`, but the app never requires it;
  missing winget just means the fallback channel takes over
* Live fallback proof: download failures and non-zero installer exits move to
  the next declared source; the log shows which backend served each app
* 46-app curated catalog across Apps and Games tabs with icons, sizes, and
  per-app timeouts
* Self-contained single-file exe, no runtime install needed

## Preview

> Screenshot placeholder: main window with category tabs, app grid, and backend
> log pane. Replace with a GIF of a batch install when available.

```text
[ screenshot coming soon ]
```

## Prerequisites & installation

Requirements:

* Windows 10/11 x64
* .NET 10 SDK (user-local install works fine)

Clone and set up:

```pwsh
git clone https://github.com/oddepic/epic-setup.git
cd epic-setup

# one-time SDK install (user-local, no admin):
& ([scriptblock]::Create((iwr -UseBasicParsing https://dot.net/v1/dotnet-install.ps1).Content)) -Channel '10.0' -InstallDir "$env:LOCALAPPDATA\Microsoft\DotNet"
$env:Path = "$env:LOCALAPPDATA\Microsoft\DotNet;$env:Path"
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\DotNet"
```

Build:

```pwsh
# debug build:
dotnet build src/epic-setup/epic-setup.csproj -c Debug -p:Platform=x64

# tests:
dotnet test tests/epic-setup.Tests/epic-setup.Tests.csproj -c Debug -p:Platform=x64

# single-file release exe into dist\:
dotnet publish src/epic-setup/epic-setup.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
  -p:SatelliteResourceLanguages=en -o dist
```

## Usage guide

Run the built exe with the catalog embedded at compile time (recommended while
developing; avoids pulling a stale remote catalog):

```pwsh
tools\run-embedded.cmd   # closes old instances, sets EPICSETUP_EMBEDDED_CATALOG=1
```

Run the published exe directly:

```pwsh
.\dist\epic-setup.exe    # loads the remote catalog from GitHub first
```

Catalog source overrides:

```pwsh
$env:EPICSETUP_CATALOG_SOURCE = "embedded"        # force embedded catalog
$env:EPICSETUP_CATALOG_OWNER   = "your-user"      # remote catalog override
$env:EPICSETUP_CATALOG_REPO    = "your-fork"
$env:EPICSETUP_CATALOG_BRANCH  = "main"
$env:EPICSETUP_GH_TOKEN        = "<token>"        # higher GitHub API rate limits
```

Add an app by editing [`catalog.json`](./catalog.json). Entries declare an
ordered sources chain; verify winget IDs live before adding them
(`winget show --id <id> --exact`):

```json
{
  "id": "7-zip",
  "name": "7-Zip",
  "description": "File archiver",
  "homepage": "https://www.7-zip.org/",
  "installerType": "Nsis",
  "sources": [
    { "kind": "winget", "id": "7zip.7zip" },
    { "kind": "github", "github": { "repo": "ip7z/7zip", "asset": "^7z[0-9]+-x64\\.exe$" } },
    { "kind": "url", "url": "https://www.7-zip.org/a/7z2408-x64.exe" }
  ]
}
```

The engine tries sources top-down until one succeeds. `installerType` and
`silentArgs` apply to downloaded artifacts; delegated installs use the package
manager's own silent flags.

Logs land in `%LOCALAPPDATA%\epic-setup\install.log` plus per-installer logs in
`%LOCALAPPDATA%\epic-setup\logs\`.

## Roadmap

* [x] Per-entry multi-backend acquisition with automatic fallback
* [x] winget CLI backend, IDs verified live against the catalog
* [ ] Publish tracking issue + migrate remaining edge-case entries (avast)
* [ ] scoop backend (portable/CLI tool coverage)
* [ ] chocolatey backend (opt-in)
* [ ] Bootstrap flow for missing package managers, gated behind explicit user consent
* [ ] Security rework: artifact trust decisions layered on top of working downloads

## Contributing

Issues and PRs both welcome.

* Bugs and requests: open a GitHub issue with the install log attached
  (`%LOCALAPPDATA%\epic-setup\install.log`)
* Code: branch from `main` using `<type>/<description>` names (`feat/…`,
  `fix/…`, `chore/…`), keep PRs focused, and make sure `dotnet test` passes
* Catalog additions: follow the `sources` schema above and include evidence the
  download URL or package ID is correct

## 📄 License

No license yet. All rights reserved by the repository owner until one is chosen.
