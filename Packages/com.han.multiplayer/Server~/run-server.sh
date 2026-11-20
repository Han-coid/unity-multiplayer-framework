#!/bin/sh
# Linux 服务器启动入口；请保持 UTF-8 无 BOM 和 LF 换行，第一行必须是 shebang。
# 读取环境变量、校验运行条件后启动已构建的程序；本脚本不负责安装或上传。
# 命令失败时退出，访问未设置的变量时报错，避免继续使用不完整的启动配置。
set -eu

# 以脚本位置定位构建目录；清空 CDPATH，避免 cd 额外输出污染目录变量。
server_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
server_binary="$server_dir/MultiplayerServer.x86_64"
# 支持通过 systemd Environment 或当前 Shell 覆盖；未设置或为空时使用默认值。
server_port=${SERVER_PORT:-7777}
server_players=${MAX_PLAYERS:-8}

# 当前产物只适用于 x86_64；其他 CPU 架构需要另外构建对应版本。
case "$(uname -m)" in
    x86_64|amd64) ;;
    *) echo 'This build requires Linux x86_64. Build a matching target for other CPU architectures.' >&2; exit 1 ;;
esac

# 先排除非数字，再比较范围，避免非法字符串直接参与 Shell 数字运算。
case "$server_port" in
    ''|*[!0-9]*) echo 'SERVER_PORT must be an integer from 1 to 65535.' >&2; exit 1 ;;
esac
if [ "$server_port" -lt 1 ] || [ "$server_port" -gt 65535 ]; then
    echo 'SERVER_PORT must be from 1 to 65535.' >&2
    exit 1
fi
# 人数范围与 Unity 会话配置保持一致；纯 Server 进程本身不占玩家名额。
case "$server_players" in
    ''|*[!0-9]*) echo 'MAX_PLAYERS must be an integer from 2 to 128.' >&2; exit 1 ;;
esac
if [ "$server_players" -lt 2 ] || [ "$server_players" -gt 128 ]; then
    echo 'MAX_PLAYERS must be from 2 to 128.' >&2
    exit 1
fi
# Linux 解压或复制后应检查执行权限；只有启动脚本存在并不足以运行服务器。
if [ ! -x "$server_binary" ]; then
    echo "Executable missing or not executable: $server_binary" >&2
    echo 'Upload the complete LinuxServer build and chmod +x MultiplayerServer.x86_64.' >&2
    exit 1
fi

# 切换工作目录使 Unity 可以按预期找到数据文件。
cd -- "$server_dir"
# exec 用游戏进程替换 Shell，让停止信号直接传给 Unity，便于 systemd 管理生命周期。
# -logFile - 将日志写到标准输出；带引号的 "$@" 保留调用者额外参数的原始边界。
exec "$server_binary" -batchmode -nographics --net-mode server \
    --net-port "$server_port" --net-max-players "$server_players" -logFile - "$@"
