using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode
{
    // 定向协议/业务夹具；不加载Unity、界面生成器或正式对局驱动，不计完整局覆盖。
    public sealed class SettlementFlowContractTests
    {
        private GameState cleanupCommandState;
        private GameCommand cleanupCommand;

        // 仅隔离定位收尾主链接缝；不作为正常游戏或界面验收。
        private Tuple<GameState, EffectRegistry, RoundExecutionService> CleanupCommandFixture(int round, bool lastPlayer,
            SettlementWholeEffectPolicy policy = SettlementWholeEffectPolicy.Optional)
        {
            var state = State(); cleanupCommandState = state;
            foreach (var player in state.Players) player.HandCardIds.Add("liskarm");
            state.Round = round; state.MaxRounds = 8; state.Phase = GamePhase.Cleanup;
            state.StartPlayerId = state.CurrentPlayerId = 1;
            var chain = new RoundMainChainBuilder().Build(state.GameId, "", round, new[] { 1, 2 }, 1);
            var main = chain.Nodes.Single(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerCleanupWindow && n.PlayerId == (lastPlayer ? 2 : 1));
            state.EffectRuntime.MainNodes.AddRange(chain.Nodes);
            foreach (var previous in chain.Nodes.Where(n => n.MainlineIndex < main.MainlineIndex)) previous.Status = EffectNodeStatus.Completed;
            state.EffectRuntime.ActiveMainNodeId = main.NodeId;
            state.EffectRuntime.FirstMainNodeId = chain.FirstNodeId;
            state.EffectRuntime.CurrentRoundExecutionId = chain.RoundExecutionId;
            state.EffectRuntime.CurrentRoundNumber = round;
            state.EffectRuntime.CurrentRoundStartPlayerId = 1;
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            var registry = new EffectRegistry(); var rounds = new RoundExecutionService(registry);
            registry.RegisterSettlementCatalogProvider("cleanup.command.fixture", (s, w) => { },
                (s, w, e, force) => new SettlementAvailabilityResult { Enabled = true });
            var engine = rounds.CreateCommandExecutor(state);
            string id = engine.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId,
                Object("mainNodeId", NormalizedValue.CreateString(main.NodeId)), main.PlayerId));
            main.ExecutionEffectId = id; main.Status = EffectNodeStatus.Running;
            main.PublishedEventKeys.Add("PlayerCleanupStarted");
            var owner = engine.GetNode(id); owner.FlowStage = "cleanup_started";
            var window = SettlementEntryService.CreateWindow(state, owner, SettlementEntryService.PlayerCleanup, main.PlayerId, "", main.NodeId);
            owner.SettlementWindow = window; state.EffectRuntime.ActiveSettlementWindowId = window.Context.Id;
            var target = Node(state, "cleanup.command.pending", id); target.PlayerId = main.PlayerId;
            SettlementEntryService.RegisterCleanup(state, window, target.EffectId, "ability.cleanup", "ability", "source", "activation",
                policy, Presentation(), policy == SettlementWholeEffectPolicy.Optional ? "ability.available" : "");
            var blocker = new EffectBlockerRuntimeState { BlockerId = "cleanup.command.child", OwnerEffectId = id,
                TargetEffectId = target.EffectId, BlockerKind = "child" };
            state.EffectRuntime.Blockers.Add(blocker); owner.BlockerIds.Add(blocker.BlockerId); owner.Status = EffectNodeStatus.Blocked;
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            new RoundExecutionProjector().Project(state);
            cleanupCommand = new GameCommand { Kind = GameCommandKind.EndAction, PlayerId = main.PlayerId,
                CommandId = "cleanup.command.end", Parameters = new Dictionary<string, string> {
                    { "windowId", window.Context.Id }, { "currentNodeEffectId", "" }, { "interactionId", "" },
                    { "expectedRevision", state.EffectRuntime.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) } } };
            return Tuple.Create(state, registry, rounds);
        }

        [TestCase(4, false)]
        [TestCase(4, true)]
        [TestCase(8, true)]
        public void EndCleanupCommandExecutesSuccessorWithoutAnotherCommand(int round, bool lastPlayer)
        {
            var fixture = CleanupCommandFixture(round, lastPlayer); var state = fixture.Item1;
            string oldMain = state.EffectRuntime.ActiveMainNodeId;
            string nextId = state.EffectRuntime.MainNodes.Single(n => n.NodeId == oldMain).NextNodeId;
            var result = new EndActionCommandHandler(new RoundAdvanceService(), null, fixture.Item2, fixture.Item3).Handle(state, cleanupCommand);
            Assert.That(result.Succeeded, Is.True, result.LogMessage);
            Assert.That(state.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active), state.EffectRuntime.LastFaultMessage);
            var next = state.EffectRuntime.MainNodes.Single(n => n.NodeId == nextId);
            Assert.That(next.ExecutionEffectId, Is.Not.Empty, "真实 EndAction 成功后后继仍为 Ready 且无执行效果。");
            Assert.That(next.Status, Is.EqualTo(EffectNodeStatus.Completed));
            if (round == 8 && lastPlayer) {
                Assert.That(state.Phase, Is.EqualTo(GamePhase.FinalScoring));
                Assert.That(state.EffectRuntime.ActiveMainNodeId, Is.Empty);
                Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            } else {
                Assert.That(state.Round, Is.EqualTo(round + 1));
                Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.True);
            }
            string completed = JsonConvert.SerializeObject(state);
            var duplicate = new EndActionCommandHandler(new RoundAdvanceService(), null, fixture.Item2, fixture.Item3).Handle(state, cleanupCommand);
            Assert.That(duplicate.Succeeded, Is.False);
            Assert.That(duplicate.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.WrongPhase));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(completed), "旧命令不得重复推进。");
        }

        [Test]
        public void EndCleanupCommandKeepsCompatibilityWithoutRoundExecutionService()
        {
            var fixture = CleanupCommandFixture(4, true); var state = fixture.Item1;
            var result = new EndActionCommandHandler(new RoundAdvanceService(), null, fixture.Item2).Handle(state, cleanupCommand);
            Assert.That(result.Succeeded, Is.True, result.LogMessage);
            var main = state.EffectRuntime.MainNodes.Single(n => n.NodeId == state.EffectRuntime.ActiveMainNodeId);
            Assert.That(main.NodeTypeId, Is.EqualTo(RoundMainlineNodeTypeIds.PlayerCleanupWindow));
            Assert.That(main.CompletionRequested, Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == main.ExecutionEffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.Round, Is.EqualTo(4));
        }

        [Test]
        public void EndCleanupCommandRejectsRequiredDutyStaleRevisionAndForeignIdentity()
        {
            foreach (string invalid in new[] { "required", "stale", "foreign", "window", "node" }) {
                var fixture = CleanupCommandFixture(4, true, invalid == "required" ? SettlementWholeEffectPolicy.Required : SettlementWholeEffectPolicy.Optional);
                var state = fixture.Item1;
                if (invalid == "stale") cleanupCommand.Parameters["expectedRevision"] = (state.EffectRuntime.StateRevision - 1).ToString();
                if (invalid == "foreign") cleanupCommand.PlayerId = 1;
                if (invalid == "window") cleanupCommand.Parameters["windowId"] = "old.window";
                if (invalid == "node") cleanupCommand.Parameters["currentNodeEffectId"] = "unrelated.node";
                string before = JsonConvert.SerializeObject(state);
                var result = new EndActionCommandHandler(new RoundAdvanceService(), null, fixture.Item2, fixture.Item3).Handle(state, cleanupCommand);
                Assert.That(result.Succeeded, Is.False, invalid);
                string reason = invalid == "required" ? "settlement_required_pending" : invalid == "stale" ? "state_revision_mismatch" :
                    invalid == "foreign" ? "settlement_permission_denied" : invalid == "window" ? "settlement_window_not_active" : "settlement_end_context_invalid";
                Assert.That(result.Validation.Reason, Is.EqualTo(reason), invalid);
                Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before), invalid);
            }
        }

        [Test]
        public void EndCleanupCommandStartsNextPlayerAndWaitsForTheirRequiredResponse()
        {
            var fixture = CleanupCommandFixture(4, false); var state = fixture.Item1;
            const string type = "fixture.cleanup.next_response";
            fixture.Item2.Register(new EffectRegistration(type, c => c.Node.FlowStage == "" ?
                EffectStepResult.Continue("answer").AddInteraction(new EffectInteractionSpec {
                    InteractionTypeId = type, PromptKey = "core:contract.label", AnswerSchema = "boolean_accept",
                    AnsweringPlayerId = 2, Visibility = "owner", MinSelections = 0, MaxSelections = 0,
                    AllowDecline = false }) : EffectStepResult.Completed(), EffectExecutorKind.IntrinsicFlow));
            fixture.Item2.RegisterEventHandler(new EffectEventHandlerRegistration(type, "PlayerCleanupStarted", "*", type,
                c => c.Event.PlayerId == 2 ? new List<EffectSpec> { EffectSpec.Create(type, playerId: 2) } : new List<EffectSpec>()));
            var handler = new EndActionCommandHandler(new RoundAdvanceService(), null, fixture.Item2, fixture.Item3);
            var result = handler.Handle(state, cleanupCommand);
            Assert.That(result.Succeeded, Is.True, result.LogMessage);
            var active = state.EffectRuntime.MainNodes.Single(n => n.NodeId == state.EffectRuntime.ActiveMainNodeId);
            Assert.That(active.NodeTypeId, Is.EqualTo(RoundMainlineNodeTypeIds.PlayerCleanupWindow));
            Assert.That(active.PlayerId, Is.EqualTo(2)); Assert.That(active.ExecutionEffectId, Is.Not.Empty);
            Assert.That(active.CompletionRequested, Is.False); Assert.That(state.Round, Is.EqualTo(4));
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(request.AnsweringPlayerId, Is.EqualTo(2)); Assert.That(request.AllowDecline, Is.False);
            string before = JsonConvert.SerializeObject(state);
            var duplicate = handler.Handle(state, cleanupCommand);
            Assert.That(duplicate.Succeeded, Is.False);
            Assert.That(duplicate.Validation.Reason, Is.EqualTo("settlement_window_not_active"));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
        }

        [TestCase(true, false)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        public void EndCleanupCommandRespectsCurrentNodeAndRequiredResponse(bool required, bool otherPlayer)
        {
            var fixture = CleanupCommandFixture(4, false, SettlementWholeEffectPolicy.Required); var state = fixture.Item1;
            const string type = "fixture.cleanup.response";
            fixture.Item2.Register(new EffectRegistration(type, c => c.Node.FlowStage == "" ?
                EffectStepResult.Continue("answer").AddInteraction(new EffectInteractionSpec {
                    InteractionTypeId = type, PromptKey = "core:contract.label", AnswerSchema = "boolean_accept",
                    AnsweringPlayerId = otherPlayer ? 2 : 1, Visibility = "owner", MinSelections = 0, MaxSelections = 0,
                    AllowDecline = !required }) : EffectStepResult.Completed(), EffectExecutorKind.IntrinsicFlow));
            state.EffectRuntime.EffectNodes.Single(n => n.EffectId == "cleanup.command.pending").EffectTypeId = type;
            var engine = fixture.Item3.CreateCommandExecutor(state);
            var window = state.EffectRuntime.EffectNodes.Single(n => n.SettlementWindow != null).SettlementWindow;
            Assert.That(engine.TryInvokeCharacterSettlementOption(1, window.Entries.Single().OptionId, "", state.EffectRuntime.StateRevision,
                out _, out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            var handler = new EndActionCommandHandler(new RoundAdvanceService(), null, fixture.Item2, fixture.Item3);
            cleanupCommand.Parameters["expectedRevision"] = state.EffectRuntime.StateRevision.ToString();
            string before = JsonConvert.SerializeObject(state);
            Assert.That(handler.Handle(state, cleanupCommand).Succeeded, Is.False, "省略当前节点不得跳过真实响应。");
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            cleanupCommand.Parameters["currentNodeEffectId"] = request.OwnerEffectId;
            cleanupCommand.Parameters["interactionId"] = request.GetStableInteractionId();
            var result = handler.Handle(state, cleanupCommand);
            if (required) {
                Assert.That(result.Succeeded, Is.False, result.LogMessage);
                Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
                Assert.That(state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open").AnsweringPlayerId, Is.EqualTo(otherPlayer ? 2 : 1));
            } else {
                Assert.That(result.Succeeded, Is.True, result.LogMessage);
                Assert.That(state.Round, Is.EqualTo(5));
                Assert.That(state.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == request.GetStableInteractionId()).Status, Is.EqualTo("answered"));
            }
        }

        private static GameState State() => new GameState { GameId = "settlement.contract",
            Players = new List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } } };
        private static EffectNodeRuntimeState Node(GameState state, string id, string parent = "")
        {
            var node = new EffectNodeRuntimeState { EffectId = id, ParentEffectId = parent, PlayerId = 1,
                EffectTypeId = SettlementEntryService.SegmentTypeId, Status = EffectNodeStatus.Created };
            state.EffectRuntime.EffectNodes.Add(node);
            if (parent.Length > 0) state.EffectRuntime.EffectNodes.Find(n => n.EffectId == parent).ChildEffectIds.Add(id);
            return node;
        }
        private static SettlementPresentationDefinition Presentation() => new SettlementPresentationDefinition {
            Label = new GameplayTextRef("core:contract.label"), Description = new GameplayTextRef("core:contract.description"),
            AriaLabel = new GameplayTextRef("core:contract.label"), ContentMode = "text" };
        private static SettlementWindowRuntimeState Window(GameState state, string kind = SettlementEntryService.PlayerCleanup)
        {
            var owner = Node(state, "owner"); owner.Status = EffectNodeStatus.Blocked;
            return SettlementEntryService.CreateWindow(state, owner, kind, 1, "", "trigger");
        }
        private static SettlementEntryRuntimeState Cleanup(GameState state, SettlementWindowRuntimeState window, string id,
            SettlementWholeEffectPolicy policy)
        {
            Node(state, id, "owner");
            return SettlementEntryService.RegisterCleanup(state, window, id, "ability." + id, "ability", "departed.card", "activation", policy, Presentation(),
                policy == SettlementWholeEffectPolicy.Optional ? "ability.available" : "");
        }
        private static void Select(GameState state, SettlementWindowRuntimeState window, SettlementEntryRuntimeState entry)
        {
            Assert.That(SettlementEntryService.TryPrepareInvocation(state, window, 1, entry.OptionId, "", state.EffectRuntime.StateRevision,
                e => new SettlementAvailabilityResult { Enabled = true }, out bool previous, out var plan, out string reason), Is.True, reason);
            Assert.That(previous, Is.False); Assert.That(plan.Effects, Is.Empty);
            SettlementEntryService.RecordInvocation(window, plan);
        }

        [Test]
        public void OptionalOfferStoresOnlyMetadataAndRequiresQualification()
        {
            var state = State(); var window = Window(state, SettlementEntryService.CharacterUse);
            state.ItemZones.Items.Add(new ItemInstanceState { ItemId = "source", ControllerSubject = ItemSubjectRef.Player(1) });
            var offer = new SettlementOfferDefinition { PlayerId = 1, SourceItemId = "source", WindowId = window.Context.Id,
                OwnerEffectId = "owner", LocalId = "ability.insert", AvailabilityHandlerId = "ability.available", Presentation = Presentation(),
                Effects = new List<EffectSpecRuntimeState> { EffectSpec.Create("probe", playerId: 1).ToPersistentDefinition() } };
            int count = state.EffectRuntime.EffectNodes.Count;
            var entry = SettlementEntryService.RegisterOffer(window, offer);
            Assert.That(state.EffectRuntime.EffectNodes.Count, Is.EqualTo(count)); Assert.That(entry.TargetEffectId, Is.Empty);
            Assert.That(SettlementEntryService.GetOptions(state, window, 1).Single().IsEnabled, Is.False);
            Assert.That(SettlementEntryService.GetOptions(state, window, 2), Is.Empty);
            Assert.That(SettlementEntryService.TryPrepareInvocation(state, window, 1, entry.OptionId, "", 0,
                e => new SettlementAvailabilityResult { Enabled = true }, out _, out var plan, out string reason), Is.True, reason);
            Assert.That(plan.Effects.Count, Is.EqualTo(1)); Assert.That(entry.AcceptedUseCount, Is.Zero);
            Assert.That(state.EffectRuntime.EffectNodes.Count, Is.EqualTo(count));
            Assert.That(SettlementEntryService.RegisterOffer(window, offer).OptionId, Is.EqualTo(entry.OptionId));
            Assert.That(window.Entries.Count, Is.EqualTo(1));
        }

        [Test]
        public void QueryContextUsesCurrentRevisionWithoutChangingStableWindowIdentity()
        {
            var state = State(); var window = Window(state); string id = window.Context.Id;
            state.EffectRuntime.StateRevision = 7;
            var query = SettlementEntryService.CreateQueryContext(state, window);
            Assert.That(query.StateRevision, Is.EqualTo(7)); Assert.That(query.Id, Is.EqualTo(id));
            Assert.That(window.Context.StateRevision, Is.Zero);
        }

        [Test]
        public void CleanupSelectsExistingLaterTargetAndDepartedSourceDoesNotEraseDuty()
        {
            var state = State(); var window = Window(state);
            var optional = Cleanup(state, window, "benefit", SettlementWholeEffectPolicy.Optional);
            var required = Cleanup(state, window, "penalty", SettlementWholeEffectPolicy.Required);
            Assert.That(SettlementEntryService.GetOptions(state, window, 1, e => new SettlementAvailabilityResult { Enabled = true }).All(o => o.IsEnabled), Is.True);
            int count = state.EffectRuntime.EffectNodes.Count; Select(state, window, required);
            Assert.That(state.EffectRuntime.EffectNodes.Count, Is.EqualTo(count));
            Assert.That(SettlementEntryService.IsChildEligible(state.EffectRuntime, window, "penalty"), Is.True);
            Assert.That(SettlementEntryService.IsChildEligible(state.EffectRuntime, window, "benefit"), Is.False);
            Assert.That(optional.AcceptedUseCount, Is.Zero);
            Assert.That(SettlementEntryService.TryPrepareInvocation(state, window, 1, required.OptionId, "", -1, null,
                out bool duplicate, out _, out _), Is.True); Assert.That(duplicate, Is.True);
            Assert.That(required.AcceptedUseCount, Is.EqualTo(1));
        }

        [Test]
        public void RequiredAndUnspecifiedPoliciesBothPreventWholeWindowDecline()
        {
            var state = State(); var window = Window(state);
            var required = Cleanup(state, window, "penalty", SettlementWholeEffectPolicy.Required);
            Cleanup(state, window, "benefit", SettlementWholeEffectPolicy.Optional);
            Assert.That(SettlementEntryService.CanCloseCleanup(window, out string reason), Is.False);
            Assert.That(reason, Is.EqualTo("settlement_required_pending"));
            required.WholeEffectPolicy = SettlementWholeEffectPolicy.Unspecified;
            Assert.That(SettlementEntryService.CanCloseCleanup(window, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("settlement_whole_effect_policy_unresolved"));
            Assert.That(SettlementEntryService.GetOptions(state, window, 1).First().IsEnabled, Is.False);
        }

        [Test]
        public void CompletingRequiredAllowsOneCloseOfAllRemainingOptionalTargets()
        {
            var state = State(); var window = Window(state);
            var required = Cleanup(state, window, "penalty", SettlementWholeEffectPolicy.Required);
            Cleanup(state, window, "benefit.a", SettlementWholeEffectPolicy.Optional);
            Cleanup(state, window, "benefit.b", SettlementWholeEffectPolicy.Optional);
            Select(state, window, required); var target = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == "penalty");
            target.Status = EffectNodeStatus.Completed; SettlementEntryService.ObserveTerminal(window, target);
            Assert.That(SettlementEntryService.TryPrepareEndAction(state, window, 1, "", "", 0, null, out var plan, out string reason), Is.True, reason);
            Assert.That(plan.Kind, Is.EqualTo(SettlementEndActionKind.CloseCleanupWindow));
            Assert.That(SettlementEntryService.TryCloseCleanup(window, out var targets, out reason), Is.True, reason);
            Assert.That(targets, Is.EquivalentTo(new[] { "benefit.a", "benefit.b" }));
            // 元数据关闭后、Host实际终止前不可宣称有效存档。
            Assert.That(SettlementEntryService.TryValidateWindow(state, window, out _), Is.False);
            foreach (var id in targets) state.EffectRuntime.EffectNodes.Find(n => n.EffectId == id).Status = EffectNodeStatus.Completed;
            Assert.That(SettlementEntryService.TryValidateWindow(state, window, out reason), Is.True, reason);
            Assert.That(required.SettlementState, Is.EqualTo("resolved"));
        }

        [Test]
        public void ActiveNodeEndNeverDeclinesSiblingsAndCannotPretendNoCurrentNode()
        {
            var state = State(); var window = Window(state);
            var current = Cleanup(state, window, "benefit", SettlementWholeEffectPolicy.Optional);
            var duty = Cleanup(state, window, "penalty", SettlementWholeEffectPolicy.Required);
            Select(state, window, current); var leaf = Node(state, "benefit.choose", "benefit"); leaf.Status = EffectNodeStatus.Blocked;
            string before = JsonConvert.SerializeObject(state);
            Assert.That(SettlementEntryService.TryPrepareEndAction(state, window, 1, leaf.EffectId, "", 0,
                n => n.EffectId == leaf.EffectId, out var plan, out string reason), Is.True, reason);
            Assert.That(plan.Kind, Is.EqualTo(SettlementEndActionKind.StopCurrentNode)); Assert.That(plan.NodeEffectId, Is.EqualTo(leaf.EffectId));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before)); Assert.That(duty.SettlementState, Is.EqualTo("available"));
            Assert.That(SettlementEntryService.TryPrepareEndAction(state, window, 1, "", "", 0, n => true, out _, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("settlement_current_node_required"));
            Assert.That(SettlementEntryService.TryPrepareEndAction(state, window, 1, "penalty", "", 0, n => true, out _, out _), Is.False);
            Assert.That(SettlementEntryService.TryPrepareEndAction(state, window, 1, "owner", "", 0, n => true, out _, out _), Is.False);
            Assert.That(SettlementEntryService.TryPrepareEndAction(state, window, 1, leaf.EffectId, "", 0, n => false, out _, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("settlement_current_node_stop_forbidden"));
        }

        [Test]
        public void RestoreRejectsEarlyStartedTargetAndDuplicateOption()
        {
            var state = State(); var window = Window(state);
            var entry = Cleanup(state, window, "benefit", SettlementWholeEffectPolicy.Optional);
            Assert.That(SettlementEntryService.TryValidateWindow(state, window, out string reason), Is.True, reason);
            var target = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == "benefit"); target.Status = EffectNodeStatus.Ready;
            Assert.That(SettlementEntryService.TryValidateWindow(state, window, out _), Is.False);
            target.Status = EffectNodeStatus.Created; window.Entries.Add(entry);
            Assert.That(SettlementEntryService.TryValidateWindow(state, window, out _), Is.False);
        }

        [Test]
        public void BoundedAllocationKeepsPerRowAndWholePaymentLimits()
        {
            var ids = new[] { "Originium", "OriginiumShard", "Iron" };
            var limits = new Dictionary<string, int> { { "Originium", 1 }, { "OriginiumShard", 0 }, { "Iron", 2 } };
            Assert.That(ResourceAllocationAnswer.TryParseBounded("Originium=1,Iron=1", ids, 0, 2, limits, out _, out _), Is.True);
            foreach (string invalid in new[] { "Originium=2", "Originium=1,Iron=2", "GoldVoucher=1", "Iron=-1", "Iron=1,Iron=1" })
                Assert.That(ResourceAllocationAnswer.TryParseBounded(invalid, ids, 0, 2, limits, out _, out _), Is.False, invalid);
            Assert.That(ResourceAllocationAnswer.TryParseBounded("", ids, 0, 2, limits, out _, out _), Is.True);
            Assert.That(ResourceAllocationAnswer.TryParse("Iron=1", ids, 2, out _, out _), Is.False);
        }

        private static GameState PaymentState(bool withBuildSupply = false)
        {
            var map = StaticMapDefinitions.CreateThreePlayerMap(); var source = State(); source.MapId = map.MapId;
            source.ContentPackHash = "settlement.fixture"; source.Round = source.ActionRound = 1;
            source.RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = source.ContentPackHash };
            source.FindPlayer(1).Resources = new ResourceSet { Originium = 1, Iron = 1, GoldVoucher = 5 };
            var declaration = new CardZoneDefinition { LocalZoneId = "markers", ZoneTemplateId = "core.zone.card_cube_storage",
                AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" },
                EntryPolicyId = "core.owned_player_marker", ExitPolicyId = "core.card_payment", CapacityPolicy = "unbounded",
                OrderingKind = ZoneOrderingKind.Unordered, VisibilityPolicyId = ItemZoneVisibility.Public,
                CarrierExitPolicyId = "core.return_contents_before_exit", EmptyExitPolicyId = "core.discard_carrier_when_empty",
                PaymentPolicyId = "core.basic_resource_substitution", MaxMarkersPerPayment = 2 };
            var recipes = new List<ItemRecipeEntry> {
                new ItemRecipeEntry { EntryId = "carrier", DefinitionId = "operator.passenger", TypeId = "core.operator_card", PerPlayer = true, Quantity = 1, InitialZoneRole = "persistent" },
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", PerPlayer = true, Quantity = 3, InitialZoneRole = "player_markers" },
                new ItemRecipeEntry { EntryId = "generated.hand", DefinitionId = "fixture.generated.hand", TypeId = "core.generated_character_card", PerPlayer = true, Quantity = 1, InitialZoneRole = "hand" },
                new ItemRecipeEntry { EntryId = "generated.discard", DefinitionId = "fixture.generated.discard", TypeId = "core.generated_character_card", PerPlayer = true, Quantity = 1, InitialZoneRole = "discard" },
                new ItemRecipeEntry { EntryId = "normal.hand", DefinitionId = "liskarm", TypeId = "core.basic_character_card", PerPlayer = true, Quantity = 1, InitialZoneRole = "hand" }
            };
            if (withBuildSupply)
            {
                source.ContentDefinitions = new GameContentDefinitions { Facilities = new List<FacilityCardDefinition> {
                    new FacilityCardDefinition { FacilityId = "fixture.mixed.facility", Color = "blue", ResourceCost = Cost(), GoldVoucherCost = 8, Score = 2 } } };
                recipes.Add(new ItemRecipeEntry { EntryId = "build.supply", DefinitionId = "fixture.mixed.facility", TypeId = "core.facility_card", Quantity = 1, InitialZoneRole = "facility_supply_0" });
                recipes.Add(new ItemRecipeEntry { EntryId = "build.refill", DefinitionId = "fixture.mixed.facility", TypeId = "core.facility_card", Quantity = 1, InitialZoneRole = "facility_deck" });
            }
            var state = new ItemZoneStateFactory().CreateForSetup(source, map, recipes,
                new[] { new ItemCardZoneDeclaration { DefinitionId = "operator.passenger", CardZones = new List<CardZoneDefinition> { declaration } } });
            var card = state.ItemZones.Items.First(i => i.DefinitionId == "operator.passenger" && i.ControllerSubject.PlayerId == 1);
            var zone = state.ItemZones.Zones.Single(z => z.ParentId == card.ItemId);
            foreach (var marker in state.ItemZones.Items.Where(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == 1)) marker.ZoneId = zone.ZoneId;
            state.CharacterUses.Add(new CharacterUseRuntimeState { UseId = "source.use", CardInstanceId = card.ItemId, ExecutingPlayerId = 1,
                Modes = new List<CharacterModeContract> { new CharacterModeContract { SkillType = "persistent", Activated = true, SubscriptionActive = true } } });
            Assert.That(new ItemZoneQuery().TryValidate(state, out string reason), Is.True, reason);
            return state;
        }
        private static string PaymentZone(GameState state) => state.ItemZones.Zones.First(z => z.Configuration?.PaymentPolicyId == "core.basic_resource_substitution" && z.OwnerPlayerId == 1).ZoneId;
        private static ResourceSet Cost() => new ResourceSet { Originium = 2, Iron = 2, GoldVoucher = 3 };
        private static Dictionary<string, int> Allocation() => new Dictionary<string, int> { { "Originium", 1 }, { "Iron", 1 } };

        private static EffectSpec CompositePayment(ResourceSet cost) => EffectSpec.Create(ResourceEffectTypeIds.Pay,
            Object("player", NormalizedValue.CreateString("1"), "cost", NormalizedValue.CreateObject(
                Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>().Select(type => new NormalizedValueEntry {
                    Name = type.ToString(), Value = NormalizedValue.CreateInteger(cost.Get(type)) }).ToList())), 1);

        private static EffectRegistry PaymentRegistry()
        {
            var registry = new EffectRegistry(); ResourceEffectExecutor.Register(registry);
            ResourcePaymentChoiceEffectExecutor.Register(registry); SettlementEntryService.Register(registry);
            ResourcePaymentSubstitutionEffectExecutor.Register(registry); FacilityBuildEffectExecutor.Register(registry);
            registry.RegisterSettlementCatalogProvider("settlement.fixture", (work, window) => {
                if (window.Context.Kind != SettlementEntryService.ResourcePayment) return;
                string zoneId = PaymentZone(work), sourceId = work.ItemZones.Zones.Single(z => z.ZoneId == zoneId).ParentId;
                var entry = SettlementEntryService.RegisterOffer(window, new SettlementOfferDefinition {
                    PlayerId = 1, WindowId = window.Context.Id, OwnerEffectId = window.Context.OwnerEffectId,
                    SourceItemId = sourceId, LocalId = "operator.passenger.tactic.optional_insertion",
                    AvailabilityHandlerId = "fixture.passenger.available", Presentation = Presentation(),
                    Effects = new List<EffectSpecRuntimeState> { ResourcePaymentSubstitutionEffectExecutor.Create(1, window.Context.OwnerEffectId, zoneId, 2).ToPersistentDefinition() }
                }); entry.AbilityId = "operator.passenger.tactic";
            }, (work, window, entry, force) => new SettlementAvailabilityResult { Enabled = true });
            return registry;
        }

        [Test]
        public void FreshOrdinaryPaymentsUseOneConfirmationAndNeverSubstituteWithoutClick()
        {
            foreach (string kind in new[] { "single", "composite", "condition" })
            foreach (bool enoughStock in new[] { false, true })
            {
                var state = PaymentState();
                if (enoughStock) { state.FindPlayer(1).Resources.Originium = 2; state.FindPlayer(1).Resources.Iron = 2; }
                var engine = new EffectTreeExecutor(state, PaymentRegistry());
                var pay = kind == "single" ? ResourceEffectSpecFactory.Pay(1, ResourceType.Originium, 2) : CompositePayment(Cost());
                var spec = kind == "condition" ? EffectSpec.Condition(new[] { pay }, new[] { ResourceEffectSpecFactory.GainScore(1, 1) }) : pay;
                spec.PlayerId = 1; string rootId = engine.CreateRoot(spec);
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
                var owner = engine.GetNode(request.OwnerEffectId); string paymentId = owner.Payment.PaymentId;
                Assert.That(owner.FlowStage, Is.EqualTo(ResourceEffectExecutor.PaymentConfirmationStage));
                Assert.That(request.InteractionTypeId, Is.EqualTo(ResourceEffectExecutor.PaymentConfirmationInteractionTypeId));
                Assert.That(request.AnswerSchema, Is.EqualTo("candidate_id"));
                Assert.That(request.CandidateIds, Is.EqualTo(new[] { ResourceEffectExecutor.PaymentConfirmationCandidateId }));
                Assert.That(Text(request.PromptParameters, "paymentId"), Is.EqualTo(owner.EffectId));
                Assert.That(Value(Value(request.PromptParameters, "cost"), "goldVoucher").IntegerValue, Is.EqualTo(kind == "single" ? 0 : 3));
                Assert.That(owner.SettlementWindow.Entries.Single().AcceptedUseCount, Is.Zero);
                Assert.That(owner.Payment.SelectedSubstitution, Is.Null);
                Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ResourcePaymentSubstitutionEffectExecutor.TypeId), Is.False);
                Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5));
                Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                    NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId), out string reason), Is.True, reason);
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                owner = engine.GetNode(owner.EffectId);
                Assert.That(owner.Status, Is.EqualTo(enoughStock ? EffectNodeStatus.Completed : EffectNodeStatus.Failed));
                Assert.That(owner.Payment.PaymentId, Is.EqualTo(paymentId)); Assert.That(owner.Payment.IsCommitted, Is.EqualTo(enoughStock));
                Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == PaymentZone(state)), Is.EqualTo(3));
                Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(enoughStock ? 0 : 1));
                Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(enoughStock && kind != "single" ? 2 : 5));
                Assert.That(state.FindPlayer(1).Score, Is.EqualTo(enoughStock && kind == "condition" ? 1 : 0));
                if (kind == "condition") Assert.That(engine.GetNode(rootId).Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(state.EffectRuntime.InteractionRequests.Count, Is.EqualTo(1));
            }
            foreach (bool basicFee in new[] { false, true })
            {
                var state = PaymentState();
                if (basicFee) state.CharacterUses.Single().Modes.Single().SubscriptionActive = false;
                var engine = new EffectTreeExecutor(state, PaymentRegistry());
                string id = engine.CreateRoot(ResourceEffectSpecFactory.Pay(1, basicFee ? ResourceType.Originium : ResourceType.GoldVoucher, 1));
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                Assert.That(engine.GetNode(id).Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(engine.GetNode(id).Payment, Is.Null); Assert.That(state.EffectRuntime.InteractionRequests, Is.Empty);
                Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == PaymentZone(state)), Is.EqualTo(3));
            }
        }

        [Test]
        public void FreshConditionAndConstructionCommitMixedPaymentBeforeFollowupActions()
        {
            foreach (bool construction in new[] { false, true })
            {
                var state = PaymentState(construction); var registry = PaymentRegistry();
                ItemTransferEffectExecutor.Register(registry);
                var supply = construction ? state.ItemZones.Items.Single(i => i.ZoneId == FindZone(state, "facility_supply_0").ZoneId) : null;
                var spec = construction ? FacilityBuildEffectSpecFactory.Build(1, supply.ItemId, -1, "resources", false, "fixture.build", consumeMainAction: false, expectedSourceZoneId: supply.ZoneId)
                    : EffectSpec.Condition(new[] { CompositePayment(Cost()) }, new[] { ResourceEffectSpecFactory.GainScore(1, 1) });
                spec.PlayerId = 1; var engine = new EffectTreeExecutor(state, registry); string rootId = engine.CreateRoot(spec);
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                var original = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
                var owner = engine.GetNode(original.OwnerEffectId); string paymentId = owner.Payment.PaymentId;
                Assert.That(owner.EffectTypeId, Is.EqualTo(construction ? FacilityBuildEffectExecutor.PaymentTypeId : ResourceEffectTypeIds.Pay));
                Assert.That(owner.EffectId, Is.Not.EqualTo(rootId));
                Assert.That(original.InteractionTypeId, Is.EqualTo(ResourceEffectExecutor.PaymentConfirmationInteractionTypeId));
                if (construction)
                {
                    Assert.That(state.ItemZones.Items.Single(i => i.ItemId == supply.ItemId).ZoneId, Is.EqualTo(FindZone(state, "facility_supply_0").ZoneId));
                    Assert.That(state.Map.Facilities, Is.Empty);
                }
                Assert.That(engine.TryInvokeCharacterSettlementOption(1, owner.SettlementWindow.Entries.Single().OptionId, "", state.EffectRuntime.StateRevision,
                    out bool already, out string reason), Is.True, reason); Assert.That(already, Is.False);
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                var allocation = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
                Assert.That(allocation.AnswerSchema, Is.EqualTo(ResourcePaymentSubstitutionEffectExecutor.AnswerSchema));
                Assert.That(Text(allocation.PromptParameters, "paymentId"), Is.EqualTo(paymentId));
                Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5));
                Assert.That(engine.TrySubmitInteraction(allocation.GetStableInteractionId(), 1, allocation.StateRevision,
                    NormalizedValue.CreateString("Originium=1,Iron=1"), out reason), Is.True, reason);
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                Assert.That(engine.GetNode(owner.EffectId).Payment.IsCommitted, Is.True);
                Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
                Assert.That(state.FindPlayer(1).Resources.Originium, Is.Zero); Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero);
                Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == PaymentZone(state)), Is.EqualTo(1));
                Assert.That(ResourceEffectExecutor.TryValidatePendingPayments(state, out reason), Is.True, reason);
                if (construction)
                {
                    var placement = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
                    Assert.That(placement.InteractionTypeId, Is.EqualTo(FacilityBuildEffectExecutor.PlacementInteractionType));
                    Assert.That(state.ItemZones.Zones.Single(z => z.ZoneId == state.ItemZones.Items.Single(i => i.ItemId == supply.ItemId).ZoneId).TemplateId, Is.EqualTo("core.zone.effect_staging"));
                    Assert.That(state.ItemZones.Items.Any(i => i.ZoneId == FindZone(state, "facility_supply_0").ZoneId && i.ItemId != supply.ItemId), Is.True);
                    Assert.That(engine.TrySubmitInteraction(placement.GetStableInteractionId(), 1, placement.StateRevision,
                        NormalizedValue.CreateString(placement.CandidateIds[0]), out reason), Is.True, reason);
                    Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                    Assert.That(state.Map.Facilities.Single().ContentInstanceId, Is.EqualTo(supply.ItemId));
                }
                Assert.That(engine.GetNode(rootId).Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(state.FindPlayer(1).Score, Is.EqualTo(construction ? 2 : 1));
                Assert.That(state.EffectRuntime.InteractionRequests.Count(r => r.InteractionTypeId == ResourceEffectExecutor.PaymentConfirmationInteractionTypeId), Is.EqualTo(1));
                Assert.That(state.EffectRuntime.InteractionRequests.Count(r => r.InteractionTypeId == ResourcePaymentSubstitutionEffectExecutor.InteractionTypeId), Is.EqualTo(1));
            }
        }

        [Test]
        public void UninvokedPaymentChoiceCanChangeCostWithoutResettingIdentityOrScope()
        {
            var state = PaymentState(); var engine = new EffectTreeExecutor(state, PaymentRegistry());
            string ownerId = engine.CreateRoot(ResourcePaymentChoiceEffectExecutor.Create(1, new List<ResourceSet> { Cost(), new ResourceSet { GoldVoucher = 4 } }, "fixture.payment"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            var options = ResourcePaymentChoiceEffectExecutor.GetPaymentOptionsForPreview(state, engine.GetNode(ownerId));
            string mixed = options.Single(p => p.Value.Originium == 2).Key, gold = options.Single(p => p.Value.GoldVoucher == 4).Key;
            Assert.That(engine.TryPreparePaymentDraft(1, ownerId, request.GetStableInteractionId(), mixed, state.EffectRuntime.StateRevision, "fixture.draft.1", out string reason), Is.True, reason);
            var owner = engine.GetNode(ownerId); string paymentId = owner.Payment.PaymentId, windowId = owner.SettlementWindow.Context.Id;
            var entry = owner.SettlementWindow.Entries.Single(); string optionId = entry.OptionId; long sequence = entry.RegistrationSequence;
            var candidates = request.CandidateIds.ToArray();
            int staleRevision = state.EffectRuntime.StateRevision;
            Assert.That(engine.TryPreparePaymentDraft(1, ownerId, request.GetStableInteractionId(), gold, state.EffectRuntime.StateRevision, "fixture.draft.2", out reason), Is.True, reason);
            owner = engine.GetNode(ownerId);
            Assert.That(owner.Payment.PaymentCost.GoldVoucher, Is.EqualTo(4)); Assert.That(owner.Payment.PaymentCost.Originium, Is.Zero);
            Assert.That(owner.SettlementWindow.Context.PaymentCost.GoldVoucher, Is.EqualTo(4));
            Assert.That(owner.Payment.PaymentId, Is.EqualTo(paymentId)); Assert.That(owner.SettlementWindow.Context.Id, Is.EqualTo(windowId));
            Assert.That(owner.SettlementWindow.Entries.Single().OptionId, Is.EqualTo(optionId));
            Assert.That(owner.SettlementWindow.Entries.Single().RegistrationSequence, Is.EqualTo(sequence));
            Assert.That(owner.SettlementWindow.Entries.Single().AcceptedUseCount, Is.Zero);
            Assert.That(state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open").CandidateIds, Is.EqualTo(candidates));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5));
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == PaymentZone(state)), Is.EqualTo(3));
            string before = JsonConvert.SerializeObject(state);
            Assert.That(engine.TryPreparePaymentDraft(1, ownerId, request.GetStableInteractionId(), mixed, staleRevision, "fixture.draft.stale", out reason), Is.False);
            Assert.That(reason, Is.EqualTo("state_revision_mismatch")); Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            Assert.That(engine.TryPreparePaymentDraft(1, ownerId, request.GetStableInteractionId(), mixed, state.EffectRuntime.StateRevision, "fixture.draft.3", out reason), Is.True, reason);
            Assert.That(engine.TryInvokeCharacterSettlementOption(1, optionId, "", state.EffectRuntime.StateRevision, out _, out reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            owner = engine.GetNode(ownerId); before = JsonConvert.SerializeObject(state);
            Assert.That(ResourcePaymentChoiceEffectExecutor.TryInitializeSelectedPayment(state, owner, gold, out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            Assert.Throws<InvalidOperationException>(() => ResourcePaymentSubstitutionEffectExecutor.InitializePayment(owner, options[gold]));
            Assert.That(owner.Payment.PaymentId, Is.EqualTo(paymentId)); Assert.That(owner.SettlementWindow.Entries.Single().AcceptedUseCount, Is.EqualTo(1));
            var allocation = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(engine.TryEndSettlementAction(1, windowId, allocation.OwnerEffectId, allocation.GetStableInteractionId(),
                state.EffectRuntime.StateRevision, "fixture.cancel.choice", out reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var resumed = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open"); before = JsonConvert.SerializeObject(state);
            Assert.That(engine.TryPreparePaymentDraft(1, ownerId, resumed.GetStableInteractionId(), gold, state.EffectRuntime.StateRevision,
                "fixture.change.after.cancel", out reason), Is.False);
            Assert.That(reason, Is.EqualTo("payment_draft_already_consumed")); Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            owner = engine.GetNode(ownerId); Assert.That(owner.Payment.PaymentId, Is.EqualTo(paymentId));
            Assert.That(owner.SettlementWindow.Context.Id, Is.EqualTo(windowId)); Assert.That(owner.SettlementWindow.Entries.Single().AcceptedUseCount, Is.EqualTo(1));
            Assert.That(owner.Payment.PaymentCost.GoldVoucher, Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5));
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == PaymentZone(state)), Is.EqualTo(3));
        }

        [Test]
        public void FreshPaymentCancellationRestoresOriginalConfirmationAndKeepsSourceReceipt()
        {
            var state = PaymentState(); state.FindPlayer(1).Resources.Originium = state.FindPlayer(1).Resources.Iron = 2;
            var engine = new EffectTreeExecutor(state, PaymentRegistry()); string ownerId = engine.CreateRoot(CompositePayment(Cost()));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var original = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            var owner = engine.GetNode(ownerId); string paymentId = owner.Payment.PaymentId, windowId = owner.SettlementWindow.Context.Id;
            string optionId = owner.SettlementWindow.Entries.Single().OptionId;
            Assert.That(engine.TryInvokeCharacterSettlementOption(1, optionId, "", state.EffectRuntime.StateRevision, out _, out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var allocation = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(engine.TryEndSettlementAction(1, windowId, allocation.OwnerEffectId, allocation.GetStableInteractionId(),
                state.EffectRuntime.StateRevision, "fixture.cancel.fresh.payment", out reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            owner = engine.GetNode(ownerId); var resumed = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(resumed.InteractionTypeId, Is.EqualTo(ResourceEffectExecutor.PaymentConfirmationInteractionTypeId));
            Assert.That(resumed.GetStableInteractionId(), Is.Not.EqualTo(original.GetStableInteractionId()));
            Assert.That(owner.FlowStage, Is.EqualTo(ResourceEffectExecutor.PaymentConfirmationStage));
            Assert.That(owner.Payment.PaymentId, Is.EqualTo(paymentId)); Assert.That(owner.Payment.SelectedSubstitution, Is.Null);
            Assert.That(owner.Payment.IsCommitted, Is.False); Assert.That(owner.Payment.IsCommitRequested, Is.False);
            Assert.That(owner.SettlementWindow.Context.Id, Is.EqualTo(windowId)); Assert.That(owner.SettlementWindow.Entries.Single().AcceptedUseCount, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5));
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == PaymentZone(state)), Is.EqualTo(3));
            string beforeDuplicate = JsonConvert.SerializeObject(state);
            Assert.That(engine.TryInvokeCharacterSettlementOption(1, optionId, "", state.EffectRuntime.StateRevision, out bool alreadyInvoked, out reason), Is.True, reason);
            Assert.That(alreadyInvoked, Is.True); Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(beforeDuplicate));
            Assert.That(engine.TrySubmitInteraction(resumed.GetStableInteractionId(), 1, resumed.StateRevision,
                NormalizedValue.CreateString(ResourceEffectExecutor.PaymentConfirmationCandidateId), out reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(ownerId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.Zero); Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero);
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == PaymentZone(state)), Is.EqualTo(3));
        }

        [Test]
        public void LegacyInFlightPaymentRetainsOriginalCombinationAndNoTypedConversion()
        {
            var state = PaymentState(); var registry = new EffectRegistry(); string legacyCandidate = "pay|" + PaymentZone(state) + "|1|0|1";
            // 受控旧存档夹具用真实内核保存旧请求，随后换成当前真实付款执行器恢复。
            registry.Register(new EffectRegistration(ResourceEffectTypeIds.Pay, c => {
                Set(c.Node, "paymentPlayerId", NormalizedValue.CreateInteger(1));
                Set(c.Node, "paymentCost", Object("Originium", NormalizedValue.CreateInteger(2), "OriginiumShard", NormalizedValue.CreateInteger(0),
                    "Iron", NormalizedValue.CreateInteger(2), "GoldVoucher", NormalizedValue.CreateInteger(3), "PureOriginium", NormalizedValue.CreateInteger(0)));
                var request = new EffectInteractionSpec { InteractionTypeId = "lua.choice", AnsweringPlayerId = 1, Visibility = "owner",
                    PromptKey = "resource.payment.substitution", AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1 };
                request.CandidateIds.Add(legacyCandidate);
                return EffectStepResult.Continue("choose_substitution").AddInteraction(request);
            }, EffectExecutorKind.IntrinsicFlow, ResourceEffectExecutor.DefinitionVersion));
            var oldEngine = new EffectTreeExecutor(state, registry); string ownerId = oldEngine.CreateRoot(CompositePayment(Cost()));
            Assert.That(oldEngine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            state = GameStateCloneService.DeepClone(state); registry = new EffectRegistry(); ResourceEffectExecutor.Register(registry);
            var engine = new EffectTreeExecutor(state, registry); var owner = engine.GetNode(ownerId);
            var saved = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(ResourceEffectExecutor.TryValidatePendingPayments(state, out string reason), Is.True, reason);
            Assert.That(ResourcePaymentSubstitutionEffectExecutor.TryOpenPaymentWindow(state, owner, Cost(), saved.GetStableInteractionId(), out _, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("legacy_payment_requires_original_executor")); Assert.That(owner.Payment, Is.Null);
            Assert.That(engine.TrySubmitInteraction(saved.GetStableInteractionId(), 1, saved.StateRevision, NormalizedValue.CreateString(legacyCandidate), out reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            owner = engine.GetNode(ownerId); Assert.That(owner.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(owner.FlowStage, Is.EqualTo("finished"));
            Assert.That(owner.Payment, Is.Null); Assert.That(state.EffectRuntime.InteractionRequests.Count, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.InteractionRequests.Single().CandidateIds, Is.EqualTo(new[] { legacyCandidate }));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == PaymentZone(state)), Is.EqualTo(1));
            Assert.That(ResourceEffectExecutor.TryValidatePendingPayments(state, out reason), Is.True, reason);
        }

        [Test]
        public void DraftAndCancelLeaveResourcesAndRealMarkersUntouched()
        {
            var state = PaymentState(); var owner = Node(state, "payment");
            ResourcePaymentSubstitutionEffectExecutor.InitializePayment(owner, Cost());
            string resources = JsonConvert.SerializeObject(state.FindPlayer(1).Resources), items = JsonConvert.SerializeObject(state.ItemZones.Items);
            Assert.That(ResourcePaymentSubstitutionEffectExecutor.TrySetDraft(state, owner, PaymentZone(state), Allocation(), 2, out string reason), Is.True, reason);
            Assert.That(JsonConvert.SerializeObject(state.FindPlayer(1).Resources), Is.EqualTo(resources));
            Assert.That(JsonConvert.SerializeObject(state.ItemZones.Items), Is.EqualTo(items));
            ResourcePaymentSubstitutionEffectExecutor.CancelDraft(owner);
            Assert.That(owner.Payment.SelectedSubstitution, Is.Null); Assert.That(owner.Payment.IsCommitRequested, Is.False);
            Assert.That(JsonConvert.SerializeObject(state.FindPlayer(1).Resources), Is.EqualTo(resources));
            Assert.That(JsonConvert.SerializeObject(state.ItemZones.Items), Is.EqualTo(items));
            Assert.That(ResourcePaymentSubstitutionEffectExecutor.TrySetDraft(state, owner, PaymentZone(state),
                new Dictionary<string, int> { { "GoldVoucher", 1 } }, 2, out _), Is.False);
        }

        private static EffectNodeRuntimeState RunPayment(GameState state)
        {
            var registry = new EffectRegistry(); registry.Register(new EffectRegistration("settlement.fixture.payment", c => {
                ResourcePaymentSubstitutionEffectExecutor.InitializePayment(c.Node, Cost());
                if (!ResourcePaymentSubstitutionEffectExecutor.TrySetDraft(c.State, c.Node, PaymentZone(c.State), Allocation(), 2, out string reason))
                    return EffectStepResult.Failed(reason);
                c.Node.Payment.IsCommitRequested = true;
                return ResourceEffectExecutor.ExecutePayment(c, Cost(), NormalizedValue.CreateString("paid"));
            }, EffectExecutorKind.IntrinsicFlow));
            var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(EffectSpec.Create("settlement.fixture.payment", playerId: 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(engine.RunUntilQuiescent().Steps, Is.Zero);
            return engine.GetNode(id);
        }

        [Test]
        public void MixedPaymentMovesOnlyTwoRealMarkersAndChargesRemainingCostOnce()
        {
            var state = PaymentState(); var node = RunPayment(state);
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(node.Payment.IsCommitted, Is.True);
            Assert.That(node.Payment.IsCommitRequested, Is.False); Assert.That(state.FindPlayer(1).Resources.Originium, Is.Zero);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == PaymentZone(state)), Is.EqualTo(1));
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == FindZone(state, "player_markers", 1).ZoneId), Is.EqualTo(2));
            Assert.That(node.PositionChanges.Count, Is.EqualTo(2));
            Assert.That(ResourcePaymentSubstitutionEffectExecutor.TryValidatePaymentRecords(state, out string reason), Is.True, reason);
        }

        [Test]
        public void FailedRemainingPaymentCannotMoveMarkersOrDeductAnyCost()
        {
            var state = PaymentState(); state.FindPlayer(1).Resources.GoldVoucher = 2;
            string before = JsonConvert.SerializeObject(state.ItemZones.Items); var node = RunPayment(state);
            Assert.That(node.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(node.Payment.IsCommitted, Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2)); Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(1));
            Assert.That(JsonConvert.SerializeObject(state.ItemZones.Items), Is.EqualTo(before)); Assert.That(node.PositionChanges, Is.Empty);
        }

        [Test]
        public void FinalAllocationAnswerCommitsPaymentWithoutASecondConfirmationRequest()
        {
            var state = PaymentState(); var registry = new EffectRegistry();
            SettlementEntryService.Register(registry); ResourcePaymentSubstitutionEffectExecutor.Register(registry);
            registry.Register(new EffectRegistration("settlement.fixture.confirm", c => {
                if (c.Node.FlowStage.Length == 0) {
                    ResourcePaymentSubstitutionEffectExecutor.InitializePayment(c.Node, Cost());
                    return EffectStepResult.Continue("payment_allocating").AddChild(SettlementEntryService.CreateCleanupSegment(1, "allocation",
                        new[] { ResourcePaymentSubstitutionEffectExecutor.Create(1, c.Node.EffectId, PaymentZone(c.State), 2) }));
                }
                return ResourceEffectExecutor.ExecutePayment(c, Cost(), NormalizedValue.CreateString("paid"));
            }, EffectExecutorKind.IntrinsicFlow));
            var engine = new EffectTreeExecutor(state, registry); string owner = engine.CreateRoot(EffectSpec.Create("settlement.fixture.confirm", playerId: 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(request.AnswerSchema, Is.EqualTo("bounded_resource_allocation"));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5));
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateString("Originium=1,Iron=1"), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(owner).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.InteractionRequests.Count, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
        }

        [Test]
        public void LargePaymentFeesKeepSubstitutionRowsWithinTheTwoMarkerBudget()
        {
            var state = PaymentState(); var registry = new EffectRegistry();
            SettlementEntryService.Register(registry); ResourcePaymentSubstitutionEffectExecutor.Register(registry);
            registry.Register(new EffectRegistration("settlement.fixture.large_payment", c => {
                ResourcePaymentSubstitutionEffectExecutor.InitializePayment(c.Node,
                    new ResourceSet { Originium = int.MaxValue, Iron = int.MaxValue, OriginiumShard = int.MaxValue });
                return EffectStepResult.Continue("allocating").AddChild(SettlementEntryService.CreateCleanupSegment(1, "large.allocation",
                    new[] { ResourcePaymentSubstitutionEffectExecutor.Create(1, c.Node.EffectId, PaymentZone(c.State), 2) }));
            }, EffectExecutorKind.IntrinsicFlow));
            var engine = new EffectTreeExecutor(state, registry);
            engine.CreateRoot(EffectSpec.Create("settlement.fixture.large_payment", playerId: 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(Number(request.PromptParameters, "maxAmount"), Is.EqualTo(2));
            var rows = Value(request.PromptParameters, "rowMaximums").Properties.ToDictionary(p => p.Name, p => (int)p.Value.IntegerValue);
            Assert.That(rows.Values.All(v => v == 2), Is.True);
            Assert.That(ResourceAllocationAnswer.TryParseBounded("Originium=2", request.CandidateIds, 0, 2, rows, out _, out string reason), Is.True, reason);
        }

        private static readonly string[] RuleIds = {
            "infected.character.amiya.tactic", "operator.passenger.tactic", "operator.dorothy.tactic", "operator.mudrock.tactic",
            "infected.character.jessica_the_liberated.tactic", "infected.character.swire.tactic", "infected.character.lin.tactic",
            "infected.character.gavial_the_invincible.tactic", "infected.award.permanent_support.cleanup", "infected.character.muelsyse.strategy",
            "operator.w.strategy", "character.liskarm.strategy", "infected.character.hellagur.strategy" };
        private static LuaInvocationResult Availability(string id, string kind, NormalizedValue facts)
        {
            string source = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Assets", "StreamingAssets", "Content", "core", "lua", "settlements", id + ".lua"));
            var definition = new LuaScriptDefinition("content." + id, id, id + ".settlement.availability", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            var context = new LuaInvocationContext("window", "SettlementAvailabilityRequested", "p1", 0, "1.0.0", 2, 0, 0) {
                ResponseKind = "settlementAvailability", EntryPoint = "availability", SettlementFacts = facts,
                SettlementContext = new SettlementWindowContext { Id = "window", Kind = kind, PlayerId = 1, OwnerEffectId = "owner", PaymentId = "payment", PaymentCost = Cost() } };
            return new MoonSharpLuaRuntimeHost().Invoke(definition, context);
        }
        private static NormalizedValue Facts() => Object(
            "source", Object("itemId", NormalizedValue.CreateString("source"), "persistentActive", NormalizedValue.CreateBoolean(true), "markerCount", NormalizedValue.CreateInteger(2)),
            "currentUse", Object("cardInstanceId", NormalizedValue.CreateString("target"), "modeStarted", NormalizedValue.CreateBoolean(false),
                "sourceConsumed", NormalizedValue.CreateBoolean(false), "doubleUseGranted", NormalizedValue.CreateBoolean(false), "hasTwoUsableModes", NormalizedValue.CreateBoolean(true)),
            "payment", Object("canConfirm", NormalizedValue.CreateBoolean(true), "isCommitted", NormalizedValue.CreateBoolean(false),
                "available", Object("originium", NormalizedValue.CreateInteger(2), "goldVoucher", NormalizedValue.CreateInteger(5)),
                "markerSources", NormalizedValue.CreateArray(new List<NormalizedValue>())),
            "binding", Object("active", NormalizedValue.CreateBoolean(true), "goldVoucherCost", NormalizedValue.CreateInteger(2)));

        [Test]
        public void ManualModulesReturnRealDeferredBodiesAndNoOfferForAnotherAbility()
        {
            for (int i = 0; i < 2; i++)
            {
                string id = RuleIds[i]; var facts = Facts();
                facts.Properties.Add(new NormalizedValueEntry { Name = "sourceItems", Value = NormalizedValue.CreateArray(new List<NormalizedValue> {
                    Object("itemId", NormalizedValue.CreateString("source"), "abilityId", NormalizedValue.CreateString(id),
                        "persistentActive", NormalizedValue.CreateBoolean(true), "markerCount", NormalizedValue.CreateInteger(2),
                        "storageZoneId", NormalizedValue.CreateString("markers")) }) });
                string source = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core/lua/settlements", id + ".lua"));
                var definition = new LuaScriptDefinition("content." + id, id, id + ".settlement.offers", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
                var context = new LuaInvocationContext("window", "SettlementAvailabilityContextChanged", "p1", 0, "1.0.0", 1, 0, 0) {
                    ResponseKind = "settlementOffers", EntryPoint = "offers", SettlementFacts = facts,
                    SettlementContext = new SettlementWindowContext { Id = "window", Kind = i == 0 ? SettlementEntryService.CharacterUse : SettlementEntryService.ResourcePayment,
                        PlayerId = 1, OwnerEffectId = "owner", TargetUseId = "use", PaymentId = "owner", PaymentCost = Cost() }
                };
                string before = facts.ToDeterministicString(); var result = new MoonSharpLuaRuntimeHost().Invoke(definition, context);
                Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.Effects, Is.Empty);
                Assert.That(result.SettlementOffers.Count, Is.EqualTo(1)); var body = result.SettlementOffers[0].Effects;
                Assert.That(body.Count, Is.EqualTo(1));
                if (i == 0) {
                    Assert.That(body[0].EffectTypeId, Is.EqualTo(EffectTypeIds.Condition));
                    Assert.That(body[0].NestedEffects[0].EffectTypeId, Is.EqualTo(CharacterLifecycleService.ConsumeSourceTypeId));
                    Assert.That(body[0].NestedEffects[1].EffectTypeId, Is.EqualTo(LuaDomainEffectTypeIds.SetCharacterDoubleUseRule));
                    Assert.That(Text(body[0].NestedEffects[0].NormalizedArguments, "useId"), Is.EqualTo("use"));
                } else {
                    Assert.That(body[0].EffectTypeId, Is.EqualTo(ResourcePaymentSubstitutionEffectExecutor.TypeId));
                    Assert.That(Text(body[0].NormalizedArguments, "paymentOwnerEffectId"), Is.EqualTo("owner"));
                }
                Assert.That(facts.ToDeterministicString(), Is.EqualTo(before));
                facts.Properties.Find(p => p.Name == "sourceItems").Value.Items[0].Properties.Find(p => p.Name == "abilityId").Value = NormalizedValue.CreateString("another.ability");
                result = new MoonSharpLuaRuntimeHost().Invoke(definition, context);
                Assert.That(result.IsSuccess, Is.True, result.Diagnostic); Assert.That(result.SettlementOffers, Is.Empty);
            }
        }

        [Test]
        public void ActualCleanupDeclarationsHaveEightOptionalAndThreeRequiredPolicies()
        {
            int optional = 0, required = 0;
            foreach (string id in RuleIds.Skip(2))
            {
                string source = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core/lua/settlements", id + ".lua"));
                // 只增加夹具入口，通过唯一Host实际解析模块元数据；不执行正文或另建Lua VM。
                source = source.Replace("return rule", "function rule.contractDescriptor(ctx) return {Settlement.WithPresentation(Effect.GainResource({recipient=ctx.playerId,resourceType='originium',amount=1}), {entryKind=rule.entryKind,localId=rule.localId,wholeEffectPolicy=rule.wholeEffectPolicy,sourceItemId='source',abilityId='" + id + "',presentation=rule.presentation})} end\nreturn rule");
                var definition = new LuaScriptDefinition("contract." + id, id, id + ".contract", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
                var result = new MoonSharpLuaRuntimeHost().Invoke(definition,
                    new LuaInvocationContext("cleanup", "PlayerCleanupStarted", "p1", 0, "1.0.0", 1, 0, 0) { EntryPoint = "contractDescriptor" });
                Assert.That(result.IsSuccess, Is.True, id + ":" + result.Diagnostic);
                var declaration = result.Effects.Single().Settlement;
                Assert.That(declaration, Is.Not.Null, id);
                bool negative = id == "operator.w.strategy" || id == "character.liskarm.strategy" || id == "infected.character.hellagur.strategy";
                Assert.That(declaration.WholeEffectPolicy, Is.EqualTo(negative ? SettlementWholeEffectPolicy.Required : SettlementWholeEffectPolicy.Optional), id);
                if (negative) required++; else optional++;
            }
            Assert.That(optional, Is.EqualTo(8)); Assert.That(required, Is.EqualTo(3));
        }

        [Test]
        public void AttachedCleanupKeepsExactTargetAndCannotRegisterAfterActivation()
        {
            var state = State(); var window = Window(state); var owner = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == "owner");
            owner.SettlementWindow = window; var target = Node(state, "event.target", owner.EffectId);
            target.Settlement = new SettlementEffectDefinition { EntryKind = SettlementEntryService.CleanupOrder,
                LocalId = "ability.cleanup", AbilityId = "ability", SourceItemId = "departed", SourceEffectId = "activation",
                WholeEffectPolicy = SettlementWholeEffectPolicy.Optional, Presentation = Presentation() };
            int before = state.EffectRuntime.EffectNodes.Count;
            var entry = SettlementEntryService.RegisterAttachedCleanup(state, owner, target, "ability.available");
            Assert.That(entry.TargetEffectId, Is.EqualTo(target.EffectId)); Assert.That(entry.Effects, Is.Empty);
            Assert.That(state.EffectRuntime.EffectNodes.Count, Is.EqualTo(before));
            Assert.That(SettlementEntryService.GetOptions(state, window, 1, e => new SettlementAvailabilityResult { Enabled = true }).Single().CanDeclineWholeEffect, Is.True);
            var tooEarly = Node(state, "already.started", owner.EffectId);
            tooEarly.Settlement = GameStateCloneService.DeepClone(target.Settlement); tooEarly.Status = EffectNodeStatus.Ready;
            Assert.Throws<ArgumentException>(() => SettlementEntryService.RegisterAttachedCleanup(state, owner, tooEarly, "ability.available"));
        }

        [Test]
        public void NonConstructionChoiceUsesOneOriginalConfirmationAndAtomicSubstitution()
        {
            var state = PaymentState(); var registry = new EffectRegistry(); ResourcePaymentChoiceEffectExecutor.Register(registry);
            var engine = new EffectTreeExecutor(state, registry);
            string ownerId = engine.CreateRoot(ResourcePaymentChoiceEffectExecutor.Create(1,
                new List<ResourceSet> { Cost(), new ResourceSet { GoldVoucher = 4 } }, "fixture.payment"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var owner = engine.GetNode(ownerId); var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            var preview = ResourcePaymentChoiceEffectExecutor.GetPaymentOptionsForPreview(state, owner);
            string option = preview.Single(p => p.Value.Originium == 2).Key;
            Assert.That(ResourcePaymentChoiceEffectExecutor.TryInitializeSelectedPayment(state, owner, option, out string reason), Is.True, reason);
            Assert.That(ResourcePaymentSubstitutionEffectExecutor.TryOpenPaymentWindow(state, owner, Cost(), request.GetStableInteractionId(), out var window, out reason), Is.True, reason);
            Assert.That(window.Context.PaymentCost.GoldVoucher, Is.EqualTo(3));
            Assert.That(ResourcePaymentSubstitutionEffectExecutor.TrySetDraft(state, owner, PaymentZone(state), Allocation(), 2, out reason), Is.True, reason);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5));
            Assert.That(ResourcePaymentChoiceEffectExecutor.TryInitializeSelectedPayment(state, owner, preview.Single(p => p.Value.GoldVoucher == 4).Key, out _), Is.False);
            // 宿主的最终数量确认提交同一付款标志，不能再开一个付款请求。
            owner.Payment.IsCommitRequested = true;
            request.Status = "invalidated";
            foreach (var blocker in state.EffectRuntime.Blockers.Where(b => b.InteractionRequestId == request.GetStableInteractionId())) blocker.IsResolved = true;
            owner.Status = EffectNodeStatus.Ready;
            // 受控夹具设置最终提交标志后运行真实执行器，不计正式UI/命令验收。
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            owner = engine.GetNode(ownerId);
            Assert.That(owner.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(owner.Payment.IsCommitted, Is.True);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == PaymentZone(state)), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.InteractionRequests.Count, Is.EqualTo(1));
        }

        [Test]
        public void PaymentChoicePlainAnswersUseOnlyStockAndCannotBypassConditionCost()
        {
            foreach (bool enoughStock in new[] { false, true })
            {
                var state = PaymentState();
                if (enoughStock) { state.FindPlayer(1).Resources.Originium = 2; state.FindPlayer(1).Resources.Iron = 2; }
                var registry = new EffectRegistry(); ResourcePaymentChoiceEffectExecutor.Register(registry);
                registry.Register("fixture.choice.reward", c => { c.State.FindPlayer(1).Score++; return EffectStepResult.Completed(); });
                var engine = new EffectTreeExecutor(state, registry);
                var root = EffectSpec.Condition(new[] { ResourcePaymentChoiceEffectExecutor.Create(1, new List<ResourceSet> { Cost() }, "fixture.payment") },
                    new[] { EffectSpec.Create("fixture.choice.reward", playerId: 1) }); root.PlayerId = 1;
                string rootId = engine.CreateRoot(root);
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                var request = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
                Assert.That(request.CandidateIds.Count, Is.EqualTo(1));
                var owner = engine.GetNode(request.OwnerEffectId);
                Assert.That(request.CandidateIds, Is.EquivalentTo(ResourcePaymentChoiceEffectExecutor.GetPaymentOptionsForPreview(state, owner).Keys));
                Assert.That(Value(request.PromptParameters, "paymentOptions").Items.Count, Is.EqualTo(1));
                // 未插入时直接沿原最终答案；不能因候选预览用了可支付性就自动消费来源。
                Assert.That(owner.Payment, Is.Null);
                Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                    NormalizedValue.CreateString(request.CandidateIds.Single()), out string reason), Is.True, reason);
                Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
                Assert.That(engine.GetNode(rootId).Status, Is.EqualTo(EffectNodeStatus.Completed));
                Assert.That(engine.GetNode(owner.EffectId).Status, Is.EqualTo(enoughStock ? EffectNodeStatus.Completed : EffectNodeStatus.Failed));
                if (!enoughStock) Assert.That(engine.GetNode(owner.EffectId).FailureReason, Is.EqualTo("insufficient_resource"));
                Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == "fixture.choice.reward"), Is.EqualTo(enoughStock));
                Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == PaymentZone(state)), Is.EqualTo(3));
                Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(enoughStock ? 2 : 5));
                Assert.That(state.FindPlayer(1).Score, Is.EqualTo(enoughStock ? 1 : 0));
                Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ResourcePaymentSubstitutionEffectExecutor.TypeId), Is.False);
                Assert.That(state.EffectRuntime.InteractionRequests.Count, Is.EqualTo(1));
            }
            var invalid = PaymentState(); invalid.FindPlayer(1).Resources.GoldVoucher = 2;
            var invalidRegistry = new EffectRegistry(); ResourcePaymentChoiceEffectExecutor.Register(invalidRegistry);
            var invalidEngine = new EffectTreeExecutor(invalid, invalidRegistry);
            string invalidId = invalidEngine.CreateRoot(ResourcePaymentChoiceEffectExecutor.Create(1, new List<ResourceSet> { Cost() }, "fixture.payment"));
            Assert.That(invalidEngine.RunUntilQuiescent().Faulted, Is.False, invalid.EffectRuntime.LastFaultMessage);
            Assert.That(invalidEngine.GetNode(invalidId).Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(invalidEngine.GetNode(invalidId).FailureReason, Is.EqualTo("insufficient_resource"));
            Assert.That(invalid.EffectRuntime.InteractionRequests, Is.Empty);
            Assert.That(invalid.ItemZones.Items.Count(i => i.ZoneId == PaymentZone(invalid)), Is.EqualTo(3));
        }

        [Test]
        public void PaymentChoiceDraftAndClickedSourceCommitThroughOneFinalAllocationAnswer()
        {
            var state = PaymentState(); var registry = new EffectRegistry();
            ResourcePaymentChoiceEffectExecutor.Register(registry); SettlementEntryService.Register(registry);
            ResourcePaymentSubstitutionEffectExecutor.Register(registry);
            // 受控来源提供者只替代目录装配；正文、Invoke、分配回答和付款均走真实Host执行器。
            registry.RegisterSettlementCatalogProvider("settlement.fixture", (work, window) => {
                if (window.Context.Kind != SettlementEntryService.ResourcePayment) return;
                string zoneId = PaymentZone(work), sourceId = work.ItemZones.Zones.Single(z => z.ZoneId == zoneId).ParentId;
                var entry = SettlementEntryService.RegisterOffer(window, new SettlementOfferDefinition {
                    PlayerId = 1, WindowId = window.Context.Id, OwnerEffectId = window.Context.OwnerEffectId,
                    SourceItemId = sourceId, LocalId = "operator.passenger.tactic.optional_insertion",
                    AvailabilityHandlerId = "fixture.passenger.available", Presentation = Presentation(),
                    Effects = new List<EffectSpecRuntimeState> { ResourcePaymentSubstitutionEffectExecutor.Create(1, window.Context.OwnerEffectId, zoneId, 2).ToPersistentDefinition() }
                }); entry.AbilityId = "operator.passenger.tactic";
            }, (work, window, entry, force) => new SettlementAvailabilityResult { Enabled = true });
            var engine = new EffectTreeExecutor(state, registry);
            string ownerId = engine.CreateRoot(ResourcePaymentChoiceEffectExecutor.Create(1, new List<ResourceSet> { Cost() }, "fixture.payment"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var original = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(original.CandidateIds.Count, Is.EqualTo(1));
            Assert.That(engine.TryPreparePaymentDraft(1, ownerId, original.GetStableInteractionId(), original.CandidateIds.Single(),
                state.EffectRuntime.StateRevision, "fixture.prepare.substitution", out string reason), Is.True, reason);
            var owner = engine.GetNode(ownerId); string paymentId = owner.Payment.PaymentId;
            var entry = owner.SettlementWindow.Entries.Single();
            Assert.That(owner.Payment.IsCommitted, Is.False); Assert.That(owner.Payment.SelectedSubstitution, Is.Null);
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == PaymentZone(state)), Is.EqualTo(3));
            Assert.That(engine.TryInvokeCharacterSettlementOption(1, entry.OptionId, "", state.EffectRuntime.StateRevision,
                out bool alreadyInvoked, out reason), Is.True, reason); Assert.That(alreadyInvoked, Is.False);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            var allocation = state.EffectRuntime.InteractionRequests.Single(r => r.Status == "open");
            Assert.That(allocation.AnswerSchema, Is.EqualTo("bounded_resource_allocation"));
            Assert.That(Text(allocation.PromptParameters, "paymentId"), Is.EqualTo(paymentId));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(5));
            Assert.That(engine.TrySubmitInteraction(allocation.GetStableInteractionId(), 1, allocation.StateRevision,
                NormalizedValue.CreateString("Originium=1,Iron=1"), out reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            owner = engine.GetNode(ownerId);
            Assert.That(owner.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(owner.Payment.PaymentId, Is.EqualTo(paymentId));
            Assert.That(owner.Payment.IsCommitted, Is.True); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.Originium, Is.Zero); Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero);
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == PaymentZone(state)), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.InteractionRequests.Count, Is.EqualTo(2));
            Assert.That(state.EffectRuntime.InteractionRequests.Count(r => r.Status == "answered"), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.InteractionRequests.Single(r => r.GetStableInteractionId() == original.GetStableInteractionId()).Status, Is.EqualTo("invalidated"));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r => r.Status == "open"), Is.False);
            Assert.That(engine.RunUntilQuiescent().Steps, Is.Zero);
        }

        [Test]
        public void CleanupMainlineReturnsOnlyAfterTypedWindowAndRealChildrenAreTerminal()
        {
            var state = State(); state.Phase = GamePhase.Cleanup; state.Round = state.ActionRound = 1;
            state.EffectRuntime.CurrentRoundNumber = 1;
            var registry = new EffectRegistry(); var rounds = new RoundExecutionService(registry);
            var engine = new EffectTreeExecutor(state, registry);
            var main = new MainlineNodeRuntimeState { NodeId = "cleanup.main", NodeTypeId = RoundMainlineNodeTypeIds.PlayerCleanupWindow,
                PlayerId = 1, RoundNumber = 1, Status = EffectNodeStatus.Running };
            state.EffectRuntime.MainNodes.Add(main); state.EffectRuntime.ActiveMainNodeId = main.NodeId;
            string id = engine.CreateRoot(EffectSpec.Create(RoundExecutionService.MainlineEffectTypeId,
                Object("mainNodeId", NormalizedValue.CreateString(main.NodeId)), 1));
            main = state.EffectRuntime.MainNodes.Single(); main.ExecutionEffectId = id;
            main.PublishedEventKeys.Add("PlayerCleanupStarted");
            var owner = engine.GetNode(id); owner.FlowStage = "cleanup_started";
            var window = SettlementEntryService.CreateWindow(state, owner, SettlementEntryService.PlayerCleanup, 1, "", main.NodeId);
            owner.SettlementWindow = window; state.EffectRuntime.ActiveSettlementWindowId = window.Context.Id;
            var target = Node(state, "cleanup.pending", id);
            var entry = SettlementEntryService.RegisterCleanup(state, window, target.EffectId, "ability.cleanup", "ability", "source", "activation",
                SettlementWholeEffectPolicy.Optional, Presentation(), "ability.available");
            var blocker = new EffectBlockerRuntimeState { BlockerId = "cleanup.child.blocker", OwnerEffectId = id,
                TargetEffectId = target.EffectId, BlockerKind = "child" };
            state.EffectRuntime.Blockers.Add(blocker); owner.BlockerIds.Add(blocker.BlockerId); owner.Status = EffectNodeStatus.Blocked;
            Assert.That(SettlementEntryService.HasPendingPlayerSelection(state.EffectRuntime), Is.True);
            var waiting = engine.RunUntilQuiescent();
            Assert.That(waiting.Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(waiting.WaitingForInput, Is.True);
            Assert.That(engine.GetNode(id).Status, Is.Not.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.MainNodes.Single().CompletionRequested, Is.False);
            // 使用真实Host提交，关闭元数据窗口、真实目标终态和活动窗口指针同次落地。
            Assert.That(engine.TryEndSettlementAction(1, window.Context.Id, "", "", state.EffectRuntime.StateRevision,
                "fixture.end.cleanup", out string reason), Is.True, reason);
            window = engine.GetNode(id).SettlementWindow;
            Assert.That(window.IsClosed, Is.True); Assert.That(window.Entries.Single().SettlementState, Is.EqualTo("declined"));
            Assert.That(engine.GetNode(target.EffectId).Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(engine.GetNode(target.EffectId).FailureReason, Is.EqualTo("whole_effect_declined"));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(engine.GetNode(id).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.MainNodes.Single().CompletionRequested, Is.True);
            Assert.That(state.EffectRuntime.ActiveSettlementWindowId, Is.Empty);
            Assert.That(state.EffectRuntime.MainNodes.Single().PublishedEventKeys, Does.Contain("PlayerCleanupCompleted"));
        }

        [Test]
        public void AllThirteenQualificationModulesStayPureAndRejectUnrelatedWindows()
        {
            foreach (string id in RuleIds) {
                string kind = id == RuleIds[0] ? SettlementEntryService.CharacterUse : id == RuleIds[1] ? SettlementEntryService.ResourcePayment : SettlementEntryService.PlayerCleanup;
                var facts = Facts(); string before = facts.ToDeterministicString(); var result = Availability(id, kind, facts);
                Assert.That(result.IsSuccess, Is.True, id + ":" + result.Diagnostic);
                Assert.That(result.SettlementAvailability.Enabled, Is.True, id);
                Assert.That(result.Effects, Is.Empty); Assert.That(result.SettlementOffers, Is.Empty); Assert.That(result.CandidatePatches, Is.Empty);
                Assert.That(facts.ToDeterministicString(), Is.EqualTo(before));
                result = Availability(id, "unrelated", facts); Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
                Assert.That(result.SettlementAvailability.Enabled, Is.False, id);
            }
        }

        [Test]
        public void MudrockQualificationUsesOneLegalMarkerSourceAndLinNeedsNoEligibleOpponent()
        {
            var facts = Facts(); var payment = Value(facts, "payment");
            Value(Value(payment, "available"), "originium").IntegerValue = 0;
            Value(payment, "markerSources").Items.Add(Object("eligible", NormalizedValue.CreateBoolean(true), "markerCount", NormalizedValue.CreateInteger(2)));
            Assert.That(Availability("operator.mudrock.tactic", SettlementEntryService.PlayerCleanup, facts).SettlementAvailability.Enabled, Is.True);
            Value(payment, "markerSources").Items[0] = Object("eligible", NormalizedValue.CreateBoolean(false), "markerCount", NormalizedValue.CreateInteger(2));
            Assert.That(Availability("operator.mudrock.tactic", SettlementEntryService.PlayerCleanup, facts).SettlementAvailability.Enabled, Is.False);
            Assert.That(Availability("infected.character.lin.tactic", SettlementEntryService.PlayerCleanup, Facts()).SettlementAvailability.Enabled, Is.True);
        }

        private static GameState CoverState()
        {
            var pack = ExternalContentPack.Current.CreateActiveSnapshot(new GameRuleConfiguration {
                MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 2,
                EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } });
            var source = State(); source.MapId = StaticMapDefinitions.ThreePlayerMapId; source.Round = source.ActionRound = 1;
            source.Phase = GamePhase.ActionRound1; source.CurrentPlayerId = source.StartPlayerId = 1; source.ContentPackHash = pack.ContentHash;
            source.RuleConfiguration = new GameRuleConfiguration { MapId = source.MapId, PlayerCount = 2, ContentPackHash = pack.ContentHash,
                EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
            source.ContentDefinitions = new GameContentDefinitions { Characters = pack.CreateCharacters().ToList(),
                Facilities = pack.CreateFacilities().ToList(), Events = pack.CreateEvents().ToList(),
                CityStyles = pack.CreateCityStyles().ToList(), SpecialActions = pack.CreateSpecialActions().ToList() };
            var entries = new List<ItemRecipeEntry> {
                new ItemRecipeEntry { EntryId="basic",DefinitionId="liskarm",TypeId="core.basic_character_card",PerPlayer=true,Quantity=1,InitialZoneRole="hand" }
            };
            foreach (string key in new[] { "muelsyse", "hellagur", "flowing_shape_alpha", "flowing_shape_beta", "flowing_shape_gamma" })
            {
                string id="infected.character."+key; var definition=pack.ActiveDefinitions.Single(d=>d.DefinitionId==id);
                entries.Add(new ItemRecipeEntry { EntryId=id,DefinitionId=id,TypeId=definition.ItemTypeId,Quantity=1,
                    InitialZoneRole=key.StartsWith("flowing_shape_",StringComparison.Ordinal)?"flowing_shapes":"operator_deck" });
            }
            return new ItemZoneStateFactory().CreateForSetup(source,StaticMapDefinitions.Resolve(source.MapId),entries);
        }
        private static Tuple<EffectTreeExecutor, InteractionRequest> CoverRequest(GameState state, bool? reusable)
        {
            var pack = ExternalContentPack.Current.CreateActiveSnapshot(state.RuleConfiguration);
            var registry = new EffectRegistry { ContentPackHash=pack.ContentHash };
            ResourceEffectExecutor.Register(registry);GenericLuaEffectExecutor.Register(registry);CharacterCoverEffectExecutor.Register(registry);
            CharacterCardLuaCatalog.Register(registry,pack);
            var engine = new EffectTreeExecutor(state,registry);
            void Run(EffectSpec spec) { engine.CreateRoot(spec);Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage); }
            void Place(string key,int player,string destination)
            {
                var item=state.ItemZones.Items.Single(i=>i.DefinitionId=="infected.character."+key);
                Run(MoveSpec(player,new[]{item.ItemId},item.ZoneId,FindZone(state,destination,player).ZoneId,"unordered",1,"core.character_card_transfer"));
            }
            Place("flowing_shape_alpha",1,"hand");Place("flowing_shape_beta",1,"discard");Place("flowing_shape_gamma",2,"hand");
            string sourceKey=reusable==false?"hellagur":"muelsyse";Place(sourceKey,1,"hand");
            Run(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId,playerId:1));
            InteractionRequest Open() => state.EffectRuntime.InteractionRequests.Last(r=>r.Status=="open");
            void Answer(NormalizedValue answer)
            {
                var request=Open();Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(),1,request.StateRevision,answer,out string reason),Is.True,reason);
                Assert.That(engine.RunUntilQuiescent().Faulted,Is.False,state.EffectRuntime.LastFaultMessage);
            }
            if(reusable!=null)
            {
                var source=state.ItemZones.Items.Single(i=>i.DefinitionId=="infected.character."+sourceKey);
                Answer(NormalizedValue.CreateString(source.ItemId));Run(CharacterLifecycleService.CreateUseSpec(1,source.ItemId,"tactic"));
                if(!reusable.Value)
                {
                    Assert.That(Open().PromptKey,Is.EqualTo("core:interaction.hellagur.discard_optional"));
                    Assert.That(Open().AllowDecline,Is.True);Answer(NormalizedValue.CreateBoolean(false));
                    Assert.That(Open().PromptKey,Is.EqualTo("core:interaction.hellagur.cover"));
                }
            }
            return Tuple.Create(engine,Open());
        }

        [Test]
        public void MuelsyseAdditionalCoverHasOnlyOwnHandGeneratedCardsAndBooleanStop()
        {
            var state = CoverState(); var pair = CoverRequest(state, true); var request = pair.Item2;
            Assert.That(request.AllowDecline, Is.True); Assert.That(request.CandidateIds, Does.Not.Contain("decline"));
            Assert.That(request.CandidateIds.Count, Is.EqualTo(1));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == request.CandidateIds[0]).ZoneId, Is.EqualTo(FindZone(state, "hand", 1).ZoneId));
            string source = state.ItemZones.Items.Single(i=>i.DefinitionId=="infected.character.muelsyse").ItemId;
            string before = JsonConvert.SerializeObject(state.ItemZones.Items.Where(i=>i.ItemId!=source));
            Assert.That(pair.Item1.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision,
                NormalizedValue.CreateBoolean(false), out string reason), Is.True, reason);
            Assert.That(pair.Item1.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(pair.Item1.GetNode(request.OwnerEffectId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(pair.Item1.GetNode(request.OwnerEffectId).NormalizedResult.Kind,Is.EqualTo(NormalizedValueKind.Null));
            Assert.That(JsonConvert.SerializeObject(state.ItemZones.Items.Where(i=>i.ItemId!=source)),Is.EqualTo(before));
            Assert.That(state.CharacterCoverSlots.Any(slot=>slot.Reusable),Is.False);
            Assert.That(state.ItemZones.Items.Single(i=>i.ItemId==source).ZoneId,Is.EqualTo(FindZone(state,"persistent",1).ZoneId));
            Assert.That(state.CharacterUses.Single().CompletedModes,Is.EqualTo(new[]{"tactic"}));
        }

        [Test]
        public void NormalCoverIncludesGeneratedHandAndHellagurIncludesHandAndDiscard()
        {
            var normal = CoverState(); var normalRequest = CoverRequest(normal, null).Item2;
            Assert.That(normalRequest.CandidateIds.Any(id => normal.ItemZones.Items.Single(i => i.ItemId == id).TypeId == "core.generated_character_card"), Is.True);
            Assert.That(normalRequest.CandidateIds.All(id => normal.ItemZones.Items.Single(i => i.ItemId == id).ZoneId == FindZone(normal, "hand", 1).ZoneId), Is.True);
            var additional = CoverState(); var pair = CoverRequest(additional,false);var additionalRequest=pair.Item2;
            Assert.That(additionalRequest.AllowDecline, Is.False);
            Assert.That(additionalRequest.CandidateIds.Any(id => additional.ItemZones.Items.Single(i => i.ItemId == id).ZoneId == FindZone(additional, "discard", 1).ZoneId), Is.True);
            Assert.That(additionalRequest.CandidateIds.Any(id => additional.ItemZones.Items.Single(i => i.ItemId == id).ZoneId == FindZone(additional, "hand", 1).ZoneId), Is.True);
            Assert.That(additionalRequest.MinSelections,Is.EqualTo(1));
            Assert.That(pair.Item1.TrySubmitInteraction(additionalRequest.GetStableInteractionId(),1,additionalRequest.StateRevision,
                NormalizedValue.CreateBoolean(false),out _),Is.False);
            string chosen=additionalRequest.CandidateIds.First(id=>additional.ItemZones.Items.Single(i=>i.ItemId==id).ZoneId==FindZone(additional,"discard",1).ZoneId);
            string source=additional.ItemZones.Items.Single(i=>i.DefinitionId=="infected.character.hellagur").ItemId;
            string originalSlot=additional.CharacterCoverSlots.Single(slot=>slot.CardInstanceId==source).SlotId;
            Assert.That(pair.Item1.TrySubmitInteraction(additionalRequest.GetStableInteractionId(),1,additionalRequest.StateRevision,
                NormalizedValue.CreateString(chosen),out string reason),Is.True,reason);
            Assert.That(pair.Item1.RunUntilQuiescent().Faulted,Is.False,additional.EffectRuntime.LastFaultMessage);
            var committed=additional.CharacterCoverSlots.Single(slot=>slot.CardInstanceId==chosen);
            Assert.That(committed.SlotId,Is.Not.EqualTo(originalSlot));Assert.That(committed.Reusable,Is.False);
            Assert.That(committed.BlockedActionRoundSequence,Is.EqualTo(CharacterLifecycleService.GetActionRoundSequence(additional)));
            Assert.That(CharacterLifecycleService.GetAvailability(additional,1,chosen).CanUse,Is.False);
            Assert.That(additional.EffectRuntime.EffectNodes.Single(n=>n.EffectTypeId==CharacterCoverEffectExecutor.EffectTypeId&&Number(n.NormalizedArguments,"apiVersion")==3).Status,Is.EqualTo(EffectNodeStatus.Completed));
        }
    }
}
