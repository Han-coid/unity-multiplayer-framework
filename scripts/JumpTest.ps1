<#
.SYNOPSIS
通过真实双进程验证跳跃、平台、顶板、断网重力及空中重连；覆盖预测开启和关闭。
.DESCRIPTION
测试控制通道只由开发测试开关注册；不提供给正常游戏，服务器只执行固定测试动作。
#>
param([ValidateSet('All','Normal','Moderate','Off')][string]$Case='All')
$ErrorActionPreference='Stop'
$projectPath=Split-Path -Parent $PSScriptRoot
$playerPath=Join-Path $projectPath 'Builds\Windows\MultiplayerDemo.exe'
$logsPath=Join-Path $projectPath 'Logs\Jump'
$reportsPath=Join-Path $projectPath 'TestResults\Jump'
New-Item -ItemType Directory -Force -Path $logsPath,$reportsPath | Out-Null
$summary=Join-Path $reportsPath 'summary.json'
if(Test-Path -LiteralPath $summary){Remove-Item -LiteralPath $summary}
$owned=[Collections.Generic.List[System.Diagnostics.Process]]::new();$results=[Collections.Generic.List[object]]::new()
function Start-Peer([string]$Name,[string]$Arguments){
 $log=Join-Path $logsPath ($Name+'.log');if(Test-Path -LiteralPath $log){Remove-Item -LiteralPath $log}
 $process=Start-Process -FilePath $playerPath -ArgumentList ('-batchmode -nographics -logFile "'+$log+'" --net-target-fps 120 --jump-test '+$Arguments) -WindowStyle Hidden -PassThru
 $owned.Add($process);return [pscustomobject]@{Process=$process;Log=$log}
}
function Wait-Marker($Peer,[string]$Marker){
 $until=[DateTime]::UtcNow.AddSeconds(25)
 while([DateTime]::UtcNow -lt $until){if((Test-Path -LiteralPath $Peer.Log) -and (Select-String -LiteralPath $Peer.Log -SimpleMatch $Marker -Quiet)){return};if($Peer.Process.HasExited){throw "Early exit: $($Peer.Log)"};Start-Sleep -Milliseconds 100};throw "Missing $Marker"
}
function Assert-Peer($Peer){
 $until=[DateTime]::UtcNow.AddSeconds(75)
 while(-not $Peer.Process.WaitForExit(500) -and [DateTime]::UtcNow -lt $until){}
 if(-not $Peer.Process.HasExited -or $Peer.Process.ExitCode -ne 0 -or -not(Select-String -LiteralPath $Peer.Log -SimpleMatch '[JUMP_TEST] PASS' -Quiet)){throw "Jump check failed: $($Peer.Log)"}
}
try{
 $cases=if($Case -eq 'All'){@('Normal','Moderate','Off')}else{@($Case)}
 foreach($name in $cases){
  $socket=[Net.Sockets.UdpClient]::new(0);$port=$socket.Client.LocalEndPoint.Port;$socket.Dispose()
  $prediction=if($name -eq 'Off'){'off'}else{'on'};$sim=if($name -eq 'Moderate'){'Moderate'}else{'None'}
  $server=Start-Peer ($name+'-host') "--net-mode host --net-port $port --net-prediction on"
  Wait-Marker $server 'State=Host'
  $report=Join-Path $reportsPath ($name+'-client.json');if(Test-Path -LiteralPath $report){Remove-Item -LiteralPath $report}
  $client=Start-Peer ($name+'-client') ('--net-mode client --net-port '+$port+' --net-sim '+$sim+' --net-prediction '+$prediction+' --jump-report "'+$report+'"')
  Assert-Peer $client;Assert-Peer $server
  $data=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
  if(-not $data.passed -or $data.ceilingPeak -lt 1.4 -or -not $data.finalState.Grounded){throw 'Insufficient jump coverage.'}
  $results.Add([pscustomobject]@{Case=$name;Result='Passed';ClientLog=$client.Log;HostLog=$server.Log});Write-Output ($name+' jump checks passed.')
 }
 $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summary -Encoding utf8
}finally{foreach($process in $owned){if(-not $process.HasExited){Stop-Process -Id $process.Id -ErrorAction SilentlyContinue};$process.Dispose()}}
