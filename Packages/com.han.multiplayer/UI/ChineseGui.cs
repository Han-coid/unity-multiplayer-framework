// 中文界面公共层：只负责显示文本和字体，不修改网络协议、错误码或玩家数据。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Han.Multiplayer.UI
{
    /// <summary>IMGUI 中文字体与显示词典；诊断和示例共用，避免互相引用产生循环依赖。</summary>
    public static class ChineseGui
    {
        static GUISkin sourceSkin;
        static GUISkin chineseSkin;
        static Font font;
        static bool fontResolved;
        static Texture2D panelBackground;

        /// <summary>临时使用中文皮肤，结束后恢复调用者皮肤；不影响项目中的其他 GUI。</summary>
        public static Scope Use()
        {
            var previous = GUI.skin;
            if (chineseSkin == null || (sourceSkin != previous && previous != chineseSkin))
            {
                if (chineseSkin != null) UnityEngine.Object.Destroy(chineseSkin);
                sourceSkin = previous;
                chineseSkin = UnityEngine.Object.Instantiate(previous);
                chineseSkin.hideFlags = HideFlags.HideAndDontSave;
                ResolveFont();
                if (font != null) chineseSkin.font = font;
                foreach (var style in new[] { chineseSkin.label, chineseSkin.button, chineseSkin.toggle, chineseSkin.textField,
                    chineseSkin.textArea, chineseSkin.box, chineseSkin.window })
                {
                    if (font != null) style.font = font;
                    style.fontSize = 14;
                }
                chineseSkin.label.wordWrap = true;
                chineseSkin.box.wordWrap = true;
                // 不透明背景防止诊断窗口覆盖大厅时，底层文字透出影响中文阅读。
                if (panelBackground == null)
                {
                    panelBackground = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    panelBackground.hideFlags = HideFlags.HideAndDontSave;
                    panelBackground.SetPixel(0, 0, new Color(0.075f, 0.09f, 0.11f, 1));
                    panelBackground.Apply(false, true);
                }
                foreach (var style in new[] { chineseSkin.window, chineseSkin.box })
                {
                    style.border = new RectOffset();
                    foreach (var state in new[] { style.normal, style.hover, style.active, style.focused,
                        style.onNormal, style.onHover, style.onActive, style.onFocused })
                    { state.background = panelBackground; state.textColor = Color.white; }
                }
            }
            GUI.skin = chineseSkin;
            return new Scope(previous);
        }

        /// <summary>优先使用项目自带字体；桌面系统回退到已安装的中文字体，不复制系统字体文件。</summary>
        static void ResolveFont()
        {
            if (fontResolved && font != null) return;
            fontResolved = true;
            font = Resources.Load<Font>("HanMultiplayer/ChineseFont");
            if (font != null) return;
            try
            {
                var installed = new HashSet<string>(Font.GetOSInstalledFontNames(), StringComparer.OrdinalIgnoreCase);
                foreach (string name in new[] { "Microsoft YaHei", "Microsoft YaHei UI", "微软雅黑", "SimHei", "黑体", "SimSun", "宋体",
                    "PingFang SC", "Noto Sans CJK SC", "Noto Sans SC", "WenQuanYi Micro Hei", "Arial Unicode MS" })
                {
                    if (!installed.Contains(name)) continue;
                    font = Font.CreateDynamicFontFromOSFont(name, 14);
                    Debug.Log("[UI] Chinese font: " + name);
                    return;
                }
            }
            catch (Exception error) { Debug.LogWarning("[UI] System font lookup unavailable: " + error.Message); }
            Debug.LogWarning("[UI] Add a licensed Chinese font at Resources/HanMultiplayer/ChineseFont for this platform.");
        }

        /// <summary>值类型作用域避免每次 OnGUI 分配对象，也确保皮肤在离开作用域时恢复。</summary>
        public readonly struct Scope : IDisposable
        {
            readonly GUISkin previous;
            internal Scope(GUISkin previousSkin) { previous = previousSkin; }
            public void Dispose() { GUI.skin = previous; }
        }

        public static string YesNo(bool value) => value ? "是" : "否";
        public static string Enabled(bool value) => value ? "开启" : "关闭";
        public static string State(SessionState value) => State(value.ToString());
        public static string State(string value)
        {
            switch (value)
            {
                case "Offline": return "未连接";
                case "Starting": return "正在准备";
                case "Connecting": return "正在连接";
                case "Host": return "主机在线";
                case "Client": return "客户端在线";
                case "Server": return "服务器在线";
                case "Stopping": return "正在断开";
                case "Faulted": return "连接异常";
                default: return string.IsNullOrEmpty(value) ? "等待采样" : value;
            }
        }
        public static string Mode(ConnectionMode value) => value == ConnectionMode.Direct ? "IP 直连" : "Relay 中继";
        public static string Phase(string value)
        {
            switch (value)
            {
                case "Waiting": return "等待准备";
                case "Loading": return "正在加载";
                case "Playing": return "游戏进行中";
                case "LoadFailed": return "加载失败";
                default: return value;
            }
        }
        public static string Scene(string value)
        {
            switch (value) { case "Entry": return "入口"; case "Lobby": return "大厅"; case "Game": return "游戏"; default: return value; }
        }
        public static string Protocol(string value)
        {
            switch (value) { case "Direct UDP": return "UDP 直连"; case "Relay DTLS": return "Relay DTLS 中继"; case "Relay WSS": return "Relay WSS 中继"; default: return value; }
        }
        public static string Preset(string value)
        {
            switch (value) { case "None": return "正常网络"; case "Light": return "轻度弱网"; case "Moderate": return "中度弱网"; case "Severe": return "严重弱网"; case "Custom": return "自定义"; default: return value; }
        }

        /// <summary>只翻译显示层，保留原始错误码供日志、协议和自动化测试使用。</summary>
        public static string Message(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string text;
            switch (value)
            {
                case "OK": return "操作成功";
                case "AUTH_REQUIRED": text = "服务器要求账号凭证，请启用并配置账号准入"; break;
                case "AUTH_UNSUPPORTED": text = "服务器未启用账号准入，请统一双方配置"; break;
                case "AUTH_INVALID_TOKEN": text = "登录凭证无效，请重新登录"; break;
                case "AUTH_TOKEN_EXPIRED": text = "登录凭证已过期，请重新登录"; break;
                case "AUTH_TOKEN_TOO_LARGE": text = "登录票据过长，请换取短票据后连接"; break;
                case "AUTH_ACCOUNT_MISMATCH": text = "当前账号与原角色绑定的账号不同，不能恢复该角色"; break;
                case "AUTH_ALREADY_CONNECTED": text = "该账号已在线，不能重复登录此房间"; break;
                case "AUTH_SESSION_HELD": text = "该账号有保留中的角色，请使用原恢复凭证或等待到期"; break;
                case "AUTH_BANNED": text = "该账号已被禁止进入本房间"; break;
                case "AUTH_REVOKED": text = "该账号的连接资格已撤销"; break;
                case "AUTH_TIMEOUT": text = "账号验证超时，请稍后重试"; break;
                case "AUTH_BUSY": text = "服务器正在验证其他连接，请稍后重试"; break;
                case "AUTH_BACKEND_UNAVAILABLE": text = "账号后端暂时不可用，请稍后重试"; break;
                case "AUTH_BACKEND_KEY_MISSING": text = "服务器未配置账号后端访问密钥"; break;
                case "AUTH_BACKEND_CONFIGURATION": text = "账号后端地址或安全配置无效"; break;
                case "AUTH_BACKEND_REJECTED": text = "账号后端拒绝验证，请检查登录与服务器配置"; break;
                case "AUTH_BACKEND_INVALID_RESPONSE": text = "账号后端响应格式无效，已拒绝连接"; break;
                case "AUTH_WRONG_AUDIENCE": text = "登录凭证不适用于当前游戏"; break;
                case "AUTH_CREDENTIAL_PROVIDER_FAILED": text = "无法取得登录凭证，请检查账号插件配置"; break;
                case "AUTH_DEVELOPMENT_DISABLED": text = "正式构建禁止使用开发模拟账号"; break;
                case "REQUEST_FORBIDDEN": text = "账号没有执行此操作的权限"; break;
                case "REQUEST_NOT_OWNER": text = "只能操作自己拥有的对象"; break;
                case "REQUEST_INVALID": text = "请求参数或大小无效"; break;
                case "REQUEST_PHASE": text = "当前游戏阶段不允许此操作"; break;
                case "REQUEST_REPLAY": text = "已忽略重复或过期的请求"; break;
                case "REQUEST_UNKNOWN_RULE": text = "服务器尚未注册此操作的校验规则"; break;
                case "REQUEST_VALIDATOR_REQUIRED": text = "启用账号准入时必须安装请求校验组件"; break;
                case "NOT_LEADER": text = "只有房主可以执行此操作"; break;
                case "NOT_IN_ROOM": text = "玩家尚未进入房间"; break;
                case "NOT_IN_LOBBY": text = "只有等待阶段可以执行此操作"; break;
                case "NOT_PLAYING": text = "当前尚未进入游戏"; break;
                case "PLAYERS_NOT_READY": text = "还有玩家未准备，请等待全员准备"; break;
                case "NOT_ENOUGH_PLAYERS": text = "人数未达到房间要求"; break;
                case "JOIN_IN_PROGRESS": text = "有玩家正在加入，请稍后重试"; break;
                case "PLAYERS_RECONNECTING": text = "有玩家正在重连，请等待其恢复或保留席位到期"; break;
                case "RECOVERY_REQUIRED": text = "服务器要求启用会话恢复模块，请更新双方程序"; break;
                case "RECOVERY_UNSUPPORTED": text = "服务器未启用会话恢复模块，请统一双方配置"; break;
                case "RECOVERY_ALREADY_CONNECTED": text = "原连接尚未关闭或已有连接在恢复，请稍后重试"; break;
                case "RECOVERY_INVALID_TICKET": text = "重连凭证校验失败，请断开后重新加入"; break;
                case "RECOVERY_SESSION_EXPIRED": text = "原会话保留期已过或凭证已撤销，请重新加入"; break;
                case "RECOVERY_ATTEMPT_TIMEOUT": text = "本次重连超时，将在期限内继续尝试"; break;
                case "RECOVERY_WINDOW_EXPIRED": text = "重连期限已用完，请检查网络后重新加入"; break;
                case "RECOVERY_RESTORE_FAILED": text = "服务器无法恢复原角色状态，请重新加入"; break;
                case "RECOVERY_RESTORE_TIMEOUT": text = "连接已建立，但服务器状态恢复超时"; break;
                case "LOBBY_LOADING": text = "房间正在加载，暂时不能加入"; break;
                case "GAME_IN_PROGRESS": text = "游戏进行中，房间不允许中途加入"; break;
                case "KICKED_FROM_LOBBY": text = "你已被房主移出房间"; break;
                case "CANNOT_KICK_SELF_OR_HOST": text = "不能移出自己或主机"; break;
                case "ROOM_FULL": text = "房间人数已满"; break;
                case "INCOMPATIBLE_GAME": text = "游戏标识或协议版本不一致"; break;
                case "INVALID_NICKNAME": text = "昵称无效，请输入 1～24 个字符且不要包含控制字符"; break;
                case "INVALID_RULES": text = "房间规则无效，请检查最少人数"; break;
                case "RATE_LIMITED": text = "操作过于频繁，请稍后重试"; break;
                case "SCENE_NOT_IN_BUILD": text = "场景未加入实际构建列表"; break;
                case "SCENE_REQUEST_FAILED": text = "场景切换请求失败"; break;
                case "SCENE_LOAD_FAILED_OR_TIMED_OUT": text = "场景加载失败或超时，请由房主返回大厅"; break;
                case "LOBBY_NOT_CONFIGURED": text = "大厅尚未正确配置"; break;
                case "SESSION_NOT_READY": text = "会话尚未准备就绪"; break;
                case "ADMISSION_CHECK_FAILED": text = "服务器入房检查失败"; break;
                case "Connection cancelled.": return "连接已取消。";
                case "Connection timed out.": return "连接超时，请检查地址、端口及主机状态。";
                case "Disconnected from the host.": return "与主机的连接已断开。";
                case "The network stopped.": return "网络会话已停止。";
                case "The network stopped during startup.": return "连接建立期间网络已停止。";
                case "Network startup failed. Check the port and Unity log.": return "网络启动失败，请检查端口占用和日志。";
                case "Transport failed. Check the network, port and Relay configuration.": return "网络传输失败，请检查网络、端口和中继配置。";
                case "Port must be 1–65535.": return "端口必须在 1～65535 之间。";
                case "Enter a valid Relay join code.": return "请输入有效的 Relay 房间码。";
                case "Relay join codes use letters and digits only.": return "房间码只能包含英文字母和数字。";
                case "Wait for a connected lobby before sending requests.": return "请等待大厅连接完成后再操作。";
                case "Nickname must contain 1-24 characters.": return "昵称需要包含 1～24 个字符。";
                case "Nickname cannot contain control characters.": return "昵称不能包含换行或其他控制字符。";
                case "Nickname is too long in UTF-8.": return "昵称过长，请缩短后再试。";
                case "Start a connection before running a blackout.": return "请先建立连接，再测试短时断网。";
                case "Reconnect once: this driver was created before the simulator was enabled.": return "请断开后重新连接，以启用弱网模拟层。";
                case "Weak-network simulation needs an enabled tool in an Editor/Development Build with no conflicting UTP constructor.": return "弱网模拟需要开发构建并启用工具；请检查是否与其他模拟工具冲突。";
                case "Input string was not in a correct format.": return "输入格式不正确，请填写有效数字。";
                case "A session is already active. Await StopAsync before starting another.": return "已有连接正在运行，请先断开并等待关闭完成。";
                case "Assign an IRelayProvider component before using Relay.": return "请先给会话指定中继服务组件。";
                case "Enter an IPv4 or IPv6 address (without a port). DNS names are not resolved by this API.": return "请输入 IPv4 或 IPv6 地址；端口单独填写，暂不支持域名解析。";
                case "UGS is already signed in with another profile. Restart the player to change profiles.": return "已使用其他身份缓存登录，请重启游戏后更换身份。";
                case "Lobby requires NGO scene management.": return "大厅需要开启 NGO 场景管理。";
                case "Configure different lobby/game scenes.": return "请分别配置不同的大厅场景和游戏场景。";
                case "Assign a registered prefab with NetworkObject and LobbyRoom.": return "请指定包含网络对象与大厅组件的已注册预制体。";
                default:
                    if (value.StartsWith("SCENE_REQUEST_", StringComparison.Ordinal)) return "场景请求未被接受（" + value + "）";
                    if (value.Contains("SendDelayMs")) return "发送延迟必须在 0～2000 毫秒之间。";
                    if (value.Contains("SendJitterMs")) return "发送抖动必须在 0～2000 毫秒之间。";
                    if (value.Contains("SendLossPercent")) return "发送丢包率必须在 0～100% 之间。";
                    if (value.Contains("ReceiveLossPercent")) return "接收丢包率必须在 0～100% 之间。";
                    if (value.StartsWith("Input string", StringComparison.Ordinal) || value.StartsWith("The input string", StringComparison.Ordinal)) return "输入格式不正确，请填写有效数字。";
                    return value; // 未知 SDK 异常保留原文，避免给出错误的翻译或丢失排错依据。
            }
            return text + "（" + value + "）";
        }

        /// <summary>保留事件时间戳，将诊断事件中的状态和已知错误转换为中文。</summary>
        public static string Event(string value)
        {
            int index = value.IndexOf("UTC ", StringComparison.Ordinal);
            if (index < 0) return Message(value);
            int start = index + 4;
            int separator = value.IndexOf(" | ", start, StringComparison.Ordinal);
            string state = separator < 0 ? value.Substring(start) : value.Substring(start, separator - start);
            return value.Substring(0, start) + State(state) + (separator < 0 ? "" : " | " + Message(value.Substring(separator + 3)));
        }
    }
}
