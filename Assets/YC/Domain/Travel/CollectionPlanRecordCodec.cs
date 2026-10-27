using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YC.Domain.Effects;
using YC.Domain.Harvest;
using YC.Domain.Maps;
using YC.Domain.State;

namespace YC.Domain.Travel
{
    public static class CollectionPlanRecordCodec
    {
        // 固定字段序，不重复JSON字段名；不截断授权、grant或处理器版本。
        private static string[] SourceFields(AbilitySourceRef s,AbilityVersionRef v)=>new[] {
            s.SourceKey,s.ItemId,s.DefinitionId,s.AbilityId,s.ActiveFaceId,s.DeclarationVersion,
            s.AuthorizedPlayerId.ToString(CultureInfo.InvariantCulture),s.AuthorizationRef,s.EffectiveZoneId,
            s.RuleOwnerItemId,JsonConvert.SerializeObject(s.GrantingSourceRefs),v.ContractVersion,v.ContentPackHash,
            v.DefinitionVersion,v.HandlerHash,v.ExecutorVersion };

        public static void WriteFacts(NormalizedValue record,CollectionFreeLocationResult facts,CollectionPartition partition=null)
        {
            if(facts==null)throw new ArgumentNullException(nameof(facts));
            var value=NormalizedValue.CreateObject(new List<NormalizedValueEntry>());
            Put(value,"basis",NormalizedValue.CreateString(facts.BasisDigest));
            Put(value,"digest",NormalizedValue.CreateString(facts.ResultDigest));
            Put(value,"legal",NormalizedValue.CreateString(RouteSelectionSessionAuthority.Encode(facts.LegalLocationIds)));
            Put(value,"free",NormalizedValue.CreateString(RouteSelectionSessionAuthority.Encode(facts.FreeLocationIds)));
            Put(value,"sources",NormalizedValue.CreateArray(facts.QuerySources.Select(s=>NormalizedValue.CreateString(
                JsonConvert.SerializeObject(SourceFields(s.SourceRef,s.VersionRef)))).ToList()));
            Put(value,"evidence",NormalizedValue.CreateArray(facts.Evidence.Select(e=>NormalizedValue.CreateString(
                JsonConvert.SerializeObject(e.Kind=="city"?new[]{e.LocationId,e.Kind}:
                    new[]{e.LocationId,e.Kind}.Concat(SourceFields(e.SourceRef,e.VersionRef)).ToArray()))).ToList()));
            if(partition!=null)
            {
                Put(value,"selectedFree",NormalizedValue.CreateString(RouteSelectionSessionAuthority.Encode(partition.SelectedFreeLocationIds)));
                Put(value,"routeRequired",NormalizedValue.CreateString(RouteSelectionSessionAuthority.Encode(partition.RouteRequiredLocationIds)));
            }
            Put(record,"collection",value);
            if(!record.IsValid())throw new ArgumentException("collection_plan_record_too_large");
        }

        public static bool TryReadFacts(NormalizedValue record,out CollectionFreeLocationResult facts)
        {
            facts=null;
            try
            {
                var value=RouteSelectionSessionAuthority.Value(record,"collection");
                if(value?.Kind!=NormalizedValueKind.Object||!value.IsValid()||
                    value.Properties.Any(p=>!new[]{"basis","digest","legal","free","sources","evidence","selectedFree","routeRequired"}.Contains(p.Name))||
                    new[]{"basis","digest","legal","free"}.Any(k=>RouteSelectionSessionAuthority.Value(value,k)?.Kind!=NormalizedValueKind.String)||
                    RouteSelectionSessionAuthority.Value(value,"sources")?.Kind!=NormalizedValueKind.Array||
                    RouteSelectionSessionAuthority.Value(value,"evidence")?.Kind!=NormalizedValueKind.Array)return false;
                string basis=RouteSelectionSessionAuthority.Text(value,"basis");
                var sources=new List<CollectionQuerySource>();
                foreach(var encoded in RouteSelectionSessionAuthority.Value(value,"sources").Items)
                {
                    if(encoded.Kind!=NormalizedValueKind.String||!ReadSource(ReadStrings(encoded.StringValue),out var source,out var version))return false;
                    sources.Add(new CollectionQuerySource(source,version));
                }
                var evidence=new List<FreeLocationEvidence>();
                foreach(var encoded in RouteSelectionSessionAuthority.Value(value,"evidence").Items)
                {
                    if(encoded.Kind!=NormalizedValueKind.String)return false;
                    var fields=ReadStrings(encoded.StringValue);
                    if(fields.Length==2&&fields[1]=="city")evidence.Add(new FreeLocationEvidence(fields[0],fields[1],basis));
                    else
                    {
                        if(fields.Length!=18||fields[1]!="ability"||!ReadSource(fields.Skip(2).ToArray(),out var source,out var version))return false;
                        evidence.Add(new FreeLocationEvidence(fields[0],fields[1],basis,source,version));
                    }
                }
                facts=CollectionFreeLocationQuery.Create(
                    RouteSelectionSessionAuthority.Decode(RouteSelectionSessionAuthority.Text(value,"legal")),
                    RouteSelectionSessionAuthority.Decode(RouteSelectionSessionAuthority.Text(value,"free")),evidence,basis,sources);
                if(facts.ResultDigest!=RouteSelectionSessionAuthority.Text(value,"digest")){facts=null;return false;}
                return true;
            }
            catch(Exception error)when(error is ArgumentException||error is JsonException||error is OverflowException||error is UriFormatException)
            {facts=null;return false;}
        }

        private static bool ReadSource(string[] fields,out AbilitySourceRef source,out AbilityVersionRef version)
        {
            source=null;version=null;
            if(fields.Length!=16||fields.Any(f=>f==null)||
                !int.TryParse(fields[6],NumberStyles.None,CultureInfo.InvariantCulture,out int player)||player<1)return false;
            var grants=ReadStrings(fields[10]).ToList();
            if(grants.Any(string.IsNullOrEmpty)||grants.Distinct(StringComparer.Ordinal).Count()!=grants.Count)return false;
            source=new AbilitySourceRef {SourceKey=fields[0],ItemId=fields[1],DefinitionId=fields[2],AbilityId=fields[3],
                ActiveFaceId=fields[4],DeclarationVersion=fields[5],AuthorizedPlayerId=player,AuthorizationRef=fields[7],
                EffectiveZoneId=fields[8],RuleOwnerItemId=fields[9],GrantingSourceRefs=grants};
            version=new AbilityVersionRef {ContractVersion=fields[11],ContentPackHash=fields[12],DefinitionVersion=fields[13],
                HandlerHash=fields[14],ExecutorVersion=fields[15]};
            return true;
        }

        public static bool TryReadPartition(NormalizedValue record,IReadOnlyList<string> selected,out CollectionPartition partition)
        {
            partition=null;
            if(!TryReadFacts(record,out var facts))return false;
            var value=RouteSelectionSessionAuthority.Value(record,"collection");
            if(RouteSelectionSessionAuthority.Value(value,"selectedFree")?.Kind!=NormalizedValueKind.String||
                RouteSelectionSessionAuthority.Value(value,"routeRequired")?.Kind!=NormalizedValueKind.String)return false;
            try
            {
                partition=CollectionPlanService.Partition(selected,facts);
                if(!partition.SelectedFreeLocationIds.SequenceEqual(RouteSelectionSessionAuthority.Decode(RouteSelectionSessionAuthority.Text(value,"selectedFree")))||
                    !partition.RouteRequiredLocationIds.SequenceEqual(RouteSelectionSessionAuthority.Decode(RouteSelectionSessionAuthority.Text(value,"routeRequired"))))
                {partition=null;return false;}
                return true;
            }
            catch(ArgumentException){return false;}
        }

        // Host公共恢复整合点；建档registry为空时仅结构校验，正式恢复必须绑定当前目录。
        public static bool TryValidateRestoredPlans(GameState state,EffectRegistry registry,out string diagnostic,bool requireRegistry=true)
        {
            diagnostic="";
            if(state?.EffectRuntime==null){diagnostic="collection_recovery_context_missing";return false;}
            var owners=state.EffectRuntime.EffectNodes.Where(n=>n.EffectTypeId==RouteResourceSelectionEffectExecutor.TypeId&&
                RouteSelectionSessionAuthority.Value(n.ContinuationData,"routeSelection")!=null &&
                (RouteSelectionSessionAuthority.Integer(RouteSelectionSessionAuthority.Value(n.ContinuationData,"routeSelection"),"schemaVersion",1)==CollectionPlanService.ProtocolVersion ||
                 RouteSelectionSessionAuthority.Value(RouteSelectionSessionAuthority.Value(n.ContinuationData,"routeSelection"),"collection")!=null ||
                 RouteSelectionSessionAuthority.Integer(n.NormalizedArguments,"collectionPlanProtocol",1)==CollectionPlanService.ProtocolVersion)).ToArray();
            if(owners.Length==0)return true; // 原协议1恢复口径不变。
            if(registry==null&&requireRegistry){diagnostic="collection_recovery_registry_missing";return false;}
            IMapQueryService map;
            try{map=new MapQueryService(StaticMapDefinitions.Resolve(state.MapId));}
            catch(ArgumentException){diagnostic="collection_recovery_map_invalid";return false;}
            var authority=new RouteSelectionSessionAuthority(map,registry);
            foreach(var owner in owners)
            {
                var restored=authority.TryRestore(state,owner,out var session,out _);
                if(!restored.IsValid){diagnostic=restored.Reason;return false;}
                if(session.CollectionFreeLocations!=null)
                {
                    var valid=CollectionPlanService.ValidateFacts(session,session.CollectionFreeLocations);
                    if(!valid.IsValid){diagnostic=valid.Reason;return false;}
                    if(registry!=null)
                    {
                        diagnostic=registry.ValidateCollectionEvidence(state,session.CollectionFreeLocations);
                        if(diagnostic.Length>0)return false;
                    }
                }
                if(session.Stage==RouteSelectionStage.Committed||session.Stage==RouteSelectionStage.Settled)
                {
                    var resolved=authority.TryResolveFinalized(state,owner.EffectId,out _,out var commitment);
                    if(!resolved.IsValid){diagnostic=resolved.Reason;return false;}
                    var receipt=RouteSelectionSessionAuthority.Value(owner.ContinuationData,"collectionCommitmentId");
                    if(receipt!=null&&(receipt.Kind!=NormalizedValueKind.String||receipt.StringValue!=commitment.CommitmentId||owner.Status!=EffectNodeStatus.Completed))
                    {diagnostic="collection_recovery_receipt_invalid";return false;}
                }
                else if(RouteSelectionSessionAuthority.Value(owner.ContinuationData,"collectionCommitmentId")!=null)
                {diagnostic="collection_recovery_receipt_stage_invalid";return false;}
            }
            return true;
        }

        private static string[] ReadStrings(string text)
        {
            var array=JArray.Parse(text);
            if(array.Any(v=>v.Type!=JTokenType.String))throw new JsonSerializationException("collection_record_string_array_required");
            return array.Values<string>().ToArray();
        }
        private static void Put(NormalizedValue target,string key,NormalizedValue value)=>RouteSelectionSessionAuthority.Set(target,key,value);
    }
}
