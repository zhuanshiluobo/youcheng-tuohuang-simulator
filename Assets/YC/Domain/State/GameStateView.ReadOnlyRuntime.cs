using System;
using System.Collections.Generic;
using YC.Domain.Cards;
using YC.Domain.Effects;
using YC.Domain.Items;

namespace YC.Domain.State
{
    public static partial class GameStateViewProjector
    {
        [ThreadStatic] private static bool fullProjectionStateCopies,verifyProjectionStateCopies;
        public static IDisposable UseFullProjectionStateCopiesForDiagnostics() => new ProjectionCopyScope(true,false);
        public static IDisposable VerifyProjectionStateCopiesForDiagnostics() => new ProjectionCopyScope(false,true);
        private sealed class ProjectionCopyScope:IDisposable
        {
            private readonly bool previousFull=fullProjectionStateCopies,previousVerify=verifyProjectionStateCopies;
            private bool closed;
            internal ProjectionCopyScope(bool full,bool verify){fullProjectionStateCopies=full;verifyProjectionStateCopies=verify;}
            public void Dispose(){if(closed)return;closed=true;fullProjectionStateCopies=previousFull;verifyProjectionStateCopies=previousVerify;}
        }
        private static bool PlainProjectionInput<T>(IReadOnlyList<T> values) =>
            values==null || values.GetType()==typeof(List<T>) || values.GetType()==typeof(T[]);

        private static bool CanBorrowProjectionRuntime(GameState state,GameStateViewer viewer,
            IReadOnlyList<CharacterSettlementOption> settlementOptions,IReadOnlyList<AbilityActionOption> abilityActions)
        {
            if(fullProjectionStateCopies || state?.EffectRuntime==null || !ItemZoneLegacyProjection.IsActive(state) ||
                !PlainProjectionInput(settlementOptions) || !PlainProjectionInput(abilityActions))return false;
            // 这两个归档身份检查在requireCurrentSource=false时只读真实既有证明，不查询规则回调。
            // 一旦有当前付款准备，其CanConfirm可能调用普通C#费用／候选处理器，仍用原整图隔离。
            if(viewer.Role==GameStateViewerRole.Player)
                foreach(var node in state.EffectRuntime.EffectNodes)
                    if(node?.Payment?.Preparation!=null && node.PlayerId==viewer.PlayerId &&
                        (ResourcePaymentPreparationService.IsCurrentPreparation(state,node,viewer.PlayerId) ||
                         ResourcePaymentPreparationService.IsCurrentEffectBuildPreparation(state,node,viewer.PlayerId,false)))return false;
            return GameStateCloneService.HasUniqueMutableReferences(state);
        }
        private static GameStateView ProjectWithReadOnlyRuntime(GameState state,GameStateViewer viewer,
            IReadOnlyList<CharacterSettlementOption> settlementOptions,IReadOnlyList<AbilityActionOption> abilityActions,EffectRegistry paymentRegistry)
        {
            if(!CanBorrowProjectionRuntime(state,viewer,settlementOptions,abilityActions))
                return ProjectCore(state,viewer,settlementOptions,abilityActions,paymentRegistry,true);
            var reference=verifyProjectionStateCopies ? GameStateCloneService.DeepClone(state) : null;
            using var sample=GameStateCloneService.MeasureDiagnostic("projection.borrowed_runtime");
            var snapshot=GameStateCloneService.CloneWorldForReadOnlyProjection(state);
            var result=ProjectCore(snapshot,viewer,settlementOptions,abilityActions,paymentRegistry,false);
            if(reference!=null)
            {
                if(!GameStateCloneService.AreEquivalent(state,reference))
                    throw new InvalidOperationException("只读展示改写了原始状态或借用的规则历史。");
                var expected=ProjectCore(reference,viewer,settlementOptions,abilityActions,paymentRegistry,false);
                if(!GameStateCloneService.AreEquivalentPersistentValues(result,expected,typeof(GameStateView)))
                    throw new InvalidOperationException("只读历史投影与原完整克隆的全部可见字段不一致。");
            }
            return result;
        }
    }
}
