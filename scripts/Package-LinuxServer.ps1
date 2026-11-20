<#
.SYNOPSIS
把已构建的 Linux 服务器整理为本地压缩包并生成 SHA256 校验文件。
.DESCRIPTION
检查主程序架构，复制启动模板，再打包运行目录；不执行 Unity 构建，
也不上传文件、安装远程服务或启动游戏服务器。
.EXAMPLE
.\scripts\Package-LinuxServer.ps1
#>
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
$buildPath = Join-Path $projectPath 'Builds\LinuxServer'
$binaryPath = Join-Path $buildPath 'MultiplayerServer.x86_64'
$templatesPath = Join-Path $projectPath 'Packages\com.han.multiplayer\Server~'

# Unity 主程序、原生运行库和数据目录缺一不可，不能只分发一个可执行文件。
foreach ($required in @('MultiplayerServer.x86_64', 'UnityPlayer.so', 'MultiplayerServer_Data')) {
    if (-not (Test-Path -LiteralPath (Join-Path $buildPath $required))) {
        throw "Incomplete Linux build: missing $required. Run Build-LinuxServer.ps1 first."
    }
}
$stream = [IO.File]::OpenRead($binaryPath)
try {
    # ELF 魔数位于 0–3；4 表示位数、5 表示字节序，18–19 是机器架构编号。
    # 这里要求 ELF64、小端、EM_X86_64（62），防止把 Windows 或其他架构产物误打包。
    $header = New-Object byte[] 20
    if ($stream.Read($header, 0, 20) -ne 20 -or
        $header[0] -ne 0x7f -or $header[1] -ne 0x45 -or $header[2] -ne 0x4c -or $header[3] -ne 0x46 -or
        $header[4] -ne 2 -or $header[5] -ne 1 -or $header[18] -ne 62 -or $header[19] -ne 0) {
        throw 'The server executable must be an ELF64 little-endian x86_64 binary.'
    }
}
# 校验失败也要释放文件句柄，避免阻止下一次构建或打包。
finally { $stream.Dispose() }

foreach ($file in @('run-server.sh', 'han-multiplayer.service', 'README.md')) {
    # 启动模板统一为无 BOM 的 UTF-8 和 LF 换行，方便 Linux 正确读取。
    $contents = [IO.File]::ReadAllText((Join-Path $templatesPath $file)).Replace("`r`n", "`n")
    [IO.File]::WriteAllText((Join-Path $buildPath $file), $contents, [Text.UTF8Encoding]::new($false))
}

$archive = Join-Path $projectPath 'Builds\HanMultiplayer-Linux-x64.tar.gz'
# 排除 Unity 明确标记为不可分发的调试/备份目录，归档路径相对于构建目录。
& tar -czf $archive --exclude '*_BurstDebugInformation_DoNotShip' --exclude '*_BackUpThisFolder_ButDontShipItWithYourGame' -C $buildPath .
if ($LASTEXITCODE -ne 0) { throw 'Could not package the Linux server.' }
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
# 双空格分隔哈希与文件名，生成可供 Linux sha256sum -c 读取的校验文件。
[IO.File]::WriteAllText(($archive + '.sha256'), "$hash  HanMultiplayer-Linux-x64.tar.gz`n", [Text.UTF8Encoding]::new($false))
Write-Output "Linux server package: $archive"
Write-Output "SHA256: $hash"
Write-Output 'Local package only. No upload, installation or remote service startup was performed.'
