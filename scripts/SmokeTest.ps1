<#
.SYNOPSIS
用 Windows 独立游戏进程验证本机直连、同步、重连和连接失败处理。
.DESCRIPTION
需要先构建 Builds/Windows/MultiplayerDemo.exe。通过日志中的状态和
SMOKE 标记判断结果，成功后写入 TestResults/smoke.json；结束时清理自建进程。
.EXAMPLE
.\scripts\SmokeTest.ps1
#>
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
$playerPath = Join-Path $projectPath 'Builds\Windows\MultiplayerDemo.exe'
$logDirectory = Join-Path $projectPath 'Logs\Smoke'
$resultDirectory = Join-Path $projectPath 'TestResults'
New-Item -ItemType Directory -Force -Path $logDirectory, $resultDirectory | Out-Null
if (-not (Test-Path -LiteralPath $playerPath)) { throw 'Build the Windows demo first.' }
# 保存每个由脚本创建的进程，异常中断时也能精确清理。
$ownedProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$summaries = [System.Collections.Generic.List[object]]::new()

# 临时绑定端口 0，让系统分配 UDP 端口；释放后到游戏绑定前仍存在被占用的窗口。
function Get-FreePort {
    $socket = [System.Net.Sockets.UdpClient]::new(0)
    try { return $socket.Client.LocalEndPoint.Port } finally { $socket.Dispose() }
}

# 为一个角色启动独立进程并返回进程/日志信息。--net-smoke 启用游戏内自动检查探针。
# Name 区分日志，Arguments 控制角色、端口、持续时间和预期结果。
function Start-Peer([string]$Name, [string]$Arguments) {
    $file = Join-Path $logDirectory ($Name + '.log')
    # 启动前清除旧日志，否则 Wait-Marker 可能误读上轮成功标记并提前启动依赖进程。
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    $process = Start-Process -FilePath $playerPath -ArgumentList ('-batchmode -nographics -logFile "' + $file + '" --net-smoke ' + $Arguments) -WindowStyle Hidden -PassThru
    $ownedProcesses.Add($process)
    return [pscustomobject]@{ Name=$Name; Process=$process; Log=$file }
}

# 等待真实日志事件再启动依赖步骤；进程提前退出或十五秒内未出现标记即失败。
function Wait-Marker($Peer, [string]$Marker) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Test-Path -LiteralPath $Peer.Log) -and (Select-String -LiteralPath $Peer.Log -SimpleMatch $Marker -Quiet)) { return }
        if ($Peer.Process.HasExited) { throw ($Peer.Name + ' exited before ' + $Marker + '. See ' + $Peer.Log) }
        Start-Sleep -Milliseconds 150
    }
    throw ('Timed out waiting for ' + $Peer.Name + ': ' + $Marker + '. See ' + $Peer.Log)
}

# 同时要求退出码为 0 和探针明确输出 PASS，避免把未执行检查的正常退出算作成功。
function Assert-Peer($Peer) {
    if (-not $Peer.Process.WaitForExit(45000)) { throw ('Peer timed out: ' + $Peer.Name) }
    if ($Peer.Process.ExitCode -ne 0 -or -not (Select-String -LiteralPath $Peer.Log -SimpleMatch '[SMOKE] PASS' -Quiet)) {
        throw ('Peer failed: ' + $Peer.Name + '. See ' + $Peer.Log)
    }
    $markers = @(Select-String -LiteralPath $Peer.Log -Pattern '\[SMOKE\]' | ForEach-Object { $_.Line })
    # 保留探针观测到的事件，便于从汇总报告追溯每个场景的结果。
    $summaries.Add([pscustomobject]@{ Name=$Peer.Name; ExitCode=$Peer.Process.ExitCode; Markers=$markers })
    Write-Output ($Peer.Name + ' passed.')
}

try {
    # 本轮失败时不能遗留上一轮的成功报告。
    $summaryPath = Join-Path $resultDirectory 'smoke.json'
    if (Test-Path -LiteralPath $summaryPath) { Remove-Item -LiteralPath $summaryPath }
    # 场景一：Host 占一个名额，客户端断开重连后验证远端移动同步。
    # 房间已满时再验证人数拒绝，以及错误游戏 ID 的兼容性拒绝。
    $port = Get-FreePort
    $hostPeer = Start-Peer 'host' "--net-mode host --net-port $port --net-max-players 2 --net-smoke-duration 25"
    Wait-Marker $hostPeer 'State=Host'
    $client = Start-Peer 'client-reconnect' "--net-mode client --net-port $port --net-reconnect --net-smoke-duration 16"
    Wait-Marker $client '[SMOKE] RECONNECTED'
    Wait-Marker $client 'remote_movement=True'
    $full = Start-Peer 'room-full' "--net-mode client --net-port $port --net-expect-error ROOM_FULL --net-smoke-duration 8"
    $wrongGame = Start-Peer 'wrong-game' "--net-mode client --net-port $port --net-app-id another.game --net-expect-error INCOMPATIBLE_GAME --net-smoke-duration 8"
    Assert-Peer $full
    Assert-Peer $wrongGame
    Assert-Peer $client
    Assert-Peer $hostPeer

    # 场景二：独立服务器不创建本地玩家，两名远端客户端连接并观察同步。
    $serverPort = Get-FreePort
    $server = Start-Peer 'dedicated-server' "--net-mode server --net-port $serverPort --net-smoke-duration 18"
    Wait-Marker $server 'State=Server'
    $first = Start-Peer 'dedicated-client-1' "--net-mode client --net-port $serverPort --net-smoke-duration 12"
    $second = Start-Peer 'dedicated-client-2' "--net-mode client --net-port $serverPort --net-smoke-duration 12"
    Assert-Peer $first
    Assert-Peer $second
    Assert-Peer $server

    # 场景三：Host 先结束，客户端应识别断线并按预期错误完成检查。
    $disconnectPort = Get-FreePort
    $shortHost = Start-Peer 'host-exits' "--net-mode host --net-port $disconnectPort --net-smoke-duration 6"
    Wait-Marker $shortHost 'State=Host'
    $disconnectedClient = Start-Peer 'host-disconnect-client' "--net-mode client --net-port $disconnectPort --net-expect-error Disconnected --net-smoke-duration 12"
    Assert-Peer $shortHost
    Assert-Peer $disconnectedClient

    # 场景四：访问没有监听者的端口，验证会话自己的连接超时机制。
    $unusedPort = Get-FreePort
    $timeout = Start-Peer 'connection-timeout' "--net-mode client --net-port $unusedPort --net-timeout 2 --net-expect-error timed --net-smoke-duration 8"
    Assert-Peer $timeout

    # 只有本轮所有场景都通过后才写出成功汇总。
    $summaries | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $resultDirectory 'smoke.json') -Encoding utf8
    Write-Output 'All standalone integration checks passed.'
}
finally {
    # 无论成功还是抛出异常，都只结束本脚本创建的进程，保留用户的编辑器和游戏窗口。
    foreach ($process in $ownedProcesses) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue }
        $process.Dispose()
    }
}
