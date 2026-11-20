[CmdletBinding()]
param(
    [ValidateSet('Start','Restore','Check','Library')][string]$Mode='Start',
    [string]$DataDirectory,
    [string]$Targets='',
    [string]$PreferredDns='',
    [string]$RdpDiagnosticTarget='',
    [string]$ControllerPath='',
    [int]$OwnerPid=0
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
$script:ToolVersion='1.0.7'
$script:OwnerProcess=$null

function Test-StopRequested {
    if (Test-Path -LiteralPath $script:StopPath) { return $true }
    if ($null -eq $script:OwnerProcess) { return $false }
    try { return $script:OwnerProcess.HasExited } catch { return $true }
}

function Write-Event([string]$Event,$Data) {
    # Diagnostic failures must never enter the network rollback path. In Windows
    # PowerShell 5.1 Add-Content conflicts with an existing log reader, even when
    # that reader permits sharing. Use an explicit append writer instead.
    try { $line=[ordered]@{Time=(Get-Date).ToString('o');Event=$Event;Data=$Data} | ConvertTo-Json -Depth 9 -Compress }
    catch { return }
    $stream=$null; $fileError=$null
    try {
        $bytes=[Text.Encoding]::UTF8.GetBytes($line+[Environment]::NewLine)
        $stream=[IO.File]::Open($script:LogPath,[IO.FileMode]::Append,[IO.FileAccess]::Write,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $stream.Write($bytes,0,$bytes.Length)
    } catch { $fileError=$_.Exception.GetType().Name }
    finally {
        if ($null -ne $stream) { try { $stream.Dispose() } catch { $fileError=$_.Exception.GetType().Name } }
    }
    # The UI still receives the original event if the file is locked or unwritable.
    # A warning uses stdout so it is not mistaken for a fatal engine error.
    try {
        [Console]::Out.WriteLine($line)
        if ($null -ne $fileError) {
            $warning=[ordered]@{Time=(Get-Date).ToString('o');Event='LOG_WARNING';Data=@{Reason=$fileError;Message='日志文件暂时无法写入，助手继续运行。'}} | ConvertTo-Json -Compress
            [Console]::Out.WriteLine($warning)
        }
    } catch { }
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
function Test-DnsServer([string]$Server,[string]$Name='example.com') {
    $udp=New-Object Net.Sockets.UdpClient
    try {
        $udp.Connect($Server,53)
        $packet=New-Object 'System.Collections.Generic.List[byte]'
        $id=Get-Random -Minimum 1 -Maximum 65535
        $packet.AddRange([byte[]]@(($id -shr 8),($id -band 255),1,0,0,1,0,0,0,0,0,0))
        foreach ($label in $Name.Split('.')) { $packet.Add([byte]$label.Length); $packet.AddRange([Text.Encoding]::ASCII.GetBytes($label)) }
        $packet.AddRange([byte[]]@(0,0,1,0,1))
        $bytes=$packet.ToArray(); [void]$udp.Send($bytes,$bytes.Length)
        $task=$udp.ReceiveAsync()
        if (-not $task.Wait(1500)) { return $false }
        $reply=$task.Result.Buffer
        return ($reply.Length -ge 12 -and ([int]$reply[0]*256+[int]$reply[1]) -eq $id -and
            ($reply[2] -band 128) -ne 0 -and ($reply[3] -band 15) -eq 0 -and
            ([int]$reply[6]*256+[int]$reply[7]) -gt 0)
    } catch { return $false } finally { $udp.Dispose() }
}
function Get-WorkingDns([string[]]$Candidates,[int]$InterfaceIndex,[int]$Attempts=1) {
    for ($attempt=1;$attempt -le $Attempts;$attempt++) {
        $name=if ($attempt -gt 1) {'example.org'} else {'example.com'}
        foreach ($server in $Candidates) {
            if (Test-StopRequested) { return }
            $ip=$null
            if (-not [Net.IPAddress]::TryParse($server,[ref]$ip) -or $ip.AddressFamily -ne 'InterNetwork' -or
                [Net.IPAddress]::IsLoopback($ip) -or $ip.GetAddressBytes()[0] -eq 0 -or $ip.GetAddressBytes()[0] -ge 224) { continue }
            $onExpectedPath=$false
            try { $onExpectedPath=[int](Get-BestRoute $server).InterfaceIndex -eq $InterfaceIndex } catch { }
            $works=$onExpectedPath -and (Test-DnsServer $server $name)
            Write-Event 'DNS_PROBE' @{Server=$server;InterfaceIndex=$InterfaceIndex;OnExpectedPath=$onExpectedPath;Responded=$works;Attempt=$attempt}
            if ($works) { return $server }
        }
        if ($attempt -lt $Attempts) { Start-Sleep -Seconds 1 }
    }
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
function Test-VpnDown {
    $active=@(Get-NetAdapter | Where-Object { $_.InterfaceDescription -like 'PANGP*' -and $_.Status -eq 'Up' })
    return $active.Count -eq 0
}
function Wait-VpnDown([int]$TimeoutSeconds) {
    $elapsed=[Diagnostics.Stopwatch]::StartNew()
    $downSamples=0
    while ($TimeoutSeconds -eq 0 -or $elapsed.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (Test-VpnDown) {
            $downSamples++
            if ($downSamples -eq 2) { return $true }
        } else { $downSamples=0 }
        Start-Sleep -Seconds 1
    }
    return $false
}
function Request-ManagedVpnDisconnect([string]$Reason) {
    if (-not ((Test-VpnDown) -and (Wait-VpnDown 3))) {
        Write-Event 'VPN_DISCONNECTING' @{Reason=$Reason}
        $request=$null
        try {
            if ($ControllerPath -and (Test-Path -LiteralPath $ControllerPath -PathType Leaf)) {
                $request=Start-Process -FilePath $ControllerPath -ArgumentList '--request-vpn-disconnect' -WindowStyle Hidden -PassThru
            }
            if ($null -ne $request -and (Wait-VpnDown 16)) {
                Write-Event 'VPN_DISCONNECTED' @{Reason=$Reason}
                return
            }
        } catch { Write-Event 'VPN_DISCONNECT_REQUEST_FAILED' $_.Exception.GetType().Name }
        finally {
            if ($null -ne $request) {
                try { if (-not $request.HasExited) { $request.Kill() } } catch { }
                $request.Dispose()
            }
        }
        Write-Event 'VPN_DISCONNECT_MANUAL' 'GlobalProtect 仍在线。请在官方客户端点断开；助手会等待并自动清理。'
    }
    # A snapshot must never put the VPN default back before the tunnel is down.
    [void](Wait-VpnDown 0)
    Write-Event 'VPN_DISCONNECTED' @{Reason=$Reason}
}
function Wait-VpnReady([int]$StableSeconds=10) {
    $stableSince=$null; $stableKey=''
    while ($true) {
        if (Test-StopRequested) { Write-Event 'WAIT_CANCELLED' '已停止等待 VPN。'; return $null }
        $vpn=Get-Vpn
        # Enumerate first: an adapter can temporarily have no routes while
        # GlobalProtect replaces its connection. A filtered CIM query throws
        # for that normal transition instead of returning an empty list.
        $routes=@(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore | Where-Object InterfaceIndex -eq $vpn.ifIndex)
        $defaults=@($routes | Where-Object DestinationPrefix -eq '0.0.0.0/0')
        if ($vpn.Status -eq 'Up' -and $defaults.Count -eq 1) {
            $key=([string]$vpn.InterfaceGuid)+':'+$vpn.ifIndex+':'+$defaults[0].NextHop
            if ($key -ne $stableKey -or $null -eq $stableSince) { $stableSince=Get-Date; $stableKey=$key }
            if (((Get-Date)-$stableSince).TotalSeconds -ge $StableSeconds) { return [pscustomobject]@{Vpn=$vpn;Routes=$routes;Defaults=$defaults} }
        } else { $stableSince=$null; $stableKey='' }
        Start-Sleep -Seconds 2
    }
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
    $previous=$null
    if (Test-Path -LiteralPath $script:StatePath) {
        $previous=Get-Content -LiteralPath $script:StatePath -Raw | ConvertFrom-Json
        if ($previous.Stage -in @('applying','applied','rollback-failed')) { throw 'An earlier session needs recovery. Run Restore.cmd first.' }
        # An already-restored session is historical. Never rewrite DNS during
        # startup to make an old snapshot textually identical to current data.
    }
    $vpn=Get-Vpn
    if ($vpn.Status -eq 'Up') {
        $physical=@(Get-NetAdapter | Where-Object { $_.HardwareInterface -and $_.Status -eq 'Up' })
        $matching=@(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore | Where-Object {
            $_.InterfaceIndex -in $physical.ifIndex -and $_.DestinationPrefix -eq '0.0.0.0/0' -and $_.NextHop -ne '0.0.0.0'
        })
        if ($previous.Stage -eq 'restored' -and $null -ne $previous.PhysicalBaseline) {
            $cached=$previous.PhysicalBaseline
            $matching=@($matching | Where-Object {
                $_.InterfaceIndex -eq $cached.InterfaceIndex -and $_.NextHop -eq $cached.NextHop -and
                @($physical | Where-Object { $_.ifIndex -eq $cached.InterfaceIndex -and [string]$_.InterfaceGuid -eq [string]$cached.InterfaceGuid }).Count -eq 1
            })
        }
        if ($matching.Count -ne 1) { throw 'VPN 已连接，但无法安全确认原来的 Wi-Fi 或有线网络。请断开 VPN 后启动助手。' }
        return [pscustomobject]@{Vpn=$vpn;BaselineRoute=$matching[0];AlreadyConnected=$true}
    }
    $baselineRoute=Get-BestRoute '1.1.1.1'
    $baselineAdapter=Get-NetAdapter -InterfaceIndex $baselineRoute.InterfaceIndex
    if (-not $baselineAdapter.HardwareInterface -or $baselineAdapter.Status -ne 'Up') { throw 'The current internet route must use an active physical Wi-Fi/Ethernet adapter.' }
    [pscustomobject]@{Vpn=$vpn;BaselineRoute=$baselineRoute;AlreadyConnected=$false}
}
function Test-PhysicalBaseline($State) {
    $cached=$State.PhysicalBaseline
    $physical=Get-NetAdapter -InterfaceIndex $State.LocalIndex -ErrorAction SilentlyContinue
    if (-not $physical.HardwareInterface -or $physical.Status -ne 'Up' -or
        [string]$physical.InterfaceGuid -ne [string]$cached.InterfaceGuid) { return $false }
    $default=@(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore | Where-Object {
        $_.InterfaceIndex -eq $State.LocalIndex -and $_.DestinationPrefix -eq '0.0.0.0/0' -and $_.NextHop -eq $cached.NextHop
    })
    return $default.Count -eq 1
}
function Set-PhysicalDnsFallback($State) {
    if (-not (Test-PhysicalBaseline $State)) { return $false }
    $configured=Get-DnsClientServerAddress -InterfaceIndex $State.LocalIndex -AddressFamily IPv4
    $servers=@(Normalize-DnsList @($configured.ServerAddresses))
    $dns=Get-WorkingDns $servers $State.LocalIndex 2
    if (-not $dns -or (Test-StopRequested)) { return $false }
    $adapter=Get-SameVpn $State
    $activeAddresses=@(Get-NetIPAddress -InterfaceIndex $State.VpnIndex -AddressFamily IPv4 |
        Where-Object IPAddress -eq $State.VpnAddress)
    if ($adapter.Status -ne 'Up' -or $activeAddresses.Count -ne 1) { return $false }
    if ([int](Get-BestRoute $dns).InterfaceIndex -ne [int]$State.LocalIndex) { return $false }
    Set-VpnDns $State @($dns)
    $State.DnsSource='Physical'
    Save-State $State
    Clear-DnsClientCache
    Write-Event 'DNS_FALLBACK' @{Server=$dns;InterfaceIndex=$State.LocalIndex}
    return $true
}
function Repair-AutomaticDns($State) {
    if ($State.DnsSource -eq 'Pending') {
        $routes=@(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore | Where-Object InterfaceIndex -eq $State.VpnIndex)
        $candidates=@((Normalize-DnsList @($State.DnsBefore)) | Where-Object { ($_+'/32') -in $routes.DestinationPrefix })
        $dns=Get-WorkingDns $candidates $State.VpnIndex
        if ($dns -and -not (Test-StopRequested)) {
            Assert-VpnSession $State
            Set-VpnDns $State @($dns)
            $State.DnsSource='Vpn'
            Save-State $State
            Clear-DnsClientCache
            Write-Event 'VPN_DNS_SELECTED' @{Server=$dns;InterfaceIndex=$State.VpnIndex}
            return $true
        }
    }
    return Set-PhysicalDnsFallback $State
}
function Assert-VpnSession($State) {
    $adapter=Get-SameVpn $State
    if ($adapter.Status -ne 'Up') { throw 'GlobalProtect 已断开，会话设置未应用。' }
    $matchingIp=@(Get-NetIPAddress -InterfaceIndex $State.VpnIndex -AddressFamily IPv4 | Where-Object IPAddress -eq $State.VpnAddress)
    if ($matchingIp.Count -ne 1) { throw 'GlobalProtect 已切换会话，会话设置未应用。' }
}
function Apply-Split($State) {
    Assert-VpnSession $State
    foreach ($target in $State.AddedTargets) {
        New-NetRoute -DestinationPrefix ($target+'/32') -InterfaceIndex $State.VpnIndex -NextHop $State.VpnNextHop -RouteMetric 1 -PolicyStore ActiveStore -AddressFamily IPv4 | Out-Null
    }
    foreach ($route in $State.RemovedRoutes) {
        Remove-NetRoute -DestinationPrefix $route.DestinationPrefix -InterfaceIndex $State.VpnIndex -NextHop $route.NextHop -PolicyStore ActiveStore -AddressFamily IPv4 -Confirm:$false
    }
    if ($script:AllowedDns) {
        Set-VpnDns $State @($script:AllowedDns)
        Clear-DnsClientCache
    }
}
function Sync-VpnDefaultRoute($State,$Guard) {
    # Other adapters can appear or disappear during Windows route discovery. Only
    # act if the original VPN default persists across separate observations.
    if (Test-StopRequested) { return 'Stopped' }
    if ((Get-SameVpn $State).Status -ne 'Up') { return 'Disconnected' }
    $routes=@(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore | Where-Object InterfaceIndex -eq $State.VpnIndex)
    $defaults=@($routes | Where-Object DestinationPrefix -eq '0.0.0.0/0')
    if ($defaults.Count -eq 0) { $Guard.SeenSince=$null; return 'Clear' }

    $now=Get-Date
    if ($null -eq $Guard.SeenSince) {
        $Guard.SeenSince=$now
        Write-Event 'VPN_DEFAULT_RETURNED' 'GlobalProtect 重新添加了默认路由，正在确认网络状态。'
        return 'Waiting'
    }
    if (($now-$Guard.SeenSince).TotalSeconds -lt 2) { return 'Waiting' }

    $original=@($State.RemovedRoutes | Where-Object DestinationPrefix -eq '0.0.0.0/0')
    if ($original.Count -ne 1 -or $defaults.Count -ne 1 -or $defaults[0].NextHop -ne $original[0].NextHop -or
        @($routes | Where-Object DestinationPrefix -in @('0.0.0.0/1','128.0.0.0/1')).Count -gt 0) {
        throw 'GlobalProtect 下发了不同的路由，已停止重新应用分流。'
    }
    $ipv6=@(Get-NetRoute -InterfaceIndex $State.VpnIndex -AddressFamily IPv6 -PolicyStore ActiveStore -ErrorAction SilentlyContinue |
        Where-Object DestinationPrefix -eq '::/0')
    if ($ipv6.Count -gt 0) { throw 'GlobalProtect 新增了 IPv6 默认路由，已停止重新应用分流。' }
    if (-not (Test-PhysicalBaseline $State)) { throw '原 Wi-Fi 或有线网络出口已变化，无法重新应用分流。' }
    $addresses=@(Get-NetIPAddress -InterfaceIndex $State.VpnIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object IPAddress -eq $State.VpnAddress)
    if ($addresses.Count -ne 1) { throw 'GlobalProtect 会话地址已变化，无法重新应用分流。' }
    foreach ($target in $State.Targets) {
        if (($target+'/32') -notin $routes.DestinationPrefix) { throw 'VPN 内网目标路由已变化，无法重新应用分流。' }
    }

    $Guard.Repairs=@($Guard.Repairs | Where-Object { ($now-$_).TotalSeconds -lt 60 })
    if ($Guard.Repairs.Count -ge 2 -or ($Guard.Repairs.Count -gt 0 -and ($now-$Guard.Repairs[-1]).TotalSeconds -lt 10)) {
        throw 'GlobalProtect 持续恢复默认路由，已停止自动修复。'
    }
    if (Test-StopRequested) { return 'Stopped' }
    if ((Get-SameVpn $State).Status -ne 'Up') { return 'Disconnected' }
    Remove-NetRoute -DestinationPrefix '0.0.0.0/0' -InterfaceIndex $State.VpnIndex -NextHop $original[0].NextHop -PolicyStore ActiveStore -AddressFamily IPv4 -Confirm:$false
    $Guard.Repairs+=@($now)
    $Guard.SeenSince=$null
    Write-Event 'VPN_DEFAULT_REPAIRED' '已重新应用分流，正在复查网络。'
    return 'Repaired'
}
function Restore-Split($State,[switch]$OnlyWhenVpnDown) {
    $adapter=Get-SameVpn $State
    $failures=New-Object 'System.Collections.Generic.List[string]'
    $sameSession=$false
    if ($adapter.Status -eq 'Up') {
        if ($OnlyWhenVpnDown) { throw 'GlobalProtect 在恢复前重新连接，请断开后重试清理。' }
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
function Complete-VpnSession([string]$Reason,$State,[bool]$WaitingForVpn) {
    if ($Reason -eq 'VpnLost') { Write-Event 'VPN_LOST' '检测到 GlobalProtect 掉线，准备清理本次分流。' }
    if ($WaitingForVpn -and $Reason -in @('Stopped','Fault')) {
        Write-Event 'VPN_DISCONNECTING' @{Reason=$Reason}
        # The login click may already be queued in the official client.
        for ($observe=0;$observe -lt 8 -and (Test-VpnDown);$observe++) { Start-Sleep -Seconds 1 }
    }
    Request-ManagedVpnDisconnect $Reason
    if ($null -ne $State) {
        Restore-Split $State -OnlyWhenVpnDown
        $State.Stage='restored'; Save-State $State
        Write-Event 'RESTORED' 'Temporary route and VPN DNS changes were undone.'
    }
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
    foreach ($name in @('example.com','example.org')) {
        try {
            $answers=@(Resolve-DnsName $name -Type A -DnsOnly -QuickTimeout -ErrorAction Stop | Where-Object IPAddress)
            if ($answers.Count -gt 0) { $dns=$true; break }
        } catch { }
    }
    [pscustomobject]@{
        RdpViaVpn=$routesCorrect
        InternetViaOriginalAdapter=([int]$webRoute.InterfaceIndex -eq [int]$State.LocalIndex)
        RdpPortReachable=$rdp; InternetTcpReachable=$web; SystemDnsWorking=$dns
        Success=($routesCorrect -and [int]$webRoute.InterfaceIndex -eq [int]$State.LocalIndex -and $web -and $dns)
    }
}
function Get-StartupResult($Result) {
    if (-not $Result.RdpViaVpn -or -not $Result.InternetViaOriginalAdapter) { return 'RouteMismatch' }
    if (-not $Result.InternetTcpReachable) { return 'PublicUnreachable' }
    if (-not $Result.SystemDnsWorking) { return 'DnsPending' }
    return 'Ready'
}
function Write-VpnTargetDiagnostic($State) {
    if (-not $RdpDiagnosticTarget -or $RdpDiagnosticTarget -notin $State.Targets) { return }
    try {
        $route=Get-BestRoute $RdpDiagnosticTarget
        if ([int]$route.InterfaceIndex -ne [int]$State.VpnIndex) { return }
        $reachable=Test-Tcp $RdpDiagnosticTarget 3389
        Write-Event 'VPN_TARGET_PROBE' @{Target=$RdpDiagnosticTarget;Port=3389;Reachable=$reachable}
    } catch { Write-Event 'VPN_TARGET_PROBE_SKIPPED' $_.Exception.Message }
}
function Get-HealthDecision($Result,[int]$PreviousFailures) {
    if (-not $Result.RdpViaVpn -or -not $Result.InternetViaOriginalAdapter) {
        return [pscustomobject]@{Rollback=$true;Failures=$PreviousFailures;Reason='Traffic no longer uses the verified adapters.'}
    }
    # An RDP server can go offline independently of the local internet.
    # A DNS list change by itself is diagnostic information, not a failure.
    if ($Result.InternetTcpReachable) {
        $reason=if ($Result.SystemDnsWorking) {'Internet route and system DNS are working.'} else {'DNS unavailable; retaining verified routes.'}
        return [pscustomobject]@{Rollback=$false;Failures=0;Reason=$reason}
    }
    $failures=$PreviousFailures+1
    [pscustomobject]@{Rollback=($failures -ge 3);Failures=$failures;Reason='Public IP connectivity check failed.'}
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
$ownsSession=$false
$waitingForVpn=$false
$endReason='NotStarted'
try {
    if ($OwnerPid -gt 0) {
        $script:OwnerProcess=[Diagnostics.Process]::GetProcessById($OwnerPid)
        [void]$script:OwnerProcess.Handle
    }
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
        $ownsSession=$true
        $endReason='Restore'
        if (Test-Path -LiteralPath $script:StatePath) {
            $saved=Get-Content -LiteralPath $script:StatePath -Raw | ConvertFrom-Json
            if ($saved.Stage -in @('applying','applied','rollback-failed')) {
                $state=$saved
                $mustRestore=$true
            }
        }
        return
    }
    try { $lock=[IO.File]::Open($lockPath,'OpenOrCreate','ReadWrite','None') }
    catch { throw 'This tool is already running. Use Restore.cmd to stop it.' }
    $script:TargetList=@(Parse-Targets $Targets)
    $script:Target=$script:TargetList[0]
    $ownsSession=$true
    $context=Get-StartContext
    $vpn=$context.Vpn
    $baselineRoute=$context.BaselineRoute
    if ($context.AlreadyConnected) { Write-Event 'VPN_DETECTED' 'GlobalProtect 已连接，正在检查分流条件。' }
    else {
        $waitingForVpn=$true
        Write-Event 'WAITING_FOR_VPN' 'Now connect GlobalProtect. This local tool will continue while the internet is unavailable.'
    }
    $ready=Wait-VpnReady
    if ($null -eq $ready) { return }
    $waitingForVpn=$false
    $vpn=$ready.Vpn; $vpnRoutes=@($ready.Routes); $defaults=@($ready.Defaults)
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
    $routedCandidates=@($candidates | Where-Object { ($_+'/32') -in $vpnRoutes.DestinationPrefix })
    Write-Event 'DNS_CANDIDATES' @{Configured=$candidates;WithHostRoute=$routedCandidates}
    $script:AllowedDns=Get-WorkingDns $routedCandidates $vpn.ifIndex 2
    if (Test-StopRequested) { return }
    if ($PreferredDns -and -not $script:AllowedDns) { throw 'VPN 已连接，但指定的 DNS 未通过验证。请在网络设置中改用自动选择后重试。' }
    $dnsSource=if ($script:AllowedDns) {'Vpn'} else {'Pending'}
    $state=[pscustomobject]@{
        Stage='applying'; Version=$script:ToolVersion; CapturedAt=(Get-Date).ToString('o'); Targets=@($script:TargetList)
        VpnIndex=[int]$vpn.ifIndex; VpnGuid=[string]$vpn.InterfaceGuid; VpnAddress=$vpnAddresses[0].IPAddress
        VpnNextHop=$defaults[0].NextHop; LocalIndex=[int]$baselineRoute.InterfaceIndex
        PhysicalBaseline=[pscustomobject]@{InterfaceIndex=$baselineRoute.InterfaceIndex;InterfaceGuid=[string](Get-NetAdapter -InterfaceIndex $baselineRoute.InterfaceIndex).InterfaceGuid;NextHop=$baselineRoute.NextHop}
        DnsBefore=@($dns.ServerAddresses)
        DnsSource=$dnsSource
        DnsRegistryBefore=$null
        AddedTargets=@($script:TargetList | Where-Object { $ip=$_; @($vpnRoutes | Where-Object DestinationPrefix -eq ($ip+'/32')).Count -eq 0 })
        RemovedRoutes=@($vpnRoutes | Where-Object DestinationPrefix -eq '0.0.0.0/0' | Select-Object DestinationPrefix,NextHop,RouteMetric)
    }
    $state.DnsRegistryBefore=Get-VpnDnsObservation $state
    Write-Event 'DNS_BEFORE' $state.DnsRegistryBefore
    if (Test-StopRequested) { Write-Event 'WAIT_CANCELLED' '已停止准备。'; return }
    Save-State $state
    $mustRestore=$true
    Apply-Split $state
    if ($state.DnsSource -eq 'Pending' -and -not (Test-StopRequested)) {
        try { [void](Set-PhysicalDnsFallback $state) }
        catch { Write-Event 'DNS_FALLBACK_FAILED' $_.Exception.Message }
    }
    Write-Event 'DNS_AFTER_APPLY' (Get-VpnDnsObservation $state)
    $state.Stage='applied'; Save-State $state
    Write-Event 'APPLIED_TEMPORARY_SPLIT' 'RDP target uses VPN. Ordinary internet destinations use the original physical route.'
    Start-Sleep -Seconds 8
    $startupResult='RouteMismatch'
    $targetChecked=$false
    $routeGuard=[pscustomobject]@{SeenSince=$null;Repairs=@()}
    for ($attempt=1;$attempt -le 3;$attempt++) {
        if (Test-StopRequested) { return }
        $routeStatus=Sync-VpnDefaultRoute $state $routeGuard
        if ($routeStatus -eq 'Waiting') {
            Start-Sleep -Seconds 3
            $routeStatus=Sync-VpnDefaultRoute $state $routeGuard
        }
        if ($routeStatus -eq 'Disconnected') { $endReason='VpnLost'; return }
        if ($routeStatus -eq 'Stopped') { return }
        if ($routeStatus -eq 'Waiting') { continue }
        $result=Test-ConnectionResult $state
        $startupResult=Get-StartupResult $result
        Write-Event 'VERIFICATION' $result
        if ($startupResult -eq 'Ready') { break }
        if ($result.RdpViaVpn -and -not $targetChecked) {
            Write-VpnTargetDiagnostic $state
            $targetChecked=$true
        }
        if ($attempt -eq 2 -and -not $PreferredDns -and $state.DnsSource -ne 'Physical' -and $startupResult -eq 'DnsPending') {
            try { [void](Repair-AutomaticDns $state) }
            catch { Write-Event 'DNS_FALLBACK_FAILED' $_.Exception.Message }
        }
        Start-Sleep -Seconds 2
    }
    if ($startupResult -eq 'RouteMismatch') { throw '分流后，内网和公网流量未走预期网卡，正在恢复设置。' }
    if ($startupResult -eq 'PublicUnreachable') { throw '分流后仍无法连接公网 IP，正在恢复设置。请检查网络和 GlobalProtect 隧道。' }
    if ($startupResult -eq 'DnsPending') { Write-Event 'DNS_DEGRADED' '分流路由与公网 IP 检查通过。DNS 暂时无响应，助手保持分流并继续重试。' }
    else { Write-Event 'READY' 'Routing, public IP and public DNS checks passed.' }
    $lastDnsFingerprint=''
    $nextHealthCheck=Get-Date
    $healthFailures=0
    $dnsFailures=if ($startupResult -eq 'DnsPending') {1} else {0}
    $nextDnsFallback=(Get-Date).AddSeconds(15)
    while ($true) {
        $routeStatus=Sync-VpnDefaultRoute $state $routeGuard
        if ($routeStatus -eq 'Disconnected') { $endReason='VpnLost'; break }
        if ($routeStatus -eq 'Stopped') { break }
        if ($routeStatus -eq 'Waiting') { Start-Sleep -Seconds 3; continue }
        if ($routeStatus -eq 'Repaired') { $nextHealthCheck=Get-Date }
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
            if ($decision.Rollback -and -not $health.InternetViaOriginalAdapter) {
                # The route can return during the connectivity probe itself.
                $routeStatus=Sync-VpnDefaultRoute $state $routeGuard
                if ($routeStatus -eq 'Disconnected') { $endReason='VpnLost'; break }
                if ($routeStatus -eq 'Stopped') { break }
                if ($routeStatus -in @('Waiting','Repaired')) { Start-Sleep -Seconds 3; continue }
            }
            $healthFailures=$decision.Failures
            if ($decision.Rollback) { throw ($decision.Reason+' Stopping and restoring the original VPN configuration.') }
            if ($health.InternetTcpReachable -and -not $health.SystemDnsWorking) {
                $dnsFailures++
                if ($dnsFailures -eq 1) { Write-Event 'DNS_DEGRADED' 'DNS 暂时不可用，正在重试。' }
                if ($dnsFailures -ge 2 -and -not $PreferredDns -and $state.DnsSource -ne 'Physical' -and (Get-Date) -ge $nextDnsFallback) {
                    $nextDnsFallback=(Get-Date).AddSeconds(60)
                    try { [void](Repair-AutomaticDns $state) }
                    catch { Write-Event 'DNS_FALLBACK_FAILED' $_.Exception.Message }
                }
            } elseif ($health.SystemDnsWorking -and $dnsFailures -gt 0) {
                $dnsFailures=0
                Write-Event 'DNS_RECOVERED' 'DNS 解析已恢复。'
            }
            $nextHealthCheck=(Get-Date).AddSeconds(15)
        }
        Start-Sleep -Seconds 3
    }
} catch {
    $failure=$_
    $tunnelDown=$false
    if ($mustRestore) { try { $tunnelDown=Test-VpnDown } catch { } }
    if ($tunnelDown -and -not (Test-StopRequested)) { $endReason='VpnLost' }
    else {
        $endReason='Fault'
        Write-Event 'ERROR' ([ordered]@{Message=$failure.Exception.Message;Location=$failure.InvocationInfo.PositionMessage;Stack=$failure.ScriptStackTrace})
    }
} finally {
    $cleanupSucceeded=$true
    try {
        if ($ownsSession -and $endReason -eq 'NotStarted' -and (Test-StopRequested)) { $endReason='Stopped' }
        if ($ownsSession -and $endReason -in @('Stopped','Fault','Restore','VpnLost')) {
            $snapshot=if ($mustRestore) { $state } else { $null }
            Complete-VpnSession $endReason $snapshot $waitingForVpn
        }
    } catch {
        $cleanupSucceeded=$false
        if ($mustRestore -and $null -ne $state) {
            try { $state.Stage='rollback-failed'; Save-State $state } catch { }
        }
        Write-Event 'ROLLBACK_FAILED' $_.Exception.Message
    } finally {
        if ($null -ne $lock) { try { $lock.Dispose() } catch { } }
        if ($null -ne $script:OwnerProcess) { $script:OwnerProcess.Dispose() }
        Write-Event 'TOOL_COMPLETE' @{Reason=$endReason;CleanupSucceeded=$cleanupSucceeded}
    }
}
