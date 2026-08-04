<#
.SYNOPSIS
  Headless catalog verifier for Epic Setup. Resolves every app's download
  source, checks the URL, and (optionally) downloads + Authenticode-verifies
  installers and writes SHA-256 pins for unsigned apps.

.PARAMETER Catalog
  Path to catalog.json (default: repo root).

.PARAMETER Full
  Download every app and run the Authenticode + publisher check (slow, ~2 GB).

.PARAMETER PinUnsigned
  Download only the `unverified` apps, compute SHA-256, and write the pins
  back into catalog.json so the engine can install them.

.PARAMETER Only
  Comma-separated app ids to check (subset for quick testing).

.EXAMPLE
  # resolve + HEAD-check every URL (fast)
  pwsh tools/verify-catalog.ps1

  # full signature verification of every app
  pwsh tools/verify-catalog.ps1 -Full

  # pin hashes for unsigned apps
  pwsh tools/verify-catalog.ps1 -PinUnsigned
#>
[CmdletBinding()]
param(
    [string]$Catalog = (Join-Path $PSScriptRoot '..\catalog.json'),
    [switch]$Full,
    [switch]$PinUnsigned,
    [string]$Only = ''
)

$ErrorActionPreference = 'Stop'
$token = $env:EPICSETUP_GH_TOKEN
$tmp = Join-Path $env:TEMP 'EpicSetup-verify'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
Add-Type -AssemblyName System.Security

function Retry([scriptblock]$action, [int]$tries = 4) {
    for ($i = 1; $i -le $tries; $i++) {
        try { return & $action }
        catch {
            if ($i -eq $tries) { throw }
            Start-Sleep -Seconds ([Math]::Min(2 * $i, 8))
        }
    }
}

function Invoke-GhApi([string]$url) {
    $headers = @{ 'User-Agent' = 'EpicSetup'; 'Accept' = 'application/vnd.github+json' }
    if ($token) { $headers['Authorization'] = "Bearer $token" }
    Retry { Invoke-RestMethod -Uri $url -Headers $headers }
}

function Resolve-GithubAsset([pscustomobject]$g) {
    $releases = Invoke-GhApi "https://api.github.com/repos/$($g.repo)/releases/latest"
    if ($g.useLatestPrerelease) {
        $all = Invoke-GhApi "https://api.github.com/repos/$($g.repo)/releases"
        foreach ($rel in $all) {
            $m = @($rel.assets | Where-Object { $_.name -match $g.asset })
            if ($m.Count -ge 1) { return $m[0].browser_download_url, $m[0].name }
        }
        throw "No pre-release asset matched '$($g.asset)'"
    }
    $m = @($releases.assets | Where-Object { $_.name -match $g.asset })
    if ($m.Count -eq 0) { throw "No asset matched '$($g.asset)' in $($g.repo)" }
    if ($m.Count -gt 1) { throw "AMBIGUOUS: $($m.name -join ', ')" }
    return $m[0].browser_download_url, $m[0].name
}

function Test-Url([string]$url) {
    try {
        $r = Retry { Invoke-WebRequest -Uri $url -Method Get -Headers @{ Range = 'bytes=0-0' } -UseBasicParsing -MaximumRedirection 8 }
        $len = -1L
        try {
            $cr = $r.Headers['Content-Range']
            if ($cr -is [array]) { $cr = $cr[0] }
            if ($cr -and $cr -match '/(\d+)\s*$') { $len = [long]$matches[1] }
            else {
                $cl = $r.Headers['Content-Length']
                if ($cl -is [array]) { $cl = $cl[0] }
                if ($cl) { $len = [long]$cl }
            }
        } catch { $len = -1 }
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Length = $len; Error = '' }
    } catch {
        return [pscustomobject]@{ Status = -1; Length = -1; Error = $_.Exception.Message }
    }
}

function Get-Download([string]$url, [string]$dest) {
    Retry {
        Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing -MaximumRedirection 8
    }
    $size = (Get-Item $dest).Length
    if ($size -eq 0) { throw "Empty download: $url" }
    return $size
}

function Get-Signer([string]$path) {
    $sig = Get-AuthenticodeSignature -FilePath $path
    $cn = ''; $o = ''
    if ($sig.SignerCertificate) {
        $cn = $sig.SignerCertificate.GetNameInfo('SimpleName', $false)
        $decoded = $sig.SignerCertificate.SubjectName.Decode(
            [System.Security.Cryptography.X509Certificates.X500DistinguishedNameFlags]::UseNewLines)
        $line = ($decoded -split "`r?`n" | Where-Object { $_ -match '^O=' } | Select-Object -First 1)
        if ($line) { $o = $line.Substring(2).Trim().Trim('"') }
    }
    return [pscustomobject]@{
        Status = $sig.Status.ToString()
        Cn     = $cn
        O      = $o
    }
}

function Test-Publisher([string]$cn, [string]$o, [string]$expected) {
    if ([string]::IsNullOrWhiteSpace($expected)) { return 'n/a' }
    if ($cn -and $cn.IndexOf($expected, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return 'ok' }
    if ($o  -and $o.IndexOf($expected,  [StringComparison]::OrdinalIgnoreCase) -ge 0) { return 'ok' }
    return 'MISMATCH'
}

function Get-Sha256([string]$path) {
    return (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$cat = Get-Content $Catalog -Raw | ConvertFrom-Json
$apps = @($cat.tabs | ForEach-Object { $_.categories } | ForEach-Object { $_.apps })
Write-Verbose "loaded apps=$($apps.Count) from '$Catalog'"
if ($Only) {
    $wanted = @($Only -split ',' | ForEach-Object { $_.Trim() })
    $apps = @($apps | Where-Object { $_.id -in $wanted })
}

$results = @()
$pinUpdates = @()

foreach ($app in $apps) {
    $row = [ordered]@{ Id = $app.id; Resolve = ''; Http = ''; Signed = ''; Signer = ''; Publisher = ''; Sha256 = '' }
    $skip = @('Script', 'WingetUpdate') -contains $app.installerType

    $url = $null
    if (-not $skip) {
        try {
            if ($app.github -and $app.github.repo) {
                $resolved = Resolve-GithubAsset $app.github
                $url = $resolved[0]
            }
            elseif ($app.url) { $url = $app.url }
            else { throw "No source" }
            $row.Resolve = 'ok'
        } catch {
            $row.Resolve = "FAIL: $($_.Exception.Message)"
            $results += [pscustomobject]$row
            continue
        }

        $probe = Test-Url $url
        $row.Http = if ($probe.Status -eq 206 -or $probe.Status -eq 200) {
            "OK ($(if ($probe.Length -ge 0) { '{0:N0}' -f $probe.Length + ' B' } else { '?' }))"
        } else { "FAIL $($probe.Status) $($probe.Error)" }
    }

    $needDownload = $Full -or ($PinUnsigned -and $app.unverified -and -not $skip)
    if ($needDownload -and $url) {
        $dest = Join-Path $tmp "$($app.id)-verify.bin"
        try {
            $size = Get-Download $url $dest
            $sig = Get-Signer $dest
            $row.Signed = $sig.Status
            $row.Signer = if ($sig.Cn) { "$($sig.Cn)" } elseif ($sig.O) { $sig.O } else { '' }
            if ($sig.Status -eq 'Valid') {
                $row.Publisher = Test-Publisher $sig.Cn $sig.O $app.publisher
            } else {
                $row.Publisher = '(unsigned)'
            }
            $row.Sha256 = Get-Sha256 $dest
            if ($PinUnsigned -and $app.unverified) {
                $pinUpdates += @{ app = $app; sha = $row.Sha256 }
            }
        } catch {
            $row.Signed = "DL FAIL: $($_.Exception.Message)"
        }
        Remove-Item $dest -Force -ErrorAction SilentlyContinue
    }

    if ($skip) { $row.Resolve = 'script (no download)' }
    $results += [pscustomobject]$row
}

$results | Format-Table -AutoSize

$ok = @($results | Where-Object { $_.Resolve -eq 'ok' }).Count
$fail = @($results | Where-Object { $_.Resolve -like 'FAIL*' }).Count
$signed = @($results | Where-Object { $_.Signed -eq 'Valid' }).Count
$unsigned = @($results | Where-Object { $_.Signed -eq 'NotSigned' }).Count
Write-Host "resolve ok=$ok fail=$fail | signed(Valid)=$signed unsigned(NotSigned)=$unsigned | apps checked=$($results.Count)"

if ($PinUnsigned -and $pinUpdates.Count -gt 0) {
    Write-Host "`nWriting SHA-256 pins for $($pinUpdates.Count) unsigned apps into $Catalog ..."
    foreach ($u in $pinUpdates) {
        $u.app | Add-Member -NotePropertyName sha256 -NotePropertyValue $u.sha -Force
    }
    $cat | ConvertTo-Json -Depth 20 | Set-Content -Path $Catalog -Encoding UTF8
    Write-Host "Done."
}
