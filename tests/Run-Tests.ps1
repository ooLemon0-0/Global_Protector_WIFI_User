$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\src\Engine.ps1') -Mode Library
function Assert([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Reset-Fixture {
    $script:Target='192.0.2.10'; $script:AllowedDns='8.8.8.8'
    $script:FakeVpn=[pscustomobject]@{Name='VPN';InterfaceDescription='PANGP Virtual Ethernet Adapter';ifIndex=3;InterfaceGuid='{11111111-1111-1111-1111-111111111111}';Status='Up'}
    $script:FakeLocal=[pscustomobject]@{Name='WLAN';InterfaceDescription='Wi-Fi';ifIndex=6;InterfaceGuid='wifi-guid';Status='Up';HardwareInterface=$true}
    $script:FakeAddress='10.8.0.2'
    $script:Routes=@(
        [pscustomobject]@{DestinationPrefix='0.0.0.0/0';NextHop='10.20.0.1';InterfaceIndex=6;RouteMetric=0},
        [pscustomobject]@{DestinationPrefix='0.0.0.0/0';NextHop='0.0.0.0';InterfaceIndex=3;RouteMetric=0},
        [pscustomobject]@{DestinationPrefix='8.8.8.8/32';NextHop='0.0.0.0';InterfaceIndex=3;RouteMetric=0},
        [pscustomobject]@{DestinationPrefix='10.8.0.1/32';NextHop='0.0.0.0';InterfaceIndex=3;RouteMetric=0}
    )
    $script:FakeDns=@('10.20.0.53','10.20.0.54','127.0.0.1','10.8.0.1','8.8.8.8')
    $script:RegistryDns=$script:FakeDns -join ','; $script:RegistryDnsExists=$true
    $script:Writes=@(); $script:FailDnsOnce=$false; $script:FailResolve=$false; $script:InjectPrefix=$false
    $script:Fixture=[pscustomobject]@{
        VpnIndex=3;VpnGuid='{11111111-1111-1111-1111-111111111111}';VpnAddress='10.8.0.2';VpnNextHop='0.0.0.0';LocalIndex=6
        DnsBefore=@($script:FakeDns);Targets=@($script:Target);AddedTargets=@($script:Target)
        DnsRegistryBefore=[pscustomobject]@{NameServerExists=$true;NameServer=$script:RegistryDns}
        RemovedRoutes=@($script:Routes | Where-Object { $_.InterfaceIndex -eq 3 -and $_.DestinationPrefix -in @('0.0.0.0/0','10.8.0.1/32') } | Select-Object DestinationPrefix,NextHop,RouteMetric)
    }
}
function Get-NetAdapter { param([int]$InterfaceIndex) if ($PSBoundParameters.ContainsKey('InterfaceIndex')) { @($script:FakeVpn,$script:FakeLocal) | Where-Object ifIndex -eq $InterfaceIndex } else { $script:FakeVpn; $script:FakeLocal } }
function Get-NetIPAddress { param($InterfaceIndex,$AddressFamily) [pscustomobject]@{IPAddress=$script:FakeAddress} }
function Get-NetRoute { param([int]$InterfaceIndex,$AddressFamily,$PolicyStore) if ($PSBoundParameters.ContainsKey('InterfaceIndex')) { @($script:Routes | Where-Object InterfaceIndex -eq $InterfaceIndex) } else { $script:Routes } }
function New-NetRoute {
    param($DestinationPrefix,[int]$InterfaceIndex,$NextHop,[int]$RouteMetric,$PolicyStore,$AddressFamily)
    Assert ($PolicyStore -eq 'ActiveStore') 'A route would have been made persistent.'
    Assert ($InterfaceIndex -eq 3) 'Unexpected route write to a physical adapter.'
    $script:Writes+=('add '+$DestinationPrefix)
    $script:Routes+=[pscustomobject]@{DestinationPrefix=$DestinationPrefix;InterfaceIndex=$InterfaceIndex;NextHop=$NextHop;RouteMetric=$RouteMetric}
}
function Remove-NetRoute {
    [CmdletBinding(SupportsShouldProcess=$true)]param($DestinationPrefix,[int]$InterfaceIndex,$NextHop,$PolicyStore,$AddressFamily)
    Assert ($InterfaceIndex -eq 3 -and $PolicyStore -eq 'ActiveStore') 'Unexpected route removal scope.'
    $script:Writes+=('remove '+$DestinationPrefix)
    $script:Routes=@($script:Routes | Where-Object { -not ($_.DestinationPrefix -eq $DestinationPrefix -and $_.InterfaceIndex -eq $InterfaceIndex -and $_.NextHop -eq $NextHop) })
}
function Get-DnsClientServerAddress {
    param([int]$InterfaceIndex,$AddressFamily)
    if ($script:FakeVpn.Status -ne 'Up') { throw 'No matching CIM DNS object for disabled VPN adapter.' }
    [pscustomobject]@{InterfaceIndex=$InterfaceIndex;ServerAddresses=@($script:FakeDns)}
}
function Get-ItemProperty {
    param($LiteralPath)
    Assert ($LiteralPath -like '*{11111111-1111-1111-1111-111111111111}') 'Read unexpected registry path.'
    if ($script:RegistryDnsExists) { [pscustomobject]@{NameServer=$script:RegistryDns} } else { [pscustomobject]@{} }
}
function Set-ItemProperty {
    param($LiteralPath,$Name,$Value)
    Assert ($LiteralPath -like '*{11111111-1111-1111-1111-111111111111}' -and $Name -eq 'NameServer') 'Wrote unexpected registry value.'
    $script:Writes+='restore-raw-dns'; $script:RegistryDnsExists=$true
    $script:RegistryDns=[string]$Value; $script:FakeDns=@($script:RegistryDns -split ',')
}
function Remove-ItemProperty {
    param($LiteralPath,$Name)
    Assert ($LiteralPath -like '*{11111111-1111-1111-1111-111111111111}' -and $Name -eq 'NameServer') 'Removed unexpected registry value.'
    $script:Writes+='restore-automatic-dns'; $script:RegistryDnsExists=$false; $script:RegistryDns=''
}
function Set-DnsClientServerAddress {
    param($InputObject,[string[]]$ServerAddresses,[switch]$ResetServerAddresses)
    Assert ($InputObject.InterfaceIndex -eq 3) 'Unexpected DNS write to a physical adapter.'
    $script:Writes+='dns'
    if ($script:FailDnsOnce) { $script:FailDnsOnce=$false; throw 'Injected DNS write failure' }
    $script:FakeDns=@($ServerAddresses)
    if ($script:InjectPrefix) { $script:FakeDns=@('10.20.0.53','10.20.0.54','127.0.0.1')+@($ServerAddresses) }
    $script:RegistryDnsExists=$true; $script:RegistryDns=$script:FakeDns -join ','
}
function Clear-DnsClientCache { }
function Write-Event { param($Event,$Data) }
function Find-NetRoute {
    param($RemoteIPAddress)
    $hostRoute=@($script:Routes | Where-Object DestinationPrefix -eq ($RemoteIPAddress+'/32'))
    if ($hostRoute.Count -gt 0) { $hostRoute[0]; return }
    $defaults=@($script:Routes | Where-Object DestinationPrefix -eq '0.0.0.0/0' | Sort-Object InterfaceIndex)
    if ($defaults.Count -gt 0) { $defaults[0] }
}
function Test-Tcp { param($Address,$Port) return $true }
function Resolve-DnsName {
    [CmdletBinding()]param($Name,$Type,[switch]$DnsOnly,[switch]$QuickTimeout)
    if ($script:FailResolve) { throw 'Injected resolver failure' }
    [pscustomobject]@{IPAddress='36.152.44.93'}
}

Reset-Fixture
$physicalBefore=$script:Routes[0] | ConvertTo-Json -Compress
Apply-Split $script:Fixture
Assert (@($script:Routes | Where-Object { $_.InterfaceIndex -eq 3 -and $_.DestinationPrefix -eq '0.0.0.0/0' }).Count -eq 0) 'VPN default route survived apply.'
Assert (@($script:Routes | Where-Object DestinationPrefix -eq '10.8.0.1/32').Count -eq 0) 'Unavailable DNS route survived apply.'
Assert (@($script:Routes | Where-Object DestinationPrefix -eq '8.8.8.8/32').Count -eq 1) 'Allowed VPN DNS route was lost.'
Assert (@($script:Routes | Where-Object DestinationPrefix -eq '192.0.2.10/32').Count -eq 1) 'RDP host route missing.'
Assert (($script:FakeDns -join ',') -eq '8.8.8.8') 'VPN DNS not scoped to tested server.'
Assert ((Test-ConnectionResult $script:Fixture).Success) 'Happy-path verification failed.'
Assert (($script:Routes | Where-Object InterfaceIndex -eq 6 | ConvertTo-Json -Compress) -eq $physicalBefore) 'Physical route changed.'
'PASS: targeted split keeps physical route and allowed DNS; verification selects correct interfaces.'

Restore-Split $script:Fixture
Assert (@($script:Routes | Where-Object { $_.InterfaceIndex -eq 3 -and $_.DestinationPrefix -eq '0.0.0.0/0' }).Count -eq 1) 'Default not restored.'
Assert (@($script:Routes | Where-Object DestinationPrefix -eq '192.0.2.10/32').Count -eq 0) 'Temporary host route survived rollback.'
Assert (($script:FakeDns -join ',') -eq ($script:Fixture.DnsBefore -join ',')) 'DNS not restored.'
'PASS: rollback restores original VPN defaults and DNS and removes temporary host route.'

Reset-Fixture
$script:FailDnsOnce=$true
try { Apply-Split $script:Fixture; throw 'Expected injected failure.' } catch { Assert ($_.Exception.Message -eq 'Injected DNS write failure') 'Wrong failure during apply.' }
Restore-Split $script:Fixture
Assert (@($script:Routes | Where-Object { $_.InterfaceIndex -eq 3 -and $_.DestinationPrefix -eq '0.0.0.0/0' }).Count -eq 1) 'Partial apply not rolled back.'
Assert (($script:FakeDns -join ',') -eq ($script:Fixture.DnsBefore -join ',')) 'Partial-apply DNS rollback failed.'
'PASS: rollback recovers partial application when a DNS write fails.'

Reset-Fixture
Apply-Split $script:Fixture
$script:FakeVpn.Status='Disabled'
$script:Routes=@($script:Routes | Where-Object InterfaceIndex -ne 3)
Restore-Split $script:Fixture
Assert (@($script:Routes | Where-Object InterfaceIndex -eq 3).Count -eq 0) 'Rollback recreated routes after VPN disconnected.'
'PASS: disconnect cleanup does not recreate stale VPN routes.'

Reset-Fixture
$script:Fixture.AddedTargets=@()
$script:Routes+=[pscustomobject]@{DestinationPrefix='192.0.2.10/32';InterfaceIndex=3;NextHop='0.0.0.0';RouteMetric=7}
Apply-Split $script:Fixture
Restore-Split $script:Fixture
Assert (@($script:Routes | Where-Object { $_.DestinationPrefix -eq '192.0.2.10/32' -and $_.RouteMetric -eq 7 }).Count -eq 1) 'Pre-existing host route was changed.'
'PASS: existing user host route is preserved.'

Reset-Fixture
$script:FakeVpn.InterfaceGuid='different-guid'
$blocked=$false
try { Apply-Split $script:Fixture } catch { $blocked=$true }
Assert ($blocked -and $script:Writes.Count -eq 0) 'Identity mismatch allowed changes.'
'PASS: adapter identity mismatch blocks every mutation.'

Reset-Fixture
Apply-Split $script:Fixture
$script:FailResolve=$true
Assert (-not (Test-ConnectionResult $script:Fixture).Success) 'Broken DNS was accepted as success.'
Restore-Split $script:Fixture
'PASS: verification rejects functioning routes with broken DNS.'

Reset-Fixture
$script:FakeAddress='10.8.0.3'
$blocked=$false
try { Restore-Split $script:Fixture } catch { $blocked=$true }
Assert ($blocked -and $script:Writes.Count -eq 0) 'Restore changed a different live VPN session.'
'PASS: recovery does not modify a different live VPN session.'

Reset-Fixture
$script:InjectPrefix=$true
Apply-Split $script:Fixture
Restore-Split $script:Fixture
Assert (Test-DnsEquivalent @($script:RegistryDns) @($script:Fixture.DnsRegistryBefore.NameServer)) 'DNS server addresses were not restored during rollback.'
'PASS: DNS rollback verifies restored server addresses despite duplicate prefixes.'

Reset-Fixture
$script:InjectPrefix=$true
Apply-Split $script:Fixture
$observation=Get-VpnDnsObservation $script:Fixture
Assert ($observation.EffectiveServers.Count -gt 1) 'Did not reproduce effective DNS list drift.'
$health=Test-ConnectionResult $script:Fixture
Assert ($health.Success -and -not (Get-HealthDecision $health 0).Rollback) 'Working DNS list drift triggered rollback.'
'PASS: a working DNS list with additional addresses no longer triggers rollback.'

Reset-Fixture
Apply-Split $script:Fixture
$health=Test-ConnectionResult $script:Fixture
$health.SystemDnsWorking=$false
$first=Get-HealthDecision $health 0
Assert (-not $first.Rollback -and $first.Failures -eq 1) 'Transient resolver failure caused immediate rollback.'
$health.SystemDnsWorking=$true
$recovered=Get-HealthDecision $health $first.Failures
Assert (-not $recovered.Rollback -and $recovered.Failures -eq 0) 'Recovery did not reset consecutive failure count.'
'PASS: transient DNS failures are tolerated and successful recovery resets the counter.'

$health.SystemDnsWorking=$false
$first=Get-HealthDecision $health 0
$second=Get-HealthDecision $health $first.Failures
$third=Get-HealthDecision $health $second.Failures
Assert (-not $first.Rollback -and -not $second.Rollback -and $third.Rollback) 'Persistent DNS failure did not roll back on the third failure.'
'PASS: persistent internet/DNS failure still triggers rollback after three checks.'

$health.SystemDnsWorking=$true; $health.RdpPortReachable=$false
Assert (-not (Get-HealthDecision $health 0).Rollback) 'An offline RDP server caused unrelated internet rollback.'
'PASS: an offline remote server does not cause unrelated internet rollback.'

$health.InternetViaOriginalAdapter=$false
Assert ((Get-HealthDecision $health 0).Rollback) 'A wrong internet route was not rejected immediately.'
'PASS: unexpected routing still triggers immediate rollback.'

Reset-Fixture
$script:Fixture.DnsRegistryBefore=[pscustomobject]@{NameServerExists=$false;NameServer=''}
$script:Fixture.DnsBefore=@()
Apply-Split $script:Fixture
$script:FakeVpn.Status='Disabled'
Restore-Split $script:Fixture
Assert (-not $script:RegistryDnsExists) 'Originally automatic DNS was replaced by static DNS during recovery.'
'PASS: recovery restores originally absent static DNS on disabled adapters.'

Reset-Fixture
$script:Fixture.PSObject.Properties.Remove('DnsRegistryBefore')
Apply-Split $script:Fixture
$script:FakeVpn.Status='Disabled'
$script:RegistryDns='10.20.0.53,10.20.0.54,127.0.0.1,'+($script:Fixture.DnsBefore -join ',')
Restore-Split $script:Fixture
Assert (Test-DnsEquivalent @($script:RegistryDns) @($script:Fixture.DnsBefore)) 'Version 1 state did not recover its DNS list.'
'PASS: version 1 state recovery accepts equivalent lists without a CIM DNS object.'

Reset-Fixture
$script:Fixture | Add-Member -NotePropertyName Stage -NotePropertyValue 'restored'
$script:FakeVpn.Status='Disabled'
$script:RegistryDns='10.20.0.53,10.20.0.54,127.0.0.1,'+($script:Fixture.DnsBefore -join ',')
$script:Routes=@($script:Routes | Where-Object InterfaceIndex -eq 6)
$script:StatePath=Join-Path $PSScriptRoot 'test-state.json'
$script:Fixture | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:StatePath -Encoding UTF8
$startup=Get-StartContext
Assert ($startup.BaselineRoute.InterfaceIndex -eq 6 -and $script:Writes.Count -eq 0) 'Restored legacy state triggered network writes during startup.'
'PASS: startup with restored legacy DNS duplicates performs no network or registry writes.'

Reset-Fixture
$script:Fixture | Add-Member -NotePropertyName Stage -NotePropertyValue 'restored'
$script:FakeVpn.Status='Disabled'
$script:RegistryDns='9.9.9.9,'+($script:Fixture.DnsBefore -join ',')
$script:Routes=@($script:Routes | Where-Object InterfaceIndex -eq 6)
$script:Fixture | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:StatePath -Encoding UTF8
$startup=Get-StartContext
Assert ($startup.BaselineRoute.InterfaceIndex -eq 6 -and $script:Writes.Count -eq 0) 'Startup rewrote unrelated DNS settings.'
'PASS: startup preserves unrelated DNS customizations instead of forcing an old snapshot.'

$script:Fixture.Stage='applying'
$script:Fixture | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:StatePath -Encoding UTF8
$blocked=$false
try { $null=Get-StartContext } catch { $blocked=$true }
Assert ($blocked -and $script:Writes.Count -eq 0) 'Startup ignored an incomplete prior transaction.'
'PASS: incomplete previous changes still block startup until restored.'

Assert (Test-DnsEquivalent @('10.20.0.53,10.20.0.54,10.20.0.53;8.8.8.8') @('10.20.0.53','10.20.0.54','8.8.8.8')) 'Equivalent duplicate DNS representations were rejected.'
'PASS: duplicate addresses and common separators are normalized for DNS recovery.'

Assert (-not (Test-DnsEquivalent @('8.8.8.8','10.20.0.53') @('10.20.0.53','8.8.8.8'))) 'A changed DNS priority was hidden by normalization.'
'PASS: normalization still detects changed DNS priority.'

Reset-Fixture
$script:RegistryDns=$script:Fixture.DnsRegistryBefore.NameServer
$script:FakeDns=@('8.8.8.8')
Restore-VpnDns $script:Fixture $script:FakeVpn
Assert (($script:FakeDns -join ',') -eq ($script:Fixture.DnsBefore -join ',')) 'Configured and effective DNS disagreement was not recovered.'
Assert ($script:Writes -contains 'dns') 'Effective DNS needed restoration but no supported DNS API write occurred.'
'PASS: DNS recovery checks effective addresses as well as registry representation.'
'ALL 22 OFFLINE BEHAVIOR CHECKS PASSED. No real network commands were executed.'

Assert ((@(Parse-Targets '192.0.2.10,192.0.2.11')).Count -eq 2) 'Multi-target parsing failed.'
foreach ($bad in @('0.0.0.0','127.0.0.1','224.0.0.1','192.0.2.1;Write-Host','::1','1.2.3.999')) {
    $blocked=$false; try { $null=Parse-Targets $bad } catch { $blocked=$true }; Assert $blocked ('Unsafe target accepted: '+$bad)
}
Reset-Fixture
$script:Fixture.Targets=@('192.0.2.10','192.0.2.11'); $script:Fixture.AddedTargets=@('192.0.2.10','192.0.2.11')
Apply-Split $script:Fixture
Assert (@($script:Routes | Where-Object DestinationPrefix -eq '192.0.2.11/32').Count -eq 1) 'Second host route missing.'
Restore-Split $script:Fixture
Assert (@($script:Routes | Where-Object DestinationPrefix -like '192.0.2.*/32').Count -eq 0) 'Multi-target rollback incomplete.'
'PASS: multi-target routes, rollback and backend input validation.'

Reset-Fixture
$script:Fixture | Add-Member -NotePropertyName Stage -NotePropertyValue 'restored'
$script:Fixture | Add-Member -NotePropertyName PhysicalBaseline -NotePropertyValue ([pscustomobject]@{InterfaceIndex=6;InterfaceGuid='wifi-guid';NextHop='10.20.0.1'})
$script:Fixture | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $script:StatePath -Encoding UTF8
$script:Resume=$true
$context=Get-StartContext
Assert ($context.BaselineRoute.InterfaceIndex -eq 6) 'Fast reconnect lost the validated physical baseline.'
$script:Resume=$false
$blocked=$false; try{$null=Get-StartContext}catch{$blocked=$true}
Assert $blocked 'A fresh start incorrectly accepted an already connected VPN.'
'PASS: fast reconnect reuses a verified physical route; fresh starts still require VPN disconnection.'

Reset-Fixture
for($cycle=0;$cycle -lt 3;$cycle++) {
    Apply-Split $script:Fixture
    Assert (@($script:Routes | Where-Object DestinationPrefix -eq '192.0.2.10/32').Count -eq 1) 'Repeated connection created duplicate routes.'
    Restore-Split $script:Fixture
    Assert (@($script:Routes | Where-Object DestinationPrefix -eq '192.0.2.10/32').Count -eq 0) 'Repeated disconnection leaked a host route.'
}
'PASS: three connect/disconnect cycles restore without duplicate or leaked routes.'
