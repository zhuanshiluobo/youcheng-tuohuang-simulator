using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Application.Gameplay;
using YC.Application.Sessions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;
using static YC.Domain.Effects.ItemTransferEffectExecutor;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 实际源Prefab + 正式非企业家Lua请求的技术隔离测试，不计正常菜单/开局或G1验收。
    public sealed class FacilitySupplySelectionUiTests
    {
        private GameObject canvas, hud;
        private object coordinator;
        private IInteractionRequestRenderer renderer;
        private GameState state;
        private GameStateView view;
        private EffectRegistry registry;
        private readonly List<GameCommand> commands = new List<GameCommand>();
        private string top, lower, other;
        private float artworkWidth;
        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static void Ignore<TValue>(TValue value) { }
        private static object Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
        private static object Property(object owner, string name) => owner.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(owner);
        private InteractionRequestProjection Request => InteractionRequestProjector.ProjectForPlayer(state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open"), 1);
        private Component Page => (Component)Field(coordinator, "page");
        private Button Confirm => (Button)Field(Page, "confirm");
        private Component[] Cards => Page.GetComponentsInChildren(T("FacilityEffectCardView")).Cast<Component>().ToArray();
        private Button Card(string id) => Cards.Select(c => (Button)Property(c, "Button"))
            .Single(b => b.GetComponents<Component>().Any(c => c != null && c.GetType().Name == "PlayerAutomationId" &&
                (string)c.GetType().GetField("Id").GetValue(c) == "supply.select." + id));

        [UnitySetUp]
        public IEnumerator PrepareActualPageAndGenericRequest()
        {
            commands.Clear();
            var theme = UnityEditor.AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset", T("UiThemeCatalog"));
            T("UiTheme").GetMethod("Initialize").Invoke(null, new object[] { theme });
            var pack = ExternalContentPack.Load(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets/Content/core"));
            TestContext.WriteLine("正式内容包ContentHash=" + pack.ContentHash);
            registry = new EffectRegistry { ContentPackHash = pack.ContentHash };
            GenericLuaEffectExecutor.Register(registry); ItemTransferEffectExecutor.Register(registry);
            var map = StaticMapDefinitions.CreateThreePlayerMap();
            var seed = new GameState { GameId = "qy017.supply.isolated." + Guid.NewGuid().ToString("N"), MapId = map.MapId, ContentPackHash = pack.ContentHash,
                Phase = GamePhase.ActionRound1, Round = 1, ActionRound = 1, CurrentPlayerId = 1, StartPlayerId = 1,
                RuleConfiguration = new GameRuleConfiguration { PlayerCount = 3, MapId = map.MapId, ContentPackHash = pack.ContentHash },
                Players = Enumerable.Range(1, 3).Select(id => new PlayerState { PlayerId = id, Resources = new ResourceSet() }).ToList(),
                ContentDefinitions = new GameContentDefinitions { Facilities = pack.CreateFacilities().ToList() } };
            state = new ItemZoneStateFactory().CreateForSetup(seed, map, new[] {
                new ItemRecipeEntry { EntryId = "blue", DefinitionId = "building_015", TypeId = "core.facility_card", Quantity = 4, InitialZoneRole = "facility_deck" }
            });
            registry.Register("qy017.isolated.position", c => {
                var moved = new ItemTransferService().RelocateBatch(c, BatchRequest(c.Node.NormalizedArguments));
                return EffectStepResult.Completed(Object("movedCount", NormalizedValue.CreateInteger(moved.MovedCount)));
            });
            var instances = state.ItemZones.Items.Where(i => i.DefinitionId == "building_015").OrderBy(i => i.ItemId).ToArray();
            lower = instances[0].ItemId; top = instances[1].ItemId; other = instances[2].ItemId;
            Move(lower, 0); Move(top, 0); Move(other, 1);
            var source = "return function(ctx) return {Effect.Choice({player=ctx.playerId,options={'" + top + "','" + other +
                "'},minSelections=1,maxSelections=1,interactionType='facility.supply.select'})} end";
            var snap = LuaFacadeSnapshot.FromInvocation("p1", 0, 0, 3, "1.0.0");
            var result = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("qy017.generic.supply", "qy017.generic", "qy017.generic", "1.0.0", source,
                LuaContentHasher.ComputeSha256(source)), new LuaInvocationContext("qy017.generic", "AbilityInvoked", "p1", state.EffectRuntime.StateRevision, "1.0.0", 3, 0, 0, snap));
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(result, new LuaEffectCompilationContext { State = state, Registry = registry, PlayerId = 1, SourceId = "qy017.generic" },
                out var specs, out var fault), Is.True, fault?.Diagnostic);
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(specs.Single()); Assert.That(engine.RunUntilQuiescent().Faulted, Is.False);
            view = GameStateViewProjector.ProjectForPlayer(state, 1);
            canvas = new GameObject("隔离供应选择画布", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            // HUD本身拥有多个根Canvas，按实际场景作为根实例，不能再嵌入测试Canvas导致根尺寸为零。
            hud = UnityEngine.Object.Instantiate(prefab);
            var dialogs = hud.GetComponentInChildren(T("GameplayDialogRegistry"), true);
            var catalog = Property(dialogs, "CardVisualCatalog");
            artworkWidth = ((Sprite)catalog.GetType().GetMethod("GetFacility").Invoke(catalog, new object[] { "building_015" })).rect.width;
            coordinator = Activator.CreateInstance(T("FacilitySupplySelectionUiCoordinator"), new object[] {
                new Func<GameStateView>(() => view), new Func<int>(() => 1), dialogs, (RectTransform)canvas.transform,
                new Action<GameCommand>(commands.Add), new Func<bool>(() => true) });
            renderer = (IInteractionRequestRenderer)coordinator;
            Assert.That(renderer.CanRender(Request), Is.True); renderer.Render(Request);
            yield return null; Canvas.ForceUpdateCanvases(); yield return null;
            Assert.That(((RectTransform)Page.transform).rect.width, Is.GreaterThan(0f));
            Assert.That(((RectTransform)Page.transform).rect.height, Is.GreaterThan(0f));
            var output = Environment.GetEnvironmentVariable("YC_QY017_SCREENSHOT_DIRECTORY");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                ScreenCapture.CaptureScreenshot(Path.Combine(output, TestContext.CurrentContext.Test.Name + ".png"));
                TestContext.WriteLine("页面尺寸=" + ((RectTransform)Page.transform).rect + "；候选尺寸=" + ((RectTransform)Field(Page, "candidates")).rect);
                yield return null;
            }
        }

        private void Move(string itemId, int slot)
        {
            var item = state.ItemZones.Items.Single(i => i.ItemId == itemId);
            var zone = FacilityLayerQuery.Supply(state).Single(s => s.SlotIndex == slot).SlotZoneId;
            var request = ItemTransferService.CreateBatch(state, 1, "qy017.position." + Guid.NewGuid().ToString("N"), new[] {
                new ItemTransferRequest { ExecutingPlayerId = 1, ItemIds = new List<string> { itemId }, RequiredCount = 1,
                    ExpectedSource = new ItemPositionRef { ZoneId = item.ZoneId }, Destination = new ZoneRef { ZoneId = zone },
                    InsertionKind = "top", OperationProfileId = "core.facility_card_transfer" }
            });
            var engine = new EffectTreeExecutor(state, registry); engine.CreateRoot(EffectSpec.Create("qy017.isolated.position", ItemTransferService.BatchArguments(request), 1));
            Assert.That(engine.RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
        }

        private string SupplyIdentity() => string.Join("|", FacilityLayerQuery.Supply(state).Select(s => s.SlotZoneId + ":" + string.Join(",", s.ItemIds)));

        [UnityTest]
        public IEnumerator OriginalConfirmSubmitsOnlyOneAnswerForExactTopEvenWithNoMoney()
        {
            var supply = SupplyIdentity(); Assert.That(Card(lower).interactable, Is.False); Assert.That(Card(top).interactable, Is.True);
            Assert.That(Card(other).interactable, Is.True); Assert.That(Confirm.interactable, Is.False);
            Card(top).onClick.Invoke(); yield return null;
            Assert.That(commands, Is.Empty); Assert.That(Confirm.interactable, Is.True);
            Confirm.onClick.Invoke(); Confirm.onClick.Invoke(); Assert.That(commands.Count, Is.EqualTo(1));
            var command = commands.Single(); Assert.That(command.Kind, Is.EqualTo(GameCommandKind.AnswerInteraction));
            Assert.That(command.OptionIds, Is.EqualTo(new[] { top }));
            Assert.That(SupplyIdentity(), Is.EqualTo(supply));
            Assert.That(new AnswerInteractionCommandHandler(registry).Handle(state, command).Succeeded, Is.True);
            Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False);
            Assert.That(SupplyIdentity(), Is.EqualTo(supply));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == FacilityEntryEffectTypeIds.Build || n.EffectTypeId == ResourceEffectTypeIds.Pay), Is.False);
            Assert.That(state.FindPlayer(1).Resources.GoldVoucher, Is.Zero); Assert.That(state.Map.Facilities, Is.Empty);
        }

        [UnityTest]
        public IEnumerator StaleRevisionWrongPlayerAndChangedTopCannotSubmitOldDraft()
        {
            Assert.That(renderer.CanRender(InteractionRequestProjector.ProjectForPlayer(state.EffectRuntime.InteractionRequests.Last(), 2)), Is.False);
            Card(top).onClick.Invoke(); yield return null; view.StateRevision++;
            Confirm.onClick.Invoke(); Assert.That(commands, Is.Empty);
            view = GameStateViewProjector.ProjectForPlayer(state, 1); renderer.Clear(); renderer.Render(Request); yield return null;
            var first = Cards.First(c => ((Button)Property(c, "Button")).interactable); ((Button)Property(first, "Button")).onClick.Invoke(); yield return null;
            Move(state.ItemZones.Items.Single(i => i.DefinitionId == "building_015" && i.ZoneId == state.ItemZones.Zones.Single(z => z.LocalZoneId == "facility_deck").ZoneId).ItemId, 0);
            view = GameStateViewProjector.ProjectForPlayer(state, 1);
            Confirm.onClick.Invoke(); Assert.That(commands, Is.Empty); renderer.Render(Request);
            Assert.That(Page.gameObject.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator VisibleLayersUse125SourcePixelsAndIncludeStackInLayout()
        {
            var stack = Page.GetComponentsInChildren(T("FacilitySupplyStackLayout")).Cast<Component>()
                .Single(s => s.GetComponentsInChildren(T("FacilityEffectCardView")).Length == 2);
            var cards = stack.GetComponentsInChildren(T("FacilityEffectCardView")).Cast<Component>().ToArray();
            var a = (RawImage)Property(cards[0], "CardImage"); Assert.That(a.texture, Is.Not.Null);
            var expected = 125f * a.rectTransform.rect.width / artworkWidth;
            var rects = cards.Select(c => (RectTransform)Property(c, "CardRect")).ToArray();
            Assert.That(Mathf.Abs(rects[0].anchoredPosition.y - rects[1].anchoredPosition.y), Is.EqualTo(expected).Within(.2f));
            Assert.That(LayoutUtility.GetPreferredHeight((RectTransform)stack.transform), Is.GreaterThan(rects[0].rect.height));
            var output = Environment.GetEnvironmentVariable("YC_QY017_SCREENSHOT_DIRECTORY");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                var path = Path.Combine(output, "供应纯选择-实际源资产-隔离渲染.png");
                CaptureCanvas(path); Assert.That(File.Exists(path), Is.True);
            }
            yield return null;
        }

        private void CaptureCanvas(string path)
        {
            // batchmode不输出屏幕帧：使用相同像素视口离屏渲染源UI，不更改任何源资产。
            var width = Screen.width; var height = Screen.height;
            var cameraObject = new GameObject("隔离截图相机", typeof(Camera)); var camera = cameraObject.GetComponent<Camera>();
            var target = new RenderTexture(width, height, 24); var pixels = new Texture2D(width, height, TextureFormat.RGB24, false);
            var canvases = hud.GetComponentsInChildren<Canvas>(true).Where(c => c.isRootCanvas && c.renderMode == RenderMode.ScreenSpaceOverlay).ToArray();
            var previous = canvases.Select(c => new { canvas = c, mode = c.renderMode, camera = c.worldCamera, distance = c.planeDistance }).ToArray();
            var activeTarget = RenderTexture.active;
            try
            {
                camera.orthographic = true; camera.orthographicSize = height / 2f; camera.nearClipPlane = .1f; camera.farClipPlane = 100f;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black; camera.targetTexture = target;
                foreach (var canvas in canvases) { canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = 10f; }
                Canvas.ForceUpdateCanvases(); camera.Render(); RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0); pixels.Apply();
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { var bytes = pixels.EncodeToPNG(); file.Write(bytes, 0, bytes.Length); }
            }
            finally
            {
                foreach (var item in previous) { item.canvas.renderMode = item.mode; item.canvas.worldCamera = item.camera; item.canvas.planeDistance = item.distance; }
                RenderTexture.active = activeTarget; camera.targetTexture = null; target.Release();
                UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(pixels); UnityEngine.Object.Destroy(cameraObject);
            }
        }

        [UnityTest]
        public IEnumerator SameSourcePageRetainsNormalInspectionAndFreePlacementModes()
        {
            var dialogs = hud.GetComponentInChildren(T("GameplayDialogRegistry"), true);
            var page = Page; var catalog = Property(dialogs, "CardVisualCatalog"); var copy = Property(dialogs, "EffectDialogLayoutProfile");
            var options = new BuildFacilityOptionQueryService().Query(state, 1);
            int payment = 0, commit = 0;
            void Show(BuildFacilityDraftViewModel model, bool inspection)
            {
                page.GetType().GetMethod("RefreshSupplyStatus").Invoke(page, new object[] { state, 1, null });
                page.GetType().GetMethod("Show").Invoke(page, new object[] { model, state.Map.Facilities, 1, catalog, copy,
                    string.Empty, string.Empty, new Func<bool>(() => true), new Action<string>(_ => { }), new Action<int>(_ => { }),
                    new Action<string>(_ => payment++), new Action(() => commit++), new Action(() => { }), inspection });
            }
            BuildFacilityDraftViewModel Model(string phase, int slot, string mode, List<int> legal)
            {
                var constructor = typeof(BuildFacilityDraftViewModel).GetConstructors().Single();
                var dispatchType = constructor.GetParameters()[8].ParameterType;
                var dispatch = typeof(FacilitySupplySelectionUiTests).GetMethod(nameof(Ignore), BindingFlags.Static | BindingFlags.NonPublic)
                    .MakeGenericMethod(dispatchType.GetGenericArguments()[0]).CreateDelegate(dispatchType);
                return (BuildFacilityDraftViewModel)constructor.Invoke(new object[] {
                    Enum.Parse(constructor.GetParameters()[0].ParameterType, phase), options, null,
                    phase == "Placing" ? FacilityCardDatabase.Get(state, "building_015") : null, slot, mode, string.Empty,
                    legal, dispatch, true });
            }
            var selecting = Model("Selecting", -1, string.Empty, new List<int>());
            Show(selecting, false); yield return null;
            var normalStack = page.GetComponentsInChildren(T("FacilitySupplyStackLayout")).Cast<Component>()
                .Single(s => s.GetComponentsInChildren(T("FacilityEffectCardView")).Length == 2);
            var normalCards = normalStack.GetComponentsInChildren(T("FacilityEffectCardView")).Cast<Component>().ToArray();
            var normalRects = normalCards.Select(c => (RectTransform)Property(c, "CardRect")).ToArray();
            var normalImage = (RawImage)Property(normalCards[0], "CardImage");
            Assert.That(Mathf.Abs(normalRects[0].anchoredPosition.y - normalRects[1].anchoredPosition.y),
                Is.EqualTo(125f * normalImage.rectTransform.rect.width / artworkWidth).Within(.2f));
            Assert.That(((Button)Field(page, "confirm")).gameObject.activeSelf, Is.False);
            foreach (var row in ((Array)Field(page, "paymentRows")).Cast<object>()) Assert.That(((Component)row).gameObject.activeSelf, Is.True);
            Show(selecting, true); yield return null;
            Assert.That(((Button)Field(page, "confirm")).gameObject.activeSelf, Is.False);
            foreach (var row in ((Array)Field(page, "paymentRows")).Cast<object>())
            { var button = (Button)Property(row, "Button"); Assert.That(button.interactable, Is.False); button.onClick.Invoke(); }
            Assert.That(payment, Is.Zero); Assert.That(commit, Is.Zero);
            var placing = Model("Placing", 0, "free", new List<int> { 0 });
            Show(placing, false); yield return null;
            var confirm = (Button)Field(page, "confirm"); Assert.That(confirm.gameObject.activeSelf, Is.True); Assert.That(confirm.interactable, Is.True);
            confirm.onClick.Invoke(); Assert.That(commit, Is.EqualTo(1)); Assert.That(payment, Is.Zero);
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            (coordinator as IDisposable)?.Dispose(); if (hud != null) UnityEngine.Object.Destroy(hud); if (canvas != null) UnityEngine.Object.Destroy(canvas);
            coordinator = null; renderer = null; yield return null;
        }
    }
}
