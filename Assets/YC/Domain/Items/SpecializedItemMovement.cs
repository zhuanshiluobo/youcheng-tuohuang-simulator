using System;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Movement;
using YC.Domain.Presentation;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Items
{
    // API-31 / 4.13：专用及有限旧输入只补参数、组织共同步骤、投影原结果。
    public static class SpecializedItemMovement
    {
        private const string SourceZone = "sharedTokenSourceZone", SourceSlot = "sharedTokenSourceSlot";
        public static bool IsCharacter(string type, NormalizedValue args) => type==LuaDomainEffectTypeIds.MoveCharacterCard && Value(args,"apiVersion")!=null;
        public static bool IsFacility(string type,NormalizedValue args)=>type==LuaDomainEffectTypeIds.MoveFacilityCard&&Value(args,"apiVersion")?.Kind==NormalizedValueKind.Integer&&Value(args,"apiVersion").IntegerValue==2;
        private static bool Token(EffectNodeRuntimeState node) => node.EffectTypeId == LuaDomainEffectTypeIds.OperateToken;
        private static bool Applicable(EffectNodeRuntimeState node) => node.EffectTypeId==PlayerMarkerOperation.TypeId || IsFacility(node.EffectTypeId,node.NormalizedArguments) || Token(node) || IsCharacter(node.EffectTypeId,node.NormalizedArguments);
        private static NormalizedValue Arguments(EffectNodeRuntimeState node)
        {
            var args=node.NormalizedArguments;
            if(node.EffectTypeId==PlayerMarkerOperation.TypeId)return PlayerMarkerOperation.CommonArguments(args);
            if (Token(node))
            {
                var from=Object("zoneId",Value(node.ContinuationData,SourceZone).Clone());
                if(Text(node.ContinuationData,SourceSlot).Length>0)from.Properties.Add(new NormalizedValueEntry{Name="slotId",Value=Value(node.ContinuationData,SourceSlot).Clone()});
                return Object("apiVersion",NormalizedValue.CreateInteger(4),"player",Value(args,"player").Clone(),"operation",NormalizedValue.CreateString("move_map_token"),
                "selector",Object("mode",NormalizedValue.CreateString("explicit"),"itemIds",Strings(new[]{Text(args,"tokenItemId")}),"expectedSource",from),
                "destinationLocationId",Value(args,"destinationLocationId").Clone(),"operationProfileId",Value(args,"operationProfileId").Clone());
            }
            var source=Value(args,"expectedSource").Clone();
            if (Value(source,"slotId")?.Kind==NormalizedValueKind.String && Text(source,"slotId").Length==0) source.Properties.RemoveAll(p=>p.Name=="slotId");
            var result=Object("apiVersion",NormalizedValue.CreateInteger(4),"player",Value(args,"player").Clone(),"operation",NormalizedValue.CreateString("transfer"),
                "selector",Object("mode",NormalizedValue.CreateString("explicit"),"itemIds",Strings(Value(args,"itemRefs").Items.Select(i=>Text(i,"itemId"))),"expectedSource",source),
                "destination",Value(args,"destination").Clone(),"insertion",Value(args,"insertion").Clone(),"operationProfileId",Value(args,"operationProfileId").Clone(),"requiredCount",NormalizedValue.CreateInteger(Number(args,"requiredCount")));
            if (Value(args,"carrierPolicy")!=null) result.Properties.Add(new NormalizedValueEntry{Name="carrierPolicy",Value=Value(args,"carrierPolicy").Clone()});
            if (Value(args,"reveal")!=null) result.Properties.Add(new NormalizedValueEntry{Name="reveal",Value=Value(args,"reveal").Clone()});
            result.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));return result;
        }
        private static EffectSpec Step(EffectNodeRuntimeState node)
        {
            var spec=EffectSpec.Create(TypeId,Arguments(node),node.PlayerId);spec.StableKey=node.EffectId+":shared-item-v4";
            spec.Presentation=GameplayPresentationNormalizer.Clone(node.Presentation);return spec;
        }
        public static EffectStepResult ExecuteCharacter(EffectExecutionContext context)
        {
            if(context.Node.FlowStage.Length==0)
            {
                var args=context.Node.NormalizedArguments;var ids=Value(args,"itemRefs").Items.Select(i=>Text(i,"itemId")).ToList();
                if(ids.Distinct(StringComparer.Ordinal).Count()!=ids.Count)return EffectStepResult.Failed("duplicate_item_id");
                if(ids.Count<Number(args,"requiredCount"))return EffectStepResult.Failed("insufficient_items");
                if(Text(args,"player")!=context.Node.PlayerId.ToString(CultureInfo.InvariantCulture))return EffectStepResult.Failed("transfer_permission_denied");
                var types=ItemTypeRegistry.CreateBuiltIn();
                foreach(string id in ids)
                {
                    var item=context.State.ItemZones.Items.SingleOrDefault(i=>i.ItemId==id);
                    if(item==null)return EffectStepResult.Failed("unknown_item");
                    if(!types.IsAssignableTo(item.TypeId,"core.character_card"))return EffectStepResult.Failed("character_type_rejected");
                }
            }
            return ExecuteWrapper(context);
        }
        public static EffectStepResult ExecuteFacility(EffectExecutionContext context)
        {
            if(context.Node.FlowStage.Length==0)
            {
                var ids=Value(context.Node.NormalizedArguments,"itemRefs").Items.Select(i=>Text(i,"itemId")).ToList();
                if(ids.Count<Number(context.Node.NormalizedArguments,"requiredCount"))return EffectStepResult.Failed("insufficient_items");
                foreach(string id in ids)
                {
                    var item=context.State.ItemZones.Items.SingleOrDefault(i=>i.ItemId==id);
                    if(item==null)return EffectStepResult.Failed("unknown_item");
                    if(!ItemTypeRegistry.CreateBuiltIn().IsAssignableTo(item.TypeId,"core.facility_card"))return EffectStepResult.Failed("destination_rejected");
                }
            }
            return ExecuteWrapper(context);
        }
        public static EffectStepResult ExecuteToken(EffectExecutionContext context)
        {
            if(context.Node.FlowStage.Length==0)
            {
                var args=context.Node.NormalizedArguments;string error=MapOperationEffectExecutor.ValidateArguments(context.Node.EffectTypeId,args);
                if(error.Length>0)return EffectStepResult.Failed(error);
                if(Text(args,"player")!=context.Node.PlayerId.ToString(CultureInfo.InvariantCulture))return EffectStepResult.Failed("invalid_player");
                error=context.Registry.TokenMovementProfiles.ValidateMove(context.State,context.Node.PlayerId,Text(args,"tokenItemId"),Text(args,"destinationLocationId"),Text(args,"operationProfileId"));
                if(error.Length>0)return EffectStepResult.Failed(error);
                var item=context.State.ItemZones.Items.Single(i=>i.ItemId==Text(args,"tokenItemId"));
                Set(context.Node,SourceZone,NormalizedValue.CreateString(item.ZoneId));Set(context.Node,SourceSlot,NormalizedValue.CreateString(item.SlotId));
            }
            return ExecuteWrapper(context);
        }
        internal static EffectStepResult ExecuteWrapper(EffectExecutionContext context)
        {
            var node=context.Node;
            if(node.FlowStage.Length==0)
            {
                string error=ItemMovementV4.ValidateArguments(Arguments(node));if(error.Length>0)return EffectStepResult.Failed(error);
                return EffectStepResult.Continue("shared_item").AddChild(Step(node));
            }
            if(!TryValidate(context.State,node))throw new InvalidOperationException("specialized_item_movement_state_invalid");
            var body=Body(context.State,node);
            if(body.Status==EffectNodeStatus.Failed)return EffectStepResult.Failed(body.FailureReason).WithFlowStage("shared_item_finished");
            if(body.Status!=EffectNodeStatus.Completed)return EffectStepResult.NoProgress("等待共同物品步骤及必要响应完成。");
            return EffectStepResult.Completed(Projection(context.State,node,body)).WithFlowStage("shared_item_finished");
        }
        public static EffectNodeRuntimeState Body(GameState state,EffectNodeRuntimeState node)=>node.ChildEffectIds.Where(id=>!state.EffectRuntime.DispatchReceipts.Any(d=>d.Succeeded&&d.AttachedEffectIds.Contains(id))).Select(id=>state.EffectRuntime.EffectNodes.SingleOrDefault(n=>n.EffectId==id))
            .SingleOrDefault(n=>n!=null&&n.EffectTypeId==TypeId&&IsV4(n.EffectTypeId,n.NormalizedArguments));
        private static bool IsV4(string type,NormalizedValue args)=>ItemMovementV4.IsV4(type,args);
        private static NormalizedValue Projection(GameState state,EffectNodeRuntimeState node,EffectNodeRuntimeState body)
        {
            var receipt=body.CommonItemMovementV4?.Receipt??body.ItemMovementV4?.Receipt;
            if(Token(node))return state.EffectRuntime.RuleEvents.Single(e=>receipt.EventIds.Contains(e.EventId)&&e.EventType=="token.moved").Payload.Clone();
            var result=receipt.Result;var projected=Object("changed",NormalizedValue.CreateBoolean(result.Changed),"movedCount",NormalizedValue.CreateInteger(result.MovedCount),"movedItemIds",Strings(result.MovedItemIds));
            if(IsFacility(node.EffectTypeId,node.NormalizedArguments)&&!result.Changed){projected.Properties.Add(new NormalizedValueEntry{Name="reasonCode",Value=NormalizedValue.CreateString("no_candidates")});projected.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));}return projected;
        }
        public static bool TryValidate(GameState state,EffectNodeRuntimeState node)
        {
            if(!Applicable(node))return true;
            if(node.PositionChanges.Count!=0||node.ItemMovementV4!=null||node.CommonItemMovementV4!=null||node.CharacterCoverMovementV4!=null)return false;
            var own=node.ChildEffectIds.Where(id=>!state.EffectRuntime.DispatchReceipts.Any(d=>d.Succeeded&&d.AttachedEffectIds.Contains(id))).ToList();
            if(node.FlowStage.Length==0)return own.Count==0&&node.Status!=EffectNodeStatus.Completed&&node.PendingOutcome!=EffectPendingOutcome.Completed;
            if(!new[]{"shared_item","shared_item_finished"}.Contains(node.FlowStage)||own.Count!=1)return false;
            if(Token(node)&&(Value(node.ContinuationData,SourceZone)?.Kind!=NormalizedValueKind.String||Text(node.ContinuationData,SourceZone).Length==0||Value(node.ContinuationData,SourceSlot)?.Kind!=NormalizedValueKind.String))return false;
            var body=Body(state,node);if(body==null||own[0]!=body.EffectId||body.ParentEffectId!=node.EffectId||body.PlayerId!=node.PlayerId)return false;
            int index=node.ChildEffectIds.IndexOf(body.EffectId);
            if(body.EffectId!=StableIdFactory.Create("effect",node.EffectId,"child","child_effect",index.ToString(CultureInfo.InvariantCulture),TypeId,node.EffectId+":shared-item-v4")||
                ItemTransferService.MovementV4Digest(body.NormalizedArguments)!=ItemTransferService.MovementV4Digest(Arguments(node))||!ItemMovementV4.TryValidateState(state,body,out _))return false;
            if(node.FlowStage!="shared_item_finished")return node.Status!=EffectNodeStatus.Completed;
            if(node.Status==EffectNodeStatus.Failed||node.PendingOutcome==EffectPendingOutcome.Failed)return body.Status==EffectNodeStatus.Failed&&node.FailureReason==body.FailureReason;
            return body.Status==EffectNodeStatus.Completed&&(body.CommonItemMovementV4?.Receipt??body.ItemMovementV4?.Receipt)!=null&&ItemTransferService.MovementV4Digest(node.NormalizedResult)==ItemTransferService.MovementV4Digest(Projection(state,node,body));
        }
    }
}
