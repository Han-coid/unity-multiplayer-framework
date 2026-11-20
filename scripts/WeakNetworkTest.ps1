<#
.SYNOPSIS
在本机用真实 Windows 独立进程验证弱网、诊断报告和短时断网恢复。
.DESCRIPTION
先构建 Windows Demo。依次运行正常、中等和严重弱网，只有客户端注入故障。
每组检查远端移动、Ready、真实流量、延迟采样，以及恢复后的重新同步。
不修改防火墙、不访问 Linux 服务器；finally 只清理本脚本创建的进程。
.PARAMETER Profile
All 运行全部配置，也可选择 Normal、Moderate、Severe 单独验证。
.EXAMPLE
.\scripts\WeakNetworkTest.ps1 -Profile Moderate
#>
param([ValidateSet('All', 'Normal', 'Moderate', 'Severe')][string]$Profile = 'All')
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
$playerPath = Join-Path $projectPath 'Builds\Windows\MultiplayerDemo.exe'
$logsPath = Join-Path $projectPath 'Logs\WeakNetwork'
$reportsPath = Join-Path $projectPath 'TestResults\WeakNetwork'
New-Item -ItemType Directory -Force -Path $logsPath, $reportsPath | Out-Null
if (-not (Test-Path -LiteralPath $playerPath)) { throw 'Build Windows Demo first.' }
$ownedProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$results = [Collections.Generic.List[object]]::new()
# 清除旧的成功汇总，避免本轮失败后被误认为仍然通过。
$summaryPath = Join-Path $reportsPath 'summary.json'
if (Test-Path -LiteralPath $summaryPath) { Remove-Item -LiteralPath $summaryPath }

# 让系统分配 UDP 端口；绑定游戏前仍可能被其他程序抢占，失败时应查看游戏日志。
function Get-FreePort {
    $socket = [Net.Sockets.UdpClient]::new(0)
    try { return $socket.Client.LocalEndPoint.Port } finally { $socket.Dispose() }
}

# 使用独立日志和 JSON 文件，清理时保存确切进程对象而不按进程名称结束。
function Start-Peer([string]$Name, [string]$Arguments) {
    $log = Join-Path $logsPath ($Name + '.log')
    $report = Join-Path $reportsPath ($Name + '.json')
    if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log }
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
    $process = Start-Process -FilePath $playerPath -ArgumentList ('-batchmode -nographics -logFile "' + $log + '" --net-smoke --net-report "' + $report + '" ' + $Arguments) -WindowStyle Hidden -PassThru
    $ownedProcesses.Add($process)
    return [pscustomobject]@{ Name=$Name; Process=$process; Log=$log; Report=$report }
}

# 依据真实 Server/Host 事件启动客户端，避免固定等待时间掩盖启动失败。
function Wait-Marker($Peer, [string]$Marker) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Test-Path -LiteralPath $Peer.Log) -and (Select-String -LiteralPath $Peer.Log -SimpleMatch $Marker -Quiet)) { return }
        if ($Peer.Process.HasExited) { throw "$($Peer.Name) exited early. See $($Peer.Log)" }
        Start-Sleep -Milliseconds 150
    }
    throw "Missing marker $Marker. See $($Peer.Log)"
}

# 成功必须同时具备退出码、探针标记和本轮生成的有效报告。
function Assert-Peer($Peer) {
    if (-not $Peer.Process.WaitForExit(45000)) { throw "$($Peer.Name) timed out." }
    if ($Peer.Process.ExitCode -ne 0 -or -not (Select-String -LiteralPath $Peer.Log -SimpleMatch '[SMOKE] PASS' -Quiet)) {
        throw "$($Peer.Name) failed. See $($Peer.Log)"
    }
    if (-not (Test-Path -LiteralPath $Peer.Report)) { throw "No current report for $($Peer.Name)." }
    return Get-Content -LiteralPath $Peer.Report -Raw | ConvertFrom-Json
}

try {
    $profiles = if ($Profile -eq 'All') { @('Normal', 'Moderate', 'Severe') } else { @($Profile) }
    foreach ($name in $profiles) {
        $port = Get-FreePort
        $preset = if ($name -eq 'Normal') { 'None' } else { $name }
        # Host 比客户端晚结束，因此结束时远端数量可能为 0；用历史样本验证其真实流量。
        $hostPeer = Start-Peer ($name + '-host') "--net-mode host --net-port $port --net-smoke-duration 32"
        Wait-Marker $hostPeer 'State=Host'
        $client = Start-Peer ($name + '-client') "--net-mode client --net-port $port --net-sim $preset --net-smoke-duration 23 --net-require-diagnostics --net-blackout-after 6 --net-blackout-seconds 3"
        $report = Assert-Peer $client
        foreach ($marker in @('[SMOKE] BLACKOUT_STARTED', '[SMOKE] BLACKOUT_RESTORED', '[SMOKE] DIAGNOSTICS')) {
            if (-not (Select-String -LiteralPath $client.Log -SimpleMatch $marker -Quiet)) { throw "Missing $marker in $($client.Log)." }
        }
        $online = @($report.samples | Where-Object { $_.driverAvailable -and $_.remotePeers -gt 0 })
        if ($online.Count -eq 0 -or ($online | Measure-Object receivedBytes -Maximum).Maximum -le 0 -or ($online | Measure-Object sentBytes -Maximum).Maximum -le 0) {
            throw "No measured traffic in $($client.Report)."
        }
        if (-not @($online | Where-Object { $_.simulation.SendLossPercent -eq 100 }).Count) { throw 'Blackout was not sampled.' }
        $expectedDelay = switch ($name) { 'Moderate' { 100 } 'Severe' { 250 } default { 0 } }
        $recovered = @($online | Where-Object { $_.simulation.SendLossPercent -lt 100 })
        if ($recovered[-1].simulation.SendDelayMs -ne $expectedDelay) { throw 'Simulator did not restore the previous preset.' }
        $rtts = @($recovered | ForEach-Object { $_.peers } | Where-Object { $_.available } | ForEach-Object { $_.milliseconds })
        if ($expectedDelay -gt 0 -and ($rtts.Count -eq 0 -or ($rtts | Measure-Object -Maximum).Maximum -lt ($expectedDelay - 20))) {
            throw "Configured send delay was not reflected in measured RTT. See $($client.Report)."
        }

        # Host 仍在运行时，用另一个真实客户端的双向 100% 丢包验证建连超时。
        $blocked = Start-Peer ($name + '-blocked') "--net-mode client --net-port $port --net-send-loss 100 --net-receive-loss 100 --net-timeout 2 --net-expect-error timed --net-smoke-duration 6"
        $null = Assert-Peer $blocked
        $hostReport = Assert-Peer $hostPeer
        if (-not @($hostReport.samples | Where-Object { $_.remotePeers -gt 0 -and $_.receivedBytes -gt 0 -and $_.sentBytes -gt 0 }).Count) {
            throw "Host never recorded remote traffic. See $($hostPeer.Report)."
        }
        $maximumRtt = if ($rtts.Count) { ($rtts | Measure-Object -Maximum).Maximum } else { $null }
        $results.Add([pscustomobject]@{ Profile=$name; Result='Passed'; MaxObservedRttMs=$maximumRtt; ClientReport=$client.Report; HostReport=$hostPeer.Report; BlackoutRecovery='Passed'; FullLossTimeout='Passed' })
        Write-Output "$name passed: real replication, diagnostics, blackout recovery and full-loss timeout."
    }
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportsPath 'summary.json') -Encoding utf8
}
finally {
    foreach ($process in $ownedProcesses) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue }
        $process.Dispose()
    }
}
