param([string]$Iscc = "$PSScriptRoot\artifacts\tools\InnoSetup\ISCC.exe")
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Iscc)) { throw 'Укажите путь к ISCC.exe через -Iscc.' }
& "$PSScriptRoot\build.ps1"
$version = (Get-Item -LiteralPath "$PSScriptRoot\dist\BlueClipboard.exe").VersionInfo.ProductVersion
$version = ([version]$version).ToString(3)
& $Iscc "/DAppVersion=$version" "$PSScriptRoot\installer\BlueClipboard.iss"
if ($LASTEXITCODE -ne 0) { throw 'Ошибка сборки установщика.' }
Get-FileHash -LiteralPath "$PSScriptRoot\dist\BlueClipboard-Setup-x64.exe" -Algorithm SHA256
