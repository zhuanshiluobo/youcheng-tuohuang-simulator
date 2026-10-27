#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;

namespace YC.Presentation.Validation
{
    // 仅验收夹具：计数依据是实际规则/请求进展，不把帧、轮询或失败提交算进展。
    internal sealed class HotseatUiValidationContinuationPolicy
    {
        private readonly Dictionary<string, int> rereads = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> firstIssues = new HashSet<string>(StringComparer.Ordinal);
        internal bool FirstIssue(string key) => firstIssues.Add(key);
        internal bool TryReread(string progress, bool exactIdentity, bool protocolHealthy, bool runtimeHealthy)
        {
            if (!exactIdentity || !protocolHealthy || !runtimeHealthy || string.IsNullOrEmpty(progress)) return false;
            rereads.TryGetValue(progress, out int count);
            rereads[progress] = count + 1;
            return count < 3;
        }
        internal static bool CanDeferBusinessRejection(string error, bool exactIdentity, bool healthy) =>
            exactIdentity && healthy && (error == "InsufficientResource" || error == "InsufficientInfluence" ||
                error == "OccupiedSlot" || error == "ClosedLocation" || error == "NoRoute");
    }
}
#endif
