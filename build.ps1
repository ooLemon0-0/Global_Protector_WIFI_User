param([switch]$Test,[string]$OutputDirectory,[string]$VietnamRelayDirectory)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$out=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $root 'dist'}
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
$framework=Split-Path $compiler
foreach($assembly in @('UIAutomationClient.dll','UIAutomationTypes.dll','WindowsBase.dll')){$arguments+=('/reference:'+(Join-Path $framework ('WPF\'+$assembly)))}
$arguments+='/reference:System.Security.dll'
$arguments+=(Join-Path $root 'src\Login.cs')
$arguments+=(Join-Path $root 'src\Updates.cs')
$arguments+=(Join-Path $root 'src\Branding.cs')
$arguments+=(Join-Path $root 'src\VietnamWeb.cs')
$arguments+=(Join-Path $root 'src\VietnamTransfer.cs')
$arguments+=(Join-Path $root 'src\VietnamFileWindow.cs')
if($VietnamRelayDirectory) {
    $relay=(Resolve-Path -LiteralPath $VietnamRelayDirectory).Path
    $bundle=Join-Path $root 'tests\bin\vietnam-build'
    New-Item -ItemType Directory -Path $bundle -Force | Out-Null
    foreach($name in @('Connect-Vietnam.ps1','Start-LocalBackground.ps1','Start-Local.ps1','LocalRelay.exe','RdpWebChannel.dll','session.txt','VietnamWebRelay-Setup.exe')) {
        $file=Join-Path $relay $name
        if(!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing Vietnam relay file: $file" }
        if($name.EndsWith('.ps1')) {
            $normalized=Join-Path $bundle $name
            [IO.File]::WriteAllText($normalized,[IO.File]::ReadAllText($file),[Text.UTF8Encoding]::new($true))
            $file=$normalized
        }
        $resource=if($name -eq 'VietnamWebRelay-Setup.exe'){'VN-Web-Setup-v5.exe'}else{$name}
        $arguments+=('/resource:'+$file+',VietnamWeb.'+$resource)
    }
}
$arguments+=('/win32icon:'+(Join-Path $root 'assets\app.ico'))
$arguments+=('/resource:'+(Join-Path $root 'assets\app.ico')+',App.ico')
$arguments+=('/resource:'+(Join-Path $root 'assets\logo.png')+',Logo.png')
& $compiler @arguments
if($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
Copy-Item (Join-Path $root 'README.md'),(Join-Path $root 'LICENSE') -Destination $out -ErrorAction SilentlyContinue
Get-FileHash $exe -Algorithm SHA256 | ForEach-Object { ($_.Hash.ToLower()+'  GlobalProtectorWiFi.exe') | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ASCII }
if($Test) {
    if($VietnamRelayDirectory) { & (Join-Path $root 'tests\VietnamWebTests.ps1') -ExecutablePath $exe }
    & (Join-Path $root 'tests\LoggingTests.ps1') -ExecutablePath $exe
    & (Join-Path $root 'tests\Run-Tests.ps1')
    $testBin=Join-Path $root 'tests\bin'
    New-Item -ItemType Directory -Path $testBin -Force | Out-Null
    Copy-Item $exe -Destination $testBin -Force
    $testExe=Join-Path $testBin 'InputTests.exe'
    & $compiler /nologo /codepage:65001 /target:exe ('/reference:'+$exe) ('/out:'+$testExe) (Join-Path $root 'tests\InputTests.cs')
    if($LASTEXITCODE -ne 0) { throw 'Frontend test compilation failed.' }
    & $testExe
    if($LASTEXITCODE -ne 0) { throw 'Frontend input tests failed.' }
    $loginTest=Join-Path $testBin 'LoginTests.exe'
    & $compiler /nologo /codepage:65001 /target:exe /reference:System.Web.Extensions.dll ('/reference:'+$exe) ('/out:'+$loginTest) (Join-Path $root 'tests\LoginTests.cs')
    if($LASTEXITCODE -ne 0) { throw 'Login test compilation failed.' }
    & $loginTest
    if($LASTEXITCODE -ne 0) { throw 'Login / credential storage tests failed.' }
    $updateTest=Join-Path $testBin 'UpdateTests.exe'
    & $compiler /nologo /codepage:65001 /target:exe ('/reference:'+$exe) ('/out:'+$updateTest) (Join-Path $root 'tests\UpdateTests.cs')
    if($LASTEXITCODE -ne 0) { throw 'Update test compilation failed.' }
    & $updateTest
    if($LASTEXITCODE -ne 0) { throw 'Update checks failed.' }
    $uiReport=Join-Path $testBin 'ui-state.txt'
    $uiTest=Start-Process -FilePath $exe -ArgumentList @('--ui-self-test',('"'+$uiReport+'"')) -WindowStyle Hidden -PassThru
    if(-not $uiTest.WaitForExit(15000) -or $uiTest.ExitCode -ne 0 -or (Get-Content $uiReport -Raw).Trim() -ne 'passed') { throw 'UI state regression checks failed.' }
    'PASS: main screen states, mandatory login gate, close cancellation, minimize preserving the active session and explicit exit.'
}
Write-Output $exe
