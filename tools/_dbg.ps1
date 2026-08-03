[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Drawing

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
  if ($null -eq $img) { return "Load returned null" }
  try {
    $w=[int]$img.Width; $h=[int]$img.Height
    $scale = [Math]::Min(1.0, $Max / [Math]::Max($w,$h))
    $nw = [Math]::Max(1,[int][Math]::Round($w*$scale))
    $nh = [Math]::Max(1,[int][Math]::Round($h*$scale))
    $bmp = New-Object System.Drawing.Bitmap $nw,$nh,'Format32bppArgb'
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    try { $g.DrawImage($img,0,0,$nw,$nh) } catch { $g.Dispose(); $bmp.Dispose(); return "Resize DrawImage threw: $($_.Exception.Message)" }
    $g.Dispose(); $bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return "OK $($w)x$($h)->$($nw)x$($nh)"
  } finally { $img.Dispose() }
}

$tests = @{
  chrome = 'https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons/png/chrome.png'
  osu    = 'https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons/png/osu.png'
  qbitt  = 'https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons/png/qbittorrent.png'
  brave  = 'https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons/png/brave.png'
}
foreach ($k in $tests.Keys) {
  $u = $tests[$k]
  $tmp = Join-Path $env:TEMP "dbg_$k"
  try { Invoke-WebRequest -Uri $u -UseBasicParsing -OutFile $tmp -TimeoutSec 25 -ErrorAction Stop } catch { Write-Host "$k : download FAIL $($_.Exception.Message.Substring(0,[Math]::Min(50,$_.Exception.Message.Length)))"; continue }
  $b = [IO.File]::ReadAllBytes($tmp)
  Write-Host "$k : bytes=$($b.Length) sig=$($b[0]),$($b[1]),$($b[2]),$($b[3])"
  $out = Join-Path $env:TEMP "dbg_$k.png"
  $r = Normalize-Save $b $out
  Write-Host "    -> $r"
  Remove-Item $tmp,$out -Force -EA SilentlyContinue
}