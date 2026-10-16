using System;
using System.Linq;
using System.Text;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    // 续接元数据不是业务参数或第二份收据；完成事件/binding仍是唯一消费依据。
    public static class EffectContinuationContext
    {
        public static bool IsValid(NormalizedValue value)
        {
            if (value == null) return true;
            int count = 0, size = 0;
            return value.Kind == NormalizedValueKind.Object && value.IsValid() && Visit(value, 0, ref count, ref size);
        }
        private static bool Visit(NormalizedValue value, int depth, ref int count, ref int size)
        {
            if (value == null || depth > 4 || ++count > 128) return false;
            size += Encoding.UTF8.GetByteCount(value.StringValue ?? "") + Encoding.UTF8.GetByteCount(value.ReferenceId ?? "") + Encoding.UTF8.GetByteCount(value.ReferenceType ?? "") + 8;
            if (size > 4096) return false;
            if (value.Kind == NormalizedValueKind.Array)
            {
                if (value.Items.Count > 64) return false;
                foreach (var item in value.Items) if (!Visit(item, depth + 1, ref count, ref size)) return false;
            }
            if (value.Kind == NormalizedValueKind.Object)
                foreach (var entry in value.Properties)
                {
                    size += Encoding.UTF8.GetByteCount(entry.Name) + 4;
                    if (!Visit(entry.Value, depth + 1, ref count, ref size)) return false;
                }
            return size <= 4096;
        }
        public static bool TryValidate(EffectRuntimeState runtime, out string reason)
        {
            reason = "effect_continuation_context_invalid";
            if (runtime?.EffectNodes == null) return false;
            foreach (var node in runtime.EffectNodes)
            {
                if (!IsValid(node.ContinuationContext) || node.ContinuationContext != null &&
                    (string.IsNullOrEmpty(node.ContinuationHandlerId) || string.IsNullOrEmpty(node.ContinuationContentHash))) return false;
                if (node.ContinuationContext == null && !string.IsNullOrEmpty(node.ContinuationScopeRootEffectId)) return false;
                if (node.ContinuationContext != null)
                {
                    var scope = runtime.EffectNodes.Find(n => n.EffectId == node.ContinuationScopeRootEffectId);
                    if (scope?.ContinuationContext == null || scope.ContinuationContentHash != node.ContinuationContentHash || scope.ContinuationAbilityId != node.ContinuationAbilityId || scope.ContinuationContentId != node.ContinuationContentId || scope.ContinuationDefinitionVersion != node.ContinuationDefinitionVersion || scope.PlayerId != node.PlayerId) return false;
                    var current = node; var visited = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                    while (current != null && current.EffectId != scope.EffectId && visited.Add(current.EffectId)) current = runtime.EffectNodes.Find(n => n.EffectId == current.ParentEffectId);
                    if (current?.EffectId != scope.EffectId) return false;
                }
                if (!Nested(node.NestedEffects, 0)) return false;
            }
            reason = ""; return true;
        }
        private static bool Nested(System.Collections.Generic.IEnumerable<EffectSpecRuntimeState> specs, int depth)
        {
            if (specs == null || depth > 64) return false;
            return specs.All(spec => spec != null && IsValid(spec.ContinuationContext) &&
                (spec.ContinuationContext == null || !string.IsNullOrEmpty(spec.CompletionHandlerId) && !string.IsNullOrEmpty(spec.ContinuationContentHash)) &&
                Nested(spec.NestedEffects, depth + 1));
        }
    }
}
