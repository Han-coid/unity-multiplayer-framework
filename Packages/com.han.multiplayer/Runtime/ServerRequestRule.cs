// 服务器创建的请求规则；客户端不能提供规则、可信所有者、权限或阶段判定结果。
using System;

namespace Han.Multiplayer
{
    public sealed class ServerRequestRule
    {
        public string Name { get; }
        public double RatePerSecond { get; }
        public int Burst { get; }
        public int MaximumPayloadBytes { get; }
        public bool RequireOwnership { get; }
        public bool RequireSequence { get; }
        public string Permission { get; }
        public bool RequireAccount { get; }
        public ServerRequestRule(string name, double rate, int burst, int maximumBytes, bool ownership = false, bool sequence = false, string permission = null, bool requireAccount = false)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || double.IsNaN(rate) || double.IsInfinity(rate) || rate <= 0 || rate > 10000 ||
                burst < 1 || burst > 10000 || maximumBytes < 0 || maximumBytes > 65536 || (permission != null && (permission.Length == 0 || permission.Length > 64)))
                throw new ArgumentException("Invalid server request rule.");
            Name = name; RatePerSecond = rate; Burst = burst; MaximumPayloadBytes = maximumBytes;
            RequireOwnership = ownership; RequireSequence = sequence; Permission = permission;
            RequireAccount = requireAccount;
        }
    }

    public interface IServerRequestValidator
    {
        string Validate(ServerRequestRule rule, ulong sender, ulong owner, int payloadBytes, bool validPayload, bool phaseAllowed, uint? sequence, ulong sequenceScope);
        void ForgetScope(ulong sender, ulong sequenceScope);
    }

    public static class FrameworkRequestRules
    {
        public static readonly ServerRequestRule Move = new ServerRequestRule("player.move", 120, 120, 16, true, true, "play");
        public static readonly ServerRequestRule PredictedInput = new ServerRequestRule("player.input-window", 120, 120, 81, true, permission: "play");
        public static readonly ServerRequestRule PredictionRebase = new ServerRequestRule("player.input-rebase", 1, 1, 4, true);
        public static readonly ServerRequestRule PlayerReady = new ServerRequestRule("player.ready", 4, 2, 0, true, permission: "play");
        public static readonly ServerRequestRule Nickname = new ServerRequestRule("lobby.nickname", 2, 2, 128, permission: "lobby.use");
        public static readonly ServerRequestRule LobbyReady = new ServerRequestRule("lobby.ready", 4, 2, 1, permission: "lobby.use");
        public static readonly ServerRequestRule LobbyRules = new ServerRequestRule("lobby.rules", 2, 2, 6, permission: "lobby.use");
        public static readonly ServerRequestRule Start = new ServerRequestRule("lobby.start", 4, 2, 0, permission: "lobby.use");
        public static readonly ServerRequestRule Return = new ServerRequestRule("lobby.return", 2, 1, 0, permission: "lobby.use");
        public static readonly ServerRequestRule Kick = new ServerRequestRule("lobby.kick", 2, 1, 8, permission: "lobby.use");
        public static readonly ServerRequestRule RecoveryLeave = new ServerRequestRule("recovery.leave", 1, 1, 0);
        public static ServerRequestRule[] All => new[] { Move, PredictedInput, PredictionRebase, PlayerReady, Nickname, LobbyReady, LobbyRules, Start, Return, Kick, RecoveryLeave };
    }
}
