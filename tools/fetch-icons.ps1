# fetch-icons.ps1 — reproducible epic setup icon bundler.
#
# Downloads a curated PNG per catalog app, preserving aspect ratio (no crop /
# no stretch), downscale-only to 64 px longest side, transparency kept, into
# src/EpicSetup/Assets/Icons/{id}.png.  Resumable: re-run to fill only the
# missing icons (use -Force to refetch all).  Anything still missing falls
# back to the app's runtime Clearbit/favicon chain (correct-by-domain) + chip.
#
#   pwsh tools/fetch-icons.ps1          # fill gaps
#   pwsh tools/fetch-icons.ps1 -Force   # nuke + refetch every icon

param([switch]$Force)
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ErrorActionPreference = 'Stop'

$Root   = Split-Path -Parent $PSScriptRoot
$OutDir = Join-Path $Root 'src\EpicSetup\Assets\Icons'
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
if ($Force) { Get-ChildItem $OutDir -File | Remove-Item -Force }

Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue

$di    = 'https://raw.githubusercontent.com/homarr-labs/dashboard-icons/main/png/'
$di2   = 'https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons/png/'
$ghraw = 'https://raw.githubusercontent.com/'
$wm    = 'https://upload.wikimedia.org/wikipedia/commons/thumb/'

$Map = [ordered]@{
  'chrome'                = @($di+'google-chrome.png', $di+'chrome.png')
  'brave'                 = @($di+'brave.png')
  'waterfox'              = @('https://raw.githubusercontent.com/WaterfoxCo/Waterfox/master/browser/branding/official/default256.png','https://waterfox.net/favicon.ico')
  'vivaldi'               = @($di+'vivaldi.png')
  'helium'                = @('https://helium.computer/favicon.png','https://helium.computer/apple-touch-icon.png')
  'discord'               = @($di+'discord.png')
  'slack'                 = @($di+'slack.png')
  'zoom'                  = @($di+'zoom.png')
  'telegram'              = @($di+'telegram.png')
  'teams'                 = @($di+'microsoft-teams.png', $di+'teams.png')
  'obs-studio'            = @('https://obsproject.com/favicon.ico')
  'mpvnet'                = @()  # no reliable bundled source found -> runtime Clearbit/favicon + letter chip
  'spotify'               = @($di+'spotify.png')
  'audacity'              = @($di+'audacity.png', $ghraw+'audacity/audacity/master/images/audacity.png')
  'blender'               = @($di+'blender.png')
  'yt-dlp'                = @($di+'yt-dlp.png', $di+'youtube-dl.png')
  'ffmpeg'                = @('https://ffmpeg.org/favicon.ico')
  'taiga'                 = @('https://taiga.moe/assets/img/icon/taiga_256px.png','https://taiga.moe/assets/img/icon/taiga_128px.png')
  'medal'                 = @('https://medal.tv/favicon.ico')
  '7-zip'                 = @($di+'7zip.png')
  'powertoys'             = @($ghraw+'microsoft/PowerToys/main/doc/images/icons/PowerToys%20icon/PNG/PowerToysAppList.targetsize-48.png',$di+'microsoft.png')
  'qbittorrent'           = @($di+'qbittorrent.png', $ghraw+'qbittorrent/qBittorrent/master/src/icons/qbittorrent.png')
  'wiztree'               = @('https://diskanalyzer.com/favicon.ico')
  'winrar'                = @('https://www.win-rar.com/favicon.ico','https://win-rar.com/favicon.ico')
  'openrgb'               = @('https://raw.githubusercontent.com/CalcProgrammer1/OpenRGB/master/qt/org.openrgb.OpenRGB.png','https://raw.githubusercontent.com/CalcProgrammer1/OpenRGB/master/qt/OpenRGB.ico','https://openrgb.org/favicon.ico')
  'vscode'                = @($di+'visual-studio-code.png', $di+'vscode.png')
  'cursor'                = @('https://cursor.com/favicon.ico','https://cursor.sh/favicon.ico')
  'antigravity'           = @('https://antigravity.google/favicon.ico','https://www.antigravity.google/favicon.ico')
  'jetbrains-toolbox'     = @($di+'jetbrains-toolbox.png', $di+'jetbrains.png')
  'arduino-ide'           = @($di+'arduino.png', $ghraw+'arduino/arduino-ide/main/arduino-ide/electron/build/icon.png')
  'dotnet-sdk'            = @($di+'dotnet.png')
  'python'                = @($di+'python.png')
  'git'                   = @($di+'git.png')
  'dotnet-desktop-runtime'= @($di+'dotnet.png')
  'temurin-jdk'           = @($di+'java.png')
  'obsidian'              = @($di+'obsidian.png')
  'notion'                = @($di+'notion.png')
  'libreoffice'           = @()  # manual override: dashboard-icons version was black. Provided by user (D:\Downloads\icons own\Libre-Office.png)
  'bitdefender'           = @('https://bitdefender.com/favicon.ico')
  'malwarebytes'          = @('https://malwarebytes.com/favicon.ico')
  'avast'                 = @('https://avast.com/favicon.ico')
  'steam'                 = @($di+'steam.png')
  'epic-games'            = @($di+'epic-games.png')
  'riot-client'           = @()  # manual needed: github avatar + favicon are both black. Provide bright red Riot logo PNG.
  'rockstar-launcher'     = @('https://www.rockstargames.com/favicon.ico',$wm+'a/a9/Rockstar_Games_logo.svg/256px-Rockstar_Games_logo.svg.png','https://rockstargames.com/favicon.ico')
  'prism-launcher'        = @('https://raw.githubusercontent.com/PrismLauncher/PrismLauncher/develop/program_info/PrismLauncher_256.png','https://raw.githubusercontent.com/PrismLauncher/PrismLauncher/develop/program_info/prismlauncher.ico',$ghraw+'PrismLauncher/PrismLauncher/develop/program_info/prismlauncher.png')
  'osu-lazer'             = @($di+'osu.png', $ghraw+'ppy/osu/master/osu.Game/Resources/Textures/Menu/logo.png')
  'msi-afterburner'       = @('https://msi.com/favicon.ico','https://www.msi.com/favicon.ico')
}

function Looks-Like-Image($b) {
  if ($b.Length -lt 60) { return $false }
  $h = $b[0..3]
  if ($h[0] -eq 0x89 -and $h[1] -eq 0x50 -and $h[2] -eq 0x4E -and $h[3] -eq 0x47) { return $true }  # PNG
  if ($h[0] -eq 0x00 -and $h[1] -eq 0x00 -and $h[2] -eq 0x01 -and $h[3] -eq 0x00) { return $true }  # ICO
  if ($h[0] -eq 0xFF -and $h[1] -eq 0xD8 -and $h[2] -eq 0xFF) { return $true }                        # JPEG
  if ($h[0] -eq 0x47 -and $h[1] -eq 0x49 -and $h[2] -eq 0x46) { return $true }                        # GIF
  if ($b[0] -eq 0x42 -and $b[1] -eq 0x4D) { return $true }                                            # BMP
  return $false
}

function Download-With-Retry($url, $tries=3) {
  for ($a=1; $a -le $tries; $a++) {
    $tmp = Join-Path $env:TEMP 'esico_tmp'
    try {
      Remove-Item $tmp -Force -ErrorAction SilentlyContinue
      Invoke-WebRequest -Uri $url -UseBasicParsing -OutFile $tmp -TimeoutSec 15 -ErrorAction Stop
      $b = [IO.File]::ReadAllBytes($tmp)
      if (Looks-Like-Image $b) { return $b }
    } catch {}
    Start-Sleep -Milliseconds 300
  }
  return $null
}

function Load-ImageFromBytes($bytes) {
  $ms = $null
  try {
    $ms = New-Object IO.MemoryStream (,$bytes)
    $img = $null
    try { $img = [System.Drawing.Image]::FromStream($ms, $false, $false) }
    catch { try { $ms.Position = 0; $ico = New-Object System.Drawing.Icon $ms; $img = $ico.ToBitmap(); $ico.Dispose() } catch { return $null } }
    if ($null -eq $img) { return $null }
    $copy = $null
    try {
      $copy = New-Object System.Drawing.Bitmap $img.Width,$img.Height,'Format32bppArgb'
      $g = [System.Drawing.Graphics]::FromImage($copy)
      $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
      $g.DrawImage($img,0,0,$img.Width,$img.Height)
      $g.Dispose()
    } catch {
      try { if ($copy) { $copy.Dispose() }; if ($img -is [System.Drawing.Bitmap]) { $copy = ([System.Drawing.Bitmap]$img).Clone() } else { $copy = New-Object System.Drawing.Bitmap $img } } catch { return $null }
    }
    $img.Dispose()
    return $copy
  } finally { if ($ms) { $ms.Dispose() } }
}

function Normalize-Save($bytes, $out, [int]$Max = 64) {
  $img = Load-ImageFromBytes $bytes
  if ($null -eq $img) { return $null }
  try {
    $w=[int]$img.Width; $h=[int]$img.Height
    if ($w -le 0 -or $h -le 0) { return $null }
    $scale = [Math]::Min(1.0, $Max / [Math]::Max($w,$h))
    $nw = [Math]::Max(1,[int][Math]::Round($w*$scale))
    $nh = [Math]::Max(1,[int][Math]::Round($h*$scale))
    $bmp = New-Object System.Drawing.Bitmap $nw,$nh,'Format32bppArgb'
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    try { $g.DrawImage($img,0,0,$nw,$nh) } catch { $g.Dispose(); $bmp.Dispose(); return $null }
    $g.Dispose()
    $bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return @{w=$w;h=$h;nw=$nw;nh=$nh}
  } finally { $img.Dispose() }
}

$rows = New-Object System.Collections.Generic.List[object]
foreach ($id in $Map.Keys) {
  $out = Join-Path $OutDir ($id+'.png')
  if ((-not $Force) -and (Test-Path $out) -and (Get-Item $out).Length -gt 60) {
    $rows.Add([pscustomobject]@{ App=$id; Result='have'; Src='-'; Orig='-'; New='-'; KB=[math]::Round((Get-Item $out).Length/1KB,1) })
    continue
  }
  $got = $null; $usedUrl = $null
  foreach ($url in $Map[$id]) {
    $b = Download-With-Retry $url 3
    if ($null -eq $b) { continue }
    $r = Normalize-Save $b $out
    if ($null -ne $r) { $got = $r; $usedUrl = $url; break }
  }
  if ($null -ne $got) {
    $rows.Add([pscustomobject]@{ App=$id; Result='OK'; Src=$usedUrl; Orig="$($got.w)x$($got.h)"; New="$($got.nw)x$($got.nh)"; KB=[math]::Round((Get-Item $out).Length/1KB,1) })
  } else {
    if (Test-Path $out) { Remove-Item $out -Force }
    $rows.Add([pscustomobject]@{ App=$id; Result='MISS'; Src='(none)'; Orig='-'; New='-'; KB='-' })
  }
  Start-Sleep -Milliseconds 400   # gentle stagger to dodge jsdelivr burst-throttling
}

$rows | Format-Table -AutoSize | Out-String | Write-Host
$ok = ($rows | Where-Object { $_.Result -in @('OK','have') }).Count
$ms = ($rows | Where-Object { $_.Result -eq 'MISS' }).Count
Write-Host ""
Write-Host "Bundled: $ok   Still missing: $ms   (of $($Map.Count) apps)  - re-run to fill gaps"