using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class RoundTrackerControllerTests
    {
        private GameObject owner;
        private Component controller;

        [TearDown]
        public void TearDown()
        {
            if (owner != null)
            {
                UnityEngine.Object.DestroyImmediate(owner);
                owner = null;
            }

        }

        [Test]
        public void BuildRoundUi_CreatesTopDockedRoundTrackWithBorderFrame()
        {
            controller = CreateController();

            var panel = GetPrivateField<RectTransform>("panelTransform");
            var content = GetPrivateField<RectTransform>("contentArea");
            var marker = GetPrivateField<RectTransform>("markerTransform");

            Assert.That(GetPublicProperty<bool>("IsExpanded"), Is.True);
            Assert.That(panel.GetComponent<RectMask2D>(), Is.Null);
            Assert.That(panel.GetComponent<Image>(), Is.Not.Null);
            Assert.That(panel.GetComponent<Outline>(), Is.Null);
            Assert.That(panel.anchorMin, Is.EqualTo(new Vector2(0.5f, 1f)));
            Assert.That(panel.anchorMax, Is.EqualTo(new Vector2(0.5f, 1f)));
            Assert.That(panel.pivot, Is.EqualTo(new Vector2(0.5f, 1f)));
            Assert.That(panel.anchoredPosition, Is.EqualTo(Vector2.zero));
            Assert.That(panel.rect.height, Is.EqualTo(78f).Within(0.01f));
            var borderThickness = GetPrivateStaticFloat("BorderThickness");
            Assert.That(borderThickness, Is.EqualTo(panel.rect.height * 0.2f).Within(0.01f));
            Assert.That(content.offsetMin.x, Is.EqualTo(borderThickness).Within(0.01f));
            Assert.That(content.offsetMin.y, Is.EqualTo(borderThickness).Within(0.01f));
            Assert.That(content.offsetMax.x, Is.EqualTo(-borderThickness).Within(0.01f));
            Assert.That(content.offsetMax.y, Is.EqualTo(-borderThickness).Within(0.01f));
            Assert.That(content.gameObject.activeSelf, Is.True);
            Assert.That(marker.anchoredPosition.y, Is.EqualTo(0f).Within(0.01f));

            Assert.That(FindChild(content, "Round Track"), Is.Not.Null);
            Assert.That(FindChild(panel, "Round Border Top"), Is.Not.Null);
            Assert.That(FindChild(panel, "Round Border Bottom"), Is.Not.Null);
            Assert.That(FindChild(panel, "Round Border Left"), Is.Not.Null);
            Assert.That(FindChild(panel, "Round Border Right"), Is.Not.Null);
            Assert.That(FindChild(content, "End Round Button"), Is.Null);
            Assert.That(FindChild(panel, "Toggle Button"), Is.Null);
        }

        [Test]
        public void Toggle_DoesNotCollapseFixedRoundTrack()
        {
            controller = CreateController();
            var panel = GetPrivateField<RectTransform>("panelTransform");
            var content = GetPrivateField<RectTransform>("contentArea");
            var initialHeight = panel.rect.height;

            InvokePublic("Toggle");

            Assert.That(GetPublicProperty<bool>("IsExpanded"), Is.True);
            Assert.That(content.gameObject.activeSelf, Is.True);
            Assert.That(panel.rect.height, Is.EqualTo(initialHeight).Within(0.01f));

            StepAnimationToTarget();

            Assert.That(panel.rect.height, Is.EqualTo(initialHeight).Within(0.01f));
            Assert.That(content.gameObject.activeSelf, Is.True);

            InvokePublic("Toggle");

            Assert.That(GetPublicProperty<bool>("IsExpanded"), Is.True);
            Assert.That(content.gameObject.activeSelf, Is.True);

            StepAnimationToTarget();

            Assert.That(panel.rect.height, Is.EqualTo(initialHeight).Within(0.01f));
            Assert.That(content.gameObject.activeSelf, Is.True);
        }

        [Test]
        public void RefreshFromState_WhenFinalScoringResolved_CreatesStructuredScoreboard()
        {
            controller = CreateController();
            var state = new GameState
            {
                Phase = GamePhase.FinalScoring,
                Players =
                {
                    new PlayerState { PlayerId = 1, Name = "甲", Color = PlayerColor.Red },
                    new PlayerState { PlayerId = 2, Name = "乙", Color = PlayerColor.Blue }
                },
                FinalScoring = new FinalScoringState
                {
                    IsResolved = true,
                    WinnerPlayerIds = { 1 },
                    TiebreakSummary = "总分最高。",
                    PlayerScores =
                    {
                        new FinalPlayerScoreState
                        {
                            PlayerId = 1,
                            BaseScore = 3,
                            FacilityScore = 4,
                            CityStyleScore = 2,
                            RegionScore = 3,
                            ResourceScore = 1,
                            TotalScore = 13
                        },
                        new FinalPlayerScoreState
                        {
                            PlayerId = 2,
                            BaseScore = 5,
                            RegionScore = 3,
                            ResourceScore = 2,
                            TotalScore = 10
                        }
                    }
                }
            };

            InvokePublic("RefreshFromState", state);

            Assert.That(FindChild(owner.transform, "Game Over Dialog"), Is.Not.Null);
            Assert.That(FindChild(owner.transform, "Final Score Header"), Is.Not.Null);
            Assert.That(FindChild(owner.transform, "Final Score Row P1"), Is.Not.Null);
            Assert.That(FindChild(owner.transform, "Final Score Row P2"), Is.Not.Null);
            Assert.That(FindChild(owner.transform, "Final Score Winner").GetComponent<Text>().text, Does.Contain("P1"));
        }

        [Test]
        public void RefreshFromState_FinalScoreDetailsDefaultToWinnerAndOpenWithBreakdownChart()
        {
            controller = CreateController();
            var state = CreateResolvedScoringState(2);

            InvokePublic("RefreshFromState", state);

            var summary = FindChild(owner.transform, "Final Score Summary View");
            var details = FindChild(owner.transform, "Final Score Details View");
            var detailsButton = FindChild(owner.transform, "Final Score Details Button");

            Assert.That(GetPublicProperty<int>("SelectedFinalScorePlayerId"), Is.EqualTo(2));
            Assert.That(GetPublicProperty<bool>("IsFinalScoreDetailsOpen"), Is.False);
            Assert.That(summary, Is.Not.Null);
            Assert.That(summary.gameObject.activeSelf, Is.True);
            Assert.That(details, Is.Not.Null);
            Assert.That(details.gameObject.activeSelf, Is.False);
            Assert.That(detailsButton, Is.Not.Null);
            Assert.That(detailsButton.GetComponentInChildren<Text>().text, Does.Contain("详情"));
            Assert.That(detailsButton.GetComponentInChildren<Text>().text, Does.Contain(">"));

            detailsButton.GetComponent<Button>().onClick.Invoke();

            Assert.That(GetPublicProperty<bool>("IsFinalScoreDetailsOpen"), Is.True);
            Assert.That(summary.gameObject.activeSelf, Is.False);
            Assert.That(details.gameObject.activeSelf, Is.True);
            Assert.That(detailsButton.GetComponentInChildren<Text>().text, Does.Contain("收起"));

            var winnerChart = FindChild(details, "Final Score Detail Chart P2");
            Assert.That(winnerChart, Is.Not.Null);
            Assert.That(winnerChart.gameObject.activeSelf, Is.True);
            Assert.That(
                FindChild(winnerChart, "Final Score Detail Base Value").GetComponent<Text>().text,
                Is.EqualTo("5"));
            Assert.That(
                FindChild(winnerChart, "Final Score Detail Facility Value").GetComponent<Text>().text,
                Is.EqualTo("6"));
            Assert.That(
                FindChild(winnerChart, "Final Score Detail CityStyle Value").GetComponent<Text>().text,
                Is.EqualTo("2"));
            Assert.That(
                FindChild(winnerChart, "Final Score Detail Region Value").GetComponent<Text>().text,
                Is.EqualTo("4"));
            Assert.That(
                FindChild(winnerChart, "Final Score Detail Resource Value").GetComponent<Text>().text,
                Is.EqualTo("3"));
            Assert.That(
                FindChild(winnerChart, "Final Score Detail Total Value").GetComponent<Text>().text,
                Is.EqualTo("20"));

            var formula = FindChild(winnerChart, "Final Score Detail Formula P2").GetComponent<Text>().text;
            Assert.That(formula, Does.Contain("基础分 5"));
            Assert.That(formula, Does.Contain("设施分 6"));
            Assert.That(formula, Does.Contain("城市样式分 2"));
            Assert.That(formula, Does.Contain("区控分 4"));
            Assert.That(formula, Does.Contain("资源分 3"));
            Assert.That(formula, Does.Contain("= 总分 20"));

            var dialog = FindChild(owner.transform, "Game Over Dialog").GetComponent<RectTransform>();
            var detailsRect = details.GetComponent<RectTransform>();
            var returnButton = FindChild(owner.transform, "Return Start Button").GetComponent<RectTransform>();
            var detailsBottom = detailsRect.anchorMin.y * dialog.rect.height + detailsRect.offsetMin.y;
            var returnButtonTop = returnButton.anchoredPosition.y + returnButton.rect.height;
            Assert.That(detailsBottom, Is.GreaterThan(returnButtonTop));

            FindChild(details, "Final Score Details Back Button").GetComponent<Button>().onClick.Invoke();

            Assert.That(GetPublicProperty<bool>("IsFinalScoreDetailsOpen"), Is.False);
            Assert.That(summary.gameObject.activeSelf, Is.True);
            Assert.That(details.gameObject.activeSelf, Is.False);
        }

        [Test]
        public void FinalScorePlayerButtons_OpenAndSwitchDisplayedPlayer()
        {
            controller = CreateController();
            var state = CreateResolvedScoringState(2);
            InvokePublic("RefreshFromState", state);

            FindChild(owner.transform, "Final Score Player Details Button P1")
                .GetComponent<Button>()
                .onClick
                .Invoke();

            var details = FindChild(owner.transform, "Final Score Details View");
            var firstChart = FindChild(details, "Final Score Detail Chart P1");
            var secondChart = FindChild(details, "Final Score Detail Chart P2");
            Assert.That(GetPublicProperty<bool>("IsFinalScoreDetailsOpen"), Is.True);
            Assert.That(GetPublicProperty<int>("SelectedFinalScorePlayerId"), Is.EqualTo(1));
            Assert.That(firstChart.gameObject.activeSelf, Is.True);
            Assert.That(secondChart.gameObject.activeSelf, Is.False);

            FindChild(details, "Final Score Detail Player Switch P2")
                .GetComponent<Button>()
                .onClick
                .Invoke();

            Assert.That(GetPublicProperty<int>("SelectedFinalScorePlayerId"), Is.EqualTo(2));
            Assert.That(firstChart.gameObject.activeSelf, Is.False);
            Assert.That(secondChart.gameObject.activeSelf, Is.True);
            Assert.That(
                FindChild(secondChart, "Final Score Detail Chart Player Name P2").GetComponent<Text>().text,
                Does.Contain("玩家2"));
            Assert.That(
                FindChild(details, "Final Score Detail Player Color P2").GetComponent<Image>().color,
                Is.EqualTo(new Color(0.003921569f, 0.2705882f, 0.6980392f, 1f)));
        }

        [Test]
        public void RefreshFromState_WhenWinnersTie_DefaultsToFirstSettlementRow()
        {
            controller = CreateController();
            var state = CreateResolvedScoringState(2);
            state.FinalScoring.PlayerScores[0].TotalScore = 20;
            state.FinalScoring.PlayerScores[1].TotalScore = 20;
            state.FinalScoring.WinnerPlayerIds.Clear();
            state.FinalScoring.WinnerPlayerIds.Add(2);
            state.FinalScoring.WinnerPlayerIds.Add(1);
            state.FinalScoring.PlayerScores.Reverse();

            InvokePublic("RefreshFromState", state);

            Assert.That(
                FindChild(owner.transform, "Final Score Row P1").GetSiblingIndex(),
                Is.LessThan(FindChild(owner.transform, "Final Score Row P2").GetSiblingIndex()));
            Assert.That(GetPublicProperty<int>("SelectedFinalScorePlayerId"), Is.EqualTo(1));
        }

        [TestCase(1)]
        [TestCase(4)]
        public void RefreshFromState_FinalScoreDetailsSupportOneToFourPlayers(int playerCount)
        {
            controller = CreateController();
            InvokePublic("RefreshFromState", CreateResolvedScoringState(playerCount));

            Assert.That(GetPublicProperty<int>("SelectedFinalScorePlayerId"), Is.EqualTo(playerCount));
            for (var playerId = 1; playerId <= playerCount; playerId++)
            {
                Assert.That(
                    FindChild(owner.transform, "Final Score Player Details Button P" + playerId),
                    Is.Not.Null);
                Assert.That(
                    FindChild(owner.transform, "Final Score Detail Player Row P" + playerId),
                    Is.Not.Null);
                Assert.That(
                    FindChild(owner.transform, "Final Score Detail Player Switch P" + playerId),
                    Is.Not.Null);
                Assert.That(
                    FindChild(owner.transform, "Final Score Detail Chart P" + playerId),
                    Is.Not.Null);
            }
        }

        private static GameState CreateResolvedScoringState(int playerCount)
        {
            var state = new GameState
            {
                Phase = GamePhase.FinalScoring,
                FinalScoring = new FinalScoringState
                {
                    IsResolved = true,
                    TiebreakSummary = "总分最高。"
                }
            };
            var colors = new[]
            {
                PlayerColor.Red,
                PlayerColor.Blue,
                PlayerColor.Green,
                PlayerColor.Yellow
            };

            for (var playerId = 1; playerId <= playerCount; playerId++)
            {
                state.Players.Add(new PlayerState
                {
                    PlayerId = playerId,
                    Name = "玩家" + playerId,
                    Color = colors[playerId - 1]
                });

                state.FinalScoring.PlayerScores.Add(new FinalPlayerScoreState
                {
                    PlayerId = playerId,
                    BaseScore = 2 * playerId + 1,
                    FacilityScore = 2 * playerId + 2,
                    CityStyleScore = 2,
                    RegionScore = playerId + 2,
                    ResourceScore = 2 * playerId - 1,
                    TotalScore = 7 * playerId + 6
                });
            }

            state.FinalScoring.WinnerPlayerIds.Add(playerCount);
            return state;
        }

        private Component CreateController()
        {
            var type = Type.GetType("YC.Presentation.RoundTrackerController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.RoundTrackerController.");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/RoundTracker/RoundTracker.prefab");
            Assert.That(prefab, Is.Not.Null, "Missing RoundTracker prefab.");
            owner = UnityEngine.Object.Instantiate(prefab);
            owner.name = "Round Tracker Controller Test";
            var component = owner.GetComponent(type);
            Assert.That(component, Is.Not.Null, "RoundTracker prefab missing controller.");
            EnsureAwakeRan(component);
            return component;
        }

        private static void EnsureAwakeRan(Component component)
        {
            var panelField = component.GetType().GetField(
                "panelTransform",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(panelField, Is.Not.Null, "Missing RoundTrackerController.panelTransform.");
            if (panelField.GetValue(component) != null)
            {
                return;
            }

            var awake = component.GetType().GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(awake, Is.Not.Null, "Missing RoundTrackerController.Awake.");
            awake.Invoke(component, null);
        }

        private void StepAnimationToTarget()
        {
            InvokePrivate("StepPanelAnimation", 1f);
        }

        private void InvokePublic(string methodName, params object[] args)
        {
            var method = controller.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "Missing RoundTrackerController." + methodName + ".");
            method.Invoke(controller, args);
        }

        private void InvokePrivate(string methodName, params object[] args)
        {
            var method = controller.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing RoundTrackerController." + methodName + ".");
            method.Invoke(controller, args);
        }

        private T GetPrivateField<T>(string fieldName)
        {
            var field = controller.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing RoundTrackerController." + fieldName + ".");
            return (T)field.GetValue(controller);
        }

        private T GetPublicProperty<T>(string propertyName)
        {
            var property = controller.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null, "Missing RoundTrackerController." + propertyName + ".");
            return (T)property.GetValue(controller, null);
        }

        private Type GetControllerType()
        {
            var type = Type.GetType("YC.Presentation.RoundTrackerController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.RoundTrackerController.");
            return type;
        }

        private float GetPrivateStaticFloat(string fieldName)
        {
            var field = GetControllerType().GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing RoundTrackerController." + fieldName + ".");
            return (float)field.GetValue(null);
        }

        private string GetPrivateStaticString(string fieldName)
        {
            var field = GetControllerType().GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing RoundTrackerController." + fieldName + ".");
            return field.GetValue(null) as string;
        }

        private static Transform FindChild(Transform parent, string name)
        {
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == name)
                {
                    return child;
                }

                var match = FindChild(child, name);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }
    }
}
