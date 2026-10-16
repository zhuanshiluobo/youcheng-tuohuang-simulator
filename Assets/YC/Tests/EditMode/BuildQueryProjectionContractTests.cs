using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class BuildQueryProjectionContractTests
    {
        [TestCase("mixed")]
        [TestCase("unique")]
        [TestCase("full")]
        [TestCase("insufficient")]
        [TestCase("gold")]
        [TestCase("spent")]
        [TestCase("other_player")]
        [TestCase("wrong_phase")]
        public void SupplyQuery_WithProjectedLists_ReturnsCompleteEquivalentResultsAndDoesNotMutateState(string scenario)
        {
            var plain = CreateScenario(scenario);
            var projected = CreateScenario(scenario);
            AttachProjections(projected);
            var service = new BuildFacilityOptionQueryService();
            var before = JsonConvert.SerializeObject(projected);
            var expected = service.Query(plain, 1);
            var actual = service.Query(projected, 1);

            Assert.That(JsonConvert.SerializeObject(actual), Is.EqualTo(JsonConvert.SerializeObject(expected)));
            Assert.That(JsonConvert.SerializeObject(projected), Is.EqualTo(before));
            Assert.That(actual.Select(option => option.FacilityId), Is.EqualTo(projected.Decks.FacilitySupply.Select(id => id ?? string.Empty)));
            foreach (var option in actual)
            {
                Assert.That(option.SlotOptions.Select(slot => slot.CityBoardSlotIndex), Is.EqualTo(Enumerable.Range(0, 12)));
                Assert.That(option.PaymentOptions.Select(payment => payment.PaymentMode), Is.EqualTo(new[] { "resources", "gold" }));
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(6)]
        public void SupplyQuery_WhenPlayerIsMissing_MaterializesOuterSupplyOnlyOnce(int count)
        {
            var state = CreateScenario("mixed");
            var supply = new ProjectionProbe<string>(Enumerable.Repeat(FacilityCardDatabase.TradeDistrict, count));
            state.Decks.FacilitySupply = supply.AsProjection();

            var result = new BuildFacilityOptionQueryService().Query(state, 99);

            Assert.That(result.Count, Is.EqualTo(count));
            Assert.That(supply.Calls, Is.EqualTo(1));
            Assert.That(result.All(option => option.Validation.ErrorCode == CommandErrorCode.InvalidPlayer), Is.True);
        }

        [Test]
        public void SupplyQuery_WithSixValidFacilities_MaterializesEachProjectionOncePerWholeQuery()
        {
            var state = CreateScenario("mixed");
            state.Decks.FacilitySupply = new List<string>(Enumerable.Repeat(FacilityCardDatabase.TradeDistrict, 6));
            Assert.That(FacilityCardDatabase.Get(FacilityCardDatabase.TradeDistrict).Unique, Is.False);
            var probes = AttachProjections(state);

            var result = new BuildFacilityOptionQueryService().Query(state, 1);

            Assert.That(result.Count, Is.EqualTo(6));
            Assert.That(result.All(option => option.CanBuild && option.SlotOptions.Count == 12 && option.PaymentOptions.Count == 2), Is.True);
            Assert.That(probes.Supply.Calls, Is.EqualTo(1));
            Assert.That(probes.Facilities.Calls, Is.EqualTo(1));
            Assert.That(probes.Built.Calls, Is.EqualTo(1));
        }

        [TestCase(0, false)]
        [TestCase(1, true)]
        [TestCase(11, false)]
        public void SlotValidation_WithProjectedFacilities_ReadsOnceAndKeepsPlayerAndSlotSemantics(int slot, bool isValid)
        {
            var state = CreateScenario("mixed");
            var probe = new ProjectionProbe<FacilityPlacement>(state.Map.Facilities);
            state.Map.Facilities = probe.AsProjection();

            var result = new BuildFacilityService().ValidateCityBoardSlot(state, 1, FacilityCardDatabase.TradeDistrict, slot);

            Assert.That(result.IsValid, Is.EqualTo(isValid));
            if (!isValid) Assert.That(result.ErrorCode, Is.EqualTo(CommandErrorCode.OccupiedSlot));
            Assert.That(probe.Calls, Is.EqualTo(1));
        }

        [Test]
        public void SlotValidation_WhenSeveralArgumentsAreInvalid_PreservesFailurePriorityWithoutReadingPlacements()
        {
            var state = CreateScenario("mixed");
            var probe = new ProjectionProbe<FacilityPlacement>(state.Map.Facilities);
            state.Map.Facilities = probe.AsProjection();
            var service = new BuildFacilityService();

            Assert.That(service.ValidateCityBoardSlot(state, 99, "missing", -1).ErrorCode, Is.EqualTo(CommandErrorCode.InvalidPlayer));
            Assert.That(service.ValidateCityBoardSlot(state, 1, "missing", -1).Reason, Is.EqualTo("未知设施牌。"));
            Assert.That(service.ValidateCityBoardSlot(state, 1, FacilityCardDatabase.TradeDistrict, -1).Reason, Is.EqualTo("城市面板槽位无效。"));
            Assert.That(probe.Calls, Is.Zero);
        }

        [Test]
        public void CostQuery_WithMixedDuplicateAndUnknownDefinitions_ReadsBuiltProjectionOnceAndPreservesEveryResource()
        {
            var state = CreateScenario("mixed");
            var player = state.FindPlayer(1);
            var definition = FacilityCardDatabase.Get(FacilityCardDatabase.CityIndustrialDistrict);
            var service = new FacilityBuildCostService();
            var expected = service.GetEffectiveResourceCost(state, player, definition);
            var definitionBefore = JsonConvert.SerializeObject(definition);
            var probe = new ProjectionProbe<string>(player.BuiltFacilityIds);
            player.BuiltFacilityIds = probe.AsProjection();

            var actual = service.GetEffectiveResourceCost(state, player, definition);

            Assert.That(probe.Calls, Is.EqualTo(1));
            Assert.That(JsonConvert.SerializeObject(actual), Is.EqualTo(JsonConvert.SerializeObject(expected)));
            Assert.That(actual.Originium, Is.EqualTo(1));
            Assert.That(actual.OriginiumShard, Is.EqualTo(3));
            Assert.That(actual.GoldVoucher, Is.EqualTo(2));
            actual.Originium = 99;
            Assert.That(JsonConvert.SerializeObject(definition), Is.EqualTo(definitionBefore));
            Assert.That(service.GetEffectiveResourceCost(state, player, definition).Originium, Is.EqualTo(1));
        }

        [Test]
        public void Queries_WhenDataChangesWithoutRevisionChange_ReadNewSupplyPlacementsColorsAndPaymentAvailability()
        {
            var state = CreateScenario("mixed");
            state.FindPlayer(1).BuiltFacilityIds.Clear();
            var probes = AttachProjections(state);
            var revision = state.EffectRuntime.StateRevision;
            var service = new BuildFacilityOptionQueryService();
            var first = service.Query(state, 1, FacilityCardDatabase.CityIndustrialDistrict);
            Assert.That(first.SlotOptions[0].IsLegal, Is.False);
            Assert.That(first.EffectiveResourceCost.Originium, Is.EqualTo(4));
            Assert.That(first.ResourcesPayment.IsAvailable, Is.True);

            probes.Supply.Values.Clear();
            probes.Supply.Values.Add(FacilityCardDatabase.CityIndustrialDistrict);
            probes.Facilities.Values.RemoveAll(placement => placement.PlayerId == 1);
            probes.Built.Values.AddRange(new[] { "building_014", "building_028", "building_032" });
            state.FindPlayer(1).Resources = new ResourceSet();
            var next = service.Query(state, 1);

            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision));
            Assert.That(next.Count, Is.EqualTo(1));
            Assert.That(next[0].FacilityId, Is.EqualTo(FacilityCardDatabase.CityIndustrialDistrict));
            Assert.That(next[0].SlotOptions.All(slot => slot.IsLegal), Is.True);
            Assert.That(next[0].EffectiveResourceCost.Originium, Is.EqualTo(1));
            Assert.That(next[0].ResourcesPayment.IsAvailable, Is.False);
            Assert.That(next[0].GoldPayment.IsAvailable, Is.False);
            Assert.That(next[0].Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientResource));
            Assert.That(first.EffectiveResourceCost.Originium, Is.EqualTo(4));
        }

        [Test]
        public void AdditionalAndReserveQueries_WithProjectedLists_PreserveFullResultsAfterMainActionIsSpent()
        {
            var plain = CreateScenario("spent");
            var projected = CreateScenario("spent");
            AttachProjections(projected);
            var service = new BuildFacilityOptionQueryService();

            var additional = service.QueryForAdditionalBuild(projected, 1, FacilityCardDatabase.TradeDistrict);
            Assert.That(JsonConvert.SerializeObject(additional), Is.EqualTo(JsonConvert.SerializeObject(service.QueryForAdditionalBuild(plain, 1, FacilityCardDatabase.TradeDistrict))));
            Assert.That(additional.CanBuild, Is.True);
            Assert.That(additional.SlotOptions.Count, Is.EqualTo(12));
            Assert.That(additional.PaymentOptions.Count, Is.EqualTo(2));
            var reserveSlots = service.GetLegalReserveBuildSlots(projected, 1, FacilityCardDatabase.ExtensionHubBlue);
            Assert.That(reserveSlots, Is.EqualTo(service.GetLegalReserveBuildSlots(plain, 1, FacilityCardDatabase.ExtensionHubBlue)));
            Assert.That(reserveSlots, Is.EqualTo(Enumerable.Range(1, 10)));
        }

        [Test]
        public void SupplyQuery_WithSeparateInstancesOfSameDefinition_PreservesSixSlotsAndSeparateItems()
        {
            var state = CreateEntityScenario();
            var supply = state.Decks.FacilitySupply.ToArray();
            var before = JsonConvert.SerializeObject(state);
            var service = new BuildFacilityOptionQueryService();
            var result = service.Query(state, 1);

            Assert.That(result.Count, Is.EqualTo(6));
            Assert.That(supply[0], Is.EqualTo(FacilityCardDatabase.TradeDistrict));
            Assert.That(supply[5], Is.EqualTo(FacilityCardDatabase.TradeDistrict));
            var leftZone = state.ItemZones.Zones.Find(zone => zone.LocalZoneId == "facility_supply_0");
            var rightZone = state.ItemZones.Zones.Find(zone => zone.LocalZoneId == "facility_supply_5");
            var leftItem = state.ItemZones.Items.Find(item => item.ZoneId == leftZone.ZoneId);
            var rightItem = state.ItemZones.Items.Find(item => item.ZoneId == rightZone.ZoneId);
            Assert.That(leftItem.ItemId, Is.Not.EqualTo(rightItem.ItemId));
            Assert.That(leftItem.DefinitionId, Is.EqualTo(rightItem.DefinitionId));
            Assert.That(result.Select(option => option.FacilityId), Is.EqualTo(supply));
            Assert.That(result[0].CanBuild && result[5].CanBuild, Is.True);
            Assert.That(service.Query(state, 1, leftItem.ItemId).FacilityId, Is.EqualTo(leftItem.ItemId));
            Assert.That(service.Query(state, 1, rightItem.ItemId).FacilityId, Is.EqualTo(rightItem.ItemId));
            for (var index = 0; index < 6; index++)
                Assert.That(JsonConvert.SerializeObject(result[index]), Is.EqualTo(JsonConvert.SerializeObject(service.Query(state, 1, supply[index]))));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
        }

        [Test]
        public void Queries_WithNullArguments_PreserveParameterNamesAndPriority()
        {
            var state = CreateScenario("mixed");
            var player = state.FindPlayer(1);
            var costService = new FacilityBuildCostService();
            var queryService = new BuildFacilityOptionQueryService();
            Assert.That(Assert.Throws<ArgumentNullException>(() => queryService.Query(null, 1)).ParamName, Is.EqualTo("state"));
            Assert.That(Assert.Throws<ArgumentNullException>(() => costService.GetEffectiveResourceCost(null, null, null)).ParamName, Is.EqualTo("state"));
            Assert.That(Assert.Throws<ArgumentNullException>(() => costService.GetEffectiveResourceCost(state, null, null)).ParamName, Is.EqualTo("player"));
            Assert.That(Assert.Throws<ArgumentNullException>(() => costService.GetEffectiveResourceCost(state, player, null)).ParamName, Is.EqualTo("facility"));
        }

        [Test]
        public void ReadOnlyQuery_WhenScopeEndsOrThrows_RejectsFurtherReadsAndStartsWithoutProjection()
        {
            var state = CreateScenario("mixed");
            var probes = AttachProjections(state);
            var service = new BuildFacilityService();
            BuildFacilityService.ReadOnlyQuery escaped = null;
            service.WithReadOnlyQuery(state, 1, null, query => { escaped = query; return 0; });
            Assert.That(probes.Supply.Calls + probes.Facilities.Calls + probes.Built.Calls, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => escaped.ValidateFacility(FacilityCardDatabase.TradeDistrict));
            Assert.Throws<InvalidOperationException>(() => escaped.ValidateCityBoardSlot(FacilityCardDatabase.TradeDistrict, 1));
            Assert.Throws<InvalidOperationException>(() => escaped.ValidatePaymentMode(FacilityCardDatabase.TradeDistrict, "resources"));
            Assert.Throws<InvalidOperationException>(() => escaped.GetEffectiveResourceCost(FacilityCardDatabase.TradeDistrict));
            var error = new InvalidOperationException("callback-error");
            Assert.That(Assert.Throws<InvalidOperationException>(() => service.WithReadOnlyQuery<int>(state, 1, null, query =>
            { escaped = query; throw error; })), Is.SameAs(error));
            Assert.Throws<InvalidOperationException>(() => escaped.ValidateFacility(FacilityCardDatabase.TradeDistrict));
        }

        [Test]
        public void ReadOnlyQuery_FailurePriorityAndNonUniqueQualification_DoNotReadUnneededProjections()
        {
            var state = CreateScenario("mixed");
            var probes = AttachProjections(state);
            var service = new BuildFacilityService();
            service.WithReadOnlyQuery(state, 99, null, query =>
            {
                Assert.That(query.ValidateCityBoardSlot("missing", -1).ErrorCode, Is.EqualTo(CommandErrorCode.InvalidPlayer));
                return 0;
            });
            service.WithReadOnlyQuery(state, 1, null, query =>
            {
                Assert.That(query.ValidateCityBoardSlot("missing", -1).Reason, Is.EqualTo("未知设施牌。"));
                Assert.That(probes.Supply.Calls, Is.Zero);
                Assert.That(query.ValidateFacility(FacilityCardDatabase.TradeDistrict).IsValid, Is.True);
                Assert.That(query.ValidateCityBoardSlot(FacilityCardDatabase.TradeDistrict, -1).Reason, Is.EqualTo("城市面板槽位无效。"));
                Assert.That(query.ValidatePaymentMode(FacilityCardDatabase.TradeDistrict, "invalid").Reason, Is.EqualTo("建设支付方式无效。"));
                return 0;
            });
            Assert.That(probes.Supply.Calls, Is.EqualTo(1));
            Assert.That(probes.Facilities.Calls, Is.Zero);
            Assert.That(probes.Built.Calls, Is.Zero);
        }

        [Test]
        public void ReadOnlyQuery_RepeatedCostsAndLaterQueries_ReturnIndependentCostsAndFreshColors()
        {
            var state = CreateScenario("mixed");
            var probes = AttachProjections(state);
            var service = new BuildFacilityService();
            ResourceSet first = null;
            service.WithReadOnlyQuery(state, 1, null, query =>
            {
                first = query.GetEffectiveResourceCost(FacilityCardDatabase.CityIndustrialDistrict);
                var second = query.GetEffectiveResourceCost(FacilityCardDatabase.CityIndustrialDistrict);
                Assert.That(second, Is.Not.SameAs(first));
                first.Originium = 99;
                Assert.That(second.Originium, Is.EqualTo(1));
                Assert.That(query.GetEffectiveResourceCost(FacilityCardDatabase.CityIndustrialDistrict).Originium, Is.EqualTo(1));
                return 0;
            });
            Assert.That(probes.Built.Calls, Is.EqualTo(1));
            probes.Built.Values.Clear();
            Assert.That(service.GetEffectiveResourceCost(state, 1, FacilityCardDatabase.CityIndustrialDistrict).Originium, Is.EqualTo(4));
            service.WithReadOnlyQuery(state, 1, null, query =>
            { Assert.That(query.GetEffectiveResourceCost(FacilityCardDatabase.CityIndustrialDistrict).Originium, Is.EqualTo(4)); return 0; });
            Assert.That(first.Originium, Is.EqualTo(99));
        }

        [TestCase("source")]
        [TestCase("slot")]
        [TestCase("resources")]
        public void FormalBuild_AfterQueryWithoutRevisionChange_RevalidatesLatestState(string changed)
        {
            var state = CreateScenario("mixed");
            var service = BuildFacilityService.CreateForEffectTree();
            var option = new BuildFacilityOptionQueryService(service).Query(state, 1, FacilityCardDatabase.TradeDistrict);
            Assert.That(option.CanBuild, Is.True);
            var revision = state.EffectRuntime.StateRevision;
            if (changed == "source") state.Decks.FacilitySupply.RemoveAll(id => id == FacilityCardDatabase.TradeDistrict);
            if (changed == "slot") state.Map.Facilities.Add(new FacilityPlacement { PlayerId = 1, FacilityCardId = FacilityCardDatabase.TradeDistrict, CityBoardSlotIndex = 1 });
            if (changed == "resources") state.FindPlayer(1).Resources = new ResourceSet();
            var before = JsonConvert.SerializeObject(state);
            var result = service.BuildForEffectTree(state, 1, FacilityCardDatabase.TradeDistrict, 1, "resources");
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(changed == "source" ? CommandErrorCode.InvalidTarget : changed == "slot" ? CommandErrorCode.OccupiedSlot : CommandErrorCode.InsufficientResource));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(before));
            Assert.That(state.EffectRuntime.StateRevision, Is.EqualTo(revision));
            Assert.That(option.CanBuild, Is.True, "旧DTO保持原观察结果，正式建设必须自行复验。");
        }

        [Test]
        public void ReadOnlyQuery_ThrowingPlacementsAndBrokenCost_KeepOriginalFirstReadOrder()
        {
            var state = CreateScenario("mixed");
            var error = new InvalidOperationException("placement-projection-error");
            state.Map.Facilities = new LegacyPositionList<FacilityPlacement>();
            state.Map.Facilities.Bind(() => { throw error; });
            new BuildFacilityService().WithReadOnlyQuery(state, 1, null, query =>
            {
                Assert.That(query.ValidateCityBoardSlot(FacilityCardDatabase.TradeDistrict, -1).Reason, Is.EqualTo("城市面板槽位无效。"));
                Assert.That(Assert.Throws<InvalidOperationException>(() => query.ValidateCityBoardSlot(FacilityCardDatabase.TradeDistrict, 1)), Is.SameAs(error));
                return 0;
            });
            state = CreateScenario("mixed");
            var facility = GameStateCloneService.DeepClone(FacilityCardDatabase.Get(FacilityCardDatabase.TradeDistrict));
            facility.ResourceCost = null;
            state.ContentDefinitions = new GameContentDefinitions { Facilities = new List<FacilityCardDefinition> { facility } };
            int builtReads = 0;
            state.FindPlayer(1).BuiltFacilityIds = new LegacyPositionList<string>();
            state.FindPlayer(1).BuiltFacilityIds.Bind(() => { builtReads++; throw new InvalidOperationException("built-projection-error"); });
            new BuildFacilityService().WithReadOnlyQuery(state, 1, null, query =>
            { Assert.Throws<NullReferenceException>(() => query.GetEffectiveResourceCost(FacilityCardDatabase.TradeDistrict)); return 0; });
            Assert.That(builtReads, Is.Zero, "先克隆本牌费用，再读取已建颜色。");
        }

        private static GameState CreateScenario(string scenario)
        {
            var state = new GameState
            {
                GameId = "game.build_query_contract", MapId = StaticMapDefinitions.ThreePlayerMapId,
                Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1, CurrentPlayerId = 1,
                Players = new List<PlayerState>
                {
                    new PlayerState { PlayerId = 1, Color = PlayerColor.Blue, Resources = new ResourceSet { Originium = 100, OriginiumShard = 100, Iron = 100, PureOriginium = 100, GoldVoucher = 100 } },
                    new PlayerState { PlayerId = 2, Color = PlayerColor.Red }
                }
            };
            state.Decks.FacilitySupply.AddRange(new[] { FacilityCardDatabase.TradeDistrict, string.Empty, FacilityCardDatabase.TradeDistrict, FacilityCardDatabase.CityIndustrialDistrict, "missing", "building_008" });
            state.FindPlayer(1).BuiltFacilityIds.AddRange(new[] { "building_014", "building_018", "building_028", "building_032", FacilityCardDatabase.CoreCommandTower, "building_001", "missing" });
            state.Map.Facilities.AddRange(new[]
            {
                new FacilityPlacement { PlayerId = 1, FacilityCardId = FacilityCardDatabase.CoreCommandTower, CityBoardSlotIndex = 0 },
                new FacilityPlacement { PlayerId = 2, FacilityCardId = FacilityCardDatabase.TradeDistrict, CityBoardSlotIndex = 1 },
                new FacilityPlacement { PlayerId = 1, FacilityCardId = FacilityCardDatabase.SourceStoneRefinery, CityBoardSlotIndex = 11 }
            });
            if (scenario == "unique") state.FindPlayer(1).BuiltFacilityIds.Add(FacilityCardDatabase.FederalOffice);
            if (scenario == "full")
                for (var index = 1; index < 11; index++) state.Map.Facilities.Add(new FacilityPlacement { PlayerId = 1, FacilityCardId = FacilityCardDatabase.TradeDistrict, CityBoardSlotIndex = index });
            if (scenario == "insufficient") state.FindPlayer(1).Resources = new ResourceSet();
            if (scenario == "gold") state.FindPlayer(1).Resources = new ResourceSet { GoldVoucher = 100 };
            if (scenario == "spent") state.FindPlayer(1).ActedMainActionThisTurn = true;
            if (scenario == "other_player") state.CurrentPlayerId = 2;
            if (scenario == "wrong_phase") state.Phase = GamePhase.Setup;
            return state;
        }

        private static GameState CreateEntityScenario()
        {
            var source = CreateScenario("mixed");
            source.Decks.FacilitySupply.Clear();
            source.FindPlayer(1).BuiltFacilityIds.Clear();
            source.Map.Facilities.Clear();
            source.MapId = "map.build_query_contract";
            source.RuleConfiguration = new GameRuleConfiguration { MapId = source.MapId, PlayerCount = 2, ContentPackHash = source.ContentPackHash };
            source.ContentDefinitions = new GameContentDefinitions { Facilities = new List<FacilityCardDefinition> { FacilityCardDatabase.Get(FacilityCardDatabase.TradeDistrict) } };
            var map = new GameMapDefinition { MapId = source.MapId, MinPlayers = 2, MaxPlayers = 4 };
            var recipe = new List<ItemRecipeEntry>
            {
                new ItemRecipeEntry { EntryId = "facility_left", DefinitionId = FacilityCardDatabase.TradeDistrict, TypeId = "core.facility_card", Quantity = 1, InitialZoneRole = "facility_supply_0" },
                new ItemRecipeEntry { EntryId = "facility_right", DefinitionId = FacilityCardDatabase.TradeDistrict, TypeId = "core.facility_card", Quantity = 1, InitialZoneRole = "facility_supply_5" }
            };
            return new ItemZoneStateFactory().CreateForSetup(source, map, recipe);
        }

        private static QueryProbes AttachProjections(GameState state)
        {
            var probes = new QueryProbes
            {
                Supply = new ProjectionProbe<string>(state.Decks.FacilitySupply),
                Facilities = new ProjectionProbe<FacilityPlacement>(state.Map.Facilities),
                Built = new ProjectionProbe<string>(state.FindPlayer(1).BuiltFacilityIds)
            };
            state.Decks.FacilitySupply = probes.Supply.AsProjection();
            state.Map.Facilities = probes.Facilities.AsProjection();
            state.FindPlayer(1).BuiltFacilityIds = probes.Built.AsProjection();
            return probes;
        }

        private sealed class QueryProbes
        {
            public ProjectionProbe<string> Supply;
            public ProjectionProbe<FacilityPlacement> Facilities;
            public ProjectionProbe<string> Built;
        }

        private sealed class ProjectionProbe<T>
        {
            public readonly List<T> Values;
            public int Calls;
            public ProjectionProbe(IEnumerable<T> values) { Values = new List<T>(values); }
            public LegacyPositionList<T> AsProjection()
            {
                var list = new LegacyPositionList<T>();
                list.Bind(() => { Calls++; return Values; });
                return list;
            }
        }
    }
}
