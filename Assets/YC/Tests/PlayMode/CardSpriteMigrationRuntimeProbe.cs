using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using YC.Domain.Interactions;
using YC.Domain.SpecialActions;
using YC.Presentation;
using YC.Presentation.Workflows;

namespace YC.Tests.PlayMode
{
    /// <summary>显式命令行启用的只读组件验收；使用实际场景 Registry，不保存或重建界面资产。</summary>
    public sealed class CardSpriteMigrationRuntimeProbe : MonoBehaviour
    {
        private string output;
        private int errors;
        private readonly List<string> errorMessages = new List<string>();
        private JObject report;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Launch()
        {
            var arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--yc-card-sprite-check="));
            if (arg == null) return;
            var runner = new GameObject("卡牌切片验收").AddComponent<CardSpriteMigrationRuntimeProbe>();
            runner.output = Path.GetFullPath(arg.Substring(arg.IndexOf('=') + 1));
            DontDestroyOnLoad(runner.gameObject);
        }

        private IEnumerator Start()
        {
            Directory.CreateDirectory(output);
            UnityEngine.Application.logMessageReceived += OnLog;
            report = new JObject { ["scene"] = "SampleScene", ["componentDiagnostic"] = true,
                ["note"] = "使用实际场景及其 Registry 的运行实例；交互回调仅记录验收结果，不提交游戏命令。" };
            var routine = Run();
            while (true)
            {
                object current = null;
                bool next;
                try { next = routine.MoveNext(); if (next) current = routine.Current; }
                catch (Exception error) { report["failure"] = error.ToString(); Finish(false); yield break; }
                if (!next) break;
                yield return current;
            }
            Finish(errors == 0);
        }

        private void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                errors++;
                errorMessages.Add(message);
            }
        }

        private IEnumerator Run()
        {
            yield return SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            for (var i = 0; i < 20; i++) yield return null;
            var hud = FindObjectOfType<GameplayInteractionHudView>();
            Require(hud != null, "实际场景未加载 HUD");
            var registry = hud.DialogRegistry;
            Require(registry.TryValidateConfiguration(out var reason), reason);
            var allComponents = SceneManager.GetActiveScene().GetRootGameObjects()
                .SelectMany(g => g.GetComponentsInChildren<Component>(true));
            Require(allComponents.All(c => c != null), "实际场景含 Missing Script");
            report["registry"] = registry.name;
            report["catalog"] = registry.CardVisualCatalog.name;
            var pack = ExternalContentRuntime.Pack;
            report["contentHash"] = pack.ContentHash;
            report["activeDefinitions"] = pack.ActiveDefinitions.Count;
            var sprites = pack.ArtworkSlices.Select(s => ExternalContentRuntime.GetRegisteredArtworkSprite(s.Id)).ToList();
            Require(sprites.Count == 94 && sprites.All(s => s != null), "94 个切片加载不完整");
            var textures = sprites.Select(s => s.texture).Distinct().ToList();
            Require(textures.Count == 7, "同整图没有共享纹理");
            report["textures"] = new JArray(textures.Select(t => new JObject {
                ["name"] = t.name, ["width"] = t.width, ["height"] = t.height, ["format"] = t.format.ToString(),
                ["runtimeBytes"] = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(t), ["readable"] = t.isReadable }));
            report["maxTextureSize"] = SystemInfo.maxTextureSize;
            report["graphicsDevice"] = SystemInfo.graphicsDeviceName;
            Require(registry.CardVisualCatalog.GetFacility("facility_enterprise_office") == null, "缺失企业规则的占位进入牌池");
            yield return Capture("01-实际场景与供应区");

            var viewer = registry.InstantiateCardViewer();
            var cases = new[] { ("facility", "building_022"), ("character", "elysium"),
                ("city_style", "city_style_material_relay_station"), ("event", "event_yellow_02") };
            foreach (var item in cases)
            {
                var sprite = ExternalContentRuntime.GetArtworkSprite(item.Item1, item.Item2);
                viewer.OpenInspect(sprite);
                for (var i = 0; i < 3; i++) yield return null;
                AssertSprite(viewer.CardImage, sprite);
                Require(Math.Abs(viewer.CardImage.rectTransform.rect.width / viewer.CardImage.rectTransform.rect.height - sprite.rect.width / sprite.rect.height) < .002f,
                    "详情单牌比例不正确：" + item.Item2);
                yield return Capture("详情-" + item.Item1);
                viewer.CancelButton.onClick.Invoke();
                Require(!viewer.IsShowing, "详情关闭按钮失效");
            }
            var canvas = (RectTransform)hud.Canvas.transform;
            var picker = new CharacterCardEffectChoiceDialog(registry, canvas);
            foreach (var ids in new[] { new[] { "liskarm", "texas", "tin-man", "cannot", "elysium" },
                new[] { "building_021", "building_022", "building_031", "building_032", "reserve_002", "reserve_003", "reserve_004" } })
            {
                string selected = null;
                picker.ShowSelection("切片验收", "实际卡牌选择组件", new EffectDialogSelectionSpec {
                    Request = new InteractionRequestProjection { InteractionId = "sprite-audit", Status = "open", VisibleToViewer = true,
                        CandidateIds = new List<string>(ids), MinSelections = 1, MaxSelections = 1 },
                    SelectedIds = Array.Empty<string>(), Label = id => id, IsCurrent = () => true,
                    Select = id => selected = id, IsEffectPage = false }, false);
                for (var i = 0; i < 5; i++) yield return null;
                var cards = FindObjectsOfType<FacilityEffectCardView>();
                Require(cards.Length == ids.Length, "选择窗口卡片数不匹配");
                foreach (var card in cards) Require(card.CardImage.texture != null && card.CardImage.uvRect.width < .6f, "选择窗口显示整版图");
                yield return Capture(ids[0] == "liskarm" ? "选择-角色" : "选择-设施与三色枢纽");
                cards[0].Button.onClick.Invoke();
                Require(ids.Contains(selected), "选择回调未触发");
                picker.Hide();
            }

            var cityStyle = new CityStyleDeclarationPreviewDialog(registry, () => canvas);
            var confirmed = 0;
            cityStyle.Show(new CityStyleOptionsViewModel(
                new List<CityStyleOptionViewModel> { new CityStyleOptionViewModel {
                    CityStyleId = "city_style_material_relay_station", Name = "切片验收", CanDeclare = true } }.AsReadOnly(),
                new List<CityBoardSlotViewModel> { new CityBoardSlotViewModel(0, "building_032", false) }.AsReadOnly(),
                new List<CityStyleMarkerViewModel>().AsReadOnly(), "city_style_material_relay_station",
                (id, slots) => new CityStyleSelectionValidationViewModel(true, string.Empty, 0, 0, slots.Count),
                (id, slots) => { confirmed++; return true; }, null, null));
            for (var i = 0; i < 5; i++) yield return null;
            var cityView = FindObjectOfType<CityStyleDeclarationPreviewView>();
            AssertSprite(cityView.CityStyleCardImage, registry.CardVisualCatalog.GetCityStyle("city_style_material_relay_station"));
            AssertSprite(cityView.GetCityBoardSlot(0).FacilityImage, registry.CardVisualCatalog.GetFacility("building_032"));
            yield return Capture("城市样式预览-横牌与设施");
            cityView.ConfirmDeclarationButton.onClick.Invoke();
            Require(confirmed == 1 && !cityStyle.IsShowing, "城市样式确认回调未执行或未关闭");

            var eventDialog = new EventChoiceDialog(registry, () => canvas);
            foreach (var id in new[] { "event_green_01", "event_yellow_02", "event_red_03" })
            {
                var chosen = -1;
                var eventCard = pack.CreateEvents().Single(e => e.CardId == id);
                eventDialog.ShowEventCardOptions(eventCard, "切片验收", Array.Empty<ExplorePaymentChoice>(),
                    new Dictionary<string, int>(), p => p.ToString(), choice => chosen = choice, null);
                for (var i = 0; i < 5; i++) yield return null;
                var view = FindObjectOfType<EventChoiceDialogView>();
                AssertSprite(view.EventCardArtworkImage, registry.CardVisualCatalog.GetEvent(id));
                var button = view.GetComponentsInChildren<Button>().First(b => b.name == "Choice 1");
                var corners = new Vector3[4]; ((RectTransform)button.transform).GetWorldCorners(corners);
                var point = (Vector2)((corners[0] + corners[2]) * .5f);
                var hits = new List<RaycastResult>();
                EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
                Require(hits.Any(h => h.gameObject == button.gameObject || h.gameObject.transform.IsChildOf(button.transform)), "事件选项点击位置不在实际按钮内");
                yield return Capture("事件选项-" + id);
                button.onClick.Invoke();
                // 红色第3张的牌面首项对应既有规则索引1，迁移不能改变既有选项语义。
                Require(chosen == (id == "event_red_03" ? 1 : 0), "事件选项回调错误");
                eventDialog.Hide();
            }
            // 拖拽使用正式公共工具、原布局配置与 Sprite，检查单格 UV 及输入穿透。
            viewer.OpenInspect(registry.CardVisualCatalog.GetFacility("building_032"));
            for (var i = 0; i < 3; i++) yield return null;
            var ghost = FacilityCardDragUtility.CreateDragGhost(canvas, viewer.CardImage.rectTransform,
                viewer.DisplayedSprite, string.Empty, null, registry.CardInteractionLayoutProfile.DragGhostLayout);
            ghost.position = new Vector3(Screen.width * .7f, Screen.height * .5f, 0);
            AssertSprite(ghost.GetComponent<RawImage>(), viewer.DisplayedSprite);
            Require(!ghost.GetComponent<RawImage>().raycastTarget && !ghost.GetComponent<CanvasGroup>().blocksRaycasts, "拖拽虚影阻挡输入");
            yield return Capture("拖拽-设施切片");
            FacilityCardDragUtility.DestroyDragGhost(ref ghost);
            viewer.Close();
            report["checks"] = new JArray("94切片/7纹理", "实际场景引用", "四类详情/关闭", "角色/设施选择回调", "三色事件选项/点击区域", "设施拖拽虚影");
        }

        private IEnumerator Capture(string name)
        {
            var path = Path.Combine(output, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            for (var i = 0; i < 8; i++) yield return null;
        }

        private static void AssertSprite(RawImage image, Sprite sprite)
        {
            Require(image != null && sprite != null && image.texture == sprite.texture, "卡图未绑定 Sprite 纹理");
            var r = sprite.rect;
            var expected = new Rect((r.x + .5f) / sprite.texture.width, (r.y + .5f) / sprite.texture.height,
                (r.width - 1) / sprite.texture.width, (r.height - 1) / sprite.texture.height);
            Require(image.uvRect == expected, "卡图未显示正确的 Sprite 区域");
        }

        private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        private void Finish(bool success)
        {
            report["success"] = success;
            report["errors"] = errors;
            report["errorMessages"] = new JArray(errorMessages);
            report["screenshots"] = new JArray(Directory.GetFiles(output, "*.png").Select(Path.GetFileName));
            File.WriteAllText(Path.Combine(output, "result.json"), report.ToString());
            UnityEngine.Application.logMessageReceived -= OnLog;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(success ? 0 : 1);
#else
            UnityEngine.Application.Quit(success ? 0 : 1);
#endif
        }
    }
}
