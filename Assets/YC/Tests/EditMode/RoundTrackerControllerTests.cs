using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.Rules;
using YC.Domain.State;

namespace YC.Tests.EditMode
{
    public sealed class RoundTrackerControllerTests
    {
        private GameObject owner;
        private GameObject createdEventSystem;
        private Component controller;

        [TearDown]
        public void TearDown()
        {
            if (owner != null)
            {
                UnityEngine.Object.DestroyImmediate(owner);
                owner = null;
            }

            if (createdEventSystem != null)
            {
                UnityEngine.Object.DestroyImmediate(createdEventSystem);
                createdEventSystem = null;
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

        private Component CreateController()
        {
            var existingEventSystem = UnityEngine.Object.FindObjectOfType<EventSystem>();
            var type = Type.GetType("YC.Presentation.RoundTrackerController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing YC.Presentation.RoundTrackerController.");

            owner = new GameObject("Round Tracker Controller Test");
            var component = owner.AddComponent(type);
            EnsureAwakeRan(component);

            if (existingEventSystem == null)
            {
                var eventSystem = UnityEngine.Object.FindObjectOfType<EventSystem>();
                createdEventSystem = eventSystem == null ? null : eventSystem.gameObject;
            }

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
