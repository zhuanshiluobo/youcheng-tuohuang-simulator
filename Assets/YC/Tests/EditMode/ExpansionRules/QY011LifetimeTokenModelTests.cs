using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 原语/类型隔离验证，非正常游戏验收。正式目录另用未修改的完整输入验证装配。
    public sealed class QY011LifetimeTokenModelTests
    {
        private IsolatedTestArtifacts artifacts;
        private ExternalContentPack pack;
        private GameState state;
        private EffectRegistry registry;
        private GameMapDefinition map;

        [SetUp] public void Prepare()
        {
            state = null; artifacts = IsolatedTestArtifacts.BeginCase();
            string installed = Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core");
            string input = artifacts.CopyContent(installed, "终身token未修改正式输入");
            map = StaticMapDefinitions.Resolve(StaticMapDefinitions.FourPlayerMapId);
            pack = ExternalContentPack.Load(input).CreateActiveSnapshot(new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2,
                EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } });
            artifacts.RecordContent("终身token实际定义输入", input, pack.ContentHash);
            var source = new GameState { GameId = "qy011.lifetime.token.isolated", MapId = map.MapId, ContentPackHash = pack.ContentHash,
                RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2, ContentPackHash = pack.ContentHash,
                    EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } },
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "甲" }, new PlayerState { PlayerId = 2, Name = "乙" } },
                ContentDefinitions = new GameContentDefinitions { Components = pack.Definitions.Where(d => d.ContentType == "component")
                    .Select(d => new ComponentContentDefinition { DefinitionId = d.DefinitionId, TypeId = d.ItemTypeId,
                        CubeAppearance = GameStateCloneService.DeepClone(d.CubeAppearance), QuantityPolicy = (string)d.Data["quantityPolicy"] }).ToList() },
                LaunchPlayerOrder = new List<int> { 1, 2 }, StartPlayerId = 1, CurrentPlayerId = 1 };
            state = new ItemZoneStateFactory().CreateForSetup(source, map, new[] {
                new ItemRecipeEntry { EntryId = "contract", DefinitionId = CompanyInfluenceRules.ContractDefinitionId, TypeId = ItemTypeRegistry.ContractTokenTypeId, Quantity = 2, InitialZoneRole = "contracts" },
                new ItemRecipeEntry { EntryId = "company", DefinitionId = "infected.component.company_marker", TypeId = "infected.company_marker", Quantity = 4, InitialZoneRole = "company_markers" }
            });
            // 最小原语registry只验证奖励取得本身；完整Lua持有查询沿原ScoringModifierContractTests复验。
            registry = new EffectRegistry { ContentPackHash = pack.ContentHash, ContentDefinitions = GameStateCloneService.DeepClone(state.ContentDefinitions) };
            ResourceEffectExecutor.Register(registry); GenericLuaEffectExecutor.Register(registry);
            ContentPrimitiveEffectExecutor.Register(registry, new MapQueryService(map), new InfluenceService(new MapQueryService(map)));
        }
        [TearDown] public void Finish()
        {
            if (artifacts == null) return;
            artifacts.WriteLabeledText("终身token实际隔离状态", JsonConvert.SerializeObject(state)); artifacts.Finish();
        }
        [Test] public void FormalDefinitionAndUnmodifiedCatalogResolveATokenRecipe()
        {
            var definition = pack.Definitions.Single(d => d.DefinitionId == CompanyInfluenceRules.ContractDefinitionId);
            Assert.That(definition.ItemTypeId, Is.EqualTo(ItemTypeRegistry.ContractTokenTypeId));
            var types = ItemTypeRegistry.CreateBuiltIn();
            Assert.That(types.Find(definition.ItemTypeId).PhysicalKind, Is.EqualTo(ItemPhysicalKind.Token));
            Assert.That(types.IsAssignableTo(definition.ItemTypeId, "core.token"), Is.True);
            Assert.That(types.IsAssignableTo(definition.ItemTypeId, "core.card"), Is.False);
            Assert.That(types.HasOperationProfile(definition.ItemTypeId, "core.reward_card_transfer"), Is.False);
            var configuration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = 2,
                EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
            var catalog = ActiveContentCatalogBuilder.Build(pack, configuration);
            var entry = catalog.SetupRecipe.Entries.Single(e => e.DefinitionId == definition.DefinitionId);
            Assert.That(entry.TypeId, Is.EqualTo(ItemTypeRegistry.ContractTokenTypeId)); Assert.That(entry.Quantity, Is.EqualTo(2));
            Assert.That(entry.InitialZoneRole, Is.EqualTo("contracts")); Assert.That(entry.PerPlayer, Is.False);
            Assert.That(entry.Condition.Kind, Is.EqualTo("enterprise_selected")); Assert.That(entry.Condition.RuntimeId, Is.EqualTo("infected.enterprise.stonehoove_security"));
            artifacts.WriteLabeledText("未改manifest的正式活动配方", JsonConvert.SerializeObject(new { catalog.ContentPackHash, entry }));
            var frozen = configuration.Clone(); frozen.ContentPackHash = catalog.ContentPackHash;
            var source = new GameState { GameId = "qy011.lifetime.formal.recipe.isolated", MapId = map.MapId, ContentPackHash = catalog.ContentPackHash,
                RuleConfiguration = frozen, ContentDefinitions = catalog.ContentDefinitions,
                Players = new List<PlayerState> { new PlayerState { PlayerId = 1, Name = "甲" }, new PlayerState { PlayerId = 2, Name = "乙" } },
                LaunchPlayerOrder = new List<int> { 1, 2 }, StartPlayerId = 1, CurrentPlayerId = 1 };
            var prepared = catalog.SetupRecipe.Assemble(source, map, 0);
            var tokens = prepared.ItemZones.Items.Where(i => i.DefinitionId == CompanyInfluenceRules.ContractDefinitionId).ToArray();
            Assert.That(tokens.Length, Is.EqualTo(2));
            Assert.That(tokens.All(i => i.TypeId == ItemTypeRegistry.ContractTokenTypeId && i.PhysicalKind == ItemPhysicalKind.Token), Is.True);
            Assert.That(tokens.All(i => prepared.ItemZones.Zones.Single(z => z.ZoneId == i.ZoneId).TemplateId == "core.zone.box_reserve" && i.OriginalSupplyZoneId == i.ZoneId), Is.True, "条件组件在真实备用区准备，不伪造奖励池原供应");
            Assert.That(CompanyInfluenceRules.HasContract(prepared, 1), Is.False);
            Assert.That(new ItemZoneQuery().TryValidate(prepared, out string reason), Is.True, reason);
            artifacts.WriteLabeledText("正式配方实际准备状态非正常页面验收", JsonConvert.SerializeObject(prepared));
        }
        [Test] public void RealAcquisitionLimitsOnePerHolderPublishesOnceAndRestoresWithoutRegranting()
        {
            string companyBefore = JsonConvert.SerializeObject(state.ItemZones.Items.Where(i => i.TypeId == "infected.company_marker"));
            string first = Acquire(1); var held = Contracts().Single(i => i.ZoneId == Zone("rewards", 1).ZoneId);
            Assert.That(held.PhysicalKind, Is.EqualTo(ItemPhysicalKind.Token)); Assert.That(held.ControllerSubject.PlayerId, Is.EqualTo(1));
            Assert.That(held.OriginalSupplyZoneId, Is.EqualTo(Zone("contracts").ZoneId));
            Assert.That(CompanyInfluenceRules.HasContract(state, 1), Is.True); Assert.That(CompanyInfluenceRules.HasContract(state, 2), Is.False);
            var node = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == first);
            Assert.That(node.PositionChanges.Count, Is.EqualTo(1)); Assert.That(node.PositionChanges[0].ItemId, Is.EqualTo(held.ItemId));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == RewardAcquisitionFlow.AcquiredEventTypeId), Is.EqualTo(1));
            string duplicate = Acquire(1); var refused = state.EffectRuntime.EffectNodes.Single(n => n.EffectId == duplicate);
            Assert.That(Flag(refused.NormalizedResult, "changed"), Is.False); Assert.That(Text(refused.NormalizedResult, "reasonCode"), Is.EqualTo("limit_reached"));
            Assert.That(refused.PositionChanges, Is.Empty);
            Acquire(2); Assert.That(CompanyInfluenceRules.HasContract(state, 2), Is.True);
            var restored = artifacts.Recover(state, "真实取得后完整Host恢复", registry); Assert.That(restored.Succeeded, Is.True, restored.Diagnostic); state = restored.State;
            Pump(); Pump();
            Assert.That(Contracts().Count(i => i.ZoneId == Zone("rewards", 1).ZoneId), Is.EqualTo(1));
            Assert.That(Contracts().Count(i => i.ZoneId == Zone("rewards", 2).ZoneId), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents.Count(e => e.EventType == RewardAcquisitionFlow.AcquiredEventTypeId), Is.EqualTo(2));
            Assert.That(JsonConvert.SerializeObject(state.ItemZones.Items.Where(i => i.TypeId == "infected.company_marker")), Is.EqualTo(companyBefore), "公司不变玩家所有物");
        }
        [Test] public void ActualHeldDeclarationUsesTokenControllerAndStopsWhenNoLongerHeld()
        {
            var declarations = AbilitySourceLuaCatalog.CreateDeclarations(pack).Where(d => d.DefinitionId == CompanyInfluenceRules.ContractDefinitionId).ToList();
            Assert.That(declarations.Count, Is.EqualTo(1));
            Assert.That(declarations[0].Kind, Is.EqualTo("ruleQuery")); Assert.That(declarations[0].Actions, Is.Empty);
            Assert.That(AbilitySourceService.QuerySources(state, declarations, 1, queryKind: "scoringModifiers.v1"), Is.Empty);
            Acquire(1); var sources = AbilitySourceService.QuerySources(state, declarations, 1, queryKind: "scoringModifiers.v1");
            Assert.That(sources.Count, Is.EqualTo(1)); Assert.That(sources[0].AuthorizedPlayerId, Is.EqualTo(1));
            Assert.That(sources[0].EffectiveZoneId, Is.EqualTo(Zone("rewards", 1).ZoneId));
            Assert.That(AbilitySourceService.QuerySources(state, declarations, 2, queryKind: "scoringModifiers.v1"), Is.Empty);
            // 明确的资格定位副本，不冒充正式退还操作或正常对局。
            var copy = GameStateCloneService.DeepClone(state); copy.ItemZones.Items.Single(i => i.ItemId == sources[0].ItemId).ZoneId = Zone("reserve").ZoneId;
            Assert.That(CompanyInfluenceRules.HasContract(copy, 1), Is.False);
            Assert.That(AbilitySourceService.QuerySources(copy, declarations, 1, queryKind: "scoringModifiers.v1"), Is.Empty);
            Assert.That(AbilitySourceService.IsCurrent(copy, declarations, sources[0]), Is.False);
        }
        [TestCase("core.token_storage")] [TestCase("core.reward_card_transfer")] [TestCase("core.reward_token_acquire")]
        public void OrdinaryRequestsCannotGrantARewardTokenByNamingAProfile(string profile)
        {
            var token = Contracts().First(); string before = JsonConvert.SerializeObject(state);
            var request = new ItemTransferRequest { ExecutingPlayerId = 1, ItemIds = new List<string> { token.ItemId },
                ExpectedSource = new ItemPositionRef { ZoneId = token.ZoneId }, Destination = new ZoneRef { ZoneId = Zone("rewards", 1).ZoneId },
                InsertionKind = "unordered", RequiredCount = 1, OperationProfileId = profile };
            Assert.That(new ItemTransferService().Validate(state, request).IsValid, Is.False);
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before)); Assert.That(CompanyInfluenceRules.HasContract(state, 1), Is.False);
        }
        [TestCase("controller")] [TestCase("origin")] [TestCase("duplicate")]
        public void CorruptedHolderOriginAndLimitAreRejectedFromAnActualArchive(string damage)
        {
            Acquire(1); string archive = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state));
            Assert.That(HostRecoveryService.RecoverJson(archive, registry).Succeeded, Is.True);
            var raw = JObject.Parse(archive); var items = (JArray)raw["Snapshot"]["State"]["ItemZones"]["Items"];
            var held = items.OfType<JObject>().Single(i => (string)i["ZoneId"] == Zone("rewards", 1).ZoneId);
            if (damage == "controller") held["ControllerSubject"]["PlayerId"] = 2;
            else if (damage == "origin") held["OriginalSupplyZoneId"] = "";
            else { var other = items.OfType<JObject>().Single(i => (string)i["TypeId"] == ItemTypeRegistry.ContractTokenTypeId && (string)i["ItemId"] != (string)held["ItemId"]); other["ZoneId"] = held["ZoneId"].DeepClone(); other["ControllerSubject"] = held["ControllerSubject"].DeepClone(); }
            string bad = raw.ToString(); artifacts.WriteLabeledText("真实持有档损坏-" + damage, bad);
            var result = HostRecoveryService.RecoverJson(bad, registry); Assert.That(result.Succeeded, Is.False); Assert.That(result.Diagnostic, Is.Not.Empty);
            Assert.That(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), Is.EqualTo(archive));
        }
        [Test] public void OldCardArchiveIsDiagnosticallyRejectedWithoutRetypingOrEditingItsOriginal()
        {
            Acquire(1); var old = GameStateCloneService.DeepClone(state); var held = old.ItemZones.Items.Single(i => i.ZoneId == Zone("rewards", 1).ZoneId);
            held.TypeId = "core.contract_card"; held.PhysicalKind = ItemPhysicalKind.Card;
            string before = JsonConvert.SerializeObject(old);
            Assert.That(new ItemZoneQuery().TryValidate(old, out string reason), Is.False); Assert.That(reason, Is.EqualTo("legacy_lifetime_security_card_requires_original_version"));
            Assert.That(JsonConvert.SerializeObject(old), Is.EqualTo(before)); Assert.That(CompanyInfluenceRules.HasContract(old, 1), Is.False);
            string original = HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)); var raw = JObject.Parse(original);
            var item = ((JArray)raw["Snapshot"]["State"]["ItemZones"]["Items"]).OfType<JObject>().Single(i => (string)i["ItemId"] == held.ItemId);
            item["TypeId"] = "core.contract_card"; item["PhysicalKind"] = (int)ItemPhysicalKind.Card;
            artifacts.WriteLabeledText("旧Card模型独立副本拒绝非用户原档", raw.ToString());
            var recovered = HostRecoveryService.RecoverJson(raw.ToString(), registry); Assert.That(recovered.Succeeded, Is.False); Assert.That(recovered.Diagnostic, Is.Not.Empty);
            Assert.That(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(state)), Is.EqualTo(original));
        }
        private IEnumerable<ItemInstanceState> Contracts() => state.ItemZones.Items.Where(i => i.DefinitionId == CompanyInfluenceRules.ContractDefinitionId);
        private ZoneInstanceState Zone(string local, int player = -1) => state.ItemZones.Zones.Single(z => z.LocalZoneId == local && z.OwnerPlayerId == player);
        private string Acquire(int player)
        {
            string id = new EffectTreeExecutor(state, registry).CreateRoot(EffectSpec.Create(RewardAcquisitionFlow.TypeId,
                Object("apiVersion", NormalizedValue.CreateInteger(1), "player", NormalizedValue.CreateString(player.ToString(System.Globalization.CultureInfo.InvariantCulture)), "poolRole", NormalizedValue.CreateString("contracts")), player));
            Pump(); Assert.That(state.EffectRuntime.EffectNodes.Single(n => n.EffectId == id).Status, Is.EqualTo(EffectNodeStatus.Completed)); return id;
        }
        private void Pump() { var report = new EffectTreeExecutor(state, registry).RunUntilQuiescent(); Assert.That(report.Faulted, Is.False, state.EffectRuntime.LastFaultMessage); }
    }
}
