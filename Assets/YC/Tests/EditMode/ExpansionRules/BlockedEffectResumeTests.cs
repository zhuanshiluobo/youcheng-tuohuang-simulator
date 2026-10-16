using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class BlockedEffectResumeTests
    {
        private const string WaitType = "test.blocked_resume.wait";

        private static GameState State()
        {
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            var source = new GameState
            {
                GameId = "blocked-resume", MapId = map.MapId, ContentPackHash = "blocked-resume-content",
                Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1,
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = "blocked-resume-content" },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } }
            };
            var state = new ItemZoneStateFactory().CreateForSetup(source, map, new List<ItemRecipeEntry>
            {
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.component.marker", TypeId = "core.player_marker", Quantity = 8, PerPlayer = true, InitialZoneRole = "player_markers" },
                new ItemRecipeEntry { EntryId = "resource", DefinitionId = "core.component.resource_point", TypeId = "core.resource_point_token", Quantity = 1, InitialZoneRole = "location/A-01" }
            });
            state.Map.OpenLocationIds.Add("A-01");
            return state;
        }

        private static EffectRegistry Registry()
        {
            var registry = new EffectRegistry();
            registry.Register(WaitType, context =>
            {
                var answer = context.GetLatestInteractionAnswer();
                if (answer != null) return answer.BooleanValue ? EffectStepResult.Completed() : EffectStepResult.Failed("player_cancelled");
                return EffectStepResult.Continue("awaiting_answer").AddInteraction(new EffectInteractionSpec
                {
                    InteractionTypeId = WaitType, AnsweringPlayerId = 1, Visibility = "owner",
                    AnswerSchema = "boolean", AllowDecline = true
                });
            });
            return registry;
        }

        private static string Snapshot(GameState state) => JsonConvert.SerializeObject(state);
        private static InteractionRequest Open(GameState state) => state.EffectRuntime.InteractionRequests.Single(request => request.Status == "open");

        private static void Quiesce(EffectTreeExecutor engine)
        {
            var result = engine.RunUntilQuiescent();
            Assert.That(result.Faulted, Is.False, engine.State.EffectRuntime.LastFaultMessage);
        }

        private static GameState Restore(GameState state, EffectRegistry registry)
        {
            var restored = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic);
            return restored.State;
        }

        private static GameCommand Answer(InteractionRequest request, bool accepted)
        {
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = request.AnsweringPlayerId };
            command.Parameters[AnswerInteractionCommandHandler.InteractionIdParameter] = request.GetStableInteractionId();
            command.Parameters[AnswerInteractionCommandHandler.ExpectedRevisionParameter] = request.StateRevision.ToString(CultureInfo.InvariantCulture);
            command.Parameters[AnswerInteractionCommandHandler.AnswerValueParameter] = accepted.ToString();
            return command;
        }

        [Test]
        public void ThreeWaitingLevelsDoNotCloneOrPublishWhileTheRealInteractionIsUnanswered()
        {
            var state = State(); var registry = Registry();
            registry.Register("test.blocked_resume.middle", context => EffectStepResult.Completed().AddChild(EffectSpec.Create(WaitType, playerId: 1)));
            registry.Register("test.blocked_resume.outer", context => EffectStepResult.Completed().AddChild(EffectSpec.Create("test.blocked_resume.middle", playerId: 1)));
            var engine = new EffectTreeExecutor(state, registry);
            engine.CreateRoot(EffectSpec.Create("test.blocked_resume.outer", playerId: 1)); Quiesce(engine);
            Assert.That(state.EffectRuntime.EffectNodes.Count(node => node.Status == EffectNodeStatus.Blocked), Is.EqualTo(3));
            string before = Snapshot(state); int revision = state.EffectRuntime.StateRevision;
            using (var clones = new GameStateCloneCounter(state))
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var report = engine.RunUntilQuiescent();
                    Assert.That(report.WaitingForInput, Is.True); Assert.That(report.Faulted, Is.False);
                    Assert.That(report.Steps, Is.Zero);
                }
                Assert.That(clones.Count, Is.Zero, "等待用户不能为每个Blocked节点创建整局副本。");
            }
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision));
            Assert.That(Snapshot(state), Is.EqualTo(before));
        }

        [TestCase(EffectNodeStatus.Completed)]
        [TestCase(EffectNodeStatus.Failed)]
        public void NormalTerminalTargetStillResolvesAndCommitsOnce(EffectNodeStatus childStatus)
        {
            var state = State(); var registry = Registry();
            registry.Register("test.blocked_resume.child", context => childStatus == EffectNodeStatus.Completed ? EffectStepResult.Completed() : EffectStepResult.Failed("expected_failure"));
            registry.Register("test.blocked_resume.parent", context => EffectStepResult.Completed().AddChild(EffectSpec.Create("test.blocked_resume.child", playerId: 1)));
            var engine = new EffectTreeExecutor(state, registry);
            string parentId = engine.CreateRoot(EffectSpec.Create("test.blocked_resume.parent", playerId: 1));
            EffectNodeRuntimeState child = null;
            for (int step = 0; step < 20; step++)
            {
                child = state.EffectRuntime.EffectNodes.Find(node => node.EffectTypeId == "test.blocked_resume.child");
                if (child != null && child.Status == childStatus) break;
                Assert.That(engine.Advance(), Is.True);
            }
            Assert.That(child?.Status, Is.EqualTo(childStatus));
            Assert.That(engine.GetNode(parentId).Status, Is.EqualTo(EffectNodeStatus.Blocked));
            // 模拟已保存的正常终态目标尚未标记解除，必须沿原工作副本路径补齐。
            var blocker = state.EffectRuntime.Blockers.Single(value => value.OwnerEffectId == parentId && value.TargetEffectId == child.EffectId);
            blocker.IsResolved = false;
            int revision = state.EffectRuntime.StateRevision;
            using (var clones = new GameStateCloneCounter(state))
            {
                Assert.That(engine.Advance(), Is.True);
                Assert.That(clones.Count, Is.EqualTo(1));
            }
            Assert.That(engine.GetNode(parentId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision + 1));
            Assert.That(state.EffectRuntime.Blockers.Single(value => value.BlockerId == blocker.BlockerId).IsResolved, Is.True);
            Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.EventType == "EffectCompleted" && value.SourceEffectId == parentId), Is.EqualTo(1));
            string finished = Snapshot(state); Quiesce(engine);
            Assert.That(Snapshot(state), Is.EqualTo(finished));
        }

        [Test]
        public void FaultedTargetCannotResumeItsBlockedOwnerOrCreateAWorkingCopy()
        {
            var state = State(); var registry = Registry();
            registry.Register("test.blocked_resume.fault", context => throw new InvalidOperationException("expected_handler_fault"));
            registry.Register("test.blocked_resume.owner", context => EffectStepResult.Completed());
            var engine = new EffectTreeExecutor(state, registry);
            string ownerId = engine.CreateRoot(EffectSpec.Create("test.blocked_resume.owner", playerId: 1));
            string targetId = engine.CreateRoot(EffectSpec.Create("test.blocked_resume.fault", playerId: 1));
            Assert.That(engine.TryAddBlocker(ownerId, targetId, "dependency", "等待独立目标", out var blockerId), Is.True);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.True);
            Assert.That(engine.GetNode(targetId).Status, Is.EqualTo(EffectNodeStatus.Faulted));
            // 独立owner不是故障目标的祖先，Fault不会把它一并标成Faulted；确实存在待扫描的Blocked。
            Assert.That(engine.GetNode(ownerId).Status, Is.EqualTo(EffectNodeStatus.Blocked));
            Assert.That(state.EffectRuntime.Blockers.Single(value => value.BlockerId == blockerId).IsResolved, Is.False);
            string before = Snapshot(state);
            // PausedFault会在Advance入口提前返回；直接检查恢复扫描，覆盖Faulted目标的谓词语义。
            var method = typeof(EffectTreeExecutor).GetMethod("TryResumeBlocked", BindingFlags.Instance | BindingFlags.NonPublic);
            using (var clones = new GameStateCloneCounter(state))
            {
                Assert.That((bool)method.Invoke(engine, new object[] { null }), Is.False);
                Assert.That(clones.Count, Is.Zero);
            }
            Assert.That(Snapshot(state), Is.EqualTo(before));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void OrderedSiblingsAndCompletionReceiptsSurviveHostRecoveryAndCancellation(bool accepted)
        {
            var state = State(); var registry = Registry(); int secondRuns = 0;
            registry.Register("test.blocked_resume.second", context => { secondRuns++; return EffectStepResult.Completed(); });
            registry.Register("test.blocked_resume.parent", context => EffectStepResult.Completed()
                .AddChild(EffectSpec.Create(WaitType, playerId: 1)).AddChild(EffectSpec.Create("test.blocked_resume.second", playerId: 1)));
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.blocked_resume.observe", "EffectCompleted", "*", "test.blocked_resume.observe",
                context => new List<EffectSpec>(), EffectHandlerRole.Observer));
            var engine = new EffectTreeExecutor(state, registry);
            string parentId = engine.CreateRoot(EffectSpec.Create("test.blocked_resume.parent", playerId: 1)); Quiesce(engine);
            Assert.That(secondRuns, Is.Zero); string requestId = Open(state).GetStableInteractionId();
            state = Restore(state, registry); engine = new EffectTreeExecutor(state, registry);
            Assert.That(Open(state).GetStableInteractionId(), Is.EqualTo(requestId));
            int waitingRevision = state.EffectRuntime.StateRevision;
            using (var clones = new GameStateCloneCounter(state)) { Quiesce(engine); Assert.That(clones.Count, Is.Zero); }
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(waitingRevision)); Assert.That(secondRuns, Is.Zero);
            var session = new GameSession(state); session.RegisterHandler(new AnswerInteractionCommandHandler(registry));
            var command = Answer(Open(state), accepted); var result = session.Submit(command);
            Assert.That(result.Succeeded, Is.True, result.Validation.Reason);
            Assert.That(secondRuns, Is.EqualTo(1)); Assert.That(engine.GetNode(parentId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.EffectNodes.Single(node => node.EffectTypeId == WaitType).Status,
                Is.EqualTo(accepted ? EffectNodeStatus.Completed : EffectNodeStatus.Failed));
            int revision = state.EffectRuntime.StateRevision, events = state.EffectRuntime.RuleEvents.Count, receipts = state.EffectRuntime.DispatchReceipts.Count;
            Assert.That(session.Submit(command).Succeeded, Is.False);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision));
            state = Restore(state, registry); Quiesce(new EffectTreeExecutor(state, registry));
            Assert.That(secondRuns, Is.EqualTo(1)); Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision));
            Assert.That(state.EffectRuntime.RuleEvents.Count, Is.EqualTo(events)); Assert.That(state.EffectRuntime.DispatchReceipts.Count, Is.EqualTo(receipts));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void FormalDeploymentRequestWaitsWithoutCloningAndAnswerOrCancelKeepsBudgetSemantics(bool cancel)
        {
            var state = State(); var registry = Registry(); var service = new InfluenceService(new MapQueryService(StaticMapDefinitions.Resolve(state.MapId)));
            var session = new GameSession(state); session.RegisterHandler(new DeployInfluenceCommandHandler(service, new RoundAdvanceService(), registry));
            session.RegisterHandler(new AnswerInteractionCommandHandler(registry));
            int supply = state.FindPlayer(1).InfluenceSupply, budget = state.FindPlayer(1).RemainingMainActionsThisTurn;
            var started = session.Submit(new GameCommand { Kind = GameCommandKind.DeployInfluence, PlayerId = 1 });
            Assert.That(started.Succeeded, Is.True, started.Validation.Reason);
            var request = Open(state); Assert.That(request.InteractionTypeId, Is.EqualTo("effect.influence.place.target"));
            string before = Snapshot(state);
            using (var clones = new GameStateCloneCounter(state)) { Quiesce(new EffectTreeExecutor(state, registry)); Assert.That(clones.Count, Is.Zero); }
            Assert.That(Snapshot(state), Is.EqualTo(before));
            state = Restore(state, registry); session = new GameSession(state); session.RegisterHandler(new AnswerInteractionCommandHandler(registry));
            request = Open(state); var command = Answer(request, false);
            if (!cancel) { command.Parameters.Remove(AnswerInteractionCommandHandler.AnswerValueParameter); command.OptionIds.Add(request.CandidateIds[0]); }
            var answered = session.Submit(command); Assert.That(answered.Succeeded, Is.True, answered.Validation.Reason);
            Assert.That(state.Map.Influences.Count, Is.EqualTo(cancel ? 0 : 1));
            Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(supply - (cancel ? 0 : 1)));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(budget - (cancel ? 0 : 1)));
            int revision = state.EffectRuntime.StateRevision; Assert.That(session.Submit(command).Succeeded, Is.False);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision));
        }

        // 只包装唯一GameState构造计划，正向校准后计数，不改克隆实现或生产状态。
        // Unity测试在同步串行窗口内计数，Dispose始终恢复共享元数据；不用GC读数推测clone次数。
        private sealed class GameStateCloneCounter : IDisposable
        {
            private readonly object plan; private readonly FieldInfo factoryField; private readonly Func<object> original;
            public int Count { get; private set; }
            public GameStateCloneCounter(GameState state)
            {
                var metadata = typeof(GameStateCloneService).Assembly.GetType("YC.Domain.State.StateCopyMetadata", true);
                plan = metadata.GetMethod("For", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { typeof(GameState) });
                factoryField = metadata.GetField("create", BindingFlags.Instance | BindingFlags.NonPublic);
                original = (Func<object>)factoryField.GetValue(plan);
                factoryField.SetValue(plan, new Func<object>(() => { Count++; return original(); }));
                try
                {
                    GameStateCloneService.DeepClone(state);
                    Assert.That(Count, Is.EqualTo(1), "clone计数器必须通过一次完整GameState克隆的阳性校准。"); Count = 0;
                }
                catch { Dispose(); throw; }
            }
            public void Dispose() => factoryField.SetValue(plan, original);
        }
    }
}
