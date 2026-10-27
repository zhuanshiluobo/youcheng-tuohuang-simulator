using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Presentation;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 全部为隔离规则/恢复检查，不计正常游戏或页面验收。
    public sealed class TotalResourcePaymentTests
    {
        private PaymentTestFixture f;
        [SetUp] public void SetUp() => f = new PaymentTestFixture();
        [TearDown] public void TearDown() => f?.Dispose();
        [TestCase(0,1)][TestCase(1,3)][TestCase(2,6)][TestCase(3,10)]
        public void ExactTotalEnumeratesEveryDistinctAllocation(int n, int count)
        {
            var args = PaymentTestFixture.TotalArgs(n, new ResourceSet { GoldVoucher = 4 });
            Assert.That(ResourcePaymentDemandParser.TryParse(args, out var demand, out var player), Is.Empty);
            Assert.That(player, Is.EqualTo(1)); var rows = ResourcePaymentAllocationService.EnumerateAllocations(demand).ToList();
            Assert.That(rows.Count, Is.EqualTo(count)); Assert.That(rows.Select(PaymentResources.Candidate).Distinct().Count(), Is.EqualTo(count));
            foreach (var row in rows) { Assert.That(ResourcePaymentAllocationService.TryValidate(demand, row, out var gross, out var reason), Is.True, reason); Assert.That(PaymentResources.Total(row), Is.EqualTo(n)); Assert.That(gross.GoldVoucher, Is.EqualTo(4)); }
            f.Artifacts.WriteLabeledText("完整合计配比", JsonConvert.SerializeObject(rows));
        }
        [Test] public void FixedPartAndAllocationOfSameTypeAreAddedAndOverflowRejected()
        {
            var d = new ResourcePaymentDemandState { Mode = "total", FixedCost = new ResourceSet { Iron = 2 }, AllowedResourceTypes = new List<ResourceType> { ResourceType.Iron }, TotalCount = 1 };
            Assert.That(ResourcePaymentAllocationService.TryValidate(d, new ResourceSet { Iron = 1 }, out var gross, out _), Is.True); Assert.That(gross.Iron, Is.EqualTo(3));
            d.FixedCost.Iron = int.MaxValue;
            Assert.That(ResourcePaymentAllocationService.TryValidate(d, new ResourceSet { Iron = 1 }, out _, out var reason), Is.False); Assert.That(reason, Is.EqualTo("payment_cost_overflow"));
        }
        [TestCase(-1)][TestCase(100)] public void InvalidTotalIsRejectedBeforeAnyPayment(int n)
        { Assert.That(ResourcePaymentDemandParser.TryParse(PaymentTestFixture.TotalArgs(n, new ResourceSet()), out _, out _), Is.Not.Empty); Assert.That(f.State.EffectRuntime.EffectNodes, Is.Empty); }
        [Test] public void DuplicateOrUnknownResourceAndFractionalCountAreRejected()
        {
            var a = PaymentTestFixture.TotalArgs(2, new ResourceSet());
            Value(a, "allowedResourceTypes").Items.Add(NormalizedValue.CreateString("Iron"));
            Assert.That(ResourcePaymentDemandParser.TryParse(a, out _, out _), Is.Not.Empty);
            a = PaymentTestFixture.TotalArgs(2, new ResourceSet()); Value(a, "allowedResourceTypes").Items[0] = NormalizedValue.CreateString("total");
            Assert.That(ResourcePaymentDemandParser.TryParse(a, out _, out _), Is.Not.Empty);
            a = PaymentTestFixture.TotalArgs(2, new ResourceSet()); a.Properties.Single(p => p.Name == "amount").Value = NormalizedValue.CreateString("2.5");
            Assert.That(ResourcePaymentDemandParser.TryParse(a, out _, out _), Is.Not.Empty);
        }
        [Test] public void TotalUsesOneHostAndDraftChangeDoesNotPayUntilOriginalConfirmation()
        {
            var node = f.Run(EffectSpec.Create(ResourceEffectTypeIds.Pay, PaymentTestFixture.TotalArgs(2, new ResourceSet { GoldVoucher = 4 }), 1));
            string id = node.EffectId; var request = f.Open(); int gold = f.State.FindPlayer(1).Resources.GoldVoucher;
            Assert.That(request.Presentation.Prompt.Key, Is.EqualTo("core:resource.payment.choice.prompt"));
            Assert.That(request.Presentation.Progress, Is.Null, "无条件普通付款保持原fallback");
            string display = JsonConvert.SerializeObject(request.Presentation);
            Assert.That(node.Payment.PaymentId, Is.EqualTo(id)); Assert.That(node.ChildEffectIds, Is.Empty);
            Assert.That(f.Engine.TryPrepareTotalPaymentDraft(1, id, request.GetStableInteractionId(), node.Payment.PaymentGeneration, new ResourceSet { Iron = 2 }, f.State.EffectRuntime.StateRevision, "合计改配比", out var reason), Is.True, reason);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold)); Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.Payment != null), Is.EqualTo(1));
            f.Restore("合计草稿恢复"); node = f.Engine.GetNode(id);
            Assert.That(JsonConvert.SerializeObject(f.Open().Presentation), Is.EqualTo(display));
            Assert.That(node.Payment.SelectedAllocation.Iron, Is.EqualTo(2));
            f.Answer(PaymentResources.Candidate(node.Payment.PaymentCost));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold - 4)); Assert.That(f.Engine.GetNode(id).Payment.IsCommitted, Is.True);
            f.Restore("合计提交后恢复"); Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold - 4));
        }
        [Test] public void InsufficientFixedPartCannotPartPayOrUnlockRightSide()
        {
            f.State.FindPlayer(1).Resources.GoldVoucher = 3;
            var left = EffectSpec.Create(ResourceEffectTypeIds.Pay, PaymentTestFixture.TotalArgs(2, new ResourceSet { GoldVoucher = 4 }), 1);
            var condition = EffectSpec.Condition(new[] { left }, new[] { ResourceEffectSpecFactory.GainScore(1, 7) }, ruleCondition:true); condition.PlayerId = 1;
            f.Run(condition); Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(3)); Assert.That(f.State.FindPlayer(1).Score, Is.Zero);
        }
        [Test] public void DeclineDoesNotRunConditionRightSideOrCreateSecondPayment()
        {
            var condition = EffectSpec.Condition(new[] { EffectSpec.Create(ResourceEffectTypeIds.Pay, PaymentTestFixture.TotalArgs(2, new ResourceSet { GoldVoucher = 4 }), 1) }, new[] { ResourceEffectSpecFactory.GainScore(1, 7) }, ruleCondition:true); condition.PlayerId = 1;
            condition.Presentation = new EffectPresentationDefinition { Interaction = new GameplayInteractionPresentation {
                Prompt = new GameplayTextRef("core:interaction.effect.optional"),
                Progress = new GameplayTextRef("core:resource.payment.confirmation.pay") } };
            var root = f.Run(condition); var request = f.Open();
            Assert.That(request.Presentation.Prompt.Key, Is.EqualTo("core:interaction.effect.optional"));
            Assert.That(request.Presentation.Progress.Key, Is.EqualTo("core:resource.payment.confirmation.pay"), "源已有进度不被通用提示覆盖");
            f.Restore("R14合计条件源Prompt和Progress待放弃"); request = f.Open();
            Assert.That(request.Presentation.Progress.Key, Is.EqualTo("core:resource.payment.confirmation.pay"));
            Assert.That(f.Engine.TrySubmitInteraction(request.GetStableInteractionId(), 1, request.StateRevision, NormalizedValue.CreateBoolean(false), out var reason), Is.True, reason); f.Drain();
            Assert.That(f.Engine.GetNode(root.EffectId).Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(f.State.FindPlayer(1).Score, Is.Zero); Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(100));
        }
        [Test] public void ConditionalTotalPromptSurvivesDraftRefreshAndRecoveryWithoutAnExtraGate()
        {
            var condition = EffectSpec.Condition(new[] { EffectSpec.Create(ResourceEffectTypeIds.Pay,
                PaymentTestFixture.TotalArgs(2, new ResourceSet { GoldVoucher = 4 }), 1) },
                new[] { ResourceEffectSpecFactory.GainScore(1, 7) }, ruleCondition:true); condition.PlayerId = 1;
            condition.Presentation = new EffectPresentationDefinition { Interaction = new GameplayInteractionPresentation {
                Prompt = new GameplayTextRef("core:interaction.effect.optional") } };
            f.Run(condition); var q = f.Open(); var owner = f.Engine.GetNode(q.OwnerEffectId);
            string requestId = q.GetStableInteractionId(), display = JsonConvert.SerializeObject(q.Presentation);
            Assert.That(q.Presentation.Prompt.Key, Is.EqualTo("core:interaction.effect.optional"));
            Assert.That(q.Presentation.Progress.Key, Is.EqualTo("core:resource.payment.choice.prompt"));
            Assert.That(q.InteractionTypeId, Is.EqualTo(ResourcePaymentChoiceEffectExecutor.InteractionTypeId));
            Assert.That(q.AnswerSchema, Is.EqualTo("candidate_id")); Assert.That(q.MinSelections, Is.EqualTo(1)); Assert.That(q.MaxSelections, Is.EqualTo(1));
            Assert.That(q.AllowDecline, Is.True); Assert.That(q.AnsweringPlayerId, Is.EqualTo(1));
            Assert.That(f.State.EffectRuntime.InteractionRequests.Any(r => r.InteractionTypeId == "action.decline_effect"), Is.False);
            string before = JsonConvert.SerializeObject(f.State);
            Assert.That(f.Engine.TrySubmitInteraction(requestId, 2, q.StateRevision,
                NormalizedValue.CreateString(q.CandidateIds[0]), out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(f.State), Is.EqualTo(before));
            Assert.That(f.Engine.TryPrepareTotalPaymentDraft(1, owner.EffectId, requestId, owner.Payment.PaymentGeneration,
                new ResourceSet { Iron = 2 }, f.State.EffectRuntime.StateRevision, "R14条件合计改配比", out var reason), Is.True, reason);
            q = f.Open(); Assert.That(q.GetStableInteractionId(), Is.EqualTo(requestId));
            Assert.That(JsonConvert.SerializeObject(q.Presentation), Is.EqualTo(display));
            Assert.That(Number(Value(q.PromptParameters, "paymentDemand"), "totalCount"), Is.EqualTo(2));
            f.Restore("R14条件合计正文与配比恢复"); q = f.Open(); owner = f.Engine.GetNode(q.OwnerEffectId);
            Assert.That(q.GetStableInteractionId(), Is.EqualTo(requestId)); Assert.That(JsonConvert.SerializeObject(q.Presentation), Is.EqualTo(display));
            Assert.That(owner.Payment.SelectedAllocation.Iron, Is.EqualTo(2));
            f.Answer(PaymentResources.Candidate(owner.Payment.PaymentCost));
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(96));
            Assert.That(f.State.FindPlayer(1).Resources.Iron, Is.EqualTo(98)); Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(7));
            Assert.That(f.State.EffectRuntime.EffectNodes.Count(n => n.Payment?.IsCommitted == true), Is.EqualTo(1));
        }
        [TestCase("foreign_player")][TestCase("outer_condition")]
        public void TotalDoesNotBorrowAnotherPayerOrOuterConditionPrompt(string scope)
        {
            var pay = EffectSpec.Create(ResourceEffectTypeIds.Pay, PaymentTestFixture.TotalArgs(2, new ResourceSet()), 1);
            var internalCondition = EffectSpec.Condition(new[] { pay }, Array.Empty<EffectSpec>()); internalCondition.PlayerId = 1;
            var condition = EffectSpec.Condition(scope == "outer_condition" ? new[] { internalCondition } : new[] { pay },
                new[] { ResourceEffectSpecFactory.GainScore(1, 1) }, ruleCondition:true);
            condition.PlayerId = scope == "foreign_player" ? 2 : 1;
            condition.Presentation = new EffectPresentationDefinition { Interaction = new GameplayInteractionPresentation {
                Prompt = new GameplayTextRef("core:interaction.effect.optional") } };
            var resourcesBefore = f.State.FindPlayer(1).Resources.Clone();
            var root = f.Run(condition);
            if (scope == "outer_condition") { Assert.That(f.Open().InteractionTypeId, Is.EqualTo("action.decline_effect")); f.Answer("continue"); }
            var q = f.Open(); Assert.That(q.InteractionTypeId, Is.EqualTo(ResourcePaymentChoiceEffectExecutor.InteractionTypeId));
            Assert.That(q.AnsweringPlayerId, Is.EqualTo(1));
            Assert.That(q.Presentation.Prompt.Key, Is.EqualTo("core:resource.payment.choice.prompt")); Assert.That(q.Presentation.Progress, Is.Null);
            Assert.That(f.Engine.TrySubmitInteraction(q.GetStableInteractionId(), 1, q.StateRevision,
                NormalizedValue.CreateBoolean(false), out var reason), Is.True, reason); f.Drain();
            var payment = f.Engine.GetNode(q.OwnerEffectId); root = f.Engine.GetNode(root.EffectId);
            Assert.That(payment.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(payment.FailureReason, Is.EqualTo("player_declined"));
            Assert.That(payment.Payment.Phase, Is.EqualTo("cancelled")); Assert.That(payment.Payment.IsCommitRequested, Is.False);
            Assert.That(payment.Payment.FinalReceipt, Is.Null); Assert.That(payment.Payment.SourceReceipts, Is.Empty);
            Assert.That(PaymentResources.Same(f.State.FindPlayer(1).Resources, resourcesBefore), Is.True);
            Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.Payment?.IsCommitted == true), Is.False);
            if (scope == "outer_condition")
            {
                // 非规则内部Condition完成的是评估；false结果不等于Failed，不表达卡面费用已付。
                var probe = f.State.EffectRuntime.EffectNodes.Single(n => n.ParentEffectId == root.EffectId && n.EffectTypeId == EffectTypeIds.Condition);
                Assert.That(Flag(probe.NormalizedArguments, "ruleCondition"), Is.False);
                Assert.That(probe.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(Flag(probe.NormalizedResult, "conditionMet"), Is.False);
                Assert.That(Text(probe.NormalizedResult, "outcome"), Is.EqualTo("condition_not_met"));
                Assert.That(Text(probe.NormalizedResult, "failedChildId"), Is.EqualTo(payment.EffectId));
                Assert.That(root.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(Flag(root.NormalizedResult, "conditionMet"), Is.True);
                Assert.That(Text(root.NormalizedResult, "outcome"), Is.EqualTo("condition_met"));
                Assert.That(payment.Payment.SourceKind, Is.EqualTo("pay")); Assert.That(payment.Payment.RuleConditionOwnerEffectId, Is.Empty);
                var gain = f.State.EffectRuntime.EffectNodes.Single(n => n.ParentEffectId == root.EffectId && n.EffectTypeId == ResourceEffectTypeIds.GainScore);
                Assert.That(gain.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(1));
            }
            else
            {
                Assert.That(root.Status, Is.EqualTo(EffectNodeStatus.Failed)); Assert.That(root.FailureReason, Is.EqualTo("player_declined"));
                Assert.That(Flag(root.NormalizedResult, "conditionMet"), Is.False);
                Assert.That(Text(root.NormalizedResult, "outcome"), Is.EqualTo("condition_not_met"));
                Assert.That(f.State.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == ResourceEffectTypeIds.GainScore), Is.False);
                Assert.That(f.State.FindPlayer(1).Score, Is.Zero);
            }
            f.Artifacts.WriteLabeledText("R15内部条件评估与付款隔离", JsonConvert.SerializeObject(new { scope,
                score = f.State.FindPlayer(1).Score, resources = f.State.FindPlayer(1).Resources,
                root.EffectId, root.Status, root.NormalizedResult, payment.Payment }));
        }
    }

    internal sealed class PaymentTestFixture : IDisposable
    {
        public IsolatedTestArtifacts Artifacts;
        public GameState State;
        public EffectRegistry Registry;
        public string PrivateContent;
        public EffectTreeExecutor Engine => new EffectTreeExecutor(State, Registry);
        public PaymentTestFixture(int markerQuantity = 6, GameMapDefinition mapOverride = null)
        {
            Artifacts = IsolatedTestArtifacts.BeginCase();
            var content = Artifacts.CopyContent(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"), "007付款私有内容");
            PrivateContent = content;
            var pack = ExternalContentPack.Load(content); Artifacts.RecordContent("007冻结输入", content, pack.ContentHash);
            if (!FacilityCardDatabase.IsInitialized) FacilityCardDatabase.InitializeExternal(pack.CreateFacilities());
            var map = mapOverride ?? new GameMapDefinition { MapId = "test.transfer_map", MinPlayers = 2, MaxPlayers = 4, Regions = new List<MapRegionDefinition> { new MapRegionDefinition { RegionId = "harbor_region" } },
                Locations = new List<MapLocationDefinition> { new MapLocationDefinition { LocationId = "harbor", RegionId = "harbor_region", CanDockCity = true, InfluenceSlotCount = 2 } } };
            var input = new GameState { GameId = "game.qy007", MapId = map.MapId, ContentPackHash = "content.qy007", RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = "content.qy007" },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "甲", RemainingMainActionsThisTurn = 1, Resources = new ResourceSet { GoldVoucher = 100, Originium = 100, Iron = 100, OriginiumShard = 100 } }, new PlayerState { PlayerId = 2, Name = "乙" } },
                Round = 1, ActionRound = 1, Phase = GamePhase.ActionRound1, CurrentPlayerId = 1, StartPlayerId = 1 };
            input.RandomRuntime = GameplayRandomRuntime.Create(input.GameId);
            var recipe = new List<ItemRecipeEntry> { new ItemRecipeEntry { EntryId = "markers", DefinitionId = "component.marker", TypeId = "core.player_marker", Quantity = markerQuantity, PerPlayer = true, InitialZoneRole = "supply" } };
            for (int i = 0; i < 2; i++) recipe.Add(new ItemRecipeEntry { EntryId = "source_" + i, DefinitionId = "test.payment.source_" + i, TypeId = "core.basic_character_card", Quantity = 1, PerPlayer = true, InitialZoneRole = "hand" });
            for (int i = 0; i < 6; i++) recipe.Add(new ItemRecipeEntry { EntryId = "facility_" + i, DefinitionId = "building_" + (i + 1).ToString("000"), TypeId = "core.facility_card", Quantity = 1, InitialZoneRole = "facility_deck" });
            State = new ItemZoneStateFactory().CreateForSetup(input, map, recipe);
            State.ContentDefinitions = new GameContentDefinitions { Facilities = pack.CreateFacilities().ToList() };
            Registry = new EffectRegistry { ContentPackHash = State.ContentPackHash };
            ItemTransferEffectExecutor.Register(Registry); ResourceEffectExecutor.Register(Registry); DeckShuffleEffectExecutor.Register(Registry); RepeatEffectExecutor.Register(Registry);
            FacilityEntryEffectExecutor.Register(Registry); FacilitySelectionEffectExecutor.Register(Registry); ResourcePaymentChoiceEffectExecutor.Register(Registry);
        }
        public static NormalizedValue TotalArgs(int n, ResourceSet fixedCost) => Object("apiVersion", NormalizedValue.CreateInteger(2), "player", NormalizedValue.CreateString("1"), "resourceType", NormalizedValue.CreateString("total"),
            "allowedResourceTypes", Strings(new[] { "Originium", "OriginiumShard", "Iron" }), "amount", NormalizedValue.CreateInteger(n), "cost", PaymentResources.Write(fixedCost));
        public EffectNodeRuntimeState Run(EffectSpec spec) { string id = Engine.CreateRoot(spec); Drain(); return Engine.GetNode(id); }
        public void Drain() { var r = Engine.RunUntilQuiescent(); Assert.That(r.Faulted, Is.False, State.EffectRuntime.LastFaultMessage); }
        public InteractionRequest Open() => State.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
        public void Answer(string candidate) { var q = Open(); Assert.That(Engine.TrySubmitInteraction(q.GetStableInteractionId(), 1, q.StateRevision, NormalizedValue.CreateString(candidate), out var reason), Is.True, reason); Drain(); }
        public void Restore(string label) { var r = Artifacts.Recover(State, label, Registry); Assert.That(r.Succeeded, Is.True, r.Diagnostic); State = r.State; Drain(); }
        public void Dispose() { Artifacts.WriteLabeledText("007当前隔离状态", JsonConvert.SerializeObject(State)); Artifacts.Finish(); }
    }
}
