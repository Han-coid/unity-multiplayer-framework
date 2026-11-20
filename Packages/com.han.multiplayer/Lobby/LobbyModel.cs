// 纯服务器大厅状态机：集中校验身份、准备、人数、锁房和加载屏障，便于独立测试。
using System;
using System.Collections.Generic;
using Unity.Collections;

namespace Han.Multiplayer.Lobby
{
    public sealed class LobbyModel
    {
        public const ulong NoLeader = ulong.MaxValue;
        readonly List<LobbyPlayerState> players = new List<LobbyPlayerState>();
        public IReadOnlyList<LobbyPlayerState> Players => players;
        public LobbyRules Rules { get; private set; }
        public LobbyPhase Phase { get; private set; } = LobbyPhase.Waiting;
        public ulong LeaderId { get; private set; } = NoLeader;
        public bool ReturningToLobby { get; private set; }

        public LobbyModel(LobbyRules rules)
        {
            ValidateRules(rules);
            Rules = rules;
        }

        /// <summary>只接纳已经由核心批准并完成连接的玩家，重复事件保持幂等。</summary>
        public void AddPlayer(ulong id)
        {
            if (IndexOf(id) >= 0) return;
            if (players.Count >= Rules.MaximumPlayers) throw new InvalidOperationException("ROOM_FULL");
            players.Add(new LobbyPlayerState { ClientId = id, Nickname = new FixedString128Bytes("Player-" + id), IsLoaded = Phase == LobbyPhase.Playing, IsConnected = true });
            if (LeaderId == NoLeader || id == 0) LeaderId = id;
        }

        /// <summary>移除连接；独立服务器上房主离开时，授权给仍在线的第一位玩家。</summary>
        public void RemovePlayer(ulong id)
        {
            int index = IndexOf(id);
            if (index < 0) return;
            players.RemoveAt(index);
            if (LeaderId == id) SelectLeader();
        }

        /// <summary>保留昵称/席位，清除准备和加载状态；授权转移给仍在线的人，不在恢复时抢回。</summary>
        public void SuspendPlayer(ulong id)
        {
            int index = IndexOf(id);
            if (index < 0) return;
            var player = players[index]; player.IsConnected = player.IsReady = player.IsLoaded = false; players[index] = player;
            if (LeaderId == id) SelectLeader();
        }
        public void ResumePlayer(ulong previousId, ulong id)
        {
            int index = IndexOf(previousId);
            if (index < 0) { AddPlayer(id); return; }
            if (IndexOf(id) >= 0) throw new InvalidOperationException("Duplicate recovered lobby player.");
            var player = players[index]; player.ClientId = id; player.IsConnected = true;
            player.IsReady = false; player.IsLoaded = Phase == LobbyPhase.Playing; players[index] = player;
            if (LeaderId == NoLeader) LeaderId = id;
        }
        void SelectLeader()
        {
            LeaderId = NoLeader;
            foreach (var player in players) if (player.IsConnected) { LeaderId = player.ClientId; break; }
        }

        /// <summary>只允许修改发送者自己的名称，不能从客户端指定被修改的 ID。</summary>
        public string Rename(ulong sender, string nickname)
        {
            int index = IndexOf(sender);
            if (index < 0) return "NOT_IN_ROOM";
            try
            {
                var player = players[index];
                player.Nickname = new FixedString128Bytes(LobbySettings.NormalizeNickname(nickname));
                players[index] = player;
                return null;
            }
            catch (ArgumentException) { return "INVALID_NICKNAME"; }
        }

        /// <summary>只有等待阶段能准备；准备不代表已完成游戏场景加载。</summary>
        public string SetReady(ulong sender, bool ready)
        {
            int index = IndexOf(sender);
            if (index < 0) return "NOT_IN_ROOM";
            if (Phase != LobbyPhase.Waiting) return "NOT_IN_LOBBY";
            var player = players[index]; player.IsReady = ready; players[index] = player;
            return null;
        }

        /// <summary>房主可更新等待阶段规则；规则变更撤销所有人的准备，防止沿用旧确认。</summary>
        public string UpdateRules(ulong sender, int minimum, bool requireReady, bool allowLateJoin)
        {
            if (sender != LeaderId || IndexOf(sender) < 0) return "NOT_LEADER";
            if (Phase != LobbyPhase.Waiting) return "NOT_IN_LOBBY";
            var rules = Rules;
            rules.MinimumPlayers = minimum; rules.RequireAllReady = requireReady; rules.AllowLateJoin = allowLateJoin;
            try { ValidateRules(rules); } catch (ArgumentException) { return "INVALID_RULES"; }
            Rules = rules;
            ResetPlayers();
            return null;
        }

        /// <summary>开始条件由服务器统一判定；已批准尚未完成的连接也必须先处理完。</summary>
        public string CheckStart(ulong sender, int pendingApprovals, bool serverOverride = false)
        {
            if (!serverOverride && (sender != LeaderId || IndexOf(sender) < 0)) return "NOT_LEADER";
            if (Phase != LobbyPhase.Waiting) return "NOT_IN_LOBBY";
            if (pendingApprovals > 0) return "JOIN_IN_PROGRESS";
            foreach (var player in players) if (!player.IsConnected) return "PLAYERS_RECONNECTING";
            if (players.Count < Rules.MinimumPlayers) return "NOT_ENOUGH_PLAYERS";
            foreach (var player in players) if (Rules.RequireAllReady && !player.IsReady) return "PLAYERS_NOT_READY";
            return null;
        }

        /// <summary>加载期间一律锁房；进行中是否允许新玩家由规则决定。</summary>
        public string CheckAdmission()
        {
            if (Phase == LobbyPhase.Loading || Phase == LobbyPhase.LoadFailed) return "LOBBY_LOADING";
            return Phase == LobbyPhase.Playing && !Rules.AllowLateJoin ? "GAME_IN_PROGRESS" : null;
        }

        public string CheckKick(ulong sender, ulong target, bool serverOverride = false)
        {
            if (!serverOverride && (sender != LeaderId || IndexOf(sender) < 0)) return "NOT_LEADER";
            if (target == sender || target == 0) return "CANNOT_KICK_SELF_OR_HOST";
            return IndexOf(target) < 0 ? "NOT_IN_ROOM" : null;
        }

        /// <summary>加载开始先清除加载确认；不会提前进入 Playing。</summary>
        public void BeginLoading(bool returning)
        {
            ReturningToLobby = returning;
            Phase = LobbyPhase.Loading;
            for (int i = 0; i < players.Count; i++) { var p = players[i]; p.IsLoaded = false; players[i] = p; }
        }

        /// <summary>请求未被 NGO 接受时恢复原阶段，避免永久锁房。</summary>
        public void RestorePhase(LobbyPhase phase)
        {
            Phase = phase;
            if (phase == LobbyPhase.Playing)
                for (int i = 0; i < players.Count; i++) { var p = players[i]; p.IsLoaded = true; players[i] = p; }
        }

        /// <summary>只接受服务器观察到的场景完成事件，不信任客户端自报加载进度。</summary>
        public void MarkLoaded(ulong id)
        {
            int index = IndexOf(id);
            if (index < 0) return;
            var p = players[index]; p.IsLoaded = true; players[index] = p;
        }

        /// <summary>加载事件整体结束后检查仍在线的所有玩家；失败时禁止进入玩法。</summary>
        public bool CompleteLoading(bool timedOut)
        {
            if (Phase != LobbyPhase.Loading) return false;
            foreach (var player in players) if (player.IsConnected && !player.IsLoaded) timedOut = true;
            if (timedOut) { Phase = LobbyPhase.LoadFailed; return false; }
            Phase = ReturningToLobby ? LobbyPhase.Waiting : LobbyPhase.Playing;
            if (ReturningToLobby) ResetPlayers();
            return true;
        }

        void ResetPlayers()
        {
            for (int i = 0; i < players.Count; i++) { var p = players[i]; p.IsReady = p.IsLoaded = false; players[i] = p; }
        }

        int IndexOf(ulong id) { for (int i = 0; i < players.Count; i++) if (players[i].ClientId == id) return i; return -1; }

        static void ValidateRules(LobbyRules rules)
        {
            if (rules.MinimumPlayers < 1 || rules.MinimumPlayers > rules.MaximumPlayers || rules.MaximumPlayers < 2 || rules.MaximumPlayers > 128)
                throw new ArgumentOutOfRangeException(nameof(rules));
        }
    }
}
