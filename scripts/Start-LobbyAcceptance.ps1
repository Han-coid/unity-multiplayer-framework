<#
.SYNOPSIS
打开两个可交互的大厅窗口，让用户手动验收；不会自动准备或开始游戏。
.PARAMETER Port
本机 Host 使用的 UDP 端口。若已被其他程序占用，直接提示，不结束其他进程。
.PARAMETER TimingPreset
两个窗口使用相同的时序配置，默认 Responsive60；可改为 Legacy30 比较操作感受。
#>
param([ValidateRange(1,65535)][int]$Port = 7777, [switch]$WaitForClose,
    [ValidateSet('Legacy30','Aligned30','Responsive60')][string]$TimingPreset = 'Responsive60', [switch]$DevelopmentAccounts, [ValidateSet('On','Off','Legacy')][string]$PredictionMode = 'On')
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
$playerPath = Join-Path $projectPath 'Builds\LobbyWindows\LobbyDemo.exe'
if (-not (Test-Path -LiteralPath $playerPath)) { throw 'Build Windows Lobby Demo first.' }
$socket = [Net.Sockets.UdpClient]::new($Port)
$socket.Dispose()
$runPath = Join-Path $projectPath ('Logs\LobbyAcceptance\' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Force -Path $runPath | Out-Null
$hostLog = Join-Path $runPath 'host.log'
$clientLog = Join-Path $runPath 'guest.log'
if ($TimingPreset -eq 'Legacy30') { $PredictionMode = 'Legacy' }
$serverPrediction = if ($PredictionMode -eq 'Legacy') { 'legacy' } else { 'on' }
$clientPrediction = $PredictionMode.ToLowerInvariant()
$hostProcess = $null
$hostAuthentication = if ($DevelopmentAccounts) { ' --net-auth development --net-account host' } else { '' }
$clientAuthentication = if ($DevelopmentAccounts) { ' --net-auth development --net-account guest' } else { '' }
try {
    # 这是用户明确用于人工操作的游戏窗口，因此使用可见普通窗口。
    $hostProcess = Start-Process -FilePath $playerPath -ArgumentList ('-screen-fullscreen 0 -screen-width 960 -screen-height 720 -logFile "' + $hostLog + '" --net-mode host --net-port ' + $Port + ' --net-nickname 房主 --net-timing ' + $TimingPreset + $hostAuthentication + ' --net-prediction ' + $serverPrediction) -WindowStyle Normal -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Test-Path -LiteralPath $hostLog) -and (Select-String -LiteralPath $hostLog -SimpleMatch 'State=Host' -Quiet)) { break }
        if ($hostProcess.HasExited) { throw "Host exited. See $hostLog" }
        Start-Sleep -Milliseconds 150
    }
    if (-not (Test-Path -LiteralPath $hostLog) -or -not (Select-String -LiteralPath $hostLog -SimpleMatch 'State=Host' -Quiet)) { throw "Host startup timed out. See $hostLog" }
    $clientProcess = Start-Process -FilePath $playerPath -ArgumentList ('-screen-fullscreen 0 -screen-width 960 -screen-height 720 -logFile "' + $clientLog + '" --net-mode client --net-address 127.0.0.1 --net-port ' + $Port + ' --net-nickname 玩家 --net-timing ' + $TimingPreset + $clientAuthentication + ' --net-prediction ' + $clientPrediction) -WindowStyle Normal -PassThru
    Write-Output "中文验收窗口已打开。主机进程=$($hostProcess.Id)，玩家进程=$($clientProcess.Id)，UDP 端口=$Port"
    Write-Output "日志：$runPath"
    Write-Output '用 Alt+Tab 切换窗口。两端点击“准备”，再由房主点击“开始游戏”。'
    Write-Output '请测试“全员返回大厅”“移出”“断开连接”和重新加入。结束后关闭两个游戏窗口。'
    Write-Output '恢复验收：在客户端游戏中移动后，点击左侧“模拟连接中断（测试重连）”，观察原位置和场景恢复；也可取消重连。'
    Write-Output "时序配置：$TimingPreset。点击“显示网络诊断”，角色静止后起步，观察位置响应耗时。"
    Write-Output "客户端移动预测模式：$PredictionMode（On/Off 使用相同的服务器输入协议）。"
    if ($PredictionMode -ne 'Legacy') { Write-Output '跳跃验收：空格或左下角“跳跃”；测试矮平台、悬空顶板、空中短断网及重连。' }
    if ($DevelopmentAccounts) { Write-Output '账号验收已启用开发模拟：房主 host，玩家 guest。可以尝试相同账号重复加入；这不是真实账号认证。' }
    # 供代理启动人工验收时保留进程会话；普通用户从终端运行可以省略此开关。
    if ($WaitForClose) {
        while (-not $hostProcess.HasExited -or -not $clientProcess.HasExited) { Start-Sleep -Milliseconds 500 }
    }
}
catch {
    # 仅在启动准备失败时清理自己刚创建的 Host，不影响其他编辑器或游戏。
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id }
    throw
}
