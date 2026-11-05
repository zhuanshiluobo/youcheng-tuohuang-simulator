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
        internal static string LocationForZone(GameState state, string zoneId)
        {
            var zone = state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == zoneId && z.TemplateId == "core.zone.map_tokens");
            return zone == null ? "" : state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == zone.ParentId && z.TemplateId == "core.zone.map_location")?.LocalZoneId ?? "";
        }
        internal static string DestinationZoneId(GameState state, string location)
        {
            var parent = state.ItemZones.Zones.SingleOrDefault(z => z.TemplateId == "core.zone.map_location" && z.LocalZoneId == location);
            return parent == null ? "" : state.ItemZones.Zones.SingleOrDefault(z => z.ParentId == parent.ZoneId && z.TemplateId == "core.zone.map_tokens")?.ZoneId ?? "";
        }
        public string ValidateMove(GameState state, int player, string itemId, string destination, string profile)
        {
            var item = new ItemZoneQuery(types).FindItem(state, itemId);
            if (item?.PhysicalKind != ItemPhysicalKind.Token || !types.HasOperationProfile(item.TypeId, profile)) return "operation_profile_rejected";
            if (item.ControllerSubject.Kind == ItemSubjectKind.Player && item.ControllerSubject.PlayerId != player) return "transfer_permission_denied";
            return profile == "core.adjacent_location_token_move" && GetDestinationLocationIds(state, itemId, profile).Contains(destination) && DestinationZoneId(state, destination).Length > 0 ? "" : "token_destination_rejected";
        }
        internal static EffectEventRequest MovedEvent(EffectNodeRuntimeState node, string itemId, ItemPositionRef from, ItemPositionRef to, string origin, string destination)
        {
            var payload = Object("changed", NormalizedValue.CreateBoolean(true), "tokenItemId", NormalizedValue.CreateString(itemId),
                "originalPosition", Object("zoneId", NormalizedValue.CreateString(from.ZoneId), "slotId", NormalizedValue.CreateString(from.SlotId), "locationId", NormalizedValue.CreateString(origin)),
                "destinationLocationId", NormalizedValue.CreateString(destination), "destination", Object("zoneId", NormalizedValue.CreateString(to.ZoneId), "slotId", NormalizedValue.CreateString(to.SlotId)));
            return new EffectEventRequest { EventId = StableIdFactory.Create("event", node.EffectId, "token.moved", itemId), EventType = "token.moved",
                SourceEffectId = node.EffectId, OwnerNodeId = node.EffectId, TargetEntityId = itemId, PlayerId = node.PlayerId, DefinitionVersion = "v1", Visibility = "public", SemanticKey = "moved", Payload = payload };
        }
        public IReadOnlyList<string> GetDestinationLocationIds(GameState state, string tokenItemId, string operationProfileId)
        {
            var item = new ItemZoneQuery(types).FindItem(state, tokenItemId);
            if (operationProfileId != "core.adjacent_location_token_move" || item?.PhysicalKind != ItemPhysicalKind.Token || !types.HasOperationProfile(item.TypeId, operationProfileId)) throw new InvalidOperationException("operation_profile_rejected");
            var parent = InfluenceSubjectQuery.GetMapParent(state, item.ZoneId);
            if (parent?.TemplateId != "core.zone.map_location") return Array.Empty<string>();
            return Map(state).GetAdjacentLocations(parent.LocalZoneId).Where(location => state.Map.OpenLocationIds.Contains(location.LocationId) &&
                AllowsDestination(state, tokenItemId, location.LocationId) && state.ItemZones.Zones.Exists(zone => zone.TemplateId == "core.zone.map_location" && zone.LocalZoneId == location.LocationId))
                .Select(location => location.LocationId).OrderBy(id => id, StringComparer.Ordinal).ToList().AsReadOnly();
        }
        private bool AllowsDestination(GameState state,string itemId,string locationId)
        {
            ExecutionWorkRetentionScope.Expose(state);
            return DestinationAllowed(state,itemId,locationId);
        }
        public ItemTransferResult Move(EffectExecutionContext context, string tokenItemId, string destinationLocationId, string operationProfileId)
        {
            string reason = ValidateMove(context.State, context.Node.PlayerId, tokenItemId, destinationLocationId, operationProfileId);
            if (reason.Length > 0) throw new InvalidOperationException(reason);
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
        public const string TokenDefinitionVersion = "2.0.0";
        public static string ValidateSavedVersion(string type,string version,bool runtimeNode=false) => type!=LuaDomainEffectTypeIds.OperateToken || version==TokenDefinitionVersion || !runtimeNode&&string.IsNullOrEmpty(version) ? "" : "token_move_contract_version_unsupported";
        public static void Register(EffectRegistry registry, IMapQueryService map = null, TokenMovementProfileRegistry tokenProfiles = null)
        {
            ItemTransferEffectExecutor.Register(registry);
            if (tokenProfiles != null || map != null) registry.TokenMovementProfiles = tokenProfiles ?? new TokenMovementProfileRegistry(map);
            if (!registry.TryGet(LuaDomainEffectTypeIds.PlaceRoad, out _)) registry.Register(new EffectRegistration(LuaDomainEffectTypeIds.PlaceRoad, context => ExecuteRoad(context, map), EffectExecutorKind.Atomic, "v1") { ExecutionDoesNotRetainState = true, Validator = spec => ValidateArguments(LuaDomainEffectTypeIds.PlaceRoad, spec.NormalizedArguments) });
            if (!registry.TryGet(LuaDomainEffectTypeIds.OperateToken, out _)) registry.Register(new EffectRegistration(LuaDomainEffectTypeIds.OperateToken, SpecializedItemMovement.ExecuteToken, EffectExecutorKind.IntrinsicFlow, TokenDefinitionVersion) { Validator = spec => ValidateArguments(LuaDomainEffectTypeIds.OperateToken, spec.NormalizedArguments) });
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

    }
}
