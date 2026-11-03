using System;
using System.Globalization;
using System.Linq;
using YC.Domain.Effects;
using YC.Domain.Items;
using YC.Domain.Rules;
using YC.Domain.State;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Domain.Cards
{
    // 主盖放不是普通角色能力：依据实际主链/组/本人任务，不依据某个来源牌或客户端给出的任务ID。
    public static class MainCoverAuthority
    {
        public static bool IsMainBody(EffectNodeRuntimeState node)=>node?.EffectTypeId==TypeId&&Value(node.NormalizedArguments,"apiVersion")?.Kind==NormalizedValueKind.Integer&&Value(node.NormalizedArguments,"apiVersion").IntegerValue==4&&Text(node.NormalizedArguments,"operation")=="cover_character"&&Text(Value(node.NormalizedArguments,"cover"),"mode")=="main";
        private static bool Terminal(EffectNodeStatus s)=>s==EffectNodeStatus.Completed||s==EffectNodeStatus.Failed||s==EffectNodeStatus.Faulted;
        // Host隔离准备仍是内部任务，公开Lua无版本盖牌继续拒绝。
        // 只认Kernel实际创建的空参数根；共同子步骤仍绑定该任务和原child身份。
        private static bool HostPreparation(GameState state, EffectNodeRuntimeState candidate, bool active)
        {
            var args = candidate.NormalizedArguments;
            int index = state.EffectRuntime.EffectNodes.IndexOf(candidate);
            return candidate.ParentEffectId.Length == 0 && candidate.SourceId.Length == 0 &&
                (args == null || args.Kind == NormalizedValueKind.Null || args.Kind == NormalizedValueKind.Object && args.Properties.Count == 0) &&
                state.FindPlayer(candidate.PlayerId) != null && index >= 0 &&
                candidate.EffectId == StableIdFactory.Create("effect", state.GameId, "root", candidate.RoundId, CharacterCoverEffectExecutor.EffectTypeId, "", index.ToString(CultureInfo.InvariantCulture), "") &&
                (!active || !Terminal(candidate.Status) && candidate.Visibility == GameStateVisibilityPolicy.Owner);
        }
        public static bool TryGetTask(GameState state,EffectNodeRuntimeState owner,bool active,out EffectNodeRuntimeState task)
        {
            task=null;if(state?.EffectRuntime==null||owner==null)return false;
            var candidate=owner.EffectTypeId==CharacterCoverEffectExecutor.EffectTypeId?owner:IsMainBody(owner)?state.EffectRuntime.EffectNodes.SingleOrDefault(n=>n.EffectId==owner.ParentEffectId):null;
            if(candidate?.EffectTypeId!=CharacterCoverEffectExecutor.EffectTypeId||Value(candidate.NormalizedArguments,"apiVersion")!=null||candidate.DefinitionVersion!=CharacterCoverEffectExecutor.SharedCoverDefinitionVersion)return false;
            if (HostPreparation(state, candidate, active))
            {
                if (owner != candidate)
                {
                    int bodyIndex = candidate.ChildEffectIds.IndexOf(owner.EffectId);
                    if (bodyIndex < 0 || owner.PlayerId != candidate.PlayerId || owner.EffectId != StableIdFactory.Create("effect", candidate.EffectId, "child", "child_effect", bodyIndex.ToString(CultureInfo.InvariantCulture), TypeId, candidate.EffectId + ":main-v4")) return false;
                }
                task = candidate; return true;
            }
            var execution=state.EffectRuntime.EffectNodes.SingleOrDefault(n=>n.EffectId==candidate.ParentEffectId);
            if(execution?.EffectTypeId!=RoundExecutionService.MainlineEffectTypeId||Text(execution.NormalizedArguments,"nodeTypeId")!=RoundMainlineNodeTypeIds.CharacterCover||!execution.ChildEffectIds.Contains(candidate.EffectId))return false;
            var main=state.EffectRuntime.MainNodes.SingleOrDefault(m=>m.ExecutionEffectId==execution.EffectId&&m.NodeId==Text(execution.NormalizedArguments,"mainNodeId")&&m.NodeTypeId==RoundMainlineNodeTypeIds.CharacterCover);
            if(main==null)return false;var order=new TurnOrderService().GetTurnOrder(state,main.StartPlayerId);int index=order.ToList().IndexOf(candidate.PlayerId);
            var args=candidate.NormalizedArguments;
            if(index<0||args?.Kind!=NormalizedValueKind.Object||args.Properties.Any(p=>!new[]{"playerId","coverIndex","mainNodeId"}.Contains(p.Name))||
                Value(args,"playerId")?.Kind!=NormalizedValueKind.Integer||Value(args,"playerId").IntegerValue!=candidate.PlayerId||Value(args,"coverIndex")?.Kind!=NormalizedValueKind.Integer||Value(args,"coverIndex").IntegerValue!=index||Text(args,"mainNodeId")!=main.NodeId||candidate.SourceId!=main.NodeId||candidate.Visibility!=GameStateVisibilityPolicy.Owner)return false;
            int childIndex=execution.ChildEffectIds.IndexOf(candidate.EffectId);
            if(candidate.EffectId!=StableIdFactory.Create("effect",execution.EffectId,"child","child_effect",childIndex.ToString(CultureInfo.InvariantCulture),CharacterCoverEffectExecutor.EffectTypeId,"cover-task:"+candidate.PlayerId.ToString(CultureInfo.InvariantCulture)))return false;
            if(RoundExecutionService.IsParallelCoverExecution(execution))
            {
                var group=execution.ChildSchedulingGroups.SingleOrDefault(g=>g.StableGroupKey=="character_cover_players");
                if(group==null||group.OwnerEffectId!=execution.EffectId||group.ProtocolVersion!=1||group.Mode!=EffectChildSchedulingMode.Independent||!group.ChildEffectIds.Contains(candidate.EffectId)||group.ChildEffectIds.IndexOf(candidate.EffectId)!=index||
                    group.ChildEffectIds.Count!=order.Count||!execution.ChildEffectIds.Skip(group.StartIndex).Take(group.ChildEffectIds.Count).SequenceEqual(group.ChildEffectIds))return false;
            }
            else if(Value(execution.NormalizedArguments,"coverTaskSchedulingVersion")!=null)return false;
            if(active&&(state.Phase!=GamePhase.CharacterCover||state.EffectRuntime.ActiveMainNodeId!=main.NodeId||Terminal(execution.Status)||Terminal(candidate.Status)||Terminal(main.Status)))return false;
            if(owner!=candidate)
            {
                int i=candidate.ChildEffectIds.IndexOf(owner.EffectId);
                if(i<0||owner.PlayerId!=candidate.PlayerId||owner.EffectId!=StableIdFactory.Create("effect",candidate.EffectId,"child","child_effect",i.ToString(CultureInfo.InvariantCulture),TypeId,candidate.EffectId+":main-v4"))return false;
            }
            task=candidate;return true;
        }
        public static EffectNodeRuntimeState Body(GameState state,EffectNodeRuntimeState task)=>task?.ChildEffectIds.Select(id=>state.EffectRuntime.EffectNodes.SingleOrDefault(n=>n.EffectId==id)).SingleOrDefault(n=>IsMainBody(n)&&TryGetTask(state,n,false,out var found)&&found.EffectId==task.EffectId);
        public static bool IsRequestOwner(GameState state,EffectNodeRuntimeState owner,string executionId)=>TryGetTask(state,owner,true,out var task)&&task.ParentEffectId==executionId;
    }
}
