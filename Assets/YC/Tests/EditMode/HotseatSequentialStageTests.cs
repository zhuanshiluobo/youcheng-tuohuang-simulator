using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Travel;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class HotseatSequentialStageTests
    {
        private const string HoldType = "test.hotseat.required_response";
        private static int commandOrdinal;
        private sealed class Fixture
        {
            public GameSession Session;
            public EffectRegistry Registry;
            public GameState State => Session.State;
        }

        [Test]
        public void LocalNonFirstRhineDepartmentRemainsControlledUntilAnswered()
        {
            Fixture fixture = null;
            // 正式配方和真实开局；只寻找莱茵在后位初始候选中的确定性种子。
            for (int seed = 1; seed <= 16; seed++)
            {
                var candidate = Build(LaunchMode.Local, true, 2, seed);
                var preparation = candidate.State.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == ExpansionSetupFlow.PreparationTypeId);
                var draft = ItemTransferEffectExecutor.Value(preparation.ContinuationData, "candidateEnterpriseItemIds").Items;
                if (draft.Skip(4).Any(id => IsRhine(candidate, id.StringValue))) { fixture = candidate; break; }
            }
            Assert.That(fixture != null, Is.True, "正式种子中须有后位莱茵候选。");
            Assert.That(fixture.State.RuleConfiguration.PlayerStageSchedulingMode, Is.EqualTo(PlayerStageSchedulingMode.Sequential));
            Assert.That(fixture.State.EffectRuntime.MainNodes.Where(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntrance).Select(n => n.PlayerId), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(fixture.State.EffectRuntime.MainNodes.Any(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntranceSetup), Is.False);
            var first = Open(fixture).Single();
            Assert.That(first.AnsweringPlayerId, Is.EqualTo(1));
            var firstChoices = first.CandidateIds.Where(id => !IsRhine(fixture, id)).Take(first.MinSelections).ToArray();
            Submit(fixture, first, firstChoices);
            var second = Open(fixture).Single();
            Assert.That(second.AnsweringPlayerId, Is.EqualTo(2));
            string rhine = second.CandidateIds.Single(id => IsRhine(fixture, id));
            Submit(fixture, second, new[] { rhine }.Concat(second.CandidateIds.Where(id => id != rhine)).Take(second.MinSelections));
            var department = Open(fixture).Single();
            Assert.That(department.InteractionTypeId, Is.EqualTo("setup.department.choose"));
            Assert.That(department.AnsweringPlayerId, Is.EqualTo(2));
            Assert.That(Open(fixture).Any(r => r.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId), Is.False);
            var resolver = new LocalPlayerResolver();
            var before = GameStateCloneService.DeepClone(fixture.State);
            for (int read = 0; read < 3; read++) Assert.That(resolver.Resolve(fixture.State, 1, true), Is.EqualTo(2));
            Assert.That(GameStateCloneService.AreEquivalent(before, fixture.State), Is.True);
            Submit(fixture, department);
            Assert.That(Open(fixture).Single(r => r.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId).AnsweringPlayerId, Is.EqualTo(1));
        }

        [Test]
        public void LocalEntranceKeepsPlacementInfluenceAndSecretRetentionBeforeTheNextBoundary()
        {
            var fixture = Build(LaunchMode.Local, true, 2, 42);
            DriveUntil(fixture, () => Open(fixture).Any(r => r.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId));
            RegisterHold(fixture, PlayerEntranceEffectExecutor.EventType);
            Submit(fixture, Open(fixture).Single(r => r.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId));
            DriveUntil(fixture, () => Open(fixture).Any(r => r.InteractionTypeId == HoldType));
            Assert.That(fixture.State.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(Open(fixture).Any(r => r.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId && r.AnsweringPlayerId == 2), Is.False);
            Assert.That(new LocalPlayerResolver().Resolve(fixture.State, 1, true), Is.EqualTo(2), "规则要求的跨玩家必要响应仍可以接管本机。");
            Submit(fixture, Open(fixture).Single(r => r.InteractionTypeId == HoldType));
            DriveUntil(fixture, () => Open(fixture).Any(r => r.InteractionTypeId == ExpansionSetupFlow.BasicCharacterInteraction && r.AnsweringPlayerId == 1));
            Assert.That(fixture.State.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(Open(fixture).Any(r => r.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId && r.AnsweringPlayerId == 2), Is.False);
            Assert.That(RoundStarted(fixture), Is.False);
            var original = GameStateCloneService.DeepClone(fixture.State);
            string digest = RuleStateDigestService.Compute(original);
            var differentMode = GameStateCloneService.DeepClone(original);
            differentMode.RuleConfiguration.PlayerStageSchedulingMode = PlayerStageSchedulingMode.Concurrent;
            Assert.That(RuleStateDigestService.Compute(differentMode) == digest, Is.False, "新增持久策略必须进入现有完整摘要。");
            Assert.That(original.RuleConfiguration.Clone().PlayerStageSchedulingMode, Is.EqualTo(PlayerStageSchedulingMode.Sequential));
            string json = HostRecoveryService.SerializeArchive(fixture.Session.CreateHostArchive());
            var recovered = HostRecoveryService.RecoverJson(json, fixture.Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(GameStateCloneService.AreEquivalent(original, recovered.State), Is.True);
            Assert.That(RuleStateDigestService.Compute(recovered.State), Is.EqualTo(digest));
            fixture.Session.ReplaceState(recovered.State);
            var oldJson = JObject.Parse(json); ((JObject)oldJson["Snapshot"]["State"]["RuleConfiguration"]).Remove("PlayerStageSchedulingMode");
            var old = HostRecoveryService.RecoverJson(oldJson.ToString(Formatting.None), fixture.Registry);
            Assert.That(old.Succeeded, Is.True, old.Diagnostic);
            Assert.That(old.State.RuleConfiguration.PlayerStageSchedulingMode, Is.EqualTo(PlayerStageSchedulingMode.Concurrent));
            Assert.That(old.State.EffectRuntime.MainNodes.Select(n => n.NodeId), Is.EqualTo(original.EffectRuntime.MainNodes.Select(n => n.NodeId)), "缺省策略不重建已在途的逐位主链。");
            Submit(fixture, Open(fixture).Single(r => r.InteractionTypeId == ExpansionSetupFlow.BasicCharacterInteraction));
            var secondEntrance = Open(fixture).Single(r => r.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId);
            Assert.That(secondEntrance.AnsweringPlayerId, Is.EqualTo(2));
            Submit(fixture, secondEntrance);
            DriveUntil(fixture, () => Open(fixture).Any(r => r.InteractionTypeId == "setup.influence.optional"));
            Assert.That(fixture.State.CurrentPlayerId, Is.EqualTo(2));
            Assert.That(RoundStarted(fixture), Is.False);
            Submit(fixture, Open(fixture).Single(r => r.InteractionTypeId == "setup.influence.optional"));
            DriveUntil(fixture, () => Open(fixture).Any(r => r.InteractionTypeId == ExpansionSetupFlow.BasicCharacterInteraction));
            Assert.That(fixture.State.CurrentPlayerId, Is.EqualTo(2));
            Assert.That(RoundStarted(fixture), Is.False);
            Submit(fixture, Open(fixture).Single(r => r.InteractionTypeId == ExpansionSetupFlow.BasicCharacterInteraction));
            Assert.That(RoundStarted(fixture), Is.True);
        }

        [TestCase(LaunchMode.Local)]
        [TestCase(LaunchMode.Host)]
        public void NormalCoverAndCollectionUseTheConfiguredModeAndKeepRequiredChildren(LaunchMode mode)
        {
            var fixture = Build(mode, false, 3, 42);
            DriveUntil(fixture, () => fixture.State.Phase == GamePhase.CharacterCover);
            RegisterHold(fixture, "CharacterCardCovered");
            bool sequential = mode == LaunchMode.Local;
            Assert.That(Open(fixture).Count(r => r.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId), Is.EqualTo(sequential ? 1 : 3));
            Submit(fixture, Open(fixture).Single(r => r.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId && r.AnsweringPlayerId == 1));
            Assert.That(Open(fixture).Any(r => r.InteractionTypeId == HoldType), Is.True);
            Assert.That(Open(fixture).Any(r => r.InteractionTypeId == CharacterCoverEffectExecutor.InteractionTypeId && r.AnsweringPlayerId == 2), Is.EqualTo(!sequential));
            if (sequential) Assert.That(fixture.State.CurrentPlayerId, Is.EqualTo(1), "盖放事实已写入但必要子结算仍未结束。");
            Assert.That(fixture.State.Phase, Is.EqualTo(GamePhase.CharacterCover));
            Submit(fixture, Open(fixture).Single(r => r.InteractionTypeId == HoldType));
            DriveUntil(fixture, () => fixture.State.Phase == GamePhase.ActionRound1);
            var round = new RoundExecutionService(fixture.Registry);
            for (int action = 0; action < 6; action++)
            {
                var result = round.CompleteMainAction(fixture.State, fixture.State.CurrentPlayerId);
                Assert.That(result.IsValid, Is.True, result.Reason);
            }
            Assert.That(fixture.State.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            var requests = Open(fixture).Where(r => r.InteractionTypeId == "route_resource.selection").ToArray();
            Assert.That(requests.Length, Is.EqualTo(sequential ? 1 : 3));
            var first = requests.Single(r => r.AnsweringPlayerId == (sequential ? 1 : 3));
            string scope = first.PromptParameters.Properties.Single(p => p.Name == "scopeKey").Value.StringValue;
            Submit(fixture, first, new[] { RouteSelectionAnswerCodec.Encode(scope, RouteSelectionCandidateKind.Commit) });
            Assert.That(fixture.State.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            Assert.That(Open(fixture).Any(r => r.InteractionTypeId == "route_resource.selection" && r.AnsweringPlayerId == 2), Is.True,
                "采集后请求：" + string.Join(",", Open(fixture).Select(r => r.InteractionTypeId + ":" + r.AnsweringPlayerId)) +
                "；任务：" + string.Join(",", fixture.State.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == RouteResourceSelectionEffectExecutor.TypeId).Select(n => n.PlayerId + ":" + n.Status + ":" + n.FlowStage)));
            if (sequential) Assert.That(fixture.State.CurrentPlayerId, Is.EqualTo(2));
            var original = GameStateCloneService.DeepClone(fixture.State);
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(fixture.Session.CreateHostArchive()), fixture.Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(GameStateCloneService.AreEquivalent(original, recovered.State), Is.True);
        }

        [Test]
        public void NetworkEntranceStillReleasesPlacementWhileEarlierRequiredWorkWaits()
        {
            var fixture = Build(LaunchMode.Host, true, 2, 42);
            DriveUntil(fixture, () => Open(fixture).Any(r => r.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId));
            RegisterHold(fixture, PlayerEntranceEffectExecutor.EventType);
            Submit(fixture, Open(fixture).Single(r => r.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId));
            DriveUntil(fixture, () => Open(fixture).Any(r => r.InteractionTypeId == HoldType));
            Assert.That(Open(fixture).Any(r => r.InteractionTypeId == PlayerEntranceEffectExecutor.InteractionTypeId && r.AnsweringPlayerId == 2) ||
                fixture.State.EffectRuntime.RuleEvents.Any(e => e.EventType == PlayerEntranceEffectExecutor.EventType && e.PlayerId == 2), Is.True,
                "入场请求：" + string.Join(",", Open(fixture).Select(r => r.InteractionTypeId + ":" + r.AnsweringPlayerId)) +
                "；入场效果：" + string.Join(",", fixture.State.EffectRuntime.EffectNodes.Where(n => n.EffectTypeId == PlayerEntranceEffectExecutor.TypeId).Select(n => n.PlayerId + ":" + n.Status + ":" + n.FlowStage)) +
                "；规则收据：" + string.Join(",", fixture.State.EffectRuntime.DispatchReceipts.Select(r => r.SubscriptionId + ":" + r.Succeeded)));
            var firstTask = fixture.State.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == RoundExecutionService.PlayerEntranceTaskTypeId && n.PlayerId == 1);
            Assert.That(firstTask.Status == EffectNodeStatus.Completed, Is.False);
            Assert.That(RoundStarted(fixture), Is.False);
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(fixture.Session.CreateHostArchive()), fixture.Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Assert.That(GameStateCloneService.AreEquivalent(fixture.State, recovered.State), Is.True);
        }

        [Test]
        public void NetworkSetupRemainsConcurrentAndPublicBuilderCallsStayCompatible()
        {
            var host = Build(LaunchMode.Host, true, 3, 42);
            var client = Build(LaunchMode.Client, true, 3, 42, true);
            Assert.That(host.State.RuleConfiguration.PlayerStageSchedulingMode, Is.EqualTo(PlayerStageSchedulingMode.Concurrent));
            Assert.That(client.State.RuleConfiguration.PlayerStageSchedulingMode, Is.EqualTo(PlayerStageSchedulingMode.Concurrent));
            Assert.That(Open(host).Where(r => r.InteractionTypeId == ExpansionSetupFlow.EnterpriseChoiceInteraction).Select(r => r.AnsweringPlayerId), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(host.State.EffectRuntime.MainNodes.Count(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerEntranceSetup), Is.EqualTo(1));
            var builder = new RoundMainChainBuilder();
            var legacy = builder.Build("compat", "", 1, new[] { 1, 2 }, 1, true);
            Assert.That(legacy.Nodes[0].NodeTypeId, Is.EqualTo(RoundMainlineNodeTypeIds.PlayerEntranceSetup));
            var sequential = builder.Build("compat", "", 1, new[] { 1, 2 }, 1, true, PlayerStageSchedulingMode.Sequential);
            Assert.That(sequential.Nodes.Take(2).Select(n => n.PlayerId), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(builder.BuildNodes("compat", "", 2, new[] { 1, 2 }).Select(n => n.NodeId), Is.EqualTo(builder.Build("compat", "", 2, new[] { 1, 2 }).Nodes.Select(n => n.NodeId)));
            Assert.That(client.State.EffectRuntime.MainNodes, Is.Empty, "延迟客户端在真实初始快照安装前不重新开局。");
        }

        private static bool RoundStarted(Fixture fixture) => fixture.State.EffectRuntime.RuleEvents.Any(e => e.EventType == "RoundStarted");
        private static InteractionRequest[] Open(Fixture fixture) => fixture.State.EffectRuntime.InteractionRequests.Where(r => r.Status == "open" && !r.IsInternalMainlineCompletion()).ToArray();
        private static bool IsRhine(Fixture fixture, string id) => fixture.Registry.EnterpriseDefinitions?.FindEnterprise(fixture.State.ItemZones.Items.Single(i => i.ItemId == id).DefinitionId)?.DepartmentRewardPolicy == "active_department";
        private static void DriveUntil(Fixture fixture, Func<bool> finished)
        {
            for (int guard = 0; guard < 80 && !finished(); guard++)
            {
                var requests = Open(fixture);
                Assert.That(requests.Length > 0, Is.True, "正式流程未达到目标但没有玩家输入。" + fixture.State.EffectRuntime.LastFaultMessage);
                var resolver = new LocalPlayerResolver();
                int player = resolver.Resolve(fixture.State, 1, true);
                Submit(fixture, requests.First(r => r.AnsweringPlayerId == player));
            }
            Assert.That(finished(), Is.True, "必要流程须在有界输入内到达目标。");
        }
        private static void Submit(Fixture fixture, InteractionRequest request, IEnumerable<string> choices = null)
        {
            var command = new GameCommand { CommandId = "hotseat-check-" + (++commandOrdinal), Kind = GameCommandKind.AnswerInteraction, PlayerId = request.AnsweringPlayerId };
            command.Parameters["interactionId"] = request.GetStableInteractionId();
            command.Parameters["expectedRevision"] = request.StateRevision.ToString(CultureInfo.InvariantCulture);
            if (request.AnswerSchema == "boolean_accept") command.Parameters["answerValue"] = request.AllowDecline ? "false" : "true";
            else command.OptionIds.AddRange(choices ?? request.CandidateIds.Take(Math.Max(1, request.MinSelections)));
            var result = fixture.Session.Submit(command);
            Assert.That(result.Succeeded, Is.True, request.InteractionTypeId + ": " + result.Validation?.Reason);
        }
        private static void RegisterHold(Fixture fixture, string eventType)
        {
            // 真实内核里的窄观察者只制造必要响应边界，不替代正式玩法执行器。
            fixture.Registry.Register(new EffectRegistration(HoldType, context => context.GetLatestInteractionAnswer() != null ? EffectStepResult.Completed() :
                EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec { InteractionTypeId = HoldType, AnsweringPlayerId = 2,
                    AnswerSchema = "boolean_accept" }), EffectExecutorKind.IntrinsicFlow));
            fixture.Registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.hotseat.hold." + eventType, eventType, "*", "test.hotseat.handler." + eventType,
                context => context.Event.PlayerId == 1 ? new List<EffectSpec> { EffectSpec.Create(HoldType, playerId: 2) } : new List<EffectSpec>()));
        }
        private static Fixture Build(LaunchMode mode, bool enterprise, int players, int seed, bool defer = false)
        {
            var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            var bootstrapper = Type.GetType("YC.Presentation.GameSessionBootstrapper, Assembly-CSharp", true);
            var instance = contextType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static); var previous = instance.GetValue(null); var setter = instance.GetSetMethod(true);
            GameObject host = null;
            try
            {
                setter.Invoke(null, new object[] { null }); host = new GameObject("HotseatSequentialStage"); var context = host.AddComponent(contextType);
                const string gameId = "hotseat-sequential-check";
                var config = new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = players,
                    EnabledExpansionIds = enterprise ? new List<string> { "core", "enterprise" } : new List<string> { "core" } };
                var order = Enumerable.Range(1, players).ToArray();
                var seats = order.Select(p => new PlayerSeat { PlayerId = p, PlayerName = "玩家" + p }).ToList();
                contextType.GetMethods().Single(m => m.Name == "Configure" && m.GetParameters().Length == 9).Invoke(context,
                    new object[] { mode, 1, "hotseat-sequential-room", seats, config, order, gameId, seed, GameplayRandomRuntime.Create(gameId) });
                var method = bootstrapper.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Single(m => m.Name == "Build" && m.GetParameters().Length == (defer ? 4 : 3));
                var result = method.Invoke(null, defer ? new object[] { context, false, false, true } : new object[] { context, false, false });
                return new Fixture { Session = (GameSession)result.GetType().GetField("Session").GetValue(result), Registry = (EffectRegistry)result.GetType().GetField("EffectRegistry").GetValue(result) };
            }
            finally { if (host != null) UnityEngine.Object.DestroyImmediate(host); setter.Invoke(null, new[] { previous }); }
        }
    }
}
