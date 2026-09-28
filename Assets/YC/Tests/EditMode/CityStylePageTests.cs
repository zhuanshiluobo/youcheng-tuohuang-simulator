using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class CityStylePageTests
    {
        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Call(object o, string n, params object[] args) => o.GetType().GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(o, args);
        private static V Get<V>(object o, string n) => (V)o.GetType().GetProperty(n).GetValue(o);
        private static V Field<V>(object o, string n) => (V)o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
        private static object Create(Transform host)
        {
            var hud = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            var registry = hud.GetComponentInChildren(T("GameplayDialogRegistry"), true);
            return Activator.CreateInstance(T("CityStyleDeclarationPreviewDialog"), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object[] {registry, new Func<RectTransform>(() => (RectTransform)host)}, null);
        }
        private static CityStyleOptionsViewModel Model(bool declare, int count, Func<string,IReadOnlyList<int>,bool> submit,
            Func<CityStyleOptionsViewModel> refresh = null, int revision = 0)
        {
            return new CityStyleOptionsViewModel(Enumerable.Range(0,count).Select(i => new CityStyleOptionViewModel {
                CityStyleId = CityStyleDatabase.DefaultSupplyIds[i % CityStyleDatabase.DefaultSupplyIds.Count], Name = "组件候选", CanDeclare = true }).ToArray(),
                new[] { new CityBoardSlotViewModel(0, FacilityCardDatabase.SourceStoneRefinery, false), new CityBoardSlotViewModel(1, FacilityCardDatabase.EquipmentWarehouse, false) },
                new[] {new CityStyleMarkerViewModel(CityStyleDatabase.MilitaryIndustrialArea,1,PlayerColor.Red,"unused")},
                "", (id,slots) => new CityStyleSelectionValidationViewModel(slots.Count==2,"",0,2,slots.Count), submit, null, null, null, declare, refresh,"test",revision);
        }
        [Test] public void MarkerRefreshAndSelectionReuseNativePlayerSlotsWithoutStaleCounts()
        {
            var host = new GameObject("玩家标记复用测试", typeof(RectTransform));
            var dialog = Create(host.transform);
            var id = CityStyleDatabase.MilitaryIndustrialArea;
            var markers = new List<CityStyleMarkerViewModel>();
            for (var color = 0; color < 4; color++)
            {
                markers.Add(new CityStyleMarkerViewModel(id, color + 1, (PlayerColor)color, "unused"));
                for (var n = 0; n < 2; n++)
                    markers.Add(new CityStyleMarkerViewModel(id, color + 1, (PlayerColor)color, "unused"));
            }
            var options = Model(false, 6, null).Options;
            Func<CityStyleOptionsViewModel> model = null;
            model = () => new CityStyleOptionsViewModel(
                options, null, markers, id, null, null, null, null, declareMode: false, refresh: model);
            try
            {
                Call(dialog, "Show", model());
                var page = Field<Component>(dialog, "view");
                var content = Field<RectTransform>(page, "optionsContent");
                var counts = content.GetComponentsInChildren<Image>(true)
                    .Where(image => image.name == "Player Marker Count").ToArray();
                Assert.That(counts.Length, Is.EqualTo(4));
                Assert.That(counts.Count(image => image.GetComponentInChildren<Text>().text == "3"), Is.EqualTo(4));
                foreach (var count in counts)
                {
                    Assert.That(count.sprite, Is.Not.Null);
                    Assert.That(count.transform.parent.parent.GetComponent<LayoutGroup>(), Is.Not.Null);
                    Assert.That(count.transform.parent.parent.childCount, Is.EqualTo(4));
                }
                markers.Clear();
                markers.Add(new CityStyleMarkerViewModel(id, 1, PlayerColor.Red, "used"));
                Call(dialog, "RefreshProjection", true);
                counts = content.GetComponentsInChildren<Image>(true)
                    .Where(image => image.name == "Player Marker Count").ToArray();
                Assert.That(counts.Length, Is.EqualTo(1), "刷新必须清理旧轨道的动态数量。");
                Assert.That(counts[0].GetComponentInChildren<Text>().text, Is.EqualTo("1"));
                var originalIndex = options.ToList().FindIndex(option => option.CityStyleId == id);
                Call(dialog, "SelectCityStyle", (originalIndex + 1) % options.Count);
                Call(dialog, "SelectCityStyle", originalIndex);
                var detail = Get<RawImage>(page, "CityStyleCardImage");
                var active = detail.GetComponentsInChildren<Image>(true)
                    .Where(image => image.name.StartsWith("样式预览影响力") && image.gameObject.activeSelf).ToArray();
                Assert.That(active.Length, Is.EqualTo(1));
                Assert.That(active[0].transform.parent, Is.Not.SameAs(counts[0].transform.parent));
                Assert.That(active[0].transform.parent.parent.GetComponent<LayoutGroup>(), Is.Not.Null);
                Assert.That(active[0].GetComponentInChildren<Text>().text, Is.EqualTo("1"));
            }
            finally { Call(dialog, "Hide"); UnityEngine.Object.DestroyImmediate(host); }
        }

        [Test] public void ViewHiddenInputAndVersionChangeCannotSubmit()
        {
            var host=new GameObject("城市样式测试",typeof(RectTransform)); var dialog=Create(host.transform);int sends=0;
            try
            {
                Call(dialog,"Show",Model(false,2,(id,slots)=>{sends++;return true;}));
                Call(dialog,"ToggleSelectedSlot",0);Call(dialog,"ConfirmDeclaration");
                Assert.That(sends,Is.Zero);Assert.That(Get<IReadOnlyList<int>>(dialog,"SelectedSlotIndexes"),Is.Empty);
                int revision=1; Func<CityStyleOptionsViewModel> refresh=null;
                refresh=()=>Model(true,2,(id,slots)=>{sends++;return false;},refresh,revision);
                Call(dialog,"Show",refresh());
                Call(dialog,"ToggleSelectedSlot",0);Call(dialog,"ToggleSelectedSlot",1);
                var page=Field<Component>(dialog,"view");
                page.gameObject.SetActive(false);Call(dialog,"ToggleSelectedSlot",0);Call(dialog,"ConfirmDeclaration");
                Assert.That(sends,Is.Zero);Assert.That(Get<IReadOnlyList<int>>(dialog,"SelectedSlotIndexes"),Is.EqualTo(new[]{0,1}));
                page.gameObject.SetActive(true);revision++;
                Call(dialog,"ConfirmDeclaration");Assert.That(sends,Is.Zero,"新版本需再次确认。");
                Call(dialog,"ConfirmDeclaration");Assert.That(sends,Is.EqualTo(1));
                Assert.That(Get<IReadOnlyList<int>>(dialog,"SelectedSlotIndexes"),Is.EqualTo(new[]{0,1}),"拒绝保留仍有效的草稿。");
            }
            finally {Call(dialog,"Hide");UnityEngine.Object.DestroyImmediate(host);}
        }
        [TestCase(0)] [TestCase(2)] [TestCase(12)]
        public void ActualAssetKeepsFullCardsAndAllCitySlotsInsideTheirAllocatedAreas(int count)
        {
            var host=new GameObject("城市样式布局测试",typeof(RectTransform),typeof(Canvas));
            host.GetComponent<Canvas>().renderMode=RenderMode.WorldSpace;
            var dialog=Create(host.transform);
            try
            {
                var model = Model(false,count,null);
                if (count == 12)
                {
                    model.Options[0].CanDeclare = false;
                    model.Options[0].Reason = string.Concat(Enumerable.Repeat("用于检查匹配状态的换行与滚动。",60));
                }
                Call(dialog,"Show",model);
                var page=Field<Component>(dialog,"view");
                foreach(var size in new[]{new Vector2(1920,900),new Vector2(1100,620),new Vector2(2300,800)})
                {
                    ((RectTransform)host.transform).sizeDelta=size;
                    for(int pass=0;pass<4;pass++)
                    { foreach(var group in page.GetComponentsInChildren<LayoutGroup>()) LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)group.transform);Canvas.ForceUpdateCanvases(); }
                    var board=Get<RectTransform>(page,"CityBoardRect");
                    Assert.That(board.rect.width/board.rect.height,Is.EqualTo(2059f/3801f).Within(.002f));
                    var slots=Field<RectTransform[]>(page,"cityBoardSlotRoots");
                    var mapping=T("CityBoardSlotLayout").GetMethod("TryGetSlotIndex",BindingFlags.Static|BindingFlags.Public);
                    for(int i=0;i<slots.Length;i++)
                    {
                        var canvas=board.GetComponentInParent<Canvas>();
                        var point=RectTransformUtility.WorldToScreenPoint(canvas.worldCamera,slots[i].TransformPoint(slots[i].rect.center));
                        var args=new object[]{board,point,canvas.worldCamera,-1};
                        Assert.That((bool)mapping.Invoke(null,args),Is.True);Assert.That((int)args[3],Is.EqualTo(i));
                        foreach(var local in new[]{slots[i].rect.min+slots[i].rect.size*.1f,slots[i].rect.max-slots[i].rect.size*.1f})
                        {
                            args[1]=RectTransformUtility.WorldToScreenPoint(canvas.worldCamera,slots[i].TransformPoint(local));
                            Assert.That((bool)mapping.Invoke(null,args),Is.True);Assert.That((int)args[3],Is.EqualTo(i));
                        }
                    }
                    var content=Field<RectTransform>(page,"optionsContent");
                    Assert.That(content.childCount,Is.EqualTo(count));
                    foreach(var raw in content.GetComponentsInChildren<RawImage>())
                        Assert.That(raw.rectTransform.rect.width/raw.rectTransform.rect.height,Is.EqualTo(31f/20f).Within(.01f));
                    if(count>2) {var scroll=content.GetComponentInParent<ScrollRect>();scroll.verticalNormalizedPosition=0;Canvas.ForceUpdateCanvases();Assert.That(scroll.verticalNormalizedPosition,Is.EqualTo(0).Within(.01));}
                    if(count==12)
                    {
                        var summary=Get<Text>(page,"MatchStatusText").GetComponentInParent<ScrollRect>();
                        Assert.That(summary.content.rect.height,Is.GreaterThan(summary.viewport.rect.height));
                        summary.verticalNormalizedPosition=0;Canvas.ForceUpdateCanvases();
                        Assert.That(summary.verticalNormalizedPosition,Is.EqualTo(0).Within(.01));
                    }
                }
            }
            finally {Call(dialog,"Hide");UnityEngine.Object.DestroyImmediate(host);}
        }
        [UnityTearDown] public IEnumerator LeavePlayMode(){if(EditorApplication.isPlaying)yield return new ExitPlayMode();}
        [UnityTest] public IEnumerator RealSceneDeclarationUsesTheFormalCommandAndKeepsRepeatMarkersSeparate()
        {
            yield return new EnterPlayMode();
            foreach(var scene in new[]{"SampleScene","ThreePlayerScene"})
            {
                yield return SceneManager.LoadSceneAsync(scene); yield return null; yield return null;
                var controller=UnityEngine.Object.FindObjectOfType(T("MobileCityInteractionController"));
                var session=Field<object>(controller,"session");var state=Get<GameState>(session,"State");
                Assert.That(state.Phase,Is.EqualTo(GamePhase.ActionRound1),"此测试须使用正式开发行动起点。");
                var player=state.FindPlayer(state.CurrentPlayerId);
                // 明确的设施测试准备；宣告本身必须通过实际页签、选点与正式命令。
                for(int i=0;i<4;i++) state.Map.Facilities.Add(new FacilityPlacement { PlayerId=player.PlayerId,
                    FacilityCardId=i%2==0?FacilityCardDatabase.SourceStoneRefinery:FacilityCardDatabase.EquipmentWarehouse,
                    CityBoardSlotIndex=i<2?i:i+1 });
                Call(controller,"SynchronizeInteractionFromState");
                var tabs=UnityEngine.Object.FindObjectOfType(T("UiMainActionTabs"));Call(tabs,"Select",2);
                var hud=UnityEngine.Object.FindObjectOfType(T("GameplayInteractionHudView"));var action=Get<object>(hud,"ActionPanelView");
                yield return null; Canvas.ForceUpdateCanvases();
                var build=UnityEngine.Object.FindObjectOfType(T("BuildInfoPanel"));
                var cards=Field<RectTransform>(build,"externalCityStyleArea");
                Assert.That(cards.IsChildOf(((Component)action).transform),Is.True,"正式列表必须位于实际行动面板中。");
                Assert.That(cards.GetComponentInParent<CanvasGroup>()?.alpha ?? 1, Is.GreaterThan(0));
                Assert.That(cards.childCount,Is.EqualTo(state.Decks.CityStyleSupply.Count));
                var tableScroll=cards.GetComponentInParent<ScrollRect>();
                var scrollBefore=tableScroll.verticalNormalizedPosition;
                Assert.That(cards.GetComponentsInChildren<Button>(),Is.Empty,"主表的样式名称不再单独接收点击。");
                Get<Button>(action,"DeclareCityStyleButton").onClick.Invoke();yield return null;
                var browse=Field<object>(Field<object>(controller,"workflowView"),"cityStyleDeclarationDialog");
                var browsePage=Field<Component>(browse,"view");
                Assert.That(Field<CityStyleOptionsViewModel>(browse,"model").DeclareMode,Is.True);
                Get<Button>(browsePage,"CloseButton").onClick.Invoke();yield return null;
                Assert.That(Get<bool>(browse,"IsShowing"),Is.False);
                Assert.That(cards.gameObject.activeInHierarchy,Is.True);
                Assert.That(tableScroll.verticalNormalizedPosition,Is.EqualTo(scrollBefore).Within(.01f));
                for(int round=0;round<2;round++)
                {
                    Get<Button>(action,"DeclareCityStyleButton").onClick.Invoke();yield return null;
                    var workflow=Field<object>(controller,"workflowView");var dialog=Field<object>(workflow,"cityStyleDeclarationDialog");
                    var page=Field<Component>(dialog,"view");
                    Canvas.ForceUpdateCanvases();
                    var frame=UnityEngine.Object.FindObjectOfType(T("GameplayHudFrame"));
                    foreach(var property in new[]{"SettingsButton","EndActionButton"})
                    {
                        var button=Get<Button>(frame,property);var rect=(RectTransform)button.transform;
                        var hits=new List<RaycastResult>();
                        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current){
                            position=RectTransformUtility.WorldToScreenPoint(null,rect.TransformPoint(rect.rect.center))},hits);
                        Assert.That(hits,Is.Not.Empty);
                        Assert.That(hits[0].gameObject.GetComponentInParent<Button>(),Is.EqualTo(button),"常驻栏须保持最高命中。");
                    }
                    Assert.That(UnityEngine.Object.FindObjectsOfType(T("CityStyleDeclarationPreviewView")).Length,Is.EqualTo(1));
                    var before=player.DeclaredCityStyles.Count;var supply=player.InfluenceSupply;var score=player.Score;
                    var slots=Field<RectTransform[]>(page,"cityBoardSlotRoots");
                    foreach(var index in round==0?new[]{0,1}:new[]{3,4})
                        ExecuteEvents.Execute<IPointerClickHandler>(slots[index].gameObject,new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left},ExecuteEvents.pointerClickHandler);
                    Assert.That(Get<bool>(dialog,"CanConfirm"),Is.True);
                    var enlarge=page.GetComponentsInChildren(T("CityStyleCardStateView")).First();
                    ExecuteEvents.Execute<IPointerClickHandler>(enlarge.gameObject,
                        new PointerEventData(EventSystem.current) { button=PointerEventData.InputButton.Right }, ExecuteEvents.pointerClickHandler);
                    var detailGesture=Field<Component>(page,"detailGesture");
                    ExecuteEvents.Execute<IPointerClickHandler>(detailGesture.gameObject,
                        new PointerEventData(EventSystem.current) { button=PointerEventData.InputButton.Left }, ExecuteEvents.pointerClickHandler);
                    Assert.That(player.InfluenceSupply,Is.EqualTo(supply));Assert.That(player.Score,Is.EqualTo(score));
                    var confirm=Get<Button>(page,"ConfirmDeclarationButton");confirm.onClick.Invoke();confirm.onClick.Invoke();yield return null;
                    Assert.That(player.DeclaredCityStyles.Count,Is.EqualTo(before+1));Assert.That(player.InfluenceSupply,Is.EqualTo(supply-1));
                    Assert.That(player.Score,Is.GreaterThan(score));
                }
                var declarations=player.DeclaredCityStyles.Where(d=>d.CityStyleId==CityStyleDatabase.MilitaryIndustrialArea).ToArray();
                Assert.That(declarations.Length,Is.EqualTo(2));
                Assert.That(declarations[0].RemainingSpecialActionUses,Is.EqualTo(1));
                Assert.That(declarations[1].RemainingSpecialActionUses,Is.Zero);
                Assert.That(CityStyleInteraction.DisplayMarkerArea(player,declarations[1]),Is.EqualTo(CityStyleInteraction.DisplayMarkerArea(player,declarations[0])));
                // 从覆盖第三页的正式入口进入，再独立发动首次宣告所解锁的特殊行动。
                Get<Button>(action,"DeclareCityStyleButton").onClick.Invoke();
                yield return null;
                var activeDialog=Field<object>(Field<object>(controller,"workflowView"),"cityStyleDeclarationDialog");
                var activePage=Field<Component>(activeDialog,"view");Call(activePage,"ShowDetail",true);Canvas.ForceUpdateCanvases();
                Assert.That(Get<IReadOnlyList<int>>(activeDialog,"SelectedSlotIndexes"),Is.Empty);
                var marker=activePage.GetComponentsInChildren<Image>().First(i=>i.name.StartsWith("样式预览影响力 玩家") && i.GetComponent<Button>().interactable);
                var pointer=marker.GetComponent(T("CardPointerInteraction"));
                var ev=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left};
                Call(pointer,"OnBeginDrag",ev);
                var target=activePage.GetComponentsInChildren<RectTransform>().Single(r=>r.name=="特殊行动合法落区 used");
                ev.pointerCurrentRaycast=new RaycastResult{gameObject=target.gameObject};Call(pointer,"OnEndDrag",ev);
                var warning=Get<GameObject>(activePage,"SpecialActionWarningObject");
                if(warning.activeSelf)Get<Button>(activePage,"ConfirmSpecialActionWarningButton").onClick.Invoke();
                yield return null;
                state=Get<GameState>(session,"State");player=state.FindPlayer(player.PlayerId);
                Assert.That(player.DeclaredCityStyles[0].MarkerArea,Is.EqualTo("used"));
                Assert.That(CityStyleInteraction.DisplayMarkerArea(player,player.DeclaredCityStyles[1]),Is.EqualTo("used"));
                Assert.That(player.DeclaredCityStyles[1].RemainingSpecialActionUses,Is.Zero);
                Assert.That(player.DeclaredCityStyles.Count,Is.EqualTo(2),"特殊行动不能成为第三次宣告。");
                Debug.Log("军工正式行动后请求：" + string.Join(",",state.EffectRuntime.InteractionRequests.Where(r=>r.Status=="open").Select(r=>r.InteractionTypeId)));

            }
        }
        [UnityTest] public IEnumerator CompositeDeclarationAndActionRestoreTheAuthoritativePaymentAfterBrowsing()
        {
            yield return new EnterPlayMode();
            yield return SceneManager.LoadSceneAsync("SampleScene");yield return null;yield return null;
            var controller=UnityEngine.Object.FindObjectOfType(T("MobileCityInteractionController"));
            var session=Field<object>(controller,"session");var state=Get<GameState>(session,"State");var player=state.FindPlayer(state.CurrentPlayerId);
            foreach(var slot in new[]{0,3,4}) state.Map.Facilities.Add(new FacilityPlacement {PlayerId=player.PlayerId, CityBoardSlotIndex=slot,
                FacilityCardId=slot==3?FacilityCardDatabase.EquipmentWarehouse:FacilityCardDatabase.ExtensionHubYellow});
            Call(controller,"SynchronizeInteractionFromState");
            Call(UnityEngine.Object.FindObjectOfType(T("UiMainActionTabs")),"Select",2);
            var action=Get<object>(UnityEngine.Object.FindObjectOfType(T("GameplayInteractionHudView")),"ActionPanelView");
            Get<Button>(action,"DeclareCityStyleButton").onClick.Invoke();yield return null;
            var workflow=Field<object>(controller,"workflowView");var dialog=Field<object>(workflow,"cityStyleDeclarationDialog");
            var model=Field<CityStyleOptionsViewModel>(dialog,"model");
            Call(dialog,"SelectCityStyle",model.Options.ToList().FindIndex(o=>o.CityStyleId==CityStyleDatabase.CompositePowerSystem));
            foreach(var slot in new[]{0,3,4})Call(dialog,"ToggleSelectedSlot",slot);
            Assert.That(Get<bool>(dialog,"CanConfirm"),Is.True);Call(dialog,"ConfirmDeclaration");yield return null;
            Assert.That(player.DeclaredCityStyles.Single().CityStyleId,Is.EqualTo(CityStyleDatabase.CompositePowerSystem));
            Get<Button>(action,"DeclareCityStyleButton").onClick.Invoke();yield return null;
            Call(dialog,"SelectCityStyle",model.Options.ToList().FindIndex(o=>o.CityStyleId==CityStyleDatabase.CompositePowerSystem));
            var page=Field<Component>(dialog,"view");Call(page,"ShowDetail",true);Canvas.ForceUpdateCanvases();
            var marker=page.GetComponentsInChildren<Image>().First(i=>i.name.StartsWith("样式预览影响力 玩家") && i.GetComponent<Button>().interactable);
            var pointer=marker.GetComponent(T("CardPointerInteraction"));
            var ev=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left};Call(pointer,"OnBeginDrag",ev);
            ev.pointerCurrentRaycast=new RaycastResult{gameObject=page.GetComponentsInChildren<RectTransform>().Single(r=>r.name=="特殊行动合法落区 used").gameObject};
            Call(pointer,"OnEndDrag",ev);if(Get<GameObject>(page,"SpecialActionWarningObject").activeSelf)Get<Button>(page,"ConfirmSpecialActionWarningButton").onClick.Invoke();yield return null;
            state=Get<GameState>(session,"State");
            Assert.That(state.EffectRuntime.InteractionRequests.Any(r=>r.Status=="open"),Is.True,"正式特殊行动必须建立支付请求。");
            var frame=UnityEngine.Object.FindObjectOfType(T("GameplayHudFrame"));
            Assert.That(Get<Button>(frame,"FoldButton").interactable,Is.True);
            var requestId=Field<string>(frame,"currentRequestId");var effectPage=Field<GameObject>(frame,"suspendedEffectPage");
            effectPage.GetComponentsInChildren<Button>().Single(b=>b.name=="Increase 0").onClick.Invoke();
            var paymentSummary=effectPage.GetComponentsInChildren<Text>().Single(t=>t.name=="Special Action Payment Summary");
            var selectedSummary=paymentSummary.text;
            Call(frame,"SuspendEffectForInformation");Assert.That(effectPage.activeSelf,Is.False);
            // 支付期间的信息浏览仅测试工作流恢复，不再依赖已移除的主表名称入口。
            Get<CityStyleInteraction>(Field<object>(controller,"turnActionPresenter"),"CityStyleInteraction").OpenPreview(CityStyleDatabase.MilitaryIndustrialArea);yield return null;
            var infoPage=Field<Component>(dialog,"view");Assert.That(infoPage.gameObject.activeInHierarchy,Is.True);
            Call(controller,"SynchronizeInteractionFromState");yield return null;
            Assert.That(effectPage.activeSelf,Is.False,"隐藏期间状态同步不能自动弹回。");
            Call(frame,"ResumeEffectPage");yield return null;
            Assert.That(infoPage.gameObject.activeInHierarchy,Is.False);Assert.That(effectPage.activeInHierarchy,Is.True);
            Assert.That(Field<string>(frame,"currentRequestId"),Is.EqualTo(requestId));
            Assert.That(paymentSummary.text,Is.EqualTo(selectedSummary),"收起和同步后支付预选保持不变。");
        }
    }
}
