[CmdletBinding()]
param(
    [ValidateSet('Start','Restore','Check','Library')][string]$Mode='Start',
    [string]$DataDirectory,
    [string]$Targets='',
    [string]$PreferredDns='',
    [switch]$Resume
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$script:Target=''
$script:TargetList=@()
$script:AllowedDns=''
if (-not $DataDirectory) { $DataDirectory=Join-Path $env:LOCALAPPDATA 'GlobalProtectorWiFi' }
$script:Runtime=$DataDirectory
$script:StatePath=Join-Path $script:Runtime 'state.json'
$script:StopPath=Join-Path $script:Runtime 'stop.request'
$script:LogPath=Join-Path $script:Runtime 'events.jsonl'
$script:ToolVersion='0.1.2'

function Write-Event([string]$Event,$Data) {
    $line=[ordered]@{Time=(Get-Date).ToString('o');Event=$Event;Data=$Data} | ConvertTo-Json -Depth 9 -Compress
    Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8
    [Console]::Out.WriteLine($line)
}
function Parse-Targets([string]$Text) {
    $result=@($Text -split '[,;\s]+' | Where-Object { $_ } | Select-Object -Unique)
    if ($result.Count -lt 1 -or $result.Count -gt 16) { throw '请填写 1～16 个远程电脑的 IPv4 地址。' }
    foreach ($item in $result) {
        if ($item -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { throw ('无效的 IPv4 地址：'+$item) }
        $ip=$null
        if (-not [Net.IPAddress]::TryParse($item,[ref]$ip) -or $ip.AddressFamily -ne 'InterNetwork') { throw ('无效的 IPv4 地址：'+$item) }
        $bytes=$ip.GetAddressBytes()
        if ($bytes[0] -eq 0 -or $bytes[0] -eq 127 -or $bytes[0] -ge 224 -or $item -ne $ip.ToString()) { throw ('不支持的远程目标：'+$item) }
    }
    $result
}
function Test-DnsServer([string]$Server) {
    $udp=New-Object Net.Sockets.UdpClient
    try {
        $udp.Connect($Server,53)
        $packet=New-Object 'System.Collections.Generic.List[byte]'
        $id=Get-Random -Minimum 1 -Maximum 65535
        $packet.AddRange([byte[]]@(($id -shr 8),($id -band 255),1,0,0,1,0,0,0,0,0,0))
        foreach ($label in 'example.com'.Split('.')) { $packet.Add([byte]$label.Length); $packet.AddRange([Text.Encoding]::ASCII.GetBytes($label)) }
        $packet.AddRange([byte[]]@(0,0,1,0,1))
        $bytes=$packet.ToArray(); [void]$udp.Send($bytes,$bytes.Length)
        $task=$udp.ReceiveAsync()
        if (-not $task.Wait(1500)) { return $false }
        $reply=$task.Result.Buffer
        return ($reply.Length -ge 12 -and ($reply[3] -band 15) -eq 0 -and ([int]$reply[6]*256+[int]$reply[7]) -gt 0)
    } catch { return $false } finally { $udp.Dispose() }
}
function Save-State($State) {
    $temp=$script:StatePath+'.tmp'
    $State | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $temp -Encoding UTF8
    Move-Item -LiteralPath $temp -Destination $script:StatePath -Force
}
function Get-Vpn {
    $all=@(Get-NetAdapter | Where-Object InterfaceDescription -like 'PANGP*')
    if ($all.Count -ne 1) { throw '没有找到唯一的 GlobalProtect 网卡。请先安装官方客户端；多张 PANGP 网卡需先排查。' }
    $all[0]
}
function Get-BestRoute([string]$Address) {
    $routes=@(Find-NetRoute -RemoteIPAddress $Address | Where-Object { $null -ne $_.DestinationPrefix })
    if ($routes.Count -ne 1) { throw ('Cannot determine best route for '+$Address) }
    $routes[0]
}
function Get-SameVpn($State) {
    $adapter=Get-Vpn
    if ([string]$adapter.InterfaceGuid -ne [string]$State.VpnGuid -or [int]$adapter.ifIndex -ne [int]$State.VpnIndex) {
        throw 'GlobalProtect adapter identity changed; stopped to protect other adapters.'
    }
    $adapter
}
function Set-VpnDns($State,[string[]]$Servers) {
    $dns=Get-DnsClientServerAddress -InterfaceIndex $State.VpnIndex -AddressFamily IPv4
    if ($Servers.Count -gt 0) { Set-DnsClientServerAddress -InputObject $dns -ServerAddresses $Servers }
    else { Set-DnsClientServerAddress -InputObject $dns -ResetServerAddresses }
}
function Get-VpnRegistryPath($State) {
    $guid=[guid]$State.VpnGuid
    'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\'+$guid.ToString('B')
}
function Get-VpnDnsObservation($State) {
    $path=Get-VpnRegistryPath $State
    $properties=Get-ItemProperty -LiteralPath $path
    $hasNameServer=$null -ne $properties.PSObject.Properties['NameServer']
    $servers=@()
    try { $servers=@((Get-DnsClientServerAddress -InterfaceIndex $State.VpnIndex -AddressFamily IPv4 -ErrorAction Stop).ServerAddresses) } catch { }
    [pscustomobject]@{EffectiveServers=$servers;NameServerExists=$hasNameServer;NameServer=[string]$properties.NameServer}
}
function Normalize-DnsList([object[]]$Values) {
    @($Values | ForEach-Object { ([string]$_ -split '[,;\s]+') } | Where-Object { $_ } | Select-Object -Unique)
}
function Test-DnsEquivalent([object[]]$Actual,[object[]]$Expected) {
    ((Normalize-DnsList $Actual) -join ',') -ceq ((Normalize-DnsList $Expected) -join ',')
}
function Restore-VpnDns($State,$Adapter) {
    $path=Get-VpnRegistryPath $State
    $saved=$State.DnsRegistryBefore
    if ($null -eq $saved) {
        # Recovery of version 1 state: it saved effective addresses only.
        $saved=[pscustomobject]@{NameServerExists=$true;NameServer=(@($State.DnsBefore | Select-Object -Unique) -join ',')}
    }
    $current=Get-ItemProperty -LiteralPath $path
    $effectiveEquivalent=$true
    if ($Adapter.Status -eq 'Up') {
        $observed=Get-VpnDnsObservation $State
        $effectiveEquivalent=Test-DnsEquivalent @($observed.EffectiveServers) @($State.DnsBefore)
    }
    if ($saved.NameServerExists -and $effectiveEquivalent -and (Test-DnsEquivalent @($current.NameServer) @($saved.NameServer))) {
        Write-Event 'DNS_RESTORE_ALREADY_EQUIVALENT' ([ordered]@{Expected=$saved.NameServer;Observed=$current.NameServer})
        return
    }
    if (-not $saved.NameServerExists -and [string]::IsNullOrWhiteSpace([string]$current.NameServer)) { return }
    if ($Adapter.Status -eq 'Up') { Set-VpnDns $State @($State.DnsBefore | Select-Object -Unique) }
    $current=Get-ItemProperty -LiteralPath $path
    # Disabled adapters may have no CIM DNS object. Restore only this verified
    # VPN adapter. Duplicate addresses do not constitute a recovery failure.
    if ($saved.NameServerExists) {
        if ([string]$current.NameServer -cne [string]$saved.NameServer) {
            Set-ItemProperty -LiteralPath $path -Name NameServer -Value ([string]$saved.NameServer)
        }
    } elseif ($null -ne $current.PSObject.Properties['NameServer']) {
        Remove-ItemProperty -LiteralPath $path -Name NameServer
    }
    $restored=$null
    for ($retry=0;$retry -lt 4;$retry++) {
        $restored=Get-ItemProperty -LiteralPath $path
        if ($saved.NameServerExists -and (Test-DnsEquivalent @($restored.NameServer) @($saved.NameServer))) {
            Write-Event 'DNS_RESTORED' ([ordered]@{Expected=$saved.NameServer;Observed=$restored.NameServer})
            return
        }
        if (-not $saved.NameServerExists -and [string]::IsNullOrWhiteSpace([string]$restored.NameServer)) { return }
        Start-Sleep -Milliseconds 500
    }
    Write-Event 'DNS_RESTORE_MISMATCH' ([ordered]@{Expected=$saved.NameServer;Observed=$restored.NameServer})
    throw 'VPN DNS addresses still differ from the saved configuration after recovery; see DNS_RESTORE_MISMATCH in the log.'
}
function Get-StartContext {
    if (Test-Path -LiteralPath $script:StatePath) {
        $previous=Get-Content -LiteralPath $script:StatePath -Raw | ConvertFrom-Json
        if ($previous.Stage -in @('applying','applied','rollback-failed')) { throw 'An earlier session needs recovery. Run Restore.cmd first.' }
        # An already-restored session is historical. Never rewrite DNS during
        # startup to make an old snapshot textually identical to current data.
    }
    $vpn=Get-Vpn
    if ($vpn.Status -eq 'Up') {
        if (-not $Resume -or $null -eq $previous -or $null -eq $previous.PhysicalBaseline) { throw 'Disconnect GlobalProtect first, then start this tool before reconnecting.' }
        $cached=$previous.PhysicalBaseline
        $physical=Get-NetAdapter -InterfaceIndex $cached.InterfaceIndex
        $matching=@(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore | Where-Object { $_.InterfaceIndex -eq $cached.InterfaceIndex -and $_.DestinationPrefix -eq '0.0.0.0/0' -and $_.NextHop -eq $cached.NextHop })
        if (-not $physical.HardwareInterface -or $physical.Status -ne 'Up' -or [string]$physical.InterfaceGuid -ne [string]$cached.InterfaceGuid -or $matching.Count -ne 1) { throw '原物理网络发生变化，请先断开 VPN 后重新开启助手。' }
        return [pscustomobject]@{Vpn=$vpn;BaselineRoute=$matching[0]}
    }
    $baselineRoute=Get-BestRoute '1.1.1.1'
    $baselineAdapter=Get-NetAdapter -InterfaceIndex $baselineRoute.InterfaceIndex
    if (-not $baselineAdapter.HardwareInterface -or $baselineAdapter.Status -ne 'Up') { throw 'The current internet route must use an active physical Wi-Fi/Ethernet adapter.' }
    [pscustomobject]@{Vpn=$vpn;BaselineRoute=$baselineRoute}
}
function Apply-Split($State) {
    $adapter=Get-SameVpn $State
    if ($adapter.Status -ne 'Up') { throw 'VPN disconnected before applying changes.' }
    foreach ($target in $State.AddedTargets) {
        New-NetRoute -DestinationPrefix ($target+'/32') -InterfaceIndex $State.VpnIndex -NextHop $State.VpnNextHop -RouteMetric 1 -PolicyStore ActiveStore -AddressFamily IPv4 | Out-Null
    }
    foreach ($route in $State.RemovedRoutes) {
        Remove-NetRoute -DestinationPrefix $route.DestinationPrefix -InterfaceIndex $State.VpnIndex -NextHop $route.NextHop -PolicyStore ActiveStore -AddressFamily IPv4 -Confirm:$false
    }
    Set-VpnDns $State @($script:AllowedDns)
    Clear-DnsClientCache
}
function Restore-Split($State) {
    $adapter=Get-SameVpn $State
    $failures=New-Object 'System.Collections.Generic.List[string]'
    $sameSession=$false
    if ($adapter.Status -eq 'Up') {
        $sameSession=@(Get-NetIPAddress -InterfaceIndex $State.VpnIndex -AddressFamily IPv4 | Where-Object IPAddress -eq $State.VpnAddress).Count -gt 0
        if (-not $sameSession) { throw 'VPN session changed. Disconnect VPN before running Restore.cmd; current session was left untouched.' }
    }
    try { Restore-VpnDns $State $adapter } catch { $failures.Add('DNS: '+$_.Exception.Message) }
    if ($sameSession) {
        foreach ($route in $State.RemovedRoutes) {
            try {
                $exists=@(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore | Where-Object { $_.InterfaceIndex -eq $State.VpnIndex -and $_.DestinationPrefix -eq $route.DestinationPrefix -and $_.NextHop -eq $route.NextHop })
                if ($exists.Count -eq 0) {
                    New-NetRoute -DestinationPrefix $route.DestinationPrefix -InterfaceIndex $State.VpnIndex -NextHop $route.NextHop -RouteMetric $route.RouteMetric -PolicyStore ActiveStore -AddressFamily IPv4 | Out-Null
                }
            } catch { $failures.Add('Route: '+$_.Exception.Message) }
        }
    }
    foreach ($target in $State.AddedTargets) {
        try {
            $exists=@(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore | Where-Object { $_.InterfaceIndex -eq $State.VpnIndex -and $_.DestinationPrefix -eq ($target+'/32') -and $_.NextHop -eq $State.VpnNextHop -and $_.RouteMetric -eq 1 })
            if ($exists.Count -gt 0) { Remove-NetRoute -DestinationPrefix ($target+'/32') -InterfaceIndex $State.VpnIndex -NextHop $State.VpnNextHop -PolicyStore ActiveStore -AddressFamily IPv4 -Confirm:$false }
        } catch { $failures.Add('Host route: '+$_.Exception.Message) }
    }
    try { Clear-DnsClientCache } catch { $failures.Add('DNS cache: '+$_.Exception.Message) }
    if ($failures.Count -gt 0) { throw ($failures -join '; ') }
}
function Test-Tcp([string]$Address,[int]$Port) {
    $client=New-Object System.Net.Sockets.TcpClient
    try { $task=$client.ConnectAsync($Address,$Port); return ($task.Wait(3000) -and $client.Connected) }
    catch { return $false } finally { $client.Dispose() }
}
function Test-ConnectionResult($State) {
    $targetRoutes=@($State.Targets | ForEach-Object { Get-BestRoute $_ })
    $routesCorrect=@($targetRoutes | Where-Object InterfaceIndex -ne $State.VpnIndex).Count -eq 0
    $webRoute=Get-BestRoute '1.1.1.1'
    $rdp=$null # Service ports are not probed; users open their own applications.
    $web=Test-Tcp '1.1.1.1' 443
    $dns=$false
    try {
        $answers=@(Resolve-DnsName 'example.com' -Type A -DnsOnly -QuickTimeout -ErrorAction Stop | Where-Object IPAddress)
        $dns=$answers.Count -gt 0
    } catch { }
    [pscustomobject]@{
        RdpViaVpn=$routesCorrect
        InternetViaOriginalAdapter=([int]$webRoute.InterfaceIndex -eq [int]$State.LocalIndex)
        RdpPortReachable=$rdp; InternetTcpReachable=$web; SystemDnsWorking=$dns
        Success=($routesCorrect -and [int]$webRoute.InterfaceIndex -eq [int]$State.LocalIndex -and $web -and $dns)
    }
}
function Get-HealthDecision($Result,[int]$PreviousFailures) {
    if (-not $Result.RdpViaVpn -or -not $Result.InternetViaOriginalAdapter) {
        return [pscustomobject]@{Rollback=$true;Failures=$PreviousFailures;Reason='Traffic no longer uses the verified adapters.'}
    }
    # An RDP server can go offline independently of the local internet.
    # A DNS list change by itself is diagnostic information, not a failure.
    if ($Result.InternetTcpReachable -and $Result.SystemDnsWorking) {
        return [pscustomobject]@{Rollback=$false;Failures=0;Reason='Internet route and system DNS are working.'}
    }
    $failures=$PreviousFailures+1
    [pscustomobject]@{Rollback=($failures -ge 3);Failures=$failures;Reason='Internet or system DNS health check failed.'}
}

if ($Mode -eq 'Library') { return }

$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '需要管理员权限。请在助手里点击开启，并允许 Windows 权限提示。' }
New-Item -ItemType Directory -Path $script:Runtime -Force | Out-Null
$lockPath=Join-Path $script:Runtime 'session.lock'
$lock=$null
$state=$null
$mustRestore=$false
try {
    Write-Event 'TOOL_START' ([ordered]@{Version=$script:ToolVersion;Mode=$Mode;Pid=$PID})
    if ($Mode -eq 'Check') {
        $vpn=Get-Vpn
        [pscustomobject]@{Vpn=$vpn.Name;Status=$vpn.Status;Index=$vpn.ifIndex;Target=$script:Target;DnsException=$script:AllowedDns;ReadOnly=$true} | Format-List
        return
    }
    if ($Mode -eq 'Restore') {
        Set-Content -LiteralPath $script:StopPath -Value (Get-Date).ToString('o') -Encoding ASCII
        $until=(Get-Date).AddSeconds(45)
        while ((Get-Date) -lt $until) {
            try { $lock=[IO.File]::Open($lockPath,'OpenOrCreate','ReadWrite','None'); break }
            catch { Start-Sleep -Seconds 1 }
        }
        if ($null -eq $lock) { throw 'The running tool did not stop. Disconnect VPN first, then run Restore again.' }
        if (Test-Path -LiteralPath $script:StatePath) {
            $saved=Get-Content -LiteralPath $script:StatePath -Raw | ConvertFrom-Json
            if ($saved.Stage -in @('applying','applied','rollback-failed')) {
                Restore-Split $saved
                $saved.Stage='restored'; Save-State $saved
            }
        }
        Write-Event 'RESTORED' 'Temporary settings have been restored.'
        return
    }
    try { $lock=[IO.File]::Open($lockPath,'OpenOrCreate','ReadWrite','None') }
    catch { throw 'This tool is already running. Use Restore.cmd to stop it.' }
    $script:TargetList=@(Parse-Targets $Targets)
    $script:Target=$script:TargetList[0]
    $context=Get-StartContext
    $vpn=$context.Vpn
    $baselineRoute=$context.BaselineRoute
    Write-Event 'WAITING_FOR_VPN' 'Now connect GlobalProtect. This local tool will continue while the internet is unavailable.'
    while ($true) {
        if (Test-Path -LiteralPath $script:StopPath) { Write-Event 'WAIT_CANCELLED' '已停止等待 VPN，网络未修改。'; return }
        $vpn=Get-Vpn
        if ($vpn.Status -eq 'Up') {
            $defaults=@(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore | Where-Object { $_.InterfaceIndex -eq $vpn.ifIndex -and $_.DestinationPrefix -eq '0.0.0.0/0' })
            if ($defaults.Count -eq 1) { break }
        }
        Start-Sleep -Seconds 2
    }
    if ($vpn.Status -ne 'Up' -or $defaults.Count -ne 1) { throw '等待 VPN 连接超时，未修改网络。请确认 GlobalProtect 显示已连接。' }
    Start-Sleep -Seconds 10
    if (Test-Path -LiteralPath $script:StopPath) { Write-Event 'WAIT_CANCELLED' '已停止等待，网络未修改。'; return }
    $vpn=Get-Vpn
    $vpnRoutes=@(Get-NetRoute -InterfaceIndex $vpn.ifIndex -AddressFamily IPv4 -PolicyStore ActiveStore)
    $defaults=@($vpnRoutes | Where-Object DestinationPrefix -eq '0.0.0.0/0')
    if ($vpn.Status -ne 'Up' -or $defaults.Count -ne 1) { throw 'VPN did not remain connected; no changes made.' }
    if (@($vpnRoutes | Where-Object DestinationPrefix -in @('0.0.0.0/1','128.0.0.0/1')).Count -gt 0) { throw 'This VPN uses unsupported half-default routes; no changes made.' }
    $v6Defaults=@(Get-NetRoute -InterfaceIndex $vpn.ifIndex -AddressFamily IPv6 -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object DestinationPrefix -eq '::/0')
    if ($v6Defaults.Count -gt 0) { throw 'This VPN also controls IPv6; no changes made.' }
    $stillLocal=@(Get-NetRoute -InterfaceIndex $baselineRoute.InterfaceIndex -AddressFamily IPv4 -PolicyStore ActiveStore | Where-Object { $_.DestinationPrefix -eq '0.0.0.0/0' -and $_.NextHop -eq $baselineRoute.NextHop })
    if ($stillLocal.Count -eq 0) { throw 'Original physical internet route disappeared; no changes made.' }
    $vpnAddresses=@(Get-NetIPAddress -InterfaceIndex $vpn.ifIndex -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '169.254.*' })
    if ($vpnAddresses.Count -ne 1) { throw 'VPN IPv4 address is ambiguous; no changes made.' }
    $dns=Get-DnsClientServerAddress -InterfaceIndex $vpn.ifIndex -AddressFamily IPv4
    $candidates=@($dns.ServerAddresses | Where-Object { $_ -and $_ -ne '127.0.0.1' } | Select-Object -Unique)
    if ($PreferredDns) {
        $validated=@(Parse-Targets $PreferredDns)
        if ($validated.Count -ne 1 -or $validated[0] -notin $candidates) { throw '指定的 DNS 必须是当前 VPN 下发的一个 IPv4 DNS 地址。' }
        $candidates=@($validated[0])
    }
    foreach ($candidate in $candidates) {
        $candidateRoutes=@($vpnRoutes | Where-Object DestinationPrefix -eq ($candidate+'/32'))
        if ($candidateRoutes.Count -gt 0 -and (Test-DnsServer $candidate)) { $script:AllowedDns=$candidate; break }
    }
    if (-not $script:AllowedDns) { throw '未找到带有 VPN 路由且可解析公网域名的 VPN DNS，未修改网络。请联系 VPN 管理员。' }
    $dnsHostRoutes=@($vpnRoutes | Where-Object DestinationPrefix -eq ($script:AllowedDns+'/32'))
    if ($dnsHostRoutes.Count -eq 0) { throw 'VPN DNS host route is missing; no changes made.' }
    $state=[pscustomobject]@{
        Stage='applying'; Version=$script:ToolVersion; CapturedAt=(Get-Date).ToString('o'); Targets=@($script:TargetList)
        VpnIndex=[int]$vpn.ifIndex; VpnGuid=[string]$vpn.InterfaceGuid; VpnAddress=$vpnAddresses[0].IPAddress
        VpnNextHop=$defaults[0].NextHop; LocalIndex=[int]$baselineRoute.InterfaceIndex
        PhysicalBaseline=[pscustomobject]@{InterfaceIndex=$baselineRoute.InterfaceIndex;InterfaceGuid=[string](Get-NetAdapter -InterfaceIndex $baselineRoute.InterfaceIndex).InterfaceGuid;NextHop=$baselineRoute.NextHop}
        DnsBefore=@($dns.ServerAddresses)
        DnsRegistryBefore=$null
        AddedTargets=@($script:TargetList | Where-Object { $ip=$_; @($vpnRoutes | Where-Object DestinationPrefix -eq ($ip+'/32')).Count -eq 0 })
        RemovedRoutes=@($vpnRoutes | Where-Object DestinationPrefix -eq '0.0.0.0/0' | Select-Object DestinationPrefix,NextHop,RouteMetric)
    }
    $state.DnsRegistryBefore=Get-VpnDnsObservation $state
    Write-Event 'DNS_BEFORE' $state.DnsRegistryBefore
    if (Test-Path -LiteralPath $script:StopPath) { Write-Event 'WAIT_CANCELLED' '已停止准备，网络未修改。'; return }
    Save-State $state
    $mustRestore=$true
    Apply-Split $state
    Write-Event 'DNS_AFTER_APPLY' (Get-VpnDnsObservation $state)
    $state.Stage='applied'; Save-State $state
    Write-Event 'APPLIED_TEMPORARY_SPLIT' 'RDP target and VPN DNS use VPN. Ordinary internet destinations use the original physical route.'
    Start-Sleep -Seconds 8
    $verified=$false
    for ($attempt=1;$attempt -le 3;$attempt++) {
        if (Test-Path -LiteralPath $script:StopPath) { return }
        $result=Test-ConnectionResult $state
        Write-Event 'VERIFICATION' $result
        if ($result.Success) { $verified=$true; break }
        Start-Sleep -Seconds 2
    }
    if (-not $verified) { throw 'Verification failed. Restoring the original VPN configuration automatically.' }
    Write-Event 'READY' 'Basic RDP, internet route and public DNS checks passed. Test your apps now. Disconnect VPN or run Restore.cmd to undo.'
    $lastDnsFingerprint=''
    $nextHealthCheck=Get-Date
    $healthFailures=0
    while ($true) {
        if (Test-Path -LiteralPath $script:StopPath) { break }
        $current=Get-SameVpn $state
        if ($current.Status -ne 'Up') { break }
        $badDefaults=@(Get-NetRoute -InterfaceIndex $state.VpnIndex -AddressFamily IPv4 -PolicyStore ActiveStore | Where-Object DestinationPrefix -eq '0.0.0.0/0')
        if ($badDefaults.Count -gt 0) { throw 'VPN restored its default route. Stopping and rolling back instead of repeatedly overriding it.' }
        $dnsObservation=Get-VpnDnsObservation $state
        $dnsFingerprint=(@($dnsObservation.EffectiveServers) -join ',')+'|'+$dnsObservation.NameServer
        if ($dnsFingerprint -cne $lastDnsFingerprint) {
            Write-Event 'DNS_OBSERVED' $dnsObservation
            $lastDnsFingerprint=$dnsFingerprint
            $nextHealthCheck=Get-Date
        }
        if ((Get-Date) -ge $nextHealthCheck) {
            $health=Test-ConnectionResult $state
            $decision=Get-HealthDecision $health $healthFailures
            Write-Event 'HEALTH' ([ordered]@{Result=$health;Decision=$decision;Dns=$dnsObservation})
            $healthFailures=$decision.Failures
            if ($decision.Rollback) { throw ($decision.Reason+' Stopping and restoring the original VPN configuration.') }
            $nextHealthCheck=(Get-Date).AddSeconds(15)
        }
        Start-Sleep -Seconds 3
    }
} catch {
    if (Test-Path -LiteralPath $script:Runtime) { Write-Event 'ERROR' ([ordered]@{Message=$_.Exception.Message;Location=$_.InvocationInfo.PositionMessage;Stack=$_.ScriptStackTrace}) }
} finally {
    if ($mustRestore -and $null -ne $state) {
        try {
            Restore-Split $state
            $state.Stage='restored'; Save-State $state
            Write-Event 'RESTORED' 'Temporary route and VPN DNS changes were undone.'
        } catch {
            $state.Stage='rollback-failed'; Save-State $state
            Write-Event 'ROLLBACK_FAILED' $_.Exception.Message
        }
    }
    if ($null -ne $lock) { $lock.Dispose() }
}
