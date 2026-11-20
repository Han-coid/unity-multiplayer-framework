// 不启动网络，直接验证真实服务器租约表的凭证、并发占位与保留期限边界。
using System;
using System.Text;
using Han.Multiplayer.Recovery;
using Han.Multiplayer.Lobby;
using NUnit.Framework;

namespace Han.Multiplayer.Tests
{
    public class RecoveryRegistryTests
    {
        static readonly byte[] NewClient = Encoding.ASCII.GetBytes("R1|N");
        [Test]
        public void ResumeKeepsIdentityAndChangesConnectionWithoutDoubleSeat()
        {
            var table = new RecoveryRegistry(2, 30);
            Assert.IsNull(table.Prepare(1, NewClient, 0));
            var first = table.Connect(1); var identity = first.SessionId;
            var ticket = table.CreateResumeData(1);
            table.Disconnect(1, 1);
            Assert.AreEqual(1, table.HeldCount(2));
            Assert.IsNull(table.Prepare(2, ticket, 2));
            Assert.AreEqual(0, table.HeldCount(2));
            Assert.AreEqual("RECOVERY_ALREADY_CONNECTED", table.Prepare(3, ticket, 2));
            var resumed = table.Connect(2);
            Assert.AreEqual(identity, resumed.SessionId);
            Assert.AreEqual(1, resumed.PreviousClientId);
            Assert.AreEqual("RECOVERY_ALREADY_CONNECTED", table.Prepare(4, ticket, 3));
        }
        [Test]
        public void InvalidTicketCannotClaimHeldOrOnlinePlayer()
        {
            var table = new RecoveryRegistry(2, 30);
            table.Prepare(1, NewClient, 0); table.Connect(1);
            var ticket = table.CreateResumeData(1);
            ticket[ticket.Length - 1] = ticket[ticket.Length - 1] == '0' ? (byte)'1' : (byte)'0';
            Assert.AreEqual("RECOVERY_INVALID_TICKET", table.Prepare(2, ticket, 1));
            table.Disconnect(1, 1);
            Assert.AreEqual("RECOVERY_INVALID_TICKET", table.Prepare(2, ticket, 2));
            Assert.AreEqual(1, table.HeldCount(2));
            Assert.AreEqual("RECOVERY_INVALID_TICKET", table.Prepare(2, new byte[] { 255 }, 2));
        }
        [Test]
        public void RejectedAndTimedOutClaimsDoNotExtendOriginalRetention()
        {
            var table = new RecoveryRegistry(2, 3);
            table.Prepare(1, NewClient, 0); table.Connect(1);
            var ticket = table.CreateResumeData(1);
            table.Disconnect(1, 1);
            Assert.IsNull(table.Prepare(2, ticket, 2));
            table.Rollback(2);
            Assert.AreEqual(1, table.HeldCount(2));
            Assert.IsNull(table.Prepare(3, ticket, 3));
            Assert.AreEqual(0, table.HeldCount(4));
            Assert.AreEqual("RECOVERY_SESSION_EXPIRED", table.Prepare(4, ticket, 5));
        }
        [Test]
        public void CompletingSceneSynchronizationAfterClaimDeadlineCannotReviveLease()
        {
            var table = new RecoveryRegistry(2, 3);
            table.Prepare(1, NewClient, 0); table.Connect(1, 0);
            var ticket = table.CreateResumeData(1); table.Disconnect(1, 1);
            Assert.IsNull(table.Prepare(2, ticket, 2));
            Assert.IsNull(table.Connect(2, 4));
            Assert.AreEqual(0, table.HeldCount(4));
        }
        [Test]
        public void HeldSeatPreventsNewJoinAndExpiryReleasesCapacity()
        {
            var table = new RecoveryRegistry(1, 3);
            table.Prepare(1, NewClient, 0); table.Connect(1); table.Disconnect(1, 1);
            Assert.AreEqual("ROOM_FULL", table.Prepare(2, NewClient, 2));
            Assert.IsNull(table.Prepare(2, NewClient, 4));
            table.Rollback(2);
            Assert.IsNull(table.Prepare(3, NewClient, 5));
        }
        [Test]
        public void RevocationInvalidatesOldTicketAndPendingNewDoesNotLeak()
        {
            var table = new RecoveryRegistry(1, 30, 2);
            table.Prepare(1, NewClient, 0); table.Connect(1);
            var ticket = table.CreateResumeData(1); table.Revoke(1);
            Assert.AreEqual("RECOVERY_SESSION_EXPIRED", table.Prepare(2, ticket, 1));
            Assert.IsNull(table.Prepare(2, NewClient, 1));
            Assert.IsNull(table.Prepare(3, NewClient, 3));
        }
        [Test]
        public void EnvelopeRejectsOversizeTruncationTrailingBytesAndKeepsBaseHandshake()
        {
            var baseData = new SessionSettings().CreateHandshake();
            var packed = ConnectionDataEnvelope.Encode(baseData, NewClient);
            Assert.IsTrue(ConnectionDataEnvelope.TryDecode(packed, out var handshake, out var extension));
            CollectionAssert.AreEqual(baseData, handshake); CollectionAssert.AreEqual(NewClient, extension);
            var truncated = new byte[packed.Length - 1]; Array.Copy(packed, truncated, truncated.Length);
            Assert.IsFalse(ConnectionDataEnvelope.TryDecode(truncated, out _, out _));
            Assert.IsFalse(ConnectionDataEnvelope.TryDecode(new byte[513], out _, out _));
            Assert.Throws<ArgumentException>(() => ConnectionDataEnvelope.Encode(baseData, new byte[512]));
            Assert.Throws<ArgumentException>(() => ConnectionDataEnvelope.Encode(new byte[513], null));
            Assert.AreEqual("INCOMPATIBLE_GAME", new AdmissionPolicy(new SessionSettings { ApplicationId = "another" }).ValidateHandshake(handshake));
        }
        [Test]
        public void CoreCapacityIncludesHeldSeatsAndDoesNotDoubleCountResumingSeat()
        {
            var settings = new SessionSettings { MaxPlayers = 2 };
            var policy = new AdmissionPolicy(settings);
            Assert.AreEqual("ROOM_FULL", policy.TryReserve(2, settings.CreateHandshake(), new ulong[] { 0 }, 0, 1));
            Assert.IsNull(policy.TryReserve(2, settings.CreateHandshake(), new ulong[] { 0 }, 0, 0));
            Assert.AreEqual("ROOM_FULL", policy.TryReserve(3, settings.CreateHandshake(), new ulong[] { 0 }, 0, 0));
        }
        [Test]
        public void LobbyRebindsOfflineSeatPreservesNameAndResetsReady()
        {
            var room = new LobbyModel(new LobbyRules { MinimumPlayers = 2, MaximumPlayers = 2, RequireAllReady = true });
            room.AddPlayer(0); room.AddPlayer(1); room.Rename(1, "旧昵称"); room.SetReady(0, true); room.SetReady(1, true);
            room.SuspendPlayer(1);
            Assert.AreEqual("PLAYERS_RECONNECTING", room.CheckStart(0, 0));
            room.ResumePlayer(1, 9);
            Assert.AreEqual(2, room.Players.Count); Assert.AreEqual("旧昵称", room.Players[1].Nickname.ToString());
            Assert.AreEqual(9, room.Players[1].ClientId); Assert.IsTrue(room.Players[1].IsConnected); Assert.IsFalse(room.Players[1].IsReady);
            Assert.AreEqual("PLAYERS_NOT_READY", room.CheckStart(0, 0));
        }
        [Test]
        public void SuspendedLeaderTransfersToOnlineMemberWithoutAutomaticTakeover()
        {
            var room = new LobbyModel(new LobbyRules { MinimumPlayers = 1, MaximumPlayers = 3 });
            room.AddPlayer(1); room.AddPlayer(2); room.SuspendPlayer(1);
            Assert.AreEqual(2, room.LeaderId);
            room.ResumePlayer(1, 3); Assert.AreEqual(2, room.LeaderId);
        }
        [Test]
        public void TerminalErrorsNeverRetryButLoadingAndDuplicateConnectionCanRetry()
        {
            Assert.IsTrue(RecoveryService.IsTerminal("KICKED_FROM_LOBBY"));
            Assert.IsTrue(RecoveryService.IsTerminal("BANNED"));
            Assert.IsTrue(RecoveryService.IsTerminal("RECOVERY_INVALID_TICKET"));
            Assert.IsTrue(RecoveryService.IsTerminal("RECOVERY_SESSION_EXPIRED"));
            Assert.IsFalse(RecoveryService.IsTerminal("LOBBY_LOADING"));
            Assert.IsFalse(RecoveryService.IsTerminal("RECOVERY_ALREADY_CONNECTED"));
        }
    }
}
