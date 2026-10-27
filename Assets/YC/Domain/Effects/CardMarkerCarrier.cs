using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    /// <summary>载体许可使用来源的 allowedOperations；格式为 carrier.{操作}:{定义}:{真实子区}:{类型}。</summary>
    public static class CardMarkerCarrier
    {
        public const string Version = "2.0.0";
        public static bool ValidPermission(string permission)
        {
            if (!permission.StartsWith("carrier.", StringComparison.Ordinal)) return true;
            var p = permission.Split(':');
            return p.Length == 4 && new[] { "carrier.load", "carrier.store", "carrier.transfer.original_supply", "carrier.transfer.retired" }.Contains(p[0]) &&
                p.Skip(1).All(v => !string.IsNullOrWhiteSpace(v) && v != "*") && ItemTypeRegistry.CreateBuiltIn().IsAssignableTo(p[3], "core.cube");
        }
        public static AbilitySourceRef Source(GameState state, EffectNodeRuntimeState node, IEnumerable<AbilitySourceDeclaration> declarations, EffectRegistry registry = null)
        {
            var visited = new HashSet<string>();
            while (node != null && visited.Add(node.EffectId))
            {
                if (node.ContinuationAbilityId.Length > 0)
                {
                    var d = declarations.SingleOrDefault(value => value.AbilityId == node.ContinuationAbilityId && value.AuthorizationPolicy == "character_subscription");
                    if (d != null && node.ContinuationDefinitionVersion == d.DeclarationVersion && !string.IsNullOrEmpty(node.ContinuationContentHash) &&
                        (registry == null || registry.HandlerHash(d.HandlerRef) == node.ContinuationContentHash))
                    {
                        string itemId = node.SourceId.StartsWith("character-card:", StringComparison.Ordinal) ? node.SourceId.Substring("character-card:".Length) : "";
                        var parent = node;
                        while (itemId.Length == 0 && parent != null)
                        {
                            if (parent.Settlement?.AbilityId == d.AbilityId && state.EffectRuntime.EffectNodes.Any(w => w.SettlementWindow?.Entries.Any(e =>
                                e.TargetEffectId == parent.EffectId && e.AbilityId == d.AbilityId && e.SourceItemId == parent.Settlement.SourceItemId) == true)) itemId = parent.Settlement.SourceItemId;
                            if (parent.SourceId.StartsWith("character-card:", StringComparison.Ordinal) && parent.ContinuationAbilityId == d.AbilityId) itemId = parent.SourceId.Substring("character-card:".Length);
                            if (parent.EffectTypeId == CharacterAbilityEffectExecutor.ActivationEffectTypeId && Text(parent.NormalizedArguments, "abilityId") == d.AbilityId)
                            { var value = Value(parent.NormalizedArguments, "cardInstanceId"); itemId = value?.Kind == NormalizedValueKind.StableReference ? value.ReferenceId : value?.StringValue ?? ""; }
                            parent = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == parent.ParentEffectId);
                        }
                        return AbilitySourceService.QuerySources(state, declarations, node.PlayerId,includeCharacterActivation:true).SingleOrDefault(s => s.AbilityId == d.AbilityId && s.ItemId == itemId);
                    }
                }
                // 仅接受真实调度器的身份；不从同卡第一项来源或普通节点自填 sourceKey 推断。
                var cleanupInvocation = CleanupSourceService.Invocations(state).SingleOrDefault(i => i.TargetEffectId == node.EffectId);
                if (cleanupInvocation != null) return cleanupInvocation.SourceRef;
                var invocation = state.EffectRuntime.AbilityActionInvocations.SingleOrDefault(i => i.OwnerEffectId == node.EffectId);
                if (invocation != null) return invocation.SourceRef;
                var use = state.EffectRuntime.AbilitySourceUses.SingleOrDefault(i => i.OwnerEffectId == node.EffectId);
                if (use != null) return use.SourceRef;
                if (node.EffectTypeId == YC.Domain.Rules.EntrepreneurFlow.EntryTypeId)
                {
                    string key = Text(node.NormalizedArguments, "sourceKey");
                    return AbilitySourceService.QuerySources(state, declarations, node.PlayerId).SingleOrDefault(s => s.SourceKey == key);
                }
                node = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == node.ParentEffectId);
            }
            return null;
        }
        public static ZoneInstanceState Resolve(GameState state, EffectNodeRuntimeState node, string cardId, string localId,
            string operation, out List<string> acceptedTypes, out string reason, IEnumerable<AbilitySourceDeclaration> declarations = null, bool legacy = false, EffectRegistry registry = null)
        {
            acceptedTypes = new List<string>(); reason = "card_zone_permission_denied";
            var card = state.ItemZones.Items.Find(i => i.ItemId == cardId);
            var location = state.ItemZones.Zones.Find(z => z.ZoneId == card?.ZoneId);
            var zone = state.ItemZones.Zones.Find(z => z.ParentKind == ZoneParentKind.Item && z.ParentId == cardId && z.LocalZoneId == localId && z.TemplateId == "core.zone.card_cube_storage");
            if (card?.ControllerSubject.Kind != ItemSubjectKind.Player || card.ControllerSubject.PlayerId != node.PlayerId || zone?.Configuration == null) return null;
            // v1仅保留旧角色生命周期合同，不能借v1把企业家伪装成角色。
            if (legacy && new[] { "core.zone.character_covered", "core.zone.character_persistent" }.Contains(location?.TemplateId))
            { acceptedTypes = zone.Configuration.AcceptedTypeIds.ToList(); reason = ""; return zone; }
            declarations = declarations ?? state.ContentDefinitions.AbilitySources;
            var source = Source(state, node, declarations, registry);
            return ResolveWithSource(state, source, node.PlayerId, cardId, localId, operation, declarations, out acceptedTypes, out reason);
        }
        public static ZoneInstanceState ResolveWithSource(GameState state, AbilitySourceRef source, int player, string cardId, string localId,
            string operation, IEnumerable<AbilitySourceDeclaration> declarations, out List<string> acceptedTypes, out string reason)
        {
            acceptedTypes = new List<string>(); reason = "card_zone_permission_denied";
            var card = state.ItemZones.Items.Find(i => i.ItemId == cardId);
            var location = state.ItemZones.Zones.Find(z => z.ZoneId == card?.ZoneId);
            var zone = state.ItemZones.Zones.Find(z => z.ParentKind == ZoneParentKind.Item && z.ParentId == cardId && z.LocalZoneId == localId && z.TemplateId == "core.zone.card_cube_storage");
            if (card?.ControllerSubject.Kind != ItemSubjectKind.Player || card.ControllerSubject.PlayerId != player || zone?.Configuration == null) return null;
            if (source == null || source.AuthorizedPlayerId != player || !(operation=="load" ? AbilitySourceService.IsCurrentForCarrierLoad(state,declarations,source) : AbilitySourceService.IsCurrent(state, declarations, source)))
            { reason = "carrier_source_invalid"; return null; }
            var declaration = declarations.SingleOrDefault(d => d.AbilityId == source.AbilityId && d.DefinitionId == source.DefinitionId);
            string prefix = "carrier." + operation + ":" + card.DefinitionId + ":" + localId + ":";
            acceptedTypes = declaration?.AllowedOperations.Where(p => p.StartsWith(prefix, StringComparison.Ordinal) && ValidPermission(p)).Select(p => p.Substring(prefix.Length)).Distinct().ToList() ?? new List<string>();
            if (acceptedTypes.Count == 0) return null;
            // 目标本身也必须是当前有效载体；位置资格来自冻结声明和生命周期，而非追加区域白名单。
            if (!AbilitySourceService.QuerySources(state, declarations, player,includeCharacterActivation:operation=="load").Any(s => s.ItemId == cardId && s.EffectiveZoneId == location?.ZoneId)) return null;
            reason = ""; return zone;
        }
        // 现有card_cube_storage的资格；不是全部token的类型定义。非方块token不借这个模板入区。
        public static bool Accepts(GameState state, ZoneInstanceState zone, ItemInstanceState item, int player, IEnumerable<string> allowedTypes)
        {
            var types = ItemTypeRegistry.CreateBuiltIn();
            return item?.PhysicalKind == ItemPhysicalKind.Cube && item.OwnerSubject.Kind == ItemSubjectKind.Player && item.OwnerSubject.PlayerId == player &&
                allowedTypes.Any(t => types.IsAssignableTo(item.TypeId, t)) && new ItemZoneQuery().Accepts(zone, item) &&
                !state.EffectRuntime.UsageTrackBindings.Any(b => b.MarkerItemId == item.ItemId && b.ZoneId == item.ZoneId);
        }
        // 卡上已有内容可具有不同原主/公司主体；返还资格来自真实受控载体及来源声明。
        // 与装载/正规资源替代的本人标记筛选分开，不能借返还许可跨玩家供应取物。
        public static bool AcceptsTransfer(GameState state, ZoneInstanceState zone, ItemInstanceState item, int player, IEnumerable<string> allowedTypes)
        {
            var types = ItemTypeRegistry.CreateBuiltIn();
            var carrier = state.ItemZones.Items.Find(i => i.ItemId == zone?.ParentId);
            return zone?.TemplateId == "core.zone.card_cube_storage" && carrier?.ControllerSubject.Kind == ItemSubjectKind.Player && carrier.ControllerSubject.PlayerId == player &&
                item?.PhysicalKind == ItemPhysicalKind.Cube && item.ZoneId == zone.ZoneId && allowedTypes.Any(t => types.IsAssignableTo(item.TypeId, t)) &&
                new ItemZoneQuery().Accepts(zone, item) && !state.EffectRuntime.UsageTrackBindings.Any(b => b.MarkerItemId == item.ItemId);
        }
        // 仅用于挂必要等待前核来源/端点意向，不能作为提交授权；恢复后仍走完整规划和强制执行守卫。
        internal static string ValidateTransferIntentWithSource(GameState state, AbilitySourceRef source, ItemTransferRequest request, IEnumerable<AbilitySourceDeclaration> declarations)
        {
            var from = state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == request.ExpectedSource.ZoneId);
            var to = state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == request.Destination.ZoneId);
            foreach (var zone in new[] { from, to }.Where(z => z?.TemplateId == "core.zone.card_cube_storage"))
            {
                string operation = zone == to ? "load" : to?.TemplateId == "core.zone.box_retired" ? "transfer.retired" : "transfer.original_supply";
                if (ResolveWithSource(state, source, request.ExecutingPlayerId, zone.ParentId, zone.LocalZoneId, operation, declarations, out var accepted, out string reason) == null) return reason;
                foreach (string id in request.ItemIds)
                {
                    var item = state.ItemZones.Items.SingleOrDefault(i => i.ItemId == id);
                    if (item?.PhysicalKind != ItemPhysicalKind.Cube || !accepted.Any(t => ItemTypeRegistry.CreateBuiltIn().IsAssignableTo(item.TypeId, t)) || !new ItemZoneQuery().Accepts(zone, item)) return "carrier_item_type_rejected";
                    if (zone == to && (!Accepts(state, zone, item, request.ExecutingPlayerId, accepted) || from?.TemplateId != "core.zone.player_supply" ||
                        from.OwnerPlayerId != request.ExecutingPlayerId || item.TypeId != "core.player_marker")) return "carrier_load_source_rejected";
                    if (zone == from && operation == "transfer.original_supply" && to?.ZoneId != item.OriginalSupplyZoneId) return "carrier_transfer_destination_rejected";
                }
            }
            return "";
        }
        public static string ValidatePublicTransfer(EffectExecutionContext context, ItemTransferRequest request, bool requireCarrierAuthority = false)
        {
            if (context.Node.EffectTypeId != ItemTransferEffectExecutor.TypeId && !requireCarrierAuthority) return "";
            if (requireCarrierAuthority && context.Node.Payment != null)
            {
                var paymentSource = ResourcePaymentSourceService.ResolveTransferAuthority(context, request);
                if (paymentSource == null) return "payment_token_source_authority_invalid";
                return ValidateTransferWithSource(context.State, paymentSource, request, context.Registry.AbilityDeclarations);
            }
            return ValidateTransferWithSource(context.State, Source(context.State, context.Node, context.Registry.AbilityDeclarations, context.Registry),
                request, context.Registry.AbilityDeclarations, !requireCarrierAuthority && !context.Registry.HasAbilityCatalog && Number(context.Node.NormalizedArguments, "apiVersion") == 1);
        }
        public static string ValidateTransferWithSource(GameState state, AbilitySourceRef source, ItemTransferRequest request,
            IEnumerable<AbilitySourceDeclaration> declarations, bool legacyCharacter = false)
        {
            var from = state.ItemZones.Zones.Find(z => z.ZoneId == request.ExpectedSource.ZoneId);
            var to = state.ItemZones.Zones.Find(z => z.ZoneId == request.Destination.ZoneId);
            foreach (var z in new[] { from, to }.Where(z => z?.TemplateId == "core.zone.card_cube_storage"))
            {
                var carrier = state.ItemZones.Items.Find(i => i.ItemId == z.ParentId);
                // 无来源目录的旧会话保留原v1角色转移合同；新目录与企业家永不走此兼容分支。
                if (legacyCharacter &&
                    carrier != null && ItemTypeRegistry.CreateBuiltIn().IsAssignableTo(carrier.TypeId, "core.character_card")) continue;
                string operation = z == to ? "load" : to?.TemplateId == "core.zone.box_retired" ? "transfer.retired" : "transfer.original_supply";
                var resolved = ResolveWithSource(state, source, request.ExecutingPlayerId, z.ParentId, z.LocalZoneId, operation, declarations, out var types, out var reason);
                if (resolved == null) return reason;
                foreach (string id in request.ItemIds)
                {
                    var item = state.ItemZones.Items.Find(i => i.ItemId == id);
                    if (!(z == from ? AcceptsTransfer(state, z, item, request.ExecutingPlayerId, types) : Accepts(state, z, item, request.ExecutingPlayerId, types))) return "carrier_item_type_rejected";
                    if (z == to && (from?.TemplateId != "core.zone.player_supply" || item.TypeId != "core.player_marker" || from.OwnerPlayerId != request.ExecutingPlayerId)) return "carrier_load_source_rejected";
                    if (z == from && operation == "transfer.original_supply" && to?.ZoneId != item.OriginalSupplyZoneId) return "carrier_transfer_destination_rejected";
                }
            }
            return "";
        }
    }
}
