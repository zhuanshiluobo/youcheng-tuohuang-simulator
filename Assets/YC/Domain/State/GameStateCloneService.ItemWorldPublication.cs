using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Items;
using YC.Domain.Cards;

namespace YC.Domain.State
{
    public static partial class GameStateCloneService
    {
        // 供封闭实体工厂和角色事务使用：入口已证明原图引用独立，预演克隆了这七组，
        // 其余字段在无回调的同步创建/实体校验中只读借用。公开CopyTo合同不变。
        // 逐字段沿原复制器保留目标对象身份及新增分支的独立性，不给借用历史列表写同值。
        internal static void CopyItemWorldPreviewTo(GameState target,GameState source,bool verify) =>
            CopyItemWorldPreviewCore(target,source,verify,null);
        // 角色移动与整批回收另有唯一的当前节点写集；其他Runtime字段仍只读借用。
        internal static void CopyItemWorldNodePreviewTo(GameState target,GameState source,string nodeId,bool verify) =>
            CopyItemWorldPreviewCore(target,source,verify,nodeId);
        private static void CopyItemWorldPreviewCore(GameState target,GameState source,bool verify,string nodeId)
        {
            using var timing=new CloneTimingOperation("handler.copy.item_world",true);
            using var nodeTiming=nodeId==null?default:MeasureDiagnostic("character.item_world.publish");
            GameState reference=null;
            if(verify || IsSchedulingCopyVerificationEnabled) { reference=DeepClone(target);CopyTo(reference,source); }
            using var uniqueness=new CopyTargetOperation(target);
            var visited=RentComparisonPairs();var active=new HashSet<object>(ReferenceEqualityComparer.Instance);
            var path=new DiagnosticPath("GameState");
            try
            {
                target.ItemZones=(ItemZoneRuntimeState)CopyValueInto(target.ItemZones,source.ItemZones,typeof(ItemZoneRuntimeState),path.AtField("ItemZones"),visited,active);
                target.CharacterUses=(List<CharacterUseRuntimeState>)CopyValueInto(target.CharacterUses,source.CharacterUses,typeof(List<CharacterUseRuntimeState>),path.AtField("CharacterUses"),visited,active);
                target.CharacterCoverSlots=(List<CoverSlotState>)CopyValueInto(target.CharacterCoverSlots,source.CharacterCoverSlots,typeof(List<CoverSlotState>),path.AtField("CharacterCoverSlots"),visited,active);
                target.FacilityCharacterBindings=(List<FacilityCharacterBindingState>)CopyValueInto(target.FacilityCharacterBindings,source.FacilityCharacterBindings,typeof(List<FacilityCharacterBindingState>),path.AtField("FacilityCharacterBindings"),visited,active);
                target.Players=(List<PlayerState>)CopyValueInto(target.Players,source.Players,typeof(List<PlayerState>),path.AtField("Players"),visited,active);
                target.Map=(MapRuntimeState)CopyValueInto(target.Map,source.Map,typeof(MapRuntimeState),path.AtField("Map"),visited,active);
                target.Decks=(DeckRuntimeState)CopyValueInto(target.Decks,source.Decks,typeof(DeckRuntimeState),path.AtField("Decks"),visited,active);
                if(nodeId!=null)
                {
                    var sourceNode=source.EffectRuntime.EffectNodes.Single(node=>node.EffectId==nodeId);
                    var targetNode=target.EffectRuntime.EffectNodes.Single(node=>node.EffectId==nodeId);
                    int index=target.EffectRuntime.EffectNodes.IndexOf(targetNode);
                    target.EffectRuntime.EffectNodes[index]=(EffectNodeRuntimeState)CopyValueInto(targetNode,sourceNode,
                        typeof(EffectNodeRuntimeState),path.AtField("EffectRuntime").AtField("EffectNodes").AtIndex(index),visited,active);
                }
                ItemZoneLegacyProjection.Bind(target);
                if(reference!=null && !AreEquivalent(target,reference))throw new InvalidOperationException("物品状态写集发布与原完整复制结果不一致。");
            }
            finally {ReturnComparisonPairs(visited);}
        }
    }
}
