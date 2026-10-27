using System;
using System.Collections.Generic;
using System.Reflection;
using YC.Application.Sessions;
using YC.Domain.State;
#if !YC_WINDOW_REGISTRATION_OFFLINE
using NUnit.Framework;
#endif

namespace YC.Tests.EditMode
{
    // 原许可Facts的登记层隔离回归；不替代完整规则尾段重放、原生或正常四端验收。
    public sealed class WindowRegistrationReplayConflictTests
    {
        private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        private static OperationWindowDescriptorDto ObservedWindow() => new OperationWindowDescriptorDto {
            GameId = "1d6fdd42570641779eabf9afabad210b", SimulationEpoch = 1, MembershipVersion = 1,
            OperationWindowId = "operation_window_b4237e22d8cf7952d32d9cf5874d13874d7e013356524469d61b8e18bc932873",
            WindowGeneration = 1, OpenedAtGlobalSequence = 228, BaseWindowSequence = 0,
            AuthorizedPlayerIds = new List<int> { 3 }, RequiredReplicaIds = new List<string> { "player.1", "player.2", "player.3", "player.4" },
            PrefixDigest = "3261992550c10bfd4b0f5e81c791fa81747b504df397cd9d0c525f08500d6274",
            WindowContractDigest = "fc5239b290107dd779d3786212ccbef82261cddd19420a405e082cc2e4079d60" };
        private static OrderedCommandSequencer Ledger(OperationWindowDescriptorDto facts)
        {
            var ledger = new OrderedCommandSequencer(facts.GameId, facts.SimulationEpoch, facts.MembershipVersion,
                baseGlobalSequence: 229, baseCommandPrefixDigest: facts.PrefixDigest);
            ledger.RegisterWindow(facts); return ledger;
        }
        private static void RegisterForFirstUse(OrderedCommandSequencer ledger, OperationWindowDescriptorDto observed)
        {
            var method = typeof(WindowLocalExecution).GetMethod("RegisterHostWindowForFirstUse", BindingFlags.Static | BindingFlags.NonPublic);
            Require(method != null, "缺少实际Host首次使用登记接线");
            try { method.Invoke(null, new object[] { ledger, observed }); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private static bool Conflict(Action action)
        { try { action(); return false; } catch (ArgumentException error) { return error.Message == "window_registration_conflict"; } }

#if !YC_WINDOW_REGISTRATION_OFFLINE
        [Test]
#endif
        public void Original228OpeningVersus229FirstUseStillReproducesStrictRegistrationConflict()
        {
            var original = ObservedWindow(); var ledger = Ledger(original);
            var legacyFirstUse = GameStateCloneService.DeepClone(original); legacyFirstUse.OpenedAtGlobalSequence = 229;
            Require(Conflict(() => ledger.RegisterWindow(legacyFirstUse)), "原228/229差异没有复现严格登记冲突");
        }
#if !YC_WINDOW_REGISTRATION_OFFLINE
        [TestCase(false)] [TestCase(true)]
#endif
        public void FirstUsePreservesRegisteredOpeningAndRecoveryLedgerMetadata(bool withSavedBase)
        {
            var original = ObservedWindow();
            if (withSavedBase) { original.BaseWindowSequence = 2; original.PrefixDigest = new string('b', 64); }
            var ledger = Ledger(original); var firstUse = ObservedWindow(); firstUse.OpenedAtGlobalSequence = 229;
            RegisterForFirstUse(ledger, firstUse); RegisterForFirstUse(ledger, ObservedWindow());
            Require(ledger.TryReadRegisteredWindow(original.OperationWindowId, out var retained) &&
                retained.OpenedAtGlobalSequence == 228 && retained.BaseWindowSequence == original.BaseWindowSequence &&
                retained.PrefixDigest == original.PrefixDigest && retained.WindowContractDigest == original.WindowContractDigest,
                "较晚首次使用或再登记改变了原开启位置/保存基线/合同");
            Require(ledger.LastSequencedSequence == 229, "只读登记错误分配了全局命令序号");
        }
#if !YC_WINDOW_REGISTRATION_OFFLINE
        [Test]
#endif
        public void NewSuccessorWindowRegistersOnceAtCurrentObservation()
        {
            var original = ObservedWindow(); var ledger = Ledger(original); var successor = ObservedWindow();
            successor.OperationWindowId += ".successor"; successor.OpenedAtGlobalSequence = 229;
            RegisterForFirstUse(ledger, successor); RegisterForFirstUse(ledger, GameStateCloneService.DeepClone(successor));
            Require(ledger.TryReadRegisteredWindow(successor.OperationWindowId, out var registered) && registered.OpenedAtGlobalSequence == 229 &&
                registered.BaseWindowSequence == 0 && registered.PrefixDigest == successor.PrefixDigest, "新后继窗登记缺失或改写了位置");
        }
#if !YC_WINDOW_REGISTRATION_OFFLINE
        [TestCase("generation")] [TestCase("authorization")] [TestCase("contract")] [TestCase("requiredReplicas")]
#endif
        public void FirstUseStillRejectsRealWindowContractConflicts(string field)
        {
            var original = ObservedWindow(); var ledger = Ledger(original); var conflicting = ObservedWindow(); conflicting.OpenedAtGlobalSequence = 229;
            if (field == "generation") conflicting.WindowGeneration++;
            else if (field == "authorization") conflicting.AuthorizedPlayerIds = new List<int> { 2, 3 };
            else if (field == "contract") conflicting.WindowContractDigest = new string('c', 64);
            else if (field == "requiredReplicas") conflicting.RequiredReplicaIds.Add("player.5");
            else throw new ArgumentException(field);
            Require(Conflict(() => RegisterForFirstUse(ledger, conflicting)), "真实窗口冲突被吞掉：" + field);
            Require(ledger.TryReadRegisteredWindow(original.OperationWindowId, out var retained) &&
                retained.WindowGeneration == 1 && retained.AuthorizedPlayerIds.Count == 1 && retained.AuthorizedPlayerIds[0] == 3 &&
                retained.RequiredReplicaIds.Count == 4 && retained.WindowContractDigest == original.WindowContractDigest,
                "失败登记覆盖了原合同：" + field);
        }
#if !YC_WINDOW_REGISTRATION_OFFLINE
        [Test]
#endif
        public void RegisteredWindowReadReturnsDetachedFactsAndStrictOpeningCheckRemains()
        {
            var original = ObservedWindow(); var ledger = Ledger(original);
            Require(ledger.TryReadRegisteredWindow(original.OperationWindowId, out var copy), "缺少已登记窗口");
            copy.OpenedAtGlobalSequence = 229; copy.AuthorizedPlayerIds.Add(2);
            Require(ledger.TryReadRegisteredWindow(original.OperationWindowId, out var current) && current.OpenedAtGlobalSequence == 228 && current.AuthorizedPlayerIds.Count == 1,
                "只读Facts出口泄露了可变内部合同");
            var otherOpening = ObservedWindow(); otherOpening.OpenedAtGlobalSequence = 227;
            Require(Conflict(() => ledger.RegisterWindow(otherOpening)), "一般登记的开启位置严格检查被放宽");
            Require(!ledger.TryReadRegisteredWindow(null, out _) && !ledger.TryReadRegisteredWindow("absent", out _), "缺失窗口查询改变了登记集合");
        }
    }
}
