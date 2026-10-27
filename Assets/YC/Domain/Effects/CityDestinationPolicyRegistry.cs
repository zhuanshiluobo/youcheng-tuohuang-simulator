using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Harvest;
using YC.Domain.Interactions;
using YC.Domain.Movement;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public sealed partial class EffectRegistry
    {
        private sealed class CityDestinationHandler
        {
            internal string Hash;
            internal Func<GameState,CandidateSetDraft,AbilitySourceRef,IReadOnlyList<CandidatePatch>> Invoke;
        }
        private readonly Dictionary<string, CityDestinationHandler> cityDestinationHandlers =
            new Dictionary<string, CityDestinationHandler>(StringComparer.Ordinal);
        private readonly Dictionary<string,string> collectionHandlerVersions = new Dictionary<string,string>(StringComparer.Ordinal);
        private bool collectionQueryContractRegistered;
        public CandidatePolicyRegistry CityDestinationCandidatePolicies { get; } = new CandidatePolicyRegistry();

        public void EnsureCollectionQueryContract()
        {
            if (collectionQueryContractRegistered) return;
            RegisterAbilityQueryContract(CollectionFreeLocationQuery.QueryKind, CollectionFreeLocationQuery.ValidateReturn);
            collectionQueryContractRegistered = true;
        }
        public void RegisterCollectionQueryHandlerVersion(string handlerRef,string hash)
        {
            if (string.IsNullOrEmpty(handlerRef) || string.IsNullOrEmpty(hash))
                throw new ArgumentException("collection_query_handler_version_missing");
            if (collectionHandlerVersions.TryGetValue(handlerRef,out var previous) && previous != hash)
                throw new InvalidOperationException("collection_query_handler_version_conflict");
            collectionHandlerVersions[handlerRef] = hash;
        }
        public void RegisterCityDestinationPolicyProvider(string handlerRef, string handlerHash,
            Func<GameState, CandidateSetDraft, AbilitySourceRef, IReadOnlyList<CandidatePatch>> provider)
        {
            if (string.IsNullOrEmpty(handlerRef) || string.IsNullOrEmpty(handlerHash) || provider == null || cityDestinationHandlers.ContainsKey(handlerRef))
                throw new ArgumentException("city_destination_provider_invalid_or_duplicate");
            cityDestinationHandlers.Add(handlerRef,new CityDestinationHandler {Hash=handlerHash,Invoke=provider});
        }

        public string ValidateCollectionEvidence(GameState state, CollectionFreeLocationResult facts)
        {
            foreach (var evidence in facts.QuerySources)
            {
                var source=evidence.SourceRef;var version=evidence.VersionRef;
                // 只消费002已交付的公开声明；不依赖008运行中目录的私有字段/方法。
                var declaration=AbilityDeclarations.FirstOrDefault(d=>d.AbilityId==source.AbilityId&&d.DefinitionId==source.DefinitionId);
                var item=state.ItemZones?.Items.Find(i=>i.ItemId==source.ItemId);
                var zone=state.ItemZones?.Zones.Find(z=>z.ZoneId==source.EffectiveZoneId);
                if (declaration==null || item?.DefinitionId!=source.DefinitionId || zone==null ||
                    !declaration.QueryKinds.Contains(CollectionFreeLocationQuery.QueryKind) ||
                    !declaration.EffectiveZones.Contains(zone.TemplateId) || declaration.ActiveFaceId!=source.ActiveFaceId ||
                    declaration.DeclarationVersion!=source.DeclarationVersion || version.ContentPackHash!=ContentPackHash ||
                    version.DefinitionVersion!=declaration.DeclarationVersion)
                    return "collection_saved_source_or_version_invalid";
                if (!collectionHandlerVersions.TryGetValue(declaration.HandlerRef,out var hash))
                    return "collection_query_version_binding_missing";
                if (hash!=version.HandlerHash) return "collection_saved_handler_version_invalid";
            }
            return "";
        }

        internal IReadOnlyList<CityDestinationExclusion> QueryCityDestinationExclusions(GameState state,CandidateSetDraft draft,string digest)
        {
            var query=new AbilityQueryContext {QueryId=draft.CandidateSetId,QueryKind=CityDestinationQueryService.QueryKind,
                MapId=state.MapId,ContentVersion=state.ContentPackHash,FactsDigest=digest,SubjectPlayerId=draft.ExecutingPlayerId};
            var result=new List<CityDestinationExclusion>();
            // 全局发现；规则来源的授权玩家与正在移动的玩家严格分开。
            foreach(var source in QuerySources(state,query).OrderBy(x=>x.SourceKey,StringComparer.Ordinal))
            {
                var declaration=AbilityDeclarations.Single(d=>d.AbilityId==source.AbilityId&&d.DefinitionId==source.DefinitionId);
                if(!cityDestinationHandlers.TryGetValue(declaration.HandlerRef,out var handler))
                    throw new InvalidOperationException("city_destination_provider_missing:"+declaration.HandlerRef);
                var patches=handler.Invoke(state,draft.Clone(),GameStateCloneService.DeepClone(source));
                if(patches==null) throw new InvalidOperationException("city_destination_return_missing");
                foreach(var patch in patches)
                {
                    if(patch==null || patch.Operation!=CandidatePatchOperation.Remove || patch.CandidateIds==null ||
                        patch.CandidateIds.Any(id=>!draft.CandidatePoolIds.Contains(id)) ||
                        patch.CandidateIds.Distinct(StringComparer.Ordinal).Count()!=patch.CandidateIds.Count)
                        throw new InvalidOperationException("city_destination_hard_exclusion_invalid");
                    foreach(string id in patch.CandidateIds)
                        if(!result.Any(x=>x.CandidateId==id&&x.SourceRef.SourceKey==source.SourceKey))
                            result.Add(new CityDestinationExclusion(id,source,new AbilityVersionRef {
                                ContentPackHash=ContentPackHash,DefinitionVersion=declaration.DeclarationVersion,
                                HandlerHash=handler.Hash,ExecutorVersion=CityDestinationQueryService.Version}));
                }
            }
            return result.AsReadOnly();
        }
    }
}
