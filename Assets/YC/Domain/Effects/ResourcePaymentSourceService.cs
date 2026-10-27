using YC.Domain.Rules;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    /// <summary>付款来源规则的纯消费方。规则数字与卡面用途来自冻结Lua，不由卡名决定。</summary>
    public static class ResourcePaymentSourceService
    {
        public const string QueryKind = "resource_payment_substitution.v1";
        public const string TokenCostContractVersion = "resource_token_cost.v1";
        internal static bool IsResourcePaymentNode(EffectNodeRuntimeState node) => node != null &&
            (node.EffectTypeId == ResourceEffectTypeIds.Pay || node.EffectTypeId == ResourcePaymentChoiceEffectExecutor.TypeId || node.EffectTypeId == FacilityBuildEffectExecutor.PaymentTypeId);
        private static readonly ConditionalWeakTable<EffectRegistry, object> Registered = new ConditionalWeakTable<EffectRegistry, object>();
        private sealed class CostQueryBinding { public EffectRegistry Registry; }
        private static readonly ConditionalWeakTable<GameState, CostQueryBinding> CostQueries = new ConditionalWeakTable<GameState, CostQueryBinding>();
        // 仅绑定实际Host/Effect上下文的同一状态对象；不按哈希或最近使用的全局Registry借用来源。
        internal static void BindCostQueries(GameState state, EffectRegistry registry)
        { if (state != null && registry != null) CostQueries.GetValue(state, _ => new CostQueryBinding()).Registry = registry; }
        internal static EffectRegistry CostQueryRegistry(GameState state) => state != null && CostQueries.TryGetValue(state, out var binding) &&
            binding.Registry?.HasAbilityCatalog == true && binding.Registry.ContentPackHash == state.ContentPackHash ? binding.Registry : null;
        public static void Register(EffectRegistry registry)
        {
            if (Registered.TryGetValue(registry, out _)) return;
            registry.RegisterAbilityQueryContract(QueryKind, (query, source, items) =>
            {
                foreach (var value in items.Items) if (!TryRead(value, source, null, out _, out var reason)) return reason;
                return "";
            });
            registry.RegisterAbilityQueryContract(ResourcePaymentCostPreview.DemandQueryKind, (query, source, items) =>
            {
                if (items.Items.Count > 1) return "payment_demand_query_conflict";
                foreach (var value in items.Items) { string error = ResourcePaymentDemandParser.TryParse(value, out _, out var player); if (error.Length > 0) return error; if (player != source.AuthorizedPlayerId) return "payment_permission_denied"; }
                return "";
            });
            Registered.Add(registry, new object());
        }
        public static string BuildIdentity(GameState state, EffectNodeRuntimeState owner)
        {
            var p = owner?.Payment?.Preparation;
            if (p?.ActionKind == "build" && !p.IsCancelled && p.PreparationOwnerEffectId == owner.EffectId)
            {
                if (p.AuthorizationKind == "effect_build" && ResourcePaymentPreparationService.ValidateEffectBuildIdentity(state, owner, out _)) return "build:" + owner.EffectId;
                if (ResourcePaymentPreparationService.IsMainActionAuthorization(p) &&
                    (ResourcePaymentPreparationService.IsCurrentPreparation(state, owner, owner.PlayerId) || p.IsPromoted && state.EffectRuntime.EffectNodes.Any(n =>
                        n.EffectId == p.PromotedActionEffectId && n.PlayerId == owner.PlayerId && n.EffectTypeId == FacilityEntryEffectTypeIds.Build && Text(n.NormalizedArguments, "preparedPaymentOwnerEffectId") == owner.EffectId))) return "build:" + owner.EffectId;
            }
            var parent = state?.EffectRuntime?.EffectNodes.Find(n => n.EffectId == owner?.ParentEffectId);
            return owner?.EffectTypeId == FacilityBuildEffectExecutor.PaymentTypeId && parent?.EffectTypeId == FacilityEntryEffectTypeIds.Build && parent.PlayerId == owner.PlayerId ? "build:" + parent.EffectId : "";
        }
        public static IReadOnlyList<PaymentSourceRule> Query(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner, ResourceSet gross)
        {
            if (state == null || registry == null || owner == null || !PaymentResources.Valid(gross)) throw new ArgumentException("payment_query_invalid");
            BindCostQueries(state, registry);
            if (owner.Payment?.AllowsSubstitution == false || owner.Payment == null && Value(owner.NormalizedArguments, "allowSubstitution")?.Kind == NormalizedValueKind.Boolean && !Flag(owner.NormalizedArguments, "allowSubstitution")) return Array.Empty<PaymentSourceRule>();
            if (!registry.HasAbilityCatalog || !registry.AbilityDeclarations.Any(d => d.Kind == "ruleQuery" && d.QueryKinds.Contains(QueryKind))) return Array.Empty<PaymentSourceRule>();
            Register(registry);
            string build = BuildIdentity(state, owner); string purpose = build.Length > 0 ? "build" : owner.Payment?.SourceKind ?? "pay";
            var facts = Object("paymentId", NormalizedValue.CreateString(owner.EffectId), "paymentGeneration", NormalizedValue.CreateInteger(owner.Payment?.PaymentGeneration ?? 1),
                "playerId", NormalizedValue.CreateInteger(owner.PlayerId), "purposeKind", NormalizedValue.CreateString(purpose),
                "buildIdentity", NormalizedValue.CreateString(build), "grossCost", PaymentResources.Write(gross));
            var query = new AbilityQueryContext { QueryKind = QueryKind, QueryId = StableIdFactory.Create("payment-query", owner.EffectId, facts.ToDeterministicString()),
                FactsDigest = StableIdFactory.Create("payment-facts", facts.ToDeterministicString(), state.EffectRuntime.StateRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ContentVersion = registry.ContentPackHash, MapId = state.MapId, StateRevision = state.EffectRuntime.StateRevision,
                SubjectPlayerId = owner.PlayerId, Purpose = purpose, Facts = facts };
            var result = new List<PaymentSourceRule>();
            foreach (var response in registry.QueryRules(state, query))
            {
                if (response.SourceRef.AuthorizedPlayerId != owner.PlayerId) continue;
                foreach (var value in response.Items.Items)
                {
                    if (!TryRead(value, response.SourceRef, response.VersionRef, out var rule, out var reason)) throw new InvalidOperationException(reason);
                    if (rule.PurposeKind != "payment" && rule.PurposeKind != purpose || rule.QuotaScope == "build" && build.Length == 0) continue;
                    if (result.Any(r => Key(r) == Key(rule))) throw new InvalidOperationException("payment_source_rule_duplicate");
                    result.Add(rule);
                }
            }
            return result.OrderBy(Key, StringComparer.Ordinal).ToList();
        }
        public static string Key(PaymentSourceRule rule) => rule?.SourceRef?.SourceKey + "|" + rule?.RuleId;
        public static string Fingerprint(PaymentSourceRule rule) => StableIdFactory.Create("payment-rule", JsonConvert.SerializeObject(rule));
        public static string TokenOperationId(EffectNodeRuntimeState owner, PaymentSourceRule rule) => StableIdFactory.Create(
            "payment-token-transfer", owner.EffectId, (owner.Payment?.PaymentGeneration ?? 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Key(rule)) + ":move";
        private static bool TryItemArguments(GameState state, int player, PaymentSourceRule rule, IReadOnlyList<string> ids, int count,
            out NormalizedValue args, out string reason)
        {
            args = null; reason = "payment_item_cost_invalid";
            var cost = rule?.ItemCost;
            if (cost == null || cost.CardInstanceId != rule.SourceRef?.ItemId || count < 1 || count > MaximumTokenCount || !ItemTransferService.IsTokenProfile(cost.OperationProfileId)) return false;
            var zone = state.ItemZones.Zones.SingleOrDefault(z => z.ParentKind == ZoneParentKind.Item && z.ParentId == cost.CardInstanceId && z.LocalZoneId == cost.LocalZoneId);
            if (zone == null) { reason = "card_zone_permission_denied"; return false; }
            var retired = cost.DestinationKind == "retired" ? state.ItemZones.Zones.SingleOrDefault(z => z.TemplateId == "core.zone.box_retired") : null;
            if (cost.DestinationKind != "original_supply" && (cost.DestinationKind != "retired" || retired == null)) return false;
            args = TokenArguments(player, ids, zone.ZoneId, retired?.ZoneId, count, cost.OperationProfileId); reason = ""; return true;
        }
        public static bool TryPlanItemCost(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner, PaymentSourceRule rule, int units,
            IReadOnlyList<string> ids, out PaymentTokenCostPlan plan, out string reason)
        {
            plan = null; reason = "payment_item_cost_invalid";
            long count = (long)units * (rule?.ItemCost?.CountPerUnit ?? 0);
            if (state == null || registry == null || owner == null || ids == null || rule?.SourceRef?.AuthorizedPlayerId != owner.PlayerId || count < 1 || count > MaximumTokenCount) return false;
            // 正规资源替代v1原有本人库存语义保留；通用api3自身仍支持Lua选择的异质/不同原主返还。
            if (ids.Any(id => !state.ItemZones.Items.Any(i => i.ItemId == id && i.OwnerSubject.Kind == ItemSubjectKind.Player && i.OwnerSubject.PlayerId == owner.PlayerId)))
            { reason = "payment_source_item_owner_invalid"; return false; }
            if (!TryItemArguments(state, owner.PlayerId, rule, ids, (int)count, out var args, out reason) ||
                !new ItemTransferService().TryPlanTokens(state, rule.SourceRef, args, registry.AbilityDeclarations, TokenOperationId(owner, rule), out var batch, out reason)) return false;
            plan = new PaymentTokenCostPlan { RuleKey = Key(rule), Arguments = args, Request = batch }; reason = ""; return true;
        }
        // 只读完整库存；资格来自同次真实规则来源和通用token规划，不复活旧付款计划器。
        public static IReadOnlyList<ItemInstanceState> TransferableItems(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner, PaymentSourceRule rule)
        {
            if (rule?.ItemCost == null || rule.SourceRef?.AuthorizedPlayerId != owner.PlayerId) return Array.Empty<ItemInstanceState>();
            var zone = CardMarkerCarrier.ResolveWithSource(state, rule.SourceRef, owner.PlayerId, rule.ItemCost.CardInstanceId, rule.ItemCost.LocalZoneId,
                "transfer." + rule.ItemCost.DestinationKind, registry.AbilityDeclarations, out var types, out _);
            if (zone == null) return Array.Empty<ItemInstanceState>();
            var itemTypes = ItemTypeRegistry.CreateBuiltIn();
            var result = new List<ItemInstanceState>();
            foreach (var item in state.ItemZones.Items.Where(i => i.ZoneId == zone.ZoneId && CardMarkerCarrier.Accepts(state, zone, i, owner.PlayerId, types)).OrderBy(i => i.ItemId, StringComparer.Ordinal))
            {
                if (!itemTypes.HasOperationProfile(item.TypeId, rule.ItemCost.OperationProfileId) ||
                    state.ItemZones.Zones.Any(z => z.ParentKind == ZoneParentKind.Item && z.ParentId == item.ItemId && state.ItemZones.Items.Any(i => i.ZoneId == z.ZoneId))) continue;
                if (rule.ItemCost.DestinationKind == "original_supply" && !state.ItemZones.Zones.Any(z => z.ZoneId == item.OriginalSupplyZoneId)) continue;
                // 不为每个候选克隆整局；完整组在TryPlanItemCost及最终事务再核端点、容量和当前位置。
                result.Add(item);
            }
            return result;
        }
        // 分组保留身份/去向事实；只消费本资源规则的SelectionPolicy，不代替其他卡牌Lua编排选择。
        public static string ItemSelectionGroup(GameState state, ItemInstanceState item) => StableIdFactory.Create("payment-token-facts.v1",
            item.TypeId, item.PhysicalKind.ToString(), JsonConvert.SerializeObject(item.OwnerSubject), JsonConvert.SerializeObject(item.ControllerSubject),
            item.OriginalSupplyZoneId, ItemTransferService.LocalStateDigest(item),
            state.EffectRuntime.InfluenceDepartures.Any(d => d.Status == "open" && d.ItemId == item.ItemId) ? item.ItemId : "");
        public static bool TrySelectItems(GameState state, EffectRegistry registry, EffectNodeRuntimeState owner, PaymentSourceRule rule, int units,
            IEnumerable<string> excludedIds, out IReadOnlyList<string> selected, out string reason)
        {
            selected = null; reason = "payment_token_selection_required";
            long count = (long)units * (rule.ItemCost?.CountPerUnit ?? 0);
            if (count < 1 || count > MaximumTokenCount) { reason = "payment_item_cost_invalid"; return false; }
            var excluded = new HashSet<string>(excludedIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            var items = TransferableItems(state, registry, owner, rule).Where(i => !excluded.Contains(i.ItemId)).ToList();
            if (items.Count < count) { reason = "insufficient_items"; return false; }
            if (items.Count != count && (rule.ItemCost.SelectionPolicy != "equivalent" || items.Select(i => ItemSelectionGroup(state, i)).Distinct().Count() != 1)) return false;
            selected = items.Take((int)count).Select(i => i.ItemId).ToList(); reason = ""; return true;
        }
        // 执行授权从真实付款节点的当前纯规则响应重建，绝不接受调用者传入一个source对象当证明。
        internal static AbilitySourceRef ResolveTransferAuthority(EffectExecutionContext context, ItemTransferRequest request)
        {
            var owner = context.Node; var p = owner.Payment;
            if (p?.PaymentProtocolVersion != 2 || !p.IsCommitRequested || p.IsCommitted || !p.BeforePaymentReady || !p.AllowsSubstitution ||
                p.PaymentId != owner.EffectId || p.OwnerEffectId != owner.EffectId || p.PlayerId != owner.PlayerId ||
                !IsResourcePaymentNode(owner) ||
                p.SourceDrafts == null || p.SourceEdits?.Any(e => e.Status == "open") != false) return null;
            var current = Query(context.State, context.Registry, owner, p.PaymentCost);
            AbilitySourceRef source = null;
            foreach (var draft in p.SourceDrafts.Where(d => d.Status == "valid" && d.Rule?.ItemCost != null))
            {
                var rule = current.SingleOrDefault(r => Key(r) == Key(draft.Rule));
                if (rule == null || Fingerprint(rule) != Fingerprint(draft.Rule) || draft.PaymentId != p.PaymentId || draft.PaymentGeneration != p.PaymentGeneration ||
                    draft.BuildIdentity != BuildIdentity(context.State, owner) || !PaymentResources.Valid(draft.Allocation)) continue;
                int units = PaymentResources.Total(draft.Allocation);
                if (units < 1 || units > rule.MaxUnits || PaymentResources.Types.Any(t => draft.Allocation.Get(t) > 0 && !rule.AllowedResourceTypes.Contains(t)) ||
                    p.SourceDrafts.Where(d => d.Status == "valid" && d.Rule.QuotaGroupId == rule.QuotaGroupId && d.Rule.QuotaScope == rule.QuotaScope).Sum(d => (long)PaymentResources.Total(d.Allocation)) > rule.QuotaLimit ||
                    !TryPlanItemCost(context.State, context.Registry, owner, rule, units, draft.SelectedItemIds, out var plan, out _)) continue;
                if (!plan.Request.Transfers.Any(t => JsonConvert.SerializeObject(t) == JsonConvert.SerializeObject(request))) continue;
                if (source != null) return null;
                source = rule.SourceRef;
            }
            return source;
        }
        internal static bool HasActualTransferBatchAuthority(EffectExecutionContext context, ItemTransferBatchRequest batch)
        {
            if (context.Node.Payment?.SourceDrafts == null || batch?.Transfers == null || batch.Transfers.Count < 1) return false;
            // 先走同一真实节点守卫，再把整组顺序/数量/操作ID与同一草稿精确匹配；片段拼接不算证明。
            var source = ResolveTransferAuthority(context, batch.Transfers[0]);
            if (source == null) return false;
            var current = Query(context.State, context.Registry, context.Node, context.Node.Payment.PaymentCost);
            int matches = 0;
            foreach (var draft in context.Node.Payment.SourceDrafts.Where(d => d.Status == "valid" && d.Rule?.SourceRef?.SourceKey == source.SourceKey && d.Rule.ItemCost != null))
            {
                var rule = current.SingleOrDefault(r => Key(r) == Key(draft.Rule));
                if (rule != null && Fingerprint(rule) == Fingerprint(draft.Rule) && TryPlanItemCost(context.State, context.Registry, context.Node, rule,
                    PaymentResources.Total(draft.Allocation), draft.SelectedItemIds, out var planned, out _) &&
                    ItemTransferService.BatchRequestDigest(planned.Request) == ItemTransferService.BatchRequestDigest(batch)) matches++;
            }
            return matches == 1;
        }
        public static bool TryRead(NormalizedValue value, AbilitySourceRef source, AbilityVersionRef version, out PaymentSourceRule rule, out string reason)
        {
            rule = null; reason = "payment_source_rule_invalid";
            string[] keys = { "ruleId", "purposeKind", "allowedResourceTypes", "maxUnits", "quotaGroupId", "quotaLimit", "quotaScope", "unitResourceCost", "itemCost", "insertion" };
            if (value?.Kind != NormalizedValueKind.Object || !value.IsValid() || value.Properties.Any(p => !keys.Contains(p.Name)) || value.Properties.Select(p => p.Name).Distinct().Count() != value.Properties.Count ||
                source == null || string.IsNullOrEmpty(Text(value, "ruleId")) || string.IsNullOrEmpty(Text(value, "quotaGroupId")) ||
                !new[] { "payment", "build", "pay", "condition", "city_move" }.Contains(Text(value, "purposeKind")) || !new[] { "payment", "build" }.Contains(Text(value, "quotaScope"))) return false;
            bool Count(string name) => Value(value, name)?.Kind == NormalizedValueKind.Integer && Value(value, name).IntegerValue >= 1 && Value(value, name).IntegerValue <= 99;
            var types = Value(value, "allowedResourceTypes");
            if (!Count("maxUnits") || !Count("quotaLimit") || types?.Kind != NormalizedValueKind.Array || types.Items.Count == 0 || !PaymentResources.TryRead(Value(value, "unitResourceCost"), out var cost)) return false;
            var parsed = new List<ResourceType>();
            foreach (var t in types.Items)
            { if (t?.Kind != NormalizedValueKind.String || !Enum.TryParse(t.StringValue, false, out ResourceType type) || !Enum.IsDefined(typeof(ResourceType), type) || type.ToString() != t.StringValue || parsed.Contains(type)) return false; parsed.Add(type); }
            PaymentItemCost item = null; var input = Value(value, "itemCost");
            if (input != null)
            {
                if (input.Kind != NormalizedValueKind.Object || !input.IsValid() || input.Properties.Any(p => !new[] { "localZoneId", "countPerUnit", "destinationKind", "operationProfileId", "selectionPolicy" }.Contains(p.Name)) ||
                    Text(input, "localZoneId").Length == 0 || Value(input, "countPerUnit")?.Kind != NormalizedValueKind.Integer || Value(input, "countPerUnit").IntegerValue < 1 || Value(input, "countPerUnit").IntegerValue > MaximumTokenCount ||
                    Value(input, "operationProfileId") != null && !ItemTransferService.IsTokenProfile(Text(input, "operationProfileId")) ||
                    Value(input, "selectionPolicy") != null && !new[] { "explicit", "equivalent" }.Contains(Text(input, "selectionPolicy")) ||
                    !new[] { "original_supply", "retired" }.Contains(Text(input, "destinationKind"))) return false;
                item = new PaymentItemCost { CardInstanceId = source.ItemId, LocalZoneId = Text(input, "localZoneId"), CountPerUnit = Number(input, "countPerUnit"), DestinationKind = Text(input, "destinationKind"),
                    OperationProfileId = Value(input, "operationProfileId") == null ? "core.cube_storage" : Text(input, "operationProfileId"),
                    SelectionPolicy = Value(input, "selectionPolicy") == null ? "equivalent" : Text(input, "selectionPolicy") };
            }
            PaymentSourceInsertion insertion = null;
            var declaredInsertion = Value(value, "insertion");
            if (declaredInsertion != null && !ResourcePaymentInsertionService.TryReadDeclaration(declaredInsertion, out insertion)) return false;
            rule = new PaymentSourceRule { Insertion = insertion, SourceRef = GameStateCloneService.DeepClone(source), VersionRef = version == null ? null : GameStateCloneService.DeepClone(version), RuleId = Text(value, "ruleId"),
                PurposeKind = Text(value, "purposeKind"), AllowedResourceTypes = parsed.OrderBy(t => (int)t).ToList(), MaxUnits = Number(value, "maxUnits"), QuotaGroupId = Text(value, "quotaGroupId"),
                QuotaLimit = Number(value, "quotaLimit"), QuotaScope = Text(value, "quotaScope"), UnitResourceCost = cost, ItemCost = item };
            reason = ""; return true;
        }
    }
}
