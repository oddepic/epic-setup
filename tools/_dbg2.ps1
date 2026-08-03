[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$di = 'https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons/png/'
$Map = [ordered]@{ 'chrome' = @($di+'chrome.png', $di+'google-chrome.png') }
$expanded = [ordered]@{}
foreach ($k in $Map.Keys) {
  $list = New-Object System.Collections.Generic.List[string]
  foreach ($u in $Map[$k]) {
    if ($u -match '^https://cdn\.jsdelivr\.net/gh/homarr-labs/dashboard-icons/png/(.+)$') {
      $tail = $Matches[1]
      $list.Add("https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons/png/$tail")
      $list.Add("https://fastly.jsdelivr.net/gh/homarr-labs/dashboard-icons/png/$tail")
      $list.Add("https://gcore.jsdelivr.net/gh/homarr-labs/dashboard-icons/png/$tail")
      $list.Add("https://raw.githubusercontent.com/homarr-labs/dashboard-icons/main/png/$tail")
    } else { $list.Add($u) }
  }
  $expanded[$k] = $list
}
foreach ($u in $expanded['chrome']) {
  try { $r = Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec 12 -ErrorAction Stop; $b=$r.RawContentStream.ToArray(); Write-Host ("  200 len={0} sig={1}  {2}" -f $b.Length, "$($b[0]),$($b[1]),$($b[2]),$($b[3])", $u) }
  catch { $sc = if($_.Exception.Response){ [int]$_.Exception.Response.StatusCode.value__ } else { 'ERR' }; Write-Host ("  {0}  {1}" -f $sc, $u) }
}