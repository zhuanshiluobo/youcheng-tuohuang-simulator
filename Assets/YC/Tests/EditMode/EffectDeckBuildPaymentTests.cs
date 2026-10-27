using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Domain.Maps;
using YC.Domain.Items;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode
{
    // 正式Lua/配方的隔离规则验收；未计正常页面或多端完整局。
    public sealed class EffectDeckBuildPaymentTests
    {
        private GameState state;
        private YC.Tests.EditMode.ExpansionRules.IsolatedTestArtifacts artifacts;
        [SetUp] public void BeginEvidence() => artifacts = YC.Tests.EditMode.ExpansionRules.IsolatedTestArtifacts.BeginCase();
        [TearDown] public void FinishEvidence()
        {
            if (state != null) artifacts.WriteLabeledText("正式Lua隔离当前状态", JsonConvert.SerializeObject(state));
            artifacts.Finish();
        }
        private EffectRegistry registry;
        private string root, selection;
        private void Start(bool frontier = false, bool zero = false, bool crossPlayer = false, bool passenger = false, bool mainWindow = false,
            string scriptPath = null, ResourceSet wallet = null)
        {
            var installed = ExternalContentPack.Current;
            var config = new GameRuleConfiguration { MapId = StaticMapDefinitions.FourPlayerMapId, PlayerCount = 4,
                EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
            var catalog = ActiveContentCatalogBuilder.Build(installed, config); config.ContentPackHash = catalog.ContentPackHash;
            registry = new EffectRegistry(); ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            FacilityEntryEffectExecutor.Register(registry); catalog.Register(registry);
            state = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1, Enumerable.Range(1, 4).Select(p => new PlayerSeat { PlayerId = p }).ToList(),
                config, 713, catalog.SetupRecipe, catalog.ContentDefinitions, new[] { 1, 2, 3, 4 }, "effect.deck.payment." + frontier, null);
            state.Round = 1; state.ActionRound = 1; state.Phase = GamePhase.ActionRound1;
            state.FindPlayer(1).Resources = wallet ?? (zero ? new ResourceSet() : new ResourceSet { GoldVoucher = 20 });
            state.FindPlayer(1).Score = 7; state.FindPlayer(1).RemainingMainActionsThisTurn = 0;
            string path = scriptPath ?? (frontier ? "lua/city_styles/city_style_city_construction_frontier.special.lua" : "lua/enterprises/enterprise.raythean.level_5.special.lua");
            string source = installed.GetScript(path), ability = frontier ? "city_style_city_construction_frontier.special" : "enterprise.raythean.level_5.special";
            var dispatcher = new LuaEffectEventDispatcher(registry);
            string continuation = frontier ? "test.deck.completed" : "";
            var definition = new LuaScriptDefinition("test.deck.formal", ability, "test.deck.primary", "1.0.0", source, LuaContentHasher.ComputeSha256(source));
            if (frontier) dispatcher.RegisterContinuation(new LuaHandlerDefinition(continuation, "EffectCompleted", "",
                new LuaScriptDefinition(definition.ContentId, ability, continuation, "1.0.0", source, definition.ContentHash), EffectHandlerRole.Continuation, abilityId: ability));
            dispatcher.Register(new LuaHandlerDefinition("test.deck.primary", "TestDeckActivated", "", definition, EffectHandlerRole.Primary, abilityId: ability, completionHandlerId: continuation));
            registry.Register("test.deck.activate", c => EffectStepResult.Completed().AddEvent(new EffectEventRequest { EventType = "TestDeckActivated", PlayerId = 1 }));
            if (crossPlayer) registry.RegisterEventHandler(new EffectEventHandlerRegistration("test.deck.required", "BeforeResourcePayment", "*", "test.deck.required",
                c => new[] { EffectSpec.Create(LuaDomainEffectTypeIds.Choice, Object("player", NormalizedValue.CreateString("2"),
                    "options", Strings(new[] { "required" }), "minSelections", NormalizedValue.CreateInteger(1), "maxSelections", NormalizedValue.CreateInteger(1)), 2) }));
            if (passenger)
            {
                CharacterCoverEffectExecutor.Register(registry); CharacterLifecycleService.Register(registry);
                var card = state.ItemZones.Items.First(item => item.DefinitionId == "operator.passenger");
                card.ControllerSubject = ItemSubjectRef.Player(1); card.OwnerSubject = ItemSubjectRef.Player(1);
                typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { state, card, FindZone(state, "hand", 1).ZoneId });
                Run(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(NormalizedValue.CreateString(card.ItemId));
                Run(CharacterLifecycleService.CreateUseSpec(1, card.ItemId, "tactic")); Answer(NormalizedValue.CreateString("3"));
            }
            if (mainWindow) OpenUnrelatedMainActionWindow();
            root = Run(EffectSpec.Create("test.deck.activate", playerId: 1)).EffectId;
            if (scriptPath != null)
                for (int step = 0; step < 4 && !state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == FacilitySelectionEffectExecutor.TypeId); step++)
                {
                    Assert.That(Open().CandidateIds, Does.Contain("continue"), "前置支付必须沿真实条件确认。");
                    Answer(NormalizedValue.CreateString("continue"));
                }
            selection = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == FacilitySelectionEffectExecutor.TypeId).EffectId;
        }
        private EffectNodeRuntimeState Run(EffectSpec spec)
        {
            var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); return engine.GetNode(id);
        }
        private InteractionRequest Open() => state.EffectRuntime.InteractionRequests.Last(q => q.Status == "open");
        private EffectNodeRuntimeState Parent => state.EffectRuntime.EffectNodes.Single(n => n.EffectId == selection);
        private EffectNodeRuntimeState Payment => state.EffectRuntime.EffectNodes.Last(n => n.Payment?.Preparation?.AuthorizationKind == "effect_build");
        private string Target => state.ItemZones.Items.First(i => Open().CandidateIds.Contains(i.ItemId) && FacilityCardDatabase.Get(state, i.DefinitionId).EffectId == "gain_iron_4").ItemId;
        private void Prepare(string target, string mode = "gold")
        {
            var q = Open(); var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TryPrepareEffectBuildPayment(1, selection, q.GetStableInteractionId(), q.CandidateSetId, q.CandidateSetVersion, target,
                Object("paymentMode", NormalizedValue.CreateString(mode), "expectedSourceZoneId", NormalizedValue.CreateString(state.ItemZones.Items.Single(i => i.ItemId == target).ZoneId)),
                state.EffectRuntime.StateRevision, Guid.NewGuid().ToString("N"), out string reason), Is.True, reason);
        }
        private void Answer(NormalizedValue value)
        {
            var q = Open(); var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(q.GetStableInteractionId(), q.AnsweringPlayerId, q.StateRevision, value, out string reason, out var code), Is.True, reason);
            Assert.That(code, Is.EqualTo(CommandErrorCode.None));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
        private void Restore()
        {
            var restored = artifacts.Recover(state, "正式Lua付款与建设冷恢复", registry);
            Assert.That(restored.Succeeded, Is.True, restored.Diagnostic); state = restored.State;
        }
        private int Shuffles => state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == LuaDomainEffectTypeIds.ShuffleFacilityDeck && n.Status == EffectNodeStatus.Completed);

        [Test] public void DeckOpensAllRealInstancesAtZeroWalletWithoutMainActionBudget()
        {
            Start(zero: true); var q = Open();
            Assert.That(q.InteractionTypeId, Is.EqualTo("facility.build.normal"));
            Assert.That(q.CandidateIds, Is.EqualTo(state.ItemZones.Items.Where(i => i.ZoneId == FindZone(state, "facility_deck").ZoneId).Select(i => i.ItemId).OrderBy(id => id, StringComparer.Ordinal)));
            Assert.That(q.PromptParameters.IsValid(), Is.True); Assert.That(Shuffles, Is.Zero);
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 2).Interactions.Any(v => v.InteractionId == q.GetStableInteractionId()), Is.False);
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
            string before = JsonConvert.SerializeObject(state);
            Assert.That(new EffectTreeExecutor(state, registry).TrySubmitInteraction(q.GetStableInteractionId(), 1, q.StateRevision, NormalizedValue.CreateString(Target), out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
        }

        [Test] public void ZeroBalanceCanPrepareAndProjectButCannotPayOrUseStaleAuthority()
        {
            Start(zero: true); var original = Open(); Prepare(Target, "resources");
            Assert.That(original.Status, Is.EqualTo("invalidated"));
            Assert.That(ResourcePaymentPreparationService.IsCurrentPreparation(state, Payment, 1), Is.False);
            var p = GameStateViewProjector.ProjectForPlayer(state, 1).PreparedActionPayments.Single();
            Assert.That(p.AuthorizationKind, Is.EqualTo("effect_build")); Assert.That(p.IsReady, Is.True); Assert.That(p.CanConfirm, Is.False);
            Assert.That(p.SelectionInteractionId, Is.EqualTo(original.GetStableInteractionId())); Assert.That(p.InteractionId, Is.EqualTo(Open().GetStableInteractionId()));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 2).PreparedActionPayments, Is.Empty);
            Restore(); int revision = state.EffectRuntime.StateRevision; var q = Open();
            Assert.That(new EffectTreeExecutor(state, registry).TrySubmitInteraction(q.GetStableInteractionId(), 1, q.StateRevision, NormalizedValue.CreateString("pay"), out _), Is.False);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision)); Assert.That(Shuffles, Is.Zero);
            Assert.That(new EffectTreeExecutor(state, registry).TrySubmitInteraction(original.GetStableInteractionId(), 1, revision, NormalizedValue.CreateBoolean(false), out _), Is.False);
            Assert.That(new EffectTreeExecutor(state, registry).TryCancelPreparedEffectBuildPayment(2, Payment.EffectId, revision, "wrong-player", out _), Is.False);
        }

        [Test] public void CancelPreparationKeepsGrantAndScoreWhileDeclineEndsOnce()
        {
            Start(frontier: true); string oldInput = Open().GetStableInteractionId(), target = Target; Prepare(target);
            Assert.That(state.FindPlayer(1).Score, Is.EqualTo(6));
            Assert.That(new EffectTreeExecutor(state, registry).TryCancelPreparedEffectBuildPayment(1, Payment.EffectId, state.EffectRuntime.StateRevision, "cancel-preparation", out string reason), Is.True, reason);
            Assert.That(Open().InteractionTypeId, Is.EqualTo("facility.build.normal")); Assert.That(Open().GetStableInteractionId(), Is.Not.EqualTo(oldInput));
            Assert.That(Shuffles, Is.Zero); Assert.That(Parent.Status, Is.EqualTo(EffectNodeStatus.Blocked)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(6));
            Prepare(target); Answer(NormalizedValue.CreateString("decline"));
            Assert.That(Shuffles, Is.EqualTo(1)); Assert.That(Parent.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(state.FindPlayer(1).Score, Is.EqualTo(6));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build), Is.False);
        }

        [TestCase(false)] [TestCase(true)] public void FormalBuildPaysOncePlacesOnceAndKeepsOriginalShuffle(bool frontier)
        {
            Start(frontier); string target = Target; int price = FacilityCardDatabase.Get(state, state.ItemZones.Items.Single(i => i.ItemId == target).DefinitionId).GoldVoucherCost;
            var supplies = state.ItemZones.Items.Where(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.facility_supply_slot")).Select(i => i.ItemId).ToArray();
            Prepare(target); Restore(); var paidInput = Open(); Answer(NormalizedValue.CreateString("pay"));
            Assert.That(Open().InteractionTypeId, Is.EqualTo(FacilityBuildEffectExecutor.PlacementInteractionType));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(20 - price)); Assert.That(Shuffles, Is.Zero);
            Assert.That(new EffectTreeExecutor(state, registry).TryCancelPreparedEffectBuildPayment(1, Payment.EffectId,
                state.EffectRuntime.StateRevision, "cannot-cancel-paid", out _), Is.False);
            var build = state.EffectRuntime.EffectNodes.Single(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build);
            Assert.That(Flag(build.NormalizedArguments, "consumeMainAction"), Is.False); Assert.That(Flag(build.NormalizedArguments, "reserveForFree"), Is.False);
            foreach (string forged in new[] { "consumeMainAction", "reserveForFree" })
            {
                var copy = GameStateCloneService.DeepClone(state);
                copy.EffectRuntime.EffectNodes.Single(n => n.EffectId == build.EffectId).NormalizedArguments.Properties.Single(p => p.Name == forged).Value = NormalizedValue.CreateBoolean(true);
                Assert.That(ResourcePaymentSubstitutionEffectExecutor.TryValidatePaymentRecords(copy, out _), Is.False, forged);
            }
            Restore(); Answer(NormalizedValue.CreateString(Open().CandidateIds.First()));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(20 - price)); Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
            Assert.That(state.ItemZones.Zones.Single(z => z.ZoneId == state.ItemZones.Items.Single(i => i.ItemId == target).ZoneId).TemplateId, Is.EqualTo("core.zone.city_facility_cell"));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build), Is.EqualTo(1));
            Assert.That(state.ItemZones.Items.Where(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.facility_supply_slot")).Select(i => i.ItemId), Is.EqualTo(supplies));
            Assert.That(Shuffles, Is.EqualTo(1)); Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == root).Status, Is.EqualTo(EffectNodeStatus.Completed));
            int revision = state.EffectRuntime.StateRevision;
            Assert.That(new EffectTreeExecutor(state, registry).TrySubmitInteraction(paidInput.GetStableInteractionId(), 1, revision, NormalizedValue.CreateString("pay"), out _), Is.False);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision));
        }

        [Test] public void CrossPlayerRequiredResponseHasNoOpenParentInputAndRestoresBeforePay()
        {
            Start(crossPlayer: true); string target = Target; Prepare(target);
            Assert.That(state.EffectRuntime.InteractionRequests.Where(q => q.Status == "open" && !q.IsInternalMainlineInteraction()).Select(q => q.AnsweringPlayerId), Is.EqualTo(new[] { 2 }));
            var p = GameStateViewProjector.ProjectForPlayer(state, 1).PreparedActionPayments.Single(); Assert.That(p.IsReady, Is.False); Assert.That(p.InteractionId, Is.Empty); Assert.That(p.CanCancel, Is.False);
            Assert.That(new EffectTreeExecutor(state, registry).TryCancelPreparedEffectBuildPayment(1, Payment.EffectId, state.EffectRuntime.StateRevision, "cannot-cancel-required", out _), Is.False);
            Restore(); Answer(NormalizedValue.CreateString("required"));
            Assert.That(Open().AnsweringPlayerId, Is.EqualTo(1)); Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1, registry).PreparedActionPayments.Single().CanConfirm, Is.True);
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "BeforeResourcePayment"), Is.EqualTo(1)); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(20));
        }

        [Test] public void UnknownOrPartialAuthorizationIsRejectedAndMissingFieldDefaultsToMainAction()
        {
            Start(); Prepare(Target);
            foreach (string corruption in new[] { "unknown", "partial", "version", "main_partial" })
            {
                var copy = GameStateCloneService.DeepClone(state); var p = copy.EffectRuntime.EffectNodes.Single(n => n.EffectId == Payment.EffectId).Payment.Preparation;
                if (corruption == "unknown") p.AuthorizationKind = "unknown";
                if (corruption == "partial") p.SelectionInteractionId = "";
                if (corruption == "version") p.SelectionCandidateSetVersion++;
                if (corruption == "main_partial") p.AuthorizationKind = "main_action";
                Assert.That(ResourcePaymentSubstitutionEffectExecutor.TryValidatePaymentRecords(copy, out _), Is.False, corruption);
                Assert.That(ResourcePaymentPreparationService.IsCurrentEffectBuildPreparation(copy, copy.EffectRuntime.EffectNodes.Single(n => n.EffectId == Payment.EffectId), 1), Is.False, corruption);
            }
            Assert.That(JsonConvert.DeserializeObject<PreparedActionPaymentContext>("{}").AuthorizationKind, Is.EqualTo("main_action"));
            var faulted = GameStateCloneService.DeepClone(state); faulted.EffectRuntime.Status = EffectRuntimeStatus.PausedFault;
            Assert.That(GameStateViewProjector.ProjectForPlayer(faulted, 1).PreparedActionPayments, Is.Empty);
        }

        [Test] public void PreparationRejectsForgedAuthorityAndFinalSlotChangesWithoutMutation()
        {
            Start(zero: true); var input = Open(); string target = Target;
            var args = Object("paymentMode", NormalizedValue.CreateString("resources"),
                "expectedSourceZoneId", NormalizedValue.CreateString(FindZone(state, "facility_deck").ZoneId));
            var baseline = state;
            foreach (string invalid in new[] { "player", "parent", "input", "set", "version", "target", "source", "mode", "extra", "revision" })
            {
                state = GameStateCloneService.DeepClone(baseline); var arguments = args.Clone();
                if (invalid == "source") arguments.Properties.Single(p => p.Name == "expectedSourceZoneId").Value = NormalizedValue.CreateString(FindZone(state, "hand", 1).ZoneId);
                if (invalid == "mode") arguments.Properties.Single(p => p.Name == "paymentMode").Value = NormalizedValue.CreateString("free");
                if (invalid == "extra") arguments.Properties.Add(new NormalizedValueEntry { Name = "cityBoardSlotIndex", Value = NormalizedValue.CreateInteger(0) });
                string before = JsonConvert.SerializeObject(state);
                Assert.That(new EffectTreeExecutor(state, registry).TryPrepareEffectBuildPayment(invalid == "player" ? 2 : 1,
                    invalid == "parent" ? root : selection, invalid == "input" ? "stale" : input.GetStableInteractionId(),
                    invalid == "set" ? "forged" : input.CandidateSetId, input.CandidateSetVersion + (invalid == "version" ? 1 : 0),
                    invalid == "target" ? "foreign-card" : target, arguments,
                    state.EffectRuntime.StateRevision - (invalid == "revision" ? 1 : 0), "invalid." + invalid, out _), Is.False, invalid);
                Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before), invalid);
            }
            state = GameStateCloneService.DeepClone(baseline); Prepare(target, "resources");
            string unchanged = JsonConvert.SerializeObject(state);
            Assert.That(new EffectTreeExecutor(state, registry).TryCancelPreparedActionPayment(1, Payment.EffectId,
                state.EffectRuntime.StateRevision, "ordinary-cleanup", out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(unchanged));
            var slots = state.ItemZones.Zones.Where(z => z.TemplateId == "core.zone.city_facility_cell" && z.OwnerPlayerId == 1).ToList();
            var fillers = state.ItemZones.Items.Where(i => input.CandidateIds.Contains(i.ItemId) && i.ItemId != target).Take(slots.Count).ToList();
            Assert.That(fillers.Count, Is.EqualTo(slots.Count));
            for (int i = 0; i < slots.Count; i++)
            {
                fillers[i].ControllerSubject = ItemSubjectRef.Player(1); fillers[i].OwnerSubject = ItemSubjectRef.Player(1);
                typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { state, fillers[i], slots[i].ZoneId });
            }
            Assert.That(BuildFacilityService.FindFirstEmptyCityBoardSlot(state, 1), Is.EqualTo(-1));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1).PreparedActionPayments.Single().CanConfirm, Is.False);
            unchanged = JsonConvert.SerializeObject(state); var pay = Open();
            Assert.That(new EffectTreeExecutor(state, registry).TrySubmitInteraction(pay.GetStableInteractionId(), 1, pay.StateRevision,
                NormalizedValue.CreateString("pay"), out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(unchanged));
        }

        [Test] public void SubstitutionEditsOriginalPayInputThenPaysAndPlacesOnlyOnce()
        {
            Start(passenger: true);
            string target = Target;
            var definition = FacilityCardDatabase.Get(state, state.ItemZones.Items.Single(i => i.ItemId == target).DefinitionId);
            var cost = new FacilityBuildCostService().GetEffectiveResourceCost(state, state.FindPlayer(1), definition);
            var type = ResourcePaymentSubstitutionEffectExecutor.BasicTypes.First(t => cost.Get(t) > 0);
            state.FindPlayer(1).Resources = cost.Clone(); state.FindPlayer(1).Resources.Set(type, cost.Get(type) - 1);
            Prepare(target, "resources"); var oldPay = GameStateCloneService.DeepClone(Open()); string ownerId = Payment.EffectId;
            var zone = state.ItemZones.Zones.Single(z => ResourcePaymentSubstitutionEffectExecutor.FindEligibleZone(state, 1, z.ZoneId) != null);
            Func<int> markers = () => ResourcePaymentSubstitutionEffectExecutor.MarkerIds(state,
                state.ItemZones.Zones.Single(z => z.ZoneId == zone.ZoneId), 1).Count;
            Assert.That(markers(), Is.EqualTo(3));
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1).PreparedActionPayments.Single().CanConfirm, Is.False);
            var engine = new EffectTreeExecutor(state, registry);
            Assert.That(Payment.Payment.PaymentProtocolVersion, Is.EqualTo(2));
            var rule = ResourcePaymentSourceService.Query(state, registry, Payment, Payment.Payment.PaymentCost)
                .Single(r => r.SourceRef.AbilityId == "operator.passenger.payment");
            Assert.That(engine.TryEditPaymentSource(1, ownerId, oldPay.GetStableInteractionId(), "begin_source_edit", rule.SourceRef.SourceKey,
                rule.RuleId, "", Payment.Payment.PaymentGeneration, 0, new ResourceSet(), Array.Empty<string>(),
                state.EffectRuntime.StateRevision, "deck.source.begin", out string reason), Is.True, reason);
            Assert.That(Open().OwnerEffectId, Is.EqualTo(ownerId)); Assert.That(Payment.Payment.IsCommitted, Is.False);
            Assert.That(Payment.Payment.SourceEdits.Single(e => e.Status == "open").SourceKey, Is.EqualTo(rule.SourceRef.SourceKey));
            Assert.That(markers(), Is.EqualTo(3));
            Restore();
            var allocation = new ResourceSet(); allocation.Set(type, 1);
            var edit = Payment.Payment.SourceEdits.Single(e => e.Status == "open");
            Assert.That(new EffectTreeExecutor(state, registry).TryEditPaymentSource(1, ownerId, Open().GetStableInteractionId(), "save_source",
                rule.SourceRef.SourceKey, rule.RuleId, edit.EditId, Payment.Payment.PaymentGeneration, edit.EnteredDraftRevision, allocation,
                Array.Empty<string>(), state.EffectRuntime.StateRevision, "deck.source.save", out reason), Is.True, reason);
            var draft = Payment.Payment.SourceDrafts.Single(); Assert.That(draft.SelectedItemIds.Count, Is.EqualTo(1));
            Assert.That(draft.Rule.SourceRef.AbilityId, Is.EqualTo("operator.passenger.payment"));
            var newPay = Open(); Assert.That(newPay.OwnerEffectId, Is.EqualTo(ownerId));
            Assert.That(newPay.InteractionTypeId, Is.EqualTo(ResourceEffectExecutor.PaymentConfirmationInteractionTypeId));
            Assert.That(newPay.GetStableInteractionId(), Is.EqualTo(oldPay.GetStableInteractionId()));
            Assert.That(newPay.StateRevision, Is.GreaterThan(oldPay.StateRevision));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(q => q.OwnerEffectId == ownerId && q.InteractionTypeId == ResourcePaymentSubstitutionEffectExecutor.InteractionTypeId), Is.False);
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1, registry).PreparedActionPayments.Single().CanConfirm, Is.True);
            Assert.That(Payment.Payment.IsCommitRequested, Is.False); Assert.That(markers(), Is.EqualTo(3));
            int revision = state.EffectRuntime.StateRevision;
            Assert.That(new EffectTreeExecutor(state, registry).TrySubmitInteraction(oldPay.GetStableInteractionId(), 1, oldPay.StateRevision,
                NormalizedValue.CreateString("pay"), out _), Is.False);
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision));
            Restore(); Answer(NormalizedValue.CreateStableReference("candidate", "pay"));
            Assert.That(Open().InteractionTypeId, Is.EqualTo(FacilityBuildEffectExecutor.PlacementInteractionType));
            Assert.That(Payment.Payment.IsCommitted, Is.True); Assert.That(markers(), Is.EqualTo(2));
            var receipt = Payment.Payment.SourceReceipts.Single();
            Assert.That(receipt.ProtocolVersion, Is.EqualTo(2)); Assert.That(receipt.ItemIds, Is.EqualTo(draft.SelectedItemIds));
            Assert.That(Number(receipt.TokenArguments, "requiredCount"), Is.EqualTo(1));
            Assert.That(receipt.TokenTransfer.Request.Transfers.SelectMany(t => t.ItemIds), Is.EqualTo(receipt.ItemIds));
            Assert.That(ItemTransferService.TryValidateTokenCommit(state, 1, receipt.TokenArguments, receipt.TokenTransfer, out reason), Is.True, reason);
            foreach (var resource in Enum.GetValues(typeof(ResourceType)).Cast<ResourceType>()) Assert.That(state.FindPlayer(1).Resources.Get(resource), Is.Zero);
            Restore(); Answer(NormalizedValue.CreateString(Open().CandidateIds.First()));
            Assert.That(markers(), Is.EqualTo(2)); Assert.That(Shuffles, Is.EqualTo(1));
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build), Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "BeforeResourcePayment" && e.SourceEffectId == ownerId), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == "InteractionAnswered" && Text(e.Payload, "requestId") == newPay.GetStableInteractionId()), Is.EqualTo(1));
        }

        [Test] public void SupplyBuildOpensNormalPanelWithoutBalanceFiltering()
        {
            Start(); Answer(NormalizedValue.CreateBoolean(false));
            state.FindPlayer(1).Resources = new ResourceSet();
            Run(EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId, playerId: 1));
            Assert.That(Open().InteractionTypeId, Is.EqualTo("facility.build.normal"));
            Assert.That(Open().PromptKey, Is.EqualTo("facility.entry.choose_additional_build"));
            Assert.That(Open().CandidateIds.Count, Is.GreaterThan(0));
            Assert.That(Text(Open().PromptParameters, "sourceZone"), Is.EqualTo("supply"));
        }

        private GameSession EffectBuildSession()
        {
            var session = new GameSession(state);
            session.RegisterHandler(new PreparePaymentDraftCommandHandler(registry));
            session.RegisterHandler(new AnswerInteractionCommandHandler(registry));
            return session;
        }

        [TestCase("enterprises/enterprise.raythean.level_3.special.lua", 2, 0, true)]
        [TestCase("departments/enterprise.department.engineering.level_4.reward.lua", 0, 3, true)]
        [TestCase("characters/operator.mudrock.strategy.lua", 0, 1, true)]
        [TestCase("facilities/build_additional_facility.lua", 0, 0, true)]
        [TestCase("city_styles/infected.city_style.emerging_buildings.declared.lua", 0, 0, true)]
        public void FormalSupplyGrantsOpenNormalBuildAndDeclineWithoutRefund(string script, int shards, int rocks, bool canDecline)
        {
            Start(scriptPath: "lua/" + script, wallet: new ResourceSet { OriginiumShard = shards, Originium = rocks });
            var request = Open();
            Assert.That(request.InteractionTypeId, Is.EqualTo(FacilitySelectionEffectExecutor.NormalBuildInteractionType));
            Assert.That(Text(request.PromptParameters, "sourceZone"), Is.EqualTo("supply"));
            Assert.That(request.AllowDecline, Is.EqualTo(canDecline));
            Assert.That(request.CandidateIds.Count, Is.GreaterThan(0));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.Zero); Assert.That(state.FindPlayer(1).Resources.Originium, Is.Zero);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
            Assert.That(state.EffectRuntime.InteractionRequests.Any(q => q.Status == "open" && q.PromptKey == "effect.build.choose_payment"), Is.False);
            if (script.Contains("emerging_buildings"))
                Assert.That(request.CandidateIds.All(id => FacilityCardDatabase.Get(state, state.ItemZones.Items.Single(i => i.ItemId == id).DefinitionId).Color != "rainbow"), Is.True);
            var session = EffectBuildSession();
            var command = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = 1 };
            command.Parameters["interactionId"] = request.GetStableInteractionId(); command.Parameters["expectedRevision"] = request.StateRevision.ToString();
            command.Parameters["answerValue"] = "false";
            var result = session.Submit(command); Assert.That(result.Succeeded, Is.True, result.LogMessage);
            Assert.That(Parent.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Parent.NormalizedResult.StringValue, Is.EqualTo("player_declined"));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.Zero); Assert.That(state.FindPlayer(1).Resources.Originium, Is.Zero);
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
        }

        private GameCommand SupplyPrepareCommand(string target, string mode = "gold")
        {
            var q = Open();
            return new GameCommand { Kind = GameCommandKind.PreparePaymentDraft, PlayerId = 1, TargetId = target,
                Parameters = new Dictionary<string, string> { { "operation", "prepare_effect_build" }, { "selectionOwnerEffectId", selection },
                    { "selectionInteractionId", q.GetStableInteractionId() }, { "selectionCandidateSetId", q.CandidateSetId },
                    { "selectionCandidateSetVersion", q.CandidateSetVersion.ToString() }, { "expectedRevision", state.EffectRuntime.StateRevision.ToString() },
                    { "paymentMode", mode }, { "expectedSourceZoneId", state.ItemZones.Items.Single(i => i.ItemId == target).ZoneId } } };
        }

        private void SupplySessionAnswer(GameSession session, string value)
        {
            var request = Open();
            var result = session.Submit(new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = request.AnsweringPlayerId,
                OptionIds = new List<string> { value }, Parameters = new Dictionary<string, string> {
                    { "interactionId", request.GetStableInteractionId() }, { "expectedRevision", request.StateRevision.ToString() } } });
            Assert.That(result.Succeeded, Is.True, result.LogMessage);
        }

        [Test]
        public void SupplySessionKeepsPrepaymentAcrossCancelAndPaysRefillsOnlyChosenSlot()
        {
            Start(scriptPath: "lua/enterprises/enterprise.raythean.level_3.special.lua", wallet: new ResourceSet { OriginiumShard = 2, GoldVoucher = 40 });
            var session = EffectBuildSession(); string target = Target;
            var originalSlots = state.ItemZones.Items.Where(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.facility_supply_slot"))
                .ToDictionary(i => i.ZoneId, i => i.ItemId);
            var prepare = SupplyPrepareCommand(target); string source = prepare.Parameters["expectedSourceZoneId"];
            foreach (string bad in new[] { "zone", "revision", "player", "candidate" }) {
                var forged = SupplyPrepareCommand(target);
                if (bad == "zone") forged.Parameters["expectedSourceZoneId"] = originalSlots.Keys.First(z => z != source);
                if (bad == "revision") forged.Parameters["expectedRevision"] = (state.EffectRuntime.StateRevision - 1).ToString();
                if (bad == "player") forged.PlayerId = 2;
                if (bad == "candidate") forged.Parameters["selectionCandidateSetVersion"] = "99";
                string before = JsonConvert.SerializeObject(state);
                Assert.That(session.Submit(forged).Succeeded, Is.False, bad); Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before), bad);
            }
            var result = session.Submit(prepare); Assert.That(result.Succeeded, Is.True, result.LogMessage);
            Assert.That(GameStateViewProjector.ProjectForPlayer(state, 1).PreparedActionPayments.Single().TargetId, Is.EqualTo(target));
            Restore(); session = EffectBuildSession();
            result = session.Submit(EffectBuildCancelCommand(Payment.EffectId)); Assert.That(result.Succeeded, Is.True, result.LogMessage);
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.Zero); Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(40));
            result = session.Submit(SupplyPrepareCommand(target)); Assert.That(result.Succeeded, Is.True, result.LogMessage);
            int price = FacilityCardDatabase.Get(state, state.ItemZones.Items.Single(i => i.ItemId == target).DefinitionId).GoldVoucherCost;
            var pay = Open(); SupplySessionAnswer(session, "pay");
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(40 - price));
            Assert.That(Open().InteractionTypeId, Is.EqualTo(FacilityBuildEffectExecutor.PlacementInteractionType));
            Assert.That(new EffectTreeExecutor(state, registry).TryCancelPreparedEffectBuildPayment(1, Payment.EffectId, state.EffectRuntime.StateRevision, "paid.cancel", out _), Is.False);
            Restore(); session = EffectBuildSession(); SupplySessionAnswer(session, Open().CandidateIds.First());
            Assert.That(Parent.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(Text(Parent.NormalizedResult, "builtItemId"), Is.EqualTo(target));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(40 - price)); Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.Zero);
            Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
            Assert.That(state.EffectRuntime.EffectNodes.Count(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build), Is.EqualTo(1));
            foreach (var pair in originalSlots.Where(p => p.Key != source)) Assert.That(state.ItemZones.Items.Single(i => i.ItemId == pair.Value).ZoneId, Is.EqualTo(pair.Key));
            Assert.That(state.ItemZones.Items.Count(i => i.ZoneId == source), Is.EqualTo(1));
            Assert.That(state.ItemZones.Items.Single(i => i.ZoneId == source).ItemId, Is.Not.EqualTo(target));
            string done = JsonConvert.SerializeObject(state);
            Assert.That(new EffectTreeExecutor(state, registry).TrySubmitInteraction(pay.GetStableInteractionId(), 1, state.EffectRuntime.StateRevision, NormalizedValue.CreateString("pay"), out _), Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(done));
        }

        [Test]
        public void SupplyStoredCandidateUsesFormalPolicyAndSurvivesPaidMigration()
        {
            Start(); Answer(NormalizedValue.CreateBoolean(false));
            CharacterCoverEffectExecutor.Register(registry); CharacterLifecycleService.Register(registry);
            var carrier = state.ItemZones.Items.Single(i => i.DefinitionId == "operator.closure");
            Run(MoveSpec(1, new[] { carrier.ItemId }, carrier.ZoneId, FindZone(state, "hand", 1).ZoneId, "unordered", 1, "core.character_card_transfer"));
            Run(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(NormalizedValue.CreateString(carrier.ItemId));
            Run(CharacterLifecycleService.CreateUseSpec(1, carrier.ItemId, "tactic"));
            string held = Target; Answer(NormalizedValue.CreateString(held)); Answer(NormalizedValue.CreateBoolean(false));
            string storedSource = state.ItemZones.Items.Single(i => i.ItemId == held).ZoneId;
            Assert.That(FacilityBuildCandidateQuery.IsActiveStoredFacility(state, state.ItemZones.Zones.Single(z => z.ZoneId == storedSource), 1), Is.True);
            selection = Run(EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId, playerId: 1)).EffectId;
            Assert.That(Open().CandidateIds, Does.Contain(held));
            Assert.That(registry.FacilityBuildCandidates.Resolve(state, 2).FinalItemIds, Does.Not.Contain(held));
            Assert.That(registry.FacilityBuildCandidates.Resolve(state, 1, new FacilityBuildCandidateOptions { IncludeStoredFacilities = false }).FinalItemIds, Does.Not.Contain(held));
            var supplies = state.ItemZones.Items.Where(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.facility_supply_slot")).Select(i => i.ItemId).ToArray();
            Prepare(held); Restore(); Answer(NormalizedValue.CreateString("pay"));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == held).ZoneId, Is.Not.EqualTo(storedSource));
            Assert.That(ResourcePaymentSubstitutionEffectExecutor.TryValidatePaymentRecords(state, out string reason), Is.True, reason);
            Restore(); Answer(NormalizedValue.CreateString(Open().CandidateIds.First()));
            Assert.That(Text(Parent.NormalizedResult, "builtItemId"), Is.EqualTo(held));
            Assert.That(state.ItemZones.Items.Where(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.facility_supply_slot")).Select(i => i.ItemId), Is.EqualTo(supplies));
        }

        [Test]
        public void SupplyPreparationRequiredResponseCannotBeCancelledOrSkipped()
        {
            Start(crossPlayer: true, scriptPath: "lua/facilities/build_additional_facility.lua", zero: true);
            var session = EffectBuildSession(); var result = session.Submit(SupplyPrepareCommand(Target));
            Assert.That(result.Succeeded, Is.True, result.LogMessage);
            var required = Open(); Assert.That(required.AnsweringPlayerId, Is.EqualTo(2));
            var view = GameStateViewProjector.ProjectForPlayer(state, 1).PreparedActionPayments.Single();
            Assert.That(view.IsReady, Is.False); Assert.That(view.CanCancel, Is.False); Assert.That(view.InteractionId, Is.Empty);
            string before = JsonConvert.SerializeObject(state);
            Assert.That(session.Submit(EffectBuildCancelCommand(Payment.EffectId)).Succeeded, Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            var answer = new GameCommand { Kind = GameCommandKind.AnswerInteraction, PlayerId = 2,
                Parameters = new Dictionary<string, string> { { "interactionId", required.GetStableInteractionId() }, { "expectedRevision", required.StateRevision.ToString() } },
                OptionIds = new List<string> { "required" } };
            result = session.Submit(answer); Assert.That(result.Succeeded, Is.True, result.LogMessage);
            var pay = Open(); Assert.That(pay.AllowDecline, Is.True);
            Answer(NormalizedValue.CreateBoolean(false));
            Assert.That(Parent.Status, Is.EqualTo(EffectNodeStatus.Completed)); Assert.That(Payment.Payment.IsCommitted, Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void FreeAndReserveBuildKeepZeroFacilityCostAndSourceSemantics(bool reserve)
        {
            Start(zero: true); Answer(NormalizedValue.CreateBoolean(false));
            selection = Run(EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId,
                reserve ? Object("sourceZone", NormalizedValue.CreateString("reserve"), "effectId", NormalizedValue.CreateString("reserve_extension_hub")) :
                    Object("sourceZone", NormalizedValue.CreateString("supply"), "paymentMode", NormalizedValue.CreateString("free"), "includeStoredFacilities", NormalizedValue.CreateBoolean(false)), 1)).EffectId;
            Assert.That(Open().InteractionTypeId, Is.EqualTo("facility.entry.choice"));
            string chosen = reserve ? Open().CandidateIds.First() : Target;
            var supplies = state.ItemZones.Items.Where(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.facility_supply_slot")).Select(i => i.ItemId).ToArray();
            Answer(NormalizedValue.CreateString(chosen));
            Assert.That(Open().InteractionTypeId, Is.EqualTo(FacilityBuildEffectExecutor.PlacementInteractionType));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(q => q.Status == "open" && q.PromptKey == "effect.build.choose_payment"), Is.False);
            Answer(NormalizedValue.CreateString(Open().CandidateIds.First()));
            Assert.That(Parent.Status, Is.EqualTo(EffectNodeStatus.Completed));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero); Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
            if (reserve) Assert.That(state.ItemZones.Items.Where(i => state.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.facility_supply_slot")).Select(i => i.ItemId), Is.EqualTo(supplies));
        }
        private GameCommand EffectBuildPrepareCommand()
        {
            var request = state.EffectRuntime.InteractionRequests.Last(q => q.OwnerEffectId == selection && q.Status == "open");
            string target = state.ItemZones.Items.First(i => request.CandidateIds.Contains(i.ItemId) && FacilityCardDatabase.Get(state, i.DefinitionId).EffectId == "gain_iron_4").ItemId;
            var command = new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.PreparePaymentDraft, PlayerId = 1, TargetId = target };
            command.Parameters["operation"] = "prepare_effect_build"; command.Parameters["selectionOwnerEffectId"] = selection;
            command.Parameters["selectionInteractionId"] = request.GetStableInteractionId(); command.Parameters["selectionCandidateSetId"] = request.CandidateSetId;
            command.Parameters["selectionCandidateSetVersion"] = request.CandidateSetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters["expectedRevision"] = state.EffectRuntime.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters["paymentMode"] = "resources"; command.Parameters["expectedSourceZoneId"] = FindZone(state, "facility_deck").ZoneId;
            return command;
        }
        private GameCommand EffectBuildCancelCommand(string ownerId)
        {
            var command = new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.PreparePaymentDraft, PlayerId = 1 };
            command.Parameters["operation"] = "cancel_effect_build"; command.Parameters["preparationOwnerEffectId"] = ownerId;
            command.Parameters["expectedRevision"] = state.EffectRuntime.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return command;
        }
        private void OpenUnrelatedMainActionWindow()
        {
            state.Phase = GamePhase.ActionRound1; RepeatEffectExecutor.Register(registry);
            var builder = new RoundMainChainBuilder(); var order = new List<int> { 1, 2, 3, 4 };
            var chain = builder.Build(state.GameId, builder.CreateRoundExecutionId(state.GameId, 1, order), 1, order, 1);
            var window = chain.Nodes.First(n => n.NodeTypeId == RoundMainlineNodeTypeIds.PlayerActionWindow && n.PlayerId == 1);
            foreach (var node in chain.Nodes.Where(n => n.MainlineIndex < window.MainlineIndex)) node.Status = EffectNodeStatus.Completed;
            var runtime = state.EffectRuntime; runtime.CurrentRoundExecutionId = chain.RoundExecutionId; runtime.CurrentRoundNumber = 1;
            runtime.CurrentRoundStartPlayerId = 1; runtime.PlayerOrderSnapshot = order; runtime.FirstMainNodeId = chain.FirstNodeId;
            runtime.MainNodes.AddRange(chain.Nodes); runtime.ActiveMainNodeId = window.NodeId; window.Status = EffectNodeStatus.Ready;
            var validation = new RoundExecutionService(registry).Advance(state);
            Assert.That(validation.IsValid, Is.True, validation.Reason);
            Assert.That(state.EffectRuntime.OperationWindows.Any(w => w.WindowKind == RoundMainlineNodeTypeIds.PlayerActionWindow &&
                w.Lifecycle == OperationWindowLifecycle.Open && w.AvailablePlayerIds.Contains(1)), Is.True);
            state.FindPlayer(1).RemainingMainActionsThisTurn = 0;
        }

        [TestCase(false)] [TestCase(true)] public void EffectBuildSessionCommandsUseTheirRealRequestWindows(bool withMainWindow)
        {
            Start(zero: true, mainWindow: withMainWindow);
            if (!withMainWindow) { state.Phase = GamePhase.ResourceCollection; state.CurrentPlayerId = 2; }
            var command = EffectBuildPrepareCommand(); string originalInput = command.Parameters["selectionInteractionId"];
            string before = JsonConvert.SerializeObject(state), commandBefore = JsonConvert.SerializeObject(command);
            Assert.That(OperationWindowService.TryResolveCommandContext(state, command, out var context, out var validation), Is.True, validation.Reason);
            Assert.That(context.SourceEffectId, Is.EqualTo(selection)); Assert.That(context.WindowKind, Is.EqualTo("effect_input"));
            Assert.That(state.EffectRuntime.OperationWindows.Any(w => w.OperationWindowId == context.OperationWindowId && w.Lifecycle == OperationWindowLifecycle.Open), Is.True);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before)); Assert.That(JsonConvert.SerializeObject(command), Is.EqualTo(commandBefore));
            Assert.That(command.Parameters.ContainsKey("interactionId"), Is.False);
            var session = EffectBuildSession(); var result = session.Submit(command);
            Assert.That(result.Succeeded, Is.True, result.LogMessage);
            var prepared = session.ProjectFor(GameStateViewer.Player(1)).PreparedActionPayments.Single();
            Assert.That(prepared.AuthorizationKind, Is.EqualTo("effect_build")); Assert.That(prepared.IsReady, Is.True);
            Assert.That(prepared.CanConfirm, Is.False); Assert.That(prepared.SelectionInteractionId, Is.EqualTo(originalInput));
            Assert.That(prepared.OperationWindowId, Is.Empty); Assert.That(prepared.WindowGeneration, Is.Zero);
            var cancel = EffectBuildCancelCommand(prepared.PaymentOwnerEffectId);
            Assert.That(OperationWindowService.TryResolveCommandContext(state, cancel, out context, out validation), Is.True, validation.Reason);
            Assert.That(context.SourceEffectId, Is.EqualTo(prepared.PaymentOwnerEffectId)); Assert.That(context.WindowKind, Is.EqualTo("effect_input"));
            result = session.Submit(cancel); Assert.That(result.Succeeded, Is.True, result.LogMessage);
            Assert.That(state.EffectRuntime.InteractionRequests.Last(q => q.OwnerEffectId == selection && q.Status == "open").GetStableInteractionId(), Is.Not.EqualTo(originalInput));
            Assert.That(Shuffles, Is.Zero); Assert.That(state.FindPlayer(1).RemainingMainActionsThisTurn, Is.Zero);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero);
        }

        [Test] public void EffectBuildWindowResolutionRejectsForgedAssociationsWithoutMainFallback()
        {
            Start(zero: true, mainWindow: true); var baseline = state;
            foreach (string invalid in new[] { "player", "owner", "input", "set", "version", "target", "source", "revision", "extraInput", "record" })
            {
                state = GameStateCloneService.DeepClone(baseline); var command = EffectBuildPrepareCommand();
                if (invalid == "player") command.PlayerId = 2;
                if (invalid == "owner") command.Parameters["selectionOwnerEffectId"] = root;
                if (invalid == "input") command.Parameters["selectionInteractionId"] = "stale";
                if (invalid == "set") command.Parameters["selectionCandidateSetId"] = "foreign";
                if (invalid == "version") command.Parameters["selectionCandidateSetVersion"] = "2";
                if (invalid == "target") command.TargetId = "foreign";
                if (invalid == "source") command.Parameters["expectedSourceZoneId"] = FindZone(state, "hand", 1).ZoneId;
                if (invalid == "revision") command.Parameters["expectedRevision"] = (state.EffectRuntime.StateRevision - 1).ToString();
                if (invalid == "extraInput") command.Parameters["interactionId"] = command.Parameters["selectionInteractionId"];
                if (invalid == "record") state.EffectRuntime.CandidateResolutions.Single(r => r.CandidateSetId == command.Parameters["selectionCandidateSetId"]).FinalCandidateIds.Reverse();
                string before = JsonConvert.SerializeObject(state);
                Assert.That(OperationWindowService.TryResolveCommandContext(state, command, out var context, out _), Is.False, invalid);
                Assert.That(context, Is.Null, invalid); Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before), invalid);
                var result = EffectBuildSession().Submit(command); Assert.That(result.Succeeded, Is.False, invalid);
                Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before), invalid);
            }
            state = GameStateCloneService.DeepClone(baseline); var preparedResult = EffectBuildSession().Submit(EffectBuildPrepareCommand());
            Assert.That(preparedResult.Succeeded, Is.True, preparedResult.LogMessage); baseline = state;
            foreach (string invalid in new[] { "player", "owner", "revision", "unknown", "missingInput", "promoted" })
            {
                state = GameStateCloneService.DeepClone(baseline); var command = EffectBuildCancelCommand(Payment.EffectId);
                if (invalid == "player") command.PlayerId = 2;
                if (invalid == "owner") command.Parameters["preparationOwnerEffectId"] = selection;
                if (invalid == "revision") command.Parameters["expectedRevision"] = (state.EffectRuntime.StateRevision - 1).ToString();
                if (invalid == "unknown") Payment.Payment.Preparation.AuthorizationKind = "unknown";
                if (invalid == "missingInput") state.EffectRuntime.InteractionRequests.Last(q => q.OwnerEffectId == Payment.EffectId && q.Status == "open").Status = "invalidated";
                if (invalid == "promoted") Payment.Payment.Preparation.IsPromoted = true;
                string before = JsonConvert.SerializeObject(state);
                Assert.That(OperationWindowService.TryResolveCommandContext(state, command, out var context, out _), Is.False, invalid);
                Assert.That(context, Is.Null, invalid); Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before), invalid);
            }
        }

        [Test] public void EffectBuildCancellationWaitsForRequiredResponseRealInput()
        {
            Start(zero: true, crossPlayer: true); var session = EffectBuildSession();
            var result = session.Submit(EffectBuildPrepareCommand()); Assert.That(result.Succeeded, Is.True, result.LogMessage);
            var cancel = EffectBuildCancelCommand(Payment.EffectId); string before = JsonConvert.SerializeObject(state);
            Assert.That(OperationWindowService.TryResolveCommandContext(state, cancel, out _, out var validation), Is.False);
            Assert.That(validation.ErrorCode, Is.EqualTo(CommandErrorCode.SettlementRequiredPending));
            result = session.Submit(cancel); Assert.That(result.Succeeded, Is.False); Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            var request = Open(); Assert.That(request.AnsweringPlayerId, Is.EqualTo(2));
            var answer = new GameCommand { CommandId = Guid.NewGuid().ToString("N"), Kind = GameCommandKind.AnswerInteraction, PlayerId = 2 };
            answer.Parameters["interactionId"] = request.GetStableInteractionId(); answer.Parameters["expectedRevision"] = request.StateRevision.ToString(); answer.OptionIds.Add("required");
            result = session.Submit(answer); Assert.That(result.Succeeded, Is.True, result.LogMessage);
            cancel = EffectBuildCancelCommand(Payment.EffectId);
            Assert.That(OperationWindowService.TryResolveCommandContext(state, cancel, out var context, out validation), Is.True, validation.Reason);
            Assert.That(context.SourceEffectId, Is.EqualTo(Payment.EffectId));
            result = session.Submit(cancel); Assert.That(result.Succeeded, Is.True, result.LogMessage); Assert.That(Shuffles, Is.Zero);
        }

        [Test] public void PaymentFactsPublishPersistentAuthorizationAndKeepLegacyMainDefault()
        {
            Start(zero: true); Prepare(Target, "resources");
            Assert.That(Text(ResourcePaymentPreparationService.Facts(state, Payment), "authorizationKind"), Is.EqualTo("effect_build"));
            Assert.That(Text(Open().PromptParameters, "authorizationKind"), Is.EqualTo("effect_build"));
            var visible = GameStateViewProjector.ProjectForPlayer(state, 1);
            Assert.That(Text(visible.Interactions.Single(q => q.Status == "open").PromptParameters, "authorizationKind"), Is.EqualTo("effect_build"));
            var owner = GameStateCloneService.DeepClone(Payment);
            owner.Payment.Preparation = JsonConvert.DeserializeObject<PreparedActionPaymentContext>("{}");
            Assert.That(Text(ResourcePaymentPreparationService.Facts(state, owner), "authorizationKind"), Is.EqualTo("main_action"));
            owner.Payment.Preparation.AuthorizationKind = "unknown";
            Assert.That(Text(ResourcePaymentPreparationService.Facts(state, owner), "authorizationKind"), Is.EqualTo("unknown"));
            owner.Payment.Preparation = null;
            Assert.That(Text(ResourcePaymentPreparationService.Facts(state, owner), "preparationOwnerEffectId"), Is.Empty);
        }
    }
}
