using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Infrastructure.Lua
{
    /// <summary>只为可重复来源接入既有纯Lua结算查询，不替换付款/角色目录。</summary>
    public static class CleanupSourceLuaCatalog
    {
        public static void Validate(ExternalContentDefinition content)
        {
            foreach (var source in content.AbilitySources)
            {
                if (source.RepeatPolicy != "once" && source.RepeatPolicy != "unbounded" || source.Kind != "cleanup" && source.RepeatPolicy != "once")
                    throw new InvalidOperationException("cleanup_repeat_policy_invalid:" + source.AbilityId);
                if (source.RepeatPolicy != "unbounded") continue;
                var ability = content.Abilities.SingleOrDefault(a => a.AbilityId == source.AbilityId);
                if (ability?.Settlement?.EntryKind != SettlementEntryService.CleanupOrder || ability.Settlement.WholeEffectPolicy != "optional" ||
                    !source.ObservedEvents.SequenceEqual(new[] { "PlayerCleanupStarted" }))
                    throw new InvalidOperationException("cleanup_unbounded_requires_optional_source:" + source.AbilityId);
            }
        }
        public static void Register(EffectRegistry registry, SettlementLuaCatalog catalog)
        {
            CleanupSourceInvocationExecutor.Register(registry);
            registry.RegisterCleanupSourceQueries(CreateFacts, (state, window, entry) => catalog.QueryAvailability(
                SettlementEntryService.CreateQueryContext(state, window), entry, CreateFacts(state, window, entry), true,
                SourceFactsLuaCatalog.ForSettlement(state, registry, SettlementEntryService.CreateQueryContext(state, window), entry)));
        }

        public static NormalizedValue CreateFacts(GameState state, SettlementWindowRuntimeState window, SettlementEntryRuntimeState entry)
        {
            var proof = entry.CleanupSource.SourceRef; int playerId = window.Context.PlayerId;
            var declaration = state.ContentDefinitions.AbilitySources.Single(d => d.AbilityId == proof.AbilityId);
            var active = AbilitySourceService.QuerySources(state, state.ContentDefinitions.AbilitySources, playerId)
                .OrderBy(s => s.SourceKey, StringComparer.Ordinal).ToList();
            var current = active.SingleOrDefault(s => s.SourceKey == proof.SourceKey);
            var permissions = declaration.AllowedOperations.Where(p => p.StartsWith("carrier.transfer.", StringComparison.Ordinal))
                .Where(CardMarkerCarrier.ValidPermission).Select(p => p.Split(':')).ToList();
            var zones = state.ItemZones.Zones.Where(z => z.ParentKind == ZoneParentKind.Item &&
                state.ItemZones.Items.Any(card => card.ItemId == z.ParentId && card.ControllerSubject.Kind == ItemSubjectKind.Player && card.ControllerSubject.PlayerId == playerId &&
                    permissions.Any(p => p[1] == card.DefinitionId && p[2] == z.LocalZoneId))).OrderBy(z => z.ZoneId, StringComparer.Ordinal).ToList();
            var storage = new JArray(); int markerCount = 0, storedCount = 0;
            foreach (var zone in zones)
            {
                var card = state.ItemZones.Items.Single(i => i.ItemId == zone.ParentId);
                var accepted = new HashSet<string>(StringComparer.Ordinal);
                var qualifications = new JArray();
                foreach (string operation in permissions.Where(p => p[1] == card.DefinitionId && p[2] == zone.LocalZoneId)
                    .Select(p => p[0].Substring("carrier.".Length)).Distinct().OrderBy(s => s, StringComparer.Ordinal))
                {
                    var resolved = CardMarkerCarrier.ResolveWithSource(state, current, playerId, card.ItemId, zone.LocalZoneId,
                        operation, state.ContentDefinitions.AbilitySources, out var types, out string reason);
                    if (resolved != null) accepted.UnionWith(types);
                    qualifications.Add(new JObject { ["operation"] = operation, ["enabled"] = resolved != null, ["reason"] = reason,
                        ["types"] = new JArray(types.OrderBy(s => s, StringComparer.Ordinal)) });
                }
                var items = state.ItemZones.Items.Where(i => i.ZoneId == zone.ZoneId).OrderBy(i => i.ItemId, StringComparer.Ordinal).ToList();
                int count = items.Count(i => CardMarkerCarrier.AcceptsTransfer(state, zone, i, playerId, accepted)); markerCount = checked(markerCount + count);
                storedCount = checked(storedCount + items.Count);
                storage.Add(new JObject { ["card"] = ItemFacts(card), ["cardLocation"] = Model(state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == card.ZoneId)),
                    ["zone"] = Model(zone), ["qualification"] = qualifications, ["payableCount"] = count,
                    ["items"] = new JArray(items.Select(i => new JObject { ["item"] = ItemFacts(i),
                        ["originalSupply"] = Model(state.ItemZones.Zones.SingleOrDefault(z => z.ZoneId == i.OriginalSupplyZoneId)),
                        ["payable"] = CardMarkerCarrier.AcceptsTransfer(state, zone, i, playerId, accepted),
                        ["usageBindings"] = new JArray(state.EffectRuntime.UsageTrackBindings.Where(b => b.MarkerItemId == i.ItemId)
                            .OrderBy(b => b.BindingId, StringComparer.Ordinal).Select(b => Model(b))) })) });
            }
            var resources = state.FindPlayer(playerId).Resources;
            var pending = EffectTreeExecutor.GetDeferredResponseDependencies(state, new DeferredDependencyQuery {
                OwnerEffectId = window.Context.OwnerEffectId, DependencyKind = DeferredDependencyKind.FinalizeGame });
            var carrierIds = new HashSet<string>(zones.Select(z => z.ParentId), StringComparer.Ordinal) { entry.SourceItemId };
            var related = state.EffectRuntime.DeferredResponses.Where(r => pending.Any(d => d.DeferredResponseId == r.DeferredResponseId) &&
                (carrierIds.Contains(r.SourceCardInstanceId) || CleanupSourceDependencies.RelatesTo(state, r, window.Context.OwnerEffectId)))
                .OrderBy(r => r.DeferredResponseId, StringComparer.Ordinal).ToList();
            var sourceCard = state.ItemZones.Items.Single(i => i.ItemId == proof.ItemId);
            // 完整事实不进入NormalizedValue预算：集合按业务ID排序，JSON对象键按Ordinal排序；数组语义保序。
            // 不截取库存、来源或响应。每个LocalState独立使用原有归一化规范，允许合法的128节点值。
            string factsDigest = StableDigest(new JObject {
                ["policy"] = CleanupSourceService.RequestPolicy, ["sourceProof"] = SourceFacts(proof), ["declaration"] = DeclarationFacts(declaration),
                ["sourceCard"] = ItemFacts(sourceCard), ["storage"] = storage,
                ["activeSources"] = new JArray(active.Select(SourceFacts)), ["resources"] = Model(resources),
                ["responses"] = new JArray(related.Select(r => Model(r))),
                ["departures"] = new JArray(state.EffectRuntime.InfluenceDepartures.Where(d =>
                    InfluenceDepartureProtocol.IsWithin(state, d.SourceEffectId, window.Context.OwnerEffectId) || related.Any(r => r.DepartureId == d.DepartureId))
                    .OrderBy(d => d.DepartureId, StringComparer.Ordinal).Select(d => Model(d))),
                ["batches"] = new JArray(state.EffectRuntime.InfluenceDepartureBatches.Where(b =>
                    InfluenceDepartureProtocol.IsWithin(state, b.OwnerEffectId, window.Context.OwnerEffectId) || related.Any(r => r.BatchId == b.BatchId))
                    .OrderBy(b => b.BatchId, StringComparer.Ordinal).Select(b => Model(b))) });
            // Lua只消费语义计数和必要上下文；完整实例/位置/授权/版本/状态由稳定摘要绑定到正式请求。
            return Object("source", Object("itemId", NormalizedValue.CreateString(entry.SourceItemId), "abilityId", NormalizedValue.CreateString(entry.AbilityId),
                "persistentActive", NormalizedValue.CreateBoolean(current != null), "markerCount", NormalizedValue.CreateInteger(markerCount), "transferableTokenCount", NormalizedValue.CreateInteger(markerCount),
                "storedItemCount", NormalizedValue.CreateInteger(storedCount), "storageZoneCount", NormalizedValue.CreateInteger(zones.Count)),
                "cleanup", Object("windowId", NormalizedValue.CreateString(window.Context.Id), "factsPolicy", NormalizedValue.CreateString(CleanupSourceService.RequestPolicy),
                    "relatedFactsDigest", NormalizedValue.CreateString(factsDigest), "pendingResponseCount", NormalizedValue.CreateInteger(related.Count),
                    "activeSourceCount", NormalizedValue.CreateInteger(active.Count),
                    "resources", Object("originium", NormalizedValue.CreateInteger(resources.Originium), "originiumShard", NormalizedValue.CreateInteger(resources.OriginiumShard),
                        "iron", NormalizedValue.CreateInteger(resources.Iron), "goldVoucher", NormalizedValue.CreateInteger(resources.GoldVoucher),
                        "pureOriginium", NormalizedValue.CreateInteger(resources.PureOriginium))));
        }

        private static JToken Model(object value) => value == null ? JValue.CreateNull() : JToken.FromObject(value,
            JsonSerializer.Create(new JsonSerializerSettings { Culture = CultureInfo.InvariantCulture }));
        private static JObject SourceFacts(AbilitySourceRef source)
        {
            var result = (JObject)Model(source);
            result[nameof(source.GrantingSourceRefs)] = new JArray(source.GrantingSourceRefs.OrderBy(s => s, StringComparer.Ordinal));
            return result;
        }
        private static JObject DeclarationFacts(AbilitySourceDeclaration declaration)
        {
            var result = (JObject)Model(declaration);
            foreach (string field in new[] { nameof(declaration.EffectiveZones), nameof(declaration.ObservedEvents), nameof(declaration.QueryKinds), nameof(declaration.AllowedOperations) })
                result[field] = new JArray(((JArray)result[field]).Values<string>().OrderBy(s => s, StringComparer.Ordinal));
            result[nameof(declaration.Actions)] = new JArray(declaration.Actions.OrderBy(a => a.ActionId, StringComparer.Ordinal).Select(a => Model(a)));
            return result;
        }
        private static JObject ItemFacts(ItemInstanceState item) => new JObject {
            ["id"] = item.ItemId, ["definition"] = item.DefinitionId, ["type"] = item.TypeId, ["physicalKind"] = item.PhysicalKind.ToString(),
            ["zone"] = item.ZoneId, ["slot"] = item.SlotId, ["originalSupply"] = item.OriginalSupplyZoneId,
            ["owner"] = Model(item.OwnerSubject), ["controller"] = Model(item.ControllerSubject), ["state"] = item.LocalState.ToDeterministicString() };
        private static string StableDigest(JToken facts)
        {
            var text = new StringBuilder();
            using (var writer = new JsonTextWriter(new System.IO.StringWriter(text, CultureInfo.InvariantCulture)) {
                Formatting = Formatting.None, StringEscapeHandling = StringEscapeHandling.EscapeNonAscii })
                WriteCanonical(writer, facts);
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))
                .Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }
        private static void WriteCanonical(JsonWriter writer, JToken value)
        {
            if (value is JObject obj)
            {
                writer.WriteStartObject();
                foreach (var property in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
                writer.WriteEndObject();
            }
            else if (value is JArray array)
            {
                writer.WriteStartArray(); foreach (var item in array) WriteCanonical(writer, item); writer.WriteEndArray();
            }
            else value.WriteTo(writer);
        }
    }
}
