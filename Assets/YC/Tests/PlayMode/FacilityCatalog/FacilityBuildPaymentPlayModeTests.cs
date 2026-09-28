using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Facilities;
using YC.Domain.State;

namespace YC.Tests.PlayMode
{
    // 只覆盖建设的正式按钮回调与结算链，不重建、不保存场景或预制体。
    public sealed class FacilityBuildPaymentPlayModeTests
    {
        [UnityTest] public IEnumerator Resources_PayRefillThenPlace() => Run(false);
        [UnityTest] public IEnumerator Gold_PayRefillThenPlace() => Run(true);

        [UnityTest]
        public IEnumerator PaymentContent_UsesContentHeightAndFitsShortColumn()
        {
            var host = new GameObject("左栏高度夹具", typeof(RectTransform));
            try
            {
                var area = (RectTransform)host.transform;
                area.sizeDelta = new Vector2(360, 100);
                var contentObject = new GameObject("内容", typeof(RectTransform), typeof(VerticalLayoutGroup));
                var content = (RectTransform)contentObject.transform;
                content.SetParent(area, false);
                content.anchorMin = content.anchorMax = content.pivot = new Vector2(0, 1);
                contentObject.AddComponent(T("UiFitContentHeight"));
                var column = contentObject.GetComponent<VerticalLayoutGroup>();
                column.childControlHeight = column.childControlWidth = true;
                column.childForceExpandHeight = false;
                for (var i = 0; i < 2; i++)
                {
                    var button = new GameObject("支付", typeof(RectTransform), typeof(Image));
                    button.transform.SetParent(content, false);
                    var layout = button.AddComponent(T("UiContentHeightLayout"));
                    var body = new GameObject("按钮内容", typeof(RectTransform), typeof(VerticalLayoutGroup));
                    body.transform.SetParent(button.transform, false);
                    var bodyLayout = body.GetComponent<VerticalLayoutGroup>();
                    bodyLayout.padding = new RectOffset(6, 6, 6, 6);
                    bodyLayout.childControlHeight = true;
                    bodyLayout.childForceExpandHeight = false;
                    var item = new GameObject("动态费用", typeof(RectTransform), typeof(LayoutElement));
                    item.transform.SetParent(body.transform, false);
                    item.GetComponent<LayoutElement>().preferredHeight = i == 0 ? 80 : 40;
                    layout.GetType().GetField("content", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(layout, (RectTransform)body.transform);
                }
                LayoutRebuilder.ForceRebuildLayoutImmediate(content);
                Assert.That(content.localScale.x, Is.LessThan(1f));
                Assert.That(content.rect.height * content.localScale.y, Is.LessThanOrEqualTo(area.rect.height + .1f));
                foreach (RectTransform button in content)
                    Assert.That(button.rect.height,
                        Is.EqualTo(LayoutUtility.GetPreferredHeight((RectTransform)button.GetChild(0))).Within(.1f));
                area.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 600);
                LayoutRebuilder.ForceRebuildLayoutImmediate(content);
                Assert.That(content.localScale, Is.EqualTo(Vector3.one), "高度恢复后不应继续缩小，也不应放大内容。");
            }
            finally { UnityEngine.Object.Destroy(host); }
            yield return null;
        }

        [UnityTest]
        public IEnumerator NoMainAction_CanInspectSupplyWithoutPayment()
        {
            yield return (IEnumerator)typeof(GameplayMainHudPlayModeTests)
                .GetMethod("ClearLaunchContext", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            yield return SceneManager.LoadSceneAsync("SampleScene");
            yield return null;
            var controller = UnityEngine.Object.FindObjectOfType(T("MobileCityInteractionController"));
            var session = Field(controller, "session");
            var state = (GameState)Property(session, "State");
            var playerId = (int)Field(controller, "localPlayerId");
            var player = state.FindPlayer(playerId);
            player.RemainingMainActionsThisTurn = 0;
            player.ActedMainActionThisTurn = true;
            state.Decks.FacilitySupply.Clear();
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.BoroughAdministrativeDistrict);
            Call(controller, "SynchronizeInteractionFromState");
            var hud = Field(controller, "gameplayInteractionHud");
            var actionView = Property(hud, "ActionPanelView");
            var build = (Button)Property(actionView, "BuildButton");
            Assert.That(build.interactable, Is.True, "主要行动用尽后仍可查看供应区。");
            Assert.That(((Text)Field(actionView, "buildButtonLabel")).text,
                Is.EqualTo(Field(actionView, "viewFacilitySupplyLabel")));
            var resources = player.Resources.Clone();
            var supply = state.Decks.FacilitySupply.ToArray();
            var deck = state.Decks.FacilityDeck.ToArray();
            build.onClick.Invoke();
            yield return null;
            var page = (Component)Field(controller, "facilityBuildPage");
            Assert.That(page.gameObject.activeInHierarchy, Is.True);
            var candidates = (RectTransform)Field(page, "candidates");
            var card = candidates.GetComponentsInChildren(T("FacilityEffectCardView")).Single();
            ((Button)Property(card, "Button")).onClick.Invoke();
            yield return null;
            foreach (var row in ((Array)Field(page, "paymentRows")).Cast<object>())
            {
                var button = (Button)Property(row, "Button");
                Assert.That(button.interactable, Is.False);
                button.onClick.Invoke();
            }
            Assert.That(((Button)Field(page, "confirm")).gameObject.activeSelf, Is.False);
            Assert.That(state.Decks.FacilitySupply, Is.EqualTo(supply));
            Assert.That(state.Decks.FacilityDeck, Is.EqualTo(deck));
            Assert.That(player.Resources.Originium, Is.EqualTo(resources.Originium));
            Assert.That(player.Resources.OriginiumShard, Is.EqualTo(resources.OriginiumShard));
            Assert.That(player.Resources.Iron, Is.EqualTo(resources.Iron));
            Assert.That(player.Resources.PureOriginium, Is.EqualTo(resources.PureOriginium));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(resources.GoldVoucher));
            Assert.That(((Text)Field(page, "supplyDeckText")).text,
                Is.EqualTo(string.Format((string)Field(page, "supplyDeckFormat"), deck.Length)));
            var colors = (Color[])Field(page, "availableHubColors");
            var ids = new[] { FacilityCardDatabase.ExtensionHubRed, FacilityCardDatabase.ExtensionHubYellow,
                FacilityCardDatabase.ExtensionHubBlue };
            var expectedHubs = ids.Select((id, index) => new { id, index })
                .Where(item => !state.Map.Facilities.Any(f => f.FacilityCardId == item.id))
                .Select(item => string.Format((string)Field(page, "extensionHubFormat"),
                    ColorUtility.ToHtmlStringRGB(colors[item.index]))).ToArray();
            var expectedHubText = expectedHubs.Length == 0
                ? string.Format((string)Field(page, "extensionHubFormat"), ColorUtility.ToHtmlStringRGB((Color)Field(page, "usedHubColor")))
                : string.Join((string)Field(page, "extensionHubSeparator"), expectedHubs);
            Assert.That(((Text)Field(page, "extensionHubsText")).text, Is.EqualTo(expectedHubText));
            var holdings = Field(page, "ownedResources");
            var held = new[] { resources.Originium, resources.OriginiumShard, resources.Iron,
                resources.PureOriginium, resources.GoldVoucher };
            var ownedAmounts = (Text[])Field(holdings, "amounts");
            var ownedItems = (GameObject[])Field(holdings, "items");
            for (var i = 0; i < held.Length; i++)
            {
                Assert.That(ownedAmounts[i].text, Is.EqualTo(held[i].ToString()));
                Assert.That(ownedItems[i].activeSelf, Is.True, "当前资源即使为零也应保留图标和数字。");
            }
            ((Button)Field(page, "cancel")).onClick.Invoke();
            Assert.That(page.gameObject.activeSelf, Is.False);
            player.ActedMainActionThisTurn = false;
            player.RemainingMainActionsThisTurn = 1;
            Call(controller, "SynchronizeInteractionFromState");
            Assert.That(((Text)Field(actionView, "buildButtonLabel")).text,
                Is.EqualTo(Field(actionView, "buildActionLabel")), "恢复主要行动后应回到建设入口。");
        }

        [UnityTest]
        public IEnumerator SupplyRows_KeepThreeColumnsAndSwitchToHorizontal()
        {
            var host = new GameObject("供应区自适应夹具", typeof(RectTransform));
            try
            {
                var rect = (RectTransform)host.transform;
                var layout = host.AddComponent(T("UiWrappingRowLayout"));
                layout.GetType().GetField("minimumColumns", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(layout, 3);
                layout.GetType().GetField("maximumColumns", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(layout, 0);
                for (var i = 0; i < 8; i++)
                {
                    var slot = new GameObject("供应位", typeof(RectTransform), typeof(LayoutElement));
                    slot.transform.SetParent(rect, false);
                    slot.GetComponent<LayoutElement>().preferredHeight = 200;
                }
                rect.sizeDelta = new Vector2(400, 800);
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                Assert.That(Property(layout, "Columns"), Is.EqualTo(3));
                var narrowWidth = ((RectTransform)rect.GetChild(0)).rect.width;
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 1000);
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                Assert.That((int)Property(layout, "Columns"), Is.GreaterThan(3));
                Assert.That(((RectTransform)rect.GetChild(0)).rect.width, Is.GreaterThan(narrowWidth));
                Call(layout, "SetHorizontalMode", true);
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, (float)Property(layout, "HorizontalContentWidth"));
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                Assert.That(Property(layout, "Columns"), Is.EqualTo(8));
                Assert.That(((RectTransform)rect.GetChild(7)).anchoredPosition.y,
                    Is.EqualTo(((RectTransform)rect.GetChild(0)).anchoredPosition.y).Within(.01f));
                Call(layout, "SetHorizontalMode", false);
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 400);
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                Assert.That(Property(layout, "Columns"), Is.EqualTo(3), "退出横滑后应恢复分行。");
            }
            finally { UnityEngine.Object.Destroy(host); }
            yield return null;
        }

        [UnityTest]
        public IEnumerator SupplyRows_UseTallestItemAndReflowWhenNarrower()
        {
            var host = new GameObject("供应位分行布局夹具", typeof(RectTransform), typeof(Canvas));
            try
            {
                var rect = (RectTransform)host.transform;
                rect.sizeDelta = new Vector2(900, 1200);
                var layout = host.AddComponent(T("UiWrappingRowLayout"));
                var slots = new RectTransform[8];
                for (var i = 0; i < slots.Length; i++)
                {
                    var slot = new GameObject("供应位 " + i, typeof(RectTransform), typeof(LayoutElement));
                    slots[i] = (RectTransform)slot.transform;
                    slots[i].SetParent(rect, false);
                    slot.GetComponent<LayoutElement>().preferredHeight = i == 0 ? 630 : 311;
                }
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                var columns = (int)Property(layout, "Columns");
                Assert.That(columns, Is.GreaterThan(1));
                Assert.That(slots[columns].anchoredPosition.y + slots[columns].rect.height * (1f - slots[columns].pivot.y),
                    Is.LessThan(slots[0].anchoredPosition.y - slots[0].rect.height * slots[0].pivot.y),
                    "第二行应避让第一行最高的多牌供应位。");
                var wideColumns = columns;
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 400);
                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                columns = (int)Property(layout, "Columns");
                Assert.That(columns, Is.LessThan(wideColumns), "变窄后应重新分行。");
                Assert.That(slots[columns].anchoredPosition.y + slots[columns].rect.height * (1f - slots[columns].pivot.y),
                    Is.LessThan(slots[0].anchoredPosition.y - slots[0].rect.height * slots[0].pivot.y));
                var corners = new Vector3[4];
                foreach (var slot in slots)
                {
                    slot.GetWorldCorners(corners);
                    Assert.That(rect.InverseTransformPoint(corners[0]).x, Is.GreaterThanOrEqualTo(rect.rect.xMin - .1f));
                    Assert.That(rect.InverseTransformPoint(corners[2]).x, Is.LessThanOrEqualTo(rect.rect.xMax + .1f));
                }
            }
            finally { UnityEngine.Object.Destroy(host); }
            yield return null;
        }

        private static IEnumerator Run(bool gold)
        {
            yield return (IEnumerator)typeof(GameplayMainHudPlayModeTests)
                .GetMethod("ClearLaunchContext", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            yield return SceneManager.LoadSceneAsync("SampleScene");
            yield return null;
            var controller = UnityEngine.Object.FindObjectOfType(T("MobileCityInteractionController"));
            Assert.That(((Behaviour)controller).enabled, Is.True, "正式 HUD 必须完成绑定。");
            var session = Field(controller, "session");
            var state = (GameState)Property(session, "State");
            var local = (int)Field(controller, "localPlayerId");
            var player = state.FindPlayer(local);
            var facilityId = FacilityCardDatabase.BoroughAdministrativeDistrict;
            var definition = FacilityCardDatabase.Get(facilityId);
            player.Resources.Originium = player.Resources.OriginiumShard = player.Resources.Iron = 30;
            player.Resources.PureOriginium = 30; player.Resources.GoldVoucher = 100;
            var originalScore = player.Score;
            state.Decks.FacilitySupply.Clear(); state.Decks.FacilitySupply.Add(facilityId);
            state.Decks.FacilityDeck.Clear(); state.Decks.FacilityDeck.Add(FacilityCardDatabase.TradeDistrict);
            Call(controller, "SynchronizeInteractionFromState");
            var hud = Field(controller, "gameplayInteractionHud");
            var build = (Button)Property(Property(hud, "ActionPanelView"), "BuildButton");
            Assert.That(build.interactable, Is.True);
            build.onClick.Invoke();
            yield return null;
            var page = (Component)Field(controller, "facilityBuildPage");
            Assert.That(page.gameObject.activeInHierarchy, Is.True);
            var rows = (Array)Field(page, "paymentRows");
            Assert.That(rows.Length, Is.EqualTo(2));
            var buttons = rows.Cast<object>().Select(row => (Button)Property(row, "Button")).ToArray();
            foreach (var button in buttons)
            {
                Assert.That(button.gameObject.activeInHierarchy, Is.True, "支付选项进入面板后就应显示。");
                Assert.That(button.interactable, Is.False, "未选设施不能扣费。");
            }
            var candidates = (RectTransform)Field(page, "candidates");
            var card = candidates.GetComponentsInChildren(T("FacilityEffectCardView")).Single();
            ((Button)Property(card, "Button")).onClick.Invoke();
            yield return null;
            var currentRows = ((Array)Field(page, "paymentRows")).Cast<object>().ToArray();
            for (var i = 0; i < 2; i++)
                Assert.That(Property(currentRows[i], "Button"), Is.SameAs(buttons[i]), "切换选择不能重建支付按钮。");
            var costs = ((Array)Field(page, "paymentCosts")).Cast<object>().ToArray();
            var resourceAmounts = (Text[])Field(costs[0], "amounts");
            var expected = new[] { definition.ResourceCost.Originium, definition.ResourceCost.OriginiumShard,
                definition.ResourceCost.Iron, definition.ResourceCost.PureOriginium };
            var resourceItems = (GameObject[])Field(costs[0], "items");
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.That(resourceAmounts[i].text, Is.EqualTo(expected[i].ToString()), "按钮费用应与实际扣费一致。");
                Assert.That(resourceItems[i].activeSelf, Is.EqualTo(expected[i] != 0));
            }
            Assert.That(((Text[])Field(costs[1], "amounts"))[4].text, Is.EqualTo(definition.GoldVoucherCost.ToString()));
            Assert.That(((GameObject)Field(page, "costBreakdown")).activeSelf, Is.True);
            // 单个供应位按当前资产配置的可显示高度等比适配，不锁定尺寸和样式。
            var supplyLayout = candidates.GetComponent(T("UiWrappingRowLayout"));
            if ((bool)Field(supplyLayout, "expandSingleRowToHeight"))
            {
                Canvas.ForceUpdateCanvases();
                var viewport = (RectTransform)Field(supplyLayout, "heightViewport");
                var outer = candidates.parent.GetComponent<LayoutGroup>();
                var availableHeight = viewport.rect.height - ((LayoutGroup)supplyLayout).padding.vertical -
                    (outer == null ? 0 : outer.padding.vertical);
                var currentCard = candidates.GetComponentsInChildren(T("FacilityEffectCardView")).Single();
                Assert.That(((RectTransform)Property(currentCard, "CardRect")).rect.height,
                    Is.EqualTo(availableHeight).Within(1f), "单行卡槽应适配可显示高度。");
            }
            var payment = buttons[gold ? 1 : 0];
            Assert.That(payment.interactable, Is.True);
            var click = payment.onClick;
            click.Invoke(); click.Invoke();
            yield return null;
            state = (GameState)Property(session, "State"); player = state.FindPlayer(local);
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(gold ? 100 - definition.GoldVoucherCost : 100), "点击支付应立即实际扣费。");
            Assert.That(player.Resources.Originium, Is.EqualTo(gold ? 30 : 30 - definition.ResourceCost.Originium));
            Assert.That(player.Resources.OriginiumShard, Is.EqualTo(gold ? 30 : 30 - definition.ResourceCost.OriginiumShard));
            Assert.That(player.Resources.Iron, Is.EqualTo(gold ? 30 : 30 - definition.ResourceCost.Iron));
            Assert.That(state.Decks.FacilitySupply, Is.EqualTo(new[] { FacilityCardDatabase.TradeDistrict }), "必须先翻补供应牌。");
            Assert.That(state.Map.Facilities.Any(item => item.PlayerId == local && item.FacilityCardId == facilityId), Is.False, "支付时不能提前落位。");
            Assert.That(player.Score, Is.EqualTo(originalScore));
            var request = state.EffectRuntime.InteractionRequests.Single(item => item.Status == "open" && item.InteractionTypeId == "facility.build.placement");
            Assert.That(request.AllowDecline, Is.False);
            var placementUi = Field(controller, "facilityBuildPlacement");
            var placementPage = (Component)Field(placementUi, "page");
            Assert.That(placementPage.gameObject.activeInHierarchy, Is.True, "付款后必须进入城市选位页面。");
            var slots = (Button[])Field(placementPage, "citySlots");
            var slot = int.Parse(request.CandidateIds[0]);
            Assert.That(slots[slot].interactable, Is.True);
            foreach (var citySlot in slots)
                Assert.That(citySlot.GetComponentsInChildren(T("FacilityEffectCardView"), true), Is.Empty, "城市板只显示原卡面，不是选牌槽。");
            slots[slot].onClick.Invoke();
            yield return null;
            var confirm = (Button)Field(placementPage, "confirm");
            Assert.That(confirm.interactable, Is.True);
            var finish = confirm.onClick;
            finish.Invoke(); finish.Invoke();
            yield return null;
            state = (GameState)Property(session, "State"); player = state.FindPlayer(local);
            Assert.That(state.Map.Facilities.Count(item => item.PlayerId == local && item.FacilityCardId == facilityId && item.CityBoardSlotIndex == slot), Is.EqualTo(1));
            Assert.That(player.Score, Is.EqualTo(originalScore + definition.Score));
            Assert.That(player.Resources.GoldVoucher, Is.EqualTo(gold ? 100 - definition.GoldVoucherCost : 100), "放置不能重复扣费。");
            Assert.That(player.Resources.Originium, Is.EqualTo(gold ? 30 : 30 - definition.ResourceCost.Originium));
            Assert.That(state.EffectRuntime.InteractionRequests.Any(item => item.Status == "open" && item.InteractionTypeId == "facility.build.placement"), Is.False);
            Assert.That(player.ActedMainActionThisTurn, Is.True);
        }

        private static Type T(string name) => Type.GetType("YC.Presentation." + name + ", Assembly-CSharp", true);
        private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static object Property(object target, string name) => target.GetType().GetProperty(name).GetValue(target);
        private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(target, args);
    }
}
