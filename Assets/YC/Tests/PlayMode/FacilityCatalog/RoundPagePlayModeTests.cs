using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.PlayMode
{
    public sealed class RoundPagePlayModeTests
    {
        [UnityTest]
        public IEnumerator FinalPage_SampleScene_StaysInsideContentAndReceivesInput()
        { return CheckFinalPage("SampleScene", 4); }

        [UnityTest]
        public IEnumerator FinalPage_ThreePlayerScene_StaysInsideContentAndReceivesInput()
        { return CheckFinalPage("ThreePlayerScene", 3); }

        private static IEnumerator CheckFinalPage(string sceneName, int count)
        {
            var contextType = Type.GetType("YC.Presentation.GameLaunchContext, Assembly-CSharp", true);
            var context = contextType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                .GetValue(null) as Component;
            if (context != null)
            {
                UnityEngine.Object.Destroy(context.gameObject);
                yield return null;
            }
            yield return SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            yield return null;
            var frame = Find("YC.Presentation.GameplayHudFrame");
            var round = Find("YC.Presentation.FinalScoreController");
            Assert.That(frame, Is.Not.Null);
            Assert.That(round, Is.Not.Null);
            var view = round.GetType().GetField("view", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(round);
            var state = new GameState
            {
                Phase = GamePhase.FinalScoring,
                FinalScoring = new FinalScoringState { IsResolved = true, TiebreakSummary = "终局显示回归诊断" }
            };
            for (var id = 1; id <= count; id++)
            {
                state.Players.Add(new PlayerState { PlayerId = id, Name = "诊断玩家" + id, Color = PlayerColor.Red });
                state.FinalScoring.PlayerScores.Add(new FinalPlayerScoreState
                {
                    PlayerId = id, BaseScore = 100000 + id, FacilityScore = 200000 + id,
                    CityStyleScore = 300000 + id, RegionScore = 400000 + id,
                    ResourceScore = 500000 + id, TotalScore = 1500000 + 5 * id
                });
            }
            state.FinalScoring.WinnerPlayerIds.Add(count);
            round.GetType().GetMethod("RefreshFromState").Invoke(round, new object[] { state });
            yield return null;
            Canvas.ForceUpdateCanvases();
            yield return null;
            var overlay = Get<GameObject>(view, "GameOverOverlay");
            var panel = overlay.GetComponentsInChildren<RectTransform>(true).Single(value => value.name == "Game Over Dialog");
            var content = Get<RectTransform>(frame, "ContentRect");
            AssertInside(ScreenRect(panel), ScreenRect(content), "终局窗口必须处于实际内容区");
            var pageCanvas = Get<Canvas>(view, "GameOverCanvas");
            Assert.That(pageCanvas.overrideSorting, Is.True);
            Assert.That(pageCanvas.sortingOrder, Is.LessThan(Get<Canvas>(frame, "BarCanvas").sortingOrder));
            AssertHit(Get<Button>(view, "ReturnStartButton"));
            AssertHit(Get<Button>(view, "FinalScoreDetailsButton"));
            AssertHit(Get<Button>(frame, "SettingsButton"));
            Get<Button>(view, "FinalScoreDetailsButton").onClick.Invoke();
            yield return null;
            Canvas.ForceUpdateCanvases();
            Assert.That(Get<bool>(round, "IsFinalScoreDetailsOpen"), Is.True);
            AssertHit(Get<Button>(view, "FinalScoreDetailsBackButton"));
            AssertHit(Get<Button>(view, "ReturnStartButton"));
            AssertInside(ScreenRect(panel), ScreenRect(content), "详情状态仍在内容区");
            var players = Get<RectTransform>(view, "DetailPlayerRowsContainer");
            var playerScroll = players.GetComponentInParent<ScrollRect>();
            Assert.That(playerScroll, Is.Not.Null, "明细玩家必须可在自己的视口滚动");
            yield return ScrollToEndThroughRaycast(playerScroll);
            var lastPlayer = players.GetComponentsInChildren<Button>().Last();
            AssertInside(ScreenRect((RectTransform)lastPlayer.transform), ScreenRect(playerScroll.viewport),
                "最后一名玩家切换按钮必须完整进入视口");
            AssertHit(lastPlayer);
            lastPlayer.onClick.Invoke();
            Assert.That(Get<int>(round, "SelectedFinalScorePlayerId"), Is.EqualTo(1));
            yield return null;
            Canvas.ForceUpdateCanvases();
            var charts = Get<RectTransform>(view, "DetailChartsContainer");
            var formula = charts.GetComponentsInChildren<Text>().Single(value =>
                value.name.StartsWith("Final Score Detail Formula P", StringComparison.Ordinal));
            var formulaScroll = formula.GetComponentInParent<ScrollRect>();
            Assert.That(formulaScroll, Is.Not.Null, "公式必须有独立的槽内滚动路径");
            Assert.That(formulaScroll.content, Is.SameAs(formula.rectTransform));
            Assert.That(formula.rectTransform.rect.height + .1f, Is.GreaterThanOrEqualTo(formula.preferredHeight),
                "正文高度必须容纳完整公式，不能只让被截断的Text滚动");
            var complete = new TextGenerator();
            var settings = formula.GetGenerationSettings(new Vector2(formula.rectTransform.rect.width, 10000));
            settings.verticalOverflow = VerticalWrapMode.Overflow;
            complete.Populate(formula.text, settings);
            Assert.That(formula.cachedTextGenerator.characterCountVisible,
                Is.EqualTo(complete.characterCountVisible), "公式必须生成与无垂直截断时相同的全部字符");
            yield return ScrollToEndThroughRaycast(formulaScroll);
            Assert.That(ScreenRect(formula.rectTransform).yMin,
                Is.EqualTo(ScreenRect(formulaScroll.viewport).yMin).Within(.1f), "公式末端必须滚入原槽底部");
            AssertHit(Get<Button>(view, "FinalScoreDetailsBackButton"));
            Get<Button>(view, "FinalScoreDetailsBackButton").onClick.Invoke();
            Assert.That(Get<bool>(round, "IsFinalScoreDetailsOpen"), Is.False);
            // 只执行页面内详情/返回，不触发ReturnStart，不提交业务命令，也不保存资产。
        }

        private static Component Find(string typeName) => UnityEngine.Object.FindObjectOfType(
            Type.GetType(typeName + ", Assembly-CSharp", true)) as Component;
        private static T Get<T>(object owner, string property) => (T)owner.GetType().GetProperty(property).GetValue(owner);
        private static Rect ScreenRect(RectTransform value)
        {
            var corners = new Vector3[4];
            value.GetWorldCorners(corners);
            var canvas = value.GetComponentInParent<Canvas>();
            var camera = canvas == null || canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.rootCanvas.worldCamera;
            var a = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            var b = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            return Rect.MinMaxRect(a.x, a.y, b.x, b.y);
        }
        private static void AssertInside(Rect actual, Rect available, string message)
        {
            Assert.That(actual.width, Is.GreaterThan(0), message);
            Assert.That(actual.height, Is.GreaterThan(0), message);
            Assert.That(actual.xMin, Is.GreaterThanOrEqualTo(available.xMin - .01f), message);
            Assert.That(actual.yMin, Is.GreaterThanOrEqualTo(available.yMin - .01f), message);
            Assert.That(actual.xMax, Is.LessThanOrEqualTo(available.xMax + .01f), message);
            Assert.That(actual.yMax, Is.LessThanOrEqualTo(available.yMax + .01f), message);
        }
        private static void AssertHit(Button button)
        {
            Assert.That(button.gameObject.activeInHierarchy, Is.True, button.name);
            var point = ScreenRect((RectTransform)button.transform).center;
            var results = new List<RaycastResult>();
            Assert.That(EventSystem.current, Is.Not.Null);
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, results);
            Assert.That(results, Is.Not.Empty, button.name);
            Assert.That(results[0].gameObject.transform.IsChildOf(button.transform), Is.True,
                button.name + " 被更高层遮挡：" + results[0].gameObject.name);
        }

        private static IEnumerator ScrollToEndThroughRaycast(ScrollRect scroll)
        {
            var overflow = scroll.content.rect.height > scroll.viewport.rect.height + .1f;
            var before = scroll.content.anchoredPosition.y;
            var data = new PointerEventData(EventSystem.current)
            {
                position = ScreenRect(scroll.viewport).center,
                scrollDelta = new Vector2(0, -10000)
            };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(data, hits);
            Assert.That(hits, Is.Not.Empty, scroll.name);
            var handler = ExecuteEvents.ExecuteHierarchy(hits[0].gameObject, data, ExecuteEvents.scrollHandler);
            Assert.That(handler, Is.SameAs(scroll.gameObject), "实际首命中必须路由至目标滚动区域");
            yield return null;
            Canvas.ForceUpdateCanvases();
            yield return null;
            if (overflow)
            {
                Assert.That(scroll.content.anchoredPosition.y, Is.GreaterThan(before), "滚轮输入必须实际移动内容");
                Assert.That(scroll.verticalNormalizedPosition, Is.LessThanOrEqualTo(.001f), "必须到达最后一项");
            }
        }
    }
}
