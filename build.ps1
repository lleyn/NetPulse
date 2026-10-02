param([switch]$Test)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 compiler was not found.' }
$destination = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$references = @('System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Xaml.dll')
$compilerArgs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', '/warn:4', '/codepage:65001', "/out:$destination\NetPulse.exe", "/win32manifest:$PSScriptRoot\app.manifest", "/resource:$PSScriptRoot\src\Widget.xaml,NetPulse.Widget.xaml")
foreach ($reference in $references) { $compilerArgs += "/reference:$framework\$reference" }
$compilerArgs += "/reference:$framework\WPF\PresentationCore.dll"
$compilerArgs += "/reference:$framework\WPF\WindowsBase.dll"
$compilerArgs += "/reference:$framework\WPF\PresentationFramework.dll"
$compilerArgs += (Get-ChildItem -LiteralPath "$PSScriptRoot\src" -Filter '*.cs').FullName
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath "$PSScriptRoot\NetPulse.exe.config" -Destination $destination -Force
Copy-Item -LiteralPath "$PSScriptRoot\README.txt" -Destination $destination -Force
Write-Output "Built: $destination\NetPulse.exe"
if ($Test) {
    $testDir = Join-Path $PSScriptRoot 'test-output'
    New-Item -ItemType Directory -Force -Path $testDir | Out-Null
    & $compiler /nologo /target:exe /optimize+ /codepage:65001 /utf8output "/out:$testDir\NetPulse.Tests.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll "$PSScriptRoot\src\NetworkMonitor.cs" "$PSScriptRoot\src\Settings.cs" "$PSScriptRoot\tests\Tests.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & "$testDir\NetPulse.Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $visualArgs = @('/nologo', '/target:exe', '/codepage:65001', '/utf8output', "/out:$testDir\NetPulse.VisualTests.exe")
    foreach ($reference in $references) { $visualArgs += "/reference:$framework\$reference" }
    foreach ($reference in @('PresentationCore.dll', 'PresentationFramework.dll', 'WindowsBase.dll')) { $visualArgs += "/reference:$framework\WPF\$reference" }
    $visualArgs += "$PSScriptRoot\tests\VisualTests.cs"
    & $compiler @visualArgs
    if ($LASTEXITCODE -ne 0) { throw 'Visual test build failed.' }
    & "$testDir\NetPulse.VisualTests.exe" "$destination\NetPulse.exe" $testDir
    if ($LASTEXITCODE -ne 0) { throw 'Visual tests failed.' }
}
