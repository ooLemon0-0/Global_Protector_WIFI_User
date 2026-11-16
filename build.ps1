param([switch]$Test)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$out=Join-Path $root 'dist'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not (Test-Path $compiler)) { $compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if(-not (Test-Path $compiler)) { throw 'Windows .NET Framework C# compiler was not found.' }
$engine=Join-Path $root 'src\Engine.ps1'
$tokens=$null; $errors=$null
[void][Management.Automation.Language.Parser]::ParseFile($engine,[ref]$tokens,[ref]$errors)
if($errors.Count) { throw ($errors -join [Environment]::NewLine) }
$exe=Join-Path $out 'GlobalProtectorWiFi.exe'
$arguments=@('/nologo','/target:winexe','/platform:anycpu','/optimize+','/utf8output','/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll',('/win32manifest:'+(Join-Path $root 'src\app.manifest')),('/resource:'+$engine+',Engine.ps1'),('/out:'+$exe),(Join-Path $root 'src\Program.cs'))
$arguments+='/codepage:65001'
& $compiler @arguments
if($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
Copy-Item (Join-Path $root 'README.md'),(Join-Path $root 'LICENSE') -Destination $out -ErrorAction SilentlyContinue
Get-FileHash $exe -Algorithm SHA256 | ForEach-Object { ($_.Hash.ToLower()+'  GlobalProtectorWiFi.exe') | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ASCII }
if($Test) {
    & (Join-Path $root 'tests\Run-Tests.ps1')
    $testBin=Join-Path $root 'tests\bin'
    New-Item -ItemType Directory -Path $testBin -Force | Out-Null
    Copy-Item $exe -Destination $testBin -Force
    $testExe=Join-Path $testBin 'InputTests.exe'
    & $compiler /nologo /codepage:65001 /target:exe ('/reference:'+$exe) ('/out:'+$testExe) (Join-Path $root 'tests\InputTests.cs')
    if($LASTEXITCODE -ne 0) { throw 'Frontend test compilation failed.' }
    & $testExe
    if($LASTEXITCODE -ne 0) { throw 'Frontend input tests failed.' }
    $uiReport=Join-Path $testBin 'ui-state.txt'
    $uiTest=Start-Process -FilePath $exe -ArgumentList @('--ui-self-test',('"'+$uiReport+'"')) -WindowStyle Hidden -PassThru
    if(-not $uiTest.WaitForExit(15000) -or $uiTest.ExitCode -ne 0 -or (Get-Content $uiReport -Raw).Trim() -ne 'passed') { throw 'UI state regression checks failed.' }
    'PASS: main screen has no input fields; start, wait, stop and recovery states are consistent.'
}
Write-Output $exe
