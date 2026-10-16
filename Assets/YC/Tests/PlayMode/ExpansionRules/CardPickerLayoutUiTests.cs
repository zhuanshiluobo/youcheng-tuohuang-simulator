using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using YC.Domain.Interactions;
using YC.Domain.Cards;
using YC.Domain.Rules;

namespace YC.Tests.PlayMode.ExpansionRules
{
    public sealed class CardPickerLayoutUiTests
    {
        private Component page;
        private object dialog;
        private HotseatProfilerSampler profiler;
        private HotseatUiInputDriver evidence;
        private bool completed;
        [UnityTearDown] public IEnumerator Cleanup()
        {
            evidence?.FlushRecords(); evidence = null;
            if (dialog != null) GameplayUiReworkFixture.Call(dialog, "Hide");
            if (page != null) UnityEngine.Object.Destroy(page.gameObject);
            if (profiler != null) { profiler.Finish(completed); UnityEngine.Object.Destroy(profiler.gameObject); }
            yield return null;
        }

        [UnityTest] public IEnumerator EnterpriseCoverAndRealCharacterFiveSixNineFiveUseScreenCenteredWholeWindow()
        {
            completed = false;
            var f = new GameplayPageAcceptanceFixture();
            string[] realPool = null;
            bool realCoverChecked = false;
            string Definition(string id) => f.State.ItemZones.Items.Single(item => item.ItemId == id).DefinitionId;
            string[] DisplayOrder(IEnumerable<string> ids)
            {
                var definitions = CharacterCardDatabase.CanonicalDisplayOrder.Concat(f.State.ContentDefinitions.Characters.Select(c => c.TemplateId)).Distinct().ToList();
                return ids.OrderBy(id => definitions.IndexOf(Definition(id)) < 0 ? int.MaxValue : definitions.IndexOf(Definition(id)))
                    .ThenBy(Definition, StringComparer.Ordinal).ThenBy(id => id, StringComparer.Ordinal).ToArray();
            }
            IEnumerator Opening(YC.Domain.State.InteractionRequest request)
            {
                if (realPool == null && request.InteractionTypeId == ExpansionSetupFlow.BasicCharacterInteraction)
                    realPool = request.CandidateIds.ToArray();
                if (realCoverChecked || request.InteractionTypeId != "character.cover.card") yield break;
                var authoritativeCandidates = request.CandidateIds.ToArray();
                yield return GameplayPageAcceptanceFixture.Wait(() => GameplayPageAcceptanceFixture.Active("EffectDialogShellView")
                    .Any(p => CharacterButtons(p).Length == request.CandidateIds.Count), "正式盖放请求没有出现实际角色选择页");
                var actual = GameplayPageAcceptanceFixture.Active("EffectDialogShellView")
                    .Single(p => CharacterButtons(p).Length == request.CandidateIds.Count);
                var actualIds = CharacterButtons(actual).Select(CharacterId).ToArray();
                Assert.That(actualIds, Is.EqualTo(DisplayOrder(authoritativeCandidates)), "企业局实际盖放页应按正式角色展示顺序排列");
                Assert.That(request.CandidateIds, Is.EqualTo(authoritativeCandidates), "显示排序不得改写权威请求顺序");
                f.Record("正式盖放角色顺序", new { request.InteractionTypeId, request.AnsweringPlayerId, authoritativeCandidates, actualIds });
                yield return f.Click(CharacterButtons(actual)[0].gameObject, "正式盖放_选择排序后的真实角色");
                var confirm = actual.GetComponentsInChildren<Button>(false).Single(b => b.name == "Confirm Selection");
                using (var count = new GameplayPageAcceptanceFixture.RuleSubmitCounter())
                {
                    yield return f.Click(confirm.gameObject, "正式盖放_确认真实实例");
                    Assert.That(count.Calls, Is.EqualTo(1));
                }
                Assert.That(f.Request == null || f.Request.GetStableInteractionId() != request.GetStableInteractionId(), Is.True);
                realCoverChecked = true;
            }
            yield return f.Start(4, _ => GameplayPageAcceptanceFixture.Settle(), openingRequestCheck: Opening);
            Assert.That(realCoverChecked, Is.True, "不能以受控六张布局替代真实盖放入口");
            Assert.That(realPool, Is.Not.Null);
            Assert.That(realPool.Length, Is.GreaterThanOrEqualTo(6));
            // 九张布局从本局真实角色实体取受控可见输入，不假称当前玩家实际持有九张手牌。
            var definitionsInGame = new HashSet<string>(f.State.ContentDefinitions.Characters.Select(c => c.TemplateId));
            realPool = realPool.Concat(f.State.ItemZones.Items.Where(item => definitionsInGame.Contains(item.DefinitionId))
                .Select(item => item.ItemId)).Distinct().Take(9).ToArray();
            Assert.That(realPool.Length, Is.EqualTo(9));
            f.Record("布局受控真实角色来源", new { scope = "本局不同玩家的实际角色实体，仅受控布局；不改权威状态", ids = realPool,
                definitions = realPool.Select(Definition).ToArray() });
            var hand = GameplayPageAcceptanceFixture.Read(f.Gameplay, "characterHandPanel");
            var handIds = ((IEnumerable)GameplayPageAcceptanceFixture.Read(hand, "OrderedHand")).Cast<object>()
                .Select(item => (string)GameplayPageAcceptanceFixture.Read(item, "CardId")).ToArray();
            Assert.That(handIds, Is.Not.Empty);
            Assert.That(handIds, Is.EqualTo(DisplayOrder(handIds)), "实际手牌组件保留正式角色顺序");
            f.Record("正式手牌角色顺序", new { handIds });

            profiler = HotseatProfilerSampler.Create(f.DirectoryPath, false, 0, 0);
            evidence = new HotseatUiInputDriver(f.DirectoryPath, () => new { scope = "正式局真实角色实例的受控5/6/9/5布局；不提交额外规则" }, profiler);
            dialog = Activator.CreateInstance(GameplayUiReworkFixture.Type("CharacterCardEffectChoiceDialog"), GameplayUiReworkFixture.Members,
                null, new[] { f.Registry, f.Host }, null);
            dialog.GetType().GetProperty("ResolveCharacterDefinition").SetValue(dialog, (Func<string, string>)Definition);
            foreach (var size in GameplayPageAcceptanceFixture.Sizes.Concat(new[] { new Vector2Int(1024, 768) }))
            {
                yield return f.Resolution(size);
                Vector2 fiveCardSize = default;
                foreach (int count in new[] { 5, 6, 9, 5 })
                {
                    bool reduced = count == 5 && fiveCardSize != default;
                    string selected = null;
                    int confirms = 0;
                    var ids = DisplayOrder(realPool).Take(count).ToArray();
                    Action render = null;
                    render = () => GameplayUiReworkFixture.Call(dialog, "ShowCoverSelection", ids, selected,
                        (Func<string, bool>)(id => ids.Contains(id)), (Action<string>)(id => { selected = id; render(); }), (Action)(() => confirms++));
                    render();
                    page = (Component)GameplayUiReworkFixture.Field(GameplayUiReworkFixture.Field(dialog, "shell"), "view");
                    yield return GameplayPageAcceptanceFixture.Settle();
                    var row = GameplayUiReworkFixture.Field(page, "cardPickerRow");
                    var scroll = (ScrollRect)GameplayUiReworkFixture.Field(page, "optionScroll");
                    Assert.That(GameplayUiReworkFixture.Property(row, "CardCount"), Is.EqualTo(count));
                    Assert.That(GameplayUiReworkFixture.Property(row, "FitsAvailableHeight"), Is.True);
                    Assert.That(GameplayUiReworkFixture.Property(row, "IsOverflowing"), Is.EqualTo(count > 5));
                    Assert.That(scroll.horizontalScrollbar.gameObject.activeInHierarchy, Is.EqualTo(count > 5));
                    Assert.That(scroll.vertical, Is.False);
                    var cards = CharacterButtons(page);
                    Assert.That(cards.Select(CharacterId), Is.EqualTo(ids));
                    var logicalCardSize = ((RectTransform)cards[0].transform).rect.size;
                    if (fiveCardSize == default) fiveCardSize = logicalCardSize;
                    Assert.That(Vector2.Distance(logicalCardSize, fiveCardSize), Is.LessThan(.1f), "六张时不得缩小卡牌挤进窗口");
                    CheckWholeWindow(f, page, count);
                    yield return evidence.Click(() => CharacterButtons(page).First().gameObject, "真实角色" + count + "张_首卡选择");
                    Assert.That(selected, Is.EqualTo(ids.First()));
                    if (count == 5)
                    {
                        Assert.That(Mathf.Abs(scroll.content.anchoredPosition.x), Is.LessThan(.1f), "回到五张应清除横移");
                        Assert.That(scroll.content.rect.width, Is.LessThanOrEqualTo(scroll.viewport.rect.width + 1f));
                    }
                    else
                    {
                        var before = scroll.horizontalNormalizedPosition;
                        yield return GameplayUiReworkFixture.Wheel(scroll, new Vector2(-3, 0), evidence);
                        Assert.That(scroll.horizontalNormalizedPosition, Is.GreaterThan(before));
                        yield return GameplayUiReworkFixture.DragScrollbar(scroll, true, evidence);
                        Assert.That(HotseatUiInputDriver.TryPoint(CharacterButtons(page).Last().gameObject, out _, out _), Is.True,
                            "六张末卡必须已可命中，不能让点击辅助再滚动掩盖问题");
                    }
                    yield return evidence.Click(() => CharacterButtons(page).Last().gameObject, "真实角色" + count + "张_末卡选择");
                    Assert.That(selected, Is.EqualTo(ids.Last()));
                    Assert.That(confirms, Is.Zero, "布局和选牌不能提前提交");
                    yield return evidence.Capture("角色" + count + "张_" + size + (reduced ? "_九转五" : "_首次呈现"), 1);
                    if (count == 6 && size == GameplayPageAcceptanceFixture.Sizes[0])
                    {
                        yield return f.Click(CharacterButtons(page).Last().gameObject, "角色末卡_右键查看原图", button: PointerEventData.InputButton.Right);
                        var viewer = (Component)GameplayUiReworkFixture.Field(GameplayUiReworkFixture.Field(dialog, "shell"), "cardViewer");
                        Assert.That(GameplayUiReworkFixture.Property(viewer, "IsShowing"), Is.True);
                        yield return evidence.Click(() => viewer.gameObject, "角色原图_返回候选");
                        Assert.That(selected, Is.EqualTo(ids.Last()));
                        Assert.That(confirms, Is.Zero, "详情返回不能误确认");
                    }
                }
            }
            completed = true;
        }

        private static Button[] CharacterButtons(Component owner) => owner.GetComponentsInChildren<Button>(false)
            .Where(b => b.GetComponents<Component>().Any(c => c != null && c.GetType().Name == "PlayerAutomationId" &&
                ((string)GameplayPageAcceptanceFixture.Read(c, "Id") ?? "").StartsWith("selection.card.", StringComparison.Ordinal)))
            .OrderBy(b => b.transform.GetSiblingIndex()).ToArray();

        private static string CharacterId(Button button) => ((string)GameplayPageAcceptanceFixture.Read(
            button.GetComponents<Component>().Single(c => c != null && c.GetType().Name == "PlayerAutomationId"), "Id")).Substring("selection.card.".Length);

        private static void CheckWholeWindow(GameplayPageAcceptanceFixture f, Component owner, int count)
        {
            var scaler = owner.GetComponentsInChildren(GameplayUiReworkFixture.Type("CardPickerPageScale"), true).Cast<Component>().Single();
            var window = (RectTransform)scaler.transform;
            var actual = GameplayUiReworkFixture.ScreenRect(window);
            var screenCenter = new Vector2(Screen.width * .5f, Screen.height * .5f);
            Assert.That(Vector2.Distance(actual.center, screenCenter), Is.LessThanOrEqualTo(1f), "窗口应居中于物理屏幕，不能以 PageHost 中心替代");
            var scale = window.localScale;
            Assert.That(scale.x, Is.EqualTo(scale.y).Within(.001f));
            Assert.That(scale.x, Is.GreaterThan(0f).And.LessThanOrEqualTo(1.2001f));
            var baseWidth = actual.width / scale.x; var baseHeight = actual.height / scale.y;
            var safe = Screen.safeArea.width > 0 ? Screen.safeArea : new Rect(0, 0, Screen.width, Screen.height);
            var requested = new Rect(screenCenter.x - baseWidth * .6f, screenCenter.y - baseHeight * .6f, baseWidth * 1.2f, baseHeight * 1.2f);
            bool fits = requested.xMin >= safe.xMin - .5f && requested.xMax <= safe.xMax + .5f && requested.yMin >= safe.yMin - .5f && requested.yMax <= safe.yMax + .5f;
            if (fits) Assert.That(scale.x, Is.EqualTo(1.2f).Within(.001f), "实际屏幕装得下时必须整体放大1.2倍");
            else
            {
                Assert.That(scale.x, Is.LessThan(1.2f));
                Assert.That(Mathf.Min(actual.xMin - safe.xMin, safe.xMax - actual.xMax, actual.yMin - safe.yMin, safe.yMax - actual.yMax),
                    Is.InRange(-1f, 1f), "不足时应等比贴合实际屏幕边界，不能被内部PageHost提前压缩");
            }
            var scroll = (ScrollRect)GameplayUiReworkFixture.Field(owner, "optionScroll");
            Assert.That(actual.width / actual.height, Is.EqualTo(2f).Within(.01f));
            if (scroll.horizontalScrollbar.gameObject.activeInHierarchy)
            {
                var bar = GameplayUiReworkFixture.ScreenRect((RectTransform)scroll.horizontalScrollbar.transform);
                foreach (var card in owner.GetComponentsInChildren(GameplayUiReworkFixture.Type("FacilityEffectCardView"), false).Cast<Component>())
                {
                    // CardRect 是含内边距的整条目；12:17 约束属于实际卡图，不能拿条目外框替代。
                    var face = GameplayUiReworkFixture.ScreenRect(((RawImage)GameplayUiReworkFixture.Property(card, "CardImage")).rectTransform);
                    Assert.That(face.height / face.width, Is.EqualTo(17f / 12f).Within(.01f));
                    Assert.That(face.yMin, Is.GreaterThanOrEqualTo(bar.yMax - .5f), "卡面不能覆盖滚条热区");
                    var selectedFrame = (Image)GameplayUiReworkFixture.Property(card, "SelectionImage");
                    if (selectedFrame != null)
                        Assert.That(GameplayUiReworkFixture.ScreenRect(selectedFrame.rectTransform).yMin, Is.GreaterThanOrEqualTo(bar.yMax - .5f), "勾选边框不能覆盖滚条热区");
                }
            }
            var title = (Text)GameplayUiReworkFixture.Field(owner, "titleText");
            var parts = CharacterButtons(owner).Select(b => (RectTransform)b.transform)
                .Concat(new[] { title.rectTransform, (RectTransform)scroll.horizontalScrollbar.transform })
                .Concat(owner.GetComponentsInChildren<Button>(false).Where(b => b.name == "Confirm Selection").Select(b => (RectTransform)b.transform)).ToArray();
            foreach (var part in parts)
            {
                Assert.That(part.IsChildOf(window), Is.True, "卡牌、标题、按钮与滚条必须随同一整窗缩放");
                Assert.That(part.lossyScale.x / window.lossyScale.x, Is.EqualTo(part.lossyScale.y / window.lossyScale.y).Within(.001f), "子控件不能改变长宽比");
            }
            object Bounds(Rect r) => new { r.x, r.y, r.width, r.height };
            f.Record("角色整窗屏幕中心与缩放", new { count, actual = Bounds(actual), screenCenter = new[] { screenCenter.x, screenCenter.y },
                safe = Bounds(safe), requestedScale = 1.2f, actualScale = new[] { scale.x, scale.y, scale.z },
                clampedByScreen = !fits, oldPageHostIsNotConstraint = true, parts = parts.Select(p => new { path = HotseatUiInputDriver.PathOf(p.gameObject), rect = Bounds(GameplayUiReworkFixture.ScreenRect(p)) }).ToArray() });
        }

    }
}
