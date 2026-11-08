<#
  Runs the off-game GEE tests (tests/GEE.Tests) in a few seconds, without KSA.
  Build output, test results and the freshly built GEE.dll (used by the satellite packing tests) go to -Out, never into the mod folder.
  Usage:  powershell -File tests\run-tests.ps1 [-Out <dir>] [-Filter <dotnet test --filter expression>]
#>
param(
    [string]$Out = $(if ($env:GEE_TEST_OUT) { $env:GEE_TEST_OUT } else { Join-Path $env:TEMP 'GEE.Tests' }),
    [string]$Filter = ''
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'; $env:DOTNET_NOLOGO = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$clock = [Diagnostics.Stopwatch]::StartNew()
$tests = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path (Split-Path -Parent $tests) 'src'
New-Item -ItemType Directory -Force $Out | Out-Null
$env:GEE_TEST_OUT = $Out

# 1. Build the mod source to $Out\gee (the installed GEE.dll may be older than src). Only when a source file is newer.
$gee = Join-Path $Out 'gee\GEE.dll'
$newest = (Get-ChildItem $src -Filter *.cs -Recurse | Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
if (-not (Test-Path $gee) -or (Get-Item $gee).LastWriteTime -lt $newest) {
    Write-Host 'building src/GEE.csproj to the output folder ...'
    & dotnet build (Join-Path $src 'GEE.csproj') -c Release --no-restore -v q --nologo "-p:OutDir=$Out\gee\" "-p:IntermediateOutputPath=$Out\geeobj\" | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning 'src build failed; satellite packing tests fall back to the installed GEE.dll' }
}
if (Test-Path $gee) { $env:GEE_TEST_DLL = $gee }

# 2. Run the tests.
$results = Join-Path $Out 'results'
Remove-Item -Recurse -Force $results -ErrorAction SilentlyContinue
$testArgs = @('test', (Join-Path $tests 'GEE.Tests\GEE.Tests.csproj'), '--nologo', '-v', 'q', '--logger', 'trx;LogFileName=results.trx', '--results-directory', $results)
if ($Filter) { $testArgs += @('--filter', $Filter) }
$ErrorActionPreference = "Continue"
$log = & dotnet @testArgs 2>&1
$ErrorActionPreference = "Stop"
$trx = Join-Path $results 'results.trx'
if (-not (Test-Path $trx)) { $log | Write-Host; Write-Host 'FAILED TO BUILD/RUN TESTS' -ForegroundColor Red; exit 2 }

# 3. Summary.
[xml]$x = Get-Content $trx
$ns = New-Object Xml.XmlNamespaceManager($x.NameTable); $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
$c = $x.SelectSingleNode('//t:ResultSummary/t:Counters', $ns)
$failed = @($x.SelectNodes('//t:UnitTestResult[@outcome="Failed"]', $ns))
foreach ($f in $failed) {
    Write-Host ("FAIL  " + $f.testName) -ForegroundColor Red
    $msg = $f.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $ns); if ($msg) { Write-Host ('      ' + ($msg.InnerText -replace '\s+', ' ')) }
}
foreach ($s in @($x.SelectNodes('//t:UnitTestResult[@outcome="NotExecuted"]', $ns))) { Write-Host ("SKIP  " + $s.testName) -ForegroundColor Yellow }
$color = if ($c.failed -eq 0) { 'Green' } else { 'Red' }
Write-Host ("tests: {0} total, {1} passed, {2} failed, {3} skipped   ({4:N1} s)" -f $c.total, $c.passed, $c.failed, ($c.total - $c.executed), $clock.Elapsed.TotalSeconds) -ForegroundColor $color
exit ([int]($c.failed -gt 0))
