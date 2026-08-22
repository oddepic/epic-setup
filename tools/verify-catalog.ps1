<#
.SYNOPSIS
  Headless catalog verifier for epic setup. Resolves every app's download
  source and HEAD-checks that the URL is alive and serves a real file.

.PARAMETER Catalog
  Path to catalog.json (default: repo root).

.PARAMETER Only
  Comma-separated app ids to check (subset for quick testing).

.EXAMPLE
  pwsh tools/verify-catalog.ps1
#>
[CmdletBinding()]
param(
    [string]$Catalog = (Join-Path $PSScriptRoot '..\catalog.json'),
    [string]$Only = ''
)

$ErrorActionPreference = 'Stop'
$token = $env:EPICSETUP_GH_TOKEN

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
    $headers = @{ 'User-Agent' = 'epic-setup'; 'Accept' = 'application/vnd.github+json' }
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

$cat = Get-Content $Catalog -Raw | ConvertFrom-Json
$apps = @($cat.tabs | ForEach-Object { $_.categories } | ForEach-Object { $_.apps })
Write-Verbose "loaded apps=$($apps.Count) from '$Catalog'"
if ($Only) {
    $wanted = @($Only -split ',' | ForEach-Object { $_.Trim() })
    $apps = @($apps | Where-Object { $_.id -in $wanted })
}

$results = @()

foreach ($app in $apps) {
    $row = [ordered]@{ Id = $app.id; Resolve = ''; Http = '' }
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

    if ($skip) { $row.Resolve = 'script (no download)' }
    $results += [pscustomobject]$row
}

$results | Format-Table -AutoSize

$ok = @($results | Where-Object { $_.Resolve -eq 'ok' }).Count
$fail = @($results | Where-Object { $_.Resolve -like 'FAIL*' }).Count
Write-Host "resolve ok=$ok fail=$fail | apps checked=$($results.Count)"
