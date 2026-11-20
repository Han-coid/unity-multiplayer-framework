<#
.SYNOPSIS
用三个本地独立进程检查大厅权限、名单、准备、场景屏障、锁房及踢人。
.DESCRIPTION
需要先通过 Build Windows Lobby Demo 构建。仅操作本机进程和 UDP，不部署服务器。
#>
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
$playerPath = Join-Path $projectPath 'Builds\LobbyWindows\LobbyDemo.exe'
$logsPath = Join-Path $projectPath 'Logs\Lobby'
$resultPath = Join-Path $projectPath 'TestResults\lobby.json'
if (-not (Test-Path -LiteralPath $playerPath)) { throw 'Build Windows Lobby Demo first.' }
New-Item -ItemType Directory -Force -Path $logsPath | Out-Null
if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
$socket = [Net.Sockets.UdpClient]::new(0)
try { $port = $socket.Client.LocalEndPoint.Port } finally { $socket.Dispose() }
$ownedProcesses = [Collections.Generic.List[System.Diagnostics.Process]]::new()
$results = [Collections.Generic.List[object]]::new()

# 每次运行先清理自己的旧日志，等待标记只能来自本轮。
function Start-Peer([string]$Name, [string]$Arguments) {
    $log = Join-Path $logsPath ($Name + '.log')
    if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log }
    $process = Start-Process -FilePath $playerPath -ArgumentList ('-batchmode -nographics -logFile "' + $log + '" --net-no-panel --lobby-test --net-port ' + $port + ' ' + $Arguments) -WindowStyle Hidden -PassThru
    $ownedProcesses.Add($process)
    return [pscustomobject]@{ Name=$Name; Process=$process; Log=$log }
}

function Wait-Marker($Peer, [string]$Marker) {
    $deadline = [DateTime]::UtcNow.AddSeconds(55)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Test-Path -LiteralPath $Peer.Log) -and (Select-String -LiteralPath $Peer.Log -SimpleMatch $Marker -Quiet)) { return }
        if ($Peer.Process.HasExited) { throw "$($Peer.Name) exited before $Marker. See $($Peer.Log)" }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out: $Marker. See $($Peer.Log)"
}

function Assert-Peer($Peer, [string[]]$Markers) {
    if (-not $Peer.Process.WaitForExit(60000)) { throw "$($Peer.Name) timed out." }
    if ($Peer.Process.ExitCode -ne 0) { throw "$($Peer.Name) failed. See $($Peer.Log)" }
    foreach ($marker in $Markers + @('[LOBBY_TEST] PASS')) {
        if (-not (Select-String -LiteralPath $Peer.Log -SimpleMatch $marker -Quiet)) { throw "Missing $marker in $($Peer.Log)" }
    }
    $results.Add([pscustomobject]@{ Name=$Peer.Name; ExitCode=$Peer.Process.ExitCode; Markers=$Markers; Log=$Peer.Log })
    Write-Output ($Peer.Name + ' passed.')
}

try {
    $hostPeer = Start-Peer 'host' '--net-mode host --net-nickname Host --lobby-role host'
    Wait-Marker $hostPeer 'State=Host'
    $member = Start-Peer 'member' '--net-mode client --net-nickname Guest --lobby-role member'
    Wait-Marker $hostPeer '[LOBBY_TEST] PLAYING'
    $late = Start-Peer 'late-join' '--net-mode client --net-nickname Late --lobby-role late'
    Assert-Peer $late @('[LOBBY_TEST] LATE_JOIN_REJECTED')
    Assert-Peer $member @('[LOBBY_TEST] ROSTER_SYNCED', '[LOBBY_TEST] UNAUTHORIZED_REQUESTS_REJECTED', '[LOBBY_TEST] PLAYING', '[LOBBY_TEST] KICK_REASON_RECEIVED')
    Assert-Peer $hostPeer @('[LOBBY_TEST] ROSTER_SYNCED', '[LOBBY_TEST] PLAYING', '[LOBBY_TEST] RETURNED', '[LOBBY_TEST] KICKED_AND_REMOVED')
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultPath -Encoding utf8
    Write-Output 'All lobby integration checks passed.'
}
finally {
    foreach ($process in $ownedProcesses) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue }
        $process.Dispose()
    }
}
