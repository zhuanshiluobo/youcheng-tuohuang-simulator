using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Items
{
    [Serializable]
    public sealed class FacilityPlacementProofV1
    {
        public string Version="facility_placement.common.v1",OwnerEffectId="",BuildEffectId="",BuildArgumentsDigest="",BuildFactsDigest="",ItemId="",CellZoneId="",CoveredZoneId="",StagingZoneId="",PreviousTopItemId="";
        public int BuilderId;
        public bool Overlay;
    }
    // API-30/31 / 4.13：真实已付款/已授权建设允许共同批次进入设施层；没有自由建设权限。
    public static class FacilityPlacementMovement
    {
        private static bool Layer(GameState state,string id)=>state.ItemZones.Zones.Any(z=>z.ZoneId==id&&(z.TemplateId==FacilityLayerQuery.CellTemplate||z.TemplateId==FacilityLayerQuery.CoveredTemplate));
        private static bool HasLayers(GameState state,NormalizedValue args)=>new[]{"transfer","transfer_batch"}.Contains(Text(args,"operation"))&&ItemMovementV4.Legs(args).Any(l=>Layer(state,Text(Value(Value(l,"selector"),"expectedSource"),"zoneId"))||Layer(state,Text(Value(l,"destination"),"zoneId")));
        private static bool MakeProof(GameState state,EffectNodeRuntimeState node,out FacilityPlacementProofV1 proof)
        {
            proof=null;var build=state.EffectRuntime.EffectNodes.SingleOrDefault(n=>n.EffectId==node.ParentEffectId);
            if(build?.EffectTypeId!=FacilityEntryEffectTypeIds.Build||!build.ChildEffectIds.Contains(node.EffectId)||build.PlayerId!=node.PlayerId||!Flag(build.ContinuationData,"paid")||
                build.DefinitionVersion!=FacilityBuildEffectExecutor.DefinitionVersion)return false;
            var facts=FacilityBuildJournal.Read(build);var slot=FacilityBuildJournal.SelectedSlot(build);
            if(facts==null||facts.BuildId!=build.EffectId||facts.BuilderId!=build.PlayerId||slot==null||slot.PlayerId!=build.PlayerId||slot.NewItemId!=facts.ItemId||
                !state.ItemZones.Zones.Any(z=>z.ZoneId==slot.CellZoneId&&z.TemplateId==FacilityLayerQuery.CellTemplate&&z.OwnerPlayerId==build.PlayerId))return false;
            var staging=state.ItemZones.Zones.SingleOrDefault(z=>z.TemplateId=="core.zone.effect_staging"&&z.OwnerEffectId==build.EffectId);
            var covered=state.ItemZones.Zones.SingleOrDefault(z=>z.TemplateId==FacilityLayerQuery.CoveredTemplate&&z.ParentId==slot.CellZoneId);
            if(staging==null||covered==null||Text(node.NormalizedArguments,"operation")!="transfer_batch")return false;
            var legs=ItemMovementV4.Legs(node.NormalizedArguments).ToList();if(legs.Count!=(slot.IsOverlay?2:1))return false;
            bool Match(NormalizedValue leg,string item,string from,string to,string insertion)
            {
                var selector=Value(leg,"selector");var ids=Value(selector,"itemIds");
                return Text(leg,"operationProfileId")=="core.facility_card_transfer"&&Number(leg,"requiredCount",-1)==1&&Text(selector,"mode")=="explicit"&&ids?.Items.Count==1&&ids.Items[0].StringValue==item&&
                    Text(Value(selector,"expectedSource"),"zoneId")==from&&Text(Value(selector,"expectedSource"),"slotId")==""&&Text(Value(leg,"destination"),"zoneId")==to&&
                    Text(Value(leg,"insertion"),"kind")==insertion&&Text(Value(leg,"insertion"),"slotId")=="";
            }
            if(!Match(legs.Last(),facts.ItemId,staging.ZoneId,slot.CellZoneId,"unordered")||slot.IsOverlay&&!Match(legs[0],slot.ExpectedTopItemId,slot.CellZoneId,covered.ZoneId,"top"))return false;
            var immutable=new[]{"buildId","itemId","definitionId","builderId","sourceZoneId","sourceKind","sourceKey","sourceContentHash","sourceVersion","sourceHandlerHash","slot","entryRequired"};
            var frozen=NormalizedValue.CreateObject(facts.Data.Properties.Where(e=>immutable.Contains(e.Name)).Select(e=>new NormalizedValueEntry{Name=e.Name,Value=e.Value.Clone()}).ToList());
            proof=new FacilityPlacementProofV1{OwnerEffectId=node.EffectId,BuildEffectId=build.EffectId,BuildArgumentsDigest=ItemTransferService.MovementV4Digest(build.NormalizedArguments),BuildFactsDigest=ItemTransferService.MovementV4Digest(frozen),
                ItemId=facts.ItemId,BuilderId=build.PlayerId,Overlay=slot.IsOverlay,CellZoneId=slot.CellZoneId,CoveredZoneId=covered.ZoneId,StagingZoneId=staging.ZoneId,PreviousTopItemId=slot.ExpectedTopItemId};return true;
        }
        public static bool Allows(EffectExecutionContext context,ItemTransferRequest request)=>request.OperationProfileId=="core.facility_card_transfer"&&MakeProof(context.State,context.Node,out var proof)&&
            (request.ItemIds.SequenceEqual(new[]{proof.ItemId})&&request.ExpectedSource.ZoneId==proof.StagingZoneId&&request.Destination.ZoneId==proof.CellZoneId&&request.InsertionKind=="unordered"||
             proof.Overlay&&request.ItemIds.SequenceEqual(new[]{proof.PreviousTopItemId})&&request.ExpectedSource.ZoneId==proof.CellZoneId&&request.Destination.ZoneId==proof.CoveredZoneId&&request.InsertionKind=="top");
        public static bool TryBind(EffectExecutionContext context,out FacilityPlacementProofV1 proof,out string reason)
        {
            proof=null;reason="";if(!HasLayers(context.State,context.Node.NormalizedArguments))return true;
            if(!MakeProof(context.State,context.Node,out proof)){reason="operation_profile_rejected";return false;}
            return Revalidate(context,proof,out reason);
        }
        public static bool Revalidate(EffectExecutionContext context,FacilityPlacementProofV1 proof,out string reason)
        {
            reason="facility_build_transfer_authorization_invalid";
            if(!MakeProof(context.State,context.Node,out var actual)||ItemTransferService.MovementV4Digest(actual)!=ItemTransferService.MovementV4Digest(proof))return false;
            var build=context.State.EffectRuntime.EffectNodes.Single(n=>n.EffectId==proof.BuildEffectId);
            if(build.FlowStage!="place"||build.Status==EffectNodeStatus.Completed||build.Status==EffectNodeStatus.Failed||build.Status==EffectNodeStatus.Faulted)return false;
            try{context.Registry.FacilityBuildQueries.RevalidateSlot(context.State,FacilityBuildJournal.SelectedSlot(build),build.EffectId);}
            catch(InvalidOperationException e){reason=e.Message;return false;}
            reason="";return true;
        }
        public static bool ValidateHistory(GameState state,EffectNodeRuntimeState node,CommonItemMovementPlanV4 plan)
        {
            if(!HasLayers(state,node.NormalizedArguments))return plan.FacilityPlacement==null;
            return plan.FacilityPlacement!=null&&MakeProof(state,node,out var expected)&&ItemTransferService.MovementV4Digest(expected)==ItemTransferService.MovementV4Digest(plan.FacilityPlacement);
        }
        public static bool IsCommitted(GameState state,EffectNodeRuntimeState node,EffectNodeRuntimeState build)=>node?.ParentEffectId==build.EffectId&&node.Status==EffectNodeStatus.Completed&&
            node.CommonItemMovementV4?.FacilityPlacement?.BuildEffectId==build.EffectId&&node.CommonItemMovementV4.Receipt?.Result.MovedItemIds.Contains(FacilityBuildJournal.Read(build).ItemId)==true&&ItemMovementV4.TryValidateState(state,node,out _);
        public static EffectSpec Create(GameState state,int player,IReadOnlyList<ItemTransferRequest> requests)
        {
            var legs=requests.Select(r=>Object("selector",Object("mode",NormalizedValue.CreateString("explicit"),"itemIds",Strings(r.ItemIds),"expectedSource",Object("zoneId",NormalizedValue.CreateString(r.ExpectedSource.ZoneId))),
                "destination",Object("zoneId",NormalizedValue.CreateString(r.Destination.ZoneId)),"insertion",Object("kind",NormalizedValue.CreateString(r.InsertionKind)),"operationProfileId",NormalizedValue.CreateString(r.OperationProfileId),"requiredCount",NormalizedValue.CreateInteger(r.RequiredCount))).ToList();
            var orders=requests.SelectMany(r=>new[]{r.ExpectedSource.ZoneId,r.Destination.ZoneId}).Distinct().OrderBy(id=>id,StringComparer.Ordinal).Select(id=>
            {
                var order=state.ItemZones.Orders.SingleOrDefault(o=>o.ZoneId==id);
                return Object("zoneId",NormalizedValue.CreateString(id),"digest",NormalizedValue.CreateString(FacilityLayerQuery.Digest(state,id)),"orderRevision",NormalizedValue.CreateInteger(order?.OrderRevision??0),"expectedTopItemId",NormalizedValue.CreateString(order?.ItemIds.FirstOrDefault()??""));
            }).ToList();
            return EffectSpec.Create(TypeId,Object("apiVersion",NormalizedValue.CreateInteger(4),"player",NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)),"operation",NormalizedValue.CreateString("transfer_batch"),"transfers",NormalizedValue.CreateArray(legs),"expectedOrders",NormalizedValue.CreateArray(orders)),player);
        }
        internal static string OrderDigest(MarkerMoveOrderFactV4 order)
        {
            var ids=order.HasOrder?order.ItemIds:order.ItemIds.OrderBy(id=>id,StringComparer.Ordinal).ToList();
            var value=new StringBuilder().Append(order.ZoneId.Length).Append(':').Append(order.ZoneId).Append(':').Append(order.Revision);
            foreach(string id in ids)value.Append(':').Append(id.Length).Append(':').Append(id);
            using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToString()))).Replace("-","").ToLowerInvariant();
        }
    }
}
