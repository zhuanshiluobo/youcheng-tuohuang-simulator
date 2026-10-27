using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Sessions;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.EditMode.ExpansionRules
{
    // 直接布置隔离初态；验证真实注册、实例转移及恢复，不代表正常局或视觉验收。
    public sealed class CityStyleMiscR7Tests
    {
        private const string Settlement = "infected.facility.temporary_settlement";
        private const string Management = "infected.city_style.district_management";
        private const string SettlementPrompt = "core:condition.infected.facility.temporary_settlement.declared";
        private const string ManagementPrompt = "core:condition.infected.city_style.district_management.declared";
        private static ExternalContentPack installed;
        private Fixture current;

        [OneTimeSetUp]
        public void ReadInstalledContentWithoutCopyingOrRebuildingAssets()
        {
            installed = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            string output = Environment.GetEnvironmentVariable("QY018_CITY_STYLE_EVIDENCE");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "运行时身份.json"), JsonConvert.SerializeObject(new { installedHash = installed.ContentHash,
                    assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => new[] { "YC.Domain", "YC.Application", "YC.Infrastructure.Lua", "YC.Tests.EditMode", "Assembly-CSharp" }.Contains(a.GetName().Name))
                        .Select(a => new { name = a.GetName().Name, mvid = a.ManifestModule.ModuleVersionId, path = a.Location, sha256 = FileHash(a.Location) }),
                    scripts = new[] { "lua/facilities/infected.facility.temporary_settlement.declared.lua", "lua/city_styles/infected.city_style.district_management.declared.lua" }
                        .Select(path => new { path, sha256 = LuaContentHasher.ComputeSha256(installed.GetScript(path)) }) }, Formatting.Indented));
            }
        }

        private static string FileHash(string path)
        {
            using var hash = SHA256.Create();
            using var stream = File.OpenRead(path);
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        [TearDown]
        public void RetainActualStateAndTrace()
        {
            current?.Save("现场-" + TestContext.CurrentContext.Test.ID);
            current = null;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SettlementConditionPreservesOrRetiresExactFacilityAndBoundCharacterAcrossCommits(bool accept)
        {
            var f = current = new Fixture(installed);
            var slots = f.Layout(Management, 1, true);
            string facility = f.State.Map.Facilities.Single(x => x.PlayerId == 1 && x.CityBoardSlotIndex == slots[0]).ContentInstanceId;
            var untouched = f.State.ItemZones.Items.Where(i => i.DefinitionId == Settlement && i.ItemId != facility).ToDictionary(i => i.ItemId, i => i.ZoneId);
            string character = f.State.ItemZones.Items.First(i => i.ZoneId == f.Zone("hand", 1).ZoneId).ItemId;
            f.Run(EffectSpec.Create(CharacterCoverEffectExecutor.EffectTypeId, playerId: 1));
            f.Answer(character);
            f.Registry.Register("qy018.test.bind", c => CharacterLifecycleService.BindFacility(c, character, facility));
            f.Run(EffectSpec.Create("qy018.test.bind", playerId: 1));
            Assert.That(f.State.FacilityCharacterBindings.Single().Active, Is.True);
            int gold = f.State.FindPlayer(1).Resources.GoldVoucher;
            int score = f.State.FindPlayer(1).Score;
            f.Run(CityStyleDeclarationEffectExecutor.Create(1, Management, slots));
            bool seenSettlement = false;
            for (int step = 0; step < 180; step++)
            {
                f.Recover();
                var request = f.Open;
                if (request != null)
                {
                    Assert.That(request.InteractionTypeId, Is.EqualTo("action.decline_effect"));
                    Assert.That(request.CandidateIds, Is.EquivalentTo(new[] { "continue", "decline" }));
                    string key = request.Presentation?.Prompt?.Key;
                    Assert.That(new[] { SettlementPrompt, ManagementPrompt }, Does.Contain(key));
                    bool settlement = key == SettlementPrompt;
                    seenSettlement |= settlement;
                    f.Submit(settlement && accept ? "continue" : "decline");
                }
                if (!f.Advance()) break;
                Assert.That(step, Is.LessThan(179), "隔离效果树未收敛");
            }
            Assert.That(seenSettlement, Is.True, "真实宣告事件未触发设施条件");
            Assert.That(f.Open, Is.Null);
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + (accept ? 12 : 0)));
            Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(3));
            Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(score + 3));
            Assert.That(f.Item(facility).ZoneId == f.Zone("retired").ZoneId, Is.EqualTo(accept));
            Assert.That(f.Item(character).ZoneId == f.Zone("retired").ZoneId, Is.EqualTo(accept));
            Assert.That(f.State.FacilityCharacterBindings.Single().Active, Is.EqualTo(!accept));
            foreach (var pair in untouched) Assert.That(f.Item(pair.Key).ZoneId, Is.EqualTo(pair.Value));
            Assert.That(f.State.EffectRuntime.RuleEvents.Count(e => e.EventType == "CityStyleDeclared"), Is.EqualTo(1));
            f.Recover(); f.Pump();
            Assert.That(f.State.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(gold + (accept ? 12 : 0)));
            f.Save("settlement-" + accept);
        }

        [TestCase("accept")]
        [TestCase("decline")]
        [TestCase("empty")]
        public void ManagementConditionUsesOnlyOwnActualLevelTwoMarkerAndNeverRewardsFailure(string mode)
        {
            var f = current = new Fixture(installed);
            const string levelTwo = "city_style_source_stone_industrial_hub";
            const string levelOne = "city_style_core_commercial_area";
            string own = "";
            if (mode != "empty")
            {
                f.Declare(levelTwo, 1);
                own = f.State.FindPlayer(1).DeclaredCityStyles.Single().InfluenceMarkerId;
            }
            f.Declare(levelTwo, 2);
            string other = f.State.FindPlayer(2).DeclaredCityStyles.Single().InfluenceMarkerId;
            f.Declare(levelOne, 1);
            string firstLevel = f.State.FindPlayer(1).DeclaredCityStyles.Last().InfluenceMarkerId;
            var source = own.Length > 0 ? f.State.FindPlayer(1).DeclaredCityStyles.First() : null;
            if (source != null && source.UnlockedSpecialActionId.Length > 0)
                f.State.FindPlayer(1).UsedSpecialActionIdsThisRound.Add(source.UnlockedSpecialActionId);
            int pure = f.State.FindPlayer(1).Resources.PureOriginium;
            int score = f.State.FindPlayer(1).Score;
            string otherZone = f.Item(other).ZoneId, firstZone = f.Item(firstLevel).ZoneId;
            f.Declare(Management, 1, expectInteraction: true);
            Assert.That(f.Open.Presentation.Prompt.Key, Is.EqualTo(ManagementPrompt));
            Assert.That(f.Open.CandidateIds, Is.EquivalentTo(new[] { "continue", "decline" }));
            f.Recover(); f.Answer(mode == "decline" ? "decline" : "continue");
            InteractionRequest completedRequest = null;
            if (mode == "accept")
            {
                Assert.That(f.Open.CandidateIds, Is.EquivalentTo(new[] { own }));
                Assert.That(f.Open.CandidateIds, Does.Not.Contain(other).And.Not.Contain(firstLevel));
                f.Recover(); completedRequest = f.Open;
                f.Answer(own); f.Recover();
                Assert.That(f.Item(own).ZoneId, Is.EqualTo(f.Item(own).OriginalSupplyZoneId));
                Assert.That(f.Item(own).ZoneId, Is.EqualTo(f.Zone("supply", 1).ZoneId));
                Assert.That(f.State.FindPlayer(1).DeclaredCityStyles.First().MarkerArea, Is.Empty);
                Assert.That(f.State.FindPlayer(1).UsedSpecialActionIdsThisRound, Does.Contain(source.UnlockedSpecialActionId));
            }
            Assert.That(f.Open, Is.Null);
            Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(pure + (mode == "accept" ? 1 : 0)));
            Assert.That(f.State.FindPlayer(1).Score, Is.EqualTo(score + 3));
            Assert.That(f.Item(other).ZoneId, Is.EqualTo(otherZone));
            Assert.That(f.Item(firstLevel).ZoneId, Is.EqualTo(firstZone));
            if (completedRequest != null)
                Assert.That(new EffectTreeExecutor(f.State, f.Registry).TrySubmitInteraction(completedRequest.GetStableInteractionId(), 1,
                    completedRequest.StateRevision, NormalizedValue.CreateString(own), out _), Is.False);
            f.Recover(); f.Pump();
            Assert.That(f.State.FindPlayer(1).Resources.PureOriginium, Is.EqualTo(pure + (mode == "accept" ? 1 : 0)));
            f.Save("management-" + mode);
        }

        private sealed class Fixture
        {
            internal GameState State;
            internal readonly EffectRegistry Registry = new EffectRegistry();
            private readonly List<object> trace = new List<object>();
            private readonly HashSet<string> declarationFacilities;
            internal Fixture(ExternalContentPack pack)
            {
                var config = new GameRuleConfiguration { MapId = StaticMapDefinitions.ThreePlayerMapId, PlayerCount = 3,
                    EnabledExpansionIds = new List<string> { "core", "enterprise", "infected" } };
                var catalog = ActiveContentCatalogBuilder.Build(pack, config);
                config.ContentPackHash = catalog.ContentPackHash;
                catalog.Register(Registry);
                // 私有隔离配方保留全部真实样式候选，避免固定声明目标落在正式随机抽样之外。
                // 仍由生产Assemble建立实例、区域和供应；不写投影列表、不改随机或安装配方。
                var recipe = catalog.SetupRecipe;
                var definitions = catalog.ContentDefinitions;
                var candidates = recipe.Entries.Where(e => e.InitialZoneRole == "city_styles")
                    .Select(e => definitions.CityStyles.Single(s => s.CityStyleId == e.DefinitionId)).ToList();
                int originalLevelOne = recipe.CityStyleLevelOneDrawCount, originalLevelTwo = recipe.CityStyleLevelTwoDrawCount;
                recipe.CityStyleLevelOneDrawCount = candidates.Count(s => s.Level == 1);
                recipe.CityStyleLevelTwoDrawCount = candidates.Count(s => s.Level == 2);
                Registry.SetupRecipe = recipe;
                State = GameLaunchStateFactory.CreateInitialState(LaunchMode.Local, 1,
                    Enumerable.Range(1, 3).Select(p => new PlayerSeat { PlayerId = p, PlayerName = "隔离玩家" + p, Color = (PlayerColor)(p - 1) }).ToList(),
                    config, 197, recipe, definitions, new[] { 1, 2, 3 }, "qy018.misc.r7", null);
                trace.Add(new { stage = "私有配方经正式装配建立供应", originalLevelOne, originalLevelTwo,
                    isolatedLevelOne = recipe.CityStyleLevelOneDrawCount, isolatedLevelTwo = recipe.CityStyleLevelTwoDrawCount,
                    actualSupply = State.Decks.CityStyleSupply.ToArray() });
                Assert.That(State.Decks.CityStyleSupply, Does.Contain(Management).And.Contain("city_style_source_stone_industrial_hub").And.Contain("city_style_core_commercial_area"));
                declarationFacilities = new HashSet<string>(catalog.Definitions.Where(d => d.ContentType == "facility" && d.Abilities.Any(a => a.Mode == "declared")).Select(d => d.DefinitionId));
                ResourceEffectExecutor.Register(Registry); GenericLuaEffectExecutor.Register(Registry);
                ItemTransferEffectExecutor.Register(Registry); RepeatEffectExecutor.Register(Registry);
                ContentLifecycleEffectExecutor.Register(Registry); CityStyleDeclarationEffectExecutor.Register(Registry);
                CharacterLifecycleService.Register(Registry); CharacterCoverEffectExecutor.Register(Registry);
                var map = new MapQueryService(StaticMapDefinitions.Resolve(State.MapId));
                ContentPrimitiveEffectExecutor.Register(Registry, map, new InfluenceService(map));
                new RoundExecutionService(Registry);
                State.Phase = GamePhase.ActionRound1; State.Round = State.ActionRound = 1; State.CurrentPlayerId = State.StartPlayerId = 1;
                State.EffectRuntime = new EffectRuntimeState { PlayerOrderSnapshot = new List<int> { 1, 2, 3 } };
                foreach (var player in State.Players)
                {
                    player.Resources = new ResourceSet { GoldVoucher = 80, Originium = 20, OriginiumShard = 20, Iron = 20, PureOriginium = 3 };
                    foreach (var tower in State.ItemZones.Items.Where(i => i.ZoneId == Zone("city_cell_7", player.PlayerId).ZoneId).ToList())
                        Place(tower, Zone("city_cell_11", player.PlayerId));
                }
                Valid();
                trace.Add(new { installedHash = pack.ContentHash, activeHash = State.ContentPackHash, State.GameId, scope = "隔离规则验证，非正常局/UI" });
            }
            internal ItemInstanceState Item(string id) => State.ItemZones.Items.Single(i => i.ItemId == id);
            internal ZoneInstanceState Zone(string local, int player = -1) => FindZone(State, local, player);
            internal InteractionRequest Open => State.EffectRuntime.InteractionRequests.LastOrDefault(r => r.Status == "open");
            private void Valid() => Assert.That(new ItemZoneQuery().TryValidate(State, out string why), Is.True, why);
            private void Place(ItemInstanceState item, ZoneInstanceState zone)
            {
                foreach (var order in State.ItemZones.Orders) if (order.ItemIds.Remove(item.ItemId)) order.OrderRevision++;
                item.ZoneId = zone.ZoneId; item.SlotId = "";
                var destination = State.ItemZones.Orders.SingleOrDefault(o => o.ZoneId == zone.ZoneId);
                if (destination != null) { destination.ItemIds.Add(item.ItemId); destination.OrderRevision++; }
            }
            internal List<int> Layout(string styleId, int player, bool settlement = false)
            {
                foreach (var item in State.Map.Facilities.Where(x => x.PlayerId == player && x.CityBoardSlotIndex != 11).ToList())
                    Place(Item(item.ContentInstanceId), Zone("facility_deck"));
                var used = new HashSet<string>(State.Players.SelectMany(p => p.DeclaredCityStyles).SelectMany(d => d.UsedFacilityIds));
                var slots = new List<int>();
                var requirement = CityStyleDatabase.Get(State, styleId).DeclarationRequirement;
                foreach (var cell in requirement.RequiredPatternCells)
                {
                    int slot = cell.RowOffset * BuildFacilityService.CityBoardSlotCountPerRow + cell.ColumnOffset;
                    var item = State.ItemZones.Items.First(i => i.TypeId == "core.facility_card" && !used.Contains(i.ItemId) &&
                        !State.ItemZones.Zones.Any(z => z.ZoneId == i.ZoneId && z.TemplateId == "core.zone.city_facility_cell") &&
                        (settlement && slots.Count == 0 ? i.DefinitionId == Settlement : !declarationFacilities.Contains(i.DefinitionId) &&
                         cell.AllowedFacilityColors.Contains(FacilityCardDatabase.Get(State, i.DefinitionId).Color)));
                    Place(item, Zone("city_cell_" + slot, player)); item.ControllerSubject = ItemSubjectRef.Player(player);
                    used.Add(item.ItemId); slots.Add(slot);
                }
                Assert.That(slots.Count, Is.EqualTo(requirement.RequiredFacilityCount));
                Valid(); return slots;
            }
            internal void Declare(string style, int player, bool expectInteraction = false)
            {
                var slots = Layout(style, player);
                var validation = new DeclareCityStyleService().Validate(State, player, style, slots);
                Assert.That(validation.IsValid, Is.True, "隔离宣告前提未成立：" + validation.Reason);
                Run(CityStyleDeclarationEffectExecutor.Create(player, style, slots));
                Assert.That(State.FindPlayer(player).DeclaredCityStyles.Any(d => d.CityStyleId == style), Is.True, "真实宣告未成功：" + style);
                if (!expectInteraction) Assert.That(Open, Is.Null, "不应有额外交互");
            }
            internal void Run(EffectSpec spec) { new EffectTreeExecutor(State, Registry).CreateRoot(spec); Pump(); }
            internal void Pump() { Assert.That(new EffectTreeExecutor(State, Registry).RunUntilQuiescent().Faulted, Is.False, State.EffectRuntime.LastFaultMessage); Valid(); }
            internal bool Advance() { bool moved = new EffectTreeExecutor(State, Registry).Advance(); Assert.That(State.EffectRuntime.Status, Is.EqualTo(EffectRuntimeStatus.Active), State.EffectRuntime.LastFaultMessage); Valid(); return moved; }
            internal void Submit(string answer)
            {
                var r = Open; Assert.That(r, Is.Not.Null);
                trace.Add(new { r.InteractionTypeId, prompt = r.Presentation?.Prompt?.Key, candidates = r.CandidateIds.ToArray(), answer });
                Assert.That(new EffectTreeExecutor(State, Registry).TrySubmitInteraction(r.GetStableInteractionId(), r.AnsweringPlayerId,
                    State.EffectRuntime.StateRevision, NormalizedValue.CreateString(answer), out string why), Is.True, why);
            }
            internal void Answer(string answer) { Submit(answer); Pump(); }
            internal void Recover()
            {
                string requestBefore = Open?.GetStableInteractionId();
                string digestBefore = RuleStateDigestService.Compute(State);
                var recovered = HostRecoveryService.RecoverJson(HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(State)), Registry);
                Assert.That(recovered.Succeeded, Is.True, recovered.Diagnostic); State = recovered.State; Valid();
                string digestAfter = RuleStateDigestService.Compute(State);
                Assert.That(Open?.GetStableInteractionId(), Is.EqualTo(requestBefore));
                Assert.That(digestAfter, Is.EqualTo(digestBefore));
                trace.Add(new { stage = "Host归档恢复", requestBefore, requestAfter = Open?.GetStableInteractionId(),
                    digestBefore, digestAfter, State.EffectRuntime.StateRevision });
            }
            internal void Save(string name)
            {
                string root = Environment.GetEnvironmentVariable("QY018_CITY_STYLE_EVIDENCE");
                if (string.IsNullOrEmpty(root)) return;
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, name + ".json"), JsonConvert.SerializeObject(new { trace,
                    resources = State.Players.Select(p => new { p.PlayerId, p.Score, p.Resources }), State.FacilityCharacterBindings,
                    openRequest = Open, runtimeStatus = State.EffectRuntime.Status, State.EffectRuntime.LastFaultMessage,
                    moves = State.EffectRuntime.EffectNodes.SelectMany(n => n.PositionChanges), declarations = State.Players.Select(p => new { p.PlayerId, p.DeclaredCityStyles }) }, Formatting.Indented));
                File.WriteAllText(Path.Combine(root, name + ".archive.json"), HostRecoveryService.SerializeArchive(HostRecoveryService.CreateArchive(State)));
            }
        }
    }
}
