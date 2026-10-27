using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    public sealed class StoredFacilityBuildCandidateTests
    {
        private ExternalContentPack core, pack;
        private EffectRegistry registry;
        private string facilityDefinitionId;
        private IsolatedTestArtifacts artifacts;
        [TearDown] public void RecordCase() => artifacts?.Finish();

        [SetUp]
        public void LoadInstalledDefinitionAndActualLua()
        {
            string content = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            artifacts = IsolatedTestArtifacts.BeginCase();
            content=artifacts.CopyContent(content,"旧设施候选完整未修改基线");
            var patches = new Dictionary<string, string>();
            var card = JObject.Parse(File.ReadAllText(Path.Combine(content, "character/operator.closure.json")));
            // 本夹具验规则，不借原UI档案，也不把裁切校验冒称画面验收。
            card.Remove("artworkSpriteId");
            patches["character/operator.closure.json"] = card.ToString();
            var inventory = JObject.Parse(File.ReadAllText(Path.Combine(content, "facility_inventory.json")));
            var group = ((JArray)inventory["groups"]).Single(value => (string)value["definition"]["dataTemplate"] == "templates/facilities/discount_originium_by_facility_color.json");
            var variant = group["variants"][0]; var instance = variant["instances"][0];
            var facility = (JObject)group["definition"].DeepClone();
            facility["definitionId"] = instance["id"].DeepClone(); facility["artwork"] = variant["artwork"].DeepClone();
            facility.Remove("artworkSpriteId");
            facility["data"] = new JObject { ["facilityId"] = instance["id"].DeepClone(), ["color"] = variant["color"].DeepClone() };
            patches["facility/source.json"] = facility.ToString();
            patches["pack.json"] = new JObject { ["schemaVersion"] = 3, ["packId"] = "core", ["version"] = "1.0.0",
                ["definitions"] = new JArray("character/operator.closure.json", "facility/source.json"), ["enabledExpansionIds"] = new JArray("core", "enterprise"), ["requiredCapabilities"] = new JArray(),
                ["expansionDependencies"] = new JArray(new JObject { ["expansionId"] = "enterprise", ["requires"] = new JArray("core") }), ["setupRecipes"] = new JArray(),
                // 实际Closure Lua沿正式显示声明解析；最小定义清单不能丢掉显示来源登记。
                ["presentation"] = JObject.Parse(File.ReadAllText(Path.Combine(content, "pack.json")))["presentation"].DeepClone() }.ToString();
            string root = artifacts.CopyContent(content, "旧设施候选独占私有输入", patches);
            pack = ExternalContentPack.Load(root); core = pack;
            artifacts.RecordContent("旧设施候选实际装载", root, pack.ContentHash);
            facilityDefinitionId = core.CreateFacilities().First(facility => !facility.Unique && !facility.HasEntryEffect && facility.GoldVoucherCost > 0).FacilityId;
        }

        private GameState State()
        {
            registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            GenericLuaEffectExecutor.Register(registry); ResourceEffectExecutor.Register(registry);
            FacilityEntryEffectExecutor.Register(registry); CharacterCoverEffectExecutor.Register(registry);
            CharacterAbilityEffectExecutor.Register(registry); CharacterCardLuaCatalog.Register(registry, pack);
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            var source = new GameState { GameId = "game.stored_build", MapId = map.MapId, ContentPackHash = pack.ContentHash,
                Round = 1, ActionRound = 1, StartPlayerId = 1, CurrentPlayerId = 1, Phase = GamePhase.ActionRound1,
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = pack.ContentHash },
                ContentDefinitions = new GameContentDefinitions { Characters = pack.CreateCharacters().ToList(), Facilities = core.CreateFacilities().ToList(),
                    Events = core.CreateEvents().ToList(), CityStyles = core.CreateCityStyles().ToList() } };
            for (int id = 1; id <= 2; id++) source.Players.Add(new PlayerState { PlayerId = id, Resources = new ResourceSet { GoldVoucher = 80, Originium = 20, OriginiumShard = 20, Iron = 20 } });
            var entries = new List<ItemRecipeEntry> {
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.player_marker", TypeId = "core.player_marker", Quantity = 29, PerPlayer = true, InitialZoneRole = "supply" },
                new ItemRecipeEntry { EntryId = "cities", DefinitionId = "core.mobile_city", TypeId = "core.mobile_city", Quantity = 1, PerPlayer = true, InitialZoneRole = "city_reserve" },
                new ItemRecipeEntry { EntryId = "closure", DefinitionId = "operator.closure", TypeId = "core.operator_card", Quantity = 1, InitialZoneRole = "hand", PerPlayer = true },
                new ItemRecipeEntry { EntryId = "facilities", DefinitionId = facilityDefinitionId, TypeId = "core.facility_card", Quantity = 9, InitialZoneRole = "facility_deck" } };
            var state = new ItemZoneStateFactory().CreateForSetup(source, map, entries,
                pack.ActiveDefinitions.Select(definition => new ItemCardZoneDeclaration { DefinitionId = definition.DefinitionId, CardZones = definition.CardZones }).ToList());
            state.EffectRuntime.PlayerOrderSnapshot = new List<int> { 1, 2 };
            for (int slot = 0; slot < 6; slot++) Run(state, RefillSpec(1, FindZone(state, "facility_supply_" + slot).ZoneId));
            return state;
        }

        private ItemInstanceState Carrier(GameState state) => state.ItemZones.Items.Single(item => item.DefinitionId == "operator.closure" && item.ControllerSubject.PlayerId == 1);
        private ZoneInstanceState Orders(GameState state) => state.ItemZones.Zones.Single(zone => zone.ParentId == Carrier(state).ItemId && zone.LocalZoneId == "orders");
        private InteractionRequest Open(GameState state) => state.EffectRuntime.InteractionRequests.Last(request => request.Status == "open");
        private EffectNodeRuntimeState Run(GameState state, EffectSpec spec)
        {
            var engine = new EffectTreeExecutor(state, registry); string id = engine.CreateRoot(spec);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage); return engine.GetNode(id);
        }
        private void Answer(GameState state, string choice)
        {
            var request = Open(state); var engine = new EffectTreeExecutor(state, registry);
            Assert.That(engine.TrySubmitInteraction(request.GetStableInteractionId(), request.AnsweringPlayerId, request.StateRevision, NormalizedValue.CreateString(choice), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }
        private string Store(GameState state)
        {
            Run(state, EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1)); Answer(state, Carrier(state).ItemId);
            Run(state, CharacterLifecycleService.CreateUseSpec(1, Carrier(state).ItemId, "tactic"));
            string held = Open(state).CandidateIds.First(id => id != "choice.skip"); Answer(state, held);
            if (state.EffectRuntime.InteractionRequests.Any(request => request.Status == "open" && request.CandidateIds.Contains("choice.skip"))) Answer(state, "choice.skip");
            Assert.That(state.ItemZones.Items.Single(item => item.ItemId == held).ZoneId, Is.EqualTo(Orders(state).ZoneId));
            return held;
        }
        private GameState Restore(GameState state)
        {
            var recovered = artifacts.Recover(state, "旧设施候选真实恢复", registry);
            Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic); return recovered.State;
        }
        private BuildFacilityOptionQueryService Query() => new BuildFacilityOptionQueryService(new BuildFacilityService(), registry.FacilityBuildCandidates);
        private void PrepareStoredBuildPayment(GameState state, EffectNodeRuntimeState selection, string itemId)
        {
            var request = Open(state); var engine = new EffectTreeExecutor(state, registry);
            Assert.That(request.InteractionTypeId, Is.EqualTo(FacilitySelectionEffectExecutor.NormalBuildInteractionType));
            Assert.That(request.CandidateIds, Does.Contain(itemId));
            string source = state.ItemZones.Items.Single(i => i.ItemId == itemId).ZoneId;
            Assert.That(engine.TryPrepareEffectBuildPayment(1, selection.EffectId, request.GetStableInteractionId(),
                request.CandidateSetId, request.CandidateSetVersion, itemId,
                Object("paymentMode", NormalizedValue.CreateString("gold"), "expectedSourceZoneId", NormalizedValue.CreateString(source)),
                state.EffectRuntime.StateRevision, Guid.NewGuid().ToString("N"), out string reason), Is.True, reason);
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Assert.That(Open(state).CandidateIds, Does.Contain("pay"));
            artifacts.WriteLabeledText("正式库存设施付款准备待答", JsonConvert.SerializeObject(state));
        }

        [Test]
        public void PurePreviewMatchesChooseBuildAndRetainsSameDefinitionInstances()
        {
            var state = State(); string held = Store(state); string before = JsonConvert.SerializeObject(state);
            var snapshot = Query().QueryCandidates(state, 1); var options = Query().Query(state, 1, snapshot);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            Assert.That(snapshot.IsDefaultSupplySet, Is.False); Assert.That(snapshot.FinalItemIds, Does.Contain(held));
            Assert.That(snapshot.Candidates.Count(candidate => candidate.DefinitionId == facilityDefinitionId), Is.EqualTo(7));
            Assert.That(snapshot.Resolution.AppliedPatches.Single().SourceContentInstanceId, Is.EqualTo(Carrier(state).ItemId));
            Run(state, EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId, Object("sourceZone", NormalizedValue.CreateString("supply")), 1));
            Assert.That(Open(state).CandidateIds, Is.EquivalentTo(options.Where(option => option.CanBuild).Select(option => option.FacilityId)));
        }

        [TestCase("resources")]
        [TestCase("gold")]
        public void MainBuildPaysNormalCostMovesStoredItemAndDiscardsEmptyCarrier(string payment)
        {
            var state = State(); string held = Store(state); var snapshot = Query().QueryCandidates(state, 1);
            string publicTwin = snapshot.DefaultItemIds.First(); var option = Query().Query(state, 1, snapshot).Single(value => value.FacilityId == held);
            var balance = state.FindPlayer(1).Resources.Clone();
            var command = new GameCommand { Kind = GameCommandKind.BuildFacility, PlayerId = 1, TargetId = held };
            command.Parameters[BuildFacilityCommandHandler.PaymentModeParameter] = payment;
            command.Parameters[BuildFacilityCommandHandler.ExpectedSourceZoneIdParameter] = option.SourceZoneId;
            var handler = new BuildFacilityCommandHandler(BuildFacilityService.CreateForEffectTree(), new RoundAdvanceService(), registry);
            Assert.That(handler.Handle(state, command).Succeeded, Is.True);
            Assert.That(Carrier(state).ZoneId, Is.EqualTo(FindZone(state, "discard", 1).ZoneId));
            Assert.That(CharacterLifecycleService.IsSubscriptionActive(state, Carrier(state).ItemId, "operator.closure.tactic"), Is.False);
            Assert.That(state.ItemZones.Items.Single(item => item.ItemId == publicTwin).ZoneId, Is.EqualTo(snapshot.Candidates.Single(candidate => candidate.ItemId == publicTwin).SourceZoneId));
            state = Restore(state); Answer(state, Open(state).CandidateIds[0]);
            Assert.That(state.Map.Facilities.Single().ContentInstanceId, Is.EqualTo(held));
            if (payment == "gold") Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(balance.GoldVoucher - FacilityCardDatabase.Get(state, facilityDefinitionId).GoldVoucherCost));
            else Assert.That(state.FindPlayer(1).Resources.Originium, Is.EqualTo(balance.Originium - option.EffectiveResourceCost.Originium));
        }

        [Test]
        public void SupplyOnlyAndOtherPlayerCannotSelectStoredItem()
        {
            var state = State(); string held = Store(state);
            Assert.That(registry.FacilityBuildCandidates.Resolve(state, 2).FinalItemIds, Does.Not.Contain(held));
            Assert.That(registry.FacilityBuildCandidates.Resolve(state, 1, new FacilityBuildCandidateOptions { IncludeStoredFacilities = false }).FinalItemIds, Does.Not.Contain(held));
            var command = new GameCommand { Kind = GameCommandKind.BuildFacility, PlayerId = 2, TargetId = held };
            command.Parameters[BuildFacilityCommandHandler.PaymentModeParameter] = "gold"; state.CurrentPlayerId = 2;
            Assert.That(new BuildFacilityCommandHandler(BuildFacilityService.CreateForEffectTree(), new RoundAdvanceService(), registry).Handle(state, command).Succeeded, Is.False);
            Assert.That(state.ItemZones.Items.Single(item => item.ItemId == held).ZoneId, Is.EqualTo(Orders(state).ZoneId));
        }

        [Test]
        public void OwnerViewPreviewKeepsNoHostRuntimeAndNoStateMutation()
        {
            var state = State(); string held = Store(state);
            var view = GameStateViewProjector.Project(state, GameStateViewer.Player(1));
            var client = GameStateViewProjector.ToClientState(view); client.ContentDefinitions = GameStateCloneService.DeepClone(state.ContentDefinitions);
            string before = JsonConvert.SerializeObject(client);
            var snapshot = Query().QueryCandidates(client, 1, view.StateRevision);
            Assert.That(snapshot.FinalItemIds, Does.Contain(held)); Assert.That(client.EffectRuntime, Is.Null);
            Assert.That(JsonConvert.SerializeObject(client), Is.EqualTo(before));
            Assert.That(snapshot.Resolution.StateRevision, Is.EqualTo(view.StateRevision));
        }

        [Test]
        public void ChooseBuildRestoresPaymentAndPaidStagingWithoutRepeatingPayment()
        {
            var state = State(); string held = Store(state);
            var selection = Run(state, EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId, Object("sourceZone", NormalizedValue.CreateString("supply")), 1));
            PrepareStoredBuildPayment(state, selection, held); state = Restore(state); Answer(state, "pay");
            int gold = state.FindPlayer(1).Resources.GoldVoucher;
            state = Restore(state); Answer(state, Open(state).CandidateIds[0]); state = Restore(state);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold));
            Assert.That(state.Map.Facilities.Single().ContentInstanceId, Is.EqualTo(held));
        }

        [Test]
        public void StaleSourceCommandDoesNotPayOrSelectPublicTwin()
        {
            var state = State(); string held = Store(state); int gold = state.FindPlayer(1).Resources.GoldVoucher;
            var command = new GameCommand { Kind = GameCommandKind.BuildFacility, PlayerId = 1, TargetId = held };
            command.Parameters[BuildFacilityCommandHandler.PaymentModeParameter] = "gold";
            command.Parameters[BuildFacilityCommandHandler.ExpectedSourceZoneIdParameter] = FindZone(state, "facility_supply_0").ZoneId;
            Assert.That(new BuildFacilityCommandHandler(BuildFacilityService.CreateForEffectTree(), new RoundAdvanceService(), registry).Handle(state, command).Succeeded, Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold)); Assert.That(state.Map.Facilities, Is.Empty);
        }

        [Test]
        public void TamperedFrozenSelectionSourceIsRejectedOnRecovery()
        {
            var state = State(); string held = Store(state);
            var node = Run(state, EffectSpec.Create(FacilitySelectionEffectExecutor.TypeId, Object("sourceZone", NormalizedValue.CreateString("supply")), 1));
            PrepareStoredBuildPayment(state, node, held); Answer(state, "pay");
            var archive = HostRecoveryService.CreateArchive(state);
            string original = HostRecoveryService.SerializeArchive(archive);
            var control = HostRecoveryService.RecoverJson(original, registry);
            Assert.That(control.Succeeded, Is.True, control.Diagnostic);
            artifacts.WriteLabeledText("库存建设冻结来源合法归档对照", original);
            Set(archive.Snapshot.State.EffectRuntime.EffectNodes.Single(value => value.EffectId == node.EffectId), "selectedSourceZoneId", NormalizedValue.CreateString(FindZone(state, "facility_supply_0").ZoneId));
            var result = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(archive), registry);
            Assert.That(result.Succeeded, Is.False); Assert.That(result.Diagnostic, Does.Contain("build_source_restore_invalid"));
        }
    }
}
