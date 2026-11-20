$ErrorActionPreference = 'Stop'
$runtime = Join-Path $PSScriptRoot 'runtime'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
$localExe = Join-Path $PSScriptRoot 'LocalRelay.exe'
if (Get-Process -Name LocalRelay -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $localExe }) { return }
$starter = Join-Path $PSScriptRoot 'Start-Local.ps1'
$shell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$arguments = @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',('"'+$starter+'"'))
$process = Start-Process -FilePath $shell -ArgumentList $arguments -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $runtime 'local.log') -RedirectStandardError (Join-Path $runtime 'local.err')
$deadline = [DateTime]::UtcNow.AddSeconds(8)
do {
    if ($process.WaitForExit(200)) { throw ('Local relay failed to start. See ' + (Join-Path $runtime 'local.err')) }
    if (Get-Process -Name LocalRelay -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $localExe }) { return }
} while ([DateTime]::UtcNow -lt $deadline)
throw ('Timed out waiting for the local relay. See ' + (Join-Path $runtime 'local.err'))
