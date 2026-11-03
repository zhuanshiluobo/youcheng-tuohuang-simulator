using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Cards
{
    // API-19 / 4.8.2：通用与专用角色移动使用相同的生命周期、提交和转交事实。
    public static class CharacterCardMovement
    {
        public const string Version = "character_move.v3.0";
        public const string DefinitionVersion = "3.0.0";
        public const string UnsupportedSavedVersion = "character_move_contract_version_unsupported";
        public const string UnversionedSignatureRetired = "character_move_unversioned_signature_retired";
        public static bool IsExplicit(string type, NormalizedValue args) =>
            Value(args, "apiVersion") != null && (type == LuaDomainEffectTypeIds.MoveCharacterCard ||
                type == TypeId && Number(args, "apiVersion") == 1 && Text(args, "operationProfileId") == "core.character_card_transfer");
        public static string ValidateSavedVersion(string type, NormalizedValue args, string version, bool runtimeNode = false)
        {
            if (type == LuaDomainEffectTypeIds.MoveCharacterCard && Value(args, "apiVersion") == null) return UnversionedSignatureRetired;
            if (!IsExplicit(type, args)) return "";
            string current = type == TypeId ? TransferDefinitionVersion : DefinitionVersion;
            return version == current || !runtimeNode && string.IsNullOrEmpty(version) ? "" : UnsupportedSavedVersion;
        }
        public static EffectSpec Create(GameState state, int player, string itemId, string destinationZoneId)
        {
            var item = new ItemZoneQuery().FindItem(state, itemId);
            if (item == null) throw new InvalidOperationException("unknown_item");
            var spec = MoveSpec(player, new[] { itemId }, item.ZoneId, destinationZoneId, "unordered", 1, "core.character_card_transfer");
            spec.EffectTypeId = LuaDomainEffectTypeIds.MoveCharacterCard;
            spec.NormalizedArguments.Properties.Single(p => p.Name == "apiVersion").Value = NormalizedValue.CreateInteger(2);
            spec.DefinitionVersion = DefinitionVersion;
            return spec;
        }
        public static EffectStepResult Execute(EffectExecutionContext context, ItemTransferRequest request)
        {
            var types = ItemTypeRegistry.CreateBuiltIn();
            if (request.ItemIds.Distinct(StringComparer.Ordinal).Count() != request.ItemIds.Count) return EffectStepResult.Failed("duplicate_item_id");
            if (request.ItemIds.Count < request.RequiredCount) return EffectStepResult.Failed("insufficient_items");
            var originalControllers = new Dictionary<string, int>();
            foreach (string id in request.ItemIds)
            {
                var item = new ItemZoneQuery().FindItem(context.State, id);
                if (item == null) return EffectStepResult.Failed("unknown_item");
                if (!types.IsAssignableTo(item.TypeId, "core.character_card")) return EffectStepResult.Failed("character_type_rejected");
                if (item.ZoneId != request.ExpectedSource.ZoneId || request.ExpectedSource.SlotId.Length > 0 && item.SlotId != request.ExpectedSource.SlotId)
                    return EffectStepResult.Failed("source_zone_changed");
                if (item.ControllerSubject.Kind == ItemSubjectKind.Player && item.ControllerSubject.PlayerId != request.ExecutingPlayerId)
                    return EffectStepResult.Failed("transfer_permission_denied");
                originalControllers[id] = item.ControllerSubject.Kind == ItemSubjectKind.Player ? item.ControllerSubject.PlayerId : -1;
            }
            var preview = GameStateCloneService.DeepClone(context.State);
            var work = new EffectExecutionContext(preview, preview.EffectRuntime.EffectNodes.Single(n => n.EffectId == context.Node.EffectId), context.Registry);
            foreach (string id in request.ItemIds)
            {
                string reason = CharacterLifecycleService.PrepareCardCarrierExit(work, id, request.Destination.ZoneId);
                if (reason.Length > 0) return EffectStepResult.Failed(reason);
            }
            var service = new ItemTransferService(); var validation = service.Validate(preview, request);
            if (!validation.IsValid) return EffectStepResult.Failed(validation.ReasonCode);
            var moved = service.RelocateItems(work, request);
            foreach (string id in moved.MovedItemIds) CharacterLifecycleService.DetachExitedSubscriptions(preview, id);
            CharacterLifecycleService.ExitEmptyCarriers(work, moved.PositionChanges.Select(c => c.From.ZoneId));
            work.Node.Visibility = GameStateVisibilityPolicy.Owner;
            var step = EffectStepResult.Completed(Object("changed", NormalizedValue.CreateBoolean(moved.Changed),
                "movedCount", NormalizedValue.CreateInteger(moved.MovedCount), "movedItemIds", Strings(moved.MovedItemIds)));
            foreach (string id in moved.MovedItemIds)
            {
                var item = new ItemZoneQuery().FindItem(preview, id);
                int recipient = item.ControllerSubject.Kind == ItemSubjectKind.Player ? item.ControllerSubject.PlayerId : -1;
                if (originalControllers[id] <= 0 || recipient <= 0 || recipient == originalControllers[id]) continue;
                step.AddEvent(new EffectEventRequest {
                    EventId = StableIdFactory.Create("event", context.Node.EffectId, "character.transferred", id),
                    EventType = "character.transferred", PlayerId = context.Node.PlayerId, Visibility = GameStateVisibilityPolicy.Public,
                    ResponseKind = RuleEventResponseKind.Effects, SemanticKey = "transferred",
                    Payload = Object("recipientPlayerId", NormalizedValue.CreateInteger(recipient)),
                    HostOnlyPayload = Object("itemId", NormalizedValue.CreateString(id), "recipientPlayerId", NormalizedValue.CreateInteger(recipient)) });
            }
            if (moved.Changed && Flag(context.Node.NormalizedArguments, "reveal"))
            {
                // API-23 / 4.10.1：只公开实际移动实例；整组只发一次，不公开其他手牌。
                var payload = Object("itemIds", Strings(moved.MovedItemIds), "definitionIds",
                    Strings(moved.MovedItemIds.Select(id => preview.ItemZones.Items.Single(i => i.ItemId == id).DefinitionId)));
                if (moved.MovedCount == 1)
                {
                    var destination = preview.ItemZones.Zones.Single(z => z.ZoneId == request.Destination.ZoneId);
                    payload.Properties.Add(new NormalizedValueEntry { Name = "recoveredItemId", Value = NormalizedValue.CreateString(destination.LocalZoneId == "hand" ? moved.MovedItemIds[0] : "") });
                    payload.Properties.Add(new NormalizedValueEntry { Name = "discardedItemIds", Value = Strings(destination.LocalZoneId == "discard" ? moved.MovedItemIds : Array.Empty<string>()) });
                    payload.Properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                }
                step.AddEvent(new EffectEventRequest {
                    EventId = StableIdFactory.Create("event", context.Node.EffectId, "character.cards.revealed"),
                    EventType = "character.cards.revealed", PlayerId = context.Node.PlayerId, Visibility = GameStateVisibilityPolicy.Public,
                    ResponseKind = RuleEventResponseKind.Effects, Payload = payload });
            }
            GameStateCloneService.CopyTo(context.State, preview);
            return step;
        }
    }
}
