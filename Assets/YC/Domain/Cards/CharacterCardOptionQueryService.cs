using System;
using System.Collections.Generic;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Movement;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Domain.Cards
{
    public sealed class CharacterCardOptionQueryService
    {
        public CharacterCardOptionQueryResult Query(
            GameState state,
            int playerId,
            CharacterCardEffectKind effect,
            IReadOnlyDictionary<string, string> selectedParameters = null)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var result = new CharacterCardOptionQueryResult();
            switch (effect)
            {
                case CharacterCardEffectKind.CannotRequisition:
                    AddResource(result, CharacterEffectParameterKeys.ResourceType, ResourceType.Originium);
                    AddResource(result, CharacterEffectParameterKeys.ResourceType, ResourceType.OriginiumShard);
                    AddResource(result, CharacterEffectParameterKeys.ResourceType, ResourceType.Iron);
                    break;
                case CharacterCardEffectKind.LiskarmSecurityProtocol:
                    QueryPlacementSlots(state, playerId, selectedParameters, result);
                    break;
                case CharacterCardEffectKind.LiskarmControlPosition:
                    QueryOpponentInfluences(state, playerId, result);
                    break;
                case CharacterCardEffectKind.ElysiumLogistics:
                    QueryMinimumResources(state, playerId, result);
                    break;
                case CharacterCardEffectKind.ElysiumNavigation:
                    QueryRaidLocations(state, playerId, result);
                    break;
                case CharacterCardEffectKind.TexasSpecialDelivery:
                    QueryFacilitySupply(state, result);
                    break;
                case CharacterCardEffectKind.TexasRemoveAndDoubleMove:
                    QueryTexasRemoveAndMove(state, playerId, selectedParameters, result);
                    break;
                case CharacterCardEffectKind.TinManEstablishPrestige:
                    QueryTinManStrategySummary(selectedParameters, result);
                    break;
            }

            return result;
        }

        private static void QueryTinManStrategySummary(
            IReadOnlyDictionary<string, string> selectedParameters,
            CharacterCardOptionQueryResult result)
        {
            var purchase12 = string.Equals(
                Get(selectedParameters, CharacterEffectParameterKeys.TinManPurchasePureOriginium12),
                "true",
                StringComparison.OrdinalIgnoreCase);
            var purchase15 = string.Equals(
                Get(selectedParameters, CharacterEffectParameterKeys.TinManPurchasePureOriginium15),
                "true",
                StringComparison.OrdinalIgnoreCase);
            var purchaseCount = (purchase12 ? 1 : 0) + (purchase15 ? 1 : 0);
            var cost = (purchase12 ? 12 : 0) + (purchase15 ? 15 : 0);
            result.SetSummary(
                "固定获得 1 分；已选购买 " + purchaseCount + " 个至纯源石，需支付 " + cost + " 金券。",
                cost,
                1,
                purchaseCount);
        }

        public CharacterCardOptionQueryResult QueryPending(GameState state, int playerId, IReadOnlyDictionary<string, string> selectedParameters = null)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var result = new CharacterCardOptionQueryResult();
            var pending = state.PendingCharacterEffect;
            if (pending == null || !pending.IsValid() || pending.PlayerId != playerId) return result;

            if (pending.ChoiceType == CharacterPendingChoiceTypes.LiskarmCleanupRemoval)
            {
                for (var i = 0; i < pending.OptionIds.Count; i++)
                {
                    result.Add(CharacterEffectParameterKeys.TargetInfluenceSlotId, pending.OptionIds[i], DescribeSlot(pending.OptionIds[i]));
                }
            }
            else if (pending.ChoiceType == CharacterPendingChoiceTypes.TinManDiscard)
            {
                result.Add(CharacterEffectParameterKeys.Choice, CharacterEffectChoiceIds.GainGold, "获得 5 金券");
                var player = state.FindPlayer(playerId);
                var committedPurchaseCost =
                    (pending.TinManPurchasePureOriginium12 ? 12 : 0) +
                    (pending.TinManPurchasePureOriginium15 ? 15 : 0);
                var maximumGoldAfterMovingThisCard = player == null
                    ? -1
                    : player.Resources.GoldVoucher + Math.Max(0, pending.RemainingCardIds.Count - 1) * 5;
                var canChooseMove = !pending.ResolveTinManStrategyAfterRecall ||
                                    maximumGoldAfterMovingThisCard >= committedPurchaseCost;
                if (canChooseMove)
                {
                    result.Add(CharacterEffectParameterKeys.Choice, CharacterEffectChoiceIds.MoveInfluence, "移动 1 个影响力");
                    QuerySingleInfluenceMove(state, playerId, selectedParameters, result);
                }
            }

            return result;
        }

        private static void QueryPlacementSlots(GameState state, int playerId, IReadOnlyDictionary<string, string> selected, CharacterCardOptionQueryResult result)
        {
            var player = state.FindPlayer(playerId);
            if (player == null || player.InfluenceSupply < 2) return;
            var service = new InfluenceService(CreateMapQuery(state));
            var allSlots = EnumerateSlots(CreateMapQuery(state).Map);
            var first = Get(selected, CharacterEffectParameterKeys.PlacementSlotId1);
            for (var i = 0; i < allSlots.Count; i++)
            {
                if (service.CanPlace(state, playerId, allSlots[i]).IsValid)
                {
                    result.Add(CharacterEffectParameterKeys.PlacementSlotId1, allSlots[i], DescribeSlot(allSlots[i]));
                    if (allSlots[i] != first)
                    {
                        result.Add(CharacterEffectParameterKeys.PlacementSlotId2, allSlots[i], DescribeSlot(allSlots[i]));
                    }
                }
            }
        }

        private static void QueryOpponentInfluences(GameState state, int playerId, CharacterCardOptionQueryResult result)
        {
            var player = state.FindPlayer(playerId);
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                var removesOnly = false;
                for (var j = 0; j < state.Players.Count; j++)
                    if (state.Players[j].PlayerId != playerId && state.Players[j].CityLocationId == influence.LocationId) removesOnly = true;
                if (influence.PlayerId != playerId && (removesOnly || (player != null && player.InfluenceSupply > 0)))
                    result.Add(CharacterEffectParameterKeys.TargetInfluenceSlotId, influence.SlotId, DescribeSlot(influence.SlotId) + "（玩家 " + influence.PlayerId + "）");
            }
        }

        private static void QueryMinimumResources(GameState state, int playerId, CharacterCardOptionQueryResult result)
        {
            var player = state.FindPlayer(playerId);
            if (player == null || player.Resources == null) return;
            var minimum = Math.Min(player.Resources.Originium, Math.Min(player.Resources.OriginiumShard, player.Resources.Iron));
            if (player.Resources.Originium == minimum) AddResource(result, CharacterEffectParameterKeys.ResourceType, ResourceType.Originium);
            if (player.Resources.OriginiumShard == minimum) AddResource(result, CharacterEffectParameterKeys.ResourceType, ResourceType.OriginiumShard);
            if (player.Resources.Iron == minimum) AddResource(result, CharacterEffectParameterKeys.ResourceType, ResourceType.Iron);
        }

        private static void QueryRaidLocations(GameState state, int playerId, CharacterCardOptionQueryResult result)
        {
            var mapQuery = CreateMapQuery(state);
            var influence = new InfluenceService(mapQuery);
            var movement = new CityMovementService(mapQuery, influence, new TravelCostService(mapQuery), new EventDeckService(), new ResourceTokenService());
            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                var location = mapQuery.Map.Locations[i];
                var ownsInfluence = false;
                for (var j = 0; j < state.Map.Influences.Count; j++)
                    if (state.Map.Influences[j].PlayerId == playerId && state.Map.Influences[j].LocationId == location.LocationId) ownsInfluence = true;
                if (ownsInfluence && movement.CanRaidCityForCharacter(state, playerId, location.LocationId).IsValid)
                    result.Add(CharacterEffectParameterKeys.TargetLocationId, location.LocationId, location.LocationId + "（" + ResourceDisplayName(location.ResourceType) + "）");
            }
        }

        private static void QueryFacilitySupply(GameState state, CharacterCardOptionQueryResult result)
        {
            for (var i = 0; i < state.Decks.FacilitySupply.Count; i++)
            {
                var id = state.Decks.FacilitySupply[i];
                var definition = FacilityCardDatabase.Get(id);
                result.Add(CharacterEffectParameterKeys.FacilityCardId, id, definition == null ? id : definition.Name + "（" + id + "）");
            }
        }

        private static void QueryTexasRemoveAndMove(GameState state, int playerId, IReadOnlyDictionary<string, string> selected, CharacterCardOptionQueryResult result)
        {
            var mapQuery = CreateMapQuery(state);
            var influenceService = new InfluenceService(mapQuery);
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                result.Add(
                    CharacterEffectParameterKeys.RemovalTargetInfluenceSlotId,
                    influence.SlotId,
                    DescribeSlot(influence.SlotId) + "（玩家 " + influence.PlayerId + "）");
            }

            var removalTarget = Get(selected, CharacterEffectParameterKeys.RemovalTargetInfluenceSlotId);
            if (string.IsNullOrEmpty(removalTarget)) return;
            var projected = CreateInfluenceProjection(state);
            var removal = influenceService.Remove(projected, removalTarget);
            if (!removal.Succeeded) return;

            var completed = new List<InfluenceMoveRequest>();
            var step = 1;
            for (; step <= 2; step++)
            {
                var source = Get(selected, MoveSourceKey(step));
                var target = Get(selected, MoveTargetKey(step));
                if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) break;
                completed.Add(new InfluenceMoveRequest(source, target));
            }

            if (step > 2) return;
            QueryMoveStep(projected, playerId, completed, MoveSourceKey(step), MoveTargetKey(step), result);
        }

        private static void QuerySingleInfluenceMove(GameState state, int playerId, IReadOnlyDictionary<string, string> selected, CharacterCardOptionQueryResult result)
        {
            QueryMoveStep(state, playerId, new List<InfluenceMoveRequest>(), CharacterEffectParameterKeys.SourceInfluenceSlotId, CharacterEffectParameterKeys.TargetInfluenceSlotId, result);
        }

        private static void QueryMoveStep(GameState state, int playerId, List<InfluenceMoveRequest> previous, string sourceKey, string targetKey, CharacterCardOptionQueryResult result)
        {
            var service = new InfluenceService(CreateMapQuery(state));
            var targets = EnumerateSlots(CreateMapQuery(state).Map);
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if (influence.PlayerId != playerId) continue;
                var hasTarget = false;
                for (var j = 0; j < targets.Count; j++)
                {
                    var plan = new List<InfluenceMoveRequest>(previous) { new InfluenceMoveRequest(influence.SlotId, targets[j]) };
                    if (!service.CanMoveAtomically(state, playerId, plan).IsValid) continue;
                    hasTarget = true;
                    result.Add(targetKey, targets[j], DescribeSlot(targets[j]), influence.SlotId);
                }
                if (hasTarget) result.Add(sourceKey, influence.SlotId, DescribeSlot(influence.SlotId));
            }
        }

        private static List<string> EnumerateSlots(GameMapDefinition map)
        {
            var result = new List<string>();
            for (var i = 0; i < map.Locations.Count; i++)
                for (var slot = 0; slot < map.Locations[i].InfluenceSlotCount; slot++)
                    result.Add(InfluenceService.GetLocationSlotId(map.Locations[i].LocationId, slot));
            for (var i = 0; i < map.Routes.Count; i++)
                for (var slot = 0; slot < map.Routes[i].InfluenceSlotCount; slot++)
                    result.Add(InfluenceService.GetRouteSlotId(map.Routes[i].RouteId, slot));
            return result;
        }

        private static MapQueryService CreateMapQuery(GameState state) => new MapQueryService(state.MapId == StaticMapDefinitions.ThreePlayerMapId ? StaticMapDefinitions.CreateThreePlayerPlaceholder() : StaticMapDefinitions.CreateFourPlayerMap());
        private static string Get(IReadOnlyDictionary<string, string> values, string key) { string value; return values != null && values.TryGetValue(key, out value) ? value : string.Empty; }
        private static string MoveSourceKey(int step) => step == 1 ? CharacterEffectParameterKeys.MoveSourceSlotId1 : CharacterEffectParameterKeys.MoveSourceSlotId2;
        private static string MoveTargetKey(int step) => step == 1 ? CharacterEffectParameterKeys.MoveTargetSlotId1 : CharacterEffectParameterKeys.MoveTargetSlotId2;
        private static void AddResource(CharacterCardOptionQueryResult result, string key, ResourceType type) => result.Add(key, ResourceId(type), ResourceDisplayName(type));
        private static string ResourceId(ResourceType type) => type == ResourceType.OriginiumShard ? "originium-shard" : type == ResourceType.Iron ? "iron" : "originium";
        private static string ResourceDisplayName(ResourceType type) => type == ResourceType.OriginiumShard ? "源石碎片" : type == ResourceType.Iron ? "异铁" : type == ResourceType.PureOriginium ? "至纯源石" : "源岩";
        public static string DescribeSlot(string slotId) => string.IsNullOrEmpty(slotId) ? "未知槽位" : slotId.Replace("location:", "资源点 ").Replace("route:", "路线 ");

        private static GameState CreateInfluenceProjection(GameState state)
        {
            var projection = new GameState
            {
                GameId = state.GameId,
                Phase = state.Phase,
                Round = state.Round,
                MaxRounds = state.MaxRounds,
                StartPlayerId = state.StartPlayerId,
                CurrentPlayerId = state.CurrentPlayerId,
                ActionRound = state.ActionRound,
                MapId = state.MapId,
                Map = new MapRuntimeState
                {
                    OpenLocationIds = state.Map.OpenLocationIds,
                    RoadRouteIds = state.Map.RoadRouteIds,
                    ResourceTokens = state.Map.ResourceTokens,
                    Facilities = state.Map.Facilities,
                    RemovedFromGameCardIds = state.Map.RemovedFromGameCardIds
                }
            };
            for (var i = 0; i < state.Players.Count; i++)
            {
                var source = state.Players[i];
                projection.Players.Add(new PlayerState
                {
                    PlayerId = source.PlayerId,
                    Name = source.Name,
                    Color = source.Color,
                    InfluenceSupply = source.InfluenceSupply,
                    HasScoreTrackMarker = source.HasScoreTrackMarker,
                    CityLocationId = source.CityLocationId,
                    Resources = source.Resources
                });
            }
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var source = state.Map.Influences[i];
                projection.Map.Influences.Add(new InfluencePlacement
                {
                    PlayerId = source.PlayerId,
                    SlotId = source.SlotId,
                    LocationId = source.LocationId,
                    RouteId = source.RouteId
                });
            }
            return projection;
        }
    }

    public sealed class CharacterCardOptionQueryResult
    {
        private readonly Dictionary<string, List<CharacterCardOption>> options = new Dictionary<string, List<CharacterCardOption>>();
        public string SummaryText { get; private set; } = string.Empty;
        public int GoldVoucherCost { get; private set; }
        public int ScoreGain { get; private set; }
        public int PureOriginiumGain { get; private set; }

        public void SetSummary(string summaryText, int goldVoucherCost, int scoreGain, int pureOriginiumGain)
        {
            SummaryText = summaryText ?? string.Empty;
            GoldVoucherCost = Math.Max(0, goldVoucherCost);
            ScoreGain = Math.Max(0, scoreGain);
            PureOriginiumGain = Math.Max(0, pureOriginiumGain);
        }

        public IReadOnlyList<CharacterCardOption> Get(string parameterKey)
        {
            List<CharacterCardOption> values;
            return options.TryGetValue(parameterKey, out values) ? values.AsReadOnly() : new List<CharacterCardOption>().AsReadOnly();
        }
        public IReadOnlyList<CharacterCardOption> Get(string parameterKey, string parentId)
        {
            var filtered = new List<CharacterCardOption>();
            var values = Get(parameterKey);
            for (var i = 0; i < values.Count; i++)
                if (string.IsNullOrEmpty(values[i].ParentId) || values[i].ParentId == (parentId ?? string.Empty)) filtered.Add(values[i]);
            return filtered.AsReadOnly();
        }
        public void Add(string parameterKey, string id, string displayName, string parentId = "")
        {
            List<CharacterCardOption> values;
            if (!options.TryGetValue(parameterKey, out values)) options[parameterKey] = values = new List<CharacterCardOption>();
            for (var i = 0; i < values.Count; i++) if (values[i].Id == id && values[i].ParentId == parentId) return;
            values.Add(new CharacterCardOption(id, displayName, parentId));
        }
    }

    public sealed class CharacterCardOption
    {
        public CharacterCardOption(string id, string displayName, string parentId = "") { Id = id ?? string.Empty; DisplayName = displayName ?? string.Empty; ParentId = parentId ?? string.Empty; }
        public string Id { get; private set; }
        public string DisplayName { get; private set; }
        public string ParentId { get; private set; }
    }
}
