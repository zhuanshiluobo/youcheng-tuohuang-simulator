#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Application.Sessions;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Presentation.Validation
{
    // 显式启用的只读采证；不点击、不刷新、不回答请求、不修改状态。
    public sealed class UiRegressionProbe : MonoBehaviour
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private string output, lastIdentity;
        private StreamWriter log;
        private bool pointerPending;
        private int pointerIndex;
        private int sampleUntilFrame;
        private GameSession timedSession;
        private bool previousTimingEnabled;
        private readonly List<object> pendingMetrics = new List<object>();
        private static object Read(object o, string n) => o?.GetType().GetField(n, Flags)?.GetValue(o) ?? o?.GetType().GetProperty(n, Flags)?.GetValue(o);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--yc-ui-regression-probe=", StringComparison.Ordinal));
            if (arg == null || FindObjectOfType<UiRegressionProbe>() != null) return;
            var path = arg.Substring("--yc-ui-regression-probe=".Length);
            if (!Path.IsPathRooted(path) || !Directory.Exists(path)) throw new InvalidOperationException("诊断输出必须是现有绝对目录");
            var go = new GameObject("只读UI回归采证"); DontDestroyOnLoad(go);
            var probe = go.AddComponent<UiRegressionProbe>(); probe.output = path;
            probe.log = new StreamWriter(Path.Combine(path, "原指针与分段.jsonl"), false) {AutoFlush = true};
            UiRegressionMetrics.Recorded += probe.Record; UiRegressionMetrics.Enabled = true;
        }
        private void Write(object value) => log?.WriteLine(JsonConvert.SerializeObject(value));
        private void Record(UiRegressionMetrics.Sample value) => pendingMetrics.Add(new { AtUtc = DateTime.UtcNow, Frame = Time.frameCount, Metric = value });
        private void FlushMetrics()
        {
            if (pendingMetrics.Count == 0) return;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var value in pendingMetrics) Write(value);
            pendingMetrics.Clear();
            Write(new { Frame = Time.frameCount, DiagnosticIoMilliseconds =
                (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency });
        }
        private static string PathOf(Transform t) => t == null ? null : t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;
        private void Update()
        {
            if (!Input.GetMouseButtonDown(0)) return;
            pointerPending = true;
            sampleUntilFrame = Time.frameCount + 12;
            StartCoroutine(CapturePointer(++pointerIndex));
            var hits = new List<RaycastResult>();
            EventSystem.current?.RaycastAll(new PointerEventData(EventSystem.current) {position = Input.mousePosition}, hits);
            Write(new { AtUtc = DateTime.UtcNow, Frame = Time.frameCount, PhysicalPointerDown = true,
                Position = new {x = Input.mousePosition.x, y = Input.mousePosition.y},
                FirstHit = PathOf(hits.FirstOrDefault().gameObject?.transform),
                Hits = hits.Take(8).Select(h => new {Object = PathOf(h.gameObject.transform), h.distance, h.depth,
                    ClickHandler = PathOf(ExecuteEvents.GetEventHandler<IPointerClickHandler>(h.gameObject)?.transform),
                    Button = h.gameObject.GetComponentInParent<Button>() is Button b ? new {b.enabled, b.interactable, Active = b.IsActive(), Interactable = b.IsInteractable()} : null,
                    Graphic = h.gameObject.GetComponent<Graphic>() is Graphic g ? new {g.enabled, g.raycastTarget, g.color.a} : null}).ToArray() });
        }
        private System.Collections.IEnumerator CapturePointer(int index)
        {
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(output, "物理指针-" + index.ToString("D3") + ".png"));
        }
        private static object Identity(GameStateView view) => view == null ? null : new {view.GameId, view.ViewerPlayerId, view.StateRevision, view.ContentPackHash, view.ViewerRole};
        private void LateUpdate()
        {
            FlushMetrics();
            var mobile = FindObjectOfType<MobileCityInteractionController>();
            if (mobile == null) return;
            var session = Read(mobile, "session") as GameSession;
            if (session != timedSession)
            {
                if (timedSession != null) timedSession.ExecutionTimings.Enabled = previousTimingEnabled;
                timedSession = session;
                previousTimingEnabled = session?.ExecutionTimings.Enabled ?? false;
                if (session != null) session.ExecutionTimings.Enabled = true;
            }
            if (Time.frameCount <= sampleUntilFrame || Time.frameCount % 120 == 0 || Time.unscaledDeltaTime >= .1f)
                Write(new { AtUtc = DateTime.UtcNow, Frame = Time.frameCount, FrameElapsedMilliseconds = Time.unscaledDeltaTime * 1000d,
                    AfterPointer = Time.frameCount <= sampleUntilFrame, Execution = session?.ExecutionTimings.Snapshot() });
            var hud = Read(mobile, "gameplayInteractionHud") as GameplayInteractionHudView;
            var snapshot = Read(hud?.Frame, "gameplaySnapshot") as GameStateView;
            var state = session?.State;
            var identity = state?.GameId + ":" + state?.EffectRuntime?.StateRevision + ":" + Read(mobile, "localPlayerId") + ":" + snapshot?.StateRevision + ":" + Read(mobile, "displayedCityPlayerId");
            if (identity == lastIdentity && !pointerPending) return;
            pointerPending = false; lastIdentity = identity;
            GameStateView published;
            using (UiRegressionMetrics.Measure("probe.read"))
                published = mobile.GetType().GetMethod("VisibleSaleState", Flags)?.Invoke(mobile, null) as GameStateView;
            var export = new {AtUtc = DateTime.UtcNow, Frame = Time.frameCount, Hud = Identity(snapshot), Published = Identity(published),
                HostIdentity = state == null ? null : new {state.GameId, Revision = state.EffectRuntime.StateRevision, state.ContentPackHash},
                LocalPlayerId = Read(mobile, "localPlayerId"), DisplayedCityPlayerId = Read(mobile, "displayedCityPlayerId"),
                CurrentRequest = Read(Read(mobile, "interactionRequestRouter"), "CurrentRequest"),
                PublishedRequests = published?.Interactions.Where(r => r.Status == "open").ToArray(),
                Viewers = FindObjectsOfType<CardViewer>().Select(v => new {v.Mode, v.IsShowing, Art = v.DisplayedSprite?.name}).ToArray(),
                Slots = hud == null ? null : ((RectTransform[])Read(hud.ExpansionUi, "entrepreneurSlotRoots"))?.Select(r => new {r.name,
                    Art = r.GetComponent<GameplayCardSlotView>().Artwork.texture?.name}).ToArray(),
                ActivePages = hud?.Frame.ContentRect.Cast<Transform>().Where(t => t.gameObject.activeInHierarchy).Select(t => t.name).ToArray()};
            Write(export);
            File.WriteAllText(Path.Combine(output, "最新显示身份.json"), JsonConvert.SerializeObject(export, Formatting.Indented));
        }
        private void OnDestroy()
        {
            if (timedSession != null) timedSession.ExecutionTimings.Enabled = previousTimingEnabled;
            UiRegressionMetrics.Recorded -= Record; UiRegressionMetrics.Enabled = false;
            FlushMetrics(); log?.Dispose(); log = null;
        }
    }
}
#endif
