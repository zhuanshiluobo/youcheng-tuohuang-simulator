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
                var declaration = SourceDeclaration(source);
                if (!abilityPureHandlers.TryGetValue(declaration.HandlerRef, out var handler)) throw new InvalidOperationException("ability_query_handler_missing");
                var value = handler(new AbilityActionCallContext { State = state, SourceRef = source, Arguments = query.Facts }, query);
                if (value?.Kind != NormalizedValueKind.Object || value.Properties.Count != 3 || Text(value, "queryId") != query.QueryId || Text(value, "basisDigest") != query.FactsDigest ||
                    Value(value, "items")?.Kind != NormalizedValueKind.Array) throw new InvalidOperationException("ability_query_return_invalid");
                string error = validate(query, source, Value(value, "items")); if (error.Length > 0) throw new InvalidOperationException("ability_query_return_invalid:" + error);
                results.Add(new AbilityRuleQueryResult { QueryId = query.QueryId, QueryKind = query.QueryKind, BasisDigest = query.FactsDigest, SourceRef = GameStateCloneService.DeepClone(source),
                    VersionRef = new AbilityVersionRef { ContentPackHash = ContentPackHash, DefinitionVersion = declaration.DeclarationVersion, HandlerHash = HandlerHash(declaration.HandlerRef) }, Items = Value(value, "items").Clone() });
            }
            return results;
        }
    }
}
