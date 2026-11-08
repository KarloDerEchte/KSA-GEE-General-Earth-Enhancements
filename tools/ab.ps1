# A/B perf runs: powershell -File tools/ab.ps1 -Out result.txt name='{"Setting":false}' ... ; restores settings.json after each run.
param([string]$Out, [Parameter(ValueFromRemainingArguments=$true)][string[]]$Cases)
$m = Split-Path $PSScriptRoot -Parent; $f = Join-Path $m 'settings.json'
foreach ($c in $Cases) {
  $name, $json = $c -split '=', 2
  $orig = Get-Content $f -Raw
  $s = $orig | ConvertFrom-Json
  foreach ($kv in ($json | ConvertFrom-Json).PSObject.Properties) { $s | Add-Member -NotePropertyName $kv.Name -NotePropertyValue $kv.Value -Force }
  $s | ConvertTo-Json -Depth 5 | Set-Content $f -Encoding utf8
  $before = Get-ChildItem (Join-Path $m 'profiles') -Filter '*_low.txt' | ForEach-Object Name
  try { & powershell -NoProfile -File (Join-Path $m 'tools/run-shots.ps1') ascent_low -NoBuild -Timeout 400 | Out-Null } finally { [IO.File]::WriteAllText($f, $orig) }
  $r = Get-ChildItem (Join-Path $m 'profiles') -Filter '*_low.txt' | Where-Object { $before -notcontains $_.Name } | Select-Object -Last 1
  if ($r) { $l = Get-Content $r.FullName | Where-Object { $_ -match '^(100 m|500 m|2 km)' }; Add-Content $Out ("${name}: " + (($l | ForEach-Object { ($_ -split '\s{2,}') -join ' ' }) -join ' | ')) }
  else { Add-Content $Out "${name}: NO REPORT" }
}
