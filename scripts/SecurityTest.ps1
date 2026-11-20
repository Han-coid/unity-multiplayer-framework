<#
.SYNOPSIS
真实独立进程验证账号准入拒绝、重复登录及统一请求校验；所有身份仅为开发模拟。
.DESCRIPTION
先构建基础 Windows Demo。验证无凭证、伪造、过期、封禁、重复账号、超时和迟到验证结果。
主客户端同时通过真实网络测试权限、所有权、NaN、阶段、大小、重放和限流。
不写入真实登录票据；只清理脚本创建的进程，不访问远端服务器或账号后端。
#>
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
$playerPath = Join-Path $projectPath 'Builds\Windows\MultiplayerDemo.exe'
$logsPath = Join-Path $projectPath 'Logs\Security'
$resultsPath = Join-Path $projectPath 'TestResults\Security'
New-Item -ItemType Directory -Force -Path $logsPath,$resultsPath | Out-Null
if (-not (Test-Path -LiteralPath $playerPath)) { throw 'Build Windows Demo first.' }
$summary = Join-Path $resultsPath 'summary.json'
$stopFile = Join-Path $resultsPath 'finished.marker'
foreach($path in @($summary,$stopFile,($stopFile+'.host-checked'))){if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path}}
$owned = [Collections.Generic.List[System.Diagnostics.Process]]::new()
$results = [Collections.Generic.List[object]]::new()
$socket=[Net.Sockets.UdpClient]::new(0); $port=$socket.Client.LocalEndPoint.Port; $socket.Dispose()
function Start-Peer([string]$Name,[string]$Arguments) {
    $log=Join-Path $logsPath ($Name+'.log')
    if(Test-Path -LiteralPath $log){Remove-Item -LiteralPath $log}
    $process=Start-Process -FilePath $playerPath -ArgumentList ('-batchmode -nographics -logFile "'+$log+'" --net-port '+$port+' '+$Arguments) -WindowStyle Hidden -PassThru
    $owned.Add($process)
    return [pscustomobject]@{Name=$Name;Process=$process;Log=$log}
}
function Wait-Marker($Peer,[string]$Marker) {
    $until=[DateTime]::UtcNow.AddSeconds(30)
    while([DateTime]::UtcNow -lt $until){
        if((Test-Path -LiteralPath $Peer.Log) -and (Select-String -LiteralPath $Peer.Log -SimpleMatch $Marker -Quiet)){return}
        if($Peer.Process.HasExited){throw "Early exit: $($Peer.Log)"}
        Start-Sleep -Milliseconds 100
    }
    throw "Missing $Marker in $($Peer.Log)"
}
function Assert-Peer($Peer,[string]$Marker) {
    $until=[DateTime]::UtcNow.AddSeconds(35)
    while(-not $Peer.Process.WaitForExit(500) -and [DateTime]::UtcNow -lt $until){}
    if(-not $Peer.Process.HasExited -or $Peer.Process.ExitCode -ne 0 -or -not(Select-String -LiteralPath $Peer.Log -SimpleMatch $Marker -Quiet)) {throw "Failed: $($Peer.Log)"}
}
try {
    $testArguments=' --security-test --security-stop-file "'+$stopFile+'"'
    $server=Start-Peer 'host' ('--net-mode host --net-auth development --net-account host'+$testArguments)
    Wait-Marker $server 'State=Host'
    $client=Start-Peer 'member' ('--net-mode client --net-auth development --net-account guest'+$testArguments)
    Wait-Marker $client '[SECURITY_TEST] REQUEST_CHECKS'
    $attacks=@(
        @('missing','--net-auth none','AUTH_REQUIRED'),
        @('forged','--net-auth development --net-auth-test-ticket forged','AUTH_INVALID_TOKEN'),
        @('expired','--net-auth development --net-auth-test-ticket expired','AUTH_TOKEN_EXPIRED'),
        @('banned','--net-auth development --net-account banned','AUTH_BANNED'),
        @('duplicate','--net-auth development --net-account guest','AUTH_ALREADY_CONNECTED'),
        @('timeout','--net-auth development --net-account slow','AUTH_TIMEOUT'),
        @('late-result','--net-auth development --net-account late','AUTH_TIMEOUT')
    )
    foreach($attack in $attacks){
        $peer=Start-Peer $attack[0] ('--net-mode client --net-smoke --net-timeout 8 --net-smoke-duration 10 --net-expect-error '+$attack[2]+' '+$attack[1])
        Assert-Peer $peer '[SMOKE] PASS'
        $results.Add([pscustomobject]@{Case=$attack[0];Expected=$attack[2];Result='Passed';Log=$peer.Log})
        Write-Output ($attack[0]+' rejected correctly.')
    }
    # 给“忽略取消的后端”返回结果的时间，随后确认它没有迟到创建玩家。
    Start-Sleep -Milliseconds 1600
    Set-Content -LiteralPath $stopFile -Value 'finished' -Encoding utf8
    Assert-Peer $client '[SECURITY_TEST] PASS'
    Assert-Peer $server '[SECURITY_TEST] PASS'
    $results.Add([pscustomobject]@{Case='request-checks-and-no-late-spawn';Result='Passed';HostLog=$server.Log;ClientLog=$client.Log})
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summary -Encoding utf8
    Write-Output 'Security admission and request integration checks passed.'
}
finally {
    foreach($process in $owned){if(-not $process.HasExited){Stop-Process -Id $process.Id -ErrorAction SilentlyContinue};$process.Dispose()}
}
