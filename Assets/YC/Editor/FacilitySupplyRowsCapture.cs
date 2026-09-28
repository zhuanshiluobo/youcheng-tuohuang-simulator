using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Facilities;
using YC.Domain.State;

namespace YC.Presentation.Editor
{
    /// <summary>只读捕获正式建设页；多牌供应位使用明确的运行时诊断夹具，不保存资产。</summary>
    public static class FacilitySupplyRowsCapture
    {
        private static readonly string Output = Path.GetFullPath("Logs/BuildPanel-20260928/supply-rows");
        private static int frames, step;
        private static double started;
        private static string pending;
        private static RectTransform candidates;
        private static object controller;
        private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static object Call(object target, string name) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(target, null);
        private static void View(string name, params object[] args) => typeof(GameplaySupplementalPageCapture).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);

        public static void Run()
        {
            Directory.CreateDirectory(Output);
            frames = step = 0; pending = null; controller = null; candidates = null;
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity", OpenSceneMode.Single);
            View("PrepareGameView"); View("SelectSize", new Vector2Int(1920, 1080));
            started = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }

        private static void Tick()
        {
            try
            {
                if (EditorApplication.timeSinceStartup - started > 180) throw new Exception("建设供应位捕获超时");
                if (!EditorApplication.isPlaying || Screen.width != 1920 || Screen.height != 1080) return;
                if (pending != null)
                {
                    if (!File.Exists(pending) || new FileInfo(pending).Length < 256) return;
                    pending = null;
                    if (++step == 2) { Stop(0); return; }
                    // 仅模拟一个供应位有两张牌，验证真实卡槽的首选高度会向外传递。
                    var slot = candidates.GetChild(0);
                    UnityEngine.Object.Instantiate(slot.GetChild(0).gameObject, slot, false);
                    frames = 0;
                }
                if (controller == null)
                {
                    controller = UnityEngine.Object.FindObjectOfType<MobileCityInteractionController>();
                    if (controller == null || GameplayHudFrame.Active == null) return;
                    var session = Field(controller, "session");
                    var state = (GameState)session.GetType().GetProperty("State").GetValue(session);
                    state.Decks.FacilitySupply.Clear();
                    state.Decks.FacilitySupply.AddRange(new[] {
                        FacilityCardDatabase.SimpleEngineeringCamp, FacilityCardDatabase.EscortDispatchCenter,
                        FacilityCardDatabase.TradeDistrict, FacilityCardDatabase.LogisticsHub,
                        FacilityCardDatabase.CityIndustrialDistrict, FacilityCardDatabase.MercenaryCommand,
                        FacilityCardDatabase.AffiliatedEnergyFacility, FacilityCardDatabase.IronRefinery });
                    Call(controller, "SynchronizeInteractionFromState");
                    Call(controller, "OnBuildActionClicked");
                    candidates = (RectTransform)Field(Field(controller, "facilityBuildPage"), "candidates");
                }
                Canvas.ForceUpdateCanvases();
                if (++frames < 30) return;
                var layout = candidates.GetComponent<UiWrappingRowLayout>();
                if (layout.Columns < 3) throw new Exception("候选足够时普通屏幕至少应有三列：" + layout.Columns);
                var text = new StringBuilder("实际屏幕：" + Screen.width + " × " + Screen.height + "\n");
                text.AppendLine("场景：Assets/Scenes/SampleScene.unity；页面由正式 Registry 创建，未重建或保存资产。");
                text.AppendLine("夹具：8 张供应牌；" + (step == 0 ? "每供应位 1 张" : "第一个供应位 2 张，其余各 1 张"));
                text.AppendLine("候选内容宽度：" + candidates.rect.width + "；列数：" + layout.Columns);
                for (var i = 0; i < candidates.childCount; i++)
                {
                    var slot = (RectTransform)candidates.GetChild(i);
                    text.AppendLine($"供应位 {i + 1}：位置 {slot.anchoredPosition}，尺寸 {slot.rect.size}");
                    var corners = new Vector3[4]; slot.GetWorldCorners(corners);
                    if (candidates.InverseTransformPoint(corners[0]).x < candidates.rect.xMin - .1f ||
                        candidates.InverseTransformPoint(corners[2]).x > candidates.rect.xMax + .1f)
                        throw new Exception("供应位横向越界：" + i);
                }
                var first = (RectTransform)candidates.GetChild(0);
                var secondRow = (RectTransform)candidates.GetChild(layout.Columns);
                var firstBottom = first.anchoredPosition.y - first.rect.height * first.pivot.y;
                var secondTop = secondRow.anchoredPosition.y + secondRow.rect.height * (1f - secondRow.pivot.y);
                if (secondTop > firstBottom + .1f)
                    throw new Exception("多牌供应位与下一行重叠");
                var card = first.GetComponentInChildren<FacilityEffectCardView>();
                text.AppendLine("首张卡面尺寸：" + card.CardImage.rectTransform.rect.size);
                var name = step == 0 ? "建设-自适应-1920x1080" : "建设-自适应多牌夹具-1920x1080";
                File.WriteAllText(Path.Combine(Output, name + ".txt"), text.ToString());
                pending = Path.Combine(Output, name + ".png");
                ScreenCapture.CaptureScreenshot(pending);
            }
            catch (Exception e) { Debug.LogException(e); Stop(1); }
        }

        private static void Stop(int code)
        {
            EditorApplication.update -= Tick;
            EditorApplication.ExitPlaymode();
            EditorApplication.delayCall += () => EditorApplication.Exit(code);
        }
    }
}
