$ErrorActionPreference = 'Stop'
$sessionId = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'session.txt') -Raw).Trim()
if ($sessionId -notmatch '^[a-f0-9]{24}$') { throw 'Invalid relay session ID.' }
$pluginPath = Join-Path $PSScriptRoot 'RdpWebChannel.dll'
$localExe = Join-Path $PSScriptRoot 'LocalRelay.exe'
$addIns = 'HKCU:\Software\Microsoft\Terminal Server Client\Default\AddIns'
$entry = Join-Path $addIns ('Web3000-' + $sessionId)
$mutex = [Threading.Mutex]::new($false, ('Local\VietnamWebRelayLauncher-' + $sessionId))
$owned = $false
try {
    try { $owned = $mutex.WaitOne(2500) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (!$owned) { return }
    if (Get-NetTCPConnection -State Listen -LocalPort 23180 -ErrorAction SilentlyContinue) { throw 'Local port 23180 is already in use.' }
    if (Test-Path -LiteralPath $entry) {
        $registered = Get-ItemProperty -LiteralPath $entry -Name 'Name' -ErrorAction SilentlyContinue
        if ($registered.Name -ne $pluginPath) {
            if (!$registered.Name -or !(Test-Path -LiteralPath $registered.Name -PathType Leaf) -or
                (Get-FileHash -LiteralPath $registered.Name).Hash -ne (Get-FileHash -LiteralPath $pluginPath).Hash) {
                throw 'The RDP add-in name is already in use.'
            }
            Set-ItemProperty -LiteralPath $entry -Name 'Name' -Value $pluginPath
        }
    } else {
        New-Item -Path $addIns -Force | Out-Null
        New-Item -Path $entry | Out-Null
        New-ItemProperty -LiteralPath $entry -Name 'Name' -PropertyType String -Value $pluginPath | Out-Null
    }
    Write-Host 'Waiting for Vietnam Web Relay in the Remote Desktop session.'
    try { & $localExe } finally {
        $registered = Get-ItemProperty -LiteralPath $entry -Name 'Name' -ErrorAction SilentlyContinue
        if ($registered.Name -eq $pluginPath) { Remove-Item -LiteralPath $entry -ErrorAction SilentlyContinue }
    }
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
