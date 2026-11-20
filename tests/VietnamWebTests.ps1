param([Parameter(Mandatory)][string]$ExecutablePath)
$ErrorActionPreference='Stop'
function Assert([bool]$Condition,[string]$Message) { if (!$Condition) { throw $Message } }

$assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ExecutablePath))
$scope=$assembly.GetType('GlobalProtectorWiFi.VietnamWebSession').GetMethod('AppliesTo',[Reflection.BindingFlags]'Static,NonPublic')
Assert ($scope.Invoke($null,@('https://113.160.245.25','113.160.245.25','10.213.134.31'))) 'The Vietnam VPN and target were not selected.'
Assert (!$scope.Invoke($null,@('113.160.245.25','10.1.1.1','10.213.134.31'))) 'The relay would start for a different active portal.'
Assert (!$scope.Invoke($null,@('113.160.245.25','','192.0.2.10'))) 'The relay would start without the Vietnam RDP route.'

$source=$assembly.GetManifestResourceStream('VietnamWeb.Connect-Vietnam.ps1')
Assert ($null -ne $source) 'The optional launcher was not packaged.'
foreach ($name in @('Start-LocalBackground.ps1','Start-Local.ps1','LocalRelay.exe','RdpWebChannel.dll','session.txt','VN-Web-Setup-v5.exe')) {
    Assert ($null -ne $assembly.GetManifestResourceInfo('VietnamWeb.'+$name)) "Missing bundled component $name."
}
$file=Join-Path $env:TEMP ('VietnamWebTest-'+[guid]::NewGuid().ToString('N')+'.ps1')
try {
    $output=[IO.File]::Create($file)
    try { $source.CopyTo($output) } finally { $output.Dispose(); $source.Dispose() }
    . $file -Mode Library
    function Find-LocalRelay { if ($script:relayRunning) { [pscustomobject]@{Id=123} } }
    function Start-VietnamLocalRelay { $script:relayRunning=$true; $script:steps+='relay' }
    function Start-VietnamRemoteDesktop { $script:steps+='rdp' }
    function Find-ConnectedRemoteDesktop { if ($script:desktopConnected) { [pscustomobject]@{Id=456} } }
    function Test-RemoteDesktopRelayPlugin { return $script:pluginLoaded }
    function Test-WebRelay([int]$RelayId) { Assert ($RelayId -eq 123) 'An unrelated relay port was checked.'; return $script:ready }
    function Open-VietnamDesktop([string]$File) { Assert ($script:ready -and $File -eq 'http://127.0.0.1:23180/') 'Opened a browser before the local relay was ready.'; $script:steps+='browser' }
    function Get-Process { param([int]$Id); if ($Id -eq 123) { [pscustomobject]@{Id=$Id} } }
    function Start-Sleep { param([int]$Seconds); $script:ready=$true; $script:steps+='wait' }

    $script:steps=@(); $script:relayRunning=$false; $script:ready=$false; $script:desktopConnected=$false
    Invoke-VietnamWeb $true | Out-Null
    Assert (($script:steps -join ',') -eq 'relay,rdp,wait,browser') 'A new session was not started in the right order.'

    $script:steps=@(); $script:ready=$false; $script:desktopConnected=$true; $script:pluginLoaded=$false
    $result=Invoke-VietnamWeb $true
    Assert (!$script:steps.Count -and $result.Contains('打开越南网页')) 'An existing connection without the add-in was silently reused or replaced.'

    $script:steps=@(); $script:ready=$false; $script:pluginLoaded=$true
    Invoke-VietnamWeb $true | Out-Null
    Assert (($script:steps -join ',') -eq 'wait,browser') 'An existing RDP session should be reused.'

    $script:steps=@()
    Invoke-VietnamWeb $true | Out-Null
    Assert (($script:steps -join ',') -eq 'browser') 'An active mapping should open without a second RDP window.'

    function Find-StartingVietnamRemoteDesktop { if ($script:desktopStarting) { [pscustomobject]@{Id=789} } }
    function Test-VietnamRemoteDesktopReachable { return $script:reachable }
    $script:steps=@(); $script:relayRunning=$false; $script:desktopStarting=$false; $script:reachable=$true
    Invoke-VietnamTransferReconnect | Out-Null
    Assert (!$script:steps.Count) 'Upload recovery replaced an existing RDP session.'
    $script:desktopConnected=$false; $script:desktopStarting=$true
    Invoke-VietnamTransferReconnect | Out-Null
    Assert (!$script:steps.Count) 'Upload recovery opened a duplicate RDP login.'
    $script:desktopStarting=$false; $script:reachable=$false
    Invoke-VietnamTransferReconnect | Out-Null
    Assert (!$script:steps.Count) 'Upload recovery repeatedly launched RDP with no route.'
    $script:reachable=$true
    Invoke-VietnamTransferReconnect | Out-Null
    Assert (($script:steps -join ',') -eq 'relay,rdp') 'Upload recovery did not restart the relay and reopen the lost desktop.'
    Write-Output 'PASS: VPN scope, packaged relay, browser readiness and upload recovery without duplicate RDP windows.'
} finally { Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
