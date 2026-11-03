#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Linq;
using UnityEngine;
using YC.Presentation.Workflows;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        private bool memoryAttributionHolding;
        private bool BeginMemoryAttributionIfRequested()
        {
            string mode = Argument("--yc-memory-attribution=");
            if (string.IsNullOrEmpty(mode)) return false;
            Require((mode == "keep" || mode == "detach") && LocalFullGameResumeMode && !NetworkMode && commands.Count == 0,
                "内存归因只允许正常菜单读取指定原档，不执行游戏后继行动");
            memoryAttributionHolding = true;
            var probe = new GameObject("内存归因只读采样").AddComponent<FixtureMemoryAttributionProbe>();
            DontDestroyOnLoad(probe.gameObject);
            probe.Begin(output, mode, this, State.GameId, State.EffectRuntime.StateRevision);
            return true;
        }
        internal object MemoryAttributionFixtureInventory() => new {
            Commands = commands.Count, Screenshots = screenshots.Count, Checks = checks.Count, Errors = errors.Count,
            AppendWriters = appendLogs.Count, ObservedGateways = observedGateways.Count,
            ResumedCompletedIds = resumedCompletedEffectIds.Count,
            SavedArchiveCharacters = localResumeChosenSlot?.Data?.ArchiveJson?.Length ?? 0,
            ActiveCoroutinesStopped = memoryAttributionHolding
        };
        internal void DetachForMemoryAttribution()
        {
            // 恢复原命令端口及订阅；只销毁独立验收对象，不销毁游戏对象或改变状态。
            foreach (var gateway in observedGateways.ToArray())
            {
                var current = Read(gateway, "commandPort");
                if (current is ObservationPort port && ReferenceEquals(port.driver, this))
                    typeof(CommandGateway).GetField("commandPort", Members).SetValue(gateway, port.inner);
            }
            UnityEngine.Application.logMessageReceived -= OnLog;
            foreach (var writer in appendLogs.Values) writer.Dispose();
            appendLogs.Clear(); observedGateways.Clear();
            StopAllCoroutines();
            Destroy(gameObject);
        }
    }
}
#endif
