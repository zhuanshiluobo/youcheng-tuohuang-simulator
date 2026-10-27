using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
#if !YC_FIXED_CAPTURE_R19_OFFLINE
using NUnit.Framework;
#endif
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Tests.EditMode.ExpansionRules;

namespace YC.Tests.EditMode
{
    // 固定图导出的隔离回归，不计正常游戏/页面或基础模式持续内存验收。
    public sealed class FixedCaptureExportR19Tests
    {
        public static string OfflineOutput;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        private static CleanupSourceTestFixture Fixture(string label)
        {
#if YC_FIXED_CAPTURE_R19_OFFLINE
            var artifacts = new IsolatedTestArtifacts(Path.Combine(OfflineOutput, label));
#else
            var artifacts = IsolatedTestArtifacts.BeginCase();
#endif
            var f = new CleanupSourceTestFixture(artifacts, inMemoryInput: true);
            f.State.RandomRuntime ??= GameplayRandomRuntime.Create(f.State.GameId);
            foreach (var player in f.State.Players) player.Color = (PlayerColor)(player.PlayerId - 1);
            return f;
        }
        private static GameSessionRecoveryPoint Freeze(GameSession session) => (GameSessionRecoveryPoint)typeof(GameSession)
            .GetMethod("FreezeBackgroundRecoveryPoint", Private).Invoke(session, new object[] { session.EnableConfirmedRecoveryCapture() });
        private static GameSessionRecoveryExport Export(GameSessionRecoveryPoint point)
        {
            try { return (GameSessionRecoveryExport)typeof(GameSessionRecoveryPoint).GetMethod("ExportDetached", Private).Invoke(point, null); }
            catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
        private static GameState State(GameSessionRecoveryPoint point) => (GameState)typeof(GameSessionRecoveryPoint).GetProperty("CapturedState", Private).GetValue(point);

#if !YC_FIXED_CAPTURE_R19_OFFLINE
        [Test]
#endif
        public void RepeatedFixedExportsOwnTheirStateAndRestoreTheSameStructure()
        {
            var f = Fixture("r19-repeated"); var point = Freeze(new GameSession(f.State)); var source = State(point);
            var first = Export(point); var second = Export(point); string expected = HostRecoveryService.SerializeArchive(second.Archive);
            var projection = typeof(LegacyPositionList<string>).GetField("projection", Private).GetValue(source.Players[0].HandCardIds);
            Require(!ReferenceEquals(source, first.Archive.Snapshot.State) && !ReferenceEquals(first.Archive.Snapshot.State, second.Archive.Snapshot.State), "外部归档借用了内部图或另一份导出");
            first.Archive.Snapshot.State.Players[0].Name = "外部修改";
            first.Archive.Snapshot.State.ItemZones.Items[0].ZoneId = "外部修改";
            Require(HostRecoveryService.SerializeArchive(second.Archive) == expected && HostRecoveryService.SerializeArchive(Export(point).Archive) == expected, "修改导出污染另一份导出或固定点");
            Require(ReferenceEquals(projection, typeof(LegacyPositionList<string>).GetField("projection", Private).GetValue(source.Players[0].HandCardIds)), "验证器重绑了内部旧列表投影");
            var recovered = HostRecoveryService.Recover(second.Archive, f.Registry);
            Require(recovered.Succeeded && RuleStateDigestService.Compute(recovered.State) == RuleStateDigestService.Compute(source), "导出恢复结构改变:" + recovered.Diagnostic);
        }

#if !YC_FIXED_CAPTURE_R19_OFFLINE
        [TestCase(false)] [TestCase(true)]
#endif
        public void LegacyMissingLaunchOrderIsNormalizedOnlyOnDetachedArchive(bool nullOrder)
        {
            var f = Fixture("r19-legacy-" + nullOrder); f.State.LaunchPlayerOrder = nullOrder ? null : new List<int>();
            var point = Freeze(new GameSession(f.State)); var source = State(point); var order = source.LaunchPlayerOrder;
            var exported = Export(point);
            Require(ReferenceEquals(order, source.LaunchPlayerOrder) && (order == null || order.Count == 0), "旧顺序补齐写回固定图");
            Require(TurnOrderService.IsValidPlayerOrder(exported.Archive.Snapshot.State.LaunchPlayerOrder, source.Players.Select(p => p.PlayerId)), "旧顺序未按原入口补齐");
            Require(HostRecoveryService.Recover(exported.Archive, f.Registry).Succeeded, "旧顺序兼容结果不能恢复");
        }

#if !YC_FIXED_CAPTURE_R19_OFFLINE
        [TestCase(false)] [TestCase(true)]
#endif
        public void InvalidFixedCaptureStillFailsWithoutPublishingOrChangingItsInput(bool duplicateOrder)
        {
            var f = Fixture("r19-invalid-" + duplicateOrder);
            if (duplicateOrder) f.State.LaunchPlayerOrder = new List<int> { 1, 1, 3 };
            else f.State.EffectRuntime.SchemaVersion = EffectRuntimeState.CurrentSchemaVersion + 1;
            var point = Freeze(new GameSession(f.State)); var source = State(point); var original = GameStateCloneService.DeepClone(source);
            GameSessionRecoveryExport published = null; bool rejected = false;
            try { published = Export(point); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected && published == null && GameStateCloneService.AreEquivalent(source, original), "无效图被导出、部分发布或校验改写了输入");
        }

#if !YC_FIXED_CAPTURE_R19_OFFLINE
        [Test]
#endif
        public void ContinuationNestedFieldsStayOwnedByEachExport()
        {
            // 人工日志DTO只核隔离，不冒充真实日志或正常恢复覆盖。
            var f = Fixture("r19-continuation"); var session = new GameSession(f.State); var original = Freeze(session);
            var continuation = new GameSessionLogContinuationDto { HasPendingActionLog = true, PlayerId = 1,
                Command = GameCommandDto.FromCommand(new GameCommand { CommandId = "r19.cont", PlayerId = 1, Kind = GameCommandKind.UseCharacterCard }),
                Before = new GameSessionActionSnapshotDto { ContentState = new GameState { GameId = "冻结前置" },
                    Players = new List<GameSessionPlayerSnapshotDto> { new GameSessionPlayerSnapshotDto { PlayerId = 1, Resources = new ResourceSet { Originium = 3 } } } },
                Commands = new List<GameCommandDto>() };
            continuation.Command.Parameters.Add(new GameCommandParameterDto { Key = "固定参数", Value = "固定值" }); continuation.Commands.Add(continuation.Command);
            var constructor = typeof(GameSessionRecoveryPoint).GetConstructor(Private, null,
                new[] { typeof(GameSession), typeof(string), typeof(GameState), typeof(GameSessionLogContinuationDto) }, null);
            var point = (GameSessionRecoveryPoint)constructor.Invoke(new object[] { session, "r19.cont", State(original), continuation });
            var a = Export(point); var b = Export(point); string expected = SimulationConfirmedRecoveryInstaller.SerializeContinuation(continuation);
            a.Continuation.Command.Parameters.Clear(); a.Continuation.Before.ContentState.GameId = "外部修改";
            a.Continuation.Before.Players[0].Resources.Originium = 999; a.Continuation.Commands.Clear();
            Require(SimulationConfirmedRecoveryInstaller.SerializeContinuation(b.Continuation) == expected &&
                SimulationConfirmedRecoveryInstaller.SerializeContinuation(continuation) == expected, "continuation可变子图借用了固定捕获");
        }

#if !YC_FIXED_CAPTURE_R19_OFFLINE
        [Test]
#endif
        public async Task OwnerCanCommitWhileExportValidationIsPausedOnItsOwnFixedPoint()
        {
            var f = Fixture("r19-concurrent"); var session = new GameSession(f.State);
            session.RegisterHandler(new InvokeSettlementOptionCommandHandler(f.Registry));
            var point = Freeze(session); var source = State(point); string digest = RuleStateDigestService.Compute(source); int revision = source.EffectRuntime.StateRevision;
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); int pauses = 0; bool timedOut = false;
            var pending = Task.Run(() => {
                ItemZoneQuery.ValidationTimingSample = _ => { if (Interlocked.Increment(ref pauses) == 1) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(15))) timedOut = true; } };
                try { return Export(point); } finally { ItemZoneQuery.ValidationTimingSample = null; }
            });
            try
            {
                Require(entered.Wait(TimeSpan.FromSeconds(10)), "未到达导出校验中的真实只读采样点");
                var option = f.Registry.GetSettlementOptions(session.State, 1).Single(o => o.SourceItemId == f.CardId && o.CleanupRequest != null);
                Require(session.Submit(CleanupSourceCommand.Create(1, "r19.advance", option.CleanupRequest, session.State.EffectRuntime.StateRevision)).Succeeded, "owner合法命令未完成");
            }
            finally { release.Set(); }
            var exported = await pending;
            Require(!timedOut && session.State.EffectRuntime.StateRevision > revision && exported.Archive.Snapshot.State.EffectRuntime.StateRevision == revision &&
                RuleStateDigestService.Compute(exported.Archive.Snapshot.State) == digest && RuleStateDigestService.Compute(source) == digest, "导出读取了推进后的活动图或修改了固定点");
            var parallel = await Task.WhenAll(Task.Run(() => Export(point)), Task.Run(() => Export(point)));
            parallel[0].Archive.Snapshot.State.Players[0].Name = "并发导出修改";
            Require(RuleStateDigestService.Compute(parallel[1].Archive.Snapshot.State) == digest, "并发导出共享可变状态");
        }
    }
}
