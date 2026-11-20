<#
.SYNOPSIS
真实独立进程验证六秒双向断网后的自动重连、角色恢复、过期、取消和踢出撤销。
.DESCRIPTION
需要先构建基础与大厅 Windows Demo。只在测试探针中缩短 UTP 超时，不修改正式项目默认值。
所有日志仅含测试标记，不写入或输出重连凭证；只清理本脚本创建的进程。
#>
param([ValidateSet('All','Recover','Expire','Cancel','Leave','Kick','Lobby','AccountSwitch')][string]$Case = 'All', [switch]$DevelopmentAccounts)
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
$logsPath = Join-Path $projectPath 'Logs\Recovery'
$reportsPath = Join-Path $projectPath 'TestResults\Recovery'
New-Item -ItemType Directory -Force -Path $logsPath,$reportsPath | Out-Null
$owned = [Collections.Generic.List[System.Diagnostics.Process]]::new()
$results = [Collections.Generic.List[object]]::new()
$summary = Join-Path $reportsPath 'summary.json'
if (Test-Path -LiteralPath $summary) { Remove-Item -LiteralPath $summary }

function Start-Peer([string]$Name,[string]$Player,[string]$Arguments) {
    $log = Join-Path $logsPath ($Name + '.log')
    if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log }
    $process = Start-Process -FilePath $Player -ArgumentList ('-batchmode -nographics -logFile "' + $log + '" ' + $Arguments) -WindowStyle Hidden -PassThru
    $owned.Add($process)
    return [pscustomobject]@{Name=$Name; Process=$process; Log=$log}
}
function Wait-Marker($Peer,[string]$Marker) {
    $until = [DateTime]::UtcNow.AddSeconds(35)
    while ([DateTime]::UtcNow -lt $until) {
        if ((Test-Path -LiteralPath $Peer.Log) -and (Select-String -LiteralPath $Peer.Log -SimpleMatch $Marker -Quiet)) { return }
        if ($Peer.Process.HasExited) { throw "Early exit: $($Peer.Log)" }
        Start-Sleep -Milliseconds 100
    }
    throw "Missing $Marker in $($Peer.Log)"
}
function Assert-Peer($Peer,[string]$Marker) {
    $until = [DateTime]::UtcNow.AddSeconds(65)
    while (-not $Peer.Process.WaitForExit(500) -and [DateTime]::UtcNow -lt $until) { }
    if (-not $Peer.Process.HasExited -or $Peer.Process.ExitCode -ne 0 -or
        -not (Select-String -LiteralPath $Peer.Log -SimpleMatch $Marker -Quiet)) { throw "Failed: $($Peer.Log)" }
}
try {
    if ($Case -eq 'AccountSwitch' -and -not $DevelopmentAccounts) { throw 'AccountSwitch requires -DevelopmentAccounts.' }
    $cases = if ($Case -eq 'All') { @('Recover','Expire','Cancel','Leave','Kick','Lobby') } else { @($Case) }
    if ($Case -eq 'All' -and $DevelopmentAccounts) { $cases += 'AccountSwitch' }
    foreach ($testCase in $cases) {
        $relative = if ($testCase -eq 'Lobby') { 'Builds\LobbyWindows\LobbyDemo.exe' } else { 'Builds\Windows\MultiplayerDemo.exe' }
        $player = Join-Path $projectPath $relative
        if (-not (Test-Path -LiteralPath $player)) { throw 'Build both Windows demos first.' }
        $socket = [Net.Sockets.UdpClient]::new(0)
        $port = $socket.Client.LocalEndPoint.Port; $socket.Dispose()
        $capacity = if ($testCase -eq 'Lobby') { 3 } else { 2 }
        $common = "--net-port $port --net-max-players $capacity --recovery-test --recovery-case $testCase"
        if ($DevelopmentAccounts) { $common += ' --net-auth development' }
        $server = Start-Peer ($testCase + '-host') $player ($common + ' --net-mode host --net-nickname 房主 --net-account host')
        Wait-Marker $server 'State=Host'
        $client = Start-Peer ($testCase + '-client') $player ($common + ' --net-mode client --net-nickname 恢复玩家 --net-account guest')
        $extra = $null
        if ($testCase -eq 'Recover' -or $testCase -eq 'Lobby') {
            Wait-Marker $server '[RECOVERY_TEST] SEAT_HELD'
            # 在原玩家离线时发起真正的新连接：保留席位不能被占用，进行中的大厅也不应被恢复特权绕过。
            $rejection = if ($testCase -eq 'Lobby') { 'GAME_IN_PROGRESS' } else { 'ROOM_FULL' }
            $authentication = if ($DevelopmentAccounts) { '--net-auth development --net-account newcomer' } else { '' }
            $extra = Start-Peer ($testCase + '-newcomer') $player "--net-mode client --net-port $port --net-smoke --net-min-players 1 --net-timeout 2 --net-smoke-duration 5 --net-expect-error $rejection $authentication"
            Assert-Peer $extra '[SMOKE] PASS'
        }
        Assert-Peer $client '[RECOVERY_TEST] PASS'
        Assert-Peer $server '[RECOVERY_TEST] PASS'
        $results.Add([pscustomobject]@{Case=$testCase; Result='Passed'; HostLog=$server.Log; ClientLog=$client.Log; NewcomerRejection=if($extra){'Passed'}else{'NotApplicable'}})
        Write-Output "$testCase passed."
    }
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summary -Encoding utf8
}
finally {
    foreach ($process in $owned) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue }
        $process.Dispose()
    }
}
