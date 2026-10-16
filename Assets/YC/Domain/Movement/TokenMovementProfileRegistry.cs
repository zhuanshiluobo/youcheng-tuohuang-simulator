using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Influence;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Movement
{
    /// <summary>共享地图路径查询；Token 的移动只提交本实例位置。</summary>
    public sealed class TokenMovementProfileRegistry
    {
        private readonly IMapQueryService map;
        private readonly ItemTypeRegistry types;
        public Func<GameState, string, string, bool> DestinationAllowed { get; set; } = (state, item, location) => true;
        public TokenMovementProfileRegistry(IMapQueryService map = null, ItemTypeRegistry types = null) { this.map = map; this.types = types ?? ItemTypeRegistry.CreateBuiltIn(); }
        private IMapQueryService Map(GameState state) => map ?? new MapQueryService(StaticMapDefinitions.Resolve(state.MapId));
        public static string GetLocationId(GameState state, string itemId)
        {
            var item = new ItemZoneQuery().FindItem(state, itemId);
            var parent = item == null ? null : InfluenceSubjectQuery.GetMapParent(state, item.ZoneId);
            return parent?.TemplateId == "core.zone.map_location" ? parent.LocalZoneId : "";
        }
        public IReadOnlyList<string> GetDestinationLocationIds(GameState state, string tokenItemId, string operationProfileId)
        {
            var item = new ItemZoneQuery(types).FindItem(state, tokenItemId);
            if (operationProfileId != "core.adjacent_location_token_move" || item?.PhysicalKind != ItemPhysicalKind.Token || !types.HasOperationProfile(item.TypeId, operationProfileId)) throw new InvalidOperationException("operation_profile_rejected");
            var parent = InfluenceSubjectQuery.GetMapParent(state, item.ZoneId);
            if (parent?.TemplateId != "core.zone.map_location") return Array.Empty<string>();
            return Map(state).GetAdjacentLocations(parent.LocalZoneId).Where(location => state.Map.OpenLocationIds.Contains(location.LocationId) &&
                DestinationAllowed(state, tokenItemId, location.LocationId) && state.ItemZones.Zones.Exists(zone => zone.TemplateId == "core.zone.map_location" && zone.LocalZoneId == location.LocationId))
                .Select(location => location.LocationId).OrderBy(id => id, StringComparer.Ordinal).ToList().AsReadOnly();
        }
        public ItemTransferResult Move(EffectExecutionContext context, string tokenItemId, string destinationLocationId, string operationProfileId)
        {
            if (!GetDestinationLocationIds(context.State, tokenItemId, operationProfileId).Contains(destinationLocationId)) throw new InvalidOperationException("token_destination_rejected");
            var item = new ItemZoneQuery(types).FindItem(context.State, tokenItemId);
            if (item.ControllerSubject.Kind == ItemSubjectKind.Player && item.ControllerSubject.PlayerId != context.Node.PlayerId) throw new InvalidOperationException("transfer_permission_denied");
            var parent = context.State.ItemZones.Zones.Single(zone => zone.TemplateId == "core.zone.map_location" && zone.LocalZoneId == destinationLocationId);
            var target = context.State.ItemZones.Zones.Single(zone => zone.ParentId == parent.ZoneId && zone.TemplateId == "core.zone.map_tokens");
            return new ItemTransferService(types).RelocateItems(context, InfluenceService.Transfer(context, item, target.ZoneId, "", operationProfileId));
        }
        public static int GetActionRoundSequence(GameState state)
        {
            if (state.Round < 1 || state.ActionRound < 1 || state.ActionRound > 2) throw new InvalidOperationException("action_round_required");
            return checked((state.Round - 1) * 2 + state.ActionRound);
        }
        public static bool IsExpiredBeforeActionRound(GameState state, int expiresBeforeActionRoundSequence) => GetActionRoundSequence(state) >= expiresBeforeActionRoundSequence;
    }

    public static class MapOperationEffectExecutor
    {
        public static void Register(EffectRegistry registry, IMapQueryService map = null, TokenMovementProfileRegistry tokenProfiles = null)
        {
            if (!registry.TryGet(LuaDomainEffectTypeIds.PlaceRoad, out _)) registry.Register(new EffectRegistration(LuaDomainEffectTypeIds.PlaceRoad, context => ExecuteRoad(context, map), EffectExecutorKind.Atomic, "v1") { Validator = spec => ValidateArguments(LuaDomainEffectTypeIds.PlaceRoad, spec.NormalizedArguments) });
            if (!registry.TryGet(LuaDomainEffectTypeIds.OperateToken, out _)) registry.Register(new EffectRegistration(LuaDomainEffectTypeIds.OperateToken, context => ExecuteToken(context, map, tokenProfiles), EffectExecutorKind.Atomic, "v1") { Validator = spec => ValidateArguments(LuaDomainEffectTypeIds.OperateToken, spec.NormalizedArguments) });
        }
        public static string ValidateArguments(string typeId, NormalizedValue args)
        {
            if (Number(args, "apiVersion", -1) != 1) return "unsupported_api_version";
            if (!int.TryParse(Text(args, "player"), out var player) || player < 1) return "invalid_player";
            if (typeId == LuaDomainEffectTypeIds.PlaceRoad) return Text(args, "routeId").Length == 0 ? "routeId_required" : "";
            return Text(args, "tokenItemId").Length == 0 || Text(args, "destinationLocationId").Length == 0 || Text(args, "operation") != "move" || Text(args, "operationProfileId") != "core.adjacent_location_token_move" ? "operation_profile_rejected" : "";
        }
        private static EffectStepResult ExecuteRoad(EffectExecutionContext context, IMapQueryService map)
        {
            var args = context.Node.NormalizedArguments;
            var error = ValidateArguments(LuaDomainEffectTypeIds.PlaceRoad, args);
            if (error.Length > 0) return EffectStepResult.Failed(error);
            if (Text(args, "player") != context.Node.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture) || context.State.FindPlayer(context.Node.PlayerId) == null) return EffectStepResult.Failed("invalid_player");
            var service = new InfluenceService(map ?? new MapQueryService(StaticMapDefinitions.Resolve(context.State.MapId)));
            string route = Text(args, "routeId");
            if (!service.GetRoadCandidateRouteIds(context.State).Contains(route)) return EffectStepResult.Failed("road_already_present");
            string item = service.PlaceRoad(context, route);
            return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(true), "routeId", NormalizedValue.CreateString(route), "roadItemId", NormalizedValue.CreateString(item)));
        }
        private static EffectStepResult ExecuteToken(EffectExecutionContext context, IMapQueryService map, TokenMovementProfileRegistry tokenProfiles)
        {
            var args = context.Node.NormalizedArguments;
            string error = ValidateArguments(LuaDomainEffectTypeIds.OperateToken, args);
            if (error.Length > 0) return EffectStepResult.Failed(error);
            if (Text(args, "player") != context.Node.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture)) return EffectStepResult.Failed("invalid_player");
            var profiles = tokenProfiles ?? new TokenMovementProfileRegistry(map);
            string itemId = Text(args, "tokenItemId"), destination = Text(args, "destinationLocationId"), profile = Text(args, "operationProfileId");
            var item = new ItemZoneQuery().FindItem(context.State, itemId);
            if (item?.PhysicalKind != ItemPhysicalKind.Token || !ItemTypeRegistry.CreateBuiltIn().HasOperationProfile(item.TypeId, profile)) return EffectStepResult.Failed("operation_profile_rejected");
            if (item.ControllerSubject.Kind == ItemSubjectKind.Player && item.ControllerSubject.PlayerId != context.Node.PlayerId) return EffectStepResult.Failed("transfer_permission_denied");
            if (!profiles.GetDestinationLocationIds(context.State, itemId, profile).Contains(destination)) return EffectStepResult.Failed("token_destination_rejected");
            var original = Object("zoneId", NormalizedValue.CreateString(item.ZoneId), "slotId", NormalizedValue.CreateString(item.SlotId), "locationId", NormalizedValue.CreateString(TokenMovementProfileRegistry.GetLocationId(context.State, itemId)));
            var moved = profiles.Move(context, itemId, destination, profile);
            var result = Object("changed", NormalizedValue.CreateBoolean(moved.Changed), "tokenItemId", NormalizedValue.CreateString(itemId), "originalPosition", original,
                "destinationLocationId", NormalizedValue.CreateString(destination), "destination", Object("zoneId", NormalizedValue.CreateString(item.ZoneId), "slotId", NormalizedValue.CreateString(item.SlotId)));
            return EffectStepResult.Completed(result)
                .AddEvent(new EffectEventRequest { EventId = StableIdFactory.Create("event", context.Node.EffectId, "token.moved", itemId), EventType = "token.moved",
                    SourceEffectId = context.Node.EffectId, OwnerNodeId = context.Node.EffectId, TargetEntityId = itemId, PlayerId = context.Node.PlayerId, DefinitionVersion = "v1", Visibility = "public", SemanticKey = "moved",
                    Payload = result.Clone() });
        }
    }
}
