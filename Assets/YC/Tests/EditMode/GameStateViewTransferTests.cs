using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using YC.Domain.CityStyles;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Infrastructure.Multiplayer;

namespace YC.Tests.EditMode
{
    public sealed class GameStateViewTransferTests
    {
        #region CityStyleDeclarationProjectionRepair
        [Test]
        public void NativeCityStyleDeclarationsSurviveTransferAndRejectUsedFacilityInstances()
        {
            var state = CreateCityStyleProjectionState(true);
            var registry = new EffectRegistry { ContentDefinitions = state.ContentDefinitions };
            CityStyleDeclarationEffectExecutor.Register(registry);
            var executor = new EffectTreeExecutor(state, registry);
            foreach (var slots in new[] { new[] { 6, 7 }, new[] { 3, 4 } })
            {
                executor.CreateRoot(CityStyleDeclarationEffectExecutor.Create(1, CityStyleDatabase.MilitaryIndustrialArea, slots));
                Assert.That(executor.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            }
            Assert.That(state.FindPlayer(1).DeclaredCityStyles.Count, Is.EqualTo(2));
            var sourceJson = JsonConvert.SerializeObject(state);
            var own = TransferCityStyleView(GameStateViewProjector.ProjectForPlayer(state, 1));
            var other = TransferCityStyleView(GameStateViewProjector.ProjectForPlayer(state, 2));
            var spectator = TransferCityStyleView(GameStateViewProjector.ProjectForSpectator(state));
            Assert.That(own.Players.Single(p => p.PlayerId == 1).PrivateCardIdsVisible, Is.True);
            Assert.That(other.Players.Single(p => p.PlayerId == 1).PrivateCardIdsVisible, Is.False);
            Assert.That(other.Players.Single(p => p.PlayerId == 1).HandCardIds, Is.Empty);
            Assert.That(spectator.Players.All(p => !p.PrivateCardIdsVisible && p.HandCardIds.Count == 0), Is.True);
            foreach (var view in new[] { own, other, spectator })
            {
                Assert.That(view.HasValidPlayerOrderAndSlots(), Is.True);
                var viewJson = JsonConvert.SerializeObject(view);
                var client = GameStateViewProjector.ToClientState(view);
                // 同局公开内容由原装配目录提供；不从Host复制任何运行态。
                client.ContentDefinitions = state.ContentDefinitions;
                CheckCityStyleRoundTrip(state, client);
                Assert.That(client.FindPlayer(1).DeclaredCityStyles[0].UsedFacilityIds.All(id =>
                    client.Map.Facilities.Any(f => f.ContentInstanceId == id && f.FacilityCardId != id)), Is.True);
                var declarations = client.FindPlayer(1).DeclaredCityStyles;
                var marker = client.ItemZones.Items.Single(item => item.ItemId == declarations[0].InfluenceMarkerId);
                marker.SlotId = CityStyleMarkerAreas.Used;
                Assert.That(declarations[0].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.Used));
                Assert.That(declarations[1].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.Declared));
                client.FindPlayer(1).DeclaredCityStyles[0].UsedFacilityIds.Clear();
                Assert.That(JsonConvert.SerializeObject(view), Is.EqualTo(viewJson));
            }
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(sourceJson));
        }

        [Test]
        public void LegacyCityStyleDeclarationsKeepActualMarkersAndDeclarationOrder()
        {
            var state = CreateCityStyleProjectionState(false);
            var service = new DeclareCityStyleService();
            Assert.That(service.Declare(state, 1, CityStyleDatabase.MilitaryIndustrialArea, new[] { 6, 7 }).Succeeded, Is.True);
            Assert.That(service.Declare(state, 1, CityStyleDatabase.MilitaryIndustrialArea, new[] { 3, 4 }).Succeeded, Is.True);
            var player = state.FindPlayer(1);
            player.DeclaredCityStyles[0].MarkerArea = CityStyleMarkerAreas.Used;
            player.DeclaredCityStyles[0].RemainingSpecialActionUses = 0;
            var sourceJson = JsonConvert.SerializeObject(state);
            var view = TransferCityStyleView(GameStateViewProjector.ProjectForPlayer(state, 1));
            var viewJson = JsonConvert.SerializeObject(view);
            var client = GameStateViewProjector.ToClientState(view);
            client.ContentDefinitions = state.ContentDefinitions;
            CheckCityStyleRoundTrip(state, client);
            Assert.That(client.FindPlayer(1).DeclaredCityStyles.Select(d => d.DeclarationId), Is.EqualTo(new[] { "", "" }));
            Assert.That(client.FindPlayer(1).DeclaredCityStyles[0].MarkerArea, Is.EqualTo(CityStyleMarkerAreas.Used));
            Assert.That(client.FindPlayer(1).DeclaredCityStyles[0].RemainingSpecialActionUses, Is.EqualTo(0));
            client.FindPlayer(1).DeclaredCityStyles[0].UsedCityBoardSlotIndexes.Clear();
            Assert.That(JsonConvert.SerializeObject(view), Is.EqualTo(viewJson));
            Assert.That(JsonConvert.SerializeObject(state), Is.EqualTo(sourceJson));
        }

        [Test]
        public void SummaryOnlyOrContradictoryCityStyleViewsCannotInventDeclarations()
        {
            var view = new GameStateView { LaunchPlayerOrder = new List<int> { 1 },
                Players = new List<PlayerStateView> { new PlayerStateView { PlayerId = 1,
                    DeclaredCityStyleIds = new List<string> { CityStyleDatabase.MilitaryIndustrialArea },
                    CityStyleUsedSlotIndexes = new List<int> { 6, 7 } } } };
            Assert.That(view.HasValidPlayerOrderAndSlots(), Is.False);
            Assert.That(Assert.Throws<InvalidOperationException>(() => GameStateViewProjector.ToClientState(view)).Message,
                Is.EqualTo("city_style_declaration_projection_incomplete"));
            var current = GameStateViewProjector.ProjectForPlayer(CreateCityStyleProjectionState(false), 1);
            Assert.That(current.HasValidPlayerOrderAndSlots(), Is.True);
            var emptyClient = GameStateViewProjector.ToClientState(current);
            Assert.That(emptyClient.FindPlayer(1).DeclaredCityStyles, Is.Empty);
            current.Players[0].DeclaredCityStyles.Add(new CityStyleDeclarationView { CityStyleId = CityStyleDatabase.MilitaryIndustrialArea });
            Assert.That(current.HasValidPlayerOrderAndSlots(), Is.False);
            Assert.Throws<InvalidOperationException>(() => GameStateViewProjector.ToClientState(current));
        }

        private static GameState CreateCityStyleProjectionState(bool native)
        {
            var configuration = new GameRuleConfiguration { PlayerCount = 3, EnabledExpansionIds = new List<string> { "core" } };
            var active = ActiveContentCatalogBuilder.Build(ExternalContentPack.Load(Path.Combine(Environment.CurrentDirectory,
                "Assets", "StreamingAssets", "Content", "core")), configuration);
            configuration.ContentPackHash = active.ContentPackHash;
            var source = new GameState { GameId = "city-style.public-projection", ContentPackHash = active.ContentPackHash,
                ContentDefinitions = active.ContentDefinitions, RuleConfiguration = configuration, MapId = configuration.MapId,
                Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1,
                LaunchPlayerOrder = new List<int> { 1, 2, 3 }, Players = Enumerable.Range(1, 3).Select(id =>
                    new PlayerState { PlayerId = id, Color = (PlayerColor)(id - 1), InfluenceSupply = 6 }).ToList() };
            var blue = source.ContentDefinitions.Facilities.Where(f => f.Color == "blue" || f.Color == "yellow")
                .OrderBy(f => f.FacilityId, StringComparer.Ordinal).Take(3).Select(f => f.FacilityId).ToArray();
            var red = source.ContentDefinitions.Facilities.Where(f => f.Color == "red")
                .OrderBy(f => f.FacilityId, StringComparer.Ordinal).Take(3).Select(f => f.FacilityId).ToArray();
            Assert.That(blue.Length, Is.EqualTo(3)); Assert.That(red.Length, Is.EqualTo(3));
            if (!native)
            {
                for (int pair = 0; pair < 3; pair++)
                    for (int side = 0; side < 2; side++)
                    {
                        var id = side == 0 ? blue[pair] : red[pair];
                        source.FindPlayer(1).BuiltFacilityIds.Add(id);
                        source.Map.Facilities.Add(new FacilityPlacement { PlayerId = 1, FacilityCardId = id,
                            CityBoardSlotIndex = (pair == 0 ? 6 : pair == 1 ? 3 : 0) + side });
                    }
                return source;
            }
            var state = active.SetupRecipe.Assemble(source, StaticMapDefinitions.CreateThreePlayerMap(), 17);
            // 正式配方的初始指挥塔占槽7；先移到空槽，避免夹具把两张设施塞入同一格。
            var initialCell = state.ItemZones.Zones.Single(z => z.OwnerPlayerId == 1 && z.LocalZoneId == "city_cell_7");
            var initialFacility = state.ItemZones.Items.Single(item => item.ZoneId == initialCell.ZoneId);
            PositionProjectionFixtureItem(state, initialFacility,
                state.ItemZones.Zones.Single(z => z.OwnerPlayerId == 1 && z.LocalZoneId == "city_cell_11").ZoneId);
            for (int pair = 0; pair < 3; pair++)
                for (int side = 0; side < 2; side++)
                {
                    var id = side == 0 ? blue[pair] : red[pair];
                    var item = state.ItemZones.Items.Single(i => i.DefinitionId == id && i.TypeId == "core.facility_card");
                    var cell = state.ItemZones.Zones.Single(z => z.OwnerPlayerId == 1 &&
                        z.LocalZoneId == "city_cell_" + ((pair == 0 ? 6 : pair == 1 ? 3 : 0) + side));
                    PositionProjectionFixtureItem(state, item, cell.ZoneId);
                    item.ControllerSubject = ItemSubjectRef.Player(1);
                }
            var styleItem = state.ItemZones.Items.Single(i => i.DefinitionId == CityStyleDatabase.MilitaryIndustrialArea);
            PositionProjectionFixtureItem(state, styleItem, state.ItemZones.Zones.Single(z => z.LocalZoneId == "city_styles").ZoneId);
            Assert.That(new ItemZoneQuery().TryValidate(state, out var reason), Is.True, reason);
            return state;
        }

        private static void PositionProjectionFixtureItem(GameState state, ItemInstanceState item, string zoneId)
        {
            state.ItemZones.Orders.Find(order => order.ZoneId == item.ZoneId)?.ItemIds.Remove(item.ItemId);
            item.ZoneId = zoneId; item.SlotId = "";
            state.ItemZones.Orders.Find(order => order.ZoneId == zoneId)?.ItemIds.Add(item.ItemId);
        }

        private static GameStateView TransferCityStyleView(GameStateView view)
        {
            var bytes = GameStateViewTransfer.Encode(JsonConvert.SerializeObject(view));
            var receiver = new GameStateViewTransfer.Assembler(); string result = null;
            foreach (var part in GameStateViewTransfer.Split(bytes))
                Assert.That(receiver.TryAppend(Math.Max(1, view.StateRevision), true, bytes.Length, part.Offset, part.ToArray(), out result), Is.True);
            Assert.That(result, Is.Not.Null);
            return JsonConvert.DeserializeObject<GameStateView>(result);
        }

        private static void CheckCityStyleRoundTrip(GameState source, GameState client)
        {
            var expected = source.FindPlayer(1).DeclaredCityStyles;
            var actual = client.FindPlayer(1).DeclaredCityStyles;
            Assert.That(actual.Count, Is.EqualTo(expected.Count));
            for (int index = 0; index < expected.Count; index++)
            {
                Assert.That(actual[index].DeclarationId, Is.EqualTo(expected[index].DeclarationId));
                Assert.That(actual[index].CityStyleItemId, Is.EqualTo(expected[index].CityStyleItemId));
                Assert.That(actual[index].InfluenceMarkerId, Is.EqualTo(expected[index].InfluenceMarkerId));
                Assert.That(actual[index].CityStyleId, Is.EqualTo(expected[index].CityStyleId));
                Assert.That(actual[index].UnlockedSpecialActionId, Is.EqualTo(expected[index].UnlockedSpecialActionId));
                Assert.That(actual[index].UsedFacilityIds, Is.EqualTo(expected[index].UsedFacilityIds));
                Assert.That(actual[index].UsedCityBoardSlotIndexes, Is.EqualTo(expected[index].UsedCityBoardSlotIndexes));
                Assert.That(actual[index].MarkerArea, Is.EqualTo(expected[index].MarkerArea));
                Assert.That(actual[index].RemainingSpecialActionUses, Is.EqualTo(expected[index].RemainingSpecialActionUses));
            }
            var style = CityStyleDatabase.Get(source, CityStyleDatabase.MilitaryIndustrialArea);
            var matcher = new CityStylePatternMatcher(); var service = new DeclareCityStyleService();
            foreach (var slots in new[] { new[] { 6, 7 }, new[] { 3, 4 } })
            {
                Assert.That(matcher.MatchSelected(client, 1, style, slots).Succeeded, Is.False);
                Assert.That(service.Validate(client, 1, style.CityStyleId, slots).IsValid, Is.False);
            }
            Assert.That(matcher.MatchSelected(client, 1, style, new[] { 0, 1 }).Succeeded, Is.True);
            Assert.That(service.Validate(client, 1, style.CityStyleId, new[] { 0, 1 }).IsValid, Is.True);
            Assert.That(matcher.Match(client, 1, style).UsedCityBoardSlotIndexes, Is.EqualTo(new[] { 0, 1 }));
            var map = new MapQueryService(StaticMapDefinitions.CreateThreePlayerMap()); var influence = new InfluenceService(map);
            var lifecycle = new SpecialActionLifecycleService();
            var query = new SpecialActionOptionQueryService(map, influence,
                new CityMovementService(map, influence, new TravelCostService(map)), lifecycle);
            var hostOptions = query.Query(source, 1); var clientOptions = query.Query(client, 1);
            Assert.That(JsonConvert.SerializeObject(clientOptions), Is.EqualTo(JsonConvert.SerializeObject(hostOptions)));
            Assert.That(clientOptions.Options.Count, Is.EqualTo(1));
            Assert.That(lifecycle.ValidateAvailable(client.FindPlayer(1), SpecialActionDatabase.Get(client, expected[0].UnlockedSpecialActionId),
                expected[0].InfluenceMarkerId).IsValid, Is.EqualTo(lifecycle.ValidateAvailable(source.FindPlayer(1),
                    SpecialActionDatabase.Get(source, expected[0].UnlockedSpecialActionId), expected[0].InfluenceMarkerId).IsValid));
        }
        #endregion

        [Test]
        public void LargeUnicodeProjectionSurvivesEveryByteBoundaryAndPublishesOnlyWhenComplete()
        {
            string expected = "{\"本人投影\":\"" + string.Concat(Enumerable.Repeat("企业与私密候选𠮷", 50000)) + "\"}";
            var bytes = GameStateViewTransfer.Encode(expected);
            Assert.That(bytes.Length, Is.GreaterThan(65534));
            var receiver = new GameStateViewTransfer.Assembler();
            string actual = null;
            foreach (var part in GameStateViewTransfer.Split(bytes))
            {
                Assert.That(receiver.TryAppend(1, true, bytes.Length, part.Offset, part.ToArray(), out actual), Is.True);
                if (part.Offset + part.Count < bytes.Length) Assert.That(actual, Is.Null);
            }
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void MissingDuplicateOrInterleavedPartsCannotPublishAnIncompleteView()
        {
            var bytes = GameStateViewTransfer.Encode(new string('x', GameStateViewTransfer.ChunkBytes + 9));
            var parts = GameStateViewTransfer.Split(bytes).ToArray();
            var receiver = new GameStateViewTransfer.Assembler();
            Assert.That(receiver.TryAppend(1, true, bytes.Length, parts[1].Offset, parts[1].ToArray(), out var json), Is.False);
            Assert.That(json, Is.Null);
            Assert.That(receiver.TryAppend(1, true, bytes.Length, 0, parts[0].ToArray(), out _), Is.True);
            Assert.That(receiver.TryAppend(2, false, bytes.Length, 0, parts[0].ToArray(), out json), Is.False);
            Assert.That(json, Is.Null);
            Assert.That(receiver.TryAppend(3, true, bytes.Length, 0, parts[0].ToArray(), out _), Is.True);
            Assert.That(receiver.TryAppend(3, true, bytes.Length, 0, parts[0].ToArray(), out json), Is.False);
            Assert.That(json, Is.Null);
        }

        [Test]
        public void BoundsAndInvalidUtf8AreRejectedAndReconnectDropsPartialData()
        {
            var receiver = new GameStateViewTransfer.Assembler();
            Assert.That(receiver.TryAppend(1, true, GameStateViewTransfer.MaximumBytes + 1, 0, new byte[] { 65 }, out _), Is.False);
            Assert.That(receiver.TryAppend(1, true, 1, int.MaxValue, new byte[] { 65 }, out _), Is.False);
            Assert.That(receiver.TryAppend(1, true, 1, 0, new byte[] { 255 }, out _), Is.False);
            Assert.That(receiver.TryAppend(1, true, 2, 0, new byte[] { 65 }, out _), Is.True);
            receiver.Reset();
            Assert.That(receiver.TryAppend(1, true, 2, 1, new byte[] { 66 }, out _), Is.False);
            Assert.That(receiver.TryAppend(2, true, 1, 0, new byte[] { 67 }, out var restored), Is.True);
            Assert.That(restored, Is.EqualTo("C"));
        }
    }
}
