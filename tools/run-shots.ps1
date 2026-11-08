[CmdletBinding(PositionalBinding=$false)]
param(
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Recipes,
    [switch]$NoBuild,
    [switch]$Baseline,
    [int]$Timeout = 600,
    [double]$Threshold = 6.0
)
# Build GEE, run recipes in the game, post-process screenshots. See tools/README.md.
$ErrorActionPreference = 'Stop'
$tools   = $PSScriptRoot
$modDir  = Split-Path $tools -Parent
$modsDir = Split-Path $modDir -Parent
$exe     = Join-Path $modsDir 'StarMap\StarMap.exe'
$shots   = Join-Path $modDir 'shots'
$staging = Join-Path $tools '.staging'
$recDir  = Join-Path $tools 'recipes'

if (-not $Recipes -or $Recipes.Count -eq 0) { $Recipes = @('smoke') }
$Recipes = @($Recipes | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$recipeFiles = foreach ($r in $Recipes) {
    $p = Join-Path $recDir ($r -replace '\.txt$', '' )
    $p += '.txt'
    if (-not (Test-Path $p)) { throw "unknown recipe '$r' (have: $((Get-ChildItem $recDir -Filter *.txt).BaseName -join ', '))" }
    $p
}

function Stop-Game {
    Get-Process StarMap -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill() } catch {}
    }
    Get-Process -Name StarMap -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $t = 0
    while ((Get-Process StarMap -ErrorAction SilentlyContinue) -and $t -lt 20) { Start-Sleep -Milliseconds 500; $t++ }
}

# 1. build into staging (the running game does not lock staging, so this can overlap a game run)
if (-not $NoBuild) {
    New-Item -ItemType Directory -Force $staging | Out-Null
    $out = $staging.TrimEnd('\') + '/'
    foreach ($proj in @('src\net\GEE.Net.csproj', 'src\GEE.csproj')) {
        Write-Host "build $proj"
        $log = & dotnet build (Join-Path $modDir $proj) -c Release "-p:OutDir=$out" -nologo -v q 2>&1
        if ($LASTEXITCODE -ne 0) {
            $log | Select-String -Pattern 'error' | Select-Object -First 15 | ForEach-Object { Write-Host $_.Line }
            throw "build failed: $proj"
        }
    }
}

# 2. game must be closed before the DLL copy
Stop-Game
if (-not $NoBuild) {
    foreach ($f in 'GEE.dll', 'GEE.pdb', 'GEE.Net.dll', 'GEE.Net.pdb') {
        $src = Join-Path $staging $f
        if (Test-Path $src) { Copy-Item $src (Join-Path $modDir $f) -Force }
    }
}

# 3. script + clean shots
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('#autoquit')
foreach ($p in $recipeFiles) {
    foreach ($l in Get-Content $p) {
        $t = $l.Trim()
        if ($t -eq '' -or $t -eq 'quit' -or $t -eq '#autoquit') { continue }
        $lines.Add($t)
    }
}
$lines.Add('quit')
New-Item -ItemType Directory -Force $shots | Out-Null
Get-ChildItem $shots -File | Remove-Item -Force
[IO.File]::WriteAllLines((Join-Path $modDir 'autoshot.txt'), $lines, (New-Object Text.UTF8Encoding($false)))
Write-Host "script: $($lines.Count) lines from $($Recipes -join ', ')"

# 4. run
$logDir = Join-Path $env:USERPROFILE 'Documents\My Games\Kitten Space Agency\Logs'
$started = Get-Date
$proc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
$done = Join-Path $shots 'DONE'
$how = 'timeout'
while (((Get-Date) - $started).TotalSeconds -lt $Timeout) {
    if (Test-Path $done) { $how = 'DONE'; break }
    if ($proc.HasExited) { $how = 'process exit'; break }
    Start-Sleep -Seconds 2
}
if ($how -eq 'DONE') {
    # give the game a few seconds to quit by itself
    $proc.WaitForExit(8000) | Out-Null
}
Stop-Game
Write-Host ("finished via {0} after {1:N0} s" -f $how, ((Get-Date) - $started).TotalSeconds)

# 5. post-process
$py = Get-Command python -ErrorAction SilentlyContinue
if (-not $py) { $py = Get-Command py -ErrorAction SilentlyContinue }
if ($py) {
    $pargs = @((Join-Path $tools 'postprocess.py'), '--shots', $shots, '--baseline', (Join-Path $tools 'baseline'),
               '--threshold', $Threshold, '--logdir', $logDir, '--since', $started.ToString('o'))
    if ($Baseline) { $pargs += '--promote' }
    & $py.Source @pargs
} else {
    Write-Host 'python not found; skipping post-processing'
}

