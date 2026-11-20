<#
.SYNOPSIS
使用相同输入协议、60 Hz 权威模拟和静态场景，对比开启/关闭本地移动预测。
.DESCRIPTION
客户端注入正常/中度/严重弱网。报告为软件起步位置观测，不包含 GPU 或屏幕延迟。
只清理本脚本创建的进程；不访问远端服务器，不改变系统网络设置。
#>
param([ValidateSet('All','Normal','Moderate','Severe')][string]$Profile='All', [ValidateRange(16,120)][int]$DurationSeconds=20)
$ErrorActionPreference='Stop'
$projectPath=Split-Path -Parent $PSScriptRoot
$playerPath=Join-Path $projectPath 'Builds\Windows\MultiplayerDemo.exe'
$logsPath=Join-Path $projectPath 'Logs\PredictionComparison'
$reportsPath=Join-Path $projectPath 'TestResults\PredictionComparison'
New-Item -ItemType Directory -Force -Path $logsPath,$reportsPath | Out-Null
$summary=Join-Path $reportsPath 'summary.json'
if(Test-Path -LiteralPath $summary){Remove-Item -LiteralPath $summary}
$owned=[Collections.Generic.List[System.Diagnostics.Process]]::new()
$results=[Collections.Generic.List[object]]::new()
function Start-Peer([string]$Name,[string]$Arguments){
 $log=Join-Path $logsPath ($Name+'.log');$report=Join-Path $reportsPath ($Name+'.json')
 foreach($path in @($log,$report)){if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path}}
 $process=Start-Process -FilePath $playerPath -ArgumentList ('-batchmode -nographics -logFile "'+$log+'" --net-target-fps 120 --net-timing Responsive60 '+$Arguments) -WindowStyle Hidden -PassThru
 $owned.Add($process);return [pscustomobject]@{Process=$process;Log=$log;Report=$report}
}
function Wait-Marker($Peer,[string]$Marker){
 $until=[DateTime]::UtcNow.AddSeconds(25)
 while([DateTime]::UtcNow -lt $until){
  if((Test-Path -LiteralPath $Peer.Log) -and (Select-String -LiteralPath $Peer.Log -SimpleMatch $Marker -Quiet)){return}
  if($Peer.Process.HasExited){throw "Early exit: $($Peer.Log)"};Start-Sleep -Milliseconds 100
 };throw "Missing $Marker in $($Peer.Log)"
}
try{
 $profiles=if($Profile -eq 'All'){@('Normal','Moderate','Severe')}else{@($Profile)}
 foreach($network in $profiles){foreach($mode in @('off','on')){
  $socket=[Net.Sockets.UdpClient]::new(0);$port=$socket.Client.LocalEndPoint.Port;$socket.Dispose()
  $name="$network-$mode";$sim=if($network -eq 'Normal'){'None'}else{$network}
  $server=Start-Peer ($name+'-host') "--net-mode host --net-port $port --net-prediction on"
  Wait-Marker $server 'State=Host'
  $reportPath=Join-Path $reportsPath ($name+'-client.json')
  $client=Start-Peer ($name+'-client') ('--net-mode client --net-port '+$port+' --net-prediction '+$mode+' --net-sim '+$sim+' --net-latency-test --net-latency-duration '+$DurationSeconds+' --net-report "'+$reportPath+'"')
  $until=[DateTime]::UtcNow.AddSeconds($DurationSeconds+40)
  while(-not $client.Process.WaitForExit(500) -and [DateTime]::UtcNow -lt $until){}
  if(-not $client.Process.HasExited -or $client.Process.ExitCode -ne 0 -or -not(Select-String -LiteralPath $client.Log -SimpleMatch '[TIMING] PASS' -Quiet)){throw "Comparison failed: $($client.Log)"}
  $report=Get-Content -LiteralPath $client.Report -Raw | ConvertFrom-Json
  $data=$report.timingMeasurements.latest
  if($data.framesPerSecond -lt 60 -or $data.observedInputHz -lt 40 -or $data.confirmation.count -lt 20 -or $data.visibleOnset.count -lt 3){throw "Insufficient measured coverage: $($client.Report)"}
  $results.Add([pscustomobject]@{Network=$network;Prediction=$mode;FramesPerSecond=$data.framesPerSecond;InputHz=$data.observedInputHz;ConfirmationMeanMs=$data.confirmation.meanMs;OnsetCount=$data.visibleOnset.count;OnsetMeanMs=$data.visibleOnset.meanMs;OnsetP95Ms=$data.visibleOnset.p95Ms;ClientReport=$client.Report;Result='Passed'})
  Write-Output ("{0}: onset mean {1:N2} ms / P95 {2:N2} ms" -f $name,$data.visibleOnset.meanMs,$data.visibleOnset.p95Ms)
  if(-not $server.Process.HasExited){Stop-Process -Id $server.Process.Id}
 }}
 $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summary -Encoding utf8
}finally{foreach($process in $owned){if(-not $process.HasExited){Stop-Process -Id $process.Id -ErrorAction SilentlyContinue};$process.Dispose()}}
