using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Items
{
    public static partial class ItemMovementV4
    {
        internal static bool ValidateCoverState(GameState state,EffectNodeRuntimeState node,out string reason)
        {
            reason="item_move_v4_cover_state_invalid";
            if(node.ItemMovementV4!=null||node.CommonItemMovementV4!=null||ValidateArguments(node.NormalizedArguments).Length>0||Text(node.NormalizedArguments,"player")!=node.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture))return false;
            bool main=MainCoverOperation(node.NormalizedArguments);if(node.MainCoverFlowV1!=null||main&&(!MainCoverAuthority.TryGetTask(state,node,false,out var task)||task.MainCoverFlowV1==null||task.MainCoverFlowV1.SelectedItemId!=Value(Value(node.NormalizedArguments,"selector"),"itemIds").Items[0].StringValue))return false;
            var plan=node.CharacterCoverMovementV4;
            if(plan==null)
            {
                return node.Status!=EffectNodeStatus.Completed&&node.PositionChanges.Count==0&&node.FlowStage=="";
            }
            var cover=Value(node.NormalizedArguments,"cover");var selector=Value(node.NormalizedArguments,"selector");var fact=plan.Item;
            if(plan.ContractVersion!=ContractVersion||plan.OwnerEffectId!=node.EffectId||plan.ExecutingPlayerId!=node.PlayerId||plan.ArgumentsDigest!=ItemTransferService.MovementV4Digest(node.NormalizedArguments)||
                (main?plan.SourceItemId!=""||plan.MainTaskEffectId!=node.ParentEffectId||node.SourceId!="main-cover:"+plan.MainTaskEffectId:plan.MainTaskEffectId!=""||plan.SourceItemId.Length==0||CoverSource(new EffectExecutionContext(state,node,null))!=plan.SourceItemId||node.SourceId!="character-card:"+plan.SourceItemId)||fact==null||fact.Owner==null||fact.Controller==null||fact.ControllerAfter==null||fact.From==null||fact.To==null||plan.SlotsBefore==null||plan.SlotsBefore.Count>1||plan.SlotAfter==null||
                !new[]{"cover_bound","cover_committed","cover_finished"}.Contains(CharacterLifecycleService.GetResumableFlowStage(node)))return false;
            var item=state.ItemZones.Items.SingleOrDefault(i=>i.ItemId==fact.ItemId);var sourceZone=state.ItemZones.Zones.SingleOrDefault(z=>z.ZoneId==fact.From.ZoneId);var target=state.ItemZones.Zones.SingleOrDefault(z=>z.ZoneId==fact.To.ZoneId);
            var historical=item==null?null:GameStateCloneService.DeepClone(item);if(historical!=null){historical.ZoneId=fact.From.ZoneId;historical.SlotId=fact.From.SlotId;historical.ControllerSubject=GameStateCloneService.DeepClone(fact.Controller);}
            if(item==null||item.TypeId!=fact.TypeId||item.OriginalSupplyZoneId!=fact.OriginalSupplyZoneId||ItemTransferService.MovementV4Digest(item.OwnerSubject)!=ItemTransferService.MovementV4Digest(fact.Owner)||
                sourceZone==null||target?.TemplateId!="core.zone.character_covered"||target.OwnerPlayerId!=node.PlayerId||!CoverItemAllowed(state,node.PlayerId,historical,cover)||fact.ControllerAfter.Kind!=ItemSubjectKind.Player||fact.ControllerAfter.PlayerId!=node.PlayerId)return false;
            if(Text(selector,"mode")=="explicit")
            {
                if(Value(selector,"itemIds").Items[0].StringValue!=fact.ItemId||Text(Value(selector,"expectedSource"),"zoneId")!=fact.From.ZoneId||Value(Value(selector,"expectedSource"),"slotId")!=null&&Text(Value(selector,"expectedSource"),"slotId")!=fact.From.SlotId)return false;
            }
            var slot=plan.SlotAfter;
            if(slot.PlayerId!=node.PlayerId||slot.SourceEffectId!=node.SourceId||slot.CardInstanceId!=fact.ItemId||slot.AcceptedTypeId!=Text(cover,"acceptedTypeId")||slot.Reusable!=Flag(cover,"reusable")||slot.BlockedActionRoundSequence!=Number(cover,"blockedActionRoundSequence")||
                plan.BoundSequence!=slot.BlockedActionRoundSequence||fact.To.SlotId!=slot.SlotId||slot.AllowedActionRounds==null)return false;
            if(plan.SlotsBefore.Count==0)
            {
                if(slot.SlotId!=StableIdFactory.Create("cover_slot",node.EffectId,main?"primary":"additional")||!slot.AllowedActionRounds.SequenceEqual(new[]{1,2}))return false;
            }
            else
            {
                var before=plan.SlotsBefore[0];if(before==null||before.CardInstanceId!=""||!before.Reusable||!slot.Reusable)return false;
                var expected=GameStateCloneService.DeepClone(before);expected.CardInstanceId=fact.ItemId;expected.BlockedActionRoundSequence=plan.BoundSequence;
                if(ItemTransferService.MovementV4Digest(expected)!=ItemTransferService.MovementV4Digest(slot))return false;
            }
            var r=plan.Receipt;
            if(r==null)return node.Status!=EffectNodeStatus.Completed&&node.FlowStage!="cover_finished"&&node.FlowStage!="cover_committed"&&node.PositionChanges.Count==0;
            reason="item_move_v4_cover_receipt_invalid";
            if(r.ContractVersion!=ContractVersion||r.OwnerEffectId!=node.EffectId||r.ExecutingPlayerId!=node.PlayerId||r.ArgumentsDigest!=plan.ArgumentsDigest||r.Outcome!="completed"||r.OperationId!=StableIdFactory.Create("item-move-v4",node.EffectId)||
                r.Items==null||r.Items.Count!=1||ItemTransferService.MovementV4Digest(r.Items[0])!=ItemTransferService.MovementV4Digest(fact)||r.LifecycleItems==null||r.LifecycleItems.Count!=0||r.CarrierExits==null||r.CarrierExits.Count!=0||r.EventIds==null||(!main&&r.EventIds.Count!=0)||
                r.BindingsBefore==null||r.BindingsAfter==null||ItemTransferService.MovementV4Digest(r.BindingsBefore)!=ItemTransferService.MovementV4Digest(r.BindingsAfter)||r.OrdersBefore==null||r.OrdersAfter==null||r.OrdersBefore.Count!=2||r.OrdersAfter.Count!=2||r.Result==null||r.Result.PositionChanges==null||r.Result.MovedItemIds==null||r.ReceiptId!=CoverReceiptId(plan))return false;
            if(main&&!ValidateMainCoverEvent(state,node,plan))return false;
            var change=new ItemPositionChange{ItemId=fact.ItemId,From=fact.From,To=fact.To};var changes=new List<ItemPositionChange>{change};
            if(!r.Result.Changed||r.Result.MovedCount!=1||!r.Result.MovedItemIds.SequenceEqual(new[]{fact.ItemId})||ItemTransferService.MovementV4Digest(r.Result.PositionChanges)!=ItemTransferService.MovementV4Digest(changes)||ItemTransferService.MovementV4Digest(node.PositionChanges)!=ItemTransferService.MovementV4Digest(changes))return false;
            for(int index=0;index<2;index++)
            {
                string zone=index==0?fact.From.ZoneId:fact.To.ZoneId;var before=r.OrdersBefore[index];var after=r.OrdersAfter[index];
                if(before==null||after==null||before.ZoneId!=zone||after.ZoneId!=zone||before.HasOrder||after.HasOrder||before.Revision!=0||after.Revision!=0||before.ItemIds==null||after.ItemIds==null||before.ItemIds.Distinct().Count()!=before.ItemIds.Count||after.ItemIds.Distinct().Count()!=after.ItemIds.Count||before.ItemIds.Concat(after.ItemIds).Any(id=>!state.ItemZones.Items.Any(i=>i.ItemId==id)))return false;
                var expected=before.ItemIds.Where(id=>id!=fact.ItemId).ToList();if(index==1)expected.Add(fact.ItemId);
                if(index==0&&!before.ItemIds.Contains(fact.ItemId)||index==1&&before.ItemIds.Contains(fact.ItemId)||!expected.OrderBy(id=>id,StringComparer.Ordinal).SequenceEqual(after.ItemIds.OrderBy(id=>id,StringComparer.Ordinal)))return false;
            }
            var currentSlot=state.CharacterCoverSlots.SingleOrDefault(s=>s.SlotId==slot.SlotId);
            if(currentSlot==null||currentSlot.PlayerId!=slot.PlayerId||currentSlot.SourceEffectId!=slot.SourceEffectId||currentSlot.AcceptedTypeId!=slot.AcceptedTypeId||currentSlot.Reusable!=slot.Reusable||currentSlot.BlockedActionRoundSequence!=slot.BlockedActionRoundSequence||!currentSlot.AllowedActionRounds.SequenceEqual(slot.AllowedActionRounds))return false;
            if((node.Status==EffectNodeStatus.Completed||node.PendingOutcome==EffectPendingOutcome.Completed)&&ItemTransferService.MovementV4Digest(node.NormalizedResult)!=ItemTransferService.MovementV4Digest(CoverResult(plan)))return false;
            reason="";return true;
        }
    }
}
