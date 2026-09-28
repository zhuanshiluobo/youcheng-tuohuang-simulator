using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using System.Collections.Generic;

namespace YC.Presentation.Editor
{
    /// <summary>只读取实际场景来源并捕获运行实例；不生成或保存任何界面资产。</summary>
    public static class CardViewerCapture
    {
        private static readonly Vector2Int[] Sizes = { new Vector2Int(1920,1080), new Vector2Int(1920,1200),
            new Vector2Int(2560,1080), new Vector2Int(900,600), new Vector2Int(1920,1080) };
        private static string output, pending, signature, scene;
        private static int sizeIndex, mode, stable, limit;
        private static double started;
        private static DateTime captureRequestedUtc;
        private static CardViewer viewer;
        private static Texture texture;
        private static Rect inspectRect;
        private static bool applied;
        public static void RunBaseline() => Begin(1);
        public static void RunMatrix() => Begin(Sizes.Length);
        private static void Begin(int count)
        {
            scene = Environment.GetCommandLineArgs().Contains("--card-viewer-three") ? "ThreePlayerScene" : "SampleScene";
            var arguments = Environment.GetCommandLineArgs();
            var outputIndex = Array.IndexOf(arguments, "--card-viewer-output");
            var outputRoot = outputIndex >= 0 && outputIndex + 1 < arguments.Length
                ? arguments[outputIndex + 1] : "prompt/UI换新/执行记录/对局窗口整合";
            output = Path.GetFullPath(Path.Combine(outputRoot, scene));
            Directory.CreateDirectory(output);
            EditorSceneManager.OpenScene("Assets/Scenes/" + scene + ".unity", OpenSceneMode.Single);
            var registry = SceneManager.GetActiveScene().GetRootGameObjects()
                .SelectMany(o => o.GetComponentsInChildren<GameplayDialogRegistry>(true)).Single();
            if (!registry.TryValidateConfiguration(out var reason)) throw new InvalidOperationException(reason);
            File.WriteAllText(Path.Combine(output, "来源.txt"),
                "场景：" + scene + "\nRegistry：" + GlobalObjectId.GetGlobalObjectIdSlow(registry) +
                "\n源：" + AssetDatabase.GetAssetPath(registry.CardViewerPrefab) +
                "\n卡图：" + AssetDatabase.GetAssetPath(registry.CardVisualCatalog) +
                "\n基准普通查看来自实际手牌入口；使用模式为相同合法卡图的组件构图诊断，不代表正式命令成功。\n");
            InvokeSize("PrepareGameView"); InvokeSize("SelectSize", Sizes[0]);
            sizeIndex = mode = stable = 0; limit = count;
            pending = signature = null; viewer = null; texture = null; applied = false;
            started = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }
        private static void InvokeSize(string name, params object[] args) =>
            typeof(GameplaySupplementalPageCapture).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,args);
        private static void Tick()
        {
            try
            {
                if (EditorApplication.timeSinceStartup - started > 240) throw new TimeoutException("卡牌查看器捕获超时");
                if (!EditorApplication.isPlaying || Screen.width != Sizes[sizeIndex].x || Screen.height != Sizes[sizeIndex].y) return;
                if (pending != null)
                {
                    var captured = new FileInfo(pending);
                    if (!captured.Exists || captured.Length < 1024 || captured.LastWriteTimeUtc < captureRequestedUtc) return;
                    pending = null; applied = false; signature = null; stable = 0;
                    if (++mode == 3)
                    {
                        mode = 0;
                        if (++sizeIndex == limit) { Stop(0); return; }
                        InvokeSize("SelectSize", Sizes[sizeIndex]);
                    }
                    return;
                }
                var frame = GameplayHudFrame.Active;
                if (frame == null) return;
                if (!applied)
                {
                    if (texture == null)
                    {
                        var hand = UnityEngine.Object.FindObjectOfType<CharacterHandPanel>();
                        if (hand == null || hand.OrderedHand.Count == 0) return;
                        typeof(CharacterHandPanel).GetMethod("OpenCardViewer", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(hand, new object[] { "", hand.OrderedHand[0].CardId });
                        viewer = UnityEngine.Object.FindObjectOfType<CardViewer>();
                        if (viewer == null) throw new InvalidOperationException("手牌正式查看入口未打开 CardViewer");
                        texture = viewer.CardImage.texture;
                    }
                    if (mode == 0) viewer.OpenInspect(texture);
                    else viewer.OpenCharacter("component-diagnostic",texture,true,mode == 1,"","诊断禁用状态", () => true, _ => {}, () => {});
                    applied = true;
                }
                Canvas.ForceUpdateCanvases();
                if (viewer.CardImage.texture == null || viewer.Window.rect.height < 1) return;
                var geometry = string.Join("|", viewer.GetComponentsInChildren<RectTransform>()
                    .Select(r => r.name + ScreenRect(r).ToString("F2")));
                if (geometry != signature) { signature = geometry; stable = 0; return; }
                if (++stable < 6) return;
                var cardRect = ScreenRect(viewer.CardImage.rectTransform);
                if (mode == 0) inspectRect = cardRect;
                else if (Vector2.Distance(inspectRect.position,cardRect.position) > .5f ||
                    Vector2.Distance(inspectRect.size,cardRect.size) > .5f)
                    throw new InvalidOperationException("两种模式卡图矩形不一致");
                var report = new Report { scene = scene, mode = mode == 0 ? "inspect" : "use-character（组件诊断）",
                    size = new Vector2(Screen.width,Screen.height), content = ScreenRect(frame.ContentRect),
                    window = ScreenRect(viewer.Window), card = cardRect,
                    settingsFirstHit = First(frame.SettingsButton.transform as RectTransform),
                    endActionFirstHit = First(frame.EndActionButton.transform as RectTransform),
                    actionsHidden = !viewer.PlotButton.gameObject.activeInHierarchy,
                    plot = ScreenRect(viewer.PlotButton.transform as RectTransform),
                    strategy = ScreenRect(viewer.StrategyButton.transform as RectTransform) };
                foreach (var rect in viewer.GetComponentsInChildren<RectTransform>())
                    report.objects.Add(new Geometry { name = rect.name, rect = ScreenRect(rect) });
                var stem = "CardViewer-"+sizeIndex+"-"+mode+"-"+Screen.width+"x"+Screen.height;
                File.WriteAllText(Path.Combine(output,stem+".json"), JsonUtility.ToJson(report,true));
                pending = Path.Combine(output,stem+".png");
                captureRequestedUtc = DateTime.UtcNow;
                ScreenCapture.CaptureScreenshot(pending);
            }
            catch(Exception e) { Debug.LogException(e); Stop(1); }
        }
        private static Rect ScreenRect(RectTransform rect)
        {
            var corners = new Vector3[4]; rect.GetWorldCorners(corners);
            return Rect.MinMaxRect(corners[0].x,corners[0].y,corners[2].x,corners[2].y);
        }
        private static string First(RectTransform rect)
        {
            var results = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = ScreenRect(rect).center },results);
            return results.Count == 0 ? "无命中" : results[0].gameObject.name;
        }
        [Serializable] private class Report
        {
            public string scene, mode, settingsFirstHit, endActionFirstHit;
            public Vector2 size;
            public Rect content,window,card,plot,strategy;
            public bool actionsHidden;
            public List<Geometry> objects = new List<Geometry>();
        }
        [Serializable] private class Geometry { public string name; public Rect rect; }
        private static void Stop(int code)
        {
            EditorApplication.update -= Tick;
            if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.ExitPlaymode();
            EditorApplication.delayCall += () => EditorApplication.Exit(code);
        }
    }
}
