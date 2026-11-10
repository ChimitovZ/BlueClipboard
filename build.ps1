$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path $compiler)) { throw '.NET Framework 4.8 необходим для сборки.' }
New-Item -ItemType Directory -Force (Join-Path $root 'dist') | Out-Null
$refs = @('System.dll','System.Core.dll','System.Xml.dll','System.Xml.Linq.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Net.Http.dll','System.Runtime.Serialization.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$refs += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','System.Xaml.dll') | ForEach-Object {
    $path = Join-Path $framework ('WPF\' + $_)
    if (-not (Test-Path $path)) { $path = Join-Path $framework $_ }
    '/reference:' + $path
}
$sources = Get-ChildItem (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output "/out:$root\dist\BlueClipboard.exe" "/win32manifest:$root\src\app.manifest" "/resource:$root\src\MainWindow.xaml,MainWindow.xaml" @refs @sources
if ($LASTEXITCODE -ne 0) { throw 'Ошибка компиляции.' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output "/out:$root\dist\BlueClipboard.Update.exe" "/win32manifest:$root\src\app.manifest" "/reference:$framework\System.Windows.Forms.dll" "$root\updater\Program.cs"
if ($LASTEXITCODE -ne 0) { throw 'Ошибка компиляции модуля обновления.' }
Copy-Item (Join-Path $root 'src\App.config') (Join-Path $root 'dist\BlueClipboard.exe.config') -Force
Write-Host 'Готово: dist\BlueClipboard.exe'
