using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.CityStyles;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public static class CityStyleDeclarationEffectExecutor
    {
        public const string TypeId = "effect.city_style.declare";
        public static void Register(EffectRegistry registry)
        {
            if (!registry.TryGet(TypeId, out _)) registry.Register(TypeId, Execute);
        }
        public static EffectSpec Create(int player, string cityStyleId, IEnumerable<int> slots) => EffectSpec.Create(TypeId,
            Object("cityStyleId", NormalizedValue.CreateString(cityStyleId), "usedCityBoardSlotIndexes", NormalizedValue.CreateArray(slots.Select(index => NormalizedValue.CreateInteger(index)).ToList())), player);
        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            var state = context.State; var player = state.FindPlayer(context.Node.PlayerId);
            string styleId = Text(context.Node.NormalizedArguments, "cityStyleId");
            var slotsValue = Value(context.Node.NormalizedArguments, "usedCityBoardSlotIndexes");
            if (slotsValue?.Kind != NormalizedValueKind.Array || slotsValue.Items.Any(v => v.Kind != NormalizedValueKind.Integer)) return EffectStepResult.Failed("city_style_slots_invalid");
            var slots = slotsValue.Items.Select(v => (int)v.IntegerValue).ToList();
            var valid = new DeclareCityStyleService().Validate(state, context.Node.PlayerId, styleId, slots);
            if (!valid.IsValid) return EffectStepResult.Failed("city_style_declaration_invalid", NormalizedValue.CreateString(valid.Reason));
            var style = CityStyleDatabase.Get(state, styleId);
            var match = new CityStylePatternMatcher().MatchSelected(state, player.PlayerId, style, slots);
            if (!ItemZoneLegacyProjection.IsActive(state)) return EffectStepResult.Failed("item_zones_required");
            var styleItem = state.ItemZones.Items.Single(i => i.DefinitionId == style.CityStyleId && i.ZoneId == FindZone(state, "city_styles").ZoneId);
            int previous = player.DeclaredCityStyles.Count(d => d.CityStyleId == style.CityStyleId);
            var usage = state.ContentDefinitions?.UsageTracks.SingleOrDefault(t => t.DefinitionId == style.CityStyleId);
            var declaration = DeclareCityStyleService.CreateDeclarationState(style, player.PlayerId, previous);
            if (previous == 0 && declaration.UnlockedSpecialActionId.Length > 0 && usage != null) declaration.MarkerArea = usage.InitialSlotId;
            string area = declaration.MarkerArea;
            var marker = state.ItemZones.Items.FirstOrDefault(i => i.TypeId == "core.player_marker" && i.OwnerSubject.PlayerId == player.PlayerId && i.ZoneId == FindZone(state, "supply", player.PlayerId).ZoneId);
            if (marker == null) return EffectStepResult.Failed("insufficient_items");
            string markerId = marker.ItemId, source = marker.ZoneId;
            var zoneDefinition = new CardZoneDefinition { LocalZoneId = "declaration_" + player.PlayerId + "_" + previous, ZoneTemplateId = "core.zone.board_marker_track",
                AcceptedItemKinds = new List<ItemPhysicalKind> { ItemPhysicalKind.Cube }, AcceptedTypeIds = new List<string> { "core.player_marker" }, OrderingKind = ZoneOrderingKind.Track,
                Slots = new List<string> { "declared", "unused", "used", "2", "1", "0", "used_from_2", "used_from_1" },
                CapacityPolicy = "bounded", Capacity = 1, VisibilityPolicyId = ItemZoneVisibility.Public, EntryPolicyId = "core.explicit_transfer", ExitPolicyId = "core.explicit_transfer", CarrierExitPolicyId = "core.preserve_contents" };
            if (usage != null) zoneDefinition.Slots = new List<string>(usage.Slots);
            if (!new ItemZoneStateFactory().TryCreateCardZone(state, styleItem.ItemId, zoneDefinition, out string zoneId, out string reason)) return EffectStepResult.Failed(reason);
            state.ItemZones.Zones.Single(z => z.ZoneId == zoneId).OwnerPlayerId = player.PlayerId;
            var transfer = new ItemTransferRequest { ExecutingPlayerId = player.PlayerId, ItemIds = new List<string> { markerId }, ExpectedSource = new ItemPositionRef { ZoneId = source }, Destination = new ZoneRef { ZoneId = zoneId }, DestinationSlotId = area,
                InsertionKind = "slot", RequiredCount = 1, CarrierPolicyId = "preserve", OperationProfileId = "core.player_marker_storage" };
            var service = new ItemTransferService(); var transferValid = service.Validate(state, transfer);
            if (!transferValid.IsValid) throw new InvalidOperationException(transferValid.ReasonCode);
            service.RelocateItems(context, transfer);
            // 工厂发布可能更新集合引用，因此再取权威玩家；匹配事实只保存同一设施实例。
            player = state.FindPlayer(context.Node.PlayerId);
            declaration.DeclarationId = StableIdFactory.Create("declaration", context.Node.EffectId);
            declaration.CityStyleItemId = styleItem.ItemId; declaration.InfluenceMarkerId = markerId;
            declaration.UsedFacilityIds.AddRange(match.UsedCityBoardSlotIndexes.Select(index => state.Map.Facilities.Single(f => f.PlayerId == player.PlayerId && f.CityBoardSlotIndex == index).ContentInstanceId));
            declaration.UsedCityBoardSlotIndexes.AddRange(match.UsedCityBoardSlotIndexes);
            player.DeclaredCityStyles.Add(declaration); declaration.BindItemZones(state);
            if (previous == 0 && declaration.UnlockedSpecialActionId.Length > 0 && usage != null) UsageTracks.Bind(context, usage, styleItem.ItemId, declaration.DeclarationId, zoneId, markerId);
            player.Score += style.Score; player.Resources.Add(style.DeclarationReward ?? new ResourceSet());
            return EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(true), "declarationId", NormalizedValue.CreateString(declaration.DeclarationId))).AddEvent(new EffectEventRequest {
                EventType = "CityStyleDeclared", PlayerId = player.PlayerId, Payload = Object("declarationId", NormalizedValue.CreateString(declaration.DeclarationId), "cityStyleItemId", NormalizedValue.CreateString(declaration.CityStyleItemId),
                    "usedFacilityItemIds", Strings(declaration.UsedFacilityIds), "playerId", NormalizedValue.CreateInteger(player.PlayerId)) });
        }
        public static void MoveMarker(EffectExecutionContext context, CityStyleDeclarationState declaration, string area)
        {
            if (!ItemZoneLegacyProjection.IsActive(context.State)) { declaration.MarkerArea = area; return; }
            var marker = new ItemZoneQuery().FindItem(context.State, declaration.InfluenceMarkerId);
            var request = new ItemTransferRequest { ExecutingPlayerId = context.Node.PlayerId, ItemIds = new List<string> { marker.ItemId }, ExpectedSource = new ItemPositionRef { ZoneId = marker.ZoneId }, Destination = new ZoneRef { ZoneId = marker.ZoneId }, DestinationSlotId = area,
                InsertionKind = "slot", RequiredCount = 1, CarrierPolicyId = "preserve", OperationProfileId = "core.player_marker_storage" };
            new ItemTransferService().RelocateItems(context, request);
        }
    }
}
