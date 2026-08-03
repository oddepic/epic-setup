# Epic Setup

Minimal, Ninite-style Windows app installer. Single self-contained `.exe` — admin-elevated, silent installs, Authenticode signature verified.

## Download

Grab `EpicSetup.exe` from the latest [GitHub Release](https://github.com/epic-setup/epic-setup/releases). Runs on Windows 10/11 x64 with zero prerequisites (~59 MB). Expect one SmartScreen warning (unsigned exe) + one UAC prompt.

## Catalog (48 apps)

**Apps** (6 categories) — Browsers · Communication · Media · Utilities · Development · Productivity & Security

**Games** (3 categories) — Launchers · Games · Gaming Tools

Edit [`catalog.json`](./catalog.json) to add apps. 13 entries resolve via GitHub latest releases; the rest use pinned evergreen URLs.

## Build

.NET 10 SDK required (user-local install):

```pwsh
& ([scriptblock]::Create((iwr -UseBasicParsing https://dot.net/v1/dotnet-install.ps1).Content)) -Channel '10.0' -InstallDir "$env:LOCALAPPDATA\Microsoft\DotNet"
dotnet publish src/EpicSetup/EpicSetup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:SatelliteResourceLanguages=en -o dist
```

Git tag → GitHub Actions builds and attaches the exe to the Release.

## Live catalog

The app fetches `catalog.json` from `https://raw.githubusercontent.com/oddepic/epic-setup/main/catalog.json` at startup, so you can update apps (add/edit catalog entries) by pushing to your repo — no new release needed. If the remote fetch fails (offline, repo not yet created), it falls back to the embedded catalog baked into the exe. Override the source with env vars:

```pwsh
setx EPICSETUP_CATALOG_OWNER oddepic
setx EPICSETUP_CATALOG_REPO   epic-setup
setx EPICSETUP_CATALOG_BRANCH main
```

(These are already the defaults — only set them if you fork the repo to a different location.)

## Safety

1. Download over HTTPS to `%TEMP%`
2. `WinVerifyTrust` Authenticode check (unsigned/expired/revoked/tampered → blocked)
3. Publisher subject matched against the pinned signer
4. Silent install via per-type switches (NSIS `/S`, Inno `/VERYSILENT`, MSI `/quiet`)
5. Downloaded file deleted (except portables)

## Built with

**C# 13 / .NET 10** — WPF, `PublishSingleFile`, self-contained single exe.

**CommunityToolkit.Mvvm 8.4.0** — the only NuGet dependency. Everything else is BCL, WPF, and `wintrust.dll` P/Invoke.

**31 bundled app icons** (64 px PNGs from dashboard-icons) + Clearbit/favicon fallback chain.

**JetBrains Mono** bundled as WPF resource.

**GitHub Actions** for release builds on `v*` tags.

**Custom `MasonryPanel`** for balanced multi-column category layout.