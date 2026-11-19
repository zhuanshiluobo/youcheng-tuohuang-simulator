using System;
using System.Collections.Generic;
using System.Linq;
using YC.Domain.Cards;
using YC.Domain.State;

namespace YC.Domain.Effects
{
    public sealed partial class EffectRegistry
    {
        private sealed class DispatchCountPolicy
        {
            internal string Identity;
            internal Func<EffectExecutionContext, NormalizedValue, IReadOnlyList<int>> Resolve;
        }
        private readonly Dictionary<string, DispatchCountPolicy> dispatchCountPolicies = new Dictionary<string, DispatchCountPolicy>(StringComparer.Ordinal);

        // 独立于效果事件分发：同步收集纯数据，不挂接效果、不消耗来源次数。
        public void RegisterOperatorDispatchCountPolicy(string id, string identity,
            Func<EffectExecutionContext, NormalizedValue, IReadOnlyList<int>> resolve)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(identity) || resolve == null)
                throw new ArgumentException("dispatch_count_policy_invalid");
            if (dispatchCountPolicies.TryGetValue(id, out var prior))
            {
                if (prior.Identity != identity) throw new InvalidOperationException("dispatch_count_policy_conflict");
                return;
            }
            dispatchCountPolicies.Add(id, new DispatchCountPolicy { Identity = identity, Resolve = resolve });
        }

        public int ResolveOperatorDispatchCount(EffectExecutionContext context, NormalizedValue payload, int basis)
        {
            long total = basis;
            foreach (var registration in dispatchCountPolicies.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                // 每个策略获得独立查询状态，既不能改权威状态，也不能污染下一个策略。
                var isolated = GameStateCloneService.DeepClone(context.State);
                var owner = isolated.EffectRuntime.EffectNodes.Single(n => n.EffectId == context.Node.EffectId);
                var response = registration.Value.Resolve(new EffectExecutionContext(isolated, owner, this), payload.Clone());
                if (response == null || response.Count > 1024 || response.Any(delta => delta < -99 || delta > 99))
                    throw new InvalidOperationException("dispatch_count_patch_invalid");
                foreach (int delta in response) total += delta;
            }
            if (total < 1 || total > OperatorDispatchFlow.MaximumDrawCount)
                throw new InvalidOperationException("dispatch_draw_count_invalid");
            return (int)total;
        }
    }
}
