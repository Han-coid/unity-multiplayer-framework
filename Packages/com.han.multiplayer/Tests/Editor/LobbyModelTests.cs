// 大厅规则与权限测试：直接模拟服务端状态机，检查客户端输入不能越权或跳过加载屏障。
using System;
using Han.Multiplayer.Lobby;
using NUnit.Framework;

namespace Han.Multiplayer.Tests
{
    public class LobbyModelTests
    {
        LobbyModel room;

        [SetUp]
        public void SetUp()
        {
            room = new LobbyModel(new LobbyRules { MinimumPlayers = 2, MaximumPlayers = 4, RequireAllReady = true });
            room.AddPlayer(0); room.AddPlayer(1);
        }

        [Test]
        public void OnlyLeaderCanStartChangeRulesOrKick()
        {
            Assert.AreEqual("NOT_LEADER", room.CheckStart(1, 0));
            Assert.AreEqual("NOT_LEADER", room.UpdateRules(1, 1, false, true));
            Assert.AreEqual("NOT_LEADER", room.CheckKick(1, 0));
            Assert.AreEqual("CANNOT_KICK_SELF_OR_HOST", room.CheckKick(0, 0));
            Assert.IsNull(room.CheckKick(0, 1));
        }

        [Test]
        public void StartRequiresCountReadyAndNoPendingApproval()
        {
            Assert.AreEqual("PLAYERS_NOT_READY", room.CheckStart(0, 0));
            room.SetReady(0, true); room.SetReady(1, true);
            Assert.AreEqual("JOIN_IN_PROGRESS", room.CheckStart(0, 1));
            Assert.IsNull(room.CheckStart(0, 0));
            room.RemovePlayer(1);
            Assert.AreEqual("NOT_ENOUGH_PLAYERS", room.CheckStart(0, 0));
        }

        [Test]
        public void RulesChangeClearsReadyAndRejectsInvalidMinimum()
        {
            room.SetReady(0, true); room.SetReady(1, true);
            Assert.AreEqual("INVALID_RULES", room.UpdateRules(0, 5, false, false));
            Assert.IsTrue(room.Players[0].IsReady);
            Assert.IsNull(room.UpdateRules(0, 1, false, true));
            Assert.IsFalse(room.Players[0].IsReady);
            Assert.IsFalse(room.Players[1].IsReady);
        }

        [Test]
        public void LoadingAlwaysLocksAndNeedsEveryPlayer()
        {
            room.BeginLoading(false);
            Assert.AreEqual("LOBBY_LOADING", room.CheckAdmission());
            Assert.AreEqual("NOT_IN_LOBBY", room.SetReady(1, true));
            room.MarkLoaded(0);
            Assert.IsFalse(room.CompleteLoading(false));
            Assert.AreEqual(LobbyPhase.LoadFailed, room.Phase);
            Assert.AreEqual("LOBBY_LOADING", room.CheckAdmission());
        }

        [Test]
        public void CompleteGameAndReturnResetPlayerFlags()
        {
            room.SetReady(0, true); room.SetReady(1, true);
            room.BeginLoading(false); room.MarkLoaded(0); room.MarkLoaded(1);
            Assert.IsTrue(room.CompleteLoading(false));
            Assert.AreEqual(LobbyPhase.Playing, room.Phase);
            Assert.AreEqual("GAME_IN_PROGRESS", room.CheckAdmission());
            room.BeginLoading(true); room.MarkLoaded(0); room.MarkLoaded(1);
            Assert.IsTrue(room.CompleteLoading(false));
            Assert.AreEqual(LobbyPhase.Waiting, room.Phase);
            Assert.IsFalse(room.Players[0].IsReady);
            Assert.IsFalse(room.Players[1].IsLoaded);
        }

        [Test]
        public void LateJoinCanBeEnabledButCannotBypassLoading()
        {
            room.UpdateRules(0, 1, false, true);
            room.BeginLoading(false);
            Assert.AreEqual("LOBBY_LOADING", room.CheckAdmission());
            room.MarkLoaded(0); room.MarkLoaded(1); room.CompleteLoading(false);
            Assert.IsNull(room.CheckAdmission());
            room.AddPlayer(2);
            Assert.IsTrue(room.Players[2].IsLoaded);
        }

        [Test]
        public void DedicatedServerTransfersLeaderWithoutMigratingServer()
        {
            var dedicated = new LobbyModel(room.Rules);
            dedicated.AddPlayer(11); dedicated.AddPlayer(22);
            Assert.AreEqual(11, dedicated.LeaderId);
            dedicated.RemovePlayer(11);
            Assert.AreEqual(22, dedicated.LeaderId);
            dedicated.RemovePlayer(22);
            Assert.AreEqual(LobbyModel.NoLeader, dedicated.LeaderId);
        }

        [TestCase(""), TestCase("\n"), TestCase("abc\tdef"), TestCase("1234567890123456789012345")]
        public void RejectsInvalidNicknames(string nickname)
        {
            Assert.AreEqual("INVALID_NICKNAME", room.Rename(1, nickname));
            Assert.AreEqual("Player-1", room.Players[1].Nickname.ToString());
        }

        [Test]
        public void NamesAreNormalizedAndCannotAddressAnotherPlayer()
        {
            Assert.IsNull(room.Rename(1, " 玩家二 "));
            Assert.AreEqual("玩家二", room.Players[1].Nickname.ToString());
            Assert.AreEqual("Player-0", room.Players[0].Nickname.ToString());
            Assert.AreEqual("NOT_IN_ROOM", room.Rename(99, "Unknown"));
        }

        [Test]
        public void FailedLoadCannotStartPlayingEvenIfEveryoneLoaded()
        {
            room.BeginLoading(false); room.MarkLoaded(0); room.MarkLoaded(1);
            Assert.IsFalse(room.CompleteLoading(true));
            Assert.AreEqual(LobbyPhase.LoadFailed, room.Phase);
        }

        [Test]
        public void CapacityAndDuplicateConnectionEventsRemainBounded()
        {
            room.AddPlayer(1); room.AddPlayer(2); room.AddPlayer(3);
            Assert.AreEqual(4, room.Players.Count);
            Assert.Throws<InvalidOperationException>(() => room.AddPlayer(4));
            room.RemovePlayer(99);
            Assert.AreEqual(4, room.Players.Count);
        }
    }
}
