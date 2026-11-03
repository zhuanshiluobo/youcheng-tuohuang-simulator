using System;
using System.Globalization;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Cards
{
    public static partial class CharacterCoverEffectExecutor
    {
        public const string SharedCoverDefinitionVersion="4.0.0";
        public static string ValidateSavedVersion(string type,NormalizedValue args,string version,bool runtimeNode=false)
        {
            if(type!=EffectTypeId)return "";
            var api=Value(args,"apiVersion");if(api==null)return version==SharedCoverDefinitionVersion||!runtimeNode&&string.IsNullOrEmpty(version)?"":"character_cover_contract_version_unsupported";
            if(api.Kind==NormalizedValueKind.Integer&&api.IntegerValue==2)return RetiredV2Reason;
            if(api.Kind!=NormalizedValueKind.Integer||api.IntegerValue!=3)return "character_cover_contract_version_unsupported";
            return version==SharedCoverDefinitionVersion||!runtimeNode&&string.IsNullOrEmpty(version)?"":"character_cover_contract_version_unsupported";
        }
        private static EffectSpec AdditionalStep(EffectNodeRuntimeState node,string sourceZone,string sourceSlot)
        {
            var args=node.NormalizedArguments;var from=Object("zoneId",NormalizedValue.CreateString(sourceZone));if(sourceSlot.Length>0){from.Properties.Add(new NormalizedValueEntry{Name="slotId",Value=NormalizedValue.CreateString(sourceSlot)});from.Properties.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Name,b.Name));}
            return EffectSpec.Create(TypeId,Object("apiVersion",NormalizedValue.CreateInteger(4),"player",NormalizedValue.CreateString(node.PlayerId.ToString(CultureInfo.InvariantCulture)),"operation",NormalizedValue.CreateString("cover_character"),
                "selector",Object("mode",NormalizedValue.CreateString("explicit"),"itemIds",Strings(new[]{Text(args,"cardInstanceId")}),"expectedSource",from),
                "cover",Object("mode",NormalizedValue.CreateString("additional"),"acceptedTypeId",Value(args,"acceptedTypeId").Clone(),"reusable",Value(args,"reusable").Clone(),"blockedActionRoundSequence",Value(args,"blockedActionRoundSequence").Clone())),node.PlayerId);
        }
        private static NormalizedValue AdditionalProjection(EffectNodeRuntimeState child)=>Object("coveredItemId",NormalizedValue.CreateString(child.CharacterCoverMovementV4.Item.ItemId),"coverSlotId",NormalizedValue.CreateString(child.CharacterCoverMovementV4.SlotAfter.SlotId));
        private static EffectStepResult ExecuteSharedAdditional(EffectExecutionContext context)
        {
            var node=context.Node;
            if(node.FlowStage=="")
            {
                var item=context.State.ItemZones.Items.SingleOrDefault(i=>i.ItemId==Text(node.NormalizedArguments,"cardInstanceId"));if(item==null)return EffectStepResult.Failed("character_type_rejected");
                string source;try{source=ContentTransactionEffectExecutor.BoundSource(context);}catch(InvalidOperationException){return EffectStepResult.Failed("content_transaction_source_binding_required");}
                node.SourceId="character-card:"+source;Set(node,"sharedCoverSourceZone",NormalizedValue.CreateString(item.ZoneId));Set(node,"sharedCoverSourceSlot",NormalizedValue.CreateString(item.SlotId));
                var child=AdditionalStep(node,item.ZoneId,item.SlotId);child.StableKey=node.EffectId+":cover-v4";return EffectStepResult.Continue("additional_common").AddChild(child);
            }
            if(node.FlowStage!="additional_common"||!TryValidateSharedWrapper(context.State,node))throw new InvalidOperationException("character_cover_wrapper_state_invalid");
            var completed=context.ChildNodes.Single(n=>n.EffectTypeId==TypeId);
            if(completed.Status==EffectNodeStatus.Failed)return EffectStepResult.Failed(completed.FailureReason).WithFlowStage("additional_finished");
            if(completed.Status!=EffectNodeStatus.Completed)return EffectStepResult.NoProgress("等待共同附加盖放完成。");
            return EffectStepResult.Completed(AdditionalProjection(completed)).WithFlowStage("additional_finished");
        }
        public static bool TryValidateSharedWrapper(GameState state,EffectNodeRuntimeState node)
        {
            if(node.EffectTypeId==EffectTypeId&&Value(node.NormalizedArguments,"apiVersion")==null)return MainCoverAuthority.TryGetTask(state,node,false,out _)&&TryValidateMainWrapper(state,node);
            if(node.EffectTypeId!=EffectTypeId||Value(node.NormalizedArguments,"apiVersion")?.IntegerValue!=3)return true;
            if(ValidateSavedVersion(node.EffectTypeId,node.NormalizedArguments,node.DefinitionVersion,true).Length>0||ValidateArguments(node.NormalizedArguments).Length>0||node.PositionChanges.Count!=0)return false;
            var automatic=node.ChildEffectIds.Where(id=>!state.EffectRuntime.DispatchReceipts.Any(d=>d.Succeeded&&d.AttachedEffectIds.Contains(id))).ToList();
            if(node.FlowStage=="")return automatic.Count==0&&node.Status!=EffectNodeStatus.Completed;
            if(!new[]{"additional_common","additional_finished"}.Contains(node.FlowStage)||automatic.Count!=1||Value(node.ContinuationData,"sharedCoverSourceZone")?.Kind!=NormalizedValueKind.String||Value(node.ContinuationData,"sharedCoverSourceSlot")?.Kind!=NormalizedValueKind.String)return false;
            var child=state.EffectRuntime.EffectNodes.SingleOrDefault(n=>n.EffectId==automatic[0]);
            if(child==null||child.ParentEffectId!=node.EffectId||child.EffectTypeId!=TypeId||child.PlayerId!=node.PlayerId||
                child.EffectId!=StableIdFactory.Create("effect",node.EffectId,"child","child_effect",node.ChildEffectIds.IndexOf(child.EffectId).ToString(CultureInfo.InvariantCulture),TypeId,node.EffectId+":cover-v4")||
                ItemTransferService.MovementV4Digest(child.NormalizedArguments)!=ItemTransferService.MovementV4Digest(AdditionalStep(node,Text(node.ContinuationData,"sharedCoverSourceZone"),Text(node.ContinuationData,"sharedCoverSourceSlot")).NormalizedArguments))return false;
            if(node.FlowStage!="additional_finished")return true;
            if(node.Status==EffectNodeStatus.Failed||node.PendingOutcome==EffectPendingOutcome.Failed)return child.Status==EffectNodeStatus.Failed&&node.FailureReason==child.FailureReason;
            return child.Status==EffectNodeStatus.Completed&&child.CharacterCoverMovementV4?.Receipt!=null&&ItemTransferService.MovementV4Digest(node.NormalizedResult)==ItemTransferService.MovementV4Digest(AdditionalProjection(child));
        }
    }
}
