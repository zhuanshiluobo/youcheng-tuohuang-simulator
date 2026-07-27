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
        private readonly IMapQueryService sharedMapQuery;
        private readonly InfluenceService sharedInfluenceService;
        private readonly CityMovementService sharedMovementService;

        public CharacterCardOptionQueryService()
        {
        }

        public CharacterCardOptionQueryService(
            IMapQueryService mapQuery,
            InfluenceService influenceService,
            CityMovementService movementService)
        {
            sharedMapQuery = mapQuery ?? throw new ArgumentNullException(nameof(mapQuery));
            sharedInfluenceService = influenceService ?? throw new ArgumentNullException(nameof(influenceService));
            sharedMovementService = movementService ?? throw new ArgumentNullException(nameof(movementService));
        }

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
                    QueryTinManStrategySummary(result);
                    break;
            }

            return result;
        }

        public bool HasLegalResolution(
            GameState state,
            int playerId,
            CharacterCardEffectKind effect)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var player = state.FindPlayer(playerId);
            if (player == null || player.Resources == null)
            {
                return false;
            }

            switch (effect)
            {
                case CharacterCardEffectKind.CannotTradeChannel:
                case CharacterCardEffectKind.CannotRequisition:
                case CharacterCardEffectKind.TinManEstablishPrestige:
                case CharacterCardEffectKind.TinManDeepPlanning:
                    return true;
                case CharacterCardEffectKind.LiskarmSecurityProtocol:
                    return HasCompleteLiskarmPlacement(state, playerId);
                case CharacterCardEffectKind.LiskarmControlPosition:
                    return player.Resources.GoldVoucher >= 3 &&
                           Query(state, playerId, effect)
                               .Get(CharacterEffectParameterKeys.TargetInfluenceSlotId).Count > 0;
                case CharacterCardEffectKind.ElysiumLogistics:
                    return Query(state, playerId, effect)
                        .Get(CharacterEffectParameterKeys.ResourceType).Count > 0;
                case CharacterCardEffectKind.ElysiumNavigation:
                    return player.Resources.OriginiumShard >= 3 &&
                           Query(state, playerId, effect)
                               .Get(CharacterEffectParameterKeys.TargetLocationId).Count > 0;
                case CharacterCardEffectKind.TexasSpecialDelivery:
                    return Query(state, playerId, effect)
                        .Get(CharacterEffectParameterKeys.FacilityCardId).Count > 0;
                case CharacterCardEffectKind.TexasRemoveAndDoubleMove:
                    return player.Resources.GoldVoucher >= 3 &&
                           HasCompleteTexasRemoveAndDoubleMove(state, playerId);
                default:
                    return false;
            }
        }

        private static void QueryTinManStrategySummary(CharacterCardOptionQueryResult result)
        {
            result.SetSummary(
                "固定获得 1 分；之后通过待选结算依次决定是否支付 " +
                CharacterCardService.TinManFirstPureOriginiumCost +
                " 金券和 " +
                CharacterCardService.TinManSecondPureOriginiumCost +
                " 金券购买至纯源石。",
                0,
                1,
                0);
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
                result.Add(CharacterEffectParameterKeys.Choice, CharacterEffectChoiceIds.MoveInfluence, "移动 1 个影响力");
                QuerySingleInfluenceMove(state, playerId, selectedParameters, result);
            }
            else if (pending.ChoiceType == CharacterPendingChoiceTypes.TinManFirstPurchase)
            {
                result.SetSummary(
                    "已固定获得 1 分。现在可以支付 " +
                    CharacterCardService.TinManFirstPureOriginiumCost +
                    " 金券获得第一个至纯源石；取消则立即结束本次策略结算。",
                    0,
                    1,
                    0);
                AddPendingChoiceIfPresent(
                    pending,
                    result,
                    CharacterEffectChoiceIds.TinManPurchaseFirstPureOriginium,
                    "支付 " + CharacterCardService.TinManFirstPureOriginiumCost + " 金券，获得 1 个至纯源石");
                AddPendingChoiceIfPresent(
                    pending,
                    result,
                    CharacterEffectChoiceIds.TinManFinishPurchasing,
                    "取消购买并结算");
            }
            else if (pending.ChoiceType == CharacterPendingChoiceTypes.TinManSecondPurchase)
            {
                result.SetSummary(
                    "第一笔 " +
                    CharacterCardService.TinManFirstPureOriginiumCost +
                    " 金券已支付并获得 1 个至纯源石。现在可以再支付 " +
                    CharacterCardService.TinManSecondPureOriginiumCost +
                    " 金券获得第二个；取消则按当前结果结算。",
                    CharacterCardService.TinManFirstPureOriginiumCost,
                    1,
                    1);
                AddPendingChoiceIfPresent(
                    pending,
                    result,
                    CharacterEffectChoiceIds.TinManPurchaseSecondPureOriginium,
                    "再支付 " + CharacterCardService.TinManSecondPureOriginiumCost + " 金券，再获得 1 个至纯源石");
                AddPendingChoiceIfPresent(
                    pending,
                    result,
                    CharacterEffectChoiceIds.TinManFinishPurchasing,
                    "取消第二笔购买并结算");
            }

            return result;
        }

        private static void AddPendingChoiceIfPresent(
            PendingCharacterEffectState pending,
            CharacterCardOptionQueryResult result,
            string choiceId,
            string displayName)
        {
            if (pending.OptionIds != null && pending.OptionIds.Contains(choiceId))
            {
                result.Add(CharacterEffectParameterKeys.Choice, choiceId, displayName);
            }
        }

        private bool HasCompleteLiskarmPlacement(GameState state, int playerId)
        {
            var firstOptions = Query(state, playerId, CharacterCardEffectKind.LiskarmSecurityProtocol)
                .Get(CharacterEffectParameterKeys.PlacementSlotId1);
            for (var i = 0; i < firstOptions.Count; i++)
            {
                var selected = new Dictionary<string, string>
                {
                    [CharacterEffectParameterKeys.PlacementSlotId1] = firstOptions[i].Id
                };
                if (Query(state, playerId, CharacterCardEffectKind.LiskarmSecurityProtocol, selected)
                        .Get(CharacterEffectParameterKeys.PlacementSlotId2).Count > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasCompleteTexasRemoveAndDoubleMove(GameState state, int playerId)
        {
            var initial = Query(state, playerId, CharacterCardEffectKind.TexasRemoveAndDoubleMove);
            var removals = initial.Get(CharacterEffectParameterKeys.RemovalTargetInfluenceSlotId);
            for (var removalIndex = 0; removalIndex < removals.Count; removalIndex++)
            {
                var selected = new Dictionary<string, string>
                {
                    [CharacterEffectParameterKeys.RemovalTargetInfluenceSlotId] = removals[removalIndex].Id
                };
                var firstMove = Query(
                    state,
                    playerId,
                    CharacterCardEffectKind.TexasRemoveAndDoubleMove,
                    selected);
                var firstSources = firstMove.Get(CharacterEffectParameterKeys.MoveSourceSlotId1);
                for (var sourceIndex = 0; sourceIndex < firstSources.Count; sourceIndex++)
                {
                    var firstTargets = firstMove.Get(
                        CharacterEffectParameterKeys.MoveTargetSlotId1,
                        firstSources[sourceIndex].Id);
                    for (var targetIndex = 0; targetIndex < firstTargets.Count; targetIndex++)
                    {
                        selected[CharacterEffectParameterKeys.MoveSourceSlotId1] = firstSources[sourceIndex].Id;
                        selected[CharacterEffectParameterKeys.MoveTargetSlotId1] = firstTargets[targetIndex].Id;
                        var secondMove = Query(
                            state,
                            playerId,
                            CharacterCardEffectKind.TexasRemoveAndDoubleMove,
                            selected);
                        var secondSources = secondMove.Get(CharacterEffectParameterKeys.MoveSourceSlotId2);
                        for (var secondSourceIndex = 0; secondSourceIndex < secondSources.Count; secondSourceIndex++)
                        {
                            if (secondMove.Get(
                                    CharacterEffectParameterKeys.MoveTargetSlotId2,
                                    secondSources[secondSourceIndex].Id).Count > 0)
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private void QueryPlacementSlots(GameState state, int playerId, IReadOnlyDictionary<string, string> selected, CharacterCardOptionQueryResult result)
        {
            var player = state.FindPlayer(playerId);
            if (player == null || player.InfluenceSupply < 2) return;
            var service = ResolveInfluenceService(state);
            var allSlots = EnumerateSlots(ResolveMapQuery(state).Map);
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
            if (CharacterCardService.IsMinimumBasicResource(player.Resources, ResourceType.Originium))
                AddResource(result, CharacterEffectParameterKeys.ResourceType, ResourceType.Originium);
            if (CharacterCardService.IsMinimumBasicResource(player.Resources, ResourceType.OriginiumShard))
                AddResource(result, CharacterEffectParameterKeys.ResourceType, ResourceType.OriginiumShard);
            if (CharacterCardService.IsMinimumBasicResource(player.Resources, ResourceType.Iron))
                AddResource(result, CharacterEffectParameterKeys.ResourceType, ResourceType.Iron);
        }

        private void QueryRaidLocations(GameState state, int playerId, CharacterCardOptionQueryResult result)
        {
            var mapQuery = ResolveMapQuery(state);
            var movement = ResolveMovementService(state);
            for (var i = 0; i < mapQuery.Map.Locations.Count; i++)
            {
                var location = mapQuery.Map.Locations[i];
                if (movement.CanRaidCityForCharacter(state, playerId, location.LocationId).IsValid)
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

        private void QueryTexasRemoveAndMove(GameState state, int playerId, IReadOnlyDictionary<string, string> selected, CharacterCardOptionQueryResult result)
        {
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
            QueryMoveStep(state, playerId, completed, MoveSourceKey(step), MoveTargetKey(step), result, removalTarget);
        }

        private void QuerySingleInfluenceMove(GameState state, int playerId, IReadOnlyDictionary<string, string> selected, CharacterCardOptionQueryResult result)
        {
            QueryMoveStep(state, playerId, new List<InfluenceMoveRequest>(), CharacterEffectParameterKeys.SourceInfluenceSlotId, CharacterEffectParameterKeys.TargetInfluenceSlotId, result, string.Empty);
        }

        private void QueryMoveStep(
            GameState state,
            int playerId,
            List<InfluenceMoveRequest> previous,
            string sourceKey,
            string targetKey,
            CharacterCardOptionQueryResult result,
            string removalTargetSlotId)
        {
            var service = ResolveInfluenceService(state);
            var targets = EnumerateSlots(ResolveMapQuery(state).Map);
            for (var i = 0; i < state.Map.Influences.Count; i++)
            {
                var influence = state.Map.Influences[i];
                if (influence.PlayerId != playerId) continue;
                var hasTarget = false;
                for (var j = 0; j < targets.Count; j++)
                {
                    var plan = new List<InfluenceMoveRequest>(previous) { new InfluenceMoveRequest(influence.SlotId, targets[j]) };
                    var validation = string.IsNullOrEmpty(removalTargetSlotId)
                        ? service.CanMoveAtomically(state, playerId, plan)
                        : service.CanRemoveThenMoveAtomically(state, playerId, removalTargetSlotId, plan);
                    if (!validation.IsValid) continue;
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

        private IMapQueryService ResolveMapQuery(GameState state) => sharedMapQuery ?? CreateMapQuery(state);
        private InfluenceService ResolveInfluenceService(GameState state) => sharedInfluenceService ?? new InfluenceService(ResolveMapQuery(state));
        private CityMovementService ResolveMovementService(GameState state)
        {
            if (sharedMovementService != null) return sharedMovementService;
            var mapQuery = ResolveMapQuery(state);
            return new CityMovementService(mapQuery, ResolveInfluenceService(state), new TravelCostService(mapQuery), new EventDeckService(), new ResourceTokenService());
        }
        private static MapQueryService CreateMapQuery(GameState state) => new MapQueryService(state.MapId == StaticMapDefinitions.ThreePlayerMapId ? StaticMapDefinitions.CreateThreePlayerPlaceholder() : StaticMapDefinitions.CreateFourPlayerMap());
        private static string Get(IReadOnlyDictionary<string, string> values, string key) { string value; return values != null && values.TryGetValue(key, out value) ? value : string.Empty; }
        private static string MoveSourceKey(int step) => step == 1 ? CharacterEffectParameterKeys.MoveSourceSlotId1 : CharacterEffectParameterKeys.MoveSourceSlotId2;
        private static string MoveTargetKey(int step) => step == 1 ? CharacterEffectParameterKeys.MoveTargetSlotId1 : CharacterEffectParameterKeys.MoveTargetSlotId2;
        private static void AddResource(CharacterCardOptionQueryResult result, string key, ResourceType type) => result.Add(key, ResourceId(type), ResourceDisplayName(type));
        private static string ResourceId(ResourceType type) => type == ResourceType.OriginiumShard ? "originium-shard" : type == ResourceType.Iron ? "iron" : "originium";
        private static string ResourceDisplayName(ResourceType type) => type == ResourceType.OriginiumShard ? "源石碎片" : type == ResourceType.Iron ? "异铁" : type == ResourceType.PureOriginium ? "至纯源石" : "源岩";
        public static string DescribeSlot(string slotId) => string.IsNullOrEmpty(slotId) ? "未知槽位" : slotId.Replace("location:", "资源点 ").Replace("route:", "路线 ");

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
