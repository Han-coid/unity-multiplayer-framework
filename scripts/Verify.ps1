<#
.SYNOPSIS
依次运行 Unity 测试、Windows 示例构建和本地多进程联机检查。
.PARAMETER UnityPath
用于测试和构建的 Unity 编辑器路径。
.PARAMETER SkipUnitTests
跳过 EditMode 和 PlayMode 测试；仍执行未被其他开关跳过的步骤。
.PARAMETER SkipBuild
复用现有 Windows 示例程序；独立进程联机检查仍会运行。
.EXAMPLE
.\scripts\Verify.ps1 -SkipUnitTests -SkipBuild
#>
param(
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Unity.exe',
    [switch]$SkipUnitTests,
    [switch]$SkipBuild
)
# 任一步骤失败即中止，避免后面的成功输出掩盖前面的失败。
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
$logPath = Join-Path $projectPath 'Logs'
$resultPath = Join-Path $projectPath 'TestResults'
New-Item -ItemType Directory -Force -Path $logPath, $resultPath | Out-Null

# Name 用于日志文件名，Arguments 是该步骤专属的 Unity 命令行参数。
# 启动隐藏的编辑器进程并检查退出码，最多等待十分钟。
function Run-Editor([string]$Name, [string]$Arguments) {
    $logFile = Join-Path $logPath ($Name + '.log')
    $editor = Start-Process -FilePath $UnityPath -ArgumentList ('-batchmode -nographics -projectPath "' + $projectPath + '" -logFile "' + $logFile + '" ' + $Arguments) -WindowStyle Hidden -PassThru
    $editorDeadline = [DateTime]::UtcNow.AddMinutes(10)
    while (-not $editor.WaitForExit(1000) -and [DateTime]::UtcNow -lt $editorDeadline) { }
    if (-not $editor.HasExited) {
        # 只清理当前步骤拥有的进程，不影响用户自行打开的 Unity 编辑器。
        Stop-Process -Id $editor.Id -ErrorAction SilentlyContinue
        throw "Unity step timed out: $Name. See $logFile"
    }
    if ($editor.ExitCode -ne 0) { throw "Unity step failed: $Name. See $logFile" }
    Write-Output "$Name passed."
}

if (-not $SkipUnitTests) {
    # 同一项目不能同时由多个 Unity 编辑器占用，因此两类测试顺序执行。
    foreach ($platform in @('EditMode', 'PlayMode')) {
        $assembly = if ($platform -eq 'EditMode') { 'Han.Multiplayer.EditorTests' } else { 'Han.Multiplayer.PlayModeTests' }
        $xmlPath = Join-Path $resultPath ($platform.ToLowerInvariant() + '.xml')
        Run-Editor $platform.ToLowerInvariant() ('-runTests -testPlatform ' + $platform + ' -assemblyNames ' + $assembly + ' -testResults "' + $xmlPath + '"')
        [xml]$testResult = Get-Content -LiteralPath $xmlPath
        # 退出码正常还不够：测试报告必须成功且实际发现了测试用例。
        if ($testResult.'test-run'.result -ne 'Passed' -or [int]$testResult.'test-run'.total -eq 0) { throw "$platform has failed or missing tests." }
    }
}
if (-not $SkipBuild) {
    # 使用项目构建入口生成供后续多进程检查使用的 Windows 程序。
    Run-Editor 'build' '-quit -executeMethod Han.Multiplayer.Editor.DemoBuilder.BuildWindowsDemo'
}
# 所有联机检查都在本机完成，不连接或部署用户的 Linux 服务器。
& (Join-Path $PSScriptRoot 'SmokeTest.ps1')
