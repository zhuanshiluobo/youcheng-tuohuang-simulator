#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Linq;

namespace YC.Presentation.Validation
{
    // 仅消费适配器已经核对过的本人可见候选；不计算卡牌规则或猜测费用。
    internal sealed class ActionCleanupCandidate
    {
        public string Key, CoverageKey, Facts, Kind, Reason;
        public bool Enabled, Pending, Required, Repeatable;
        public int PriorCompleted;
        public long Order;
    }

    internal sealed class ActionCleanupCoveragePolicy
    {
        private sealed class Attempt
        {
            public string Facts, Outcome;
            public bool Terminal;
        }
        private readonly Dictionary<string, List<Attempt>> attempts = new Dictionary<string, List<Attempt>>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> completed = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> declinedConditions = new HashSet<string>(StringComparer.Ordinal);
        public int Completed(string key) => completed.TryGetValue(key ?? "", out var count) ? count : 0;

        public string BlockReason(ActionCleanupCandidate candidate)
        {
            if (candidate.Pending) return "原来源仍在结算，先完成当前响应";
            if (!candidate.Enabled) return string.IsNullOrEmpty(candidate.Reason) ? "原控件或正式候选不可用" : candidate.Reason;
            if (!attempts.TryGetValue(candidate.Key, out var prior) || prior.Count == 0) return "";
            var last = prior[prior.Count - 1];
            if (!last.Terminal) return "首次尝试未结清，不能重复点击";
            // 必做的独立目标有独立Key；有限重复只约束本夹具的可选来源。
            if (prior.Count >= (candidate.Repeatable ? 2 : 1)) return "本窗口已完成有限尝试，保留后续多局覆盖";
            if (last.Outcome != "completed") return "上次未正常完成，本窗口不再反复发动";
            if (candidate.Facts == last.Facts) return "来源资格与库存事实未变化，不能以补点制造覆盖";
            return "";
        }

        public ActionCleanupCandidate Choose(IEnumerable<ActionCleanupCandidate> candidates) => candidates
            .Where(candidate => BlockReason(candidate).Length == 0)
            .OrderBy(candidate => candidate.Required ? 0 : 1)
            .ThenBy(candidate => Math.Max(Completed(candidate.CoverageKey), candidate.PriorCompleted))
            .ThenBy(candidate => attempts.TryGetValue(candidate.Key, out var values) ? values.Count : 0)
            .ThenBy(candidate => candidate.Order).ThenBy(candidate => candidate.Key, StringComparer.Ordinal).FirstOrDefault();

        public void Begin(ActionCleanupCandidate candidate)
        {
            var reason = BlockReason(candidate);
            if (reason.Length > 0) throw new InvalidOperationException(reason);
            if (!attempts.TryGetValue(candidate.Key, out var list)) attempts.Add(candidate.Key, list = new List<Attempt>());
            list.Add(new Attempt { Facts = candidate.Facts });
        }

        public void Complete(ActionCleanupCandidate candidate, string outcome)
        {
            if (!attempts.TryGetValue(candidate.Key, out var list) || list.Count == 0 || list[list.Count - 1].Terminal)
                throw new InvalidOperationException("结清证据必须对应唯一未结束的原尝试");
            list[list.Count - 1].Terminal = true; list[list.Count - 1].Outcome = outcome;
            if (outcome == "completed") completed[candidate.CoverageKey] = Completed(candidate.CoverageKey) + 1;
        }

        // 先完成正常使用；只有正式可放弃且已结清过该来源时，有限覆盖一次主动放弃。
        public bool ChooseDecline(string source, bool canExecute, bool canDecline, bool sourceCompleted) =>
            canDecline && (!canExecute || sourceCompleted && !declinedConditions.Contains(source));
        public void ConfirmDecline(string source) => declinedConditions.Add(source);
    }
}
#endif
