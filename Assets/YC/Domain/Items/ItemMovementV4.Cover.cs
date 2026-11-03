using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Interactions;
using YC.Domain.Presentation;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Items
{
    [Serializable] public sealed class CharacterCoverMovementPlanV4
    {
        public string ContractVersion = ItemMovementV4.ContractVersion, OwnerEffectId = "", ArgumentsDigest = "", SourceItemId = "", MainTaskEffectId="";
        public int ExecutingPlayerId, BoundSequence;
        public MarkerMoveItemFactV4 Item = new MarkerMoveItemFactV4();
        public List<CoverSlotState> SlotsBefore = new List<CoverSlotState>();
        public CoverSlotState SlotAfter = new CoverSlotState();
        public TransferReceiptV4 Receipt;
    }
    public static partial class ItemMovementV4
    {
        internal static bool CoverOperation(NormalizedValue args) => Text(args, "operation") == "cover_character";
        private static string ValidateCoverArguments(NormalizedValue args)
        {
            if (!Fields(args, "kind", "effectTypeId", "apiVersion", "player", "operation", "selector", "cover")) return "item_move_v4_field_rejected";
            var cover=Value(args,"cover");var selector=Value(args,"selector");
            if (!Fields(cover,"mode","acceptedTypeId","reusable","blockedActionRoundSequence") || !new[]{"main","additional"}.Contains(Text(cover,"mode")) ||
                !new[]{"core.character_card","core.generated_character_card"}.Contains(Text(cover,"acceptedTypeId")) || Value(cover,"reusable")?.Kind!=NormalizedValueKind.Boolean || !Count(Value(cover,"blockedActionRoundSequence"),0,int.MaxValue)) return "character_cover_arguments_invalid";
            if(Text(cover,"mode")=="main"&&(Text(cover,"acceptedTypeId")!="core.character_card"||Flag(cover,"reusable")||Number(cover,"blockedActionRoundSequence")!=0))return "character_cover_arguments_invalid";
            if(Flag(cover,"reusable") && Number(cover,"blockedActionRoundSequence")!=0) return "character_cover_block_sequence_invalid";
            string mode=Text(selector,"mode");
            if(mode=="explicit")
            {
                var ids=Value(selector,"itemIds");var source=Value(selector,"expectedSource");
                if(!Fields(selector,"mode","itemIds","expectedSource") || ids?.Kind!=NormalizedValueKind.Array || ids.Items.Count!=1 || !String(ids.Items[0]))return "item_move_v4_items_invalid";
                if(!Fields(source,"zoneId","slotId") || !String(Value(source,"zoneId")) || Value(source,"slotId")!=null&&!String(Value(source,"slotId")))return "invalid_zone_ref";
            }
            else if(mode=="select") return "item_move_selection_not_supported";
            else return "item_move_v4_selector_invalid";
            return "";
        }
        private static string CoverSource(EffectExecutionContext context)
        {
            try{return ContentTransactionEffectExecutor.BoundSource(context);}
            catch(InvalidOperationException){return "";}
        }
        internal static bool CoverItemAllowed(GameState state,int player,ItemInstanceState item,NormalizedValue cover)
        {
            var types=ItemTypeRegistry.CreateBuiltIn();var source=item==null?null:state.ItemZones.Zones.SingleOrDefault(z=>z.ZoneId==item.ZoneId);
            return item!=null&&types.IsAssignableTo(item.TypeId,Text(cover,"acceptedTypeId"))&&types.HasOperationProfile(item.TypeId,"core.character_card_use")&&
                item.ControllerSubject.Kind==ItemSubjectKind.Player&&item.ControllerSubject.PlayerId==player&&source?.OwnerPlayerId==player&&
                (source.LocalZoneId=="hand"||Text(cover,"mode")!="main"&&!Flag(cover,"reusable")&&source.LocalZoneId=="discard");
        }
        private static EffectStepResult ExecuteCover(EffectExecutionContext context)
        {
            var node=context.Node;node.Visibility=GameStateVisibilityPolicy.Owner;CharacterLifecycleService.ResumeDeferredDependencies(context);
            bool main=MainCoverOperation(node.NormalizedArguments);
            if(main&&(!MainCoverAuthority.TryGetTask(context.State,node,true,out var approvedTask)||approvedTask.MainCoverFlowV1==null||approvedTask.MainCoverFlowV1.SelectedItemId!=Value(Value(node.NormalizedArguments,"selector"),"itemIds").Items[0].StringValue))return EffectStepResult.Failed("normal_cover_outside_mainline");
            var cover=Value(node.NormalizedArguments,"cover");var selector=Value(node.NormalizedArguments,"selector");
            string source=main?node.ParentEffectId:CoverSource(context);if(source.Length==0)return EffectStepResult.Failed("content_transaction_source_binding_required");
            node.SourceId=(main?"main-cover:":"character-card:")+source;
            if(node.CharacterCoverMovementV4==null)
            {
                string card=Value(selector,"itemIds").Items[0].StringValue;
                var item=context.State.ItemZones.Items.SingleOrDefault(i=>i.ItemId==card);
                if(item==null)return EffectStepResult.Failed("character_type_rejected");
                if(Text(selector,"mode")=="explicit"&&(item.ZoneId!=Text(Value(selector,"expectedSource"),"zoneId")||Value(Value(selector,"expectedSource"),"slotId")!=null&&item.SlotId!=Text(Value(selector,"expectedSource"),"slotId")))return EffectStepResult.Failed("source_zone_changed");
                if(!CoverItemAllowed(context.State,node.PlayerId,item,cover))return EffectStepResult.Failed("character_cover_source_invalid");
                int blocked=Number(cover,"blockedActionRoundSequence");if(blocked!=(main||Flag(cover,"reusable")?0:CharacterLifecycleService.GetActionRoundSequence(context.State)))return EffectStepResult.Failed("character_cover_block_sequence_invalid");
                var preview=GameStateCloneService.DeepClone(context.State);var work=new EffectExecutionContext(preview,preview.EffectRuntime.EffectNodes.Single(n=>n.EffectId==node.EffectId),context.Registry);
                var result=CharacterLifecycleService.Cover(work,card,!main,blocked,Text(cover,"acceptedTypeId"),Flag(cover,"reusable"));
                if(result.PendingOutcome!=EffectPendingOutcome.Completed)return result;
                var after=preview.ItemZones.Items.Single(i=>i.ItemId==card);var slot=preview.CharacterCoverSlots.Single(s=>s.CardInstanceId==card);
                node.CharacterCoverMovementV4=new CharacterCoverMovementPlanV4{OwnerEffectId=node.EffectId,ExecutingPlayerId=node.PlayerId,ArgumentsDigest=ItemTransferService.MovementV4Digest(node.NormalizedArguments),SourceItemId=main?"":source,MainTaskEffectId=main?source:"",BoundSequence=blocked,
                    Item=new MarkerMoveItemFactV4{ItemId=card,TypeId=item.TypeId,OriginalSupplyZoneId=item.OriginalSupplyZoneId,Owner=GameStateCloneService.DeepClone(item.OwnerSubject),Controller=GameStateCloneService.DeepClone(item.ControllerSubject),ControllerAfter=GameStateCloneService.DeepClone(after.ControllerSubject),From=new ItemPositionRef{ZoneId=item.ZoneId,SlotId=item.SlotId},To=new ItemPositionRef{ZoneId=after.ZoneId,SlotId=after.SlotId}},
                    SlotsBefore=GameStateCloneService.DeepClone(context.State.CharacterCoverSlots.Where(s=>s.SlotId==slot.SlotId).ToList()),SlotAfter=GameStateCloneService.DeepClone(slot)};
                return EffectStepResult.Continue("cover_bound");
            }
            if(!ValidateCoverState(context.State,node,out string invalid))throw new InvalidOperationException(invalid);
            var plan=node.CharacterCoverMovementV4;
            if(plan.Receipt==null)
            {
                if(CharacterLifecycleService.TryAwaitCardMoves(context,new[]{plan.Item.ItemId},plan.Item.To.ZoneId))return CharacterLifecycleService.DeferredDependencyWait();
                var current=context.State.ItemZones.Items.Single(i=>i.ItemId==plan.Item.ItemId);
                if(current.ZoneId!=plan.Item.From.ZoneId||current.SlotId!=plan.Item.From.SlotId||ItemTransferService.MovementV4Digest(current.ControllerSubject)!=ItemTransferService.MovementV4Digest(plan.Item.Controller))return EffectStepResult.Failed("source_zone_changed");
                if(plan.BoundSequence!=(main||Flag(cover,"reusable")?0:CharacterLifecycleService.GetActionRoundSequence(context.State)))return EffectStepResult.Failed("character_cover_block_sequence_invalid");
                var preview=GameStateCloneService.DeepClone(context.State);var work=new EffectExecutionContext(preview,preview.EffectRuntime.EffectNodes.Single(n=>n.EffectId==node.EffectId),context.Registry);
                var result=CharacterLifecycleService.Cover(work,plan.Item.ItemId,!main,plan.BoundSequence,Text(cover,"acceptedTypeId"),Flag(cover,"reusable"));
                if(result.PendingOutcome!=EffectPendingOutcome.Completed)return result;
                var actual=preview.CharacterCoverSlots.Single(s=>s.CardInstanceId==plan.Item.ItemId);
                if(ItemTransferService.MovementV4Digest(actual)!=ItemTransferService.MovementV4Digest(plan.SlotAfter)||!SameCoverSlotBefore(context.State,plan))return EffectStepResult.Failed("character_cover_slot_changed");
                var change=work.Node.PositionChanges.Single();change.To=GameStateCloneService.DeepClone(plan.Item.To);
                var receipt=new TransferReceiptV4{OwnerEffectId=node.EffectId,ExecutingPlayerId=node.PlayerId,ArgumentsDigest=plan.ArgumentsDigest,OperationId=StableIdFactory.Create("item-move-v4",node.EffectId),Items=new List<MarkerMoveItemFactV4>{GameStateCloneService.DeepClone(plan.Item)},
                    OrdersBefore=new[]{plan.Item.From.ZoneId,plan.Item.To.ZoneId}.Select(id=>ItemTransferService.CaptureOrderV4(context.State,id)).ToList(),OrdersAfter=new[]{plan.Item.From.ZoneId,plan.Item.To.ZoneId}.Select(id=>ItemTransferService.CaptureOrderV4(preview,id)).ToList(),
                    BindingsBefore=ItemTransferService.CaptureBindingsV4(context.State),BindingsAfter=ItemTransferService.CaptureBindingsV4(preview),Result=new ItemTransferResult{Changed=true,MovedCount=1,MovedItemIds=new List<string>{plan.Item.ItemId},PositionChanges=GameStateCloneService.DeepClone(work.Node.PositionChanges)}};
                EffectEventRequest coveredEvent=null;
                if(main)
                {
                    if(!MainCoverAuthority.TryGetTask(preview,work.Node,true,out var task))return EffectStepResult.Failed("normal_cover_outside_mainline");
                    CharacterCoverEffectExecutor.MarkCompletedPlayer(new EffectExecutionContext(preview,task,context.Registry),node.PlayerId);
                    coveredEvent=CharacterCoverEffectExecutor.CreateMainCoveredEvent(preview,work.Node,plan.Item.ItemId);task.MainCoverFlowV1.AllPlayersCoveredAtCommit=Flag(coveredEvent.Payload,"allPlayersCovered");receipt.EventIds.Add(coveredEvent.EventId);
                }
                work.Node.CharacterCoverMovementV4.Receipt=receipt;receipt.ReceiptId=CoverReceiptId(work.Node.CharacterCoverMovementV4);GameStateCloneService.CopyTo(context.State,preview);var committed=EffectStepResult.Continue("cover_committed");if(coveredEvent!=null)committed.AddEvent(coveredEvent);return committed;
            }
            return EffectStepResult.Completed(CoverResult(plan)).WithFlowStage("cover_finished");
        }
        private static bool SameCoverSlotBefore(GameState state,CharacterCoverMovementPlanV4 plan)=>ItemTransferService.MovementV4Digest(state.CharacterCoverSlots.Where(s=>s.SlotId==plan.SlotAfter.SlotId).ToList())==ItemTransferService.MovementV4Digest(plan.SlotsBefore);
        internal static NormalizedValue CoverResult(CharacterCoverMovementPlanV4 plan)
        {
            var result=Result(plan.Receipt);result.Properties.Add(new NormalizedValueEntry{Name="coveredItemId",Value=NormalizedValue.CreateString(plan.Item.ItemId)});result.Properties.Add(new NormalizedValueEntry{Name="coverSlotId",Value=NormalizedValue.CreateString(plan.SlotAfter.SlotId)});result.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));return result;
        }
        private static string CoverReceiptId(CharacterCoverMovementPlanV4 plan)=>StableIdFactory.Create("transfer-receipt-v4",plan.OwnerEffectId,ItemTransferService.MovementV4Digest(new{plan.SourceItemId,plan.MainTaskEffectId,plan.BoundSequence,plan.SlotsBefore,plan.SlotAfter,receipt=ItemTransferService.ReceiptDigestV4(plan.Receipt)}));
    }
}
