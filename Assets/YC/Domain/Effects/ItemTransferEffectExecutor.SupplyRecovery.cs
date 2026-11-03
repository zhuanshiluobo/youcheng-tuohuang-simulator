using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using YC.Domain.Facilities;
using YC.Domain.Items;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public static partial class ItemTransferEffectExecutor
    {
        public static bool HasRawSupplyFlowFields(JObject node)
        {
            if(node["FacilitySupplyFlowV1"]!=null&&node["FacilitySupplyFlowV1"].Type!=JTokenType.Null&&node["FacilitySupplyFlowV1"] is not JObject)return false;
            if(node["FacilitySupplyFlowV1"] is not JObject flow)return true;
            if(new[]{"ContractVersion","OwnerEffectId","ArgumentsDigest","DeckZoneId","OriginalSlotZoneId"}.Any(k=>flow[k]?.Type!=JTokenType.String)||flow["NoTarget"]?.Type!=JTokenType.Boolean||flow["SupplyZoneIds"] is not JArray zones||flow["ReturnedItems"] is not JArray rows)return false;
            return zones.All(x=>x.Type==JTokenType.String)&&rows.All(x=>x is JObject row&&new[]{"ItemId","DefinitionId","ZoneId","SlotId"}.All(k=>row[k]?.Type==JTokenType.String));
        }
        private static List<EffectSpec> ExpectedSupplySteps(EffectNodeRuntimeState node,List<EffectNodeRuntimeState> children)
        {
            var f=node.FacilitySupplyFlowV1;int p=node.PlayerId;var result=new List<EffectSpec>();
            if(node.EffectTypeId==RefillTypeId)result.Add(Labeled(SharedTop(p,f.DeckZoneId,f.OriginalSlotZoneId),node.EffectId+":supply.top"));
            else if(node.EffectTypeId==LuaDomainEffectTypeIds.MoveFacilityCard&&!f.NoTarget&&f.ReturnedItems.Count==1)
            {
                result.Add(Labeled(SharedExplicit(p,f.ReturnedItems[0],f.DeckZoneId),node.EffectId+":supply.move"));
                if(node.FlowStage=="cycle_refill"||new[]{"facility_finished","facility_failed"}.Contains(node.FlowStage)&&children.Count==2)result.Add(Labeled(RefillSpec(p,f.OriginalSlotZoneId),node.EffectId+":supply.refill"));
            }
            else if(node.EffectTypeId==RefreshTypeId)
            {
                for(int i=0;i<f.ReturnedItems.Count;i++)result.Add(Labeled(SharedExplicit(p,f.ReturnedItems[i],f.DeckZoneId),node.EffectId+":supply.return:"+i.ToString(CultureInfo.InvariantCulture)));
                if(node.FlowStage!="refresh_return"&&!(node.FlowStage=="facility_failed"&&children.Count==result.Count))result.Add(Labeled(SupplyShuffle(p,f.DeckZoneId),node.EffectId+":supply.shuffle"));
                if(node.FlowStage=="refresh_refill"||node.FlowStage=="facility_finished"||node.FlowStage=="facility_failed"&&children.Count==f.ReturnedItems.Count+1+f.SupplyZoneIds.Count)
                    for(int i=0;i<f.SupplyZoneIds.Count;i++)result.Add(Labeled(RefillSpec(p,f.SupplyZoneIds[i]),node.EffectId+":supply.refill:"+i.ToString(CultureInfo.InvariantCulture)));
            }
            return result;
        }
        public static bool TryValidateSupplyFlow(GameState state,EffectNodeRuntimeState node)
        {
            if(!SupplyManaged(node.EffectTypeId,node.NormalizedArguments))return node.FacilitySupplyFlowV1==null;
            if(ValidateSavedSupplyVersion(node.EffectTypeId,node.NormalizedArguments,node.DefinitionVersion,true).Length>0||node.PositionChanges.Count!=0||
                (node.EffectTypeId==LuaDomainEffectTypeIds.MoveFacilityCard?ValidateFacilityCycleArguments(node.NormalizedArguments):ValidateSupplyArguments(node.EffectTypeId,node.NormalizedArguments)).Length>0)return false;
            if(state.EffectRuntime.InteractionRequests.Any(r=>r.OwnerEffectId==node.EffectId))return false;
            var children=SupplyChildren(state,node);var f=node.FacilitySupplyFlowV1;
            if(node.FlowStage.Length==0)return f==null&&children.Count==0&&node.Status!=EffectNodeStatus.Completed;
            if(f==null||f.ContractVersion!=SupplyFlowProtocol||f.OwnerEffectId!=node.EffectId||f.ArgumentsDigest!=ItemTransferService.MovementV4Digest(node.NormalizedArguments)||
                f.DeckZoneId!=FindZone(state,"facility_deck").ZoneId||f.SupplyZoneIds==null||!f.SupplyZoneIds.SequenceEqual(FacilityLayerQuery.Supply(state).Select(s=>s.SlotZoneId))||f.ReturnedItems==null||
                f.ReturnedItems.Select(i=>i.ItemId).Distinct().Count()!=f.ReturnedItems.Count||f.ReturnedItems.Any(i=>!f.SupplyZoneIds.Contains(i.ZoneId)||!state.ItemZones.Items.Any(item=>item.ItemId==i.ItemId&&item.DefinitionId==i.DefinitionId)))return false;
            var stages=node.EffectTypeId==RefillTypeId?new[]{"refill_wait","facility_finished","facility_failed"}:node.EffectTypeId==RefreshTypeId?new[]{"refresh_return","refresh_shuffle","refresh_refill","facility_finished","facility_failed"}:new[]{"cycle_move","cycle_refill","facility_finished","facility_failed"};
            if(!stages.Contains(node.FlowStage))return false;
            if(node.EffectTypeId==RefillTypeId&&(f.NoTarget||f.ReturnedItems.Count!=0||f.OriginalSlotZoneId!=Text(node.NormalizedArguments,"slotZoneId")||!f.SupplyZoneIds.Contains(f.OriginalSlotZoneId)))return false;
            if(node.EffectTypeId==RefreshTypeId&&(f.NoTarget||f.OriginalSlotZoneId.Length!=0))return false;
            if(node.EffectTypeId==LuaDomainEffectTypeIds.MoveFacilityCard)
            {
                var selector=Value(node.NormalizedArguments,"selector");var source=Value(selector,"expectedSource");
                if(f.NoTarget||f.ReturnedItems.Count!=1||f.ReturnedItems[0].ZoneId!=f.OriginalSlotZoneId||f.ReturnedItems[0].ItemId!=Value(selector,"itemIds").Items[0].StringValue||
                    f.OriginalSlotZoneId!=Text(source,"zoneId")||Value(source,"slotId")!=null&&f.ReturnedItems[0].SlotId!=Text(source,"slotId"))return false;
            }
            var expected=ExpectedSupplySteps(node,children);if(expected.Count!=children.Count)return false;
            for(int i=0;i<children.Count;i++)
            {
                var child=children[i];var spec=expected[i];int index=node.ChildEffectIds.IndexOf(child.EffectId);
                if(child.ParentEffectId!=node.EffectId||child.PlayerId!=node.PlayerId||child.EffectTypeId!=spec.EffectTypeId||
                    child.EffectId!=StableIdFactory.Create("effect",node.EffectId,"child","child_effect",index.ToString(CultureInfo.InvariantCulture),spec.EffectTypeId,spec.StableKey)||
                    ItemTransferService.MovementV4Digest(child.NormalizedArguments)!=ItemTransferService.MovementV4Digest(spec.NormalizedArguments))return false;
            }
            if(node.EffectTypeId==LuaDomainEffectTypeIds.MoveFacilityCard&&f.ReturnedItems.Count>0&&node.FlowStage!="cycle_move"&&node.FlowStage!="facility_failed")
            {
                var receipt=children[0].CommonItemMovementV4?.Receipt;var fact=receipt?.Items.SingleOrDefault();
                if(children[0].Status!=EffectNodeStatus.Completed||receipt?.Result==null||receipt.Result.MovedCount!=1||fact==null||fact.ItemId!=f.ReturnedItems[0].ItemId||fact.From.ZoneId!=f.OriginalSlotZoneId)return false;
            }
            if(node.FlowStage=="facility_failed")return children.Any(c=>c.Status==EffectNodeStatus.Failed&&c.FailureReason==node.FailureReason);
            if(node.FlowStage!="facility_finished")return true;
            if(children.Any(c=>c.Status!=EffectNodeStatus.Completed))return false;
            NormalizedValue projection;
            if(node.EffectTypeId==RefillTypeId){if(children[0].CommonItemMovementV4?.Receipt==null)return false;projection=TransferCompleted(children[0].CommonItemMovementV4.Receipt.Result,"empty_deck").NormalizedResult;}
            else if(node.EffectTypeId==RefreshTypeId)projection=Object("changed",NormalizedValue.CreateBoolean(true));
            else projection=CycleProjection(f,children.Count==2&&Flag(children[1].NormalizedResult,"changed"));
            return ItemTransferService.MovementV4Digest(node.NormalizedResult)==ItemTransferService.MovementV4Digest(projection);
        }
    }
}
