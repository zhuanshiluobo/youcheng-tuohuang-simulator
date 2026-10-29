using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Presentation;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.SpecialActions;
using YC.Domain.Presentation;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 仅隔离规则接缝；不生成界面，不作为正常对局或人工视觉验收。
    public sealed class UnifiedResourcePaymentTests
    {
        private GameState state;
        private EffectRegistry registry;
        private MapQueryService map;
        private CityMovementService movement;
        private IsolatedTestArtifacts artifacts;
        private string privateContentRoot;

        [SetUp]
        public void SetUp()
        {
            state = null; artifacts = IsolatedTestArtifacts.BeginCase();
            string content = artifacts.CopyContent(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"), "R2付款私有输入");
            privateContentRoot = content;
            var pack = ExternalContentPack.Load(content); artifacts.RecordContent("R2实际付款内容", content, pack.ContentHash);
            var configuration = new GameRuleConfiguration { PlayerCount = 3, MapId = StaticMapDefinitions.FourPlayerMapId,
                EnabledExpansionIds = new List<string> { "core", "enterprise" } };
            var catalog = ActiveContentCatalogBuilder.Build(pack, configuration); configuration.ContentPackHash = catalog.ContentPackHash;
            state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, Enumerable.Range(1, 3).Select(id => new PlayerSeat { PlayerId = id }).ToList(),
                configuration, 321, catalog.SetupRecipe, catalog.ContentDefinitions);
            state.Round = 2; state.ActionRound = 1; state.Phase = GamePhase.ActionRound1; state.CurrentPlayerId = 1;
            foreach (var placement in new[] { new { Player = 1, Location = "G-03" }, new { Player = 2, Location = "D-02" }, new { Player = 3, Location = "C-02" } })
            {
                var city = state.ItemZones.Items.Single(item => item.PhysicalKind == ItemPhysicalKind.MobileCity && item.OwnerSubject.PlayerId == placement.Player);
                var location = state.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.map_location" && zone.LocalZoneId == placement.Location);
                Position(city, state.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.city_dock" && zone.ParentId == location.ZoneId).ZoneId);
                state.FindPlayer(placement.Player).Resources = new ResourceSet { Originium = 8, OriginiumShard = 8, Iron = 8, GoldVoucher = 20 };
                state.FindPlayer(placement.Player).RemainingMainActionsThisTurn = 1;
            }
            map = new MapQueryService(StaticMapDefinitions.Resolve(state.MapId));
            var influence = new InfluenceService(map); var costs = new TravelCostService(map);
            movement = new CityMovementService(map, influence, costs);
            registry = new EffectRegistry(); catalog.Register(registry);
            ResourceEffectExecutor.Register(registry); ResourcePaymentChoiceEffectExecutor.Register(registry);
            InfluenceEffectExecutor.Register(registry, influence); ItemTransferEffectExecutor.Register(registry);
            CityMoveEffectExecutor.Register(registry, map, influence, movement, costs, new ResourceTokenService());
            FacilityBuildEffectExecutor.Register(registry);
            FacilityEntryEffectExecutor.Register(registry); DeckShuffleEffectExecutor.Register(registry); RepeatEffectExecutor.Register(registry);
            GenericLuaEffectExecutor.Register(registry); ContentPrimitiveEffectExecutor.Register(registry, map, influence);
            EventCardEffectExecutor.Register(registry, map, influence, new EventDeckService(321), new ResourceTokenService());
            CharacterCoverEffectExecutor.Register(registry); CharacterLifecycleService.Register(registry);
        }

        [TearDown] public void FinishEvidence()
        {
            if (state != null) artifacts.WriteLabeledText("当前真实状态", JsonConvert.SerializeObject(state));
            artifacts.Finish();
        }

        private EffectTreeExecutor Engine() => new EffectTreeExecutor(state, registry);
        private void Drain() => Assert.That(Engine().RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultCode + ":" + state.EffectRuntime.LastFaultMessage);
        private string Run(EffectSpec spec) { string id = Engine().CreateRoot(spec); Drain(); return id; }
        private InteractionRequest Open() => state.EffectRuntime.InteractionRequests.Last(request => request.Status == "open" && !request.IsInternalMainlineInteraction());
        private void Answer(string answer)
        {
            var request = Open();
            Assert.That(Engine().TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision,
                NormalizedValue.CreateString(answer), out string reason), Is.True, reason); Drain();
        }
        private void Position(ItemInstanceState item, string zoneId) => typeof(ItemZoneStateMigrator)
            .GetMethod("SetPosition", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { state, item, zoneId });
        private void Restore()
        {
            var result = artifacts.Recover(state, "R2付款恢复", registry);
            Assert.That(result.Succeeded, Is.True, result.Diagnostic); state = result.State; Drain();
        }
        private void LoadPassenger()
        {
            var card = state.ItemZones.Items.First(item => item.DefinitionId == "operator.passenger");
            card.ControllerSubject = ItemSubjectRef.Player(1); card.OwnerSubject = ItemSubjectRef.Player(1);
            Position(card, FindZone(state, "hand", 1).ZoneId);
            Run(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(card.ItemId);
            Run(CharacterLifecycleService.CreateUseSpec(1, card.ItemId, "tactic")); Answer("3");
            Assert.That(state.ItemZones.Zones.Any(zone => ResourcePaymentSubstitutionEffectExecutor.FindEligibleZone(state, 1, zone.ZoneId) != null), Is.True);
        }
        private int MarkerCount() => state.ItemZones.Zones.Where(zone => ResourcePaymentSubstitutionEffectExecutor.FindEligibleZone(state, 1, zone.ZoneId) != null)
            .Sum(zone => ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state, zone, 1).Count);
        private void Substitute(string ownerId, string allocation)
        {
            var owner = Engine().GetNode(ownerId);
            Assert.That(owner.Payment.PaymentProtocolVersion, Is.EqualTo(2));
            // 当前正式付款只使用来源草稿入口，不要求已被协议2替代的旧结算窗口。
            var input = Open();
            Assert.That(input.OwnerEffectId, Is.EqualTo(ownerId));
            Assert.That(Engine().TryQueryPaymentSources(new PaymentSourceQuery { PlayerId = 1, OwnerEffectId = ownerId,
                InteractionId = input.GetStableInteractionId(), PaymentGeneration = owner.Payment.PaymentGeneration,
                StateRevision = state.EffectRuntime.StateRevision,
                SnapshotId = Text(Value(input.PromptParameters, "paymentSourceQuery"), "snapshotId") }, out var sources, out string queryReason), Is.True, queryReason);
            var row = sources.Sources.Single(value => value.Rule.SourceRef.AbilityId == "operator.passenger.payment");
            var allowed = row.Rule.AllowedResourceTypes.Select(type => type.ToString()).ToArray();
            var maximums = row.Rule.AllowedResourceTypes.ToDictionary(type => type.ToString(), type => row.RowMaximums.Get(type));
            Assert.That(YC.Domain.Interactions.ResourceAllocationAnswer.TryParseBounded(allocation, allowed, 0, row.MaxAmount,
                maximums, out var amounts, out string parseReason), Is.True, parseReason);
            var resources = new ResourceSet();
            foreach (var amount in amounts) resources.Set((ResourceType)Enum.Parse(typeof(ResourceType), amount.Key), amount.Value);
            Assert.That(row.Rule.ItemCost, Is.Not.Null);
            var items = row.AvailableItems.Take(PaymentResources.Total(resources) * row.Rule.ItemCost.CountPerUnit).Select(item => item.ItemId).ToArray();
            Assert.That(items.Length, Is.EqualTo(PaymentResources.Total(resources) * row.Rule.ItemCost.CountPerUnit));
            Assert.That(Engine().TryEditPaymentSource(1, ownerId, input.GetStableInteractionId(), "save_source", row.Rule.SourceRef.SourceKey,
                row.Rule.RuleId, "", owner.Payment.PaymentGeneration, row.DraftRevision, resources, items,
                state.EffectRuntime.StateRevision, Guid.NewGuid().ToString("N"), out string saveReason), Is.True, saveReason);
            Drain();
            Assert.That(Engine().GetNode(ownerId).Payment.SourceDrafts.Single(draft => draft.Rule.RuleId == row.Rule.RuleId).Status, Is.EqualTo("valid"));
        }
        private void AssertPaidTokens(string ownerId, int count)
        {
            var payment = Engine().GetNode(ownerId).Payment;
            var receipt = payment.SourceReceipts.Single(r => r.Rule.SourceRef.AbilityId == "operator.passenger.payment");
            Assert.That(receipt.ProtocolVersion, Is.EqualTo(2)); Assert.That(receipt.ItemIds.Count, Is.EqualTo(count));
            Assert.That(Number(receipt.TokenArguments, "apiVersion"), Is.EqualTo(3));
            Assert.That(Number(receipt.TokenArguments, "requiredCount"), Is.EqualTo(count));
            Assert.That(receipt.TokenTransfer.Request.Transfers.SelectMany(t => t.ItemIds), Is.EquivalentTo(receipt.ItemIds));
            Assert.That(ItemTransferService.TryValidateTokenCommit(state, 1, receipt.TokenArguments, receipt.TokenTransfer, out string reason), Is.True, reason);
            Assert.That(payment.FinalReceipt.CostReceiptPolicyVersion, Is.EqualTo(ResourcePaymentSourceService.TokenCostContractVersion));
        }
        private static ResourceSet Mixed() => new ResourceSet { Originium = 2, OriginiumShard = 2, Iron = 3 };

        [TestCase("continue")]
        [TestCase("decline")]
        public void FixedConditionUsesOneHostAndPreservesDraftUntilOriginalConfirmation(string finalAnswer)
        {
            LoadPassenger(); state.FindPlayer(1).Resources = new ResourceSet { Originium = 2, OriginiumShard = 2, Iron = 2 };
            string conditionId = Run(EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Pay(1, Mixed()) },
                new[] { ResourceEffectSpecFactory.GainScore(1, 7) }, allowDecline: true, decisionPlayerId: 1, ruleCondition: true));
            var initial = Open(); Assert.That(initial.InteractionTypeId, Is.EqualTo("action.decline_effect"));
            string ownerId = initial.OwnerEffectId; string paymentId = Engine().GetNode(ownerId).Payment.PaymentId;
            var sourceZone = state.ItemZones.Zones.First(zone => ResourcePaymentSubstitutionEffectExecutor.FindEligibleZone(state, 1, zone.ZoneId) != null);
            var rule = ResourcePaymentSourceService.Query(state, registry, Engine().GetNode(ownerId), Mixed()).Single(r => r.SourceRef.AbilityId == "operator.passenger.payment");
            string beforeInvalid = JsonConvert.SerializeObject(state);
            Assert.That(ResourcePaymentDraftService.TrySetSourceDraft(state, registry, Engine().GetNode(ownerId), rule.SourceRef.SourceKey, rule.RuleId,
                Engine().GetNode(ownerId).Payment.PaymentGeneration, 0, new ResourceSet { Originium = 1, OriginiumShard = 1, Iron = 1 },
                ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state, sourceZone, 1), "", out _), Is.False, "同笔最多替代2份，不能保存3份草稿");
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(beforeInvalid));
            Assert.That(Text(initial.PromptParameters, "sourceKind"), Is.EqualTo("condition"));
            Assert.That(Flag(initial.PromptParameters, "canPay"), Is.False); Assert.That(Flag(initial.PromptParameters, "canSubstitute"), Is.True);
            int score = state.FindPlayer(1).Score; int actions = state.FindPlayer(1).RemainingMainActionsThisTurn;
            Substitute(ownerId, "Iron=1");
            var owner = Engine().GetNode(ownerId);
            Assert.That(owner.Payment.PaymentId, Is.EqualTo(paymentId)); Assert.That(owner.Payment.IsCommitRequested, Is.False);
            Assert.That(owner.Payment.IsCommitted, Is.False); Assert.That(MarkerCount(), Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(2)); Assert.That(Open().InteractionTypeId, Is.EqualTo("action.decline_effect"));
            Assert.That(Flag(Open().PromptParameters, "canPay"), Is.True);
            Restore(); var oldId = Open().GetStableInteractionId(); int oldRevision = Open().StateRevision;
            Answer(finalAnswer);
            Assert.That(Engine().TrySubmitInteraction(oldId, 1, oldRevision, NormalizedValue.CreateString(finalAnswer), out _), Is.False);
            Drain();
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(finalAnswer == "continue" ? 0 : 2));
            Assert.That(MarkerCount(), Is.EqualTo(finalAnswer == "continue" ? 2 : 3));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + (finalAnswer == "continue" ? 7 : 0)));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(actions));
            Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.SourceEffectId == ownerId && value.EventType == ResourcePaymentPreparationService.BeforeResourcePayment), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(request => request.OwnerEffectId == ownerId && request.InteractionTypeId == ResourceEffectExecutor.PaymentConfirmationInteractionTypeId), Is.False);
            Assert.That(Engine().GetNode(conditionId).Status, Is.EqualTo(finalAnswer == "continue" ? EffectNodeStatus.Completed : EffectNodeStatus.Failed));
            if (finalAnswer == "decline")
            {
                var payment = Engine().GetNode(ownerId).Payment;
                Assert.That(payment.SourceDrafts.Single().Status, Is.EqualTo("withdrawn"));
                Assert.That(payment.SourceDrafts.Single().SelectedItemIds, Is.Empty);
                Assert.That(payment.SourceReceipts, Is.Empty); Assert.That(payment.FinalReceipt, Is.Null);
            }
            else AssertPaidTokens(ownerId, 1);
        }

        [Test]
        public void RightSidePayCannotAcquireConditionStartPermission()
        {
            string condition = Run(EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Gain(1, ResourceType.Iron, 1) },
                new[] { ResourceEffectSpecFactory.Pay(1, ResourceType.Iron, 1) }, ruleCondition: true));
            Assert.That(state.EffectRuntime.RuleEvents.Any(value => value.EventType == ResourcePaymentPreparationService.BeforeResourcePayment), Is.False);
            Answer("continue");
            var pay = state.EffectRuntime.EffectNodes.Single(value => value.ParentEffectId == condition && value.EffectTypeId == ResourceEffectTypeIds.Pay);
            Assert.That(pay.Payment.SourceKind, Is.EqualTo("pay")); Assert.That(pay.Payment.RuleConditionOwnerEffectId, Is.Empty);
            Assert.That(pay.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.InteractionRequests.Count(request => request.OwnerEffectId == condition && request.InteractionTypeId == "action.decline_effect"), Is.EqualTo(1));
        }

        [Test]
        public void InternalConditionAndTwoIndependentCostsKeepTheirOwnSemantics()
        {
            string internalId = Run(EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Pay(1, ResourceType.Iron, 1) },
                new[] { ResourceEffectSpecFactory.GainScore(1, 1) }));
            Assert.That(Engine().GetNode(internalId).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(request => request.InteractionTypeId == "action.decline_effect"), Is.False);
            string rule = Run(EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Pay(1, ResourceType.Iron, 1),
                ResourceEffectSpecFactory.Pay(1, ResourceType.Originium, 1) }, new[] { ResourceEffectSpecFactory.GainScore(1, 1) }, ruleCondition: true));
            int before = state.FindPlayer(1).Resources.Iron; int events = state.EffectRuntime.RuleEvents.Count(value => value.EventType == ResourcePaymentPreparationService.BeforeResourcePayment);
            Assert.That(Open().OwnerEffectId, Is.EqualTo(rule)); Answer("continue");
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(before - 1));
            var payments = state.EffectRuntime.EffectNodes.Where(node => node.ParentEffectId == rule && node.Payment != null).ToList();
            Assert.That(payments.Count, Is.EqualTo(2)); Assert.That(payments.Select(node => node.Payment.PaymentId).Distinct().Count(), Is.EqualTo(2));
            Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.EventType == ResourcePaymentPreparationService.BeforeResourcePayment), Is.EqualTo(events + 2));
        }

        [Test]
        public void PaymentChoicePublishesBeforeEventWhileKeepingOriginalFinalHost()
        {
            LoadPassenger(); state.FindPlayer(1).Resources = new ResourceSet { Originium = 2, OriginiumShard = 2, Iron = 2 };
            string ownerId = Run(ResourcePaymentChoiceEffectExecutor.Create(1, new[] { Mixed() }, "resource.payment.choice"));
            var request = Open(); string selected = request.CandidateIds.Single();
            Assert.That(Engine().TryPreparePaymentDraft(1, ownerId, request.GetStableInteractionId(), selected,
                state.EffectRuntime.StateRevision, "choice.prepare", out string reason), Is.True, reason); Drain();
            Assert.That(Open().InteractionTypeId, Is.EqualTo(ResourcePaymentChoiceEffectExecutor.InteractionTypeId));
            Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.SourceEffectId == ownerId && value.EventType == ResourcePaymentPreparationService.BeforeResourcePayment), Is.EqualTo(1));
            Substitute(ownerId, "Iron=1"); Assert.That(Engine().GetNode(ownerId).Payment.IsCommitRequested, Is.False);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(2)); Answer(selected);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero); Assert.That(MarkerCount(), Is.EqualTo(2));
            AssertPaidTokens(ownerId, 1);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(value => value.OwnerEffectId == ownerId && value.InteractionTypeId == ResourceEffectExecutor.PaymentConfirmationInteractionTypeId), Is.False);
        }

        private void OpenMainWindow()
        {
            var builder = new RoundMainChainBuilder(); var order = new List<int> { 1, 2, 3 };
            var chain = builder.Build(state.GameId, builder.CreateRoundExecutionId(state.GameId, 2, order), 2, order, 1);
            var window = chain.Nodes.Single(node => node.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && node.ActionRound == 2 && node.PlayerId == 1);
            foreach (var node in chain.Nodes.Where(node => node.MainlineIndex < window.MainlineIndex)) node.Status = EffectNodeStatus.Completed;
            var runtime = state.EffectRuntime; runtime.CurrentRoundExecutionId = chain.RoundExecutionId; runtime.CurrentRoundNumber = 2;
            runtime.CurrentRoundStartPlayerId = 1; runtime.PlayerOrderSnapshot = order; runtime.FirstMainNodeId = chain.FirstNodeId;
            runtime.MainNodes.AddRange(chain.Nodes); runtime.ActiveMainNodeId = window.NodeId; window.Status = EffectNodeStatus.Ready;
            var result = new RoundExecutionService(registry).Advance(state); Assert.That(result.IsValid, Is.True, result.Reason);
        }
        private string Prepare(string kind, string target, NormalizedValue arguments)
        {
            Assert.That(Engine().TryPrepareActionPayment(1, kind, target, arguments, state.EffectRuntime.StateRevision, Guid.NewGuid().ToString("N"),
                out string preparationId, out string paymentId, out _, out string reason), Is.True, reason);
            Assert.That(paymentId, Is.EqualTo(preparationId)); return preparationId;
        }

        [Test]
        public void EmptyMovePreparationIsProjectedAndCanCancelWithoutStartingAction()
        {
            OpenMainWindow(); int shards = state.FindPlayer(1).Resources.OriginiumShard;
            string prepared = Prepare("city_move", "", Object());
            var projected = GameStateViewProjector.ProjectForPlayer(state, 1, registry).PreparedActionPayments.Single();
            Assert.That(projected.PreparationOwnerEffectId, Is.EqualTo(prepared)); Assert.That(projected.IsReady, Is.False);
            Assert.That(projected.CanCancel, Is.True); Assert.That(projected.InteractionId, Is.Empty);
            Assert.That(projected.PaymentMode, Is.Empty);
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 2, registry).PreparedActionPayments, Is.Empty);
            Assert.That(state.EffectRuntime.EffectNodes.Any(node => node.EffectTypeId == CityMoveEffectTypeIds.Move), Is.False);
            Restore();
            Assert.That(Engine().TryCancelPreparedActionPayment(1, prepared, state.EffectRuntime.StateRevision, "move.cancel", out string reason), Is.True, reason);
            Drain(); Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(shards));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1, registry).PreparedActionPayments, Is.Empty);
            Restore(); Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1, registry).PreparedActionPayments, Is.Empty);
            Assert.That(Engine().TryCreatePreparedPlayerActionEffect(1, prepared, CityMoveEffectSpecFactory.Move(1, "G-02"), "", "move.old", out _, out _), Is.False);
        }

        [Test]
        public void MoveUsesOriginalFinalCommandAndDispatchesEachBeforeEventOnce()
        {
            OpenMainWindow();
            string empty = Prepare("city_move", "", Object());
            string target = new CityMoveCandidateQueryService(map, movement, new TravelCostService(map)).Query(state, 1).First(value => value.IsLegal).TargetLocationId;
            string prepared = Prepare("city_move", target, Object()); Assert.That(prepared, Is.EqualTo(empty));
            var payment = Engine().GetNode(prepared).Payment; int shards = state.FindPlayer(1).Resources.OriginiumShard;
            Assert.That(payment.Preparation.BusinessBeforeReady, Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Any(node => node.EffectTypeId == CityMoveEffectTypeIds.Move), Is.False);
            var before = state.EffectRuntime.RuleEvents.Where(value => value.SourceEffectId == prepared).ToList();
            Assert.That(before.FindIndex(value => value.EventType == CityMoveEventTypeIds.BeforeCityMove), Is.LessThan(before.FindIndex(value => value.EventType == ResourcePaymentPreparationService.BeforeResourcePayment)));
            Restore();
            var command = new GameCommand { CommandId = "move.final", PlayerId = 1, Kind = GameCommandKind.MoveCity, TargetId = target };
            command.Parameters["paymentPreparationOwnerEffectId"] = prepared;
            var result = new MoveCityCommandHandler(movement, new RoundAdvanceService(), registry).Handle(state, command);
            Assert.That(result.Succeeded, Is.True, result.LogMessage + ":" + result.Validation.Reason);
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo(target));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(shards - payment.PaymentCost.OriginiumShard));
            Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.EventType == CityMoveEventTypeIds.BeforeCityMove), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.SourceEffectId == prepared && value.EventType == ResourcePaymentPreparationService.BeforeResourcePayment), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Any(value => value.EventType == CityMoveEventTypeIds.CityMoveCompleted), Is.True);
            Assert.That(Engine().TryCreatePreparedPlayerActionEffect(1, prepared, CityMoveEffectSpecFactory.Move(1, target), "", "move.retry", out _, out _), Is.False);
        }

        [Test]
        public void MatchSaveRestoresPreparedPaymentAndFinalActionWithoutDoubleCharge()
        {
            state.RuleConfiguration.PlayerStageSchedulingMode = PlayerStageSchedulingMode.Sequential;
            foreach (var player in state.Players) player.Color = YC.Infrastructure.Multiplayer.RoomConfigurationService.GetSeatColor(player.PlayerId - 1);
            OpenMainWindow();
            string empty = Prepare("city_move", "", Object());
            string target = new CityMoveCandidateQueryService(map, movement, new TravelCostService(map)).Query(state, 1).First(value => value.IsLegal).TargetLocationId;
            string prepared = Prepare("city_move", target, Object()); Assert.That(prepared, Is.EqualTo(empty));
            var payment = Engine().GetNode(prepared).Payment; int shards = state.FindPlayer(1).Resources.OriginiumShard;
            Assert.That(payment.Preparation.BusinessBeforeReady, Is.True);
            Assert.That(state.EffectRuntime.EffectNodes.Any(node => node.EffectTypeId == CityMoveEffectTypeIds.Move), Is.False);
            var before = state.EffectRuntime.RuleEvents.Where(value => value.SourceEffectId == prepared).ToList();
            Assert.That(before.FindIndex(value => value.EventType == CityMoveEventTypeIds.BeforeCityMove), Is.LessThan(before.FindIndex(value => value.EventType == ResourcePaymentPreparationService.BeforeResourcePayment)));
            var save = MatchSaveResumeTests.FromSession(new GameSession(state), LaunchMode.Local, 1);
            save.Validate();
            var restored = MatchSaveResumeTests.Restore(save);
            Assert.That(RuleStateDigestService.Compute(restored.State), Is.EqualTo(RuleStateDigestService.Compute(state)));
            state = restored.State;
            var command = new GameCommand { CommandId = "move.final", PlayerId = 1, Kind = GameCommandKind.MoveCity, TargetId = target };
            command.Parameters["paymentPreparationOwnerEffectId"] = prepared;
            var result = new MoveCityCommandHandler(movement, new RoundAdvanceService(), registry).Handle(state, command);
            Assert.That(result.Succeeded, Is.True, result.LogMessage + ":" + result.Validation.Reason);
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo(target));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(shards - payment.PaymentCost.OriginiumShard));
            Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.EventType == CityMoveEventTypeIds.BeforeCityMove), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.SourceEffectId == prepared && value.EventType == ResourcePaymentPreparationService.BeforeResourcePayment), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Any(value => value.EventType == CityMoveEventTypeIds.CityMoveCompleted), Is.True);
            Assert.That(Engine().TryCreatePreparedPlayerActionEffect(1, prepared, CityMoveEffectSpecFactory.Move(1, target), "", "move.retry", out _, out _), Is.False);
        }

        [Test]
        public void BuildPreparationCanSwitchAndFinalBuildKeepsSamePayment()
        {
            LoadPassenger();
            for (int index = 0; index < 6; index++)
            {
                string zoneId = FindZone(state, "facility_supply_" + index).ZoneId;
                if (!state.ItemZones.Items.Any(item => item.ZoneId == zoneId)) Run(RefillSpec(1, zoneId));
            }
            OpenMainWindow();
            var item = state.ItemZones.Items.First(value => state.ItemZones.Zones.Any(zone => zone.ZoneId == value.ZoneId && zone.TemplateId == "core.zone.facility_supply_slot"));
            var arguments = Object("paymentMode", NormalizedValue.CreateString("resources"), "expectedSourceZoneId", NormalizedValue.CreateString(item.ZoneId));
            string first = Prepare("build", item.ItemId, arguments);
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1, registry).PreparedActionPayments.Single().PaymentMode, Is.EqualTo("resources"));
            Assert.That(state.Map.Facilities.Any(value => value.ContentInstanceId == item.ItemId), Is.False);
            Assert.That(Engine().TryCancelPreparedActionPayment(1, first, state.EffectRuntime.StateRevision - 1, "build.stale", out _), Is.False);
            Assert.That(Engine().TryCancelPreparedActionPayment(1, first, state.EffectRuntime.StateRevision, "build.cancel", out string reason), Is.True, reason); Drain();
            var goldArguments = Object("paymentMode", NormalizedValue.CreateString("gold"), "expectedSourceZoneId", NormalizedValue.CreateString(item.ZoneId));
            string prepared = Prepare("build", item.ItemId, goldArguments); Assert.That(prepared, Is.Not.EqualTo(first));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1, registry).PreparedActionPayments.Single().PaymentMode, Is.EqualTo("gold"));
            var payment = Engine().GetNode(prepared).Payment;
            Assert.That(Engine().TryCreatePreparedPlayerActionEffect(1, prepared,
                FacilityBuildEffectSpecFactory.Build(1, item.ItemId, 0, "gold", false, "build.final", true, item.ZoneId), "", "build.final", out string action, out reason), Is.True, reason);
            Drain(); Assert.That(Engine().GetNode(prepared).Payment.PaymentId, Is.EqualTo(payment.PaymentId));
            Assert.That(Engine().GetNode(prepared).Payment.IsCommitted, Is.True);
            Assert.That(state.Map.Facilities.Any(value => value.ContentInstanceId == item.ItemId), Is.True);
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
            Assert.That(state.EffectRuntime.EffectNodes.Count(value => value.EffectTypeId == FacilityBuildEffectExecutor.PaymentTypeId && value.ParentEffectId == action), Is.Zero);
        }

        [Test]
        public void DamagedPreparationGenerationIsRejectedByFormalRecovery()
        {
            OpenMainWindow(); string prepared = Prepare("city_move", "", Object());
            var archive = JObject.Parse(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)));
            // 不猜序列化封套结构，定向找本笔原始付款记录。
            var payment = archive.Descendants().OfType<JObject>().Single(value => (string)value["PaymentId"] == prepared);
            payment.Remove("PaymentGeneration");
            var recovered = HostRecoveryService.RecoverJson(archive.ToString(), registry);
            Assert.That(recovered.Succeeded, Is.False); Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
        }

        [Test]
        public void RequiredBeforeResponseKeepsPreparationBusyUntilItFinishes()
        {
            registry.Register(new EffectRegistration("fixture.payment.response", context => context.Node.FlowStage.Length > 0 ? EffectStepResult.Completed() :
                EffectStepResult.Continue("waiting").AddInteraction(new EffectInteractionSpec { InteractionTypeId = "fixture.payment.response",
                    AnsweringPlayerId = 1, AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { "finish" } }), EffectExecutorKind.IntrinsicFlow));
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("fixture.payment.before", ResourcePaymentPreparationService.BeforeResourcePayment,
                "*", "fixture.payment.before", context => new[] { EffectSpec.Create("fixture.payment.response", playerId: 1) }, EffectHandlerRole.Observer));
            OpenMainWindow();
            string target = new CityMoveCandidateQueryService(map, movement, new TravelCostService(map)).Query(state, 1).First(value => value.IsLegal).TargetLocationId;
            string prepared = Prepare("city_move", target, Object());
            var projection = GameStateViewProjector.ProjectForPlayer(state, 1, registry).PreparedActionPayments.Single();
            Assert.That(projection.IsReady, Is.False); Assert.That(projection.CanCancel, Is.False); Assert.That(projection.InteractionId, Is.Empty);
            Assert.That(Engine().TryCancelPreparedActionPayment(1, prepared, state.EffectRuntime.StateRevision, "busy.cancel", out string reason), Is.False);
            Assert.That(reason, Is.EqualTo("settlement_required_pending"));
            int shards = state.FindPlayer(1).Resources.OriginiumShard; Answer("finish");
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1, registry).PreparedActionPayments.Single().IsReady, Is.True);
            Assert.That(Engine().TryCancelPreparedActionPayment(1, prepared, state.EffectRuntime.StateRevision, "finished.cancel", out reason), Is.True, reason);
            Drain(); Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(shards));
            Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.EventType == ResourcePaymentPreparationService.BeforeResourcePayment), Is.EqualTo(1));
        }

        [Test]
        public void BuildCanUseSubstitutionWhenOriginalCostIsUnaffordableAndStillWaitsForOriginalButton()
        {
            LoadPassenger(); OpenMainWindow();
            var item = state.ItemZones.Items.First(value => state.ItemZones.Zones.Any(zone => zone.ZoneId == value.ZoneId && zone.TemplateId == "core.zone.facility_supply_slot") &&
                ResourcePaymentSubstitutionEffectExecutor.BasicTypes.Sum(type => FacilityCardDatabase.Get(state, value.DefinitionId).ResourceCost.Get(type)) > 0);
            var cost = new FacilityBuildCostService().GetEffectiveResourceCost(state, state.FindPlayer(1), FacilityCardDatabase.Get(state, item.DefinitionId));
            var type = ResourcePaymentSubstitutionEffectExecutor.BasicTypes.First(value => cost.Get(value) > 0);
            state.FindPlayer(1).Resources = cost.Clone(); state.FindPlayer(1).Resources.Set(type, cost.Get(type) - 1);
            var arguments = Object("paymentMode", NormalizedValue.CreateString("resources"), "expectedSourceZoneId", NormalizedValue.CreateString(item.ZoneId));
            string prepared = Prepare("build", item.ItemId, arguments);
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1, registry).PreparedActionPayments.Single().CanConfirm, Is.False);
            int resources = state.FindPlayer(1).Resources.Get(type); Substitute(prepared, type + "=1");
            Assert.That(Engine().GetNode(prepared).Payment.IsCommitRequested, Is.False); Assert.That(MarkerCount(), Is.EqualTo(3));
            Assert.That(state.FindPlayer(1).Resources.Get(type), Is.EqualTo(resources));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1, registry).PreparedActionPayments.Single().CanConfirm, Is.True);
            Assert.That(Engine().TrySubmitInteraction(Open().GetStableInteractionId(), 1, Open().StateRevision, NormalizedValue.CreateString("pay"), out _), Is.False);
            var command = new GameCommand { CommandId = "build.button", Kind = GameCommandKind.BuildFacility, PlayerId = 1, TargetId = item.ItemId };
            command.Parameters["paymentMode"] = "resources"; command.Parameters["expectedSourceZoneId"] = item.ZoneId;
            command.Parameters["paymentPreparationOwnerEffectId"] = prepared;
            var result = new BuildFacilityCommandHandler(new BuildFacilityService(), new RoundAdvanceService(), registry).Handle(state, command);
            Assert.That(result.Succeeded, Is.True, result.LogMessage);
            Assert.That(Open().InteractionTypeId, Is.EqualTo(FacilityBuildEffectExecutor.PlacementInteractionType));
            var placementCandidates = registry.FacilityBuildQueries.ResolveSlots(state, 1, item.ItemId, Open().OwnerEffectId)
                .Select(candidate => candidate.SlotIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.That(placementCandidates.Length, Is.GreaterThanOrEqualTo(10), "本夹具须实际覆盖原完整观察超过128元素的候选量。");
            Assert.That(Open().CandidateIds, Is.EquivalentTo(placementCandidates), "全部正式合法槽位必须保留，不能截断候选迎合预算。");
            Assert.That(Open().PromptParameters.TryValidate(out string promptReason), Is.True, promptReason);
            Assert.That(Engine().GetNode(Open().OwnerEffectId).ContinuationData.TryValidate(out string continuationReason), Is.True, continuationReason);
            string placementId = Open().GetStableInteractionId();
            Restore();
            Assert.That(Open().GetStableInteractionId(), Is.EqualTo(placementId));
            Assert.That(Open().CandidateIds, Is.EquivalentTo(placementCandidates));
            Assert.That(MarkerCount(), Is.EqualTo(2)); Assert.That(state.FindPlayer(1).Resources.Get(type), Is.Zero);
            AssertPaidTokens(prepared, 1);
            Assert.That(Engine().GetNode(prepared).Payment.IsCommitted, Is.True); Answer("0");
            Assert.That(state.Map.Facilities.Any(value => value.ContentInstanceId == item.ItemId), Is.True);
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
        }

        [Test]
        public void OrdinaryPaymentBeforeEventIsRealAndRestoredWithoutRedispatch()
        {
            LoadPassenger(); string ownerId = Run(ResourceEffectSpecFactory.Pay(1, new ResourceSet { Iron = 1 }));
            Assert.That(Open().InteractionTypeId, Is.EqualTo(ResourceEffectExecutor.PaymentConfirmationInteractionTypeId));
            Assert.That(Open().AllowDecline, Is.False);
            string eventId = Engine().GetNode(ownerId).Payment.BeforePaymentEventId;
            var ruleEvent = state.EffectRuntime.RuleEvents.Single(value => value.EventId == eventId);
            Assert.That(Text(ruleEvent.Payload, "paymentId"), Is.EqualTo(ownerId));
            Assert.That(Text(ruleEvent.Payload, "sourceEffectId"), Is.EqualTo(ruleEvent.SourceEffectId));
            Assert.That(Value(ruleEvent.Payload, "payer").ReferenceType, Is.EqualTo("player"));
            Restore(); int balance = state.FindPlayer(1).Resources.Iron; Answer("pay");
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(balance - 1));
            Restore(); Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.EventId == eventId), Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(balance - 1));
        }

        [Test]
        public void AmiyaSourceConsumptionConditionRemainsTechnicalInActualModule()
        {
            const string ability = "infected.character.amiya.tactic";
            string source = File.ReadAllText(Path.Combine(privateContentRoot, "lua/settlements/" + ability + ".lua"));
            var script = new LuaScriptDefinition("content." + ability, ability, ability + ".settlement.offers", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            var context = new LuaInvocationContext("window", "SettlementAvailabilityContextChanged", "p1", 0, "1.0.0", 1, 0, 0) {
                ResponseKind = "settlementOffers", EntryPoint = "offers",
                SettlementContext = new SettlementWindowContext { Id = "window", Kind = SettlementEntryService.CharacterUse,
                    PlayerId = 1, OwnerEffectId = "owner", TargetUseId = "use" },
                SettlementFacts = Object("sourceItems", NormalizedValue.CreateArray(new[] {
                    Object("itemId", NormalizedValue.CreateString("source"), "abilityId", NormalizedValue.CreateString(ability),
                        "persistentActive", NormalizedValue.CreateBoolean(true)) }),
                    "currentUse", Object("cardInstanceId", NormalizedValue.CreateString("target"), "modeStarted", NormalizedValue.CreateBoolean(false),
                        "sourceConsumed", NormalizedValue.CreateBoolean(false), "doubleUseGranted", NormalizedValue.CreateBoolean(false),
                        "hasTwoUsableModes", NormalizedValue.CreateBoolean(true))) };
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(script, context);
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            var condition = invocation.SettlementOffers.Single().Effects.Single();
            Assert.That(condition.EffectTypeId, Is.EqualTo(EffectTypeIds.Condition));
            Assert.That(Flag(condition.NormalizedArguments, "ruleCondition"), Is.False);
        }

        [Test]
        public void RusthammerGeneratedPaymentIsOneDeclaredRuleCondition()
        {
            var definition = EventCardLuaCatalog.CreateDefinition(EventCardDatabase.Get(state, "event_yellow_04"));
            var context = new LuaInvocationContext("rusthammer", "EventCardResolved", "p1", 0, definition.DefinitionVersion, 2, 0, 0);
            context.FacadeSnapshot.EventPayload = LuaSnapshotValue.Object(new[] {
                        new LuaSnapshotEntry("optionIndex", LuaSnapshotValue.Integer(0)),
                        new LuaSnapshotEntry("primaryInfluenceSlotId", LuaSnapshotValue.String("")),
                        new LuaSnapshotEntry("selectedInfluenceSlotIds", LuaSnapshotValue.Array(new[] { LuaSnapshotValue.String(InfluenceService.GetLocationSlotId("G-02", 0)) })) });
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(definition, context);
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            var condition = invocation.Effects.Single();
            Assert.That(condition.EffectTypeId, Is.EqualTo(EffectTypeIds.Condition));
            Assert.That(Flag(condition.RawArguments, "ruleCondition"), Is.True);
            Assert.That(condition.NestedEffectGroups[0].Count, Is.EqualTo(1));
            Assert.That(condition.NestedEffectGroups[0][0].EffectTypeId, Is.EqualTo(ResourceEffectTypeIds.Pay));
            Assert.That(condition.NestedEffectGroups[1].Any(effect => effect.EffectTypeId == EffectTypeIds.PlaceInfluence), Is.True);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void RusthammerConfirmsBeforeInfluenceTargetAndRecoversBothStages(bool pay)
        {
            // 隔离规则回归：仅在本夹具中固定牌堆顶，不计正常对局或界面验收。
            var deck = FindZone(state, "event_yellow");
            var order = state.ItemZones.Orders.Single(value => value.ZoneId == deck.ZoneId);
            string card = state.ItemZones.Items.Single(item => item.DefinitionId == "event_yellow_04").ItemId;
            order.ItemIds.Remove(card); order.ItemIds.Insert(0, card); order.OrderRevision++;
            state.FindPlayer(1).Resources.GoldVoucher = 8;
            string primary = InfluenceService.GetLocationSlotId("A-02", 0);
            string root = Run(EventCardEffectSpecFactory.Resolve(1, EventColor.Yellow, "A-02", primary));
            Answer(EventCardOptionIdFactory.Create("event_yellow_04", 0));
            Assert.That(Open().InteractionTypeId, Is.EqualTo("action.decline_effect"));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(request => request.Status == "open" &&
                request.InteractionTypeId == EventCardEffectExecutor.InfluenceInteractionTypeId), Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(8));
            Assert.That(state.Map.Influences.Count, Is.EqualTo(1));
            Restore();
            Assert.That(Open().InteractionTypeId, Is.EqualTo("action.decline_effect"));
            Answer(pay ? "continue" : "decline");
            if (pay)
            {
                Assert.That(Open().InteractionTypeId, Is.EqualTo("effect.influence.place.target"));
                Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4));
                Restore();
                Assert.That(Open().CandidateIds, Does.Not.Contain(primary));
                Answer(Open().CandidateIds[0]);
            }
            Assert.That(state.EffectRuntime.InteractionRequests.Any(request => request.Status == "open"), Is.False);
            Assert.That(Engine().GetNode(root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.Map.Influences.Count, Is.EqualTo(pay ? 2 : 1));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(pay ? 4 : 8));
        }

        [Test]
        public void OriginallyFreeStyleRunsContentWithoutEmptyCondition()
        {
            const string action = "fixture.free_style";
            var player = new PlayerState { PlayerId = 1, RemainingMainActionsThisTurn = 1, Resources = new ResourceSet() };
            player.DeclaredCityStyles.Add(new CityStyleDeclarationState { InfluenceMarkerId = "fixture.marker", UnlockedSpecialActionId = action,
                MarkerArea = "1", RemainingSpecialActionUses = 1 });
            state = new GameState { GameId = "fixture.free_style", EntitySchemaVersion = 0, MapId = map.Map.MapId, Round = 2, ActionRound = 1,
                CurrentPlayerId = 1, StartPlayerId = 1, Phase = GamePhase.ActionRound1, Players = new List<PlayerState> { player },
                ContentDefinitions = new GameContentDefinitions { SpecialActions = new List<SpecialActionDefinition> {
                    new SpecialActionDefinition { SpecialActionId = action, CityStyleId = "fixture.style", Level = 1,
                        EffectKind = SpecialActionEffectKind.GrantExtraMainActions, FixedCost = new ResourceSet() } } } };
            registry = new EffectRegistry();
            var influence = new InfluenceService(map);
            var query = new SpecialActionOptionQueryService(map, influence, movement, new SpecialActionLifecycleService(), new MainActionBudgetService());
            CityStyleSpecialActionEffectExecutor.Register(registry, query, null, map, influence, movement);
            string id = Run(CityStyleSpecialActionEffectSpecFactory.Activate(1, action, "fixture.marker"));
            Assert.That(Engine().GetNode(id).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.EffectRuntime.EffectNodes.Any(node => node.EffectTypeId == EffectTypeIds.Condition), Is.False);
            Assert.That(state.EffectRuntime.EffectNodes.Any(node => node.Payment != null), Is.False);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(request => request.InteractionTypeId == "action.decline_effect"), Is.False);
        }

        [Test]
        public void DeclaredZeroCostConditionStillRequiresItsOriginalDecision()
        {
            int score = state.FindPlayer(1).Score;
            string condition = Run(EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Pay(1, new ResourceSet()) },
                new[] { ResourceEffectSpecFactory.GainScore(1, 1) }, ruleCondition: true));
            Assert.That(Open().InteractionTypeId, Is.EqualTo("action.decline_effect"));
            Assert.That(Flag(Open().PromptParameters, "canPay"), Is.True);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score));
            Answer("continue"); Assert.That(Engine().GetNode(condition).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + 1));
        }

        [Test]
        public void SalePaymentCannotConsumeSubstitutionMarkers()
        {
            LoadPassenger(); state.FindPlayer(1).Resources.Iron = 1;
            var spec = ResourceEffectSpecFactory.Pay(1, ResourceType.Iron, 1);
            spec.NormalizedArguments.Properties.Add(new NormalizedValueEntry { Name = "allowSubstitution", Value = NormalizedValue.CreateBoolean(false) });
            spec.NormalizedArguments.Properties.Sort((first, second) => StringComparer.Ordinal.Compare(first.Name, second.Name));
            string id = Run(spec); Assert.That(Engine().GetNode(id).Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Engine().GetNode(id).Payment.AllowsSubstitution, Is.False);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero); Assert.That(MarkerCount(), Is.EqualTo(3));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(request => request.OwnerEffectId == id), Is.False);
        }

        [Test]
        public void DeclineOnlyClosesExplicitUnstartedFixedPaymentRelationship()
        {
            LoadPassenger();
            string condition = Run(EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Pay(1, ResourceType.Iron, 1) },
                new[] { ResourceEffectSpecFactory.GainScore(1, 3) }, ruleCondition: true));
            string paymentOwner = Open().OwnerEffectId; int balance = state.FindPlayer(1).Resources.Iron; int score = state.FindPlayer(1).Score;
            Substitute(paymentOwner, "Iron=1");
            Assert.That(Engine().TryDeclineEffect(1, condition, out string reason), Is.True, reason); Drain();
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(balance)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score));
            var cancelled = Engine().GetNode(paymentOwner).Payment;
            Assert.That(cancelled.SourceDrafts.Single().Status, Is.EqualTo("withdrawn"));
            Assert.That(cancelled.SourceDrafts.Single().SelectedItemIds, Is.Empty);
            Assert.That(cancelled.SourceReceipts, Is.Empty); Assert.That(cancelled.FinalReceipt, Is.Null);
            Assert.That(Engine().GetNode(condition).Status, Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(Engine().TryDeclineEffect(1, condition, out _), Is.False);
        }

        [Test]
        public void FailedBeforeResponseKeepsCancelableIdentityAndDoesNotRollbackItsResult()
        {
            registry.Register(new EffectRegistration("fixture.payment.failed_response", context => {
                context.State.FindPlayer(1).Score += 1;
                return EffectStepResult.Failed("fixture_response_failed");
            }));
            registry.RegisterEventHandler(new EffectEventHandlerRegistration("fixture.payment.failed_before", ResourcePaymentPreparationService.BeforeResourcePayment,
                "*", "fixture.payment.failed_before", context => new[] { EffectSpec.Create("fixture.payment.failed_response", playerId: 1) }, EffectHandlerRole.Observer));
            OpenMainWindow(); int score = state.FindPlayer(1).Score;
            string target = new CityMoveCandidateQueryService(map, movement, new TravelCostService(map)).Query(state, 1).First(value => value.IsLegal).TargetLocationId;
            string prepared = Prepare("city_move", target, Object());
            var projection = GameStateViewProjector.ProjectForPlayer(state, 1, registry).PreparedActionPayments.Single();
            Assert.That(projection.IsReady, Is.False); Assert.That(projection.CanConfirm, Is.False); Assert.That(projection.CanCancel, Is.True);
            Assert.That(Engine().TryCancelPreparedActionPayment(1, prepared, state.EffectRuntime.StateRevision, "failed.cancel", out string reason), Is.True, reason);
            Drain(); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + 1));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.EqualTo(1));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1, registry).PreparedActionPayments, Is.Empty);
        }

        [Test]
        public void CannotActualForcedSaleNeverOffersOrConsumesSubstitution()
        {
            LoadPassenger();
            state.FindPlayer(1).Resources.Iron = 1; state.FindPlayer(2).Resources.Iron = 3; state.FindPlayer(3).Resources.Iron = 0;
            var before = state.Players.ToDictionary(player => player.PlayerId, player => player.Resources.GoldVoucher);
            int score = state.FindPlayer(1).Score;
            string source = File.ReadAllText(Path.Combine(privateContentRoot, "lua/characters/cannot.tactic.lua"));
            var definition = new LuaScriptDefinition("cannot.tactic", "cannot.tactic", "fixture.cannot.tactic", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            var snapshot = LuaFacadeSnapshot.FromInvocation("p1", score, before[1], 3, "1.0.0");
            snapshot.Players.Clear();
            foreach (var player in state.Players) snapshot.Players.Add(new LuaPlayerSnapshot("p" + player.PlayerId, player.Score,
                player.Resources.GoldVoucher, player.CityLocationId, player.Resources.Originium, player.Resources.OriginiumShard, player.Resources.Iron, player.Resources.PureOriginium));
            snapshot.EventPayload = LuaSnapshotValue.Object(new[] { new LuaSnapshotEntry("outcome", LuaSnapshotValue.String("completed")),
                new LuaSnapshotEntry("normalizedResult", LuaSnapshotValue.String("iron")) });
            var invocation = new MoonSharpLuaRuntimeHost().Invoke(definition,
                new LuaInvocationContext("cannot.sale", "EffectCompleted", "p1", state.EffectRuntime.StateRevision, "1.0.0", 3, score, before[1], snapshot) { AllowOtherPlayerReads = true });
            Assert.That(invocation.IsSuccess, Is.True, invocation.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invocation, new LuaEffectCompilationContext { PlayerId = 1, SourceId = "cannot.tactic" },
                out var specs, out var fault), Is.True, fault?.Diagnostic);
            var payments = specs.Where(spec => spec.EffectTypeId == ResourceEffectTypeIds.Pay).ToList();
            Assert.That(payments.Count, Is.EqualTo(2)); Assert.That(payments.Select(spec => spec.PlayerId), Is.EquivalentTo(new[] { 1, 2 }));
            foreach (var spec in specs) Run(spec);
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.Zero); Assert.That(state.FindPlayer(2).Resources.Iron, Is.Zero);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(before[1] + 2));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(before[2] + 6));
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(score + 1)); Assert.That(MarkerCount(), Is.EqualTo(3));
            Assert.That(state.EffectRuntime.EffectNodes.Where(node => node.Payment != null).All(node => !node.Payment.AllowsSubstitution), Is.True);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(request => request.Status == "open"), Is.False);
        }

        [TestCase("'false'")]
        [TestCase("0")]
        public void LuaRejectsNonBooleanSubstitutionPermission(string flag)
        {
            string source = "return function(ctx) return {Effect.PayResource({payer=ctx.playerId,resourceType='iron',amount=1,allowSubstitution=" + flag + "})} end";
            var definition = new LuaScriptDefinition("fixture.pay_flag", "fixture.pay_flag", "fixture.pay_flag", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            Assert.That(new MoonSharpLuaRuntimeHost().Invoke(definition, new LuaInvocationContext("pay", "pay", "p1", 0, "1.0.0", 1, 0, 0)).IsSuccess, Is.False);
        }

        private GameSession CreatePreparedActionSession()
        {
            var session = new GameSession(state);
            session.RegisterHandler(new PreparePaymentDraftCommandHandler(registry));
            session.RegisterHandler(new BuildFacilityCommandHandler(new BuildFacilityService(), new RoundAdvanceService(), registry));
            session.RegisterHandler(new MoveCityCommandHandler(movement, new RoundAdvanceService(), registry));
            return session;
        }

        private ItemInstanceState SessionBuildTarget()
        {
            state.FindPlayer(1).Resources = new ResourceSet { Originium = 50, OriginiumShard = 50, Iron = 50, PureOriginium = 10, GoldVoucher = 100 };
            return state.ItemZones.Items.First(item => state.ItemZones.Zones.Any(zone => zone.ZoneId == item.ZoneId && zone.TemplateId == "core.zone.facility_supply_slot"));
        }

        private string SessionPrepareBuild(GameSession session, ItemInstanceState item, string mode)
        {
            var result = session.Submit(EffectInteractionCommands.PrepareActionPayment(1, "build", item.ItemId,
                state.EffectRuntime.StateRevision, mode, item.ZoneId));
            Assert.That(result.Succeeded, Is.True, result.LogMessage);
            var payment = session.ProjectFor(GameStateViewer.Player(1)).PreparedActionPayments.Single();
            Assert.That(payment.IsReady && payment.CanConfirm, Is.True);
            return payment.PreparationOwnerEffectId;
        }

        private GameCommand SessionBuildCommand(string preparationId, ItemInstanceState item, string mode)
        {
            var command = new GameCommand { Kind = GameCommandKind.BuildFacility, PlayerId = 1, TargetId = item.ItemId };
            command.Parameters["paymentMode"] = mode;
            command.Parameters["expectedSourceZoneId"] = item.ZoneId;
            command.Parameters["cityBoardSlotIndex"] = "0";
            command.Parameters["paymentPreparationOwnerEffectId"] = preparationId;
            return command;
        }

        [TestCase("resources")]
        [TestCase("gold")]
        public void SessionAcceptsOnlyReadyPreparedBuildAndDoesNotChargeItsRepeat(string mode)
        {
            var item = SessionBuildTarget(); OpenMainWindow(); var session = CreatePreparedActionSession();
            string prepared = SessionPrepareBuild(session, item, mode);
            var cost = Engine().GetNode(prepared).Payment.PaymentCost.Clone(); var before = state.FindPlayer(1).Resources.Clone();
            var command = SessionBuildCommand(prepared, item, mode);
            var result = session.Submit(command);
            Assert.That(result.Succeeded, Is.True, result.LogMessage);
            Assert.That(Engine().GetNode(prepared).Payment.IsCommitted, Is.True);
            foreach (var type in Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>())
                Assert.That(state.FindPlayer(1).Resources.Get(type), Is.EqualTo(before.Get(type) - cost.Get(type)));
            Assert.That(state.Map.Facilities.Any(value => value.ContentInstanceId == item.ItemId), Is.True);
            var paid = state.FindPlayer(1).Resources.Clone();
            Assert.That(session.Submit(command).Succeeded, Is.False);
            foreach (var type in Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>())
                Assert.That(state.FindPlayer(1).Resources.Get(type), Is.EqualTo(paid.Get(type)));
        }

        [Test]
        public void SessionAcceptsReadyPreparedMoveAndPreservesBothBeforeEventsOnce()
        {
            OpenMainWindow(); var session = CreatePreparedActionSession();
            string target = new CityMoveCandidateQueryService(map, movement, new TravelCostService(map)).Query(state, 1).First(value => value.IsLegal).TargetLocationId;
            var preparedResult = session.Submit(EffectInteractionCommands.PrepareActionPayment(1, "city_move", target, state.EffectRuntime.StateRevision));
            Assert.That(preparedResult.Succeeded, Is.True, preparedResult.LogMessage);
            string prepared = session.ProjectFor(GameStateViewer.Player(1)).PreparedActionPayments.Single().PreparationOwnerEffectId;
            int before = state.FindPlayer(1).Resources.OriginiumShard, cost = Engine().GetNode(prepared).Payment.PaymentCost.OriginiumShard;
            var command = new GameCommand { Kind = GameCommandKind.MoveCity, PlayerId = 1, TargetId = CityMoveCandidateQueryService.CandidatePrefix + target };
            command.Parameters["paymentPreparationOwnerEffectId"] = prepared;
            var result = session.Submit(command);
            Assert.That(result.Succeeded, Is.True, result.LogMessage);
            Assert.That(state.FindPlayer(1).CityLocationId, Is.EqualTo(target));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(before - cost));
            Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.EventType == CityMoveEventTypeIds.BeforeCityMove), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Count(value => value.SourceEffectId == prepared && value.EventType == ResourcePaymentPreparationService.BeforeResourcePayment), Is.EqualTo(1));
            Assert.That(session.Submit(command).Succeeded, Is.False);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(before - cost));
        }

        [TestCase("identity")]
        [TestCase("missing_identity")]
        [TestCase("target")]
        [TestCase("mode")]
        [TestCase("source")]
        [TestCase("player")]
        [TestCase("revision")]
        [TestCase("window")]
        public void SessionRejectsMismatchedPreparedBuildBeforeAnyCharge(string mismatch)
        {
            var item = SessionBuildTarget(); OpenMainWindow(); var session = CreatePreparedActionSession();
            string prepared = SessionPrepareBuild(session, item, "gold");
            var command = SessionBuildCommand(prepared, item, "gold");
            if (mismatch == "identity") command.Parameters["paymentPreparationOwnerEffectId"] += ".wrong";
            else if (mismatch == "missing_identity") command.Parameters.Remove("paymentPreparationOwnerEffectId");
            else if (mismatch == "target") command.TargetId += ".wrong";
            else if (mismatch == "mode") command.Parameters["paymentMode"] = "resources";
            else if (mismatch == "source") command.Parameters["expectedSourceZoneId"] += ".wrong";
            else if (mismatch == "player") command.PlayerId = 2;
            else if (mismatch == "revision") command.Parameters["expectedRevision"] = (state.EffectRuntime.StateRevision - 1).ToString();
            else Engine().GetNode(prepared).Payment.Preparation.WindowGeneration += 1;
            int before = state.FindPlayer(1).Resources.GoldVoucher, nodes = state.EffectRuntime.EffectNodes.Count;
            Assert.That(session.Submit(command).Succeeded, Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(before));
            Assert.That(state.EffectRuntime.EffectNodes.Count, Is.EqualTo(nodes));
            Assert.That(Engine().GetNode(prepared).Payment.IsCommitted || Engine().GetNode(prepared).Payment.IsCommitRequested, Is.False);
        }

        [Test]
        public void SessionDoesNotBypassAnotherRequiredRequestForPreparedFinalBuild()
        {
            var item = SessionBuildTarget(); OpenMainWindow(); var session = CreatePreparedActionSession();
            string prepared = SessionPrepareBuild(session, item, "gold");
            registry.Register(new EffectRegistration("fixture.session.required", context => EffectStepResult.Continue("waiting")
                .AddInteraction(new EffectInteractionSpec { InteractionTypeId = "fixture.session.required", AnsweringPlayerId = 1,
                    AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { "finish" } }), EffectExecutorKind.IntrinsicFlow));
            Run(EffectSpec.Create("fixture.session.required", playerId: 1));
            int before = state.FindPlayer(1).Resources.GoldVoucher;
            var result = session.Submit(SessionBuildCommand(prepared, item, "gold"));
            Assert.That(result.Succeeded, Is.False); Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.PendingChoiceRequired));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(before));
            Assert.That(Engine().GetNode(prepared).Payment.IsCommitted, Is.False);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(request => request.Status == "open" && request.InteractionTypeId == "fixture.session.required"), Is.True);
        }

        private GameplayTextDocument ResolvePaymentText(GameplayTextRef reference)
        {
            var catalog = ExternalContentPack.Load(privateContentRoot).Presentation;
            var context = new GameplayTextContext { Catalog = catalog, ContentHash = catalog.ContentHash, Locale = catalog.Locale,
                ViewerPlayerId = 1, StateRevision = state.EffectRuntime.StateRevision, VisibilityScopeId = state.GameId };
            return new GameplayTextResolver(catalog).Resolve(reference, context);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PaymentPresentationFreshRequestsResolveAllTypedCostFields(bool condition)
        {
            LoadPassenger();
            var cost = new ResourceSet { Originium = 2, OriginiumShard = 3, Iron = 4, PureOriginium = 5, GoldVoucher = 6 };
            var pay = ResourceEffectSpecFactory.Pay(1, cost);
            Run(condition ? EffectSpec.Condition(new[] { pay }, new[] { ResourceEffectSpecFactory.GainScore(1, 1) }, ruleCondition: true) : pay);
            var request = Open(); var projected = GameStateViewProjector.ProjectForPlayer(state, 1, registry).Interactions.Single(value => value.InteractionId == request.GetStableInteractionId());
            Assert.That(projected.Presentation?.Prompt, Is.Not.Null);
            var prompt = projected.Presentation.Prompt;
            Assert.That(prompt.Key, Is.EqualTo(condition ? "core:resource.payment.condition.prompt" : "core:resource.payment.confirmation.prompt"));
            Assert.That(prompt.Arguments.Count, Is.EqualTo(5));
            Assert.That(prompt.Arguments.All(argument => argument.Kind == "integer" && argument.Value.Kind == NormalizedValueKind.Integer), Is.True);
            var document = ResolvePaymentText(prompt);
            Assert.That(document, Is.Not.Null);
            Assert.That(document.Runs.OfType<NumberRun>().Select(run => run.Value), Is.EquivalentTo(new[] { 2, 3, 4, 5, 6 }));
            if (condition)
            {
                Assert.That(request.CandidateIds, Is.EquivalentTo(new[] { "continue", "decline" }));
            }
            else Assert.That(ResolvePaymentText(projected.Presentation.Options.Single(option => option.CandidateId == "pay").Label).ToPlainText(), Is.Not.Empty);
            Assert.That(Engine().GetNode(request.OwnerEffectId).Payment.IsCommitted || Engine().GetNode(request.OwnerEffectId).Payment.IsCommitRequested, Is.False);
        }

        [Test]
        public void PaymentPresentationKeepsAuthoredSourceBodyAndAddsCurrentCost()
        {
            var author = new GameplayTextRef("core:interaction.tin_man.purchase", new GameplayTextArgument {
                Name = "cost", Kind = "integer", Value = NormalizedValue.CreateInteger(9) });
            var condition = EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Pay(1, new ResourceSet { GoldVoucher = 3 }) },
                new[] { ResourceEffectSpecFactory.GainScore(1, 1) }, ruleCondition: true);
            condition.Presentation = new EffectPresentationDefinition { Interaction = new GameplayInteractionPresentation { Prompt = author } };
            string root = Run(condition); var request = Open();
            Assert.That(request.Presentation.Prompt.Key, Is.EqualTo(author.Key));
            Assert.That(request.Presentation.Prompt.Arguments.Single().Value.IntegerValue, Is.EqualTo(9));
            Assert.That(Engine().GetNode(root).Presentation.Interaction.Prompt.Key, Is.EqualTo(author.Key));
            Assert.That(request.Presentation.Progress.Key, Is.EqualTo("core:resource.payment.condition.prompt"));
            Assert.That(ResolvePaymentText(request.Presentation.Prompt).Runs.OfType<NumberRun>().Select(run => run.Value), Does.Contain(9));
            var fees = ResolvePaymentText(request.Presentation.Progress);
            Assert.That(fees.Runs.OfType<NumberRun>().Select(run => run.Value), Is.EquivalentTo(new[] { 0, 0, 0, 0, 3 }));
            var authorProgress = new GameplayTextRef("core:effect.repeat.progress",
                new GameplayTextArgument { Name = "completed", Kind = "integer", Value = NormalizedValue.CreateInteger(2) },
                new GameplayTextArgument { Name = "total", Kind = "integer", Value = NormalizedValue.CreateInteger(10) });
            var withProgress = EffectSpec.Condition(new[] { ResourceEffectSpecFactory.Pay(1, new ResourceSet { GoldVoucher = 3 }) },
                new[] { ResourceEffectSpecFactory.GainScore(1, 1) }, ruleCondition: true);
            withProgress.Presentation = new EffectPresentationDefinition { Interaction = new GameplayInteractionPresentation {
                Prompt = author.Clone(), Progress = authorProgress } };
            string next = Run(withProgress); var preserved = Open().Presentation;
            Assert.That(preserved.Prompt.Key, Is.EqualTo(author.Key));
            Assert.That(preserved.Progress.Key, Is.EqualTo(authorProgress.Key));
            Assert.That(preserved.Progress.Arguments.Select(argument => argument.Value.IntegerValue), Is.EqualTo(new long[] { 2, 10 }));
            Assert.That(Engine().GetNode(next).Presentation.Interaction.Progress.Key, Is.EqualTo(authorProgress.Key));
            Assert.That(ResolvePaymentText(preserved.Progress).Runs.OfType<NumberRun>().Select(run => run.Value), Is.EqualTo(new[] { 2, 10 }));
        }

        private ItemInstanceState BuildQuoteTarget()
        {
            var item = state.ItemZones.Items.First(value => state.ItemZones.Zones.Any(zone => zone.ZoneId == value.ZoneId && zone.TemplateId == "core.zone.facility_supply_slot") &&
                FacilityCardDatabase.Get(state, value.DefinitionId).ResourceCost.Enumerate().Any(cost => cost.Value > 0));
            state.FindPlayer(1).Resources = new ResourceSet { GoldVoucher = 100 };
            return item;
        }

        [Test]
        public void BuildQuoteShowsIndependentGoldOptionDuringUnaffordableResourcePreparationWithoutWriting()
        {
            var item = BuildQuoteTarget(); OpenMainWindow(); var session = CreatePreparedActionSession();
            var applied = session.Submit(EffectInteractionCommands.PrepareActionPayment(1, "build", item.ItemId,
                state.EffectRuntime.StateRevision, "resources", item.ZoneId));
            Assert.That(applied.Succeeded, Is.True, applied.LogMessage);
            var prepared = session.ProjectFor(GameStateViewer.Player(1)).PreparedActionPayments.Single();
            Assert.That(prepared.IsReady && prepared.CanCancel, Is.True); Assert.That(prepared.CanConfirm, Is.False);
            var before = GameStateCloneService.DeepClone(state);
            var service = new BuildFacilityOptionQueryService();
            var option = service.Query(state, 1, item.ItemId);
            Assert.That(option.ResourcesPayment.IsAvailable, Is.False); Assert.That(option.GoldPayment.IsAvailable, Is.True, option.GoldPayment.Reason);
            Assert.That(option.CanBuild, Is.True); Assert.That(option.SlotOptions.Any(slot => slot.IsLegal), Is.True);
            var snapshotOption = service.Query(state, 1, service.QueryCandidates(state, 1)).Single(value => value.FacilityId == item.ItemId);
            Assert.That(snapshotOption.GoldPayment.IsAvailable, Is.True);
            Assert.That(GameStateCloneService.AreEquivalent(before, state), Is.True, "报价查询不得派事件、改准备或扣款。");
            var goldWithoutSwitch = SessionBuildCommand(prepared.PreparationOwnerEffectId, item, "gold");
            Assert.That(session.Submit(goldWithoutSwitch).Succeeded, Is.False, "金券报价可用不代表资源准备可冒充金券最终提交。");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(100));
            Assert.That(Engine().GetNode(prepared.PreparationOwnerEffectId).Payment.IsCommitted, Is.False);
        }

        [TestCase("player")]
        [TestCase("target")]
        [TestCase("window")]
        [TestCase("committing")]
        [TestCase("other_request")]
        public void BuildQuoteKeepsPreparationScopeAndRequiredObligations(string mismatch)
        {
            var item = BuildQuoteTarget(); OpenMainWindow(); var session = CreatePreparedActionSession();
            var applied = session.Submit(EffectInteractionCommands.PrepareActionPayment(1, "build", item.ItemId,
                state.EffectRuntime.StateRevision, "resources", item.ZoneId));
            Assert.That(applied.Succeeded, Is.True, applied.LogMessage);
            string prepared = session.ProjectFor(GameStateViewer.Player(1)).PreparedActionPayments.Single().PreparationOwnerEffectId;
            int playerId = 1; string target = item.ItemId;
            if (mismatch == "player") playerId = 2;
            else if (mismatch == "target") target = state.ItemZones.Items.First(value => value.ItemId != item.ItemId &&
                state.ItemZones.Zones.Any(zone => zone.ZoneId == value.ZoneId && zone.TemplateId == "core.zone.facility_supply_slot")).ItemId;
            else if (mismatch == "window") Engine().GetNode(prepared).Payment.Preparation.WindowGeneration += 1;
            else if (mismatch == "committing") Engine().GetNode(prepared).Payment.IsCommitRequested = true;
            else
            {
                registry.Register(new EffectRegistration("fixture.quote.required", context => EffectStepResult.Continue("waiting")
                    .AddInteraction(new EffectInteractionSpec { InteractionTypeId = "fixture.quote.required", AnsweringPlayerId = 1,
                        AnswerSchema = "candidate_id", MinSelections = 1, MaxSelections = 1, CandidateIds = { "finish" } }), EffectExecutorKind.IntrinsicFlow));
                Run(EffectSpec.Create("fixture.quote.required", playerId: 1));
            }
            var before = GameStateCloneService.DeepClone(state);
            var option = new BuildFacilityOptionQueryService().Query(state, playerId, target);
            Assert.That(option.CanBuild || option.GoldPayment.IsAvailable || option.ResourcesPayment.IsAvailable, Is.False);
            Assert.That(option.Validation.ErrorCode, Is.EqualTo(mismatch == "player" ? CommandErrorCode.NotCurrentPlayer : CommandErrorCode.PendingChoiceRequired));
            Assert.That(GameStateCloneService.AreEquivalent(before, state), Is.True);
        }
    }
}
