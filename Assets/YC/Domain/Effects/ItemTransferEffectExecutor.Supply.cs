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
    [Serializable]
    public sealed class FacilitySupplySourceFactV1
    {
        public string ItemId="",DefinitionId="",ZoneId="",SlotId="";
    }
    [Serializable]
    public sealed class FacilitySupplyFlowStateV1
    {
        public string ContractVersion="",OwnerEffectId="",ArgumentsDigest="",DeckZoneId="",OriginalSlotZoneId="";
        public bool NoTarget;
        public List<string> SupplyZoneIds=new List<string>();
        public List<FacilitySupplySourceFactV1> ReturnedItems=new List<FacilitySupplySourceFactV1>();
    }
    public static partial class ItemTransferEffectExecutor
    {
        public const string FacilityMoveDefinitionVersion="6.0.0",SupplyDefinitionVersion="2.3.0";
        public const string RetiredFacilityMoveSignature="facility_move_unversioned_signature_retired";
        public const string SupplyFlowProtocol="facility_supply.explicit.v3";
        private static bool IsCycle(NormalizedValue args)=>Value(args,"apiVersion")?.Kind==NormalizedValueKind.Integer&&new long[]{3,4}.Contains(Value(args,"apiVersion").IntegerValue);
        public const string RetiredFacilitySelection="facility_cycle_selection_contract_retired";
        internal static string ValidateExplicitMovementTarget(NormalizedValue selector)
        {
            if(selector?.Kind!=NormalizedValueKind.Object||selector.Properties.Any(p=>!new[]{"mode","itemIds","expectedSource"}.Contains(p.Name))||Text(selector,"mode")!="explicit")return "movement_explicit_target_required";
            var ids=Value(selector,"itemIds");var source=Value(selector,"expectedSource");
            if(ids?.Kind!=NormalizedValueKind.Array||ids.Items.Count!=1||ids.Items[0].Kind!=NormalizedValueKind.String||string.IsNullOrWhiteSpace(ids.Items[0].StringValue)||
                source?.Kind!=NormalizedValueKind.Object||source.Properties.Any(p=>!new[]{"zoneId","slotId"}.Contains(p.Name))||Value(source,"zoneId")?.Kind!=NormalizedValueKind.String||string.IsNullOrWhiteSpace(Text(source,"zoneId"))||
                Value(source,"slotId")!=null&&(Value(source,"slotId").Kind!=NormalizedValueKind.String||string.IsNullOrWhiteSpace(Text(source,"slotId"))))return "movement_explicit_target_required";
            return "";
        }
        public static string ValidateFacilityCycleArguments(NormalizedValue args)
        {
            if(Number(args,"apiVersion")==3)return RetiredFacilitySelection;
            if(args?.Kind!=NormalizedValueKind.Object||!args.IsValid()||Number(args,"apiVersion")!=4||args.Properties.Any(p=>!new[]{"kind","effectTypeId","apiVersion","player","operation","selector"}.Contains(p.Name))||
                !int.TryParse(Text(args,"player"),NumberStyles.None,CultureInfo.InvariantCulture,out int player)||player<1||Text(args,"operation")!="cycle_supply")return "facility_cycle_arguments_invalid";
            return ValidateExplicitMovementTarget(Value(args,"selector"));
        }
        public static string ValidateSavedSupplyVersion(string type,NormalizedValue args,string version,bool runtime=false)
        {
            if(type==LuaDomainEffectTypeIds.MoveFacilityCard)
            {
                if(Value(args,"apiVersion")==null)return RetiredFacilityMoveSignature;
                var api=Value(args,"apiVersion");
                if(api.Kind==NormalizedValueKind.Integer&&api.IntegerValue==3)return RetiredFacilitySelection;
                if(api.Kind!=NormalizedValueKind.Integer||api.IntegerValue!=2&&api.IntegerValue!=4)return "facility_move_contract_version_unsupported";
                return version==FacilityMoveDefinitionVersion||!runtime&&string.IsNullOrEmpty(version)?"":"facility_move_contract_version_unsupported";
            }
            if(type!=RefillTypeId&&type!=RefreshTypeId)return "";
            return version==SupplyDefinitionVersion||!runtime&&string.IsNullOrEmpty(version)?"":"facility_supply_contract_version_unsupported";
        }
        private static bool SupplyManaged(string type,NormalizedValue args)=>type==RefillTypeId||type==RefreshTypeId||type==LuaDomainEffectTypeIds.MoveFacilityCard&&IsCycle(args);
        private static NormalizedValue SharedTransferArguments(int player,NormalizedValue selector,string destination,string insertion,int required)=>Object(
            "apiVersion",NormalizedValue.CreateInteger(4),"player",NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)),"operation",NormalizedValue.CreateString("transfer"),
            "selector",selector,"destination",Object("zoneId",NormalizedValue.CreateString(destination)),"insertion",Object("kind",NormalizedValue.CreateString(insertion)),
            "operationProfileId",NormalizedValue.CreateString("core.facility_card_transfer"),"requiredCount",NormalizedValue.CreateInteger(required));
        private static EffectSpec SharedExplicit(int player,FacilitySupplySourceFactV1 item,string deck)
        {
            var source=Object("zoneId",NormalizedValue.CreateString(item.ZoneId));
            if(item.SlotId.Length>0){source.Properties.Add(new NormalizedValueEntry{Name="slotId",Value=NormalizedValue.CreateString(item.SlotId)});source.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));}
            return EffectSpec.Create(TypeId,SharedTransferArguments(player,Object("mode",NormalizedValue.CreateString("explicit"),"itemIds",Strings(new[]{item.ItemId}),"expectedSource",source),deck,"bottom",1),player);
        }
        private static EffectSpec SharedTop(int player,string deck,string slot)
        {
            var args=SharedTransferArguments(player,Object("mode",NormalizedValue.CreateString("top"),"zoneId",NormalizedValue.CreateString(deck),"itemTypeId",NormalizedValue.CreateString("core.facility_card"),"count",NormalizedValue.CreateInteger(1)),slot,"top",0);
            var destination=Value(args,"destination");destination.Properties.Add(new NormalizedValueEntry{Name="mustBeEmpty",Value=NormalizedValue.CreateBoolean(true)});destination.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));return EffectSpec.Create(TypeId,args,player);
        }
        private static EffectSpec SupplyShuffle(int player,string deck)=>EffectSpec.Create(DeckShuffleEffectExecutor.TypeId,Object("apiVersion",NormalizedValue.CreateInteger(1),"player",NormalizedValue.CreateString(player.ToString(CultureInfo.InvariantCulture)),"deckZone",Object("zoneId",NormalizedValue.CreateString(deck))),player);
        private static EffectSpec Labeled(EffectSpec spec,string label){spec.StableKey=label;return spec;}
        private static FacilitySupplyFlowStateV1 NewSupplyFlow(EffectExecutionContext c)=>new FacilitySupplyFlowStateV1{
            ContractVersion=SupplyFlowProtocol,OwnerEffectId=c.Node.EffectId,ArgumentsDigest=ItemTransferService.MovementV4Digest(c.Node.NormalizedArguments),DeckZoneId=FindZone(c.State,"facility_deck").ZoneId,
            SupplyZoneIds=FacilityLayerQuery.Supply(c.State).Select(s=>s.SlotZoneId).ToList()};
        private static List<EffectNodeRuntimeState> SupplyChildren(GameState state,EffectNodeRuntimeState node)=>node.ChildEffectIds.Where(id=>!state.EffectRuntime.DispatchReceipts.Any(d=>d.Succeeded&&d.AttachedEffectIds.Contains(id))).Select(id=>state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id)).ToList();
        private static NormalizedValue CycleProjection(FacilitySupplyFlowStateV1 flow,bool refilled)=>Object("changed",NormalizedValue.CreateBoolean(!flow.NoTarget),
            "selectedItemId",NormalizedValue.CreateString(flow.ReturnedItems.FirstOrDefault()?.ItemId??""),"selectedDefinitionId",NormalizedValue.CreateString(flow.ReturnedItems.FirstOrDefault()?.DefinitionId??""),
            "originalSupplySlotZoneId",NormalizedValue.CreateString(flow.OriginalSlotZoneId),"refilled",NormalizedValue.CreateBoolean(refilled),"reasonCode",NormalizedValue.CreateString(flow.NoTarget?"no_target":""));
        private static EffectStepResult ExecuteRefillCommon(EffectExecutionContext c)
        {
            string error=ValidateSupplyContext(c);if(error.Length>0)return EffectStepResult.Failed(error);
            if(c.Node.FlowStage.Length==0)
            {
                string slot=Text(c.Node.NormalizedArguments,"slotZoneId");
                if(!c.State.ItemZones.Zones.Any(z=>z.ZoneId==slot&&z.TemplateId=="core.zone.facility_supply_slot")||c.State.ItemZones.Items.Any(i=>i.ZoneId==slot))return EffectStepResult.Failed("destination_rejected");
                var flow=c.Node.FacilitySupplyFlowV1=NewSupplyFlow(c);flow.OriginalSlotZoneId=slot;
                return EffectStepResult.Continue("refill_wait").AddChild(Labeled(SharedTop(c.Node.PlayerId,flow.DeckZoneId,slot),c.Node.EffectId+":supply.top"));
            }
            if(!TryValidateSupplyFlow(c.State,c.Node))throw new InvalidOperationException("facility_supply_flow_state_invalid");
            var child=SupplyChildren(c.State,c.Node).Single();if(child.Status==EffectNodeStatus.Failed)return EffectStepResult.Failed(child.FailureReason).WithFlowStage("facility_failed");
            if(child.Status!=EffectNodeStatus.Completed)return EffectStepResult.NoProgress("等待共同补槽完成。");
            return TransferCompleted(child.CommonItemMovementV4.Receipt.Result,"empty_deck").WithFlowStage("facility_finished");
        }
        private static EffectStepResult ExecuteCycleCommon(EffectExecutionContext c)
        {
            string error=ValidateFacilityCycleArguments(c.Node.NormalizedArguments);if(error.Length>0)return EffectStepResult.Failed(error);
            if(Text(c.Node.NormalizedArguments,"player")!=c.Node.PlayerId.ToString(CultureInfo.InvariantCulture))return EffectStepResult.Failed("transfer_permission_denied");
            if(c.Node.FlowStage.Length==0)
            {
                var selector=Value(c.Node.NormalizedArguments,"selector");string id=Value(selector,"itemIds").Items[0].StringValue;
                var item=c.State.ItemZones.Items.SingleOrDefault(i=>i.ItemId==id);var source=Value(selector,"expectedSource");
                var slot=FacilityLayerQuery.Supply(c.State).SingleOrDefault(z=>z.SlotZoneId==Text(source,"zoneId"));
                if(item==null||slot==null||slot.ItemIds.FirstOrDefault()!=id||item.ZoneId!=slot.SlotZoneId||Value(source,"slotId")!=null&&item.SlotId!=Text(source,"slotId"))return EffectStepResult.Failed("source_zone_changed");
                var flow=c.Node.FacilitySupplyFlowV1=NewSupplyFlow(c);flow.OriginalSlotZoneId=item.ZoneId;
                flow.ReturnedItems.Add(new FacilitySupplySourceFactV1{ItemId=id,DefinitionId=item.DefinitionId,ZoneId=item.ZoneId,SlotId=item.SlotId});
                return EffectStepResult.Continue("cycle_move").AddChild(Labeled(SharedExplicit(c.Node.PlayerId,flow.ReturnedItems[0],flow.DeckZoneId),c.Node.EffectId+":supply.move"));
            }
            if(!TryValidateSupplyFlow(c.State,c.Node))throw new InvalidOperationException("facility_supply_flow_state_invalid");
            var saved=c.Node.FacilitySupplyFlowV1;var children=SupplyChildren(c.State,c.Node);var failed=children.FirstOrDefault(n=>n.Status==EffectNodeStatus.Failed);
            if(failed!=null)return EffectStepResult.Failed(failed.FailureReason).WithFlowStage("facility_failed");
            if(children.Any(n=>n.Status!=EffectNodeStatus.Completed))return EffectStepResult.NoProgress("等待共同回底及补槽完成。");
            if(c.Node.FlowStage=="cycle_move")
            {
                var fact=children[0].CommonItemMovementV4.Receipt.Items.Single();var item=c.State.ItemZones.Items.Single(i=>i.ItemId==fact.ItemId);
                if(!c.State.ItemZones.Items.Any(i=>i.ZoneId==saved.OriginalSlotZoneId))return EffectStepResult.Continue("cycle_refill").AddChild(Labeled(RefillSpec(c.Node.PlayerId,saved.OriginalSlotZoneId),c.Node.EffectId+":supply.refill"));
            }
            return EffectStepResult.Completed(CycleProjection(saved,children.Count==2&&Flag(children[1].NormalizedResult,"changed"))).WithFlowStage("facility_finished");
        }
        private static EffectStepResult ExecuteRefreshCommon(EffectExecutionContext c)
        {
            string error=ValidateSupplyContext(c);if(error.Length>0)return EffectStepResult.Failed(error);
            if(c.Node.FlowStage.Length==0)
            {
                var flow=c.Node.FacilitySupplyFlowV1=NewSupplyFlow(c);var step=EffectStepResult.Continue("refresh_return");
                foreach(var slot in FacilityLayerQuery.Supply(c.State))foreach(string id in slot.ItemIds){var item=c.State.ItemZones.Items.Single(i=>i.ItemId==id);flow.ReturnedItems.Add(new FacilitySupplySourceFactV1{ItemId=id,DefinitionId=item.DefinitionId,ZoneId=slot.SlotZoneId,SlotId=item.SlotId});}
                for(int i=0;i<flow.ReturnedItems.Count;i++)step.AddChild(Labeled(SharedExplicit(c.Node.PlayerId,flow.ReturnedItems[i],flow.DeckZoneId),c.Node.EffectId+":supply.return:"+i.ToString(CultureInfo.InvariantCulture)));
                return step;
            }
            if(!TryValidateSupplyFlow(c.State,c.Node))throw new InvalidOperationException("facility_supply_flow_state_invalid");
            var saved=c.Node.FacilitySupplyFlowV1;var children=SupplyChildren(c.State,c.Node);var failed=children.FirstOrDefault(n=>n.Status==EffectNodeStatus.Failed);
            if(failed!=null)return EffectStepResult.Failed(failed.FailureReason).WithFlowStage("facility_failed");
            if(children.Any(n=>n.Status!=EffectNodeStatus.Completed))return EffectStepResult.NoProgress("等待供应刷新共同步骤完成。");
            if(c.Node.FlowStage=="refresh_return")return EffectStepResult.Continue("refresh_shuffle").AddChild(Labeled(SupplyShuffle(c.Node.PlayerId,saved.DeckZoneId),c.Node.EffectId+":supply.shuffle"));
            if(c.Node.FlowStage=="refresh_shuffle")
            {
                var step=EffectStepResult.Continue("refresh_refill");for(int i=0;i<saved.SupplyZoneIds.Count;i++)step.AddChild(Labeled(RefillSpec(c.Node.PlayerId,saved.SupplyZoneIds[i]),c.Node.EffectId+":supply.refill:"+i.ToString(CultureInfo.InvariantCulture)));return step;
            }
            return EffectStepResult.Completed(Object("changed",NormalizedValue.CreateBoolean(true))).WithFlowStage("facility_finished");
        }
    }
}
