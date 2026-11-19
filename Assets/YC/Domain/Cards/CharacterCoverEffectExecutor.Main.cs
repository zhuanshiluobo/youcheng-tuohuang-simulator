using System;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Cards
{
    public static partial class CharacterCoverEffectExecutor
    {
        public const string PublicNormalCoverReason="normal_cover_host_task_only";
        private static EffectSpec MainStep(GameState state,EffectNodeRuntimeState task)
        {
            var flow=task.MainCoverFlowV1;
            var spec=ItemMovementV4.MainCoverSpec(state,task.PlayerId,flow.SelectedItemId,flow.SelectedSourceSlot);spec.StableKey=task.EffectId+":main-v4";return spec;
        }
        private static EffectStepResult ExecuteSharedMain(EffectExecutionContext c)
        {
            if(!MainCoverAuthority.TryGetTask(c.State,c.Node,true,out _))return EffectStepResult.Failed("normal_cover_outside_mainline");
            if(c.Node.FlowStage.Length==0)
            {
                var flow=c.Node.MainCoverFlowV1=new MainCoverFlowStateV1{
                    ContractVersion="main_cover.business.v2",OwnerEffectId=c.Node.EffectId,TaskEffectId=c.Node.EffectId,
                    MainNodeId=Text(c.Node.NormalizedArguments,"mainNodeId"),ArgumentsDigest=ItemTransferService.MovementV4Digest(c.Node.NormalizedArguments),
                    HandZoneId=FindZone(c.State,"hand",c.Node.PlayerId).ZoneId,DiscardZoneId=FindZone(c.State,"discard",c.Node.PlayerId).ZoneId,
                    CoveredZoneId=FindZone(c.State,"covered",c.Node.PlayerId).ZoneId};
                flow.ExistingItemId=c.State.ItemZones.Items.Where(i=>i.ZoneId==flow.CoveredZoneId).OrderBy(i=>i.ItemId,StringComparer.Ordinal).Select(i=>i.ItemId).FirstOrDefault()??"";
                if(flow.ExistingItemId.Length>0)
                {
                    flow.NoChangeReceipt=new TransferReceiptV4{OwnerEffectId=c.Node.EffectId,ExecutingPlayerId=c.Node.PlayerId,ArgumentsDigest=flow.ArgumentsDigest,
                        OperationId=StableIdFactory.Create("main-cover-no-change",c.Node.EffectId),
                        OrdersBefore=new List<MarkerMoveOrderFactV4>{ItemTransferService.CaptureOrderV4(c.State,flow.CoveredZoneId)},
                        OrdersAfter=new List<MarkerMoveOrderFactV4>{ItemTransferService.CaptureOrderV4(c.State,flow.CoveredZoneId)},
                        BindingsBefore=ItemTransferService.CaptureBindingsV4(c.State),BindingsAfter=ItemTransferService.CaptureBindingsV4(c.State),Result=new ItemTransferResult()};
                    flow.NoChangeReceipt.ReceiptId=MainNoChangeReceiptId(flow);MarkCompletedPlayer(c,c.Node.PlayerId);
                    return EffectStepResult.Completed(CreateResult(c.Node.PlayerId,"",false)).WithFlowStage("main_unchanged");
                }
                if(MainCandidates(c.State,c.Node).Count==0)
                {
                    flow.RecycledItemIds=c.State.ItemZones.Items.Where(i=>i.ZoneId==flow.DiscardZoneId).Select(i=>i.ItemId).OrderBy(id=>id,StringComparer.Ordinal).ToList();
                    var steps=MainRecycleSteps(c.Node);if(steps.Count>0)return EffectStepResult.Continue("main_recycle").AddChildren(steps);
                }
                return ChooseMainCard(c);
            }
            if(!TryValidateMainWrapper(c.State,c.Node))throw new InvalidOperationException("main_cover_wrapper_state_invalid");
            if(c.Node.FlowStage=="main_recycle")
            {
                var children=MainOwnChildren(c.State,c.Node);var failed=children.FirstOrDefault(n=>n.Status==EffectNodeStatus.Failed);
                if(failed!=null)return EffectStepResult.Failed(failed.FailureReason).WithFlowStage("main_recycle_failed");
                if(children.Any(n=>n.Status!=EffectNodeStatus.Completed))return EffectStepResult.NoProgress("等待正常盖放回收完成。");
                return ChooseMainCard(c);
            }
            if(c.Node.FlowStage=="awaiting_card")
            {
                BusinessItemChoice.TryGet(c.State,c.Node,"character_cover.main",out var request,InteractionTypeId);
                if(request.Status!="answered")return EffectStepResult.NoProgress("等待主盖放选择。");
                string id=BusinessItemChoice.Answer(request);
                if(!MainCandidates(c.State,c.Node).Contains(id))return EffectStepResult.Failed("source_zone_changed").WithFlowStage("main_choice_failed");
                var item=c.State.ItemZones.Items.Single(i=>i.ItemId==id);
                c.Node.MainCoverFlowV1.SelectedItemId=id;c.Node.MainCoverFlowV1.SelectedSourceSlot=item.SlotId;
                return EffectStepResult.Continue("main_common").AddChild(MainStep(c.State,c.Node));
            }
            var body=MainCoverAuthority.Body(c.State,c.Node);
            if(body.Status==EffectNodeStatus.Failed)return EffectStepResult.Failed(body.FailureReason).WithFlowStage("main_finished");
            if(body.Status!=EffectNodeStatus.Completed)return EffectStepResult.NoProgress("等待正常盖放共同步骤及必要响应完成。");
            return EffectStepResult.Completed(MainProjection(body)).WithFlowStage("main_finished");
        }
        private static NormalizedValue MainProjection(EffectNodeRuntimeState body)=>body.CharacterCoverMovementV4?.Receipt?.Result.Changed==true?Object("changed",NormalizedValue.CreateBoolean(true)):CreateResult(body.PlayerId,"",false);
        private static bool TryValidateMainWrapper(GameState state,EffectNodeRuntimeState node)
        {
            if(node.PositionChanges.Count!=0)return false;
            var own=MainOwnChildren(state,node);var f=node.MainCoverFlowV1;
            if(node.FlowStage.Length==0)return f==null&&own.Count==0&&node.Status!=EffectNodeStatus.Completed;
            if(f==null||f.ContractVersion!="main_cover.business.v2"||f.OwnerEffectId!=node.EffectId||f.TaskEffectId!=node.EffectId||f.MainNodeId!=Text(node.NormalizedArguments,"mainNodeId")||
                f.ArgumentsDigest!=ItemTransferService.MovementV4Digest(node.NormalizedArguments)||f.HandZoneId!=FindZone(state,"hand",node.PlayerId).ZoneId||
                f.DiscardZoneId!=FindZone(state,"discard",node.PlayerId).ZoneId||f.CoveredZoneId!=FindZone(state,"covered",node.PlayerId).ZoneId||f.RecycledItemIds==null||
                f.RecycledItemIds.Distinct().Count()!=f.RecycledItemIds.Count||f.RecycledItemIds.Any(id=>!state.ItemZones.Items.Any(i=>i.ItemId==id&&ItemTypeRegistry.CreateBuiltIn().IsAssignableTo(i.TypeId,"core.character_card"))))return false;
            if(f.ExistingItemId.Length>0)return ValidateMainNoChange(state,node,own.Count);
            if(f.NoChangeReceipt!=null||f.SelectedItemId==""&&f.SelectedSourceSlot!="")return false;
            var expected=MainRecycleSteps(node);
            bool hasBody=new[]{"main_common","main_finished"}.Contains(node.FlowStage);
            if(hasBody)expected.Add(MainStep(state,node));
            if(expected.Count!=own.Count)return false;
            for(int i=0;i<expected.Count;i++)
            {
                var child=own[i];var spec=expected[i];int index=node.ChildEffectIds.IndexOf(child.EffectId);
                if(child.ParentEffectId!=node.EffectId||child.PlayerId!=node.PlayerId||child.EffectTypeId!=TypeId||
                    child.EffectId!=StableIdFactory.Create("effect",node.EffectId,"child","child_effect",index.ToString(CultureInfo.InvariantCulture),TypeId,spec.StableKey)||
                    ItemTransferService.MovementV4Digest(child.NormalizedArguments)!=ItemTransferService.MovementV4Digest(spec.NormalizedArguments))return false;
            }
            if(node.FlowStage=="main_recycle_failed")return own.Any(n=>n.Status==EffectNodeStatus.Failed&&n.FailureReason==node.FailureReason);
            if(node.FlowStage=="main_recycle")return own.Count>0&&f.SelectedItemId=="";
            if(node.FlowStage=="main_empty")return f.SelectedItemId==""&&own.All(n=>n.Status==EffectNodeStatus.Completed)&&node.FailureReason=="cover_no_character_card"&&state.EffectRuntime.InteractionRequests.All(r=>r.OwnerEffectId!=node.EffectId);
            if(!BusinessItemChoice.TryGet(state,node,"character_cover.main",out var request,InteractionTypeId))return false;
            if(node.FlowStage=="awaiting_card"||node.FlowStage=="main_choice_failed")return f.SelectedItemId==""&&own.All(n=>n.Status==EffectNodeStatus.Completed)&&
                (node.FlowStage!="main_choice_failed"||request.Status=="answered"&&node.FailureReason=="source_zone_changed");
            if(!hasBody||f.SelectedItemId==""||BusinessItemChoice.Answer(request)!=f.SelectedItemId||own.Take(own.Count-1).Any(n=>n.Status!=EffectNodeStatus.Completed))return false;
            var body=MainCoverAuthority.Body(state,node);if(body==null||own.Last()!=body)return false;
            if(node.FlowStage!="main_finished")return true;
            if(node.Status==EffectNodeStatus.Failed||node.PendingOutcome==EffectPendingOutcome.Failed)return body.Status==EffectNodeStatus.Failed&&node.FailureReason==body.FailureReason;
            return body.Status==EffectNodeStatus.Completed&&ItemTransferService.MovementV4Digest(node.NormalizedResult)==ItemTransferService.MovementV4Digest(MainProjection(body));
        }
        private static List<EffectNodeRuntimeState> MainOwnChildren(GameState state,EffectNodeRuntimeState node)=>node.ChildEffectIds.Where(id=>!state.EffectRuntime.DispatchReceipts.Any(r=>r.Succeeded&&r.AttachedEffectIds.Contains(id))).Select(id=>state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==id)).ToList();
        private static List<string> MainCandidates(GameState state,EffectNodeRuntimeState node)=>state.ItemZones.Items.Where(i=>i.ZoneId==node.MainCoverFlowV1.HandZoneId&&
            ItemMovementV4.CoverItemAllowed(state,node.PlayerId,i,Object("mode",NormalizedValue.CreateString("main"),"acceptedTypeId",NormalizedValue.CreateString("core.character_card"),"reusable",NormalizedValue.CreateBoolean(false))))
            .Select(i=>i.ItemId).OrderBy(id=>id,StringComparer.Ordinal).ToList();
        private static EffectStepResult ChooseMainCard(EffectExecutionContext c)
        {
            var ids=MainCandidates(c.State,c.Node);if(ids.Count==0)return EffectStepResult.Failed("cover_no_character_card").WithFlowStage("main_empty");
            return EffectStepResult.Continue("awaiting_card").AddInteraction(BusinessItemChoice.Create(c,ids,"character_cover.main","core:interaction.character.cover.prompt",InteractionTypeId));
        }
        private static List<EffectSpec> MainRecycleSteps(EffectNodeRuntimeState node)
        {
            var flow=node.MainCoverFlowV1;var result=new List<EffectSpec>();
            for(int i=0;i<flow.RecycledItemIds.Count;i+=32)
            {
                var ids=flow.RecycledItemIds.Skip(i).Take(32).ToList();var spec=ItemMovementV4.TransferSpec(node.PlayerId,ids,flow.DiscardZoneId,flow.HandZoneId,required:ids.Count,profile:"core.character_card_transfer");
                spec.StableKey=node.EffectId+":main-recycle:"+result.Count.ToString(CultureInfo.InvariantCulture);result.Add(spec);
            }
            return result;
        }
        private static string MainNoChangeReceiptId(MainCoverFlowStateV1 f)=>StableIdFactory.Create("main-cover-no-change",f.OwnerEffectId,f.TaskEffectId,f.ExistingItemId,ItemTransferService.ReceiptDigestV4(f.NoChangeReceipt));
        private static bool ValidateMainNoChange(GameState state,EffectNodeRuntimeState node,int children)
        {
            var f=node.MainCoverFlowV1;var r=f.NoChangeReceipt;
            return children==0&&f.RecycledItemIds.Count==0&&f.SelectedItemId==""&&f.SelectedSourceSlot==""&&node.FlowStage=="main_unchanged"&&
                state.EffectRuntime.InteractionRequests.All(q=>q.OwnerEffectId!=node.EffectId)&&r!=null&&r.ContractVersion==ItemMovementV4.ContractVersion&&
                r.OwnerEffectId==node.EffectId&&r.ExecutingPlayerId==node.PlayerId&&r.ArgumentsDigest==f.ArgumentsDigest&&r.Outcome=="completed"&&
                r.OperationId==StableIdFactory.Create("main-cover-no-change",node.EffectId)&&r.ReceiptId==MainNoChangeReceiptId(f)&&
                r.Items?.Count==0&&r.LifecycleItems?.Count==0&&r.CarrierExits?.Count==0&&r.EventIds?.Count==0&&r.Result!=null&&!r.Result.Changed&&r.Result.MovedCount==0&&r.Result.MovedItemIds?.Count==0&&r.Result.PositionChanges?.Count==0&&
                r.OrdersBefore?.Count==1&&r.OrdersAfter?.Count==1&&r.OrdersBefore[0].ZoneId==f.CoveredZoneId&&r.OrdersBefore[0].ItemIds.Contains(f.ExistingItemId)&&
                ItemTransferService.MovementV4Digest(r.OrdersBefore)==ItemTransferService.MovementV4Digest(r.OrdersAfter)&&state.ItemZones.Items.Any(i=>i.ItemId==f.ExistingItemId)&&
                r.BindingsBefore!=null&&r.BindingsAfter!=null&&ItemTransferService.MovementV4Digest(r.BindingsBefore)==ItemTransferService.MovementV4Digest(r.BindingsAfter)&&
                ItemTransferService.MovementV4Digest(node.NormalizedResult)==ItemTransferService.MovementV4Digest(CreateResult(node.PlayerId,"",false));
        }
        internal static EffectEventRequest CreateMainCoveredEvent(GameState state,EffectNodeRuntimeState owner,string card)
        {
            var task=state.EffectRuntime.EffectNodes.Single(n=>n.EffectId==owner.ParentEffectId);
            int index=ReadInteger(task.NormalizedArguments,"coverIndex",0);
            return new EffectEventRequest{
                EventId=StableIdFactory.Create("event",owner.EffectId,"CharacterCardCovered",owner.PlayerId.ToString(CultureInfo.InvariantCulture)),EventType="CharacterCardCovered",SourceEffectId=owner.EffectId,OwnerNodeId=owner.EffectId,PlayerId=owner.PlayerId,
                Visibility=GameStateVisibilityPolicy.Public,ResponseKind=RuleEventResponseKind.Effects,SemanticKey=owner.PlayerId.ToString(CultureInfo.InvariantCulture),Payload=CreatePublicPayload(owner.PlayerId,AreAllPlayersCovered(state),index),HostOnlyPayload=CreatePrivatePayload(card,index)};
        }
    }
}
