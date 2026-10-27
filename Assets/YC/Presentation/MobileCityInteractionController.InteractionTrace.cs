using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
using YC.Presentation.Workflows;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace YC.Presentation
{
    public sealed partial class MobileCityInteractionController
    {
        private bool interactionTraceOwner, traceCountersWereEnabled;
        private int traceMainThreadId;
        private long traceLastPulse;
        private long traceUiLocalDirectHits, traceUiCacheHits, traceUiCreationHits;
        private int traceButtonFlags = -1, traceGateRevision = -1, traceBoundRevision = -1, traceBoundIdentity;
        private bool traceGateResult;
        private string traceGateReason, traceGateRequest, traceBoundContent;
        private string tracePendingEffect, tracePendingCharacter;
        private int traceWaitFacts = -1;

        private void StartInteractionTrace()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--yc-interaction-latency") < 0 || !NetworkExecutionObservation.Enabled) return;
            traceMainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            interactionTraceOwner = InteractionLatencyTrace.TryStart(WriteInteractionTrace);
            if (!interactionTraceOwner) return;
            traceCountersWereEnabled = session?.ExecutionTimings.Enabled == true;
            if (session != null) session.ExecutionTimings.Enabled = true;
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            InteractionLatencyTrace.Point("run.identity", detail: "unity=" + UnityEngine.Application.unityVersion +
                ";pid=" + process.Id +
                ";mainMvid=" + GetType().Assembly.ManifestModule.ModuleVersionId + ";appMvid=" +
                typeof(YC.Application.Sessions.GameSession).Assembly.ManifestModule.ModuleVersionId);
            InteractionLatencyTrace.Point("run.domain_workflows_identity", detail: "domainMvid=" + typeof(GameState).Assembly.ManifestModule.ModuleVersionId +
                ";workflowsMvid=" + typeof(InteractionLatencyTrace).Assembly.ManifestModule.ModuleVersionId);
            traceLastPulse = Stopwatch.GetTimestamp();
#endif
        }

        private void StopInteractionTrace()
        {
            if (!interactionTraceOwner) return;
            InteractionLatencyTrace.Stop();
            if (session != null) session.ExecutionTimings.Enabled = traceCountersWereEnabled;
            interactionTraceOwner = false;
        }

        private void WriteInteractionTrace(InteractionLatencyTrace.Sample sample)
        {
            if (!NetworkExecutionObservation.Enabled) { InteractionLatencyTrace.Stop("trace.network_sink_disabled"); return; }
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != traceMainThreadId)
            {
                // 罕见后台端口调用不碰Unity或UI状态；不假借主线程会话元数据。
                NetworkExecutionObservation.Event("interaction." + sample.Name,
                    new NetworkExecutionObservation.Context { CommandId = sample.CommandId, Frame = -1, Revision = -1, PlayerId = -1 },
                    "edge=" + sample.Edge + ";span=" + sample.SpanId + ";parent=" + sample.ParentId +
                    ";ticks=" + sample.ElapsedTicks + ";off_main_unbound");
                return;
            }
            // 安装图与实际绑定图分开记；这里不调用VisibleSaleState/投影/可用性查询。
            var view = session?.View;
            var identity = commandSubmission?.InteractionTraceIdentity ?? default;
            identity.GameId = view?.GameId ?? identity.GameId;
            identity.CommandId = sample.CommandId;
            identity.Frame = Time.frameCount;
            identity.Revision = view?.StateRevision ?? -1;
            identity.PlayerId = localPlayerId;
            string detail = "edge=" + sample.Edge + ";span=" + sample.SpanId + ";parent=" + sample.ParentId +
                ";ticks=" + sample.ElapsedTicks + ";value=" + sample.Value + ";boundRev=" + traceBoundRevision +
                ";boundView=" + traceBoundIdentity + ";installedView=" + (view == null ? 0 : RuntimeHelpers.GetHashCode(view));
            // 身份行独立写，避免将长Request/Window/Content拼到既有512字符Detail上限之后。
            if (sample.Detail != null) detail += ";detail=" + sample.Detail;
            NetworkExecutionObservation.Event("interaction." + sample.Name, identity, detail);
        }

        private void TraceVisibleBinding(GameStateView visible)
        {
            if (!InteractionLatencyTrace.Enabled) return;
            traceBoundRevision = visible?.StateRevision ?? -1;
            traceBoundIdentity = visible == null ? 0 : RuntimeHelpers.GetHashCode(visible);
            if (traceBoundContent != visible?.ContentPackHash)
            {
                traceBoundContent = visible?.ContentPackHash;
                InteractionLatencyTrace.Point("binding.content", detail: traceBoundContent);
            }
            InteractionLatencyTrace.Point("binding.request", detail: interactionRequestRouter?.CurrentRequest?.InteractionId);
            InteractionLatencyTrace.Point("binding.window", detail: visible?.ActiveSettlementWindowId);
            InteractionLatencyTrace.Point("binding.phase", value: visible == null ? -1 : (int)visible.Phase);
            gameplayInteractionHud?.ObserveInteractionTraceFrame();
        }

        private bool TraceEndGate(bool result, string reason, GameStateView visible = null)
        {
            if (!InteractionLatencyTrace.Enabled) return result;
            int revision = visible?.StateRevision ?? session?.View?.StateRevision ?? -1;
            string request = interactionRequestRouter?.CurrentRequest?.InteractionId;
            if (traceGateReason != reason || traceGateResult != result || traceGateRevision != revision || traceGateRequest != request)
            {
                traceGateReason = reason; traceGateResult = result; traceGateRevision = revision; traceGateRequest = request;
                InteractionLatencyTrace.Point("end.gate", detail: reason, value: result ? 1 : 0);
                InteractionLatencyTrace.Point("end.request", detail: request);
                if (reason == "route.can_confirm")
                {
                    try
                    {
                    var presenter = routeResourceSelection?.Presenter;
                    var projection = presenter?.Projection;
                    int flags = (projection != null ? 1 : 0) | (projection?.Validation.IsValid == true ? 2 : 0) |
                        (projection?.Preview != null ? 4 : 0) | (projection?.Preview?.FinalizationValidation.IsValid == true ? 8 : 0) |
                        (presenter?.Session != null ? 16 : 0) | (presenter?.Session?.MinimumLocations == 0 ? 32 : 0) |
                        (presenter?.Draft != null ? 64 : 0) | (presenter?.Draft?.SelectedLocationIds.Count == 0 ? 128 : 0);
                    InteractionLatencyTrace.Point("route.stored_projection_flags", value: flags);
                    }
                    catch { InteractionLatencyTrace.Stop("trace.route_observation_failed"); }
                }
            }
            return result;
        }

        private void PulseInteractionTrace()
        {
            if (!InteractionLatencyTrace.Enabled) return;
            int waitFacts = (GameplayHudFrame.EffectInputSuspended ? 1 : 0) |
                (resourceCollectionPresenter?.IsSubmissionPending == true ? 2 : 0) |
                (!string.IsNullOrEmpty(pendingEffectCommandId) ? 4 : 0) |
                (!string.IsNullOrEmpty(characterUseCommandId) ? 8 : 0) |
                (lastActionPanelPermissionModel?.CanEndAction == true ? 16 : 0) |
                (lastActionPanelPermissionModel == null ? 32 : 0);
            if (waitFacts != traceWaitFacts || tracePendingEffect != pendingEffectCommandId || tracePendingCharacter != characterUseCommandId)
            {
                traceWaitFacts = waitFacts; tracePendingEffect = pendingEffectCommandId; tracePendingCharacter = characterUseCommandId;
                InteractionLatencyTrace.Point("gate.local_wait_facts", value: waitFacts);
                InteractionLatencyTrace.Point("gate.pending_effect", detail: tracePendingEffect);
                InteractionLatencyTrace.Point("gate.pending_character", detail: tracePendingCharacter);
            }
            var first = gameplayInteractionHud?.Frame?.EndActionButton;
            var second = gameplayInteractionHud?.ActionPanelView?.EndRoundButton;
            int flags = ButtonTraceFlags(first) | ButtonTraceFlags(second) << 4;
            if (flags != traceButtonFlags)
            { traceButtonFlags = flags; InteractionLatencyTrace.Point("end.actual_buttons", value: flags); }
            if (Input.GetMouseButtonDown(0)) InteractionLatencyTrace.Point("input.unity_mouse_down",
                detail: Input.mousePosition.x.ToString(CultureInfo.InvariantCulture) + "," + Input.mousePosition.y.ToString(CultureInfo.InvariantCulture));
            long now = Stopwatch.GetTimestamp();
            if (now - traceLastPulse < Stopwatch.Frequency) return;
            InteractionLatencyTrace.Point("main.pulse_gap", value: now - traceLastPulse);
            InteractionLatencyTrace.Point("ui.read_state.branch_hits", detail: "localDirect=" + traceUiLocalDirectHits +
                ";cache=" + traceUiCacheHits + ";create=" + traceUiCreationHits);
            traceLastPulse = now;
            // 每秒一次，不强制GC；managed估算、驻留集和PrivateBytes不能互相替代。
            try
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                process.Refresh();
                InteractionLatencyTrace.Point("memory.working_set", value: process.WorkingSet64);
                InteractionLatencyTrace.Point("memory.private_bytes", value: process.PrivateMemorySize64);
                InteractionLatencyTrace.Point("memory.managed_estimate", value: GC.GetTotalMemory(false));
                InteractionLatencyTrace.Point("process.cpu_ticks", value: process.TotalProcessorTime.Ticks);
                InteractionLatencyTrace.Point("gc.collections", detail: GC.CollectionCount(0) + "," + GC.CollectionCount(1) + "," + GC.CollectionCount(2));
            }
            catch { /* 诊断访问失败不影响游戏。 */ }
        }

        private GameState ReadUiStateWithTrace()
        {
            // 计时移到唯一实际创建点，覆盖直接调用；缓存命中只累计，每秒输出一次。
            // 不因高频状态读取耗尽8,192项，也不额外重读状态或候选。
            return UiReadState();
        }

        internal static int ButtonTraceFlags(UnityEngine.UI.Button button) => button == null ? 0 :
            (button.gameObject.activeInHierarchy ? 1 : 0) | (button.interactable ? 2 : 0) | (button.IsInteractable() ? 4 : 0) | (button.enabled ? 8 : 0);
    }
}
