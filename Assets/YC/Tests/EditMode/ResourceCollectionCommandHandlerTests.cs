using NUnit.Framework;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.Commands;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Effects;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class ResourceCollectionCommandHandlerTests
    {
        [Test]
        public void CollectResource_WithMultipleTargets_SharesRouteCostPaysChosenRecipientAndAdvancesToCleanup()
        {
            var state = CreateCollectionState();
            var map = StaticMapDefinitions.CreateFourPlayerMap();
            state.MapId = map.MapId;
            state.FindPlayer(1).CityLocationId = "C-01";
            state.Players.Add(new PlayerState { PlayerId = 3, Color = PlayerColor.Green });
            state.FindPlayer(1).Resources.GoldVoucher = 6;
            state.FindPlayer(2).HasCollectedResourcesThisRound = true;
            state.FindPlayer(3).HasCollectedResourcesThisRound = true;
            AddToken(state, "B-01", ResourceType.Iron, 1);
            AddToken(state, "A-02", ResourceType.OriginiumShard, 2);
            AddLocationInfluence(state, 1, "B-01");
            AddLocationInfluence(state, 1, "A-02");
            AddRouteInfluence(state, 2, "C2", 0);
            AddRouteInfluence(state, 3, "C2", 1);
            AddRouteInfluence(state, 1, "B2", 0);
            PrepareLegacyBudgets(state);
            state = AssembleEntityFixture(state, map);
            var handler = new CollectResourceCommandHandler(new ResourceCollectionService(new MapQueryService(map), new ResourceTokenService()));

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.CollectResource,
                PlayerId = 1,
                Parameters =
                {
                    { CollectResourceCommandHandler.LocationIdsParameter, "B-01,A-02" },
                    { CollectResourceCommandHandler.RouteIdsParameter, "C2,B2,R6" },
                    { CollectResourceCommandHandler.PaymentRecipientsParameter, "C2=3" }
                }
            });

            Assert.That(result.Succeeded, Is.True, result.Validation == null ? string.Empty : result.Validation.Reason);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(1));
            Assert.That(state.FindPlayer(1).Resources.OriginiumShard, Is.EqualTo(2));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.FindPlayer(3).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).HasCollectedResourcesThisRound, Is.False, "下一轮已重置采集标记。");
            Assert.That(state.Round, Is.EqualTo(2));
            Assert.That(state.Phase, Is.EqualTo(GamePhase.CharacterCover));
            Assert.That(result.Events[0].Data["paymentCount"], Is.EqualTo("2"));
        }

        [Test]
        public void CollectResource_WhenUsingGoldGainedDuringCollectionPhaseToPay_FailsWithoutReward()
        {
            var state = CreateCollectionState();
            state.FindPlayer(1).Resources.GoldVoucher = 2;
            state.FindPlayer(1).ResourceCollectionStartGoldVoucher = 0;
            AddToken(state, "mine-c", ResourceType.GoldVoucher, 3);
            AddLocationInfluence(state, 1, "mine-c");
            AddRouteInfluence(state, 1, "route-hub-mine", 0);
            PrepareLegacyBudgets(state);
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.CollectResource,
                PlayerId = 1,
                TargetId = "mine-c",
                Parameters = { { CollectResourceCommandHandler.RouteIdsParameter, "route-a-hub,route-hub-mine" } }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InsufficientResource));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(2));
            Assert.That(state.FindPlayer(1).HasCollectedResourcesThisRound, Is.False);
            Assert.That(state.Phase, Is.EqualTo(GamePhase.ResourceCollection));
        }

        [Test]
        public void CollectResource_WithInvalidRecipient_FailsWithoutPaying()
        {
            var state = CreateCollectionState();
            state.FindPlayer(1).Resources.GoldVoucher = 4;
            AddToken(state, "mine-c", ResourceType.Iron, 1);
            AddLocationInfluence(state, 1, "mine-c");
            AddRouteInfluence(state, 2, "route-a-hub", 0);
            PrepareLegacyBudgets(state);
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.CollectResource,
                PlayerId = 1,
                TargetId = "mine-c",
                Parameters =
                {
                    { CollectResourceCommandHandler.RouteIdsParameter, "route-a-hub,route-hub-mine" },
                    { CollectResourceCommandHandler.PaymentRecipientsParameter, "route-a-hub=3" }
                }
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.EqualTo(4));
            Assert.That(state.FindPlayer(2).Resources.GoldVoucher, Is.EqualTo(0));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(0));
        }

        [Test]
        public void CollectResource_WhenTargetHasNoOwnInfluenceOrCity_Fails()
        {
            var state = CreateCollectionState();
            state.FindPlayer(1).Resources.GoldVoucher = 4;
            AddToken(state, "mine-c", ResourceType.Iron, 1);
            PrepareLegacyBudgets(state);
            var handler = CreateHandler();

            var result = handler.Handle(state, new GameCommand
            {
                Kind = GameCommandKind.CollectResource,
                PlayerId = 1,
                TargetId = "mine-c"
            });

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Validation.ErrorCode, Is.EqualTo(CommandErrorCode.InvalidTarget));
            Assert.That(state.FindPlayer(1).Resources.Iron, Is.EqualTo(0));
        }

        // 将旧测试的资源和影响力配置装配成当前规则要求的实体；只在隔离夹具准备时执行。
        private static GameState AssembleEntityFixture(GameState legacy, GameMapDefinition map)
        {
            var placements = legacy.Map.Influences.ToArray();
            var tokens = legacy.Map.ResourceTokens.ToArray();
            var city = legacy.FindPlayer(1).CityLocationId;
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            if (!CharacterCardDatabase.IsInitialized) CharacterCardDatabase.Initialize(pack.CreateCharacters());
            legacy.GameId = "legacy-collection-command-fixture";
            legacy.ContentPackHash = pack.ContentHash;
            legacy.RuleConfiguration = new GameRuleConfiguration { MapId = map.MapId, PlayerCount = legacy.Players.Count, ContentPackHash = pack.ContentHash };
            legacy.ContentDefinitions = new GameContentDefinitions { Characters = pack.CreateCharacters().ToList() };
            var recipe = new List<ItemRecipeEntry>
            {
                new ItemRecipeEntry { EntryId = "characters", DefinitionId = "texas", TypeId = "core.basic_character_card", Quantity = 2, PerPlayer = true, InitialZoneRole = "hand" },
                new ItemRecipeEntry { EntryId = "cities", DefinitionId = "core.mobile_city", TypeId = "core.mobile_city", Quantity = 1, PerPlayer = true, InitialZoneRole = "city_reserve" },
                new ItemRecipeEntry { EntryId = "markers", DefinitionId = "core.component.marker", TypeId = "core.player_marker", Quantity = 30, PerPlayer = true, InitialZoneRole = "supply" }
            };
            foreach (var token in tokens)
                recipe.Add(new ItemRecipeEntry { EntryId = "resource-" + token.LocationId, DefinitionId = "core.resource_point", TypeId = "core.resource_point_token", Quantity = 1, InitialZoneRole = "location/" + token.LocationId });
            var state = new ItemZoneStateFactory().CreateForSetup(legacy, map, recipe);
            foreach (var id in tokens.Select(t => t.LocationId).Append(city).Distinct())
                if (!state.Map.OpenLocationIds.Contains(id)) state.Map.OpenLocationIds.Add(id);
            var move = typeof(ItemZoneStateMigrator).GetMethod("SetPosition", BindingFlags.Static | BindingFlags.NonPublic);
            var cityParent = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == city);
            var dock = state.ItemZones.Zones.Single(z => z.ParentId == cityParent.ZoneId && z.TemplateId == "core.zone.city_dock");
            var cityItem = state.ItemZones.Items.Single(i => i.TypeId == "core.mobile_city" && i.OwnerSubject.PlayerId == 1);
            move.Invoke(null, new object[] { state, cityItem, dock.ZoneId });
            foreach (var token in tokens)
            {
                var parent = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == token.LocationId);
                var zone = state.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == "core.zone.resource_point_markers");
                var item = state.ItemZones.Items.Single(i => i.ZoneId == zone.ZoneId && i.TypeId == "core.resource_point_token");
                item.LocalState = ItemTransferEffectExecutor.Object("resourceType", NormalizedValue.CreateInteger((int)token.ResourceType), "amount", NormalizedValue.CreateInteger(token.Amount));
            }
            foreach (var placement in placements)
            {
                string id = string.IsNullOrEmpty(placement.LocationId) ? placement.RouteId : placement.LocationId;
                string type = string.IsNullOrEmpty(placement.LocationId) ? "core.zone.map_route" : "core.zone.map_location";
                var parent = state.ItemZones.Zones.Single(z => z.TemplateId == type && z.LocalZoneId == id);
                var zone = state.ItemZones.Zones.Single(z => z.ParentId == parent.ZoneId && z.TemplateId == "core.zone.influence_slots");
                var supply = state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.player_supply" && z.OwnerPlayerId == placement.PlayerId);
                var item = state.ItemZones.Items.First(i => i.ZoneId == supply.ZoneId && i.TypeId == "core.player_marker");
                move.Invoke(null, new object[] { state, item, zone.ZoneId });
                item.SlotId = placement.SlotId;
            }
            Assert.That(new ItemZoneQuery().TryValidate(state, out var reason), Is.True, reason);
            Assert.That(state.Map.Influences.Count, Is.EqualTo(placements.Length));
            return state;
        }

        private static void PrepareLegacyBudgets(GameState state)
        {
            foreach (var player in state.Players)
                if (player.ResourceCollectionStartGoldVoucher < 0)
                    player.ResourceCollectionStartGoldVoucher = player.Resources.GoldVoucher;
        }

        private static CollectResourceCommandHandler CreateHandler()
        {
            var mapQuery = new MapQueryService(CreateCollectionMap());
            return new CollectResourceCommandHandler(new ResourceCollectionService(mapQuery, new ResourceTokenService()));
        }

        private static GameState CreateCollectionState()
        {
            return new GameState
            {
                MapId = "resource-collection-test",
                Phase = GamePhase.ResourceCollection,
                Round = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Red,
                        CityLocationId = "city-a"
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Color = PlayerColor.Blue
                    }
                }
            };
        }

        private static GameMapDefinition CreateCollectionMap()
        {
            return new GameMapDefinition
            {
                MapId = "resource-collection-test",
                MinPlayers = 2,
                MaxPlayers = 4,
                Locations = new List<MapLocationDefinition>
                {
                    CreateLocation("city-a"),
                    CreateLocation("hub-b"),
                    CreateLocation("mine-c"),
                    CreateLocation("quarry-d")
                },
                Routes = new List<MapRouteDefinition>
                {
                    CreateRoute("route-a-hub", "city-a", "hub-b", 2),
                    CreateRoute("route-hub-mine", "hub-b", "mine-c", 2),
                    CreateRoute("route-hub-quarry", "hub-b", "quarry-d", 2)
                }
            };
        }

        private static MapLocationDefinition CreateLocation(string locationId)
        {
            return new MapLocationDefinition
            {
                LocationId = locationId,
                ResourceType = ResourceType.Iron,
                CanDockCity = true,
                ResourceSlotCount = 1,
                InfluenceSlotCount = 2
            };
        }

        private static MapRouteDefinition CreateRoute(string routeId, string fromLocationId, string toLocationId, int cost)
        {
            return new MapRouteDefinition
            {
                RouteId = routeId,
                FromLocationId = fromLocationId,
                ToLocationId = toLocationId,
                InfluenceSlotCount = 2,
                BaseCost = cost
            };
        }

        private static void AddToken(GameState state, string locationId, ResourceType resourceType, int amount)
        {
            state.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = locationId,
                ResourceType = resourceType,
                Amount = amount
            });
        }

        private static void AddLocationInfluence(GameState state, int playerId, string locationId)
        {
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = playerId,
                SlotId = InfluenceService.GetLocationSlotId(locationId, 0),
                LocationId = locationId
            });
        }

        private static void AddRouteInfluence(GameState state, int playerId, string routeId, int slotIndex)
        {
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = playerId,
                SlotId = InfluenceService.GetRouteSlotId(routeId, slotIndex),
                RouteId = routeId
            });
        }
    }
}
