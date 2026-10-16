using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.PlayMode.ExpansionRules
{
    /// <summary>复用已交接的实际场景夹具；缺失的感染者内容仅作明确的接口投影验证。</summary>
    public sealed class InfectedGameplayUiTests
    {
        private const BindingFlags Access = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private EnterpriseGameplayUiTests scene;
        private GameState State => (GameState)Get(scene, "state");
        private GameStateView View => (GameStateView)Get(scene, "view");
        private Component Hud => (Component)Get(scene, "hud");
        private Component Ui => (Component)Get(scene, "adapter");
        private EffectRegistry Registry => (EffectRegistry)Get(scene, "registry");
        private List<GameCommand> Commands => (List<GameCommand>)Get(scene, "commands");
        private static object Get(object obj, string name) => obj.GetType().GetField(name, Access).GetValue(obj);
        private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Access).SetValue(obj, value);
        private static object Prop(object obj, string name) => obj.GetType().GetProperty(name, Access).GetValue(obj);
        private static object Call(object obj, string name, params object[] args) => obj.GetType().GetMethods(Access)
            .Single(method => method.Name == name && method.GetParameters().Length == args.Length && method.GetParameters()
                .Select((parameter, i) => args[i] == null || parameter.ParameterType.IsByRef || parameter.ParameterType.IsInstanceOfType(args[i])).All(match => match))
            .Invoke(obj, args);
        private static Type Presentation(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("YC.Presentation." + name)).First(type => type != null);
        private void Refresh() { Call(scene, "RefreshView"); Call(scene, "RenderDetails"); }
        private void Route() => Call(scene, "Route");
        private void Run(EffectSpec spec) => Call(scene, "Run", spec);
        private ItemInstanceState[] Cards(string role, int player = -1) => (ItemInstanceState[])Call(scene, "Cards", role, player);
        private void Place(ItemInstanceState item, string zone) => Call(scene, "Place", item, zone);
        private IEnumerable<Component> Rows => ((IEnumerable)Prop(Ui, "Rows")).Cast<Component>();
        private Component Row(string id) => ((IDictionary)Get(Ui, "settlementRows"))[id] as Component ?? Rows.Single(row => (string)Prop(row, "ItemId") == id);
        private void Use(Component row) => ((Button)Prop(row, "StatusButton")).onClick.Invoke();

        [UnitySetUp] public IEnumerator LoadScene()
        {
            scene = new EnterpriseGameplayUiTests();
            yield return scene.LoadActualThreePlayerScene();
            Refresh();
        }
        [UnityTearDown] public IEnumerator ReleaseScene() { yield return scene.ReleaseUi(); scene = null; }

        private CharacterSettlementOption OpenDoubleUseContract()
        {
            var cards = Cards("hand", 1).Take(2).ToArray();
            foreach (var card in cards)
            {
                var definition = CharacterCardDatabase.Get(State, card.DefinitionId);
                foreach (string mode in new[] { "strategy", "tactic" })
                {
                    string ability = mode == "strategy" ? definition.StrategyAbilityId : definition.TacticAbilityId;
                    string subscription = "character.ui_contract." + ability;
                    Registry.CharacterActivationSubscriptions[ability] = subscription;
                    Registry.CharacterAbilityContracts[ability] = new CharacterModeContract { Mode = mode, AbilityId = ability,
                        SkillType = mode == "tactic" ? "persistent" : "normal", TitleKey = "character.double_use.title",
                        SettlementActionId = mode == "tactic" ? CharacterSettlementOptionQuery.DoubleUseActionId : "" };
                    Registry.RegisterEventHandler(new EffectEventHandlerRegistration(subscription, CharacterAbilityEffectExecutor.CharacterEffectActivatedEventType,
                        ability, subscription, context => new List<EffectSpec> { ResourceEffectSpecFactory.GainScore(1, 1) }, EffectHandlerRole.Primary));
                }
            }
            Registry.Register("character.ui_contract.cover", context => CharacterLifecycleService.Cover(context, Text(context.Node.NormalizedArguments, "cardId"), true));
            foreach (var card in cards)
            {
                Run(EffectSpec.Create("character.ui_contract.cover", Object("cardId", NormalizedValue.CreateString(card.ItemId)), 1));
                Run(CharacterLifecycleService.CreateUseSpec(1, card.ItemId, card == cards[0] ? "tactic" : ""));
            }
            Refresh(); Route();
            return View.CharacterSettlementOptions.Single();
        }

        [UnityTest] public IEnumerator CurrentUseBottomBarSubmitsOnceAndRestoresTheSameDoubleUse()
        {
            var option = OpenDoubleUseContract();
            var host = (RectTransform)Get(Ui, "settlementHost");
            Assert.That(host.gameObject.activeInHierarchy, Is.True);
            var row = host.GetComponentsInChildren(Presentation("UiEffectRowView"), false).Cast<Component>().Single();
            Assert.That((string)Prop(row, "ItemId"), Is.EqualTo(option.OptionId));
            Use(row); Use(row);
            Assert.That(Commands.Count, Is.EqualTo(1));
            Assert.That(Commands[0].Parameters["useId"], Is.EqualTo(option.TargetUseId));
            Assert.That(Commands[0].Kind, Is.EqualTo(GameCommandKind.InvokeSettlementOption));
            int useCount = State.CharacterUses.Count;
            var result = new InvokeSettlementOptionCommandHandler(Registry).Handle(State, Commands[0]);
            Assert.That(result.Succeeded, Is.True, result.LogMessage);
            var use = State.CharacterUses.Single(value => value.UseId == option.TargetUseId);
            Assert.That(use.SourceConsumed && use.DoubleUseGranted, Is.True);
            Assert.That(State.CharacterUses.Count, Is.EqualTo(useCount));
            Assert.That(State.ItemZones.Items.Single(item => item.ItemId == option.SourceItemId).ZoneId, Is.EqualTo(FindZone(State, "discard", 1).ZoneId));
            var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(State)), Registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic);
            Set(scene, "state", recovered.State); Call(Ui, "NotifyCommandSettled"); Refresh(); Route();
            Assert.That(State.CharacterUses.Single(value => value.UseId == option.TargetUseId).SourceConsumed, Is.True);
            Assert.That(RoundTrip(View).CharacterUses.Single(value => value.UseId == option.TargetUseId).SourceConsumed, Is.True);
            var repeated = EffectInteractionCommands.InvokeSettlementOption(1, option.OptionId, option.TargetUseId, option.StateRevision);
            Assert.That(new InvokeSettlementOptionCommandHandler(Registry).Handle(State, repeated).Succeeded, Is.True);
            Assert.That(State.CharacterUses.Count, Is.EqualTo(useCount));
            yield return Capture("当前角色双发与恢复-接口合同");
        }

        [UnityTest] public IEnumerator StaleOrTransferredUseCannotInvokeAndHotseatClearsBottomBar()
        {
            var option = OpenDoubleUseContract();
            var snapshot = SettlementOptionView.FromOption(option);
            State.EffectRuntime.StateRevision++; Refresh();
            Call(Ui, "InvokeSettlement", snapshot); Assert.That(Commands, Is.Empty);
            var current = View.CharacterSettlementOptions.Single();
            var use = State.CharacterUses.Single(value => value.UseId == current.TargetUseId);
            var item = State.ItemZones.Items.Single(value => value.ItemId == use.CardInstanceId);
            // 受控位置变化验证旧页面失效；真实刻俄柏转交生命周期由015验收。
            Place(item, FindZone(State, "hand", 2).ZoneId); item.ControllerSubject = new ItemSubjectRef { Kind = ItemSubjectKind.Player, PlayerId = 2 };
            State.EffectRuntime.StateRevision++; Refresh(); Route();
            Call(Ui, "InvokeSettlement", SettlementOptionView.FromOption(current)); Assert.That(Commands, Is.Empty);
            Assert.That(((RectTransform)Get(Ui, "settlementHost")).gameObject.activeSelf, Is.False);
            Set(scene, "viewer", 2); Refresh(); Route();
            Assert.That(View.CharacterSettlementOptions, Is.Empty);
            Assert.That(((RectTransform)Get(Ui, "settlementHost")).gameObject.activeSelf, Is.False);
            yield return null;
        }

        [UnityTest] public IEnumerator BindingProjectionDisplaysHostCostAndRemovesRetiredDetails()
        {
            var character = Cards("hand", 1)[0];
            Place(character, FindZone(State, "persistent", 1).ZoneId);
            var facility = State.ItemZones.Items.First(value => value.TypeId == "core.facility_card" && State.ItemZones.Zones.Any(zone => zone.ZoneId == value.ZoneId && zone.TemplateId == "core.zone.facility_supply_slot"));
            State.FacilityCharacterBindings.Add(new FacilityCharacterBindingState { BindingId = "binding-contract", CharacterItemId = character.ItemId,
                FacilityItemId = facility.ItemId, ControllerPlayerId = 1, Active = true });
            Refresh();
            Assert.That(RoundTrip(View).FacilityCharacterBindings.Single().FacilityItemId, Is.EqualTo(facility.ItemId));
            var option = new CharacterSettlementOption { OptionId = "binding-option", OwnerEffectId = "cleanup-contract", InteractionId = "cleanup-choice", CandidateId = character.ItemId,
                SourceItemId = character.ItemId, AnsweringPlayerId = 1, StateRevision = View.StateRevision, CandidateSetVersion = 1,
                RequiredGoldVoucherAmount = 13, TitleKey = "character.finish_facility_binding", VisibleToViewer = true, IsEnabled = false, DisabledReasonCode = "insufficient_gold_voucher" };
            View.CharacterSettlementOptions.Add(option); Call(Ui, "NotifyCommandSettled"); Call(Ui, "Refresh");
            var row = Row(option.OptionId);
            Assert.That(((Button)Prop(row, "StatusButton")).interactable, Is.False); Use(row); Assert.That(Commands, Is.Empty);
            string detail = (string)Call(Ui, "SettlementDescription", SettlementOptionView.FromOption(option));
            Assert.That(detail, Does.Contain(facility.ItemId)); Assert.That(detail, Does.Contain("13"));
            int score = State.FindPlayer(1).Score, vouchers = State.FindPlayer(1).Resources.GoldVoucher;
            var rejection = new InvokeSettlementOptionCommandHandler(Registry).Handle(State, EffectInteractionCommands.InvokeSettlementOption(1, option.OptionId, "", option.StateRevision));
            Assert.That(rejection.Succeeded, Is.False, "未交付真实收尾来源时Host必须拒绝接口夹具Option");
            Assert.That(((RectTransform)Get(Ui, "settlementHost")).gameObject.activeSelf, Is.True);
            yield return Capture("杰西卡费用不足-受控投影");
            option.IsEnabled = true; option.DisabledReasonCode = ""; Call(Ui, "NotifyCommandSettled"); Call(Ui, "Refresh");
            Use(Row(option.OptionId)); Use(Row(option.OptionId)); Assert.That(Commands.Count, Is.EqualTo(1));
            Assert.That(State.FindPlayer(1).Score, Is.EqualTo(score)); Assert.That(State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(vouchers));
            yield return Capture("杰西卡费用足够-受控投影");
            State.FacilityCharacterBindings[0].Active = false; Place(facility, FindZone(State, "retired").ZoneId); State.EffectRuntime.StateRevision++;
            Refresh();
            Assert.That(Rows.Any(value => (string)Prop(value, "ItemId") == "binding-contract" || (string)Prop(value, "ItemId") == option.OptionId), Is.False);
        }

        [UnityTest] public IEnumerator AdditionalGeneratedCoverPreservesOldSlotAndOnlyBlocksNewItem()
        {
            var old = Cards("hand", 1)[0];
            var generated = new ItemInstanceState { ItemId = "generated-contract", DefinitionId = "infected.character.flowing_shape_alpha", TypeId = "core.generated_character_card",
                PhysicalKind = ItemPhysicalKind.Card, OwnerSubject = new ItemSubjectRef { Kind = ItemSubjectKind.Player, PlayerId = 1 },
                ControllerSubject = new ItemSubjectRef { Kind = ItemSubjectKind.Player, PlayerId = 1 } };
            State.ItemZones.Items.Add(generated); Place(generated, FindZone(State, "hand", 1).ZoneId);
            Registry.Register("character.ui_contract.cover", context => CharacterLifecycleService.Cover(context, Text(context.Node.NormalizedArguments, "cardId"), true,
                Number(context.Node.NormalizedArguments, "blocked"), Text(context.Node.NormalizedArguments, "type")));
            Run(EffectSpec.Create("character.ui_contract.cover", Object("cardId", NormalizedValue.CreateString(old.ItemId), "blocked", NormalizedValue.CreateInteger(0), "type", NormalizedValue.CreateString("core.character_card")), 1));
            string oldSlot = State.CharacterCoverSlots.Single().SlotId;
            Run(EffectSpec.Create("character.ui_contract.cover", Object("cardId", NormalizedValue.CreateString(generated.ItemId), "blocked", NormalizedValue.CreateInteger(1), "type", NormalizedValue.CreateString("core.generated_character_card")), 1));
            Refresh(); Assert.That(View.CharacterCoverSlots.Count, Is.EqualTo(2));
            Assert.That(RoundTrip(View).CharacterCoverSlots.Count, Is.EqualTo(2));
            Assert.That(View.CharacterCoverSlots.Single(value => value.SlotId == oldSlot).CardInstanceId, Is.EqualTo(old.ItemId));
            Assert.That(View.Characters.Single(value => value.ItemId == generated.ItemId).DisabledReasonCode, Is.EqualTo("character_instance_blocked"));
            Assert.That(View.Characters.Single(value => value.ItemId == old.ItemId).BlockedActionRoundSequence, Is.EqualTo(0));
            var presenter = Activator.CreateInstance(Presentation("Workflows.CharacterCardPanelPresenter"));
            var model = Call(presenter, "BuildProjectedView", State, 1, View);
            var covered = ((IEnumerable)Prop(model, "CoveredCards")).Cast<object>().Select(card => (string)Prop(card, "CardId")).ToArray();
            Assert.That(covered, Is.EquivalentTo(new[] { old.ItemId, generated.ItemId }));
            var handPanel = Prop(Hud, "CharacterHandPanel"); Call(handPanel, "Render", 1, model);
            Call(handPanel, "OpenCoveredCharacterCardViewer");
            Assert.That((bool)Prop(handPanel, "IsDiscardPreviewOpen"), Is.True);
            Assert.That(Rows.Any(row => (string)Prop(row, "ItemId") == generated.ItemId), Is.False);
            yield return Capture("多盖牌回原盖牌区-接口合同");
            State.ActionRound = 2; State.Phase = GamePhase.ActionRound2; State.EffectRuntime.StateRevision++; Refresh();
            Assert.That(View.Characters.Single(value => value.ItemId == generated.ItemId).DisabledReasonCode, Is.Not.EqualTo("character_instance_blocked"));
            Assert.That(GameStateViewProjector.ProjectForPlayer(State, 2).CharacterCoverSlots, Is.Empty);
        }

        [UnityTest] public IEnumerator RewardRequestUsesExistingRouteAndOnlyAnswersForItsOwner()
        {
            var pool = State.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.reward_pool" && zone.LocalZoneId == "awards");
            State.ItemZones.Items.Add(new ItemInstanceState { ItemId = "award-contract", DefinitionId = "infected.award.enterprise_special",
                TypeId = "core.award_card", PhysicalKind = ItemPhysicalKind.Card, ZoneId = pool.ZoneId });
            Run(EffectSpec.Create(RewardAcquisitionFlow.TypeId, Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString("1"), "poolRole", NormalizedValue.CreateString("awards")), 1)); Route();
            var renderer = Get(scene, "characterRenderer");
            var request = (InteractionRequestProjection)Call(scene, "Projection");
            Assert.That((bool)Call(renderer, "CanRender", request), Is.True);
            Call(renderer, "SubmitCandidates", request, (IReadOnlyList<string>)new[] { "award-contract" });
            Call(renderer, "SubmitCandidates", request, (IReadOnlyList<string>)new[] { "award-contract" });
            Assert.That(Commands.Count, Is.EqualTo(1)); Assert.That(Commands[0].Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
            Assert.That(Commands[0].OptionIds, Is.EqualTo(new[] { "award-contract" }));
            var result = new AnswerInteractionCommandHandler(Registry).Handle(State, Commands[0]);
            Assert.That(result.Succeeded, Is.True, result.LogMessage);
            Assert.That(State.ItemZones.Items.Single(item => item.ItemId == "award-contract").ControllerSubject.PlayerId, Is.EqualTo(1));
            Set(scene, "viewer", 2); Refresh(); Route();
            Assert.That(View.Interactions.All(value => value.CandidateIds.Count == 0), Is.True);
            Assert.That((string)Get(renderer, "renderedInteractionId"), Is.Empty);
            yield return null;
        }

        private void AddMapToken(string id, string type, string definition, string location)
        {
            var parent = State.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.map_location" && zone.LocalZoneId == location);
            var zone = State.ItemZones.Zones.Single(value => value.ParentId == parent.ZoneId && value.TemplateId == "core.zone.map_tokens");
            State.ItemZones.Items.Add(new ItemInstanceState { ItemId = id, DefinitionId = definition, TypeId = type, PhysicalKind = ItemPhysicalKind.Token, ZoneId = zone.ZoneId });
        }
        [UnityTest] public IEnumerator MapTokensPersistAcrossSourceExitAndCompanyUsesWholePairs()
        {
            var map = StaticMapDefinitions.Resolve(State.MapId);
            var region = map.Regions.Single(value => value.RegionId == map.Locations.First(location => location.LocationId == "B-01").RegionId);
            AddMapToken("permanent-token", "infected.gavial_token", "infected.component.gavial_token", region.LocationIds[0]);
            AddMapToken("lock-token", "infected.influence_lock_token", "infected.component.influence_lock_token", region.LocationIds[1]);
            var contractZone = State.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.player_awards" && zone.OwnerPlayerId == 1);
            State.ItemZones.Items.Add(new ItemInstanceState { ItemId = "contract-card", DefinitionId = CompanyInfluenceRules.ContractDefinitionId, TypeId = "core.contract_card",
                PhysicalKind = ItemPhysicalKind.Card, ZoneId = contractZone.ZoneId, ControllerSubject = new ItemSubjectRef { Kind = ItemSubjectKind.Player, PlayerId = 1 } });
            foreach (var location in region.LocationIds.Take(3))
            {
                var parent = State.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.map_location" && zone.LocalZoneId == location);
                var zone = State.ItemZones.Zones.Single(value => value.ParentId == parent.ZoneId && value.TemplateId == "core.zone.influence_slots");
                State.ItemZones.Items.Add(new ItemInstanceState { ItemId = "company-" + location, DefinitionId = "infected.component.company_marker", TypeId = "infected.company_marker",
                    PhysicalKind = ItemPhysicalKind.Cube, ZoneId = zone.ZoneId, SlotId = zone.LegalSlotIds[0], OwnerSubject = new ItemSubjectRef { Kind = ItemSubjectKind.Company, CompanyId = CompanyInfluenceRules.CompanyId, PlayerId = -1 } });
            }
            Refresh();
            var control = View.CompanyControl.Single(value => value.RegionId == region.RegionId && value.PlayerId == 1);
            Assert.That(control.CompanyMarkerCount, Is.EqualTo(3)); Assert.That(control.Contribution, Is.EqualTo(1)); Assert.That(control.Remainder, Is.EqualTo(1));
            var token = View.MapTokens.Single(value => value.ItemId == "permanent-token"); Assert.That(token.IsPermanent, Is.True);
            View.MapTokens.Single(value => value.ItemId == "lock-token").ExpiresBeforeActionRoundSequence = 2;
            Call(Ui, "NotifyCommandSettled"); Call(Ui, "Refresh");
            Call(Prop(Hud, "MainModules"), "ToggleLocalDetail"); yield return Capture("公司奖励与地图指示物-受控投影");
            Assert.That(Rows.Any(row => ((string)Prop(row, "ItemId")).StartsWith("company-control:")), Is.False);
            Call(Ui, "InspectMapToken", "company-" + region.LocationIds[0]);
            Assert.That((IReadOnlyList<string>)Prop(Ui, "InspectedItemIds"), Is.EqualTo(new[] { "company-" + region.LocationIds[0] }));
            var explanation = string.Format((string)Get(Prop(Ui, "Copy"), "CompanyControlFormat"), control.RegionName, 3, 1, 1);
            Assert.That(Hud.GetComponentsInChildren<Text>(true).Any(text => text.text.Contains(explanation)), Is.True);
            yield return Capture("地图公司整对计数-对象详情");
            Call(Ui, "InspectMapToken", "permanent-token"); yield return Capture("永久指示物-对象详情");
            Call(Ui, "InspectMapToken", "lock-token"); yield return Capture("锁定到期-对象详情");
            Place(Cards("hand", 1)[0], FindZone(State, "discard", 1).ZoneId); State.EffectRuntime.StateRevision++; Refresh();
            Assert.That(View.MapTokens.Single(value => value.ItemId == token.ItemId).LocationId, Is.EqualTo(token.LocationId));
            var wire = RoundTrip(View);
            Assert.That(wire.CompanyControl.Single(value => value.RegionId == region.RegionId).Contribution, Is.EqualTo(1));
            Assert.That(wire.MapTokens.Count, Is.EqualTo(5));
            var route = View.ItemZones.Zones.First(value => value.Zone.TemplateId == "core.zone.map_route").Zone;
            var routeRegion = View.ItemZones.Zones.Single(value => value.Zone.ZoneId == route.ParentId).Zone;
            string format = (string)Get(Prop(Ui, "Copy"), "MapCandidateFormat");
            Assert.That((string)Call(Ui, "MapCandidateLabel", route.LocalZoneId), Is.EqualTo(string.Format(format, route.LocalZoneId, routeRegion.LocalZoneId)));
            Assert.That((string)Call(Ui, "MapCandidateLabel", "unknown-map-candidate"), Is.Empty);
        }

        [UnityTest] public IEnumerator SecretRetiredAmiyaStaysHiddenFromEveryViewerAndAfterSerialization()
        {
            var secret = Cards("hand", 1)[0];
            secret.DefinitionId = "infected.character.amiya";
            Place(secret, FindZone(State, "secret_retired").ZoneId);
            foreach (var player in State.Players)
            {
                Set(scene, "viewer", player.PlayerId); Refresh(); Route();
                string json = JsonConvert.SerializeObject(new InitialGameStateViewDto { View = View });
                Assert.That(json, Does.Not.Contain(secret.ItemId)); Assert.That(json, Does.Not.Contain(secret.DefinitionId));
                Assert.That(GameStateViewProtocol.TryReadInitial(json, View.RuleConfiguration, out var restored), Is.True);
                var wire = restored.View;
                Assert.That(wire.ItemZones.Zones.Single(value => value.Zone.LocalZoneId == "secret_retired").ItemCount, Is.EqualTo(1));
                Assert.That(wire.CharacterSettlementOptions, Is.Empty);
                Assert.That(Rows.Any(row => (string)Prop(row, "ItemId") == secret.ItemId), Is.False);
            }
            yield return null;
        }

        private static GameStateView RoundTrip(GameStateView view)
        {
            string json = JsonConvert.SerializeObject(new InitialGameStateViewDto { View = view });
            Assert.That(GameStateViewProtocol.TryReadInitial(json, view.RuleConfiguration, out var restored), Is.True);
            return restored.View;
        }

        [UnityTest] public IEnumerator RepeatProgressUsesHostCountAndResumesWithoutAnExtraIteration()
        {
            // 验证正式Repeat路由；林的真实买卖定义与定价由内容任务及组合验收覆盖。
            var repeat = EffectSpec.Create(LuaDomainEffectTypeIds.Repeat, Object("apiVersion", NormalizedValue.CreateInteger(1),
                "count", NormalizedValue.CreateInteger(10), "allowEarlyStop", NormalizedValue.CreateBoolean(true)), 1);
            repeat.NestedEffectGroups.Add(new List<EffectSpec> { ResourceEffectSpecFactory.GainScore(1, 1) });
            RepeatEffectExecutor.Register(Registry);
            int startingScore = State.FindPlayer(1).Score;
            Run(repeat);
            for (int completed = 0; completed < 10; completed++)
            {
                Refresh(); Route();
                var request = (InteractionRequestProjection)Call(scene, "Projection");
                Assert.That(request.PromptKey, Is.EqualTo("effect.repeat.continue"));
                var wire = RoundTrip(View);
                var node = wire.Effects.Single(value => value.EffectId == request.OwnerEffectId);
                Assert.That(node.RequestedCount, Is.EqualTo(10)); Assert.That(node.CompletedCount, Is.EqualTo(completed));
                string format = (string)Get(Prop(Ui, "Copy"), "RepeatProgressFormat");
                Assert.That((string)Call(Ui, "RequestProgress", request), Does.Contain(string.Format(format, completed, 10)));
                Commands.Clear();
                Call(Get(scene, "characterRenderer"), "SubmitCandidates", request, (IReadOnlyList<string>)new[] { "continue" });
                Assert.That(Commands.Count, Is.EqualTo(1));
                var result = new AnswerInteractionCommandHandler(Registry).Handle(State, Commands[0]);
                Assert.That(result.Succeeded, Is.True, result.LogMessage);
                Call(Get(scene, "characterRenderer"), "NotifyCommandSettled", Commands[0].CommandId);
                if (completed == 3)
                {
                    var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(State)), Registry);
                    Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic); Set(scene, "state", recovered.State);
                }
                Call(Ui, "NotifyCommandSettled");
                yield return null;
            }
            Refresh(); Route();
            Assert.That(State.FindPlayer(1).Score, Is.EqualTo(startingScore + 10));
            Assert.That(View.Interactions.Any(value => value.Status == "open" && value.PromptKey == "effect.repeat.continue"), Is.False);
        }

        private void ScrollDetails(int column, float position)
        {
            var host = (RectTransform)Call(Prop(Hud, "MainModules"), "GetLocalDetailHost", column);
            Canvas.ForceUpdateCanvases(); host.GetComponentInParent<ScrollRect>().verticalNormalizedPosition = position;
        }

        private void ScrollRowIntoView(string id)
        {
            var row = (RectTransform)Row(id).transform;
            var scroll = row.GetComponentInParent<ScrollRect>();
            Canvas.ForceUpdateCanvases();
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.content, row);
            float hidden = scroll.content.rect.height - scroll.viewport.rect.height;
            scroll.verticalNormalizedPosition = hidden <= 0 ? 1 : Mathf.Clamp01(1 - (scroll.content.rect.yMax - bounds.max.y) / hidden);
        }

        [UnityTest] public IEnumerator SmallWindowAndFourPlayerSceneUseTheActualConfiguredHud()
        {
            OpenDoubleUseContract(); yield return Capture("三人小窗口底栏-接口合同", 1024, 768);
            var host = (RectTransform)Get(Ui, "settlementHost");
            var button = host.GetComponentsInChildren<Button>().Last();
            var corners = new Vector3[4]; ((RectTransform)button.transform).GetWorldCorners(corners);
            Assert.That(button.gameObject.activeInHierarchy, Is.True);
            Assert.That(corners.All(point => point.x >= 0 && point.y >= 0 && point.x <= Screen.width && point.y <= Screen.height), Is.True);
            yield return scene.ReleaseUi();
            var launch = Presentation("GameLaunchContext").GetMethod("Ensure").Invoke(null, null);
            var config = new GameRuleConfiguration { PlayerCount = 4, MapId = StaticMapDefinitions.FourPlayerMapId };
            var seats = Enumerable.Range(1, 4).Select(id => new PlayerSeat { PlayerId = id, PlayerName = "合同玩家" + id }).ToList();
            Presentation("GameLaunchContext").GetMethod("Configure", new[] { typeof(LaunchMode), typeof(int), typeof(string), typeof(IList<PlayerSeat>), typeof(GameRuleConfiguration) })
                .Invoke(launch, new object[] { LaunchMode.Local, 1, "", seats, config });
            yield return SceneManager.LoadSceneAsync("SampleScene"); yield return null;
            var actualHud = (Component)UnityEngine.Object.FindObjectOfType(Presentation("GameplayInteractionHudView"));
            var args = new object[] { null }; Assert.That((bool)Call(actualHud, "TryValidateConfiguration", args), Is.True, args[0] as string);
            var actualController = (Component)UnityEngine.Object.FindObjectOfType(Presentation("MobileCityInteractionController"));
            Assert.That((bool)Call(actualHud, "IsBoundTo", actualController), Is.True);
            yield return Capture("四人实际场景-引用合同");
            ((Behaviour)actualController).enabled = false;
            Set(scene, "controller", actualController); Set(scene, "hud", actualHud); Set(scene, "adapter", Prop(actualHud, "ExpansionUi"));
            Set(scene, "state", GameStateCloneService.DeepClone((GameState)Prop(actualController, "CurrentState"))); Set(scene, "viewer", 1);
            Call(scene, "RefreshView");
            Call(Ui, "Configure", (Func<GameStateView>)(() => View), (Func<int>)(() => 1), Get(scene, "definitions"), Registry.CharacterAbilityContracts,
                (Action<GameCommand>)(command => Commands.Add(command)));
            var cards = Cards("hand", 1).Take(2).ToArray();
            View.CharacterUses.Add(new CharacterUseRuntimeState { UseId = "current-use-contract", OwnerEffectId = "current-effect-contract", CardInstanceId = cards[0].ItemId, ExecutingPlayerId = 1 });
            View.CharacterSettlementOptions.Add(new CharacterSettlementOption { OptionId = "current-option-contract", TargetUseId = "current-use-contract", OwnerEffectId = "current-effect-contract",
                InteractionId = "current-request-contract", SourceItemId = cards[1].ItemId, CandidateId = cards[1].ItemId, AnsweringPlayerId = 1, VisibleToViewer = true, IsEnabled = true,
                StateRevision = View.StateRevision, TitleKey = "character.double_use.title" });
            Call(Ui, "OnRequestRouted", new InteractionRequestProjection { InteractionId = "current-request-contract", OwnerEffectId = "current-effect-contract", AnsweringPlayerId = 1,
                StateRevision = View.StateRevision, VisibleToViewer = true, Status = "open", InteractionTypeId = "character.use.mode" });
            Call(scene, "RenderDetails"); yield return Capture("四人底栏当前使用实例-受控投影", 1024, 768);
        }

        private IEnumerator Capture(string name, int width = 1920, int height = 1080)
        {
            var capture = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
            capture.GetMethod("PrepareGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            capture.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { new Vector2Int(width, height) });
            for (int frame = 0; frame < 8; frame++) yield return null;
            Canvas.ForceUpdateCanvases();
            string directory = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", "Logs", "ExpansionValidation", "PlayerDetails", "infected-screenshots"));
            Directory.CreateDirectory(directory); string path = Path.Combine(directory, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            for (int frame = 0; frame < 12; frame++) yield return null;
            Assert.That(File.Exists(path), Is.True, path);
        }
    }
}
