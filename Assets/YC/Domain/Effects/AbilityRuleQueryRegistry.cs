using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Effects
{
    public sealed partial class EffectRegistry
    {
        private readonly Dictionary<string, Func<AbilityActionCallContext, AbilityQueryContext, NormalizedValue>> abilityPureHandlers = new Dictionary<string, Func<AbilityActionCallContext, AbilityQueryContext, NormalizedValue>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<AbilityQueryContext, AbilitySourceRef, NormalizedValue, string>> pureQueryContracts = new Dictionary<string, Func<AbilityQueryContext, AbilitySourceRef, NormalizedValue, string>>(StringComparer.Ordinal);
        public void RegisterAbilityPureHandler(string handlerId, Func<AbilityActionCallContext, AbilityQueryContext, NormalizedValue> handler)
        { if (HasAbilityCatalog || string.IsNullOrEmpty(handlerId) || handler == null) throw new ArgumentException("ability_query_handler_invalid"); abilityPureHandlers.Add(handlerId, handler); }
        /// <summary>查询消费方必须登记返回类别、目标及合并规则验证；未接通类别明确拒绝。</summary>
        public void RegisterAbilityQueryContract(string queryKind, Func<AbilityQueryContext, AbilitySourceRef, NormalizedValue, string> validator)
        { if (string.IsNullOrEmpty(queryKind) || validator == null) throw new ArgumentException("ability_query_contract_invalid"); pureQueryContracts.Add(queryKind, validator); }
        public IReadOnlyList<AbilitySourceRef> QuerySources(GameState state, AbilityQueryContext query)
        {
            if (query == null || string.IsNullOrEmpty(query.QueryId) || string.IsNullOrEmpty(query.FactsDigest) || query.ContentVersion != ContentPackHash || query.MapId != state.MapId)
                throw new InvalidOperationException("ability_query_context_or_version_invalid");
            return QuerySources(state, -1, "ruleQuery", queryKind: query.QueryKind);
        }
        public IReadOnlyList<AbilityRuleQueryResult> QueryRules(GameState state, AbilityQueryContext query)
        {
            if (query == null || !pureQueryContracts.TryGetValue(query.QueryKind, out var validate)) throw new InvalidOperationException("ability_query_kind_not_connected");
            if (query.Facts == null || !query.Facts.IsValid() || query.SubjectPlayerId >= 0 && state.FindPlayer(query.SubjectPlayerId) == null ||
                query.SourceRef != null && !AbilitySourceService.IsCurrent(state, abilityDeclarations, query.SourceRef)) throw new InvalidOperationException("ability_query_source_or_facts_invalid");
            var results = new List<AbilityRuleQueryResult>();
            foreach (var source in QuerySources(state, query))
            {
                if (query.SourceRef != null && query.SourceRef.SourceKey != source.SourceKey) continue;
                results.Add(EvaluateRuleQuery(state, query, source, validate));
            }
            return results;
        }

        // 公共样式的受证调用没有宣告标记；其费用查询只继承本次真实行动的授权。
        // 不向普通QuerySources发布临时来源，也不把该授权扩成其它查询类别。
        internal IReadOnlyList<AbilityRuleQueryResult> QueryGrantedStylePaymentDemand(GameState state, AbilityQueryContext query, AbilitySourceRef actionSource, string actionId)
        {
            if (query?.QueryKind != ResourcePaymentCostPreview.DemandQueryKind || actionSource == null ||
                !pureQueryContracts.TryGetValue(query.QueryKind, out var validate)) return Array.Empty<AbilityRuleQueryResult>();
            var invocation = state.EffectRuntime.AbilityActionInvocations.SingleOrDefault(i => i.ActionId == actionId &&
                i.SourceRef?.SourceKey == actionSource.SourceKey && i.SourceRef.ItemId == actionSource.ItemId && i.SourceRef.DefinitionId == actionSource.DefinitionId &&
                i.SourceRef.AbilityId == actionSource.AbilityId && i.SourceRef.AuthorizedPlayerId == actionSource.AuthorizedPlayerId &&
                i.SourceRef.AuthorizationRef == actionSource.AuthorizationRef && i.SourceRef.EffectiveZoneId == actionSource.EffectiveZoneId &&
                i.SourceRef.DeclarationVersion == actionSource.DeclarationVersion && i.SourceRef.ActiveFaceId == actionSource.ActiveFaceId &&
                i.SourceRef.RuleOwnerItemId == actionSource.RuleOwnerItemId && i.VersionRef?.ContentPackHash == ContentPackHash &&
                i.SourceRef.GrantingSourceRefs.SequenceEqual(actionSource.GrantingSourceRefs) &&
                AbilityLegacyStyleBridge.IsPublicGrant(state, i.SourceRef) && AbilityLegacyStyleBridge.SourceCurrent(state, i));
            var owner = state.EffectRuntime.EffectNodes.Find(n => n.EffectId == invocation?.OwnerEffectId);
            if (owner == null || owner.Status == EffectNodeStatus.Completed || owner.Status == EffectNodeStatus.Failed || owner.Status == EffectNodeStatus.Faulted)
                return Array.Empty<AbilityRuleQueryResult>();
            var zone = state.ItemZones.Zones.Find(z => z.ZoneId == actionSource.EffectiveZoneId);
            var results = new List<AbilityRuleQueryResult>();
            foreach (var declaration in abilityDeclarations.Where(d => d.DefinitionId == actionSource.DefinitionId && d.Kind == "ruleQuery" &&
                d.AuthorizationPolicy == "public_declaration" && d.QueryKinds.Contains(query.QueryKind) && d.EffectiveZones.Contains(zone?.TemplateId)))
            {
                var source = GameStateCloneService.DeepClone(actionSource);
                source.AbilityId = declaration.AbilityId; source.DeclarationVersion = declaration.DeclarationVersion;
                source.SourceKey = StableIdFactory.Create("ability-source", source.ItemId, source.AbilityId, source.ActiveFaceId, source.DeclarationVersion,
                    source.AuthorizedPlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                results.Add(EvaluateRuleQuery(state, query, source, validate));
            }
            return results;
        }

        private AbilityRuleQueryResult EvaluateRuleQuery(GameState state, AbilityQueryContext query, AbilitySourceRef source,
            Func<AbilityQueryContext, AbilitySourceRef, NormalizedValue, string> validate)
        {
            var declaration = SourceDeclaration(source);
            if (!abilityPureHandlers.TryGetValue(declaration.HandlerRef, out var handler)) throw new InvalidOperationException("ability_query_handler_missing");
            var value = handler(new AbilityActionCallContext { State = state, SourceRef = source, Arguments = query.Facts }, query);
            if (value?.Kind != NormalizedValueKind.Object || value.Properties.Count != 3 || Text(value, "queryId") != query.QueryId || Text(value, "basisDigest") != query.FactsDigest ||
                Value(value, "items")?.Kind != NormalizedValueKind.Array) throw new InvalidOperationException("ability_query_return_invalid");
            string error = validate(query, source, Value(value, "items")); if (error.Length > 0) throw new InvalidOperationException("ability_query_return_invalid:" + error);
            return new AbilityRuleQueryResult { QueryId = query.QueryId, QueryKind = query.QueryKind, BasisDigest = query.FactsDigest, SourceRef = GameStateCloneService.DeepClone(source),
                VersionRef = new AbilityVersionRef { ContentPackHash = ContentPackHash, DefinitionVersion = declaration.DeclarationVersion, HandlerHash = HandlerHash(declaration.HandlerRef) }, Items = Value(value, "items").Clone() };
        }
    }
}
