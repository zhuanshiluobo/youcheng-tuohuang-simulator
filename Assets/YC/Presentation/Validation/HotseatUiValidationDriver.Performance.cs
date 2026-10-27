#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YC.Application.Gameplay;
using YC.Application.Interactions;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Multiplayer;
using YC.PlayerJourney;
using YC.Presentation.Workflows;
using Clock = System.Diagnostics.Stopwatch;

namespace YC.Presentation.Validation
{
    public sealed partial class HotseatUiValidationDriver
    {
        // 显式开发期入口。普通驱动不进入此分支；不修改规则、存档或渲染配置。
        private bool? performanceModeCached;
        private bool PerformanceMode
        {
            get
            {
                if (!performanceModeCached.HasValue) performanceModeCached = Argument("--yc-ui-performance=") == "true";
                return performanceModeCached.Value;
            }
        }
        private StreamWriter performanceLog;
        private System.Diagnostics.Process performanceProcess;
        private float performanceNextPulse, performanceLastProgress;
        private int performanceGameCount = 1, performancePulseSeconds = 5;
        private long performanceLastInputTick, performanceStopInputTick, performanceFirstSettledTick;
        private string performanceLastInputUtc, performanceLastInputName, performanceStopInputUtc;
        private int performanceStopCheckpoint;
        private readonly int[] performanceStopTargets = { 0, 5, 15, 30, 90 };
        private bool PerformanceLifecycleAudit => performanceLifetimeMode || performanceGameCount > 1;
        private static int ParsePerformanceGameCount(string value, bool weakLifetime)
        {
            if (value == null) return weakLifetime ? 2 : 1;
            if (!int.TryParse(value, out int count) || count < 1 || count > 2)
                throw new ArgumentException("性能局数只允许1或2");
            return count;
        }
        private void PerformanceObserveSettled()
        {
            if (performanceStopInputTick == 0 || performanceFirstSettledTick != 0 || PerformanceSaveState().AllWorkSettled != true) return;
            performanceFirstSettledTick = Clock.GetTimestamp();
            PerformanceRecord("operations.first_save_settled", new { StopInputTick = performanceStopInputTick,
                ObservedTick = performanceFirstSettledTick,
                SecondsSinceStop = (performanceFirstSettledTick - performanceStopInputTick) / (double)Clock.Frequency,
                ObservationIsNotExactCompletionTime = true });
        }
        private void PerformanceStopInputs()
        {
            performanceStopInputTick = Clock.GetTimestamp(); performanceStopInputUtc = DateTime.UtcNow.ToString("o");
            performanceFirstSettledTick = 0; performanceStopCheckpoint = 0;
            PerformanceRecord("operations.input_stopped", new { StopInputTick = performanceStopInputTick, StopInputUtc = performanceStopInputUtc,
                LastInputTick = performanceLastInputTick, LastInputUtc = performanceLastInputUtc, LastInputName = performanceLastInputName,
                LastInputIndex = performanceInputs, SuccessfulSubmissions = performanceSuccessfulSubmissions,
                Elapsed = performanceOperationsElapsed, StopReason = performanceStopReason,
                SafeBoundaryAlreadyReached = session.ReadSaveCaptureSafety() == SaveCaptureSafety.Safe,
                PendingCommands = performancePending.Count });
            PerformanceMemoryPulse("memory.stop_checkpoint");
        }
        private int performanceRows, performanceInputs, performanceSubmissions, performanceHarvests;
        private int performanceSuccessfulSubmissions;
        private sealed class PerformanceSaveObservation
        {
            public bool Available, StatisticsEnabled;
            public bool? AutomaticSavingEnabled, ControllerSaving, ControllerLeaving, ControllerSessionBound, AutomaticCapturePending;
            public long? Enqueued, Started, Completed, Failed, Cancelled, Pending, Active, Outstanding, PeakOutstanding;
            public int? PendingAutomaticWindowSaves, PeakAutomaticWindowSaves, TracedSaveOperations, StoreIdentity;
            public bool? DiagnosticsRequested, AllWorkSettled;
            public string CurrentGameId, LastFailure;
        }
        private static PerformanceSaveObservation PerformanceSaveState()
        {
            // 只返回标量；不缓存Controller、Store、Session或任务，跨菜单仍读产品同一单例。
            var controller = MatchSaveController.Instance;
            if (controller == null) return new PerformanceSaveObservation { Available = false };
            var queue = controller.PerformanceSaveStatistics;
            bool saving = (bool)Read(controller, "saving"), leaving = (bool)Read(controller, "leaving");
            bool capturePending = controller.AutomaticSavingEnabled && controller.CanSave &&
                (int)Read(controller, "dirty") != (int)Read(controller, "saved");
            var store = Read(controller, "store");
            return new PerformanceSaveObservation { Available = true, StatisticsEnabled = queue.Enabled,
                AutomaticSavingEnabled = controller.AutomaticSavingEnabled, ControllerSaving = saving, ControllerLeaving = leaving,
                ControllerSessionBound = Read(controller, "session") != null, AutomaticCapturePending = capturePending,
                Enqueued = queue.Enabled ? queue.Enqueued : (long?)null, Started = queue.Enabled ? queue.Started : (long?)null,
                Completed = queue.Enabled ? queue.Completed : (long?)null, Failed = queue.Enabled ? queue.Failed : (long?)null,
                Cancelled = queue.Enabled ? queue.Cancelled : (long?)null, Pending = queue.Enabled ? queue.Pending : (long?)null,
                Active = queue.Enabled ? queue.Active : (long?)null, Outstanding = queue.Enabled ? queue.Outstanding : (long?)null,
                PeakOutstanding = queue.Enabled ? queue.PeakOutstanding : (long?)null,
                PendingAutomaticWindowSaves = controller.PendingAutomaticWindowSaves, PeakAutomaticWindowSaves = controller.PeakAutomaticWindowSaves,
                TracedSaveOperations = controller.TracedSaveOperations, DiagnosticsRequested = controller.SavePerformanceDiagnosticsRequested,
                StoreIdentity = store == null ? (int?)null : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(store),
                CurrentGameId = controller.CurrentGameId, LastFailure = controller.LastFailure,
                AllWorkSettled = queue.Enabled ? !saving && !leaving && !capturePending && controller.PendingAutomaticWindowSaves == 0 &&
                    queue.Pending == 0 && queue.Active == 0 && queue.Outstanding == 0 && queue.Enqueued == queue.Completed + queue.Failed + queue.Cancelled : (bool?)null };
        }
        private IEnumerator PerformanceAwaitSaveSettled(string boundary)
        {
            float began = Time.realtimeSinceStartup;
            while (PerformanceSaveState().AllWorkSettled != true && Time.realtimeSinceStartup - began < 30f)
                yield return new WaitForSecondsRealtime(.1f);
            PerformanceObserveSettled();
            PerformanceRecord("save.boundary_readiness", new { Boundary = boundary, WaitSeconds = Time.realtimeSinceStartup - began,
                SaveOwner = PerformanceSaveState(), RuleSafe = session == null ? (bool?)null : session.ReadSaveCaptureSafety() == SaveCaptureSafety.Safe,
                TimeoutIsNotZeroQueueClaim = true });
        }
        private long performanceWriteTicks, performancePulseTicks, performanceSelectorTicks;
        private readonly HashSet<string> performanceDraftClicks = new HashSet<string>();
        private readonly HashSet<string> performancePending = new HashSet<string>();
        private CommandSubmissionController performanceSubmission;
        private string performanceLastFacts, performanceProtocolFault;
        private bool performanceHarvestSubmitted, performanceHarvestCompleted;
        private long performanceHarvestSequence = -1;
        private bool performanceQuickTabsDone, performanceBuiltBound, performanceNormalMenuReturned, performanceQuitObserved;
        private bool performanceLeavingMenuFlow;
        private bool performanceMemoryComparison, performanceDriverReferencesReleased, performanceFirstStallRecorded;
        private bool performanceArtworkBindings;
        private readonly HashSet<string> performanceArtworkEvidenceKeys = new HashSet<string>();
        private int performanceMenuIdleSeconds, performanceInputAttempts, performanceActiveInputIndex, performanceExternalInputFrames;
        private int performanceUnfocusedFrames;
        private Vector3 performancePreviousMousePosition;
        private bool performanceMousePositionObserved;
        private PerformanceRoomPlan performanceRoomPlan;
        private readonly HashSet<string> performanceSnapshotBoundaries = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> performanceCapturedBoundaries = new HashSet<string>(StringComparer.Ordinal);
        private string performanceObservedHarvestReadyRequest;
        private bool performanceSustained;
        private int performanceRowLimit = 2048;
        private float performanceInputStepSeconds, performanceNoInputSeconds, performanceOperationsElapsed;
        private string performanceStopReason;
        private bool performanceMemoryDetail, performanceSnapshotRequested, performanceSnapshotAttempted, performanceHarvestReadyMetrics;
        private float performanceSnapshotPauseSeconds;
        private long performanceOuterSubmitAllocated, performanceHarvestObservationTicks;
        private int performanceOuterSubmitAllocationCalls, performanceMissingSubmitAllocations;
        private string performanceObservedHarvestMapRequest;
        private bool performanceHarvestReceipt;
        private string performanceHarvestRequest;
        private PerformanceObservation performanceRejectedObservation;
        private string performanceBuildEffectId, performanceBuildInstance, performanceGameId;
        private readonly Dictionary<string, PerformanceCommandRecord> performanceCommandRecords = new Dictionary<string, PerformanceCommandRecord>();
        private readonly HashSet<GamePhase> performanceTraceSegments = new HashSet<GamePhase>();
        private sealed class PerformanceCommandRecord
        {
            public GameCommand Command;
            public WorkflowSubmissionResult Result;
            public bool ResolutionSeen;
            public int BeforeRevision, AfterRevision = -1, LocalPlayerAfter;
            public PerformanceObservation Observation;
            public int InputIndex, ReceiptFrame;
            public string Category, RequestType, OwnerEffect;
            public long SubmitTick, ReceiptTick, FrameObservedTick, SpecificVisibleTick;
        }
        private sealed class PerformanceRoomPlan
        {
            public int Players;
            public string Mode, Map;
            public string[] Expansions;
        }
        private static PerformanceRoomPlan ParsePerformanceRoomPlan(string players, string mode, string expansions, string map)
        {
            if (!int.TryParse(players ?? "4", System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int count) || count < 2 || count > 4)
                throw new ArgumentException("性能人数参数必须为2、3或4");
            mode = (mode ?? "standard").Trim().ToLowerInvariant();
            if (mode != "standard" && mode != "entrepreneur") throw new ArgumentException("性能规则模式不是正式已知模式：" + mode);
            var ids = (expansions ?? "core").Split(',').Select(value => value.Trim()).ToArray();
            if (ids.Length == 0 || ids.Any(value => value != "core" && value != "enterprise" && value != "infected") ||
                ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || !ids.Contains("core"))
                throw new ArgumentException("扩展参数须包含core且只能列出不重复的正式core/enterprise/infected ID");
            if (ids.Contains("infected") && !ids.Contains("enterprise") || mode == "entrepreneur" && (!ids.Contains("enterprise") || !ids.Contains("infected")))
                throw new ArgumentException("所选模式/扩展缺少正式依赖，不能自动补齐或改为基础模式");
            map = map ?? (count == 4 ? "map-four-players" : "map-three-players");
            if (map != "map-three-players" && map != "map-four-players" || count == 4 && map != "map-four-players")
                throw new ArgumentException("地图参数不符合正式人数约束：" + map);
            return new PerformanceRoomPlan { Players = count, Mode = mode, Map = map, Expansions = ids.OrderBy(value => value, StringComparer.Ordinal).ToArray() };
        }
        private static string[] ParsePerformanceSnapshotBoundaries(string supplied)
        {
            if (string.IsNullOrEmpty(supplied)) return Array.Empty<string>();
            var values = supplied.Split(',').Select(value => value.Trim()).ToArray();
            if (values.Length > 3 || values.Distinct(StringComparer.Ordinal).Count() != values.Length ||
                values.Any(value => value != "initial" && value != "after-operation-idle" && value != "after-menu-idle"))
                throw new ArgumentException("快照仅允许三个不重复稳定边界initial/after-operation-idle/after-menu-idle");
            return values;
        }

        private IEnumerator PerformanceConfigureRoom(StartMenuRoomPanelView room)
        {
            var plan = performanceRoomPlan;
            var modes = (StartMenuRoomPanelView.ContentOption[])Read(room, "ruleModes");
            var expansions = (StartMenuRoomPanelView.ContentOption[])Read(room, "expansions");
            var maps = (StartMenuRoomPanelView.ContentOption[])Read(room, "maps");
            var seats = (StartMenuRoomPanelView.SeatBinding[])Read(room, "seats");
            PerformanceRecord("room.actual_options", new { Modes = modes.Select(value => value.Id).ToArray(),
                Expansions = expansions.Select(value => value.Id).ToArray(), Maps = maps.Select(value => value.Id).ToArray(), Seats = seats.Length, Requested = plan });
            Require(modes.Count(value => value.Id == plan.Mode) == 1 && maps.Count(value => value.Id == plan.Map) == 1 &&
                plan.Expansions.All(id => expansions.Count(value => value.Id == id) == 1), "当前真实房间UI缺少请求配置，保原选项不伪造");
            Require((performanceGameOrdinal == 1 || room.DisplayedRoom.PlayerCount == 1) && seats.Length >= plan.Players, "新热座房间初始席位不符，不能替换房间状态");
            if (performanceGameOrdinal == 1)
                for (int seat = seats.Length - 1; seat >= 1 && room.DisplayedRoom.PlayerCount > plan.Players; seat--)
                    if (PerformanceReady(seats[seat].RemoveButton))
                    {
                        int expected = room.DisplayedRoom.PlayerCount - 1;
                        yield return PerformanceRoomControl(room, seats[seat].RemoveButton, "再次建房原移除席位按钮", () => room.DisplayedRoom.PlayerCount == expected);
                    }
            for (int seat = room.DisplayedRoom.PlayerCount; seat < plan.Players; seat++)
            {
                int expected = seat + 1;
                yield return PerformanceRoomControl(room, seats[seat].HotSeatButton, "原房间添加热座" + expected,
                    () => room.DisplayedRoom.PlayerCount == expected);
            }
            var mode = modes.Single(value => value.Id == plan.Mode);
            if (plan.Mode == "standard" && room.DisplayedRoom.Configuration.RuleModeId != plan.Mode)
                yield return PerformanceRoomControl(room, mode.Button, "原房间选择模式" + plan.Mode, () => room.DisplayedRoom.Configuration.RuleModeId == plan.Mode);
            foreach (string id in new[] { "enterprise", "infected" })
            {
                var option = expansions.SingleOrDefault(value => value.Id == id);
                if (plan.Expansions.Contains(id) && !room.DisplayedRoom.Configuration.EnabledExpansionIds.Contains(id))
                    yield return PerformanceRoomControl(room, option?.Button, "原房间启用扩展" + id, () => room.DisplayedRoom.Configuration.EnabledExpansionIds.Contains(id));
            }
            foreach (string id in new[] { "infected", "enterprise" })
            {
                var option = expansions.SingleOrDefault(value => value.Id == id);
                if (!plan.Expansions.Contains(id) && room.DisplayedRoom.Configuration.EnabledExpansionIds.Contains(id))
                    yield return PerformanceRoomControl(room, option?.Button, "原房间关闭扩展" + id, () => !room.DisplayedRoom.Configuration.EnabledExpansionIds.Contains(id));
            }
            if (room.DisplayedRoom.Configuration.RuleModeId != plan.Mode)
                yield return PerformanceRoomControl(room, mode.Button, "原房间选择模式" + plan.Mode, () => room.DisplayedRoom.Configuration.RuleModeId == plan.Mode);
            var map = maps.Single(value => value.Id == plan.Map);
            if (room.DisplayedRoom.Configuration.MapId != plan.Map)
                yield return PerformanceRoomControl(room, map.Button, "原房间选择地图" + plan.Map, () => room.DisplayedRoom.Configuration.MapId == plan.Map);
            var actual = room.DisplayedRoom;
            Require(actual.PlayerCount == plan.Players && actual.Configuration.RuleModeId == plan.Mode && actual.Configuration.MapId == plan.Map &&
                actual.Configuration.EnabledExpansionIds.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(plan.Expansions), "原房间实际配置未与请求一致");
            PerformanceRecord("room.configuration", actual.Configuration);
            Require(room.CanStartCurrentRoom && PerformanceReady(room.StartButton), "正式房间尚不能开始：" + room.StatusText.text);
        }
        private IEnumerator PerformanceRoomControl(StartMenuRoomPanelView room, Button button, string description, Func<bool> accepted)
        {
            yield return Press(button, description);
            float began = Time.realtimeSinceStartup;
            while ((bool)Read(room, "requestPending") && Time.realtimeSinceStartup - began < 30f) yield return null;
            PerformanceRecord("room.ui_result", new { Description = description, Accepted = accepted(), room.DisplayedRoom.Configuration,
                Players = room.DisplayedRoom.PlayerCount, Status = room.StatusText.text });
            Require(accepted(), description + "被正式房间拒绝：" + room.StatusText.text);
        }
        // 只保留最近一次准确过期拒绝的轻量UI身份，不保留规则图、答案或旧控件。
        private sealed class PerformanceObservation
        {
            public string GameId, RequestId, CandidateSetId;
            public long Epoch, ObservedSequence;
            public int Membership, Actor, Revision, RequestRevision, CandidateVersion;
            public bool Same(PerformanceObservation other) => other != null && GameId == other.GameId &&
                Epoch == other.Epoch && Membership == other.Membership && Actor == other.Actor &&
                RequestId == other.RequestId && Revision == other.Revision && RequestRevision == other.RequestRevision &&
                ObservedSequence == other.ObservedSequence && CandidateSetId == other.CandidateSetId && CandidateVersion == other.CandidateVersion;
        }
        private PerformanceObservation PerformanceReadObservation()
        {
            if (!NetworkMode || !NetworkTryReadPublication(out var publication)) return null;
            var request = Request;
            return new PerformanceObservation { GameId = publication.GameId, Epoch = publication.SimulationEpoch,
                Membership = publication.MembershipVersion, Actor = LocalPlayer, ObservedSequence = publication.LastAppliedSequence,
                Revision = session.View.StateRevision, RequestId = request?.InteractionId, RequestRevision = request?.StateRevision ?? -1,
                CandidateSetId = request?.CandidateSetId, CandidateVersion = request?.CandidateSetVersion ?? -1 };
        }
        private bool PerformanceWaitingForRefresh()
        {
            if (performanceRejectedObservation == null) return false;
            var current = PerformanceReadObservation();
            if (current == null || performanceRejectedObservation.Same(current)) return true;
            PerformanceRecord("driver.refresh_observation_changed", new { Rejected = performanceRejectedObservation, Current = current });
            performanceRejectedObservation = null;
            return false;
        }
        private bool PerformanceIdle => Argument("--yc-ui-performance-stage=") == "idle";
        private static bool PerformanceReady(Button value) => value != null && value.IsActive() && value.IsInteractable();
        private static long? PerformanceMemoryBytes(long value) => value > 0 ? value : (long?)null;

        private object PerformanceLocalCollectionCounts()
        {
            // 只在热座主线程稳定边界读Count；联机不读取worker的活动State，不复制规则图。
            if (!performanceMemoryDetail || NetworkMode || performanceLeavingMenuFlow || session == null ||
                session.ReadSaveCaptureSafety() != SaveCaptureSafety.Safe) return null;
            var state = session.State; var runtime = state.EffectRuntime;
            return new { OwnerThread = System.Threading.Thread.CurrentThread.ManagedThreadId, state.GameId,
                Revision = runtime.StateRevision, Phase = state.Phase.ToString(), state.Round,
                EffectNodes = runtime.EffectNodes.Count, Requests = runtime.InteractionRequests.Count,
                Journal = runtime.Journal.Count, RuleEvents = runtime.RuleEvents.Count, Receipts = runtime.DispatchReceipts.Count,
                Items = state.ItemZones.Items.Count, Zones = state.ItemZones.Zones.Count,
                DriverCommandRecords = performanceCommandRecords.Count, DriverObservedCommands = commands.Count };
        }

        private object PerformanceMemoryPoint()
        {
            performanceProcess.Refresh();
            return new { Private = PerformanceMemoryBytes(performanceProcess.PrivateMemorySize64), WorkingSet = PerformanceMemoryBytes(performanceProcess.WorkingSet64),
                Managed = GC.GetTotalMemory(false), MonoUsed = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong(), MonoHeap = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong(),
                UnityAllocated = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(), UnityReserved = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong(),
                Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2), Counts = PerformanceLocalCollectionCounts(),
                SaveOwner = PerformanceSaveState(), Attribution = performanceMemoryDetail ? PerformanceMemoryAttribution() : null,
                DriverReferencesReleased = performanceDriverReferencesReleased };
        }
        private static object PerformanceProfilerScalar(Func<long> getter)
        {
            long began = Clock.GetTimestamp(); long? raw = null; string error = null;
            try { raw = getter(); } catch (Exception failure) { error = failure.GetType().FullName; }
            return new { RawBytes = raw, PositiveBytes = raw > 0 ? raw : null,
                Status = error ?? (raw > 0 ? "positive_observed" : "nonpositive_capability_unconfirmed"),
                BeginTick = began, EndTick = Clock.GetTimestamp(), DevelopmentBuild = Debug.isDebugBuild };
        }
        private static object PerformanceMemoryAttribution()
        {
            // 原Unity主线程低频读取；不保存资源、Controller或Store引用，不枚举业务缓存。
            long began = Clock.GetTimestamp();
            var unused = PerformanceProfilerScalar(UnityEngine.Profiling.Profiler.GetTotalUnusedReservedMemoryLong);
            var graphics = PerformanceProfilerScalar(UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver);
            long artworkBegin = Clock.GetTimestamp(); var artwork = ExternalContentRuntime.ReadMemoryDiagnostics();
            long artworkEnd = Clock.GetTimestamp(); var icons = GameplayIconResourceResolver.ReadMemoryDiagnostics();
            long iconsEnd = Clock.GetTimestamp();
            var controller = MatchSaveController.Instance;
            var store = controller == null ? null : Read(controller, "store") as YC.Infrastructure.Persistence.MatchSaveStore;
            var sizes = store == null ? (YC.Infrastructure.Persistence.MatchSaveSizeStatistics?)null : store.ReadSizeStatistics();
            long ended = Clock.GetTimestamp();
            return new { BeginTick = began, EndTick = ended, GetterMilliseconds = (ended - began) * 1000d / Clock.Frequency,
                OwnerThread = System.Threading.Thread.CurrentThread.ManagedThreadId, UnityUnused = unused, GraphicsDriver = graphics,
                Artwork = artwork, Icons = icons, ArtworkBeginTick = artworkBegin, ArtworkEndTick = artworkEnd,
                IconsEndTick = iconsEnd, SaveSizeEndTick = ended, SaveSize = sizes,
                StoreIdentity = store == null ? (int?)null : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(store),
                SaveSequenceBelongsToStoreNotCurrentGame = true, ResourceDecodeMeasurementCostIncluded = false };
        }
        private void PerformanceObserveResultFrames()
        {
            if (mobile == null || hud == null || !mobile.isActiveAndEnabled || !hud.isActiveAndEnabled || SceneTransitionContext.IsTransitionInProgress) return;
            var view = session?.View;
            if (view == null || view.GameId != performanceGameId) return;
            foreach (var row in performanceCommandRecords.Values)
            {
                if (row.ReceiptTick == 0 || row.FrameObservedTick != 0 || Time.frameCount <= row.ReceiptFrame || row.AfterRevision > view.StateRevision) continue;
                row.FrameObservedTick = Clock.GetTimestamp();
                PerformanceRecord("command.result_frame_observed", new { row.Command.CommandId, row.Category, row.InputIndex, row.ReceiptTick,
                    row.ReceiptFrame, ObservedFrame = Time.frameCount, InstalledRevision = view.StateRevision, RequestId = Request?.InteractionId,
                    RequestRevision = Request?.StateRevision, HudActive = true, SemanticResultVerified = false, GpuFirstPixelClaimed = false });
            }
        }
        private void PerformanceSpecificBuildResult()
        {
            foreach (var row in performanceCommandRecords.Values)
                if (row.ReceiptTick != 0 && row.Result?.CommandResult?.Succeeded == true && row.SpecificVisibleTick == 0 && row.OwnerEffect == performanceBuildEffectId &&
                    row.RequestType == YC.Domain.Effects.FacilityBuildEffectExecutor.PlacementInteractionType)
                {
                    row.SpecificVisibleTick = Clock.GetTimestamp();
                    PerformanceRecord("command.specific_result_visible", new { row.Command.CommandId, row.Category, row.InputIndex,
                        row.ReceiptTick, Instance = performanceBuildInstance, Effect = performanceBuildEffectId,
                        ResultEvidence = "同一建设实例Completed及主城实际RawImage绑定", GpuFirstPixelClaimed = false });
                }
        }

        private void PerformanceOnSubmitAllocation(ExecutionTimingCounters.SubmitAllocationObservation sample)
        {
            if (sample.Depth == 1)
            {
                performanceOuterSubmitAllocationCalls++;
                if (sample.AllocatedBytes.HasValue) performanceOuterSubmitAllocated += sample.AllocatedBytes.Value;
                else performanceMissingSubmitAllocations++;
            }
            PerformanceRecord("allocation.submit.owner", new { Sample = sample,
                CumulativeOuterSubmitAllocated = performanceOuterSubmitAllocationCalls > performanceMissingSubmitAllocations ? (long?)performanceOuterSubmitAllocated : null,
                OuterCalls = performanceOuterSubmitAllocationCalls, Missing = performanceMissingSubmitAllocations,
                IncludesNestedSubmit = true, IncludesPostSubmitUi = false });
        }

        private void PerformanceObserveHarvestMap()
        {
            if (!performanceHarvestReadyMetrics || State.Phase != GamePhase.ResourceCollection || Route?.IsActive != true ||
                Request == null) return;
            if (Request.InteractionId != performanceObservedHarvestMapRequest)
            {
                long began = Clock.GetTimestamp();
                var camera = Camera.main;
                var visible = camera == null ? null : FindObjectsOfType<MapButtonView>().FirstOrDefault(value =>
                    value.gameObject.activeInHierarchy && value.Image != null && value.Image.enabled && value.Image.isVisible &&
                    value.HitArea != null && TabletopPointerClassifier.CanRouteMapPointer(camera.WorldToScreenPoint(value.HitArea.bounds.center)));
                performanceHarvestObservationTicks += Clock.GetTimestamp() - began;
                if (visible != null)
                {
                    performanceObservedHarvestMapRequest = Request.InteractionId;
                    PerformanceRecord("harvest.map_visible", new { RequestId = Request.InteractionId, RequestType = Request.InteractionTypeId,
                        visible.TargetId, Kind = visible.TargetKind.ToString(), SpriteVisible = true, UncoveredMapPointer = true,
                        RouteCanConfirm = Route.CanConfirm, GpuFirstPixelClaimed = false });
                }
            }
            int flags = PerformanceButtonFlags(hud?.Frame?.EndActionButton) | PerformanceButtonFlags(hud?.ActionPanelView?.EndRoundButton) << 4;
            if (Request.InteractionId != performanceObservedHarvestReadyRequest && Route.CanConfirm && flags == 255)
            {
                performanceObservedHarvestReadyRequest = Request.InteractionId;
                PerformanceRecord("harvest.actual_ready", new { RequestId = Request.InteractionId, ButtonFlags = flags, RouteCanConfirm = true,
                    GateSource = "原已绑定按钮属性与当前Route.CanConfirm", GateReevaluated = false, GpuFirstPixelClaimed = false });
            }
        }
        private static int PerformanceButtonFlags(Button button) => button == null ? 0 :
            (button.gameObject.activeInHierarchy ? 1 : 0) | (button.interactable ? 2 : 0) | (button.IsInteractable() ? 4 : 0) | (button.enabled ? 8 : 0);

        private IEnumerator PerformanceCaptureMemoryOnce() => PerformanceCaptureMemory("submit150");
        private IEnumerator PerformanceSnapshotBoundary(string boundary)
        {
            if (!performanceSnapshotBoundaries.Contains(boundary)) yield break;
            Require(performancePending.Count == 0 && (performanceDriverReferencesReleased || session?.ReadSaveCaptureSafety() == SaveCaptureSafety.Safe),
                "请求快照时规则仍未到正式安全边界");
            yield return PerformanceCaptureMemory(boundary);
        }
        private IEnumerator PerformanceCaptureMemory(string boundary)
        {
            Require(performanceCapturedBoundaries.Count < 3 && performanceCapturedBoundaries.Add(boundary), "快照边界重复或超过三份有界上限");
            performanceSnapshotAttempted = true;
            string path = Path.Combine(output, boundary == "submit150" ? "正式提交150后稳定点.snap" : "稳定边界-" + boundary + ".snap");
            long diskFree = new DriveInfo(Path.GetPathRoot(path)).AvailableFreeSpace;
            if (diskFree < 6L * 1024 * 1024 * 1024) { PerformanceRecord("memory.snapshot.skipped_space", new { Boundary = boundary, AvailableBytes = diskFree }); yield break; }
            long began = Clock.GetTimestamp(); float realBegan = Time.realtimeSinceStartup;
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            PerformanceRecord("memory.snapshot.begin", new { Path = path, Inputs = performanceInputs, Submissions = performanceSubmissions,
                Boundary = boundary, CaptureFlags = 7, Before = PerformanceMemoryPoint(), Counts = PerformanceLocalCollectionCounts(), RequestId = mobile == null ? null : Request?.InteractionId,
                Managed = GC.GetTotalMemory(false), MonoHeap = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong(), Gen0 = g0, Gen1 = g1, Gen2 = g2 });
            performanceLog.Flush();
            bool done = false, success = false; string error = null;
            try
            {
                if (File.Exists(path)) throw new IOException("快照路径已存在，禁止覆盖");
                Unity.Profiling.Memory.MemoryProfiler.TakeSnapshot(path, (saved, ok) => { success = ok; done = true; },
                    Unity.Profiling.Memory.CaptureFlags.ManagedObjects | Unity.Profiling.Memory.CaptureFlags.NativeObjects | Unity.Profiling.Memory.CaptureFlags.NativeAllocations);
            }
            catch (Exception failure) { error = failure.ToString(); done = true; }
            while (!done && Time.realtimeSinceStartup - realBegan < 120f) yield return new WaitForSecondsRealtime(.1f);
            float pause = Time.realtimeSinceStartup - realBegan;
            performanceSnapshotPauseSeconds += pause;
            PerformanceRecord("memory.snapshot.end", new { Path = path, Done = done, Success = success, Error = error,
                Ticks = Clock.GetTimestamp() - began, PauseSeconds = pause, ExcludedFromOperationBudget = true,
                Bytes = File.Exists(path) ? new FileInfo(path).Length : (long?)null, Boundary = boundary, After = PerformanceMemoryPoint(), Counts = PerformanceLocalCollectionCounts(),
                Managed = GC.GetTotalMemory(false), MonoHeap = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong(),
                Gen0Before = g0, Gen1Before = g1, Gen2Before = g2, Gen0After = GC.CollectionCount(0), Gen1After = GC.CollectionCount(1), Gen2After = GC.CollectionCount(2),
                ExplicitGcCollectCalled = false, ParserInstalledClaimed = false });
            performanceLog.Flush();
            Write("快照-" + boundary + "-回调.json", new { Boundary = boundary, Done = done, Success = success, Error = error,
                Path = path, CaptureFlags = 7, AvailableBytesBefore = diskFree, Utc = DateTime.UtcNow.ToString("o"), Tick = Clock.GetTimestamp(), Frequency = Clock.Frequency,
                Frame = Time.frameCount, Pid = performanceProcess.Id, PreviousGameId = performanceGameId,
                CurrentGameId = performanceDriverReferencesReleased ? null : session?.View?.GameId,
                SaveOwner = PerformanceSaveState(), DriverReferencesReleased = performanceDriverReferencesReleased,
                UnityVersion = UnityEngine.Application.unityVersion, SnapshotSessionGuid = (string)null,
                SessionGuidSource = "保留原snap内嵌身份，待018官方元数据读取；本轮不启动分析Editor", PauseSeconds = pause });
            float regionWait = Time.realtimeSinceStartup;
            while (!File.Exists(Path.Combine(output, "区域-" + boundary + "-完成.json")) && Time.realtimeSinceStartup - regionWait < 30f)
                yield return new WaitForSecondsRealtime(.1f);
            PerformanceRecord("memory.regions.handoff", new { Boundary = boundary, WaitSeconds = Time.realtimeSinceStartup - regionWait,
                Acknowledged = File.Exists(Path.Combine(output, "区域-" + boundary + "-完成.json")), PartOfNormalLatency = false });
        }

        private void PerformanceRecord(string name, object detail = null)
        {
            if (performanceLog == null || performanceRows >= performanceRowLimit) return;
            long began = Clock.GetTimestamp();
            if (name == "ui.event.begin" || name == "ui.map_event.begin" || name == "ui.escape_event.begin")
            { performanceLastInputTick = began; performanceLastInputUtc = DateTime.UtcNow.ToString("o"); performanceLastInputName = name; }
            var view = session?.View;
            var localState = !NetworkMode && mobile != null && !performanceLeavingMenuFlow ? session?.State : null;
            SimulationPublishedSnapshot publication = null;
            networkTransport?.SimulationCoordinator?.TryReadPublishedSnapshot(out publication);
            performanceLog.WriteLine(JsonConvert.SerializeObject(new {
                Name = name, Utc = DateTime.UtcNow.ToString("o"), Tick = began, Frequency = Clock.Frequency,
                Frame = Time.frameCount, Stage = stage, Pid = performanceProcess.Id,
                GameId = view?.GameId ?? localState?.GameId ?? performanceGameId,
                GameOrdinal = performanceGameOrdinal, CurrentGameId = performanceDriverReferencesReleased ? null : view?.GameId ?? localState?.GameId, PreviousGameId = performanceGameId, SaveOwner = PerformanceSaveState(),
                Revision = view?.StateRevision ?? localState?.EffectRuntime.StateRevision ?? -1, Player = mobile == null ? -1 : LocalPlayer,
                Sequence = publication?.LastAppliedSequence ?? -1, Validated = publication?.ValidatedThroughSequence ?? -1,
                Grant = publication?.WindowExecution?.GrantId,
                AwaitingGrant = publication?.WindowExecution?.AwaitingGrant,
                BoundaryPending = publication?.WindowExecution?.BoundaryPending, Detail = detail
            }));
            performanceRows++;
            performanceWriteTicks += Clock.GetTimestamp() - began;
        }

        private void Update()
        {
            if (!PerformanceMode || finished || performanceLog == null) return;
            // 只累计标量，不逐帧写日志；内部ExecuteEvents不会伪造成物理按键输入。
            if (performanceMemoryComparison)
            {
                if (!UnityEngine.Application.isFocused) performanceUnfocusedFrames++;
                else if (Input.anyKey || Input.mouseScrollDelta.sqrMagnitude > 0 ||
                    performanceMousePositionObserved && Input.mousePosition != performancePreviousMousePosition) performanceExternalInputFrames++;
                performancePreviousMousePosition = Input.mousePosition;
                performanceMousePositionObserved = UnityEngine.Application.isFocused;
            }
            if (performanceLifetimeMode)
            {
                PerformanceDetectNaturalGc();
                if (performanceWeakSettled && !performanceWeakAfterGc && performanceNaturalGcDetectedTick != 0 && Time.realtimeSinceStartup >= performanceNextWeakPoll)
                {
                    performanceNextWeakPoll = Time.realtimeSinceStartup + 1f;
                    if (PerformanceSaveState().AllWorkSettled == true) PerformanceWeakRecordAfterGc();
                }
            }
            if (Time.realtimeSinceStartup < performanceNextPulse) return;
            performanceNextPulse = Time.realtimeSinceStartup + performancePulseSeconds;
            PerformanceMemoryPulse("memory.pulse");
        }
        private void PerformanceMemoryPulse(string name)
        {
            long began = Clock.GetTimestamp();
            PerformanceObserveSettled();
            double? stoppedSeconds = performanceStopInputTick == 0 ? (double?)null : (began - performanceStopInputTick) / (double)Clock.Frequency;
            var targets = new List<int>(1);
            while (performanceStopCheckpoint < performanceStopTargets.Length && stoppedSeconds >= performanceStopTargets[performanceStopCheckpoint])
                targets.Add(performanceStopTargets[performanceStopCheckpoint++]);
            performanceProcess.Refresh();
            PerformanceRecord(name, new {
                StopInputTick = performanceStopInputTick, StopInputUtc = performanceStopInputUtc, SecondsSinceStop = stoppedSeconds,
                StopTargetsDue = targets.ToArray(), ObservationTick = began, NominalPulseSeconds = performancePulseSeconds,
                LastInputTick = performanceLastInputTick, LastInputUtc = performanceLastInputUtc,
                FirstSaveSettledTick = performanceFirstSettledTick,
                WorkingSet = PerformanceMemoryBytes(performanceProcess.WorkingSet64), Private = PerformanceMemoryBytes(performanceProcess.PrivateMemorySize64),
                CpuMilliseconds = performanceProcess.TotalProcessorTime.TotalMilliseconds,
                Managed = GC.GetTotalMemory(false), Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2),
                UnityAllocated = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(),
                UnityReserved = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong(),
                MonoHeap = PerformanceMemoryBytes(UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong()),
                MonoUsed = PerformanceMemoryBytes(UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong()),
                Inputs = performanceInputs, Submissions = performanceSubmissions, SuccessfulSubmissions = performanceSuccessfulSubmissions, StableOwnerCounts = PerformanceLocalCollectionCounts(),
                ExternalInputFrames = performanceExternalInputFrames, UnfocusedFrames = performanceUnfocusedFrames,
                SaveOwner = PerformanceSaveState(), Attribution = performanceMemoryDetail ? PerformanceMemoryAttribution() : null,
                DriverReferencesReleased = performanceDriverReferencesReleased,
                CumulativeOuterSubmitAllocated = performanceMemoryDetail && performanceOuterSubmitAllocationCalls > performanceMissingSubmitAllocations ? (long?)performanceOuterSubmitAllocated : null,
                OuterSubmitAllocationCalls = performanceOuterSubmitAllocationCalls, MissingSubmitAllocations = performanceMissingSubmitAllocations,
                Focused = UnityEngine.Application.isFocused, UnityEngine.Application.runInBackground,
                UnityEngine.Application.targetFrameRate, QualitySettings.vSyncCount, Width = Screen.width, Height = Screen.height,
                DriverRows = performanceRows, DriverInputs = performanceInputs, DriverPending = performancePending.Count
            });
            performanceLog.Flush();
            performancePulseTicks += Clock.GetTimestamp() - began;
        }

        private IEnumerator PerformanceWait(Func<bool> condition, string description, float seconds = 120f)
        {
            long began = Clock.GetTimestamp();
            PerformanceRecord("driver.wait.begin", description);
            while (!condition())
            {
                Require(string.IsNullOrEmpty(performanceProtocolFault), "真实联机协议故障：" + performanceProtocolFault);
                if ((Clock.GetTimestamp() - began) / (double)Clock.Frequency > seconds)
                {
                    PerformanceFirstStall(description);
                    throw new DriverContinuationPendingException(description + "：实际条件仍未成立；保留原请求和原按钮，不重发、不代答");
                }
                yield return new WaitForSecondsRealtime(.1f);
            }
            PerformanceRecord("driver.wait.end", new { Description = description, Ticks = Clock.GetTimestamp() - began,
                PollSeconds = .1, ProductLatencyClaimed = false });
        }

        private IEnumerator PerformancePause(string label, float seconds)
        {
            stage = label;
            int inputsBefore = performanceInputs, submissionsBefore = performanceSubmissions, physicalBefore = performanceExternalInputFrames, unfocusedBefore = performanceUnfocusedFrames;
            long began = Clock.GetTimestamp();
            PerformanceRecord("sample.begin", new { Label = label, Seconds = seconds, Inputs = performanceInputs,
                Submissions = performanceSubmissions, RequestId = mobile == null ? null : Request?.InteractionId,
                RequestType = mobile == null ? null : Request?.InteractionTypeId,
                Phase = mobile == null ? null : State.Phase.ToString(), Round = mobile == null ? -1 : State.Round,
                ExternalInputFrames = performanceExternalInputFrames, UnfocusedFrames = performanceUnfocusedFrames,
                SaveOwner = PerformanceSaveState() });
            yield return new WaitForSecondsRealtime(seconds);
            PerformanceRecord("sample.end", new { Label = label, Seconds = seconds, Inputs = performanceInputs,
                Submissions = performanceSubmissions, RequestId = mobile == null ? null : Request?.InteractionId,
                RequestType = mobile == null ? null : Request?.InteractionTypeId,
                Phase = mobile == null ? null : State.Phase.ToString(), Round = mobile == null ? -1 : State.Round,
                ObservedTicks = Clock.GetTimestamp() - began,
                NoDriverInputVerified = inputsBefore == performanceInputs && submissionsBefore == performanceSubmissions,
                NoInputVerified = performanceMemoryComparison && unfocusedBefore != performanceUnfocusedFrames ? (bool?)null :
                    inputsBefore == performanceInputs && submissionsBefore == performanceSubmissions && physicalBefore == performanceExternalInputFrames,
                ExternalInputFrames = performanceExternalInputFrames, PhysicalInputObservationAvailable = performanceMemoryComparison,
                UnfocusedFrames = performanceUnfocusedFrames,
                InputVerificationScope = performanceMemoryComparison ? "驱动输入/提交计数及聚焦窗口的按键、滚轮与指针位移；失焦段未知" : "驱动输入/提交计数",
                SaveOwner = PerformanceSaveState() });
        }
        private void PerformanceFirstStall(string description)
        {
            if (performanceFirstStallRecorded) return;
            performanceFirstStallRecorded = true;
            PerformanceRecord("driver.first_stall", new { Description = description, performanceInputs, performanceSubmissions, ContinueIfLegallyReachable = true });
            try { SaveCapture("性能首次等待现场"); }
            catch (Exception error) { PerformanceRecord("driver.first_stall_capture_failed", error.ToString()); }
        }

        private bool performanceLifetimeMode;
        private int performanceGameOrdinal, performanceCatalogId;
        private string performanceFirstGameId;
        private bool performanceWeakSettled, performanceWeakAfterGc, performanceWeakSettledAttempted;
        private MatchSaveWeakSnapshot performanceSettledWeak;
        private long performanceNaturalGcDetectedTick;
        private int performanceNaturalGcDetectedCount;
        private float performanceWeakPauseSeconds, performanceNextWeakPoll;
        private bool performanceCatalogChangedChecked;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static MatchSaveWeakSnapshot PerformanceReadWeak()
        {
            var owner = MatchSaveController.Instance;
            return owner == null ? MatchSaveWeakSnapshot.Disabled(false, false, default) : owner.ReadSaveWeakObservation();
        }
        private void PerformanceDetectNaturalGc()
        {
            if (performanceGameOrdinal != 0 || !performanceWeakSettled || performanceNaturalGcDetectedTick != 0) return;
            int gc = GC.CollectionCount(0);
            if (gc <= performanceSettledWeak.ReadGc0After) return;
            performanceNaturalGcDetectedTick = Clock.GetTimestamp(); performanceNaturalGcDetectedCount = gc;
            PerformanceRecord("save.weak.natural_gc_detected", new { Baseline = performanceSettledWeak.ReadGc0After, Observed = gc,
                DetectedTick = performanceNaturalGcDetectedTick, DetectionIsNotExactCollectionTime = true });
        }
        private void PerformanceWeakRecord(string label)
        {
            var snapshot = PerformanceReadWeak();
            if (label == "save_weak_settled") { performanceSettledWeak = snapshot; performanceWeakSettled = true; }
            bool same = !performanceWeakSettled || snapshot.GameId == performanceSettledWeak.GameId && snapshot.OperationId == performanceSettledWeak.OperationId &&
                snapshot.SourceRevision == performanceSettledWeak.SourceRevision && snapshot.OldSlot == performanceSettledWeak.OldSlot &&
                snapshot.NewSlot == performanceSettledWeak.NewSlot && snapshot.OldValidatedPayloadChecksum == performanceSettledWeak.OldValidatedPayloadChecksum &&
                snapshot.NewPreparedPayloadChecksum == performanceSettledWeak.NewPreparedPayloadChecksum && snapshot.NewArchiveChars == performanceSettledWeak.NewArchiveChars &&
                snapshot.NewContinuationChars == performanceSettledWeak.NewContinuationChars && snapshot.OldArchiveChars == performanceSettledWeak.OldArchiveChars &&
                snapshot.OldContinuationChars == performanceSettledWeak.OldContinuationChars;
            PerformanceRecord(label, new { Snapshot = snapshot, Coverage = snapshot.Coverage.ToString(), SameSelectedIdentity = same,
                GameOrdinal = performanceGameOrdinal, Memory = PerformanceMemoryPoint(), SaveOwner = PerformanceSaveState(),
                ReadIsNonAtomic = snapshot.ReadGc0Before != snapshot.ReadGc0After, DetectionTick = performanceNaturalGcDetectedTick,
                DetectionGc0 = performanceNaturalGcDetectedCount, DelayAfterGcDetectionMs = performanceNaturalGcDetectedTick == 0 ? (double?)null :
                    (Clock.GetTimestamp() - performanceNaturalGcDetectedTick) * 1000d / Clock.Frequency,
                AdditionalGcSinceDetection = performanceNaturalGcDetectedTick == 0 ? (int?)null : snapshot.ReadGc0After - performanceNaturalGcDetectedCount });
            performanceLog.Flush();
        }
        private void PerformanceWeakRecordAfterGc()
        {
            if (performanceGameOrdinal != 0 || performanceWeakAfterGc || performanceNaturalGcDetectedTick == 0) return;
            PerformanceWeakRecord("save_weak_after_natural_gc"); performanceWeakAfterGc = true;
        }
        private IEnumerator PerformanceWeakBoundaryCheck()
        {
            if (performanceGameOrdinal != 0) yield break;
            PerformanceDetectNaturalGc();
            if (!performanceWeakSettled)
            {
                if (performanceWeakSettledAttempted) yield break;
                var snapshot = PerformanceReadWeak();
                if (!snapshot.Selected || snapshot.Coverage == MatchSaveWeakCoverage.InFlight) yield break;
                performanceWeakSettledAttempted = true;
                float began = Time.realtimeSinceStartup;
                yield return PerformanceAwaitSaveSettled("save_weak_settled");
                performanceWeakPauseSeconds += Time.realtimeSinceStartup - began;
                if (PerformanceSaveState().AllWorkSettled == true) PerformanceWeakRecord("save_weak_settled");
                else { PerformanceWeakRecord("save.weak.settled_unqualified"); yield break; }
            }
            PerformanceDetectNaturalGc();
            if (!performanceWeakAfterGc && performanceNaturalGcDetectedTick != 0)
            {
                float began = Time.realtimeSinceStartup;
                yield return PerformanceAwaitSaveSettled("save_weak_after_natural_gc");
                performanceWeakPauseSeconds += Time.realtimeSinceStartup - began;
                if (PerformanceSaveState().AllWorkSettled == true) PerformanceWeakRecordAfterGc();
                else PerformanceRecord("save.weak.after_gc_not_settled", new { SaveOwner = PerformanceSaveState() });
            }
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private object PerformanceCatalogAudit(bool menu, string boundary)
        {
            var catalog = menu ? Resources.FindObjectsOfTypeAll<CardVisualCatalog>().SingleOrDefault(value => value.GetInstanceID() == performanceCatalogId) : hud?.DialogRegistry?.CardVisualCatalog;
            if (catalog == null) return new { Boundary = boundary, Found = false, RequestedInstanceId = performanceCatalogId,
                Reason = "实际绑定catalog未载入或已卸载；不加载另一个源资产替代", GameOrdinal = performanceGameOrdinal };
            if (!menu) performanceCatalogId = catalog.GetInstanceID();
            var resolver = Read(catalog, "runtimeDefinitionResolver") as Delegate;
            var target = resolver?.Target;
            var fields = target == null ? Array.Empty<System.Reflection.FieldInfo>() : target.GetType().GetFields(Members);
            var shape = fields.Select(field => new { Name = field.Name, Type = field.FieldType.FullName,
                WeakMobile = field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(WeakReference<>) && field.FieldType.GetGenericArguments()[0] == typeof(MobileCityInteractionController),
                StrongMobileOrSession = typeof(MobileCityInteractionController).IsAssignableFrom(field.FieldType) || typeof(GameSession).IsAssignableFrom(field.FieldType) || field.Name == "<>4__this" }).ToArray();
            string input = null, expected = null, actual = null; int revision = -1, viewer = -1; string game = null;
            if (!menu && session != null)
            {
                var visible = CurrentPaymentView(); var own = visible?.Players.SingleOrDefault(value => value.PlayerId == visible.ViewerPlayerId);
                var ids = own == null ? Array.Empty<string>() : (own.PrivateCardIdsVisible ? own.HandCardIds : new List<string>()).Concat(own.BuiltFacilityIds).ToArray();
                foreach (string id in ids)
                {
                    string definition = GameContentDefinitions.ResolveDefinitionId(State, id);
                    if (definition == id) continue;
                    input = id; expected = definition; actual = resolver?.DynamicInvoke(id) as string; break;
                }
                game = visible?.GameId; revision = visible?.StateRevision ?? -1; viewer = visible?.ViewerPlayerId ?? -1;
            }
            return new { Boundary = boundary, Found = true, InstanceId = catalog.GetInstanceID(), CatalogName = catalog.name,
                ResolverTargetType = target?.GetType().FullName, CapturedFields = shape,
                OnlyIndependentWeakMobileCapture = shape.Length == 1 && shape[0].WeakMobile && !shape[0].StrongMobileOrSession,
                GameOrdinal = performanceGameOrdinal, GameId = game, Revision = revision, Viewer = viewer,
                InputVisibleId = input, ExpectedDefinitionId = expected, ActualDefinitionId = actual,
                DynamicMappingCovered = input != null && expected != input, DynamicMappingMatches = input == null ? (bool?)null : actual == expected,
                WeakTargetAliveNotRead = true, FullRootEnumerationClaimed = false };
        }
        private IEnumerator PerformanceRun()
        {
            performanceLifetimeMode = Argument("--yc-ui-performance-weak-lifetime=") == "true";
            performanceGameCount = ParsePerformanceGameCount(Argument("--yc-ui-performance-games="), performanceLifetimeMode);
            Require(!NetworkMode || performanceGameCount == 1, "同PID多局仅用于本地性能样本");
            for (performanceGameOrdinal = 0; performanceGameOrdinal < performanceGameCount; performanceGameOrdinal++)
                yield return PerformanceRunGame();
        }
        private IEnumerator PerformanceRunGame()
        {
            performanceOperationsElapsed = 0; performanceInputStepSeconds = 0; performanceNoInputSeconds = 0;
            performanceStopReason = null; performanceStopInputTick = 0; performanceFirstSettledTick = 0;
            performanceStopInputUtc = null; performanceStopCheckpoint = 0;
            performancePulseSeconds = PerformanceBudget("--yc-ui-performance-pulse-seconds=", 5, 1, 5);
            int seconds = performanceGameOrdinal == 1 ? 120 : PerformanceBudget("--yc-ui-performance-seconds=", NetworkMode ? 600 : 260, 30, 900);
            int inputLimit = PerformanceBudget("--yc-ui-performance-inputs=", 400, 100, 1600);
            int submissionLimit = performanceGameOrdinal == 1 ? 40 : PerformanceBudget("--yc-ui-performance-submissions=", 160, 40, 240);
            if (performanceGameOrdinal == 1)
            {
                performanceDriverReferencesReleased = false; performanceLeavingMenuFlow = false;
                performanceGameId = null; performanceLastFacts = null; performanceDraftClicks.Clear();
                performanceBuildInstance = null; performanceBuildEffectId = null; performanceBuiltBound = false;
                performanceQuickTabsDone = false; performanceHarvestSubmitted = false; performanceHarvestCompleted = false;
                performanceHarvestReceipt = false; performanceHarvestRequest = null; performanceHarvestSequence = -1;
                performanceObservedHarvestMapRequest = null; performanceObservedHarvestReadyRequest = null;
                performanceCatalogId = 0; performanceCatalogChangedChecked = false;
                performanceNormalMenuReturned = false;
            }
            int gameSuccessStart = performanceSuccessfulSubmissions, gameInputStart = performanceInputs;
            int gameSubmissionStart = performanceSubmissions;
            performanceRoomPlan = performanceGameOrdinal == 1 && Argument("--yc-ui-performance-repeat-configuration=") != "true" ? ParsePerformanceRoomPlan("3", "entrepreneur", "core,enterprise,infected", "map-three-players") : ParsePerformanceRoomPlan(Argument("--yc-ui-performance-players="), Argument("--yc-ui-rule-mode="),
                Argument("--yc-ui-performance-expansions="), Argument("--yc-ui-performance-map="));
            performanceMemoryComparison = Argument("--yc-ui-performance-memory-comparison=") == "true";
            int idleSeconds = PerformanceIdle ? PerformanceBudget("--yc-ui-performance-idle-seconds=", 180, 30, 1800) :
                PerformanceBudget("--yc-ui-performance-initial-idle-seconds=", performanceMemoryComparison ? 30 : 2, 0, 1800);
            if (performanceGameOrdinal == 1) idleSeconds = 2;
            int menuWaitSeconds = PerformanceIdle ? PerformanceBudget("--yc-ui-performance-idle-menu-seconds=", 180, 0, 180) : 2;
            performanceMenuIdleSeconds = PerformanceBudget("--yc-ui-performance-menu-idle-seconds=", performanceMemoryComparison ? 90 : 0, 0, 1800);
            performanceRowLimit = PerformanceBudget("--yc-ui-performance-rows=", 2048, 2048, 8192);
            performanceSustained = Argument("--yc-ui-performance-sustained=") == "true";
            performanceMemoryDetail = !NetworkMode && (performanceMemoryComparison || Argument("--yc-ui-performance-memory-detail=") == "true");
            performanceArtworkBindings = performanceMemoryDetail && Argument("--yc-ui-performance-artwork-bindings=") == "true";
            performanceArtworkEvidenceKeys.Clear();
            if (performanceMemoryDetail)
            {
                ExternalContentRuntime.SetMemoryDiagnosticsEnabled(true);
                GameplayIconResourceResolver.SetMemoryDiagnosticsEnabled(true);
            }
            performanceSnapshotRequested = performanceMemoryDetail && Argument("--yc-ui-performance-memory-snapshot=") == "true";
            foreach (string boundary in ParsePerformanceSnapshotBoundaries(Argument("--yc-ui-performance-snapshot-boundaries="))) performanceSnapshotBoundaries.Add(boundary);
            Require(!performanceSnapshotRequested || performanceSnapshotBoundaries.Count == 0, "旧150提交快照与稳定边界快照不能混用");
            Require(performanceSnapshotBoundaries.Count == 0 || performanceMemoryDetail && !NetworkMode, "稳定边界快照只准备本地显式内存诊断入口");
            Require(!NetworkMode || !performanceMemoryComparison && performanceMenuIdleSeconds == 0 && performanceRoomPlan.Players == 4 &&
                performanceRoomPlan.Mode == "standard" && performanceRoomPlan.Expansions.SequenceEqual(new[] { "core" }), "当前四端性能入口只保原四人基础配置，不能冒多人配置矩阵");
            performanceHarvestReadyMetrics = performanceMemoryComparison || Argument("--yc-ui-performance-harvest-ready-metrics=") == "true";
            if (performanceProcess == null) performanceProcess = System.Diagnostics.Process.GetCurrentProcess();
            if (performanceLog == null) performanceLog = new StreamWriter(new FileStream(Path.Combine(output, "低开销样本.jsonl"), FileMode.CreateNew,
                FileAccess.Write, FileShare.ReadWrite), new System.Text.UTF8Encoding(false));
            Write(performanceGameOrdinal == 0 ? "性能运行身份.json" : "性能运行身份-第二局.json", new { Pid = performanceProcess.Id, Arguments = Environment.GetCommandLineArgs(),
                MainMvid = GetType().Assembly.ManifestModule.ModuleVersionId, ClockFrequency = Clock.Frequency,
                Scene = SceneManager.GetActiveScene().name, PerformanceIdle, MaximumRows = performanceRowLimit, PulseSeconds = performancePulseSeconds,
                Games = performanceGameCount, GCMode = UnityEngine.Scripting.GarbageCollector.GCMode.ToString(),
                IncrementalGC = UnityEngine.Scripting.GarbageCollector.isIncremental,
                IncrementalTimeSliceNanoseconds = UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds,
                OperationSeconds = seconds, IdleSeconds = idleSeconds, MenuWaitSeconds = menuWaitSeconds,
                InputLimit = inputLimit, SubmissionLimit = submissionLimit, Sustained = performanceSustained,
                RequestedConfiguration = performanceRoomPlan, MemoryComparison = performanceMemoryComparison, MenuIdleSeconds = performanceMenuIdleSeconds,
                RequestedAutoSaveExperiment = Argument("--yc-perf-autosave=") ?? "default", SaveOwner = PerformanceSaveState(),
                SourceTreeIdentityFromRunner = Argument("--yc-ui-performance-source-tree="), PackageTreeIdentityFromRunner = Argument("--yc-ui-performance-package-tree="),
                GameOrdinal = performanceGameOrdinal, LifetimeMode = performanceLifetimeMode, DevelopmentBuild = Debug.isDebugBuild, SnapshotBoundaries = performanceSnapshotBoundaries.ToArray(),
                FullStateOutput = false, OriginalRouteBoundaryDigest = true,
                PerStepRestore = false, DriverFixedPoint65SecondDelay = false,
                ExternalMouse = false, OriginalButtonAndRaycastEvents = true, AutoSaveEffectivePolicy = PerformanceSaveState(),
                RunInBackgroundFromExistingDriver = UnityEngine.Application.runInBackground });
            PerformanceRecord("run.begin");
            yield return null;
            yield return new WaitForEndOfFrame();
            var menu = FindObjectOfType<StartMenuView>();
            Require(menu != null, "性能采样必须从正式主菜单开始");
            SaveCapture("性能正常主菜单");
            yield return PerformancePause("主菜单稳定等待", menuWaitSeconds);
            if (NetworkMode) yield return PerformanceNetworkRoom(menu);
            else
            {
                stage = "原房间控件配置性能局";
                yield return Press(menu.StartGameButton, "原主菜单建房");
                yield return PerformanceWait(() => menu.RoomPanel.gameObject.activeInHierarchy && menu.RoomPanel.DisplayedRoom != null, "正常房间出现");
                var room = menu.RoomPanel;
                yield return PerformanceConfigureRoom(room);
                yield return Press(room.StartButton, "原房间开始游戏");
            }
            yield return AwaitNormalGameplayEntry();
            session = (GameSession)Read(mobile, "session");
            registry = (YC.Domain.Effects.EffectRegistry)Read(mobile, "sessionEffectRegistry");
            hud = (GameplayInteractionHudView)Read(mobile, "gameplayInteractionHud");
            Require(session != null && hud != null && registry != null, "正常开局装配不完整");
            if (NetworkMode)
            {
                networkTransport = FindObjectOfType<MirrorCommandTransport>();
                Require(networkTransport != null, "正常联机缺少原传输");
                performanceSubmission = Read(mobile, "commandSubmission") as CommandSubmissionController;
                Require(performanceSubmission != null, "正常联机缺少原提交观察源");
                performanceSubmission.SubmissionResolutionObserved += PerformanceOnResolution;
                networkTransport.SimulationCommandApplied += PerformanceOnApplied;
                networkTransport.SimulationProtocolFaulted += PerformanceOnFault;
                yield return PerformanceWait(() => NetworkTryReadPublication(out _), "正常联机发表与本人界面就绪");
            }
            ObserveGateway(mobile);
            ObserveGateway(Read(mobile, "turnActionPresenter"));
            ObserveGateway(Read(mobile, "influenceActionPresenter"));
            PerformanceRecord("game.configuration", new { State.GameId, State.ContentPackHash,
                State.RuleConfiguration.RuleModeId, State.RuleConfiguration.EnabledExpansionIds, Players = State.Players.Count,
                State.MapId, Mode = GameLaunchContext.Instance.Mode.ToString(), MainMvid = GetType().Assembly.ManifestModule.ModuleVersionId,
                SourceTreeIdentityFromRunner = Argument("--yc-ui-performance-source-tree="), PackageTreeIdentityFromRunner = Argument("--yc-ui-performance-package-tree=") });
            Require(State.Players.Count == performanceRoomPlan.Players && State.RuleConfiguration.RuleModeId == performanceRoomPlan.Mode &&
                State.MapId == performanceRoomPlan.Map && State.RuleConfiguration.EnabledExpansionIds.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(performanceRoomPlan.Expansions),
                "开局实际配置与正常房间请求不一致，不能改配置对象补通过");
            performanceGameId = State.GameId;
            if (PerformanceLifecycleAudit)
            {
                if (performanceGameOrdinal == 0) performanceFirstGameId = performanceGameId;
                else Require(performanceGameId != performanceFirstGameId, "再次正常开局必须形成新GameId，不能拿旧存档代新局");
            }
            if (performanceMemoryDetail)
            {
                long probeDelta = -1;
                try
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    var probe = new byte[4096]; GC.KeepAlive(probe);
                    probeDelta = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch { }
                session.ExecutionTimings.CurrentThreadAllocationAvailable = probeDelta >= 4096;
                session.ExecutionTimings.SubmitAllocationObserved += PerformanceOnSubmitAllocation;
                session.ExecutionTimings.CaptureSubmitAllocations = true;
                PerformanceRecord("allocation.capability", new { ProbeDelta = probeDelta,
                    Available = session.ExecutionTimings.CurrentThreadAllocationAvailable, OwnerThread = System.Threading.Thread.CurrentThread.ManagedThreadId,
                    Scope = "原同步SubmitCore，不含返回后的UI回调；未知/不支持为null，父子不累加" });
            }
            if (PerformanceLifecycleAudit) PerformanceRecord("catalog.audit", PerformanceCatalogAudit(false, "active_initial"));
            SaveCapture("性能正常开局当前请求");
            yield return PerformanceAwaitSaveSettled("initial");
            yield return PerformancePause("局内稳定等待", idleSeconds);
            yield return PerformanceSnapshotBoundary("initial");
            if (PerformanceIdle) SaveCapture("局内静置结束原请求");
            if (!PerformanceIdle)
            {
                float actionsBegan = Time.realtimeSinceStartup;
                float snapshotPauseBeforeActions = performanceSnapshotPauseSeconds;
                float weakPauseBeforeActions = performanceWeakPauseSeconds;
                performanceLastProgress = actionsBegan;
                PerformanceRecord("operations.begin", new { Seconds = seconds, SubmissionLimit = submissionLimit, InputLimit = inputLimit });
                while (Time.realtimeSinceStartup - actionsBegan - (performanceSnapshotPauseSeconds - snapshotPauseBeforeActions) - (performanceWeakPauseSeconds - weakPauseBeforeActions) < seconds && (NetworkMode ? performanceSubmissions - gameSubmissionStart : performanceSuccessfulSubmissions - gameSuccessStart) < submissionLimit && performanceInputs - gameInputStart < inputLimit)
                {
                    if (!NetworkMode && State.Phase == GamePhase.GameOver)
                    {
                        performanceStopReason = "natural_game_over";
                        PerformanceRecord("operations.natural_game_over", new { State.GameId, State.Round, State.EffectRuntime.StateRevision });
                        break;
                    }
                    if (performanceSnapshotRequested && !performanceSnapshotAttempted && performanceSubmissions >= 150 &&
                        performancePending.Count == 0 && Request != null && PerformanceRequestIsCurrent(Request) &&
                        session.ReadSaveCaptureSafety() == SaveCaptureSafety.Safe)
                        yield return PerformanceCaptureMemoryOnce();
                    if (PerformanceLifecycleAudit)
                    {
                        if (performanceLifetimeMode) yield return PerformanceWeakBoundaryCheck();
                        if (!performanceCatalogChangedChecked && performanceSuccessfulSubmissions - gameSuccessStart >= 20)
                        {
                            PerformanceRecord("catalog.audit", PerformanceCatalogAudit(false, "active_changed"));
                            performanceCatalogChangedChecked = true;
                        }
                    }
                    float stepBegan = Time.realtimeSinceStartup;
                    if (NetworkMode && !NetworkTryReadPublication(out _))
                    { yield return new WaitForSecondsRealtime(.1f); performanceNoInputSeconds += Time.realtimeSinceStartup - stepBegan; continue; }
                    Require(string.IsNullOrEmpty(performanceProtocolFault), "真实协议故障：" + performanceProtocolFault);
                    Require(State.EffectRuntime.Status != EffectRuntimeStatus.PausedFault,
                        "真实规则故障：" + State.EffectRuntime.LastFaultCode + ":" + State.EffectRuntime.LastFaultMessage);
                    if ((State.Phase == GamePhase.ActionRound1 || State.Phase == GamePhase.ResourceCollection) &&
                        performanceTraceSegments.Count < 2 && performanceTraceSegments.Add(State.Phase) &&
                        Environment.GetCommandLineArgs().Contains("--yc-interaction-latency"))
                    {
                        // 有界短段复用原R61入口；不刷新状态，不修改产品输入或规则。
                        mobile.GetType().GetMethod("StopInteractionTrace", Members).Invoke(mobile, null);
                        mobile.GetType().GetMethod("StartInteractionTrace", Members).Invoke(mobile, null);
                        PerformanceRecord("diagnostic.segment", new { Phase = State.Phase.ToString(), MaximumRows = 8192, MaximumSeconds = 300 });
                    }
                    if (performanceHarvestSubmitted && performanceHarvestReceipt && performancePending.Count == 0 &&
                        Request?.InteractionId != performanceHarvestRequest &&
                        (!NetworkMode || NetworkTryReadPublication(out var harvestPublication) && harvestPublication.ValidatedThroughSequence >= performanceHarvestSequence))
                    {
                        if (!performanceHarvestCompleted)
                        {
                            performanceHarvestCompleted = true;
                            PerformanceRecord("harvest.following_ui", new { Sequence = performanceHarvestSequence });
                        }
                        if (NetworkMode) break;
                    }
                    if (!NetworkMode && !performanceSustained && performanceBuiltBound && performanceQuickTabsDone) break;
                    bool submitReady = !NetworkMode || performanceSubmission.CanSubmitForLocalPlayer;
                    PerformanceObserveResultFrames();
                    PerformanceObserveHarvestMap();
                    string facts = (Request?.InteractionId ?? "") + "|" + (Request?.InteractionTypeId ?? "") + "|" +
                        LocalPlayer + "|" + State.EffectRuntime.StateRevision + "|" +
                        (PerformanceReady(hud.Frame.EndActionButton) ? 1 : 0) + "|" + (Route?.IsActive == true ? 1 : 0) + "|" + submitReady;
                    if (facts != performanceLastFacts)
                    {
                        performanceLastFacts = facts;
                        PerformanceRecord("ui.facts.changed", new { RequestId = Request?.InteractionId, RequestType = Request?.InteractionTypeId,
                            Phase = State.Phase.ToString(), State.Round, EndReady = PerformanceReady(hud.Frame.EndActionButton),
                            SubmitReady = submitReady, AnsweringPlayer = Request?.AnsweringPlayerId, Visible = Request?.VisibleToViewer,
                            RouteActive = Route?.IsActive == true, RouteCanConfirm = Route?.IsActive == true && Route.CanConfirm });
                    }
                    if (!submitReady)
                    { yield return new WaitForSecondsRealtime(.1f); performanceNoInputSeconds += Time.realtimeSinceStartup - stepBegan; continue; }
                    int before = performanceInputs;
                    yield return new PerformanceStepBoundary(PerformanceStep());
                    if (performanceInputs > before)
                    { performanceLastProgress = Time.realtimeSinceStartup; performanceInputStepSeconds += Time.realtimeSinceStartup - stepBegan; }
                    else
                    {
                        yield return new WaitForSecondsRealtime(.1f);
                        performanceNoInputSeconds += Time.realtimeSinceStartup - stepBegan;
                    }
                }
                performanceOperationsElapsed = Time.realtimeSinceStartup - actionsBegan - (performanceSnapshotPauseSeconds - snapshotPauseBeforeActions) - (performanceWeakPauseSeconds - weakPauseBeforeActions);
                performanceStopReason = performanceStopReason ?? (performanceOperationsElapsed >= seconds ? "time_budget" : (NetworkMode ? performanceSubmissions - gameSubmissionStart : performanceSuccessfulSubmissions - gameSuccessStart) >= submissionLimit
                    ? "submission_budget" : performanceInputs - gameInputStart >= inputLimit ? "input_budget" : "target_completed");
                PerformanceStopInputs();
                yield return PerformanceWait(() => performancePending.Count == 0 && session.ReadSaveCaptureSafety() == SaveCaptureSafety.Safe, "预算后正式安全结算边界", 30f);
                if (performanceArtworkBindings) yield return PerformanceCaptureArtworkBindings(null);
                PerformanceRecord("operations.end", new { performanceHarvestCompleted, performanceHarvestSequence, performanceStopReason,
                    Elapsed = performanceOperationsElapsed, InputStepWallSeconds = performanceInputStepSeconds,
                    NoInputStepWallSeconds = performanceNoInputSeconds, SnapshotPauseSeconds = performanceSnapshotPauseSeconds, WeakDiagnosticPauseSeconds = performanceWeakPauseSeconds - weakPauseBeforeActions, SuccessfulSubmissions = performanceSuccessfulSubmissions - gameSuccessStart, GameInputs = performanceInputs - gameInputStart, InputIncludesReceiptWait = true });
                SaveCapture("性能短操作当前合法边界");
                if (NetworkMode)
                {
                    Write("采集本人结束.json", new { State.GameId, LocalPlayer, performanceHarvestCompleted, performanceHarvestSequence });
                    // 协调等待单列；不向产品提交任何额外命令，不导出或恢复全图。
                    string done = Path.Combine(networkRoot, "允许正常退出.txt");
                    if (networkRole == "host")
                    {
                        yield return PerformanceWait(() => networkRoles.All(role => File.Exists(Path.Combine(networkRoot, role, "采集本人结束.json"))), "四端短样本文件协调", 180f);
                        File.WriteAllText(done, "正常短样本完成");
                    }
                    else yield return PerformanceWait(() => File.Exists(done), "等待四端短样本协调结束", 180f);
                }
                else if (performanceMemoryDetail)
                {
                    int recoverySeconds = performanceGameOrdinal == 1 ? PerformanceBudget("--yc-ui-performance-second-recovery-seconds=", 30, 30, 1800) : PerformanceBudget("--yc-ui-performance-recovery-seconds=", performanceMemoryComparison ? 90 : 0, 0, 1800);
                    yield return PerformanceAwaitSaveSettled("after-operation-idle");
                    if (recoverySeconds > 0) yield return PerformancePause("有效操作后自然回收观察", recoverySeconds);
                    yield return PerformanceSnapshotBoundary("after-operation-idle");
                }
            }
            if (PerformanceLifecycleAudit)
            {
                if (performanceLifetimeMode)
                { yield return PerformanceWeakBoundaryCheck(); PerformanceWeakRecord("save_weak_post_idle"); }
                PerformanceRecord("catalog.audit", PerformanceCatalogAudit(false, "active_final"));
            }
            yield return PerformanceExitThroughMenu();
        }

        private int PerformanceBudget(string key, int fallback, int minimum, int maximum)
        {
            string value = Argument(key);
            if (value == null) return fallback;
            Require(int.TryParse(value, out int budget) && budget >= minimum && budget <= maximum, "性能预算参数越界：" + key);
            return budget;
        }
        private sealed class PerformanceStepBoundary : IEnumerator, IDisposable
        {
            private IEnumerator routine;
            public PerformanceStepBoundary(IEnumerator value) { routine = value; }
            public object Current => routine.Current;
            public bool MoveNext() => routine != null && routine.MoveNext();
            public void Reset() => throw new NotSupportedException();
            public void Dispose() { (routine as IDisposable)?.Dispose(); routine = null; }
        }
        private bool PerformanceTryDeferStep(Exception error, Stack<IEnumerator> stack)
        {
            if (TryResumeMapDraftStep(error, stack)) return true;
            if (!DeferContentDifferences || !(error is DriverContentDifferenceException) || !stack.Any(value => value is PerformanceStepBoundary)) return false;
            // 只接住已明确标记的内容/夹具差异。规则、安全、权限和超时异常仍保原失败。
            RecordDeferredDifference("性能局内容或夹具差异", error.Message, error);
            while (stack.Count > 0)
            {
                var current = stack.Pop(); (current as IDisposable)?.Dispose();
                if (current is PerformanceStepBoundary) break;
            }
            stack.Push(CaptureDeferredDifferences());
            return true;
        }
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void ResumeR10NativeCheck()
        {
            if (!UnityEditor.SessionState.GetBool("QY004.R10.native.pending", false)) return;
            if (UnityEngine.Application.isPlaying)
            {
                UnityEditor.EditorApplication.delayCall += () => {
                    UnityEditor.SessionState.SetBool("QY004.R10.native.pending", false);
                    try { VerifyR10Integration(); UnityEditor.EditorApplication.Exit(0); }
                    catch (Exception error) { Debug.LogException(error); UnityEditor.EditorApplication.Exit(1); }
                };
                return;
            }
            UnityEditor.EditorApplication.playModeStateChanged += state => {
                if (state != UnityEditor.PlayModeStateChange.EnteredPlayMode) return;
                UnityEditor.SessionState.SetBool("QY004.R10.native.pending", false);
                try { VerifyR10Integration(); UnityEditor.EditorApplication.Exit(0); }
                catch (Exception error) { Debug.LogException(error); UnityEditor.EditorApplication.Exit(1); }
            };
        }
        public static void VerifyR10Integration()
        {
            if (!UnityEngine.Application.isPlaying)
            {
                UnityEditor.SessionState.SetBool("QY004.R10.native.pending", true);
                ResumeR10NativeCheck();
                var deadline = DateTime.UtcNow.AddSeconds(90);
                UnityEditor.EditorApplication.update += () => { if (DateTime.UtcNow > deadline) UnityEditor.EditorApplication.Exit(1); };
                UnityEditor.EditorApplication.EnterPlaymode();
                return;
            }
            string Arg(string prefix) => Environment.GetCommandLineArgs().FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length);
            string destination = Arg("--qy004-r10-check-output=");
            if (string.IsNullOrEmpty(destination) || !Directory.Exists(destination)) throw new InvalidOperationException("有限核验需要明确独立输出目录");
            var checks = new List<string>();
            var baseline = ParsePerformanceRoomPlan(null, null, null, null);
            var two = ParsePerformanceRoomPlan("2", "standard", "core", null);
            var three = ParsePerformanceRoomPlan("3", "entrepreneur", "core,enterprise,infected", null);
            foreach (var plan in new[] { baseline, two, three })
            {
                var rule = new GameRuleConfiguration { PlayerCount = plan.Players, RuleModeId = plan.Mode, MapId = plan.Map, EnabledExpansionIds = plan.Expansions.ToList() };
                Require(rule.TryValidate(out var reason), reason); checks.Add("正式参数配置" + plan.Players + ":" + plan.Mode);
            }
            Require(ParsePerformanceSnapshotBoundaries(null).Length == 0 && ParsePerformanceSnapshotBoundaries("initial,after-operation-idle,after-menu-idle").Length == 3, "快照边界默认/上限错误");
            Require((int)(Unity.Profiling.Memory.CaptureFlags.ManagedObjects | Unity.Profiling.Memory.CaptureFlags.NativeObjects | Unity.Profiling.Memory.CaptureFlags.NativeAllocations) == 7, "正式快照flags不是7");
            checks.Add("正式快照默认关闭及flags7");
            var plain = MatchSavePerformanceOptions.Parse(new[] { "--yc-perf-autosave=off" }, "unused", "unused");
            Require(!plain.DisableAutomatic && !plain.Performance, "普通模式不能受实验off影响"); checks.Add("无性能入口的off无效");
            var controller = MatchSaveController.Instance;
            if (controller == null) controller = new GameObject("R10隔离保存合同核验").AddComponent<MatchSaveController>();
            if (MatchSaveController.Instance != controller) typeof(MatchSaveController).GetMethod("Awake", Members).Invoke(controller, null);
            bool expected = Arg("--yc-perf-autosave=") != "off";
            var observed = PerformanceSaveState();
            Require(observed.Available && observed.StatisticsEnabled && observed.AutomaticSavingEnabled == expected, "原生Awake实际自动保存策略/统计落点不符");
            Require(observed.AllWorkSettled == true && observed.Outstanding == 0 && observed.PendingAutomaticWindowSaves == 0, "新控制器不是已核零队列");
            checks.Add("原生Awake实际策略" + expected); checks.Add("真实标量初始零队列且平衡");
            var weakSnapshot = controller.ReadSaveWeakObservation();
            bool weakRequested = Arg("--yc-perf-save-weak=") == "true";
            Require(weakSnapshot.Requested == weakRequested && weakSnapshot.Enabled == (weakRequested && expected) && !weakSnapshot.Selected,
                "原生弱观察默认/off/on落点错误");
            var weakObject = Read(controller, "weakObservation");
            Require((weakObject != null) == (weakRequested && expected), "默认/off不应创建弱观察器");
            if (weakObject != null)
            {
                var weakFields = weakObject.GetType().GetFields(Members);
                Require(weakFields.Count(value => value.FieldType == typeof(WeakReference)) == 2 && weakFields.All(value =>
                    value.FieldType == typeof(WeakReference) || value.FieldType.IsPrimitive || value.FieldType.IsEnum || value.FieldType == typeof(string)),
                    "实际弱观察器字段超出两弱句柄及标量/短字符串");
            }
            Require(typeof(MatchSaveWeakSnapshot).GetFields().All(value => value.FieldType.IsValueType || value.FieldType == typeof(string)), "快照不能返回强目标容器");
            checks.Add("原生weak默认/off/on及只读快照"); checks.Add("实际弱容器仅两WeakReference和标量");
            var manual = typeof(MatchSaveController).GetMethod("Save");
            byte[] il = manual?.GetMethodBody()?.GetILAsByteArray();
            Require(manual != null && il != null && il.Contains((byte)0x16), "手动Save编译入口未保留显式false参数");
            checks.Add("实际程序集保留手动Save非自动入口；此处未绑定对局写盘");
            string path = Path.Combine(destination, "原生有限核验.json");
            using (var stream = new StreamWriter(new FileStream(path, FileMode.CreateNew))) stream.Write(JsonConvert.SerializeObject(new {
                Passed = true, Checks = checks, ActualSaveOwner = observed, MainMvid = typeof(MatchSaveController).Assembly.ManifestModule.ModuleVersionId,
                SaveWeak = weakSnapshot, ManualEntryIL = BitConverter.ToString(il), ManualDiskSaveExecuted = false, Scope = "隔离原生编译/参数/控制器检查，不算正常菜单游戏验收", Unity = UnityEngine.Application.unityVersion }, Formatting.Indented));
            UnityEngine.Object.DestroyImmediate(controller.gameObject);
        }
#endif

        private IEnumerator PerformanceNetworkRoom(StartMenuView menu)
        {
            networkRoot = Argument("--yc-network-ui-root="); networkRole = Argument("--yc-network-ui-role=");
            Require(Path.IsPathRooted(networkRoot ?? "") && Directory.Exists(networkRoot) && networkRoles.Contains(networkRole), "性能四端目录/角色无效");
            Require(LocalMirrorTestMode.IsEnabled, "只允许正式本地联机后端，仍从原菜单建房加入");
            if (networkRole == "host")
            {
                yield return Press(menu.StartGameButton, "房主原菜单建房");
                yield return PerformanceWait(() => menu.RoomPanel.gameObject.activeInHierarchy && menu.RoomPanel.DisplayedRoom != null, "原房间出现");
                var room = menu.RoomPanel;
                foreach (var option in (StartMenuRoomPanelView.ContentOption[])Read(room, "expansions"))
                    if (option.Id != "core" && option.Selection.gameObject.activeSelf) yield return Press(option.Button, "原房间关闭扩展" + option.Id);
                var map = ((StartMenuRoomPanelView.ContentOption[])Read(room, "maps")).Single(value => value.Id == "map-four-players");
                if (!map.Selection.gameObject.activeSelf) yield return Press(map.Button, "原四人地图");
                File.WriteAllText(Path.Combine(networkRoot, "房间号.txt"), room.DisplayedRoom.RoomId);
                yield return PerformanceWait(() => room.DisplayedRoom.PlayerCount == 4 && room.CanStartCurrentRoom && PerformanceReady(room.StartButton), "真实四名成员连接准备", 180f);
                yield return Press(room.StartButton, "房主原开始按钮");
            }
            else
            {
                yield return PerformanceWait(() => File.Exists(Path.Combine(networkRoot, "房间号.txt")), "房主原页面房间号");
                string code = File.ReadAllText(Path.Combine(networkRoot, "房间号.txt"));
                int separator = code.LastIndexOf(':'); Require(separator > 0, "原本地房间号缺端口");
                code = "127.0.0.1" + code.Substring(separator);
                yield return Press(menu.OnlineModeButton, "成员原加入按钮");
                var input = menu.JoinPanel.RoomCodeInput;
                yield return PointerSequence(input.gameObject, "原房间号输入框");
                input.ProcessEvent(Event.KeyboardEvent("^a"));
                foreach (char ch in code) input.ProcessEvent(new Event { type = EventType.KeyDown, character = ch, keyCode = KeyCode.None });
                Require(input.text == code, "原输入框字符输入不完整");
                yield return Press(menu.JoinPanel.JoinButton, "原连接房间按钮");
                yield return PerformanceWait(() => menu.RoomPanel.gameObject.activeInHierarchy && menu.RoomPanel.DisplayedRoom != null && PerformanceReady(menu.RoomPanel.StartButton), "成员原准备门禁", 180f);
                yield return Press(menu.RoomPanel.StartButton, "成员原准备按钮");
            }
        }

        private IEnumerator PerformanceStep()
        {
            stage = "低开销-" + State.Round + "-" + LocalPlayer + "-" + (Request?.InteractionTypeId ?? State.Phase.ToString());
            if (performancePending.Count != 0) { yield return PerformanceWait(() => performancePending.Count == 0, "本人原命令准确回执"); yield break; }
            if (PerformanceWaitingForRefresh()) yield break;
            if (performanceBuildInstance != null && !performanceBuiltBound)
            {
                if (PerformanceBuildCompleted(performanceBuildInstance) && PerformanceCityBindingVisible(performanceBuildInstance))
                {
                    yield return new WaitForEndOfFrame();
                    // 同一截图帧重核同一GameId、Build节点、实例和真实RawImage，不能拼接另一笔建设。
                    if (PerformanceBuildCompleted(performanceBuildInstance) && PerformanceCityBindingVisible(performanceBuildInstance))
                    {
                        SaveCapture("完整建设后本城实际显示");
                        performanceBuiltBound = true;
                        PerformanceRecord("build.completed_and_bound", new { Instance = performanceBuildInstance, Effect = performanceBuildEffectId,
                            CaptureNumber = captureNumber, FirstPixelClaimed = false });
                        PerformanceSpecificBuildResult();
                        if (!NetworkMode) yield break;
                    }
                }
            }
            // 效果建设有独立原工作流；先分流，不能借普通主要行动的付款续接处理它。
            var effectBuildRequest = Request;
            if (effectBuildRequest?.InteractionTypeId == YC.Domain.Effects.FacilitySelectionEffectExecutor.NormalBuildInteractionType ||
                effectBuildRequest?.InteractionTypeId == YC.Domain.Effects.ResourceEffectExecutor.PaymentConfirmationInteractionTypeId)
            {
                if (!PerformanceRequestIsCurrent(effectBuildRequest)) yield break;
                if (TryCurrentEffectBuild(out var effectBuild))
                { yield return PerformanceExistingContinuation("effect_build", EffectBuildChoice(effectBuild)); yield break; }
                if (effectBuildRequest.InteractionTypeId == YC.Domain.Effects.FacilitySelectionEffectExecutor.NormalBuildInteractionType) yield break;
            }
            if (preparedMainAction != null && (Request == null || ActionPaymentPreparation.IsPreparedActionRequest(Request)))
            {
                var preparation = CurrentPaymentView()?.PreparedActionPayments.SingleOrDefault(value => value.ActionKind == preparedMainAction.Kind);
                if (preparation == null) { yield return null; yield break; }
                if (!preparation.IsReady)
                {
                    // 移城入口先准备空目标，原地图首击才准备目标费用；不能用付款Ready挡住首击。
                    if (PerformanceNeedsMoveTarget(preparedMainAction, preparation, Request))
                        yield return PerformanceContinueMoveTarget();
                    else yield return null;
                    yield break;
                }
                yield return ContinuePreparedMainAction(); yield break;
            }
            if (Route?.IsActive == true)
            {
                if (State.Phase != GamePhase.ResourceCollection)
                {
                    if (Request != null && !PerformanceRequestIsCurrent(Request)) yield break;
                    yield return PerformanceExistingContinuation("granted_route", GrantedRouteSelection()); yield break;
                }
                if (Route.CanConfirm && PerformanceReady(hud.Frame.EndActionButton))
                {
                    PerformanceRecord("harvest.button_ready", new { RequestId = Request?.InteractionId });
                    performanceHarvestRequest = Request?.InteractionId;
                    performanceHarvestSubmitted = true; performanceHarvests++;
                    yield return Press(hud.Frame.EndActionButton, "正常采集原结束按钮");
                }
                else if (!Route.CanConfirm)
                {
                    var point = PreferVisibleMapTarget(FindObjectsOfType<MapButtonView>().Where(value =>
                        value.IsInteractive && !value.IsSelected && value.TargetKind == WorkflowHighlightTargetKind.Location));
                    if (point != null) yield return MapPress(point.TargetKind, point.TargetId, "采集当前合法资源点");
                }
                yield break;
            }
            if (Request?.InteractionTypeId == YC.Domain.Effects.FacilityBuildEffectExecutor.PlacementInteractionType)
            {
                performanceBuildEffectId = Request.OwnerEffectId;
                yield return BuildPlacement(); yield break;
            }
            if (FindObjectOfType<FacilityBuildDialogView>()?.isActiveAndEnabled == true || FindConditionPaymentView() != null)
            { yield return PerformanceChoice(); yield break; }
            if (Request != null || State.Phase == GamePhase.CharacterCover || State.Phase == GamePhase.Entrance)
            { yield return PerformanceChoice(); yield break; }
            if (PerformanceReady(hud.Frame.EndActionButton))
            { yield return Press(hud.Frame.EndActionButton, "当前原结束行动按钮"); yield break; }
            if (State.Phase != GamePhase.ActionRound1 && State.Phase != GamePhase.ActionRound2) yield break;
            if (!NetworkMode && !performanceQuickTabsDone)
            {
                int count = performanceSubmissions;
                yield return Press(MarkedButton("action.tab.1"), "快对照-原快捷行动页签");
                yield return Press(MarkedButton("action.tab.0"), "快对照-原主要行动页签");
                performanceQuickTabsDone = true;
                PerformanceRecord("control.quick_tabs", new { SubmittedBefore = count, SubmittedAfter = performanceSubmissions });
                yield break;
            }
            string window = State.Round + ":" + State.ActionRound + ":" + LocalPlayer;
            if (!NetworkMode && PerformanceReady(hud.ActionPanelView.BuildButton) && performanceDraftClicks.Add("build:" + window))
            {
                long start = Clock.GetTimestamp();
                var options = CurrentRegisteredBuildOptions().Query(State, LocalPlayer);
                var legal = options.FirstOrDefault(option => BuildFacilitySelectionController.CanPrepareOption(option) &&
                    option.PaymentOptions.Any(payment => payment.IsAvailable) && option.SlotOptions.Any(slot => slot.IsLegal));
                long ticks = Clock.GetTimestamp() - start; performanceSelectorTicks += ticks;
                PerformanceRecord("driver.legal_build_query", new { Ticks = ticks, Candidate = legal?.FacilityId });
                if (legal != null)
                {
                    yield return Press(hud.ActionPanelView.BuildButton, "正常建设打开");
                    preparedMainAction = new PreparedMainActionContinuation { GameId = State.GameId, Kind = "build", TargetId = legal.FacilityId,
                        PlayerId = LocalPlayer, Round = State.Round, ActionRound = State.ActionRound };
                    var candidate = PerformanceMarker("build.candidate." + legal.FacilityId);
                    if (candidate != null) yield return Press(candidate, "正常建设选择当前可支付牌");
                    yield break;
                }
            }
            if (PerformanceReady(hud.ActionPanelView.DeployButton) && performanceDraftClicks.Add("deploy:" + window))
            {
                yield return Press(hud.ActionPanelView.DeployButton, "正常部署打开");
                var target = PreferVisibleMapTarget(FindObjectsOfType<MapButtonView>().Where(value => value.TargetKind == WorkflowHighlightTargetKind.InfluenceSlot && value.IsInteractive));
                if (target != null)
                {
                    string id = target.TargetId;
                    var expected = new MapInputContext { Game = State.GameId, Player = LocalPlayer,
                        Revision = State.EffectRuntime.StateRevision, Request = GameStateCloneService.DeepClone(Request) };
                    int before = performanceSubmissions;
                    yield return MapPress(WorkflowHighlightTargetKind.InfluenceSlot, id, "正常部署预选", expected);
                    // 普通部署沿原Draft双击，不要求必答请求宿主；首击已提交或上下文变化时不补第二击。
                    if (performanceSubmissions == before && MapContextStillCurrent(expected))
                        yield return MapPress(WorkflowHighlightTargetKind.InfluenceSlot, id, "正常部署确认", expected);
                }
                yield break;
            }
            if (!NetworkMode && Argument("--yc-ui-performance-existing-main-fallback=") == "true")
                yield return PerformanceExistingContinuation("existing_main_action_fallback", SalesAction());
        }

        private Button PerformanceMarker(string id) => FindObjectsOfType<PlayerAutomationId>()
            .Where(value => value.Id == id).Select(value => value.GetComponent<Button>()).FirstOrDefault(PerformanceReady);

        private static bool PerformanceNeedsMoveTarget(PreparedMainActionContinuation action,
            PreparedActionPaymentView preparation, InteractionRequestProjection request) =>
            action != null && preparation != null && action.Kind == "city_move" && preparation.ActionKind == "city_move" &&
            preparation.AuthorizationKind == "main_action" && !preparation.IsReady &&
            !action.TargetPreselected && !action.FinalControlClicked && string.IsNullOrEmpty(action.TargetId) &&
            string.IsNullOrEmpty(preparation.TargetId) && string.IsNullOrEmpty(preparation.InteractionId) && request == null;

        private IEnumerator PerformanceContinueMoveTarget()
        {
            var action = preparedMainAction;
            var preparation = CurrentPaymentView()?.PreparedActionPayments.SingleOrDefault(value => value.ActionKind == action?.Kind);
            // yield后重读本人同GameId/revision投影；待答请求或已选目标不走这条新增分流。
            if (!PerformanceNeedsMoveTarget(action, preparation, Request)) yield break;
            if (State.GameId != action.GameId || LocalPlayer != action.PlayerId || State.CurrentPlayerId != action.PlayerId ||
                State.Round != action.Round || State.ActionRound != action.ActionRound)
                throw new DriverContinuationPendingException("移城首击准备的本人操作窗口已改变；保留断点，不替换身份");
            var move = (Read(mobile, "turnActionPresenter") as TurnActionPresenter)?.MoveInteraction;
            if (move?.IsSelectingMoveTarget != true || !move.CanResumePreparedDraft)
                throw new DriverContinuationPendingException("移城空目标准备未恢复同笔原地图工作流；保留断点，不重开入口");
            if (move.IsSubmissionInFlight) yield break;
            if (ActionPaymentPreparation.HasBlockingChoice(State, LocalPlayer))
                throw new DriverContinuationPendingException("移城首击仍有正式必答阻塞；不借空目标分流绕过响应");
            var targets = FindObjectsOfType<MapButtonView>().Where(value =>
                value.TargetKind == WorkflowHighlightTargetKind.Location && value.IsInteractive).ToArray();
            if (targets.Length > 0)
            {
                if (targets.Any(value => value.Image == null || !value.Image.enabled))
                    throw new DriverContinuationPendingException("移城可交互目标的原地图显示缺失；保留现场，不取消掩盖");
                // 原助手选可见目标；MapPress仍逐输入核GameId/request/revision/actor、首命中和目标资格。
                yield return ContinuePreparedMainAction();
                yield break;
            }
            if (move.BuildPresentation().Highlights.Any(value => value.TargetKind == WorkflowHighlightTargetKind.Location))
                throw new DriverContinuationPendingException("移城原工作流仍有合法高亮但实际地图控件缺失；保留现场，不取消掩盖");
            var cancel = hud.ActionPanelView.MoveCityButton;
            if (!preparation.CanCancel || !move.CanCancelPreparedPayment || !PerformanceReady(cancel))
                throw new DriverContinuationPendingException("移城当前无合法可见目标且原取消不可用；保留断点，不冒行动完成");
            yield return CancelPreparedMainAction(cancel, preparation, "移城当前无合法可见目标，沿原移动按钮正常取消；本次移城未覆盖");
        }

        private IEnumerator PerformanceExistingContinuation(string name, IEnumerator continuation)
        {
            try
            {
                while (true)
                {
                    int before = performanceSubmissions;
                    long began = Clock.GetTimestamp();
                    bool next = continuation.MoveNext();
                    // 只计父续接同步部分；yield出的原控件事件/等待另计，不叠加为纯驱动CPU。
                    PerformanceRecord("driver.existing_continuation.parent_step", new { Name = name,
                        Ticks = Clock.GetTimestamp() - began, Submitted = performanceSubmissions - before,
                        IncludesInlineProductCalls = true, IncludesYieldedChildren = false });
                    if (!next) yield break;
                    var current = continuation.Current;
                    // 原Escape取消可直接提交，未经过PerformancePointer；仍等本人的准确回执。
                    if (performancePending.Count > 0)
                        yield return PerformanceWait(() => performancePending.Count == 0 &&
                            (!NetworkMode || NetworkTryReadPublication(out _)), "既有续接原命令准确Applied及当前发表");
                    yield return current;
                }
            }
            finally { (continuation as IDisposable)?.Dispose(); }
        }

        private IEnumerator PerformanceChoice()
        {
            var request = Request;
            if (request != null && !PerformanceRequestIsCurrent(request)) { yield return null; yield break; }
            if (IsEntrepreneurUiRequest(request))
            {
                if (PerformanceRequestIsCurrent(request)) yield return PerformanceExistingContinuation("entrepreneur", EntrepreneurUiChoice(request));
                yield break;
            }
            var enterprise = CurrentEnterpriseChoicePage(request);
            if (enterprise != null)
            { yield return PerformanceExistingContinuation("enterprise_selection", NormalEnterpriseChoice(request, enterprise)); yield break; }
            // 同一正式请求的页尚在绑定/刷新时等待；别的请求的旧页或只读页不占此路由。
            if (HasEnterprisePageAwaitingRevision(request)) { yield return null; yield break; }
            // 正式建设请求已交原续接；scope暂不可读时等待，不能退回候选marker轮转。
            if (request?.InteractionTypeId == YC.Domain.Effects.FacilitySelectionEffectExecutor.NormalBuildInteractionType ||
                request?.InteractionTypeId == YC.Domain.Effects.ResourceEffectExecutor.PaymentConfirmationInteractionTypeId && CurrentBuildPage() != null)
                yield break;
            if (request != null && PerformanceRequestIsCurrent(request) &&
                FindObjectsOfType<EffectDialogShellView>().Any(shell =>
                    shell.TryGetResourceAllocation(request.InteractionId, out _, out _)))
            {
                // 原Shell按请求绑定的确认入口；不按按钮静态名称猜测数量页。
                yield return RequiredAllocation(); yield break;
            }
            if (request != null && EffectDialogSelectionSpec.IsInfluenceMapTarget(request))
            {
                if (!PerformanceRequestIsCurrent(request)) { yield return null; yield break; }
                var target = PreferVisibleMapTarget(FindObjectsOfType<MapButtonView>().Where(value =>
                    value.TargetKind == WorkflowHighlightTargetKind.InfluenceSlot && value.IsInteractive));
                if (target != null) yield return MapPressWithFreshConfirmation(request, target.TargetKind, target.TargetId);
                else yield return null;
                yield break;
            }
            string key = (request?.InteractionId ?? State.Phase.ToString()) + ":" + State.EffectRuntime.StateRevision + ":" + LocalPlayer + ":";
            var payment = FindConditionPaymentView();
            if (payment != null)
            {
                if (PerformanceReady(payment.ConditionPaymentExecuteButton)) yield return Press(payment.ConditionPaymentExecuteButton, "原付款/条件执行");
                else if (PerformanceReady(payment.ConditionPaymentDeclineButton)) yield return Press(payment.ConditionPaymentDeclineButton, "原正式允许放弃按钮");
                yield break;
            }
            var choiceExecute = ChoiceExecuteButton();
            if (PerformanceReady(choiceExecute))
            { yield return Press(choiceExecute, "原通用效果选择行"); yield break; }
            var build = FindObjectOfType<FacilityBuildDialogView>();
            if (build != null && build.isActiveAndEnabled)
            {
                var confirm = PerformanceMarker("build.confirm");
                if (confirm != null) { yield return Press(confirm, "正常建设原确认"); yield break; }
                foreach (string prefix in new[] { "build.payment.", "build.slot.", "build.candidate." })
                {
                    var marker = FindObjectsOfType<PlayerAutomationId>().FirstOrDefault(value => value.Id.StartsWith(prefix, StringComparison.Ordinal) &&
                        PerformanceReady(value.GetComponent<Button>()) && !performanceDraftClicks.Contains(key + value.Id));
                    if (marker != null)
                    {
                        performanceDraftClicks.Add(key + marker.Id);
                        yield return Press(marker.GetComponent<Button>(), "正常建设当前原控件" + marker.Id); yield break;
                    }
                }
            }
            foreach (string confirmId in new[] { "supply.select.confirm" })
            {
                var confirm = PerformanceMarker(confirmId);
                if (confirm != null) { yield return Press(confirm, "原供应确认"); yield break; }
            }
            // 通用卡牌选择（含盖牌）仍沿原确认入口；资源分配已在上方按Shell请求绑定处理。
            var selectionConfirm = FindObjectsOfType<Button>().FirstOrDefault(value =>
            {
                if (value.name != "Confirm Selection" || !PerformanceReady(value)) return false;
                var shell = value.GetComponentInParent<EffectDialogShellView>();
                return shell != null && !shell.GetComponentsInChildren<EffectDialogResourceRowView>().Any(row => row.isActiveAndEnabled) &&
                    (shell.IsCardPicker || shell.GetComponentsInChildren<EffectDialogOptionRowView>().Any(row => row.isActiveAndEnabled));
            });
            if (selectionConfirm != null) { yield return Press(selectionConfirm, "当前原卡牌选择确认"); yield break; }
            foreach (string prefix in new[] { "supply.select.", "selection.card.", "event.option." })
            {
                var marker = FindObjectsOfType<PlayerAutomationId>().Where(value => value.Id.StartsWith(prefix, StringComparison.Ordinal) &&
                    value.Id != "supply.select.confirm" && PerformanceReady(value.GetComponent<Button>()) && !performanceDraftClicks.Contains(key + value.Id))
                    .OrderBy(value => IsUiTargetVisible(value.gameObject) ? 0 : 1).FirstOrDefault();
                if (marker != null)
                {
                    performanceDraftClicks.Add(key + marker.Id);
                    yield return Press(marker.GetComponent<Button>(), "当前真实候选" + marker.Id); yield break;
                }
            }
            var row = FindObjectsOfType<EffectDialogOptionRowView>().FirstOrDefault(value => PerformanceReady(value.Button) && !performanceDraftClicks.Contains(key + value.name));
            if (row != null)
            {
                performanceDraftClicks.Add(key + row.name);
                yield return Press(row.Button, "当前原文本候选" + row.name); yield break;
            }
            foreach (var view in FindObjectsOfType<EventChoiceDialogView>())
                foreach (var button in new[] { view.ResourcePaymentBankButton, view.ExploreConfirmButton, view.CharacterContinueButton, view.CharacterFinishButton })
                    if (PerformanceReady(button)) { yield return Press(button, "当前原合法后继按钮"); yield break; }
            var map = PreferVisibleMapTarget(FindObjectsOfType<MapButtonView>().Where(value => value.IsInteractive &&
                value.TargetKind == WorkflowHighlightTargetKind.Location));
            if (map != null)
            {
                if (request != null) yield return MapPressWithFreshConfirmation(request, map.TargetKind, map.TargetId);
                else yield return MapPress(map.TargetKind, map.TargetId, "当前正式地图候选");
                yield break;
            }
        }

        private bool PerformanceRequestIsCurrent(InteractionRequestProjection expected)
        {
            var current = Request;
            return expected != null && current != null && expected.Status == "open" && expected.VisibleToViewer &&
                expected.AnsweringPlayerId == LocalPlayer && current.Status == "open" && current.VisibleToViewer &&
                current.AnsweringPlayerId == LocalPlayer && current.StateRevision == expected.StateRevision &&
                expected.StateRevision == State.EffectRuntime.StateRevision &&
                InteractionCommandIntent.SameContract(State.GameId, expected, State.GameId, current);
        }

        private IEnumerator PerformanceAllocation(InteractionRequestProjection initial)
        {
            var request = GameStateCloneService.DeepClone(initial);
            // 每次原控件事件后重读Shell绑定；不沿用被页面刷新销毁的行或旧确认按钮。
            for (int step = 0; step < 100 && PerformanceRequestIsCurrent(request); step++)
            {
                var shell = FindObjectsOfType<EffectDialogShellView>().SingleOrDefault(value =>
                    value.TryGetResourceAllocation(request.InteractionId, out _, out _));
                if (shell == null || !shell.TryGetResourceAllocation(request.InteractionId, out var confirm, out var rows)) yield break;
                var increase = rows?.FirstOrDefault(row => row != null && row.isActiveAndEnabled && PerformanceReady(row.IncreaseButton))?.IncreaseButton;
                var finish = request.PromptKey == "effect.resource.sell" ? SaleFinish() : null;
                if (finish != null && finish.GetComponentInParent<EffectDialogShellView>() != shell) finish = null;
                // 出售耗尽后由原结束按钮结清；没有可点击控件时不反复构造付款投影。
                if (!PerformanceReady(confirm) && increase == null && !PerformanceReady(finish)) yield break;
                long began = Clock.GetTimestamp();
                var validatedConfirm = CurrentAllocationConfirm(request);
                long queryTicks = Clock.GetTimestamp() - began;
                performanceSelectorTicks += queryTicks;
                if (validatedConfirm == null || validatedConfirm != confirm) yield break;
                PerformanceRecord("driver.allocation_query", new { request.InteractionId, Step = step, Ticks = queryTicks });
                if (PerformanceReady(confirm))
                {
                    yield return PointerSequenceGuarded(confirm.gameObject, "正常资源分配原确认", () =>
                        PerformanceRequestIsCurrent(request) && shell != null &&
                        shell.TryGetResourceAllocation(request.InteractionId, out var button, out _) && button == confirm);
                    yield break;
                }
                if (increase == null)
                {
                    if (PerformanceReady(finish))
                        yield return PointerSequenceGuarded(finish.gameObject, "原出售结束按钮", () =>
                            PerformanceRequestIsCurrent(request) && shell != null &&
                            shell.TryGetResourceAllocation(request.InteractionId, out var button, out _) && button == confirm && SaleFinish() == finish);
                    yield break;
                }
                yield return PointerSequenceGuarded(increase.gameObject, "正常资源分配原加号", () =>
                    PerformanceRequestIsCurrent(request) && shell != null &&
                    shell.TryGetResourceAllocation(request.InteractionId, out var button, out var currentRows) && button == confirm &&
                    currentRows?.Any(row => row != null && row.IncreaseButton == increase) == true);
            }
        }

        private WorkflowSubmissionResult PerformanceSubmit(IGameCommandPort inner, GameCommand command)
        {
            performanceSubmissions++;
            var record = new PerformanceCommandRecord { Command = command,
                BeforeRevision = State.EffectRuntime.StateRevision, Observation = PerformanceReadObservation(), InputIndex = performanceActiveInputIndex,
                SubmitTick = Clock.GetTimestamp(), RequestType = Request?.InteractionTypeId, OwnerEffect = Request?.OwnerEffectId,
                Category = command.Kind + (Request == null ? "" : ":" + Request.InteractionTypeId) };
            Require(performanceCommandRecords.Count < 256, "本轮驱动观察记录达到有界上限");
            performanceCommandRecords.Add(command.CommandId, record); commands.Add(record);
            performancePending.Add(command.CommandId);
            PerformanceRecord("command.submit.begin", new { command.CommandId, Kind = command.Kind.ToString(), command.PlayerId,
                record.InputIndex, record.Category, record.RequestType, record.BeforeRevision });
            long began = Clock.GetTimestamp();
            WorkflowSubmissionResult result;
            using (QY018MonoTrace.ObserveCommand(State.GameId, command.CommandId, record.BeforeRevision))
                result = inner.Submit(command);
            if (!NetworkMode && result.CommandResult?.Succeeded == true) performanceSuccessfulSubmissions++;
            if (!NetworkMode && performanceHarvestSubmitted && command.Kind == GameCommandKind.AnswerInteraction &&
                command.Parameters.TryGetValue("interactionId", out var answered) && answered == performanceHarvestRequest && result.CommandResult?.Succeeded == true)
                performanceHarvestReceipt = true;
            if (!NetworkMode || result.AppliedLocally || result.CommandResult?.Succeeded != true)
            {
                record.Result = result;
                record.AfterRevision = NetworkMode ? session?.View?.StateRevision ?? -1 : State.EffectRuntime.StateRevision;
                record.LocalPlayerAfter = LocalPlayer;
                if (record.ReceiptTick == 0 && (!NetworkMode || result.CommandResult?.Succeeded != true))
                {
                    record.ReceiptTick = Clock.GetTimestamp(); record.ReceiptFrame = Time.frameCount;
                    PerformanceRecord("command.receipt", new { command.CommandId, record.InputIndex, record.Category, record.ReceiptTick, record.AfterRevision,
                        Accepted = result.CommandResult?.Succeeded, ExactSynchronousResult = !NetworkMode,
                        Reason = performanceArtworkBindings ? result.CommandResult?.Validation?.Reason : null,
                        ErrorCode = performanceArtworkBindings ? result.CommandResult?.Validation?.ErrorCode.ToString() : null });
                }
            }
            PerformanceRecord("command.submit.return", new { command.CommandId, Kind = command.Kind.ToString(), Ticks = Clock.GetTimestamp() - began,
                Accepted = result.CommandResult?.Succeeded, result.AppliedLocally, Error = result.CommandResult?.Validation?.ErrorCode.ToString() });
            if (!NetworkMode || result.AppliedLocally || result.CommandResult?.Succeeded != true) performancePending.Remove(command.CommandId);
            return result;
        }

        private void PerformanceOnResolution(string game, string command, bool accepted, CommandErrorCode error, bool refresh)
        {
            if (performanceCommandRecords.TryGetValue(command, out var row))
            {
                row.ResolutionSeen = true;
                if (!accepted)
                {
                    row.Result = new WorkflowSubmissionResult(CommandResult.Invalid(ValidationResult.Failure(error, "原准确拒绝")), false);
                    row.AfterRevision = session?.View?.StateRevision ?? -1; row.LocalPlayerAfter = LocalPlayer;
                    if ((refresh || error == CommandErrorCode.StateRevisionMismatch) && row.Observation != null && game == row.Observation.GameId)
                    {
                        performanceRejectedObservation = row.Observation;
                        PerformanceRecord("driver.wait_changed_observation", new { Command = command, Error = error.ToString(), Refresh = refresh,
                            Observation = row.Observation, ResignsOriginalCommand = false });
                    }
                }
                if (row.Result != null)
                {
                    performancePending.Remove(command);
                    if (row.ReceiptTick == 0) { row.ReceiptTick = Clock.GetTimestamp(); row.ReceiptFrame = Time.frameCount;
                        PerformanceRecord("command.receipt", new { CommandId = command, row.InputIndex, row.Category, row.ReceiptTick, row.AfterRevision, Accepted = accepted, ExactSynchronousResult = false }); }
                }
            }
            PerformanceRecord("command.exact_resolution", new { Game = game, Command = command, Accepted = accepted, Error = error.ToString(), Refresh = refresh });
        }
        private void PerformanceOnApplied(SimulationCommandAppliedDto applied)
        {
            if (applied?.Command == null) return;
            if (performanceCommandRecords.TryGetValue(applied.Command.CommandId, out var row))
            {
                var result = NetworkExactAppliedResult(row.Command, applied, performanceGameId);
                if (result != null)
                {
                    row.Result = result; row.AfterRevision = session?.View?.StateRevision ?? -1; row.LocalPlayerAfter = LocalPlayer;
                    if (row.ResolutionSeen)
                    {
                        performancePending.Remove(row.Command.CommandId);
                        if (row.ReceiptTick == 0) { row.ReceiptTick = Clock.GetTimestamp(); row.ReceiptFrame = Time.frameCount;
                            PerformanceRecord("command.receipt", new { row.Command.CommandId, row.InputIndex, row.Category, row.ReceiptTick, row.AfterRevision,
                                Accepted = result.CommandResult?.Succeeded, ExactSynchronousResult = false }); }
                    }
                }
            }
            if (performanceHarvestSubmitted && applied.Command.PlayerId == LocalPlayer && applied.Command.Kind == GameCommandKind.AnswerInteraction &&
                performanceCommandRecords.TryGetValue(applied.Command.CommandId, out var harvestRow) &&
                harvestRow.Command.Parameters.TryGetValue("interactionId", out var answered) && answered == performanceHarvestRequest && applied.RuleResult?.Succeeded == true)
            { performanceHarvestSequence = applied.GlobalCommandSequence; performanceHarvestReceipt = true; }
            PerformanceRecord("network.applied_callback", new { applied.Command.CommandId, Kind = applied.Command.Kind.ToString(), applied.GlobalCommandSequence });
        }
        private void PerformanceOnFault(SimulationProtocolFaultDto fault)
        {
            performanceProtocolFault = fault?.ToString();
            PerformanceRecord("network.protocol_fault", performanceProtocolFault);
        }

        // 仅检查原事件目标所属企业页及当前HUD已有Logo引用；不枚举场景/资源，不调用任何取图或刷新入口。
        private IEnumerator PerformanceCaptureArtworkBindings(GameObject target)
        {
            if (!performanceArtworkBindings || hud == null || mobile == null || performanceLeavingMenuFlow || performanceArtworkEvidenceKeys.Count >= 16) yield break;
            var page = target == null ? null : target.GetComponentInParent<EnterpriseSelectionDialogView>();
            string pageKey = page != null && page.IsShowing && page.Draft.Projection != null
                ? "boards:" + string.Join("|", page.Draft.Projection.Boards.Select(b => b.VisualKey)) : null;
            var expansion = hud.ExpansionUi;
            var logos = expansion == null ? null : Read(expansion, "cooperationLogos") as Image[];
            bool observeLogos = Request == null && !performanceArtworkEvidenceKeys.Contains("logos") &&
                logos != null && logos.Any(i => i != null && i.isActiveAndEnabled && i.sprite != null);
            bool observePage = pageKey != null && !performanceArtworkEvidenceKeys.Contains(pageKey);
            if (!observePage && !observeLogos) yield break;
            long began = Clock.GetTimestamp();
            yield return new WaitForEndOfFrame();
            long readBegan = Clock.GetTimestamp();
            var values = new List<object>();
            if (observePage && page != null && page.IsShowing && Read(page, "items") is IDictionary items)
            {
                foreach (DictionaryEntry entry in items)
                {
                    var board = page.Draft.Projection.Boards.FirstOrDefault(b => b.Id == (string)entry.Key);
                    var item = entry.Value as EnterpriseBoardItemView;
                    if (board != null && item != null && item.gameObject.activeInHierarchy)
                        values.Add(PerformanceArtworkBinding(item.BoardImage, null, "企业板", board.VisualKey));
                }
                performanceArtworkEvidenceKeys.Add(pageKey);
            }
            if (observeLogos && expansion != null)
            {
                var catalog = Read(expansion, "enterpriseArtwork") as EnterpriseBoardCatalog;
                var existing = catalog == null ? null : Read(catalog, "logos") as IDictionary;
                foreach (var logo in logos)
                {
                    if (logo == null || !logo.gameObject.activeInHierarchy) continue;
                    string key = null;
                    if (existing != null)
                        foreach (DictionaryEntry entry in existing)
                            if (ReferenceEquals(entry.Value, logo.sprite)) { key = (string)entry.Key; break; }
                    values.Add(PerformanceArtworkBinding(logo, logo.sprite, "企业Logo", key));
                }
                performanceArtworkEvidenceKeys.Add("logos");
            }
            if (values.Count == 0) yield break;
            long readEnd = Clock.GetTimestamp();
            SaveCapture("离局重载卡图绑定-第" + (performanceGameOrdinal + 1) + "局-" + performanceArtworkEvidenceKeys.Count);
            Append("实际卡图与Logo绑定.jsonl", new { Utc = DateTime.UtcNow.ToString("o"), Frame = Time.frameCount,
                GameOrdinal = performanceGameOrdinal, State.GameId, ActiveContentHash = State.ContentPackHash,
                CaptureNumber = captureNumber, RequestId = Request?.InteractionId, RequestType = Request?.InteractionTypeId,
                Values = values.ToArray(), Artwork = ExternalContentRuntime.ReadMemoryDiagnostics(),
                ReadMilliseconds = (readEnd - readBegan) * 1000d / Clock.Frequency,
                ScreenshotMilliseconds = (Clock.GetTimestamp() - readEnd) * 1000d / Clock.Frequency,
                TotalIncludingFrameWaitMilliseconds = (Clock.GetTimestamp() - began) * 1000d / Clock.Frequency,
                NoResourceLoadOrRefresh = true, FirstPixelTimingClaimed = false });
        }

        private static object PerformanceArtworkBinding(Graphic graphic, Sprite sprite, string role, string visualKey)
        {
            var raw = graphic as RawImage;
            var texture = raw != null ? raw.texture : sprite != null ? sprite.texture : null;
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var pack = typeof(ExternalContentRuntime).GetField("pack", flags).GetValue(null) as YC.Infrastructure.Lua.ExternalContentPack;
            var index = typeof(ExternalContentRuntime).GetField("installedArtwork", flags).GetValue(null) as IDictionary;
            object binding = null;
            if (visualKey != null && index != null)
                foreach (var type in new[] { "enterprise", "department" })
                    if (index.Contains((type, visualKey))) { binding = index[(type, visualKey)]; break; }
            var slice = binding == null ? null : Read(binding, "Slice") as YC.Infrastructure.Lua.ExternalArtworkSlice;
            float[] RectValues(Rect r) => new[] { r.x, r.y, r.width, r.height };
            return new { Role = role, VisualKey = visualKey, UiPath = graphic == null ? null : ComponentPath(graphic.transform),
                Active = graphic != null && graphic.gameObject.activeInHierarchy, Enabled = graphic != null && graphic.enabled,
                Culled = graphic == null || graphic.canvasRenderer.cull, Alpha = graphic == null ? 0 : graphic.color.a,
                TextureAlive = texture != null, TextureId = texture != null ? texture.GetInstanceID() : 0,
                TexturePath = texture != null ? texture.name : null, Width = texture != null ? texture.width : 0, Height = texture != null ? texture.height : 0,
                RawUv = raw != null ? RectValues(raw.uvRect) : null, UiRect = graphic == null ? null : RectValues(graphic.rectTransform.rect),
                SpriteAlive = sprite != null, SpriteId = sprite != null ? sprite.GetInstanceID() : 0, SpriteName = sprite != null ? sprite.name : null,
                SpriteRect = sprite != null && texture != null ? RectValues(sprite.rect) : null,
                Vertices = sprite != null && texture != null ? sprite.vertices.Select(v => new[] { v.x, v.y }).ToArray() : null,
                SourcePath = binding == null ? null : Read(binding, "Path") as string, SourceSlice = slice,
                InstalledPackHash = pack?.ContentHash, InstalledPackIdentity = pack == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pack) };
        }

        private IEnumerator PerformancePointer(GameObject target, string description, Func<bool> stillCurrent = null)
        {
            if (performanceLifetimeMode) yield return PerformanceWeakBoundaryCheck();
            Require(performancePending.Count == 0, "原命令仍待准确回执，不重复点击");
            int attempt = ++performanceInputAttempts;
            long readinessBegan = Clock.GetTimestamp();
            PerformanceRecord("input.wait.begin", new { Attempt = attempt, Description = description, Kind = "button" });
            bool gameplay = mobile != null && !performanceLeavingMenuFlow;
            var originalRequest = gameplay ? GameStateCloneService.DeepClone(Request) : null;
            string game = gameplay ? State.GameId : null;
            int actor = gameplay ? LocalPlayer : -1, originalRevision = gameplay ? State.EffectRuntime.StateRevision : -1;
            string marker = target == null ? null : target.GetComponent<PlayerAutomationId>()?.Id;
            bool ContextCurrent() => !finished && stillCurrent?.Invoke() != false && (!gameplay || !PerformanceWaitingForRefresh() &&
                game == State.GameId && actor == LocalPlayer && originalRevision == State.EffectRuntime.StateRevision &&
                (originalRequest == null ? Request == null : PerformanceRequestIsCurrent(originalRequest)));
            GameObject CurrentTarget()
            {
                if (!ContextCurrent()) return null;
                if (target != null && target.activeInHierarchy &&
                    (string.IsNullOrEmpty(marker) || target.GetComponent<PlayerAutomationId>()?.Id == marker)) return target;
                if (string.IsNullOrEmpty(marker)) return null;
                var matches = FindObjectsOfType<PlayerAutomationId>().Where(value => value.Id == marker &&
                    PerformanceReady(value.GetComponent<Button>())).ToArray();
                return target = matches.Length == 1 ? matches[0].gameObject : null;
            }
            if (!ContextCurrent()) yield break;
            if (!IsUiTargetVisible(target)) yield return RevealOriginalScrollTarget(CurrentTarget, description);
            float wait = Time.realtimeSinceStartup;
            while (true)
            {
                if (!ContextCurrent())
                { PerformanceRecord("driver.stale_target_retired", description); yield break; }
                target = CurrentTarget();
                if (target != null && IsUiTargetVisible(target) && FirstUiClickTarget(UiPointerPoint(target)) == target) break;
                if (Time.realtimeSinceStartup - wait > 8f) { PerformanceFirstStall(description); PerformanceRecord("driver.target_not_ready", new { Attempt = attempt, Description = description }); yield break; }
                yield return null;
            }
            if (!ContextCurrent())
            { PerformanceRecord("driver.request_changed_before_input", description); yield break; }
            var point = UiPointerPoint(target);
            var button = target.GetComponent<Button>();
            Require(button == null || PerformanceReady(button), description + "：原按钮不可用");
            PerformanceRecord("input.wait.end", new { Attempt = attempt, Description = description, Ticks = Clock.GetTimestamp() - readinessBegan });
            if (performanceArtworkBindings)
            {
                yield return PerformanceCaptureArtworkBindings(target);
                if (!ContextCurrent()) yield break;
            }
            int inputIndex = ++performanceInputs;
            PerformanceRecord("ui.event.begin", new { Description = description, Target = ComponentPath(target.transform), Index = inputIndex, Attempt = attempt });
            long began = Clock.GetTimestamp();
            var data = Pointer(point);
            long guardTicks = 0;
            bool InputStillReady()
            {
                long guardBegan = Clock.GetTimestamp();
                bool ready = ContextCurrent() && performancePending.Count == 0 && target != null && target.activeInHierarchy &&
                    (button == null || PerformanceReady(button)) && FirstUiClickTarget(point) == target;
                guardTicks += Clock.GetTimestamp() - guardBegan;
                return ready;
            }
            performanceActiveInputIndex = inputIndex;
            try
            {
                ExecuteEvents.Execute(target, data, ExecuteEvents.pointerDownHandler);
                if (!InputStillReady()) { PerformanceRecord("driver.request_changed_after_pointer_down", description); yield break; }
                ExecuteEvents.Execute(target, data, ExecuteEvents.pointerUpHandler);
                if (!InputStillReady()) { PerformanceRecord("driver.request_changed_after_pointer_up", description); yield break; }
                ExecuteEvents.Execute(target, data, ExecuteEvents.pointerClickHandler);
            }
            finally { performanceActiveInputIndex = 0; }
            if (finished) yield break;
            PerformanceRecord("ui.event.return", new { Description = description, Index = performanceInputs, Ticks = Clock.GetTimestamp() - began, GuardTicks = guardTicks });
            if (NetworkMode && performancePending.Count > 0)
                yield return PerformanceWait(() => performancePending.Count == 0 && NetworkTryReadPublication(out _), "原按钮命令准确Applied及当前发表");
            yield return null; yield return new WaitForEndOfFrame();
            PerformanceRecord("driver.frame_after_event", new { Index = performanceInputs, FirstPixelClaimed = false });
            PerformanceObserveResultFrames();
        }

        private IEnumerator PerformanceMap(WorkflowHighlightTargetKind kind, string id, string description,
            MapInputContext expected = null, bool confirmSelected = false)
        {
            if (performanceLifetimeMode) yield return PerformanceWeakBoundaryCheck();
            Require(performancePending.Count == 0, "原命令仍待准确回执，不重复地图输入");
            int attempt = ++performanceInputAttempts;
            long readinessBegan = Clock.GetTimestamp();
            PerformanceRecord("input.wait.begin", new { Attempt = attempt, Description = description, Kind = "map" });
            if (PerformanceWaitingForRefresh()) yield break;
            if (Request != null && !PerformanceRequestIsCurrent(Request)) yield break;
            expected = expected ?? new MapInputContext { Game = State.GameId, Player = LocalPlayer,
                Revision = State.EffectRuntime.StateRevision, Request = GameStateCloneService.DeepClone(Request) };
            if (expected != null && !MapContextStillCurrent(expected)) yield break;
            var target = FindObjectsOfType<MapButtonView>().SingleOrDefault(value => value.TargetKind == kind && value.TargetId == id);
            Require(target != null && target.IsInteractive && target.Image != null && target.Image.enabled, description + "：当前地图候选不可用");
            Vector2 point = Camera.main.WorldToScreenPoint(target.HitArea.bounds.center);
            if (!TabletopPointerClassifier.CanRouteMapPointer(point))
            { yield return RevealMapTarget(target, detail => PerformanceRecord("driver.map_navigation", new { kind, id })); }
            if (PerformanceWaitingForRefresh()) yield break;
            if (expected != null && !MapContextStillCurrent(expected)) yield break;
            target = FindObjectsOfType<MapButtonView>().SingleOrDefault(value => value.TargetKind == kind && value.TargetId == id);
            if (target == null || !target.IsInteractive || target.Image == null || !target.Image.enabled) yield break;
            point = Camera.main.WorldToScreenPoint(target.HitArea.bounds.center);
            if (confirmSelected && (!target.IsSelected || !MapConfirmationOwnerMatches(kind, id))) yield break;
            Require(target.IsInteractive && TabletopPointerClassifier.CanRouteMapPointer(point) && FirstUiClickTarget(point) == target.gameObject,
                description + "：真实首命中或地图判权不成立");
            PerformanceRecord("input.wait.end", new { Attempt = attempt, Description = description, Ticks = Clock.GetTimestamp() - readinessBegan });
            int inputIndex = ++performanceInputs;
            PerformanceRecord("ui.map_event.begin", new { Description = description, Kind = kind.ToString(), Id = id, Index = inputIndex, Attempt = attempt });
            long began = Clock.GetTimestamp();
            performanceActiveInputIndex = inputIndex;
            try { ExecuteEvents.Execute(target.gameObject, Pointer(point), ExecuteEvents.pointerClickHandler); }
            finally { performanceActiveInputIndex = 0; }
            PerformanceRecord("ui.map_event.return", new { Index = performanceInputs, Ticks = Clock.GetTimestamp() - began });
            if (NetworkMode && performancePending.Count > 0)
                yield return PerformanceWait(() => performancePending.Count == 0 && NetworkTryReadPublication(out _), "原地图命令准确Applied及当前发表");
            yield return null; yield return new WaitForEndOfFrame();
            PerformanceRecord("driver.frame_after_event", new { Index = performanceInputs, FirstPixelClaimed = false });
            PerformanceObserveResultFrames();
        }

        private IEnumerator PerformanceExitThroughMenu()
        {
            performanceStopInputTick = 0; performanceStopInputUtc = null;
            stage = "短样本正常返回主菜单退出";
            yield return PerformanceWait(() => performancePending.Count == 0, "退出前本人原命令结清");
            performanceLeavingMenuFlow = true;
            if (performanceSubmission != null) performanceSubmission.SubmissionResolutionObserved -= PerformanceOnResolution;
            if (networkTransport != null)
            {
                networkTransport.SimulationCommandApplied -= PerformanceOnApplied;
                networkTransport.SimulationProtocolFaulted -= PerformanceOnFault;
            }
            var settings = FindObjectOfType<GameSettingsMenuView>();
            Require(settings != null, "正常设置齿轮不存在，保留窗口");
            yield return Press(hud.Frame.SettingsButton, "原HUD设置按钮");
            if (Argument("--yc-ui-performance-settled-exit=") == "true")
            {
                float waiting = Time.realtimeSinceStartup;
                while (!PerformanceReady(settings.ReturnButton) && Time.realtimeSinceStartup - waiting < 12f)
                {
                    yield return null; yield return new WaitForEndOfFrame();
                    if (!PerformanceReady(settings.ReturnButton)) yield return Press(hud.Frame.SettingsButton, "当前合法界面重新打开原设置");
                }
            }
            yield return Press(settings.ReturnButton, "原返回主菜单按钮");
            yield return PerformanceWait(() => FindObjectOfType<StartMenuView>() != null && !SceneTransitionContext.IsTransitionInProgress, "正常主菜单返回完成");
            PerformanceRecord("exit.normal_menu");
            PerformanceMemoryPulse("memory.menu_entered");
            performanceNormalMenuReturned = true;
            if (performanceMenuIdleSeconds > 0)
            {
                settings = null;
                PerformanceReleaseDriverReferences();
                yield return PerformanceAwaitSaveSettled("after-menu-idle");
                yield return PerformancePause("返回主菜单后自然闲置观察", performanceMenuIdleSeconds);
                SaveCapture("菜单自然闲置结束");
                yield return PerformanceSnapshotBoundary("after-menu-idle");
            }
            if (PerformanceLifecycleAudit)
            {
                if (performanceLifetimeMode) PerformanceWeakRecord("save_weak_menu");
                PerformanceRecord("catalog.audit", PerformanceCatalogAudit(true, "menu"));
                PerformanceRecord("lifetime.game.end", new { GameOrdinal = performanceGameOrdinal, PreviousGameId = performanceGameId,
                    Inputs = performanceInputs, SuccessfulSubmissions = performanceSuccessfulSubmissions, StopReason = performanceStopReason,
                    OperationsElapsed = performanceOperationsElapsed, WeakDiagnosticPauseSeconds = performanceWeakPauseSeconds,
                    CompleteBuildBound = performanceBuiltBound, QuickTabs = performanceQuickTabsDone, HarvestCompleted = performanceHarvestCompleted });
                if (performanceGameOrdinal + 1 < performanceGameCount) yield break;
            }
            if (performanceMemoryDetail)
            {
                PerformanceMemoryPulse("memory.all_windows_completed");
                ExternalContentRuntime.SetMemoryDiagnosticsEnabled(false);
                GameplayIconResourceResolver.SetMemoryDiagnosticsEnabled(false);
                PerformanceRecord("memory.attribution_disabled");
            }
            if (Argument("--yc-ui-performance-validate-saves=") == "true")
            {
                yield return PerformanceAwaitSaveSettled("final-read-verification");
                Require(PerformanceSaveState().AllWorkSettled == true, "退出读取核验前保存仍未结清");
                stage = "曲线结束后的独立实际存档读取核验";
                PerformanceRecord("save.final_read.begin");
                string saveRoot = Argument("--yc-performance-save-root=");
                Require(!string.IsNullOrEmpty(saveRoot), "完整读取核验需要显式测试存档路径");
                var read = System.Threading.Tasks.Task.Run(() => {
                    var store = new YC.Infrastructure.Persistence.MatchSaveStore(saveRoot);
                    var rows = new List<object>(); var ids = new HashSet<string>();
                    for (int i = 0; i < YC.Infrastructure.Persistence.MatchSaveStore.SlotCount; i++)
                    {
                        string path = Path.Combine(saveRoot, (i < 3 ? "manual-" : "auto-") + (i % 3 + 1) + ".json");
                        if (!File.Exists(path)) continue;
                        var data = store.Read(i);
                        if (!ids.Add(data.GameId)) throw new InvalidDataException("实际测试存档出现同局重复槽");
                        rows.Add(new { Slot = i, data.GameId, data.ContentHash, data.SavedUtcTicks, data.Round,
                            ArchiveChars = data.ArchiveJson?.Length, ContinuationChars = data.ContinuationJson?.Length,
                            FileBytes = new FileInfo(path).Length, FullReadAndValidate = true });
                    }
                    return rows.ToArray();
                });
                while (!read.IsCompleted) yield return null;
                if (read.IsFaulted) throw read.Exception;
                Write("最终实际存档完整读取.json", new { Passed = true, Slots = read.Result, ObservationAfterAllMemoryWindows = true });
                PerformanceRecord("save.final_read.end", new { Slots = read.Result.Length, FullReadAndValidate = true });
            }
            UnityEngine.Application.quitting += PerformanceOnQuitting;
            yield return Press(FindObjectOfType<StartMenuView>().QuitGameButton, "原主菜单退出游戏");
            if (!finished) yield return PerformanceWait(() => finished, "原退出按钮的真实quitting事件", 15f);
        }
        private void PerformanceReleaseDriverReferences()
        {
            // 只释放取证器拥有的观察/续接引用；不清游戏历史，不干预产品保存队列或静态owner。
            PerformanceRecord("driver.menu_references_release", new { Gateways = observedGateways.Count, Records = performanceCommandRecords.Count, QuickPending = driverQuickPending.Count, QuickPendingUnsettled = driverQuickPending.Count(value => value.Coverage?.EffectSettled != true),
                SavedQueueTouched = false, GameHistoryCleared = false });
            if (session != null && performanceMemoryDetail)
            {
                session.ExecutionTimings.SubmitAllocationObserved -= PerformanceOnSubmitAllocation;
                session.ExecutionTimings.CaptureSubmitAllocations = false;
            }
            observedGateways.Clear(); performanceCommandRecords.Clear(); commands.Clear();
            pendingActionReceipts.Clear(); verifiedActionReceipts.Clear(); effectBuildPendingEntries.Clear(); stylePlacementCache.Clear();
            driverQuickPending.Clear();
            preparedMainAction = null; pendingMoveCompletion = null; paymentSourceContinuation = null; effectBuildPaymentClick = null;
            explorationPlayPlan = null; lastStylePlacement = null;
            session = null; registry = null; mobile = null; hud = null; performanceSubmission = null;
            performanceDriverReferencesReleased = true;
            // 只列本驱动已审字段，不声称涵盖托管栈/TLS/所有Unity根。
            PerformanceRecord("driver.reference_audit", new { SessionField = session != null, MobileField = mobile != null,
                SubmissionField = performanceSubmission != null, GatewayCount = observedGateways.Count, CommandCount = commands.Count,
                RecordCount = performanceCommandRecords.Count, QuickPendingCount = driverQuickPending.Count, ClearingPendingIsNotCompletion = true, KnownContinuationCount = (preparedMainAction == null ? 0 : 1) + (pendingMoveCompletion == null ? 0 : 1) +
                    (paymentSourceContinuation == null ? 0 : 1) + (explorationPlayPlan == null ? 0 : 1),
                CoroutineStackAndTlsFullyEnumerated = false, ProductRootsCleared = false });
            PerformanceRecord("driver.menu_references_released", new { SessionReference = false, GatewayReferences = observedGateways.Count,
                SaveOwner = PerformanceSaveState() });
        }

        private void PerformanceOnQuitting()
        {
            performanceQuitObserved = true;
            UnityEngine.Application.quitting -= PerformanceOnQuitting;
            PerformanceFinish(null);
        }

        private bool PerformanceCityBindingVisible(string id)
        {
            if (State.GameId != performanceGameId) return false;
            var placement = State.Map.Facilities.SingleOrDefault(value => value.ContentInstanceId == id);
            if (placement == null || placement.PlayerId != LocalPlayer) return false;
            foreach (var panel in FindObjectsOfType<BuildInfoPanel>())
                if (Read(panel, "cityBoardSlotBindings") is IEnumerable values)
                    foreach (var value in values)
                    {
                        if ((string)Read(value, "ContentInstanceId") != id || (string)Read(value, "GameId") != State.GameId ||
                            (int)Read(value, "PlayerId") != placement.PlayerId || (int)Read(value, "SlotIndex") != placement.CityBoardSlotIndex) continue;
                        var item = Read(value, "Item") as BuildInfoItemView; var sprite = Read(value, "CardSprite") as Sprite;
                        var raw = item?.RawImage;
                        if (item == null || item.Kind != BuildInfoItemKind.CardContent || sprite == null || sprite.texture == null || raw == null ||
                            !item.gameObject.activeInHierarchy || !raw.isActiveAndEnabled || raw.canvasRenderer.cull || raw.color.a <= 0 ||
                            raw.GetComponentsInParent<CanvasGroup>().Any(group => group.alpha <= 0) || raw.texture != sprite.texture) continue;
                        // 与原CardArtworkView所绑定的同一贴图/子图一致；不按卡名或相似贴图判成功。
                        var rect = sprite.textureRect;
                        var expectedUv = new Rect((rect.x + .5f) / sprite.texture.width, (rect.y + .5f) / sprite.texture.height,
                            (rect.width - 1f) / sprite.texture.width, (rect.height - 1f) / sprite.texture.height);
                        if (raw.uvRect != expectedUv) continue;
                        // RawImage本身不一定接收点击；用同一城市槽的真实首命中检查画面未被其它页面遮住。
                        var slot = Read(value, "Slot") as BuildInfoSlotView;
                        var first = FirstUiClickTarget(UiPointerPoint(raw.gameObject));
                        bool visible = slot != null && item.transform.IsChildOf(slot.transform) && first != null &&
                            (first == slot.PointerInteraction?.gameObject || first == slot.Button?.gameObject);
                        if (!visible) continue;
                        PerformanceRecord("build.actual_raw_binding", new { Instance = id, State.GameId, placement.PlayerId,
                            placement.CityBoardSlotIndex, TextureId = raw.texture.GetInstanceID(), SpriteId = sprite.GetInstanceID(),
                            Uv = raw.uvRect.ToString(), VisibleToCamera = visible, FirstPixelClaimed = false });
                        return true;
                    }
            return false;
        }

        private bool PerformanceBuildCompleted(string id)
        {
            if (State.GameId != performanceGameId) return false;
            var node = State.EffectRuntime.EffectNodes.SingleOrDefault(value => value.EffectId == performanceBuildEffectId);
            var selected = node?.ContinuationData?.Properties?.FirstOrDefault(value => value.Name == "selectedItemIds")?.Value;
            return node?.Status == EffectNodeStatus.Completed && node.EffectTypeId == "effect.facility.build" && node.PlayerId == LocalPlayer &&
                selected?.Kind == NormalizedValueKind.Array && selected.Items.Count == 1 &&
                selected.Items[0].Kind == NormalizedValueKind.String && selected.Items[0].StringValue == id;
        }

        private void PerformanceRecordPlacedFacility(string id)
        {
            performanceBuildInstance = id;
            PerformanceRecord("build.placed", new { Instance = id, Effect = performanceBuildEffectId });
        }

        private void PerformanceFinish(Exception failure)
        {
            if (finished) return;
            PerformanceRecord("sample.finish", new { Error = failure?.ToString() });
            Write("性能采样结果.json", new { Pid = performanceProcess?.Id, Stage = stage, Error = failure?.ToString(),
                Completed = failure == null, RealGameHealthyClaimed = false, performanceHarvestCompleted, performanceHarvestSequence,
                Inputs = performanceInputs, Submissions = performanceSubmissions, SuccessfulSubmissions = performanceSuccessfulSubmissions, HarvestButtons = performanceHarvests,
                DriverRows = performanceRows, DriverWriteMs = performanceWriteTicks * 1000d / Clock.Frequency,
                DriverPulseMsIncludingWrite = performancePulseTicks * 1000d / Clock.Frequency,
                DriverSelectionQueriesMs = performanceSelectorTicks * 1000d / Clock.Frequency,
                RuntimeErrors = errors.ToArray(), FullGameClaimed = false, ForcedGc = false, StateInjection = false,
                NormalMenuExit = performanceNormalMenuReturned, QuittingObserved = performanceQuitObserved,
                CompleteBuildBound = performanceBuiltBound, QuickTabs = performanceQuickTabsDone,
                Sustained = performanceSustained, StopReason = performanceStopReason, OperationsElapsed = performanceOperationsElapsed,
                InputStepWallSeconds = performanceInputStepSeconds, NoInputStepWallSeconds = performanceNoInputSeconds,
                InputIncludesReceiptWait = true,
                MemoryComparison = performanceMemoryComparison, DriverReferencesReleased = performanceDriverReferencesReleased,
                SnapshotBoundaries = performanceCapturedBoundaries.ToArray(), RequestedSnapshotBoundaries = performanceSnapshotBoundaries.ToArray(),
                SaveOwner = PerformanceSaveState(), ExternalInputFrames = performanceExternalInputFrames,
                SnapshotAttempted = performanceSnapshotAttempted, SnapshotPauseSeconds = performanceSnapshotPauseSeconds,
                CumulativeOuterSubmitAllocated = performanceMemoryDetail && performanceOuterSubmitAllocationCalls > performanceMissingSubmitAllocations ? (long?)performanceOuterSubmitAllocated : null,
                OuterSubmitAllocationCalls = performanceOuterSubmitAllocationCalls, MissingSubmitAllocations = performanceMissingSubmitAllocations,
                HarvestMapObservationMs = performanceHarvestObservationTicks * 1000d / Clock.Frequency,
                PreserveWindowOnFailure = failure != null,
                Missing = new[] { "未将绑定帧当作首个可见像素", "本轮性能与生命周期样本不等于完整对局验收" } });
            performanceLog?.Dispose(); performanceLog = null;
            if (performanceMemoryDetail && session != null)
            {
                session.ExecutionTimings.CaptureSubmitAllocations = false;
                session.ExecutionTimings.SubmitAllocationObserved -= PerformanceOnSubmitAllocation;
            }
            performanceProcess?.Dispose();
            finished = true;
        }
    }
}
#endif
