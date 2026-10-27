using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Infrastructure.Lua;
using YC.Presentation;
using YC.Presentation.Workflows;
using YC.Tests.EditMode.ExpansionRules;

namespace YC.Tests.EditMode
{
    // 原预制体实例的隔离行为检查。需要图形设备；不启动正常局、不改变资产或规则状态。
    public sealed class DeclarationReuseContractTests
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Read(object value, string name) => value.GetType().GetField(name, Flags)?.GetValue(value) ?? value.GetType().GetProperty(name, Flags)?.GetValue(value);
        private static object Call(object value, string name, params object[] args)
        {
            try { return value.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private readonly List<GameObject> huds = new List<GameObject>();
        private readonly List<UiRegressionMetrics.Sample> samples = new List<UiRegressionMetrics.Sample>();
        private IsolatedTestArtifacts artifacts;
        private object dialog;
        private bool previousMetrics;
        private int submits, cancels;
        private static string[] Ids => CityStyleDatabase.DefaultSupplyIds.Take(3).ToArray();
        private Component Page => (Component)Read(dialog, "view");
        private Component[] Rows => ((IEnumerable)Read(Page, "options")).Cast<Component>().ToArray();
        private Action ClickOf(Component row) => (Action)Read(Read(row, "PointerInteraction"), "singleClick");
        private string CurrentId => (string)Read(dialog, "CurrentCityStyleId");
        private void Record(UiRegressionMetrics.Sample value) => samples.Add(value);

        private (Component Registry, RectTransform Canvas) CreateHud()
        {
            var theme = AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset", T("UiThemeCatalog"));
            T("UiTheme").GetMethod("Initialize").Invoke(null, new[] { theme });
            var hud = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));
            huds.Add(hud);
            var frame = Read(hud.GetComponent(T("GameplayInteractionHudView")), "Frame");
            if (!ReferenceEquals(Read(frame, "active"), frame)) Call(frame, "Awake");
            return (hud.GetComponentInChildren(T("GameplayDialogRegistry"), true), (RectTransform)Read(frame, "ContentRect"));
        }
        [SetUp] public void Begin()
        {
            samples.Clear(); submits = cancels = 0;
            artifacts = IsolatedTestArtifacts.BeginCase();
            var ui = CreateHud();
            dialog = Activator.CreateInstance(T("CityStyleDeclarationPreviewDialog"), Flags, null,
                new object[] { ui.Registry, new Func<RectTransform>(() => ui.Canvas) }, null);
            previousMetrics = UiRegressionMetrics.Enabled;
            UiRegressionMetrics.Recorded += Record; UiRegressionMetrics.Enabled = true;
        }
        private CityStyleOptionsViewModel Model(IEnumerable<string> ids, int revision = 0,
            IReadOnlyList<CityStyleMarkerViewModel> markers = null, Func<CityStyleOptionsViewModel> refresh = null,
            string context = "QY017.R5.组件隔离")
        {
            var options = ids.Select(id => new CityStyleOptionViewModel { CityStyleId = id, Name = id, CanDeclare = true }).ToArray();
            return new CityStyleOptionsViewModel(options,
                new[] { new CityBoardSlotViewModel(0, FacilityCardDatabase.SourceStoneRefinery, false),
                    new CityBoardSlotViewModel(1, FacilityCardDatabase.EquipmentWarehouse, false) },
                markers, options.FirstOrDefault()?.CityStyleId ?? "",
                (id, slots) => new CityStyleSelectionValidationViewModel(slots.Count == 2, "", 0, 2, slots.Count),
                (id, slots) => { submits++; return false; }, null, () => cancels++,
                declareMode: true, refresh: refresh, contextKey: context, revision: revision);
        }

        [Test] public void ReopenReusesRowsButClearsDraftAndRejectsPreviousCallbacks()
        {
            var ids = Ids;
            Call(dialog, "Show", Model(ids.Take(2)));
            var firstPage = Page; var retained = Rows.Single(r => r.name == ids[1]);
            var oldClick = ClickOf(retained);
            var oldOpen = (Action)Read(retained.GetComponent(T("CityStyleCardGesture")), "open");
            var oldBack = (Action)Read(Read(Page, "detailGesture"), "back");
            Call(dialog, "ToggleSelectedSlot", 0);
            Assert.That((IReadOnlyList<int>)Read(dialog, "SelectedSlotIndexes"), Is.EqualTo(new[] { 0 }));
            Call(dialog, "Hide"); Assert.That(firstPage.gameObject.activeSelf, Is.False);
            Assert.That(cancels, Is.EqualTo(1));
            samples.Clear();
            var next = Model(new[] { ids[1], ids[2] }, 1, context: "QY017.R7.新上下文"); next.Options[0].CanDeclare = false;
            Call(dialog, "Show", next);
            Assert.That(Page, Is.SameAs(firstPage));
            Assert.That(Rows.Single(r => r.name == ids[1]), Is.SameAs(retained));
            Assert.That(Rows.Select(r => r.name), Is.EqualTo(new[] { ids[1], ids[2] }));
            Assert.That((IReadOnlyList<int>)Read(dialog, "SelectedSlotIndexes"), Is.Empty);
            Assert.That((bool)Read(dialog, "IsSelecting"), Is.False, "本次不可宣告候选不能沿用旧权限");
            Assert.That(samples.Count(s => s.Phase == "declare.instantiate"), Is.Zero);
            Assert.That(samples.Count(s => s.Phase == "declare.option.instantiate"), Is.EqualTo(1), "只创建本次新增的候选卡槽");
            oldClick(); Assert.That(CurrentId, Is.EqualTo(ids[1]), "上次索引1不能选择新页面索引1");
            oldOpen(); Assert.That((bool)Read(Page, "IsDetail"), Is.False);
            Call(dialog, "ConfirmDeclaration"); Assert.That(submits, Is.Zero, "新候选权限不能沿用上一页可用值");
            ClickOf(Rows.Single(r => r.name == ids[2]))(); Assert.That(CurrentId, Is.EqualTo(ids[2]));
            Call(dialog, "ToggleSelectedSlot", 0); Call(dialog, "ToggleSelectedSlot", 1);
            Call(dialog, "ConfirmDeclaration"); Assert.That(submits, Is.EqualTo(1), "新候选仍可按当前合法选择提交");
            ((Action)Read(Rows.Single(r => r.name == ids[2]).GetComponent(T("CityStyleCardGesture")), "open"))();
            Assert.That((bool)Read(Page, "IsDetail"), Is.True);
            oldBack(); Assert.That((bool)Read(Page, "IsDetail"), Is.True);
            ((Action)Read(Read(Page, "detailGesture"), "back"))();
            Assert.That((bool)Read(Page, "IsDetail"), Is.False);
            var lastClick = ClickOf(retained);
            Call(dialog, "Dispose"); lastClick();
            Assert.That(firstPage == null, Is.True, "退出控制器须释放保留的页面");
        }

        [Test] public void MarkerRefreshReusesInstancesAndMovesCountsToTheCurrentTrack()
        {
            var id = CityStyleDatabase.MilitaryIndustrialArea;
            var markers = new List<CityStyleMarkerViewModel> {
                new CityStyleMarkerViewModel(id, 1, PlayerColor.Red, "unused"),
                new CityStyleMarkerViewModel(id, 1, PlayerColor.Red, "unused") };
            CityStyleOptionsViewModel model = null;
            model = Model(new[] { id }, markers: markers, refresh: () => model);
            Call(dialog, "Show", model);
            var content = (RectTransform)Read(Page, "optionsContent");
            var first = content.GetComponentsInChildren<Image>(true).Single(i => i.name == "Player Marker Count");
            var previousSlot = first.transform.parent;
            Assert.That(first.GetComponentInChildren<Text>(true).text, Is.EqualTo("2"));
            markers.Clear();
            for (int i = 0; i < 3; i++) markers.Add(new CityStyleMarkerViewModel(id, 1, PlayerColor.Red, "used"));
            samples.Clear(); Call(dialog, "RefreshProjection", true);
            var current = content.GetComponentsInChildren<Image>(true).Single(i => i.name == "Player Marker Count");
            Assert.That(current, Is.SameAs(first)); Assert.That(current.transform.parent, Is.Not.SameAs(previousSlot));
            Assert.That(current.GetComponentInChildren<Text>(true).text, Is.EqualTo("3"));
            Assert.That(samples.Count(s => s.Phase == "declare.option.marker_instantiate"), Is.Zero);
            var usedSlot = current.transform.parent;
            markers.Clear(); markers.Add(new CityStyleMarkerViewModel(id, 2, PlayerColor.Blue, "used"));
            Call(dialog, "RefreshProjection", true);
            current = content.GetComponentsInChildren<Image>(true).Single(i => i.name == "Player Marker Count");
            var overlay = Rows[0].GetComponentInChildren(T("CityStyleTrackOverlay"), true);
            var visuals = Read(Read(Page, "CardBoardVisualLayout"), "CityStyleVisuals");
            Assert.That(current, Is.SameAs(first));
            Assert.That(current.transform.parent, Is.Not.SameAs(usedSlot));
            Assert.That(current.transform.parent, Is.SameAs(Call(overlay, "FindSlot", id, "used", (int)PlayerColor.Blue, visuals)));
            Assert.That(current.GetComponentInChildren<Text>(true).text, Is.EqualTo("1"));
            markers.Clear(); Call(dialog, "RefreshProjection", true);
            Assert.That(content.GetComponentsInChildren<Image>(true).Any(i => i.name == "Player Marker Count"), Is.False);
        }

        [Test] public void ArtworkIndexTracksFrozenContentAndDestroyedSprites()
        {
            // 展示缓存隔离：只替换本测试进程的冻结包引用，不是正常对局或新内容验收。
            var runtime = T("ExternalContentRuntime");
            var original = (ExternalContentPack)runtime.GetProperty("Pack").GetValue(null);
            var core = runtime.GetField("coreRoot", Flags).GetValue(null);
            var registry = runtime.GetField("registryRoot", Flags).GetValue(null);
            var reset = runtime.GetMethod("Reset", Flags);
            var packField = runtime.GetField("pack", Flags);
            var get = runtime.GetMethod("GetDeclaredArtworkSprite");
            const string id = "infected.city_style.district_management";
            const string path = "city_style/infected.city_style.district_management.json";
            Sprite Get() => (Sprite)get.Invoke(null, new object[] { "city_style", id });
            try
            {
                reset.Invoke(null, null); packField.SetValue(null, original); samples.Clear();
                var first = Get(); Assert.That(first, Is.Not.Null);
                var rect = first.rect; var dimensions = new Vector2Int(first.texture.width, first.texture.height);
                var copy = original.Definitions.Single(d => d.DefinitionId == id);
                copy.Enabled = false; copy.Artwork = "隔离副本不得污染冻结包";
                Assert.That(Get(), Is.SameAs(first));
                Assert.That(get.Invoke(null, new object[] { "facility", id }), Is.Null, "卡面键不能跨内容类型复用");
                Assert.That(samples.Count(s => s.Phase == "artwork.definition_index_build"), Is.EqualTo(1));
                UnityEngine.Object.DestroyImmediate(first);
                var recreated = Get(); Assert.That(recreated, Is.Not.Null);
                Assert.That(recreated.rect, Is.EqualTo(rect));
                Assert.That(new Vector2Int(recreated.texture.width, recreated.texture.height), Is.EqualTo(dimensions));

                var document = JObject.Parse(System.Text.Encoding.UTF8.GetString(original.GetArtworkBytes(path)));
                document["enabled"] = false;
                var changed = original.CreateWithOverrides(new Dictionary<string, string> { [path] = document.ToString() });
                Assert.That(changed.ContentHash, Is.Not.EqualTo(original.ContentHash));
                // 即使调用方更换了冻结包而没有复用页生命周期，旧启用项也不能从索引返回。
                packField.SetValue(null, changed); samples.Clear();
                Assert.That(Get(), Is.Null);
                Assert.That(samples.Count(s => s.Phase == "artwork.definition_index_build"), Is.EqualTo(1));
                // Reload使用的Reset必须同时释放图像与索引；相同旧包引用再装入也要重建。
                reset.Invoke(null, null); Assert.That(recreated == null, Is.True);
                packField.SetValue(null, original); samples.Clear();
                Assert.That(Get(), Is.Not.Null);
                Assert.That(samples.Count(s => s.Phase == "artwork.definition_index_build"), Is.EqualTo(1));
                artifacts.WriteLabeledText("冻结展示索引旧新内容", JsonConvert.SerializeObject(new {
                    original = original.ContentHash, changed = changed.ContentHash, id, path,
                    rect = new { rect.x, rect.y, rect.width, rect.height },
                    dimensions = new { dimensions.x, dimensions.y } }));
            }
            finally
            {
                reset.Invoke(null, null); packField.SetValue(null, original);
                runtime.GetField("coreRoot", Flags).SetValue(null, core);
                runtime.GetField("registryRoot", Flags).SetValue(null, registry);
            }
        }

        [Test] public void WarmRowsRebindCurrentResolverLabelsAndDisplayConfiguration()
        {
            var ids = Ids;
            var model = Model(ids.Take(2)); Call(dialog, "Show", model);
            var row = Rows[0]; var stale = ClickOf(Rows[1]);
            var registry = Read(dialog, "dialogRegistry");
            var catalog = UnityEngine.Object.Instantiate((ScriptableObject)Read(registry, "CardVisualCatalog"));
            var layout = UnityEngine.Object.Instantiate((ScriptableObject)Read(Page, "CardBoardVisualLayout"));
            var visuals = UnityEngine.Object.Instantiate((ScriptableObject)Read(layout, "CityStyleVisuals"));
            try
            {
                var resolved = ids[0];
                Call(catalog, "BindRuntimeDefinitions", new Func<string, string>(id => id == ids[0] ? resolved : id));
                int chosen = -1;
                Action<int> choose = index => chosen = index;
                Call(Page, "RenderOptions", model, catalog, choose);
                var oldUv = ((RawImage)Read(row, "RawImage")).uvRect;
                resolved = ids[1]; model.Options[0].Name = "本次动态名称"; model.Options[0].CanDeclare = false;
                var configured = Call(visuals, "Find", ids[0]);
                configured.GetType().GetField("trackLayout").SetValue(configured, "limited");
                layout.GetType().GetField("cityStyleVisuals", Flags).SetValue(layout, visuals);
                Page.GetType().GetField("cardBoardVisualLayout", Flags).SetValue(Page, layout);
                Call(Page, "RenderOptions", model, catalog, choose);
                Assert.That(Rows[0], Is.SameAs(row));
                var expected = (Sprite)Call(catalog, "GetCityStyle", ids[1]);
                var image = (RawImage)Read(row, "RawImage");
                Assert.That(image.texture, Is.SameAs(expected.texture));
                Assert.That(image.uvRect, Is.Not.EqualTo(oldUv), "同实例解析到另一当前定义时须重绑图像区域");
                Assert.That(((Text)Read(row, "FallbackText")).text, Is.EqualTo(model.Options[0].Name));
                Assert.That((bool)Read(row.GetComponent(T("CityStyleCardStateView")), "available"), Is.False);
                var overlay = row.GetComponentInChildren(T("CityStyleTrackOverlay"), true);
                foreach (var track in (IEnumerable)Read(overlay, "tracks"))
                    Assert.That(((GameObject)Read(track, "root")).activeSelf,
                        Is.EqualTo((string)Read(track, "layout") == "limited" || (string)Read(track, "layout") == "all"));
                var previous = CurrentId; stale(); Assert.That(CurrentId, Is.EqualTo(previous));
                ClickOf(row)(); Assert.That(chosen, Is.EqualTo(0));
            }
            finally
            {
                Call(dialog, "Dispose");
                UnityEngine.Object.DestroyImmediate(visuals); UnityEngine.Object.DestroyImmediate(layout);
                UnityEngine.Object.DestroyImmediate(catalog);
            }
        }

        [Test] public void PageCacheIsReleasedWhenTheActiveHudParentChanges()
        {
            Call(dialog, "Show", Model(Ids.Take(2))); var first = Page;
            Call(dialog, "Hide"); var nextHud = CreateHud();
            Call(dialog, "Show", Model(Ids.Take(2), 1));
            Assert.That(first == null, Is.True);
            Assert.That(Page.transform.parent, Is.SameAs(nextHud.Canvas));
            Assert.That((bool)Read(dialog, "IsShowing"), Is.True);
            // 场景卸载可能先销毁HUD，再通知控制器Dispose；保留活动落区以覆盖该销毁顺序。
            var targets = (IList)Read(dialog, "specialActionDropTargets");
            var binding = Activator.CreateInstance(targets.GetType().GetGenericArguments()[0], true);
            binding.GetType().GetField("Rect", Flags).SetValue(binding, new GameObject("隔离落区", typeof(RectTransform)).GetComponent<RectTransform>());
            ((RectTransform)Read(binding, "Rect")).SetParent(Page.transform, false); targets.Add(binding);
            UnityEngine.Object.DestroyImmediate(huds.Last());
            Assert.DoesNotThrow(() => Call(dialog, "Dispose"));
            Assert.That((bool)Read(dialog, "IsShowing"), Is.False);
        }

        [Test] public void UnchangedDuePollDoesNotRebuildButANewProjectionRebindsCandidates()
        {
            var ids = Ids;
            CityStyleOptionsViewModel current = null;
            current = Model(ids.Take(2), refresh: () => current);
            Call(dialog, "Show", current); var first = Rows; var stale = ClickOf(first[1]);
            samples.Clear();
            // 仅模拟UI轮询截止时间，规则状态/版本不改变；不是正常空闲帧时延采样。
            dialog.GetType().GetField("nextRefresh", Flags).SetValue(dialog, -1f);
            Call(dialog, "RefreshProjection", false);
            Assert.That(samples.Count(s => s.Phase == "declare.render_options"), Is.Zero);
            Assert.That(Rows, Is.EqualTo(first));
            current = Model(new[] { ids[1], ids[0] }, 1, refresh: () => current);
            dialog.GetType().GetField("nextRefresh", Flags).SetValue(dialog, -1f);
            Call(dialog, "RefreshProjection", false);
            Assert.That(samples.Count(s => s.Phase == "declare.render_options"), Is.EqualTo(1));
            ClickOf(Rows.Single(r => r.name == ids[1]))();
            var before = CurrentId; stale(); Assert.That(CurrentId, Is.EqualTo(before));
            ClickOf(Rows.Single(r => r.name == ids[0]))(); Assert.That(CurrentId, Is.EqualTo(ids[0]));
        }

        [TearDown] public void Finish()
        {
            UiRegressionMetrics.Recorded -= Record; UiRegressionMetrics.Enabled = previousMetrics;
            var model = dialog == null ? null : Read(dialog, "model") as CityStyleOptionsViewModel;
            artifacts?.WriteLabeledText("宣告复用实际组件与采样", JsonConvert.SerializeObject(new {
                scope = "原预制体隔离行为；不计正常局或视觉验收；无规则状态注入",
                test = TestContext.CurrentContext.Test.FullName, submits, cancels, samples,
                options = model?.Options, markers = model?.CityStyleMarkers, revision = model?.Revision,
                device = new { SystemInfo.graphicsDeviceType, SystemInfo.maxTextureSize } }));
            try { if (dialog != null) Call(dialog, "Dispose"); }
            finally
            {
                for (int i = huds.Count - 1; i >= 0; i--) if (huds[i] != null) UnityEngine.Object.DestroyImmediate(huds[i]);
                huds.Clear(); dialog = null; artifacts?.Finish(); artifacts = null;
            }
        }
    }
}
