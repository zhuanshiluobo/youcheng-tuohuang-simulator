using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    /// <summary>内容显式声明一次生产范围；事件响应不继承该范围，动态分支只在实际挂接时登记。</summary>
    public static class InfluenceDepartureBatchExecutor
    {
        public const string TypeId = "effect.influence.departure_batch";
        public static EffectSpec Create(int player, IEnumerable<EffectSpec> effects)
        { var spec = EffectSpec.Create(TypeId, ItemTransferEffectExecutor.Object(), player); spec.NestedEffectGroups.Add(effects.ToList()); return spec; }
        public static void Register(EffectRegistry registry)
        {
            if (registry.TryGet(TypeId, out _)) return;
            registry.Register(new EffectRegistration(TypeId, Execute, EffectExecutorKind.IntrinsicFlow, "1.0.0")
            { Validator = spec => spec.NestedEffectGroups.Count == 1 && spec.Children.Count == 0 && spec.NormalizedArguments.Properties.Count == 0 ? "" : "departure_batch_body_invalid" });
        }
        private static EffectStepResult Execute(EffectExecutionContext context)
        {
            var node = context.Node; var runtime = context.State.EffectRuntime;
            if (node.FlowStage.Length == 0)
            {
                string id = StableIdFactory.Create("departure_batch", node.EffectId);
                runtime.InfluenceDepartureBatches.Add(new InfluenceDepartureBatch { BatchId = id, OwnerEffectId = node.EffectId });
                node.DepartureBatchId = id;
                return EffectStepResult.Continue("producing").AddChildren(node.NestedEffects.Select(EffectRegistry.FromRuntimeSpec).ToList());
            }
            if (node.FlowStage != "producing") throw new InvalidOperationException("departure_batch_phase_invalid");
            var batch = runtime.InfluenceDepartureBatches.Single(b => b.BatchId == node.DepartureBatchId);
            foreach (string producer in batch.ProducerSteps)
            {
                var step = runtime.EffectNodes.Single(n => n.EffectId == producer);
                if (step.Status != EffectNodeStatus.Completed && step.Status != EffectNodeStatus.Failed) throw new InvalidOperationException("departure_batch_producer_unfinished");
                if (!batch.FinishedSteps.Contains(producer)) batch.FinishedSteps.Add(producer);
            }
            batch.Sealed = true;
            return EffectStepResult.Completed(ItemTransferEffectExecutor.Object("batchId", NormalizedValue.CreateString(batch.BatchId), "actualRemovedCount", NormalizedValue.CreateInteger(batch.DepartureIds.Count)));
        }
    }
}
