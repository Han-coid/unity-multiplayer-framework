<#
.SYNOPSIS
在本机构建并打包 Linux x86_64 Unity 独立服务器。
.DESCRIPTION
需要安装对应 Unity 版本的 Linux Dedicated Server Build Support。
构建成功后调用打包脚本；不会连接远程服务器或安装服务。
.PARAMETER UnityPath
Unity 编辑器可执行文件路径，默认使用本项目验证过的编辑器版本。
.EXAMPLE
.\scripts\Build-LinuxServer.ps1
#>
param(
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
# 从脚本位置定位项目，避免调用者的当前目录影响构建路径。
$projectPath = Split-Path -Parent $PSScriptRoot
$logDirectory = Join-Path $projectPath 'Logs'
$logFile = Join-Path $logDirectory 'build-linux-server.log'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
if (-not (Test-Path -LiteralPath $UnityPath)) { throw "Unity Editor not found: $UnityPath" }

# 无界面调用项目内的编辑器构建入口；含空格的路径必须保留引号。
$arguments = '-batchmode -nographics -quit -projectPath "' + $projectPath + '" -executeMethod Han.Multiplayer.Editor.LinuxServerBuilder.Build -logFile "' + $logFile + '"'
$editor = Start-Process -FilePath $UnityPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
$deadline = [DateTime]::UtcNow.AddMinutes(15)
# 每秒检查一次退出状态；超时只结束本脚本启动的编辑器进程。
while (-not $editor.WaitForExit(1000) -and [DateTime]::UtcNow -lt $deadline) { }
if (-not $editor.HasExited) {
    Stop-Process -Id $editor.Id -ErrorAction SilentlyContinue
    throw "Build timed out. See $logFile"
}
if ($editor.ExitCode -ne 0) { throw "Linux build failed. Install Linux Dedicated Server Build Support if missing. See $logFile" }
# 构建失败时会提前抛出异常，只有成功产物才能进入打包步骤。
& (Join-Path $PSScriptRoot 'Package-LinuxServer.ps1')
