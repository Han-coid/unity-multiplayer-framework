// 直接验证授权与状态机边界，覆盖乱序/重放、无效请求消耗预算和账号恢复绑定。
using System;
using System.Text;
using Han.Multiplayer.Recovery;
using Han.Multiplayer.Security;
using NUnit.Framework;

namespace Han.Multiplayer.Tests
{
    public class SecurityPolicyTests
    {
        static AccountPrincipal Player(long expiry = 1000) => new AccountPrincipal("alice", "game", expiry, new[] { "play" });
        [Test]
        public void PrincipalCopiesPermissionsAndRejectsMalformedClaims()
        {
            var permissions = new[] { "play" };
            var principal = new AccountPrincipal("alice", "game", 1000, permissions);
            permissions[0] = "admin";
            Assert.IsTrue(principal.HasPermission("play")); Assert.IsFalse(principal.HasPermission("admin"));
            Assert.Throws<ArgumentException>(() => new AccountPrincipal("../alice", "game", 1000, new string[0]));
            Assert.Throws<ArgumentException>(() => new AccountPrincipal("alice", "game", 1000, new[] { "*" }));
            Assert.IsTrue(principal.IsExpired(1000));
        }
        [Test]
        public void AuthenticationEnvelopeCannotBeIgnoredByLegacyReader()
        {
            byte[] handshake = new SessionSettings().CreateHandshake(), resume = Encoding.ASCII.GetBytes("R1|N"), account = Encoding.ASCII.GetBytes("opaque-ticket");
            var packed = ConnectionDataEnvelope.Encode(handshake, resume, account);
            Assert.IsTrue(ConnectionDataEnvelope.TryDecode(packed, out var first, out var second, out var third));
            CollectionAssert.AreEqual(handshake, first); CollectionAssert.AreEqual(resume, second); CollectionAssert.AreEqual(account, third);
            Assert.IsFalse(ConnectionDataEnvelope.TryDecode(packed, out _, out _));
            packed[8]++;
            Assert.IsFalse(ConnectionDataEnvelope.TryDecode(packed, out _, out _, out _));
            Assert.Throws<ArgumentException>(() => ConnectionDataEnvelope.Encode(handshake, resume, new byte[512]));
        }
        [Test]
        public void GuardChecksIdentityPermissionOwnershipPhaseAndPayload()
        {
            var rule = new ServerRequestRule("action", 100, 100, 8, true, permission: "play", requireAccount: true);
            var ledger = new RequestLedger(); ledger.Register(rule);
            string Check(ulong owner, bool valid, bool phase, AccountPrincipal principal, int bytes = 8, bool connected = true)
                => ledger.Check(rule, 1, owner, bytes, valid, phase, null, 0, 1, connected, true, principal, 100);
            Assert.AreEqual("REQUEST_NOT_CONNECTED", Check(1, true, true, Player(), connected: false));
            Assert.AreEqual("AUTH_REQUIRED", Check(1, true, true, null));
            Assert.AreEqual("AUTH_TOKEN_EXPIRED", Check(1, true, true, Player(100)));
            Assert.AreEqual("REQUEST_FORBIDDEN", Check(1, true, true, new AccountPrincipal("a", "game", 1000, new string[0])));
            Assert.AreEqual("REQUEST_NOT_OWNER", Check(2, true, true, Player()));
            Assert.AreEqual("REQUEST_INVALID", Check(1, false, true, Player()));
            Assert.AreEqual("REQUEST_INVALID", Check(1, true, true, Player(), 9));
            Assert.AreEqual("REQUEST_PHASE", Check(1, true, false, Player()));
            Assert.IsNull(Check(1, true, true, Player()));
        }
        [Test]
        public void InvalidRequestsConsumeQuotaAndRefillUsesRealElapsedTime()
        {
            var rule = new ServerRequestRule("burst", 2, 2, 1);
            var ledger = new RequestLedger(); ledger.Register(rule);
            string Check(double now, bool valid) => ledger.Check(rule, 1, 1, 1, valid, true, null, 0, now, true, false, null, 0);
            Assert.AreEqual("REQUEST_INVALID", Check(0, false));
            Assert.AreEqual("REQUEST_INVALID", Check(0, false));
            Assert.AreEqual("RATE_LIMITED", Check(.49, true));
            Assert.IsNull(Check(.5, true));
            Assert.AreEqual("RATE_LIMITED", Check(.5, true));
            Assert.AreEqual(1, ledger.Allowed); Assert.AreEqual(4, ledger.Rejected);
        }
        [Test]
        public void AggregateBudgetCannotBeBypassedBySwitchingOperations()
        {
            var first = new ServerRequestRule("one", 100, 100, 0);
            var second = new ServerRequestRule("two", 100, 100, 0);
            var ledger = new RequestLedger(3, 3); ledger.Register(first); ledger.Register(second);
            for (int i = 0; i < 3; i++) Assert.IsNull(ledger.Check(first, 1, 1, 0, true, true, null, 0, 0, true, false, null, 0));
            Assert.AreEqual("RATE_LIMITED", ledger.Check(second, 1, 1, 0, true, true, null, 0, 0, true, false, null, 0));
        }
        [Test]
        public void SequenceIsPerServerObjectAndWrapSafe()
        {
            var rule = new ServerRequestRule("sequence", 100, 100, 0, sequence: true);
            var ledger = new RequestLedger(); ledger.Register(rule);
            string Check(uint sequence, ulong scope) => ledger.Check(rule, 1, 1, 0, true, true, sequence, scope, 1, true, false, null, 0);
            Assert.IsNull(Check(uint.MaxValue, 10)); Assert.IsNull(Check(0, 10));
            Assert.AreEqual("REQUEST_REPLAY", Check(0, 10)); Assert.AreEqual("REQUEST_REPLAY", Check(uint.MaxValue, 10));
            Assert.IsNull(Check(0, 11)); // 服务器生成的新角色对象可以从新序号开始。
            Assert.AreEqual("REQUEST_REPLAY", Check(0, 10));
        }
        [Test]
        public void GuardRejectsUnknownPoliciesAndBoundsClientState()
        {
            var rule = new ServerRequestRule("bounded", 100, 100, 0);
            var ledger = new RequestLedger();
            Assert.AreEqual("REQUEST_UNKNOWN_RULE", ledger.Check(rule, 1, 1, 0, true, true, null, 0, 0, true, false, null, 0));
            ledger.Register(rule);
            Assert.Throws<InvalidOperationException>(() => ledger.Register(new ServerRequestRule("bounded", 1, 1, 0)));
            for (ulong i = 0; i < 128; i++) Assert.IsNull(ledger.Check(rule, i, i, 0, true, true, null, 0, 1, true, false, null, 0));
            Assert.AreEqual("REQUEST_BUSY", ledger.Check(rule, 129, 129, 0, true, true, null, 0, 1, true, false, null, 0));
            ledger.Prune(id => id == 0); Assert.AreEqual(1, ledger.ClientCount);
        }
        [Test]
        public void DuplicateAccountAndHeldSessionRequireOriginalRecoveryProof()
        {
            var book = new AccountBindingBook(); bool online = true, held = false;
            Assert.IsNull(book.TryClaim("alice", 1, ulong.MaxValue, 0, 10, id => online, id => held)); book.Confirm(1);
            Assert.AreEqual("AUTH_ALREADY_CONNECTED", book.TryClaim("alice", 2, 1, 1, 10, id => online, id => held));
            online = false; held = true;
            Assert.AreEqual("AUTH_SESSION_HELD", book.TryClaim("alice", 2, ulong.MaxValue, 2, 10, id => online, id => held));
            Assert.IsNull(book.TryClaim("alice", 2, 1, 2, 10, id => online, id => held));
            book.Rollback(2); Assert.IsTrue(book.TryGetClient("alice", out ulong current)); Assert.AreEqual(1, current);
            Assert.IsNull(book.TryClaim("alice", 3, 1, 3, 10, id => online, id => held));
            Assert.AreEqual(3, book.Revoke(1)); Assert.AreEqual(0, book.Count);
        }
        [Test]
        public void RecoveryTicketCannotMoveBetweenAccountsAndKickRevokesPendingSuccessor()
        {
            var table = new RecoveryRegistry(2, 30);
            table.Prepare(1, Encoding.ASCII.GetBytes("R1|N"), 0, "alice"); table.Connect(1, 0);
            var ticket = table.CreateResumeData(1); table.Disconnect(1, 1);
            Assert.AreEqual("AUTH_ACCOUNT_MISMATCH", table.Prepare(2, ticket, 2, "bob"));
            Assert.AreEqual(1, table.HeldCount(2));
            Assert.IsNull(table.Prepare(2, ticket, 2, "alice"));
            table.Revoke(1);
            Assert.IsNull(table.Connect(2, 3)); Assert.AreEqual(0, table.HeldCount(3));
        }
        [Test]
        public void ErrorCodesNeverEchoBackendBodies()
        {
            Assert.AreEqual("AUTH_REJECTED", AccountValidationResult.Reject("credential=secret or stack trace").Error);
            Assert.AreEqual("AUTH_INVALID_TOKEN", AccountValidationResult.Reject("AUTH_INVALID_TOKEN").Error);
        }
        [Test]
        public void RepeatedRespawnsReleaseSequenceScopesWithoutResettingAggregateQuota()
        {
            var rule = new ServerRequestRule("spawn", 100, 100, 0, sequence: true);
            var ledger = new RequestLedger(1, 1); ledger.Register(rule);
            for (ulong scope = 1; scope <= 1000; scope++)
            {
                Assert.IsNull(ledger.Check(rule, 1, 1, 0, true, true, 1, scope, scope, true, false, null, 0));
                ledger.ForgetScope(1, scope);
                Assert.AreEqual("RATE_LIMITED", ledger.Check(rule, 1, 1, 0, true, true, 1, scope, scope, true, false, null, 0));
            }
        }
    }
}
