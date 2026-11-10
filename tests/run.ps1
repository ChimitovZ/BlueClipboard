$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $root 'build.ps1')
$process = Start-Process -FilePath (Join-Path $root 'dist\BlueClipboard.exe') -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
Get-Content (Join-Path $root 'artifacts\self-test.txt')
if ($process.ExitCode -ne 0) { throw "Self-tests failed with exit code $($process.ExitCode)" }
$smoke = Start-Process -FilePath (Join-Path $root 'dist\BlueClipboard.exe') -ArgumentList '--smoke-test' -WindowStyle Hidden -Wait -PassThru
$report = Get-Content (Join-Path $root 'artifacts\demo\smoke.txt') -Raw
Write-Host $report
if ($smoke.ExitCode -ne 0 -or -not $report.StartsWith('PASS:')) { throw 'UI startup smoke test failed' }
