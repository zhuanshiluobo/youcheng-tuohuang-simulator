using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Effects;
using YC.Domain.Facilities;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Infrastructure.Lua;
using YC.Presentation.Workflows;

namespace YC.Tests.PlayMode.ExpansionRules
{
    // 隔离技术测试：当前正式Lua、正式Host请求/答案和原建设页；不计正常对局。
    public sealed class FacilityPanelSelectionUiTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private GameObject hud;
        private object fixture, coordinator;
        private GameState state;
        private GameStateView visible;
        private EffectRegistry registry;
        private int viewer = 1;
        private readonly List<GameCommand> commands = new List<GameCommand>();
        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Read(object target, string name) => target.GetType().GetField(name, Flags)?.GetValue(target) ?? target.GetType().GetProperty(name, Flags)?.GetValue(target);
        private static object Call(object target, string name, params object[] args) => target.GetType().GetMethods(Flags).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target, args);
        private IInteractionRequestRenderer Renderer => (IInteractionRequestRenderer)coordinator;
        private object Page => Read(coordinator, "page");
        private InteractionRequest Request => state.EffectRuntime.InteractionRequests.Last(r => r.Status == "open");
        private InteractionRequestProjection Projection => InteractionRequestProjector.ProjectForPlayer(Request, viewer);
        private Button Confirm => (Button)Read(Page, "confirm");
        private Button[] Slots => (Button[])Read(Page, "citySlots");

        [UnitySetUp]
        public IEnumerator Prepare()
        {
            commands.Clear(); viewer = 1;
            var theme = UnityEditor.AssetDatabase.LoadAssetAtPath("Assets/YC/Presentation/Content/UiThemeCatalog.asset", T("UiThemeCatalog"));
            T("UiTheme").GetMethod("Initialize").Invoke(null, new object[] { theme });
            hud = UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"));
            fixture = Activator.CreateInstance(Type.GetType("YC.Tests.EditMode.ExpansionRules.PageSequenceFixContractTests, YC.Tests.EditMode", true));
            Call(fixture, "StartMartin");
            yield return null;
        }
        private void Create(bool martin)
        {
            state = (GameState)Call(fixture, "State", martin ? "entrepreneur.round.M.7" : "", false, null);
            registry = (EffectRegistry)Read(fixture, "Registry");
            if (martin) Call(fixture, "ActivateFace", state, "entrepreneur.round.M.7", "M11");
            var lower = (ItemInstanceState)Call(fixture, "Card", state, "building_015");
            Call(fixture, "Position", state, lower, FacilityLayerQuery.ResolveCell(state, 1, 0).CellZoneId, "unordered", 1);
            var upper = (ItemInstanceState)Call(fixture, "Card", state, "building_016");
            Call(fixture, "Overlay", state, upper, 0);
            var dialogs = hud.GetComponentInChildren(T("GameplayDialogRegistry"), true);
            var frame = Read(hud.GetComponent(T("GameplayInteractionHudView")), "Frame");
            coordinator = Activator.CreateInstance(T("FacilityPanelSelectionUiCoordinator"), Flags, null, new object[] {
                new Func<GameStateView>(() => visible), new Func<int>(() => viewer), dialogs, Read(frame, "ContentRect"),
                new Action<GameCommand>(commands.Add), new Func<bool>(() => true) }, null);
        }
        private void StartMartinPanel()
        {
            Create(true); Call(fixture, "Action", state, "M11");
            Assert.That(Request.InteractionTypeId, Is.EqualTo("action.decline_effect"));
            Call(fixture, "Answer", state, NormalizedValue.CreateString("continue"));
            Show();
        }
        private void Show()
        {
            visible = GameStateViewProjector.ProjectForPlayer(state, viewer);
            Assert.That(Request.InteractionTypeId, Is.EqualTo("facility.panel.select"));
            Assert.That(Renderer.CanRender(Projection), Is.True); Renderer.Render(Projection);
        }
        private void Apply()
        {
            var command = commands.Single(); var result = new AnswerInteractionCommandHandler(registry).Handle(state, command);
            Assert.That(result.Succeeded, Is.True, result.Validation?.Reason);
            Assert.That(new EffectTreeExecutor(state, registry).RunUntilQuiescent().Faulted, Is.False, state.EffectRuntime.LastFaultMessage);
            Call(coordinator, "NotifyCommandSettled", command.CommandId); commands.Clear();
        }

        [UnityTest]
        public IEnumerator DriverPanelBindingUsesZeroBasedOriginalCandidateAndRejectsChangedIdentity()
        {
            StartMartinPanel(); yield return null;
            var original = GameStateCloneService.DeepClone(Projection);
            var method = T("Validation.HotseatUiValidationDriver").GetMethod("FacilityPanelCandidateMatches",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            bool Matches(InteractionRequestProjection current, int player = 1, int slot = 0) =>
                (bool)method.Invoke(null, new object[] { original, current, visible, player, slot });
            Assert.That(Matches(Projection), Is.True);
            Assert.That(Matches(Projection, slot: 1), Is.False, "零基格0不能因按钮序号1而偏移到格1");
            Assert.That(Matches(Projection, player: 2), Is.False);
            var stale = GameStateCloneService.DeepClone(Projection); stale.StateRevision--;
            Assert.That(Matches(stale), Is.False);
            var wrongOwner = GameStateCloneService.DeepClone(Projection); wrongOwner.OwnerEffectId += ".other";
            Assert.That(Matches(wrongOwner), Is.False);
            var changedOrder = GameStateCloneService.DeepClone(Projection);
            var candidate = changedOrder.PromptParameters.Properties.Single(p => p.Name == "candidateSlots").Value.Items[0];
            candidate.Properties.Single(p => p.Name == "orderDigest").Value = NormalizedValue.CreateString("changed-order");
            Assert.That(Matches(changedOrder), Is.False);
            var changedTop = GameStateCloneService.DeepClone(Projection);
            changedTop.PromptParameters.Properties.Single(p => p.Name == "candidateSlots").Value.Items[0]
                .Properties.Single(p => p.Name == "topItemId").Value = NormalizedValue.CreateString("another-item");
            Assert.That(Matches(changedTop), Is.False);
            Assert.That(Slots[0].IsInteractable(), Is.True); Assert.That(Confirm.IsInteractable(), Is.False);
            Assert.That(commands, Is.Empty, "候选识别校验不能派令或修改原设施格选择");
        }

        [UnityTest]
        public IEnumerator PanelSelectionFollowsVerticalHostAfterOuterCanvasSizeChanges()
        {
            StartMartinPanel(); yield return null;
            Canvas.ForceUpdateCanvases(); yield return null;
            var panel = (RectTransform)Read(Page, "verticalPagePanel");
            var frame = Read(hud.GetComponent(T("GameplayInteractionHudView")), "Frame");
            var host = (RectTransform)Read(frame, "ContentRect");
            var root = (RectTransform)((Component)Page).transform;
            Assert.That(root.parent, Is.SameAs(host));
            var horizontal = new[] { panel.anchorMin.x, panel.anchorMax.x, panel.anchoredPosition.x, panel.sizeDelta.x };
            var originalCorners = new Vector3[4]; panel.GetWorldCorners(originalCorners);
            // 隔离模拟外层Canvas与宿主不同高；不改真实HUD、场景资产或正式对局。
            var min = root.offsetMin; var max = root.offsetMax;
            min.y -= host.rect.height * .2f; max.y += host.rect.height * .2f;
            root.offsetMin = min; root.offsetMax = max;
            Canvas.ForceUpdateCanvases(); yield return null;
            Canvas.ForceUpdateCanvases(); yield return null;
            var hostCorners = new Vector3[4]; host.GetWorldCorners(hostCorners);
            void Within(RectTransform target)
            {
                var corners = new Vector3[4]; target.GetWorldCorners(corners);
                Assert.That(corners.Min(c => c.y), Is.GreaterThanOrEqualTo(hostCorners.Min(c => c.y) - .01f));
                Assert.That(corners.Max(c => c.y), Is.LessThanOrEqualTo(hostCorners.Max(c => c.y) + .01f));
            }
            Within(panel); Within((RectTransform)Confirm.transform);
            var cancel = (Button)Read(Page, "cancel"); Within((RectTransform)cancel.transform);
            var expanded = (RectTransform)Read(((Component)Page).GetComponent(T("EffectDialogShellView")), "ExpandedContent");
            var title = expanded.Cast<Transform>().Where(t => t.gameObject.activeInHierarchy)
                .Select(t => t.GetComponent<Text>()).Single(t => t != null);
            Within(title.rectTransform);
            Assert.That(new[] { panel.anchorMin.x, panel.anchorMax.x, panel.anchoredPosition.x, panel.sizeDelta.x }, Is.EqualTo(horizontal));
            var currentCorners = new Vector3[4]; panel.GetWorldCorners(currentCorners);
            for (int i = 0; i < currentCorners.Length; i++) Assert.That(currentCorners[i].x, Is.EqualTo(originalCorners[i].x).Within(.01f));
            Assert.That(Slots[0].IsInteractable(), Is.True); Slots[0].onClick.Invoke();
            Assert.That(Confirm.IsInteractable(), Is.True); Assert.That(cancel.IsInteractable(), Is.True);
            Assert.That(commands, Is.Empty, "布局适配和设施格草稿选择不派正式命令");
            CommonUiTestCapture.Save(hud, "设施格选择竖直宿主边界-隔离组件");
        }

        [UnityTest]
        public IEnumerator M11OriginalConfirmRetiresActualTopAndResumesOriginalLuaOnlyOnce()
        {
            StartMartinPanel(); yield return null;
            var before = FacilityLayerQuery.ResolveCell(state, 1, 0); var lower = before.CoveredItemIds.Single();
            Assert.That(Slots[0].interactable, Is.True); Assert.That(Slots.Skip(1).Any(s => s.interactable), Is.False);
            Assert.That(Confirm.interactable, Is.False); Slots[0].onClick.Invoke(); Assert.That(commands, Is.Empty);
            CommonUiTestCapture.Save(hud, "M11原建设页城市面板选择");
            Assert.That(Confirm.interactable, Is.True); var originalConfirm = Confirm;
            originalConfirm.onClick.Invoke(); originalConfirm.onClick.Invoke(); Assert.That(commands.Count, Is.EqualTo(1)); Apply();
            Assert.That(FacilityLayerQuery.ResolveCell(state, 1, 0).TopItemId, Is.EqualTo(lower));
            Assert.That(state.ItemZones.Items.Single(i => i.ItemId == before.TopItemId).ZoneId,
                Is.EqualTo(state.ItemZones.Zones.Single(z => z.TemplateId == "core.zone.box_retired").ZoneId));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == InfluenceEffectTypeIds.PlaceInfluence), Is.True);
            Assert.That(state.EffectRuntime.InteractionRequests.Count(r => r.InteractionTypeId == "action.decline_effect"), Is.EqualTo(1));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == "effect.payment" || n.EffectTypeId == "effect.facility.build"), Is.False);
        }

        [UnityTest]
        public IEnumerator M11PanelCancelKeepsTopAndDoesNotGrantRightSide()
        {
            StartMartinPanel(); yield return null;
            var top = FacilityLayerQuery.ResolveCell(state, 1, 0).TopItemId;
            Assert.That(Request.AllowDecline, Is.True); ((Button)Read(Page, "cancel")).onClick.Invoke(); Apply();
            Assert.That(FacilityLayerQuery.ResolveCell(state, 1, 0).TopItemId, Is.EqualTo(top));
            Assert.That(state.EffectRuntime.EffectNodes.Any(n => n.EffectTypeId == InfluenceEffectTypeIds.PlaceInfluence || n.EffectTypeId == ContentPrimitiveEffectExecutor.ChooseResources), Is.False);
        }

        [UnityTest]
        public IEnumerator ActualCityAndSupplyBrowsersShowAllLayersWithoutSelectingOrBuilding()
        {
            Create(false);
            var lower = (ItemInstanceState)Call(fixture, "Supply", state, "building_017", 0);
            var upper = (ItemInstanceState)Call(fixture, "Supply", state, "building_018", 0);
            var otherCity = (ItemInstanceState)Call(fixture, "Card", state, "building_019");
            Call(fixture, "Position", state, otherCity, FacilityLayerQuery.ResolveCell(state, 2, 0).CellZoneId, "unordered", 2);
            var panel = hud.GetComponentInChildren(T("BuildInfoPanel"), true);
            var dialogs = hud.GetComponentInChildren(T("GameplayDialogRegistry"), true);
            panel.GetType().GetProperty("DialogRegistry").SetValue(panel, dialogs);
            Assert.That(Call(panel, "ConfigureCardVisualCatalog", Read(dialogs, "CardVisualCatalog")), Is.True);
            Assert.That(Call(panel, "Bind", Read(panel, "View")), Is.True);
            Call(panel, "Refresh", state, 1); visible = GameStateViewProjector.ProjectForPlayer(state, 1);
            Call(panel, "OpenCityCellLayers", 0, "", null); yield return null;
            Button[] Cards() => hud.GetComponentsInChildren<Button>().Where(b => b.GetComponents<Component>().Any(c =>
                c != null && c.GetType().Name == "PlayerAutomationId" && ((string)Read(c, "Id")).StartsWith("selection.card."))).ToArray();
            Assert.That(Cards().Length, Is.EqualTo(2));
            hud.GetComponentsInChildren<Button>().Single(b => b.name == "Cancel Selection").onClick.Invoke(); yield return null;
            Call(panel, "RefreshViewedCity", visible, 2); Call(panel, "OpenCityCellLayers", 0, "", null); yield return null;
            Assert.That(Cards().Length, Is.EqualTo(1));
            hud.GetComponentsInChildren<Button>().Single(b => b.name == "Cancel Selection").onClick.Invoke(); yield return null;
            var bindings = ((IEnumerable)Read(panel, "facilityCardBindings")).Cast<object>().ToArray();
            Call(panel, "HandleExternalFacilityClick", bindings[0]); yield return null;
            Assert.That(Cards().Length, Is.EqualTo(2));
            CommonUiTestCapture.Save(hud, "供应全层只读");
            Assert.That(commands, Is.Empty);
            Assert.That(FacilityLayerQuery.Supply(state).Single(s => s.SlotIndex == 0).TopItemId, Is.EqualTo(upper.ItemId));
            Call(panel, "Refresh", state, 2); yield return null;
            Assert.That(Cards(), Is.Empty, "热座换人应关闭旧只读列表");
        }

        [UnityTest]
        public IEnumerator GenericLuaUsesSamePageAndRejectsWrongPlayerAndChangedTop()
        {
            Create(false);
            const string lua = "return function(ctx) return {Effect.SelectFacilityPanel({apiVersion=1,player=ctx.playerId,allowDecline=true,operationProfile='city_top',emptyIsFailure=true,promptKey='core:entrepreneur.M.M11.panel'})} end";
            var invoked = new MoonSharpLuaRuntimeHost().Invoke(new LuaScriptDefinition("qy017.generic.panel", "qy017.generic", "qy017.generic", "1.0.0", lua, LuaContentHasher.ComputeSha256(lua)),
                new LuaInvocationContext("qy017.generic", "AbilityInvoked", "p1", state.EffectRuntime.StateRevision, "1.0.0", 3, 0, 0, LuaFacadeSnapshot.FromInvocation("p1", 0, 0, 3, "1.0.0")));
            Assert.That(invoked.IsSuccess, Is.True, invoked.Diagnostic);
            Assert.That(new LuaEffectSpecCompiler().TryCompile(invoked, new LuaEffectCompilationContext { State = state, Registry = registry, PlayerId = 1, SourceId = "qy017.generic" }, out var specs, out var fault), Is.True, fault?.Diagnostic);
            Call(fixture, "Run", state, specs.Single()); Show(); yield return null;
            var request = Projection; var old = Confirm; Slots[0].onClick.Invoke();
            var model = Read(coordinator, "draft"); var cells = ((IEnumerable)Read(model, "Cells")).Cast<object>().ToArray();
            var cell = cells.Single(c => (int)Read(c, "Index") == 0);
            Assert.That(((IEnumerable)Read(cell, "Cards")).Cast<object>().Count(), Is.EqualTo(2));
            viewer = 2; visible = GameStateViewProjector.ProjectForPlayer(state, viewer);
            Assert.That(Renderer.CanRender(request), Is.False); old.onClick.Invoke(); Assert.That(commands, Is.Empty);
            viewer = 1; Show(); Slots[0].onClick.Invoke(); old = Confirm;
            var next = (ItemInstanceState)Call(fixture, "Card", state, "building_017"); Call(fixture, "Overlay", state, next, 0);
            visible = GameStateViewProjector.ProjectForPlayer(state, viewer); old.onClick.Invoke(); Assert.That(commands, Is.Empty);
            Assert.That(Renderer.CanRender(request), Is.False);
            Renderer.Render(InteractionRequestProjector.ProjectForPlayer(Request, viewer));
            Assert.That(Confirm.interactable, Is.False, "新请求必须清掉旧选择并重新确认");
        }

        [UnityTearDown]
        public IEnumerator End()
        {
            Renderer?.Clear(); if (fixture != null) Call(fixture, "FinishMartin");
            if (hud != null) UnityEngine.Object.Destroy(hud); fixture = coordinator = null; yield return null;
        }
    }
}
