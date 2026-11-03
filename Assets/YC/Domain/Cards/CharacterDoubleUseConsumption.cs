using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Cards
{
    // API-31 / 4.13.3：移动只由共同步骤执行，消费事实及双用授权仍归角色使用规则。
    public static class CharacterDoubleUseConsumption
    {
        public const string DefinitionVersion = "3.0.0";
        private const string Moving = "moving_source", Consumed = "source_consumed", Failed = "source_move_failed", Already = "source_already_consumed";
        public static string ValidateSavedVersion(string type,string version,bool runtime=false) =>
            type!=CharacterLifecycleService.ConsumeSourceTypeId || version==DefinitionVersion || !runtime&&string.IsNullOrEmpty(version) ? "" : "character_double_use_source_version_unsupported";
        private static CharacterUseRuntimeState Use(GameState state,EffectNodeRuntimeState node) => state.CharacterUses.Find(u=>u.UseId==Text(node.NormalizedArguments,"useId"));
        private static bool SameUse(EffectNodeRuntimeState node,CharacterUseRuntimeState use) => use!=null&&use.ExecutingPlayerId==node.PlayerId&&
            use.DoubleUseSourceItemId==Text(node.NormalizedArguments,"sourceItemId")&&use.DoubleUseSourceItemId!=use.CardInstanceId;
        private static NormalizedValue Arguments(GameState state,EffectNodeRuntimeState node) => Object("apiVersion",NormalizedValue.CreateInteger(4),
            "player",NormalizedValue.CreateString(node.PlayerId.ToString(CultureInfo.InvariantCulture)),"operation",NormalizedValue.CreateString("transfer"),
            "selector",Object("mode",NormalizedValue.CreateString("explicit"),"itemIds",Strings(new[]{Text(node.NormalizedArguments,"sourceItemId")}),
                "expectedSource",Object("zoneId",NormalizedValue.CreateString(FindZone(state,"persistent",node.PlayerId).ZoneId))),
            "destination",Object("zoneId",NormalizedValue.CreateString(FindZone(state,"discard",node.PlayerId).ZoneId)),
            "insertion",Object("kind",NormalizedValue.CreateString("unordered")),"operationProfileId",NormalizedValue.CreateString("core.character_card_transfer"),
            "requiredCount",NormalizedValue.CreateInteger(1));
        private static string Key(EffectNodeRuntimeState node)=>node.EffectId+":consume-source-v4";
        private static EffectNodeRuntimeState Body(GameState state,EffectNodeRuntimeState node) => node.ChildEffectIds.Select((id,index)=>id==StableIdFactory.Create("effect",node.EffectId,"child","child_effect",index.ToString(CultureInfo.InvariantCulture),TypeId,Key(node))
            ?state.EffectRuntime.EffectNodes.SingleOrDefault(n=>n.EffectId==id):null).SingleOrDefault(n=>n!=null);
        public static EffectStepResult Execute(EffectExecutionContext context)
        {
            var node=context.Node;var use=Use(context.State,node);
            if(node.FlowStage.Length==0)
            {
                if(!SameUse(node,use)||use.Finished||use.ModeOrder.Count!=0||use.CompletedModes.Count!=0)return EffectStepResult.Failed("character_double_use_target_invalid");
                if(use.SourceConsumed)return EffectStepResult.Completed().WithFlowStage(Already);
                var source=context.State.ItemZones.Items.Find(i=>i.ItemId==use.DoubleUseSourceItemId);
                if(source==null||source.ControllerSubject.PlayerId!=node.PlayerId||source.ZoneId!=FindZone(context.State,"persistent",node.PlayerId).ZoneId)
                    return EffectStepResult.Failed("character_double_use_source_invalid");
                var step=EffectSpec.Create(TypeId,Arguments(context.State,node),node.PlayerId);step.StableKey=Key(node);
                return EffectStepResult.Continue(Moving).AddChild(step);
            }
            if(!TryValidate(context.State,node)||!SameUse(node,use)||use.Finished||use.ModeOrder.Count!=0||use.CompletedModes.Count!=0)
                throw new InvalidOperationException("character_double_use_source_state_invalid");
            var body=Body(context.State,node);
            if(body.Status==EffectNodeStatus.Failed)return EffectStepResult.Failed(body.FailureReason).WithFlowStage(Failed);
            if(body.Status!=EffectNodeStatus.Completed)return EffectStepResult.NoProgress("等待双用来源的共同移牌及必要响应完成。");
            var receipt=(body.CommonItemMovementV4?.Receipt??body.ItemMovementV4?.Receipt);
            if(receipt==null||!receipt.Result.Changed||receipt.Result.MovedItemIds.Count!=1||receipt.Result.MovedItemIds[0]!=use.DoubleUseSourceItemId)
                throw new InvalidOperationException("character_double_use_source_receipt_invalid");
            use.SourceConsumed=true;
            return EffectStepResult.Completed().WithFlowStage(Consumed);
        }
        public static bool TryValidate(GameState state,EffectNodeRuntimeState node)
        {
            if(node.EffectTypeId!=CharacterLifecycleService.ConsumeSourceTypeId)return true;
            if(ValidateSavedVersion(node.EffectTypeId,node.DefinitionVersion,true).Length>0||node.PositionChanges.Count!=0||
                node.CommonItemMovementV4!=null||node.ItemMovementV4!=null||node.CharacterCoverMovementV4!=null)return false;
            string stage=CharacterLifecycleService.GetResumableFlowStage(node);var body=Body(state,node);var use=Use(state,node);
            if(stage.Length==0)return body==null&&node.Status!=EffectNodeStatus.Completed;
            if(stage==Already)return body==null&&SameUse(node,use)&&use.SourceConsumed&&state.EffectRuntime.EffectNodes.Any(n=>n.EffectId!=node.EffectId&&
                n.EffectTypeId==node.EffectTypeId&&n.FlowStage==Consumed&&Text(n.NormalizedArguments,"useId")==use.UseId&&TryValidate(state,n));
            if(!new[]{Moving,Consumed,Failed}.Contains(stage)||!SameUse(node,use)||body==null||body.EffectTypeId!=TypeId||!ItemMovementV4.IsV4(body.EffectTypeId,body.NormalizedArguments)||body.ParentEffectId!=node.EffectId||body.PlayerId!=node.PlayerId)return false;
            int index=node.ChildEffectIds.IndexOf(body.EffectId);
            if(body.EffectId!=StableIdFactory.Create("effect",node.EffectId,"child","child_effect",index.ToString(CultureInfo.InvariantCulture),TypeId,Key(node))||
                ItemTransferService.MovementV4Digest(body.NormalizedArguments)!=ItemTransferService.MovementV4Digest(Arguments(state,node))||!ItemMovementV4.TryValidateState(state,body,out _))return false;
            if(stage==Moving)return node.Status!=EffectNodeStatus.Completed;
            if(stage==Failed)return body.Status==EffectNodeStatus.Failed&&node.FailureReason==body.FailureReason;
            var receipt=body.CommonItemMovementV4?.Receipt??body.ItemMovementV4?.Receipt;
            return use.SourceConsumed&&body.Status==EffectNodeStatus.Completed&&receipt!=null&&receipt.Result.Changed&&receipt.Result.MovedItemIds.SequenceEqual(new[]{use.DoubleUseSourceItemId})&&
                (node.Status==EffectNodeStatus.Completed||node.PendingOutcome==EffectPendingOutcome.Completed)&&node.NormalizedResult.Kind==NormalizedValueKind.Null;
        }
    }
}
