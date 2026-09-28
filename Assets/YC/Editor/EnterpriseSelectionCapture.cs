using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation.Workflows;

namespace YC.Presentation.Editor
{
    /// <summary>读取实际场景 Registry 的组件诊断；没有业务提交，不生成或保存任何界面资产。</summary>
    public static class EnterpriseSelectionCapture
    {
        private static EnterpriseSelectionDialogView page;
        private static int step, stable;
        private static string signature, pending, source, phase;
        private static double started;
        private static bool matrix;
        private static readonly Vector2Int[] Sizes = { new Vector2Int(1920,1080), new Vector2Int(1920,1200),
            new Vector2Int(2560,1080), new Vector2Int(900,600), new Vector2Int(1920,1080) };
        private static readonly string Output = Path.GetFullPath("prompt/UI换新/执行记录/企业与科室");
        public static void RunBaseline() { Begin(false); }
        public static void RunFinalBaseline() { Begin(false); phase = "FinalBaseline"; }
        public static void RunMatrix() { Begin(true); }
        private static void Begin(bool sizes)
        {
            matrix = sizes; phase = sizes ? "Matrix" : "Baseline"; step = stable = 0; page = null; signature = pending = null;
            Directory.CreateDirectory(Output);
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
            InvokeGameView("PrepareGameView"); InvokeGameView("SelectSize", Sizes[0]);
            started = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }
        private static void InvokeGameView(string name, params object[] args) =>
            typeof(GameplaySupplementalPageCapture).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,args);
        private static void Tick()
        {
            try
            {
                if (EditorApplication.timeSinceStartup - started > 240) throw new Exception("企业组件捕获超时。");
                if (!EditorApplication.isPlaying) return;
                int mode = matrix ? (step < Sizes.Length ? 1 : 3) : step;
                var size = matrix ? Sizes[step % Sizes.Length] : Sizes[0];
                if (pending != null)
                {
                    if (!File.Exists(pending) || new FileInfo(pending).Length < 256) return;
                    pending = null; stable = 0; signature = null;
                    if (++step >= (matrix ? Sizes.Length * 2 : 4)) { Stop(0); return; }
                    if (matrix)
                    {
                        InvokeGameView("SelectSize", Sizes[step % Sizes.Length]);
                        if (step == Sizes.Length) { UnityEngine.Object.Destroy(page.gameObject); page = null; }
                    }
                    else { UnityEngine.Object.Destroy(page.gameObject); page = null; }
                    return;
                }
                if (Screen.width != size.x || Screen.height != size.y || GameplayHudFrame.Active == null) return;
                if (page == null)
                {
                    var registry = UnityEngine.Object.FindObjectOfType<GameplayDialogRegistry>();
                    if (registry == null) return;
                    source = AssetDatabase.GetAssetPath(mode >= 2 ? registry.DepartmentSelectionPrefab : registry.EnterpriseSelectionPrefab);
                    page = registry.InstantiateEnterpriseSelection(GameplayHudFrame.Active.ContentRect, mode >= 2);
                    page.Present(Diagnostic((EnterpriseSelectionMode)mode));
                    page.Choose("target-1");
                }
                Canvas.ForceUpdateCanvases();
                if (page.GetComponentsInChildren<EnterpriseBoardItemView>().Any(b => b.BoardImage.texture == null))
                    throw new Exception("诊断原板缺失，停止捕获。");
                var geometry = string.Join("|",page.GetComponentsInChildren<RectTransform>().Select(r=>ScreenRect(r).ToString("F2")));
                if (signature != geometry) { signature = geometry; stable = 0; return; }
                if (++stable < 6) return;
                var data = new Report { sourceAsset = source, requestedSize = size, actualSize = new Vector2(Screen.width,Screen.height),
                    mode = ((EnterpriseSelectionMode)mode).ToString(), selectedId = page.Draft.SelectedId,
                    entries = page.GetComponentsInChildren<RectTransform>().Select(r => new Entry { path = PathOf(r),
                        screenRect = ScreenRect(r), drivers = r.GetComponents<Component>().Where(c => c is ILayoutController || c is ILayoutElement).Select(c=>c.GetType().Name).ToArray() }).ToArray() };
                pending = Path.Combine(Output,"Component-"+phase+"-"+step+"-"+mode+"-"+Screen.width+"x"+Screen.height+".png");
                File.WriteAllText(Path.ChangeExtension(pending,".json"),JsonUtility.ToJson(data,true));
                ScreenCapture.CaptureScreenshot(pending);
            }
            catch(Exception ex) { Debug.LogException(ex); Stop(1); }
        }
        public static EnterpriseSelectionProjection Diagnostic(EnterpriseSelectionMode mode)
        {
            bool dep = mode >= EnterpriseSelectionMode.InitialDepartment;
            var keys = new[] { "department-defense", "department-engineering", "department-structure", "department-hr", "department-business", "department-research" };
            var labels = new[] { "防卫科", "工程科", "结构科", "人力科", "商务科", "科考科" };
            var p = new EnterpriseSelectionProjection { RequestId = "component-only", Revision = 1, Mode = mode,
                Context = "组件诊断数据 · 未提交正式命令", CanCancel = true,
                CurrentDepartmentId = mode == EnterpriseSelectionMode.SwitchDepartment ? "department-0" : null };
            p.Boards = Enumerable.Range(0, dep ? 6 : 4).Select(i=>new EnterpriseBoardProjection { Id = "board-"+i,
                VisualKey = dep ? keys[i] : "enterprise-"+i, Label = dep ? labels[i] : "企业原板 "+(i+1),
                PlayerLevels = dep ? null : new[] { 2,3,1,0 } }).ToArray();
            p.Targets = p.Boards.Select((b,i)=>new EnterpriseSelectionTarget { Id = "target-"+i, BoardId = b.Id,
                CompanyId = dep ? null : "company-"+i, EffectId = mode == EnterpriseSelectionMode.Effect ? "special-"+i : null,
                DepartmentId = dep ? "department-"+i : null, Label = b.Label, Available = i != 2, Tier = 2,
                X = 160f/650, Y = 1177f/1850, Width = 482f/650, Height = 225f/1850,
                UnavailableReason = i == 2 ? "诊断不可选原因：资格不足" : null,
                Description = string.Concat(Enumerable.Repeat("组件诊断：规则说明与支付条件必须来自正式玩家投影，不依据素材自动结算。\n",8)) }).ToArray();
            if(dep) p.FixedCompany = new EnterpriseBoardProjection { Id = "fixed-rhine", VisualKey = "enterprise-2",Label = "莱茵生命",PlayerLevels = new[]{0,0,0,0} };
            return p;
        }
        [Serializable] private sealed class Report
        {
            public bool componentDiagnostic = true, businessVerified = false;
            public string sourceAsset, mode, selectedId;
            public Vector2 requestedSize, actualSize;
            public Entry[] entries;
        }
        [Serializable] private sealed class Entry { public string path; public Rect screenRect; public string[] drivers; }
        private static string PathOf(Transform r) => r.parent == null ? r.name : PathOf(r.parent)+"/"+r.name;
        private static Rect ScreenRect(RectTransform r)
        {
            var corners = new Vector3[4]; r.GetWorldCorners(corners);
            var a=RectTransformUtility.WorldToScreenPoint(null,corners[0]); var b=RectTransformUtility.WorldToScreenPoint(null,corners[2]);
            return Rect.MinMaxRect(a.x,a.y,b.x,b.y);
        }
        private static void Stop(int code)
        { EditorApplication.update -= Tick; EditorApplication.ExitPlaymode(); EditorApplication.delayCall += ()=>EditorApplication.Exit(code); }
    }
}
