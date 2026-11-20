<#
.SYNOPSIS
用真实本机双进程对比三种输入/同步时序，客户端可单独注入中度弱网。
.DESCRIPTION
先构建 Windows 基础示例。测试原版 30、对齐 30、响应 60 的实际发送频率和起步响应。
报告保留原始有界样本；不对采样摘要的 P95 再求平均，不把测量确认当作画面延迟。
只清理脚本创建的进程，不修改系统网络或部署服务器。
#>
param([ValidateSet('All','Normal','Moderate')][string]$Profile = 'All',
    [ValidateRange(16,300)][int]$DurationSeconds = 20)
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
$playerPath = Join-Path $projectPath 'Builds\Windows\MultiplayerDemo.exe'
$logsPath = Join-Path $projectPath 'Logs\Timing'
$reportsPath = Join-Path $projectPath 'TestResults\Timing'
New-Item -ItemType Directory -Force -Path $logsPath, $reportsPath | Out-Null
if (-not (Test-Path -LiteralPath $playerPath)) { throw 'Build Windows Demo first.' }
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$results = [Collections.Generic.List[object]]::new()
$summaryPath = Join-Path $reportsPath 'summary.json'
if (Test-Path -LiteralPath $summaryPath) { Remove-Item -LiteralPath $summaryPath }

# 每个进程单独日志；固定测试目标 120 FPS，真实帧率仍以报告为准。
function Start-Peer([string]$Name, [string]$Arguments) {
    $log = Join-Path $logsPath ($Name + '.log')
    $report = Join-Path $reportsPath ($Name + '.json')
    foreach ($path in @($log,$report)) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
    $process = Start-Process -FilePath $playerPath -ArgumentList ('-batchmode -nographics -logFile "' + $log + '" --net-target-fps 120 --net-prediction legacy ' + $Arguments) -WindowStyle Hidden -PassThru
    $ownedProcesses.Add($process)
    return [pscustomobject]@{ Process=$process; Log=$log; Report=$report }
}
function Wait-Marker($Peer, [string]$Marker) {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Test-Path -LiteralPath $Peer.Log) -and (Select-String -LiteralPath $Peer.Log -SimpleMatch $Marker -Quiet)) { return }
        if ($Peer.Process.HasExited) { throw "Peer exited early: $($Peer.Log)" }
        Start-Sleep -Milliseconds 100
    }
    throw "Missing $Marker in $($Peer.Log)"
}

try {
    $profiles = if ($Profile -eq 'All') { @('Normal','Moderate') } else { @($Profile) }
    foreach ($network in $profiles) {
        foreach ($preset in @('Legacy30','Aligned30','Responsive60')) {
            $socket = [Net.Sockets.UdpClient]::new(0)
            $port = $socket.Client.LocalEndPoint.Port
            $socket.Dispose()
            $name = "$network-$preset"
            # Host 不注入故障；仅在客户端施加发送延迟/抖动和收发丢包，便于解释结果。
            $server = Start-Peer ($name + '-host') "--net-mode host --net-port $port --net-timing $preset"
            Wait-Marker $server 'State=Host'
            $sim = if ($network -eq 'Normal') { 'None' } else { 'Moderate' }
            $reportPath = Join-Path $reportsPath ($name + '-client.json')
            $client = Start-Peer ($name + '-client') ('--net-mode client --net-port ' + $port + ' --net-timing ' + $preset + ' --net-sim ' + $sim + ' --net-latency-test --net-latency-duration ' + $DurationSeconds + ' --net-report "' + $reportPath + '"')
            $deadline = [DateTime]::UtcNow.AddSeconds($DurationSeconds + 40)
            while (-not $client.Process.WaitForExit(500) -and [DateTime]::UtcNow -lt $deadline) { }
            if (-not $client.Process.HasExited -or $client.Process.ExitCode -ne 0 -or
                -not (Select-String -LiteralPath $client.Log -SimpleMatch '[TIMING] PASS' -Quiet)) { throw "Timing probe failed: $($client.Log)" }
            if (-not (Test-Path -LiteralPath $client.Report)) { throw 'Missing current timing report.' }
            $report = Get-Content -LiteralPath $client.Report -Raw | ConvertFrom-Json
            $data = $report.timingMeasurements.latest
            $targetHz = if ($preset -eq 'Responsive60') { 60 } else { 30 }
            # 实际频率容许主循环抖动；过载/未正常发送的运行不能作为有效性能对比。
            if ($data.settings.networkTickRate -ne $targetHz -or $data.observedInputHz -lt ($targetHz * .6) -or
                $data.observedInputHz -gt ($targetHz * 1.3) -or $data.observedServerSimulationHz -lt 20 -or
                $data.framesPerSecond -lt 60 -or $data.confirmation.count -lt 20 -or $data.visibleOnset.count -lt 3 -or
                $data.visibleOnset.ageMs -gt 6000) { throw "Invalid timing coverage: $($client.Report)" }
            $results.Add([pscustomobject]@{
                Network=$network; Preset=$preset; FramesPerSecond=$data.framesPerSecond
                InputHz=$data.observedInputHz; ServerHz=$data.observedServerSimulationHz
                ConfirmationMeanMs=$data.confirmation.meanMs; ConfirmationP95Ms=$data.confirmation.p95Ms
                OnsetCount=$data.visibleOnset.count; OnsetMeanMs=$data.visibleOnset.meanMs; OnsetP95Ms=$data.visibleOnset.p95Ms
                ClientReport=$client.Report; Result='Passed'
            })
            Write-Output ("{0}: onset {1:N1} ms, P95 {2:N1} ms; input {3:N1} Hz" -f $name,$data.visibleOnset.meanMs,$data.visibleOnset.p95Ms,$data.observedInputHz)
            if (-not $server.Process.HasExited) { Stop-Process -Id $server.Process.Id }
        }
    }
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding utf8
}
finally {
    foreach ($process in $ownedProcesses) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue }
        $process.Dispose()
    }
}
