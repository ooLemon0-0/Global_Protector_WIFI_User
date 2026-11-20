param([ValidateSet('Manual','Automatic','DefaultSite','Ssh','FileTransfer','ResumeTransfer','Library')][string]$Mode = 'Manual',[string]$TransferApp)
$ErrorActionPreference = 'Stop'
$target = '10.213.134.31'
$webUrl = 'http://127.0.0.1:23180/'

function Find-LocalRelay {
    $channel = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'session.txt') -Raw).Trim()
    if ($channel -notmatch '^[a-f0-9]{24}$') { throw 'Invalid relay session ID.' }
    $entry = 'HKCU:\Software\Microsoft\Terminal Server Client\Default\AddIns\Web3000-' + $channel
    $registered = (Get-ItemProperty -LiteralPath $entry -Name 'Name' -ErrorAction SilentlyContinue).Name
    if (!$registered -or !(Test-Path -LiteralPath $registered -PathType Leaf)) { return }
    $expected = Join-Path $PSScriptRoot 'RdpWebChannel.dll'
    if ((Get-FileHash -LiteralPath $registered).Hash -ne (Get-FileHash -LiteralPath $expected).Hash) { return }
    $worker = Join-Path (Split-Path -LiteralPath $registered) 'LocalRelay.exe'
    $expectedWorker = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'LocalRelay.exe')).Hash
    $session = (Get-Process -Id $PID).SessionId
    Get-Process -Name LocalRelay -ErrorAction SilentlyContinue | Where-Object {
        $_.SessionId -eq $session -and $_.Path -eq $worker -and
            (Get-FileHash -LiteralPath $_.Path).Hash -eq $expectedWorker
    } | Select-Object -First 1
}

function Find-ConnectedRemoteDesktop {
    $session = (Get-Process -Id $PID).SessionId
    $clients = @(Get-Process -Name mstsc -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session | Select-Object -ExpandProperty Id)
    if (!$clients.Count) { return }
    $connection = Get-NetTCPConnection -RemoteAddress $target -RemotePort 3389 -State Established -ErrorAction SilentlyContinue |
        Where-Object OwningProcess -in $clients | Select-Object -First 1
    if ($connection) { Get-Process -Id $connection.OwningProcess -ErrorAction SilentlyContinue }
}

function Find-StartingVietnamRemoteDesktop {
    $session = (Get-Process -Id $PID).SessionId
    Get-CimInstance Win32_Process -Filter "Name='mstsc.exe' AND SessionId=$session" |
        Where-Object CommandLine -match '(?:\\|/)Vietnam-Web\.rdp(?="|\s|$)' | Select-Object -First 1
}

function Test-VietnamRemoteDesktopReachable {
    $socket = [Net.Sockets.TcpClient]::new()
    try { return $socket.ConnectAsync($target,3389).Wait(1200) -and $socket.Connected }
    catch { return $false }
    finally { $socket.Dispose() }
}

function Test-RemoteDesktopRelayPlugin($Desktop, $Relay) {
    $dll = Join-Path (Split-Path -LiteralPath $Relay.Path) 'RdpWebChannel.dll'
    return $null -ne ($Desktop.Modules | Where-Object FileName -eq $dll | Select-Object -First 1)
}

function Test-WebRelay([int]$RelayId,[int]$Port = 23180) {
    if (!(Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort $Port -OwningProcess $RelayId -State Listen -ErrorAction SilentlyContinue)) { return $false }
    $request = [Net.WebRequest]::CreateHttp("http://127.0.0.1:$Port/")
    $request.Proxy = $null
    $request.AllowAutoRedirect = $false
    $request.Timeout = 2500
    try {
        $response = $request.GetResponse()
        try { return ([int]$response.StatusCode -ge 200 -and [int]$response.StatusCode -lt 400) }
        finally { $response.Dispose() }
    } catch [Net.WebException] {
        if ($Port -eq 23181 -and $_.Exception.Response) {
            $_.Exception.Response.Dispose()
            return $true
        }
        return $false
    }
}

function Open-VietnamDesktop([string]$File, [string]$Arguments = '') {
    # Ask the interactive Explorer desktop to launch client applications with its token.
    $shell = New-Object -ComObject Shell.Application
    $handle = 0
    $desktop = $shell.Windows().FindWindowSW(0, 0, 8, [ref]$handle, 1)
    if (!$desktop) { throw '找不到当前用户的 Windows 桌面。' }
    $desktop.Document.Application.ShellExecute($File, $Arguments, '', 'open', 1)
}

function Test-SshRelay([int]$RelayId) {
    if (!(Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort 23222 -OwningProcess $RelayId -State Listen -ErrorAction SilentlyContinue)) { return $false }
    $socket = [Net.Sockets.TcpClient]::new()
    try {
        if (!$socket.ConnectAsync('127.0.0.1',23222).Wait(1000)) { return $false }
        $stream = $socket.GetStream()
        $buffer = [byte[]]::new(512)
        $banner = ''
        $elapsed = [Diagnostics.Stopwatch]::StartNew()
        while ($banner.Length -lt 2048 -and $elapsed.ElapsedMilliseconds -lt 5500) {
            $stream.ReadTimeout = [Math]::Max(1,5500 - [int]$elapsed.ElapsedMilliseconds)
            $count = $stream.Read($buffer,0,$buffer.Length)
            if (!$count) { return $false }
            $banner += [Text.Encoding]::ASCII.GetString($buffer,0,$count)
            if ($banner -match '(?m)^SSH-(2\.0|1\.99)-[^\r\n]+\r?\n') { return $true }
        }
        return $false
    } catch { return $false } finally { $socket.Dispose() }
}

function Start-VietnamRemoteDesktop {
    $rdpPath = Join-Path (Join-Path $PSScriptRoot 'runtime') 'Vietnam-Web.rdp'
    @(
        "full address:s:$target"
        'username:s:Administrator'
        'screen mode id:i:1'
        'desktopwidth:i:1280'
        'desktopheight:i:800'
        'authentication level:i:2'
        'enablecredsspsupport:i:1'
        'autoreconnection enabled:i:1'
        'redirectclipboard:i:1'
        'drivestoredirect:s:'
        'redirectprinters:i:0'
        'remoteapplicationmode:i:0'
    ) | Set-Content -LiteralPath $rdpPath -Encoding Unicode
    Open-VietnamDesktop (Join-Path $env:WINDIR 'System32\mstsc.exe') ('"' + $rdpPath + '"')
}

function Start-VietnamLocalRelay {
    $channel = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'session.txt') -Raw).Trim()
    if ($channel -notmatch '^[a-f0-9]{24}$') { throw 'Invalid relay session ID.' }
    $entry = 'HKCU:\Software\Microsoft\Terminal Server Client\Default\AddIns\Web3000-' + $channel
    $plugin = (Get-ItemProperty -LiteralPath $entry -Name Name -ErrorAction SilentlyContinue).Name
    if ($plugin -and (Test-Path -LiteralPath $plugin -PathType Leaf) -and
        (Get-FileHash -LiteralPath $plugin).Hash -eq (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'RdpWebChannel.dll')).Hash) {
        $oldWorker = Join-Path (Split-Path -LiteralPath $plugin) 'LocalRelay.exe'
        $compatiblePrevious = @('1C989B4088656FE1D733AA178A3C64C0C12B7CC1FA777278D8B9C013E15F97D4',
                                'FBA70BA3B4610CCB3666D2293DD3237C6F6E9DED49A9E4CEE236C7B979E82E39',
                                'A797C1FF89FE057ACBE80EDE2D96B56845EC7C1D008E78EF5F140059C28514B6',
                                '7140A8DBF6D8C630E0FFE554CD1A0C55E40F513714F2D3A0C480E02B5787AB4C',
                                'D8C18CE2E4807C9514BEC1CE2AEF409E3D9984E7C0A35C194B19784CAF01E629',
                                'D5090A1CBB81C6CBEAD9CBD46E4AC8207B7E97B9FCE0AB65F30D18F75F8EF570')
        if ((Test-Path -LiteralPath $oldWorker -PathType Leaf) -and
            (Get-FileHash -LiteralPath $oldWorker).Hash -in $compatiblePrevious) {
            $session = (Get-Process -Id $PID).SessionId
            $old = Get-Process -Name LocalRelay -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session -and $_.Path -eq $oldWorker }
            if ($old) {
                if (Find-ConnectedRemoteDesktop) { throw '当前远程桌面还在使用旧版中转。请在用完后断开该窗口，再点「打开越南网页」。' }
                foreach ($process in $old) {
                    Stop-Process -Id $process.Id
                    if (!$process.WaitForExit(4000)) { throw '旧版中转尚未退出，请稍后重试。' }
                }
            }
        }
    }
    & (Join-Path $PSScriptRoot 'Start-LocalBackground.ps1')
}

function Invoke-VietnamWeb([bool]$OpenBrowser) {
    $relay = Find-LocalRelay
    if (!$relay) {
        Start-VietnamLocalRelay
        $relay = Find-LocalRelay
    }
    if (!$relay) { throw '本机网页中转未启动。' }
    if ($OpenBrowser -and (Test-WebRelay $relay.Id)) {
        Open-VietnamDesktop $webUrl
        Write-Output '越南网页已打开。'
        return
    }
    $desktop = if ($OpenBrowser) { Find-ConnectedRemoteDesktop }
    if (!$desktop) {
        Start-VietnamRemoteDesktop
        if ($OpenBrowser) { Write-Output '远程桌面已打开。请完成登录，网页会自动打开。' }
    } elseif (Test-RemoteDesktopRelayPlugin $desktop $relay) {
        Write-Output '已检测到当前远程桌面连接，正在等待网页中转。'
    } else {
        Write-Output '当前远程桌面未加载中转插件。请断开该远程桌面窗口，再在助手内点「打开越南网页」。'
        return
    }
    if (!$OpenBrowser) { return }
    $deadline = [DateTime]::UtcNow.AddMinutes(4)
    do {
        Start-Sleep -Seconds 2
        if (!(Get-Process -Id $relay.Id -ErrorAction SilentlyContinue)) { throw '本机网页中转已退出。' }
        if (Test-WebRelay $relay.Id) {
            Open-VietnamDesktop $webUrl
            Write-Output '越南网页已打开。'
            return
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    Write-Output '等待越南网页中转超时。请确认已登录远程桌面且目标端已安装中转，然后在助手内点「打开越南网页」。'
}

function Invoke-VietnamTransferReconnect {
    if ((Find-ConnectedRemoteDesktop) -or (Find-StartingVietnamRemoteDesktop)) {
        Write-Output '等待远程桌面恢复或完成登录…'
        return
    }
    if (!(Test-VietnamRemoteDesktopReachable)) {
        Write-Output '远程桌面暂不可达，正在等待网络恢复…'
        return
    }
    if (!(Find-LocalRelay)) { Start-VietnamLocalRelay }
    if (!(Find-LocalRelay)) { throw '本机文件中转未启动。' }
    if (!(Find-ConnectedRemoteDesktop) -and !(Find-StartingVietnamRemoteDesktop)) {
        Start-VietnamRemoteDesktop
    }
    Write-Output '远程桌面正在重新连接，如有提示请登录。'
}

if ($Mode -ne 'Library') {
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'runtime') -PathType Container)) {
        New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'runtime') -Force | Out-Null
    }
    try {
        if ($Mode -eq 'FileTransfer') {
            if (!$TransferApp -or ![IO.Path]::IsPathRooted($TransferApp) -or !(Test-Path -LiteralPath $TransferApp -PathType Leaf)) { throw '找不到助手程序。' }
            Open-VietnamDesktop $TransferApp '--vietnam-file-transfer'
        } elseif ($Mode -eq 'ResumeTransfer') {
            Invoke-VietnamTransferReconnect
        } elseif ($Mode -eq 'DefaultSite') {
            $relay = Find-LocalRelay
            if (!$relay -or !(Test-WebRelay $relay.Id 23181)) { throw '默认网站未就绪。请保持远程桌面连接，并确认远端已更新中转、网站的 80 端口可用。' }
            Open-VietnamDesktop 'http://127.0.0.1:23181/'
            Write-Output '默认网站已打开。'
        } elseif ($Mode -eq 'Ssh') {
            $relay = Find-LocalRelay
            if (!$relay -or !(Test-SshRelay $relay.Id)) { throw '现场 SSH 未就绪。请保持远程桌面连接，并确认远端已升级中转、现场机的 TCP 22 可用。' }
            $ssh = Join-Path $env:WINDIR 'System32\OpenSSH\ssh.exe'
            if (!(Test-Path -LiteralPath $ssh -PathType Leaf)) { throw '请先安装 Windows 的 OpenSSH 客户端。' }
            Open-VietnamDesktop $ssh '-p 23222 -o HostKeyAlias=10.214.1.18 cisdi@127.0.0.1'
            Write-Output '现场 SSH 已打开，请按平时方式登录。'
        } else { Invoke-VietnamWeb ($Mode -eq 'Automatic') }
    }
    catch { Write-Output ('越南网页中转未启动：' + $_.Exception.Message); exit 1 }
}
