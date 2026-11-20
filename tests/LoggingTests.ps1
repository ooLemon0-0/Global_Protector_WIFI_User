param([string]$ExecutablePath)
$ErrorActionPreference='Stop'
function Assert([bool]$Condition,[string]$Message) { if(-not $Condition) { throw $Message } }
$testRoot=Join-Path $PSScriptRoot ('bin\logging-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$enginePath=Join-Path $PSScriptRoot '..\src\Engine.ps1'
if($ExecutablePath) {
    $assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes([IO.Path]::GetFullPath($ExecutablePath)))
    $resource=$assembly.GetManifestResourceStream('Engine.ps1')
    Assert ($null -ne $resource) 'Built executable has no embedded engine.'
    $reader=[IO.StreamReader]::new($resource,[Text.Encoding]::UTF8)
    try { $enginePath=Join-Path $testRoot 'EmbeddedEngine.ps1';[IO.File]::WriteAllText($enginePath,$reader.ReadToEnd(),[Text.Encoding]::UTF8) }
    finally { $reader.Dispose() }
}
# Library mode defines the real packaged functions without inspecting or changing
# the machine's adapters, routes, DNS, credentials or saved application state.
. $enginePath -Mode Library -DataDirectory $testRoot
$originalOut=[Console]::Out
$capture=[IO.StringWriter]::new()
$handle=$null
try {
    [Console]::SetOut($capture)
    $script:LogPath=Join-Path $testRoot 'shared.jsonl'
    [IO.File]::WriteAllText($script:LogPath,'')
    $handle=[IO.File]::Open($script:LogPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $oldWriterBlocked=$false
    try { Add-Content -LiteralPath $script:LogPath -Value 'old writer' -Encoding UTF8 }
    catch { $oldWriterBlocked=$true }
    if($PSVersionTable.PSVersion.Major -eq 5) { Assert $oldWriterBlocked 'Did not reproduce the original PowerShell 5.1 sharing conflict.' }
    for($i=0;$i -lt 100;$i++) { Write-Event 'HEALTH' @{Index=$i;Text='中文日志';Success=$true} }
    $handle.Dispose();$handle=$null
    $rows=@(Get-Content -LiteralPath $script:LogPath -Encoding UTF8 | Where-Object {$_ -ne 'old writer'} | ForEach-Object {$_ | ConvertFrom-Json})
    Assert ($rows.Count -eq 100 -and $rows[99].Data.Index -eq 99 -and $rows[99].Data.Text -eq '中文日志') 'Shared log reader blocked, corrupted or truncated an event.'
    Assert ($capture.ToString() -notmatch 'LOG_WARNING') 'Compatible sharing produced a log warning.'

    [void]$capture.GetStringBuilder().Clear()
    $handle=[IO.File]::Open($script:LogPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    Write-Event 'HEALTH' @{Success=$true;Locked=$true}
    $consoleRows=@($capture.ToString() -split '\r?\n' | Where-Object {$_} | ForEach-Object {$_ | ConvertFrom-Json})
    Assert ($consoleRows.Count -eq 2 -and $consoleRows[0].Event -eq 'HEALTH' -and $consoleRows[1].Event -eq 'LOG_WARNING') 'Exclusive lock lost the original console event or did not produce a nonfatal warning.'
    $handle.Dispose();$handle=$null

    [void]$capture.GetStringBuilder().Clear()
    Write-Event 'HEALTH' @{Success=$true;Recovered=$true}
    Assert ($capture.ToString() -notmatch 'LOG_WARNING') 'File logging did not recover after lock release.'
    $last=(Get-Content -LiteralPath $script:LogPath -Tail 1 -Encoding UTF8) | ConvertFrom-Json
    Assert ($last.Data.Recovered -eq $true) 'Logging required an engine restart after lock release.'

    [void]$capture.GetStringBuilder().Clear()
    $validLog=$script:LogPath
    $script:LogPath=Join-Path $testRoot 'missing-directory\events.jsonl'
    Write-Event 'READY' @{Success=$true}
    $consoleRows=@($capture.ToString() -split '\r?\n' | Where-Object {$_} | ForEach-Object {$_ | ConvertFrom-Json})
    Assert ($consoleRows.Count -eq 2 -and $consoleRows[0].Event -eq 'READY' -and $consoleRows[1].Event -eq 'LOG_WARNING') 'Unwritable destination interrupted engine events.'

    $closedOut=[IO.StringWriter]::new();$closedOut.Dispose();[Console]::SetOut($closedOut)
    $script:LogPath=$validLog
    Write-Event 'HEALTH' @{Success=$true;ClosedConsole=$true}
    $last=(Get-Content -LiteralPath $validLog -Tail 1 -Encoding UTF8) | ConvertFrom-Json
    Assert ($last.Data.ClosedConsole -eq $true) 'Closed console prevented file logging.'
    $script:LogPath=Join-Path $testRoot 'missing-directory\events.jsonl'
    Write-Event 'HEALTH' @{Success=$true;BothOutputsUnavailable=$true}
} finally {
    if($null -ne $handle) { $handle.Dispose() }
    [Console]::SetOut($originalOut)
    $capture.Dispose()
}
'PASS: original Windows PowerShell 5.1 reader conflict reproduced; 100 shared-reader events remain valid UTF-8 JSON.'
'PASS: exclusive lock and unwritable destination preserve console events without interrupting the engine.'
'PASS: file logging resumes after lock release; closed console and simultaneous output failures do not escape logging.'
@{PowerShell=$PSVersionTable.PSVersion.ToString();PackagedEngine=[bool]$ExecutablePath;Passed=$true;SharedReaderEvents=100} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'result.json') -Encoding UTF8
