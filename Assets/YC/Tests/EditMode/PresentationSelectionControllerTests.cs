using NUnit.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using YC.Application.Gameplay;
using YC.Domain.Cards;
using YC.Domain.CardFlows;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    public sealed class PresentationSelectionControllerTests
    {
        [Test]
        public void ExplorePaymentRecipientSelection_BuildsDefaultsAllowsValidSelectionAndEncodesRecipients()
        {
            var controller = CreateController("YC.Presentation.ExplorePaymentRecipientSelectionController");
            var path = new MapPath
            {
                LocationIds = { "A", "B", "C" },
                RouteIds = { "route-owned", "route-opponent", "route-system" }
            };

            Func<string, bool> hasLocalInfluence = routeId => routeId == "route-owned";
            Func<string, List<int>> getOpponentOwners = routeId =>
                routeId == "route-opponent" ? new List<int> { 2, 3 } : new List<int>();

            Invoke(controller, "BuildForPath", path, hasLocalInfluence, getOpponentOwners);

            var choices = (ICollection)GetProperty(controller, "Choices");
            Assert.That(choices.Count, Is.EqualTo(1));
            Assert.That((bool)GetProperty(controller, "HasChoices"), Is.True);
            Assert.That(Invoke(controller, "EncodeRecipients"), Is.EqualTo("route-opponent=2"));

            Assert.That(Invoke(controller, "SelectRecipient", "route-opponent", 3), Is.True);
            Assert.That(Invoke(controller, "SelectRecipient", "route-opponent", 4), Is.False);
            Assert.That(Invoke(controller, "EncodeRecipients"), Is.EqualTo("route-opponent=3"));
        }

        [Test]
        public void EventOptionSelection_ReportsInfluenceTargetRequirement()
        {
            var controller = CreateController("YC.Presentation.EventOptionSelectionController");
            var card = CreateInfluenceChoiceCard(2);

            Invoke(controller, "Begin", card);
            var args = new object[] { 0, null };
            var selected = (bool)controller.GetType().GetMethod("TrySelectChoice").Invoke(controller, args);

            Assert.That(selected, Is.True);
            Assert.That(GetProperty(args[1], "ChoiceIndex"), Is.EqualTo(0));
            Assert.That(GetProperty(args[1], "RequiredInfluenceSlotCount"), Is.EqualTo(2));
            Assert.That(GetProperty(args[1], "RequiresInfluenceTargets"), Is.EqualTo(true));
        }

        [Test]
        public void EventInfluenceTargetSelection_CompletesAfterRequiredSlotsAndWritesCommandParameter()
        {
            var controller = CreateController("YC.Presentation.EventInfluenceTargetSelectionController");
            var card = CreateInfluenceChoiceCard(2);

            Invoke(controller, "Begin", 0);

            var firstSelection = new object[] { card, "location:A:0", null };
            Assert.That((bool)controller.GetType().GetMethod("TrySelectSlot").Invoke(controller, firstSelection), Is.True);
            Assert.That(firstSelection[2], Is.EqualTo(false));

            var duplicateSelection = new object[] { card, "location:A:0", null };
            Assert.That((bool)controller.GetType().GetMethod("TrySelectSlot").Invoke(controller, duplicateSelection), Is.True);
            Assert.That(duplicateSelection[2], Is.EqualTo(false));

            var secondSelection = new object[] { card, "route:R1:0", null };
            Assert.That((bool)controller.GetType().GetMethod("TrySelectSlot").Invoke(controller, secondSelection), Is.True);
            Assert.That(secondSelection[2], Is.EqualTo(true));

            var command = new GameCommand { Kind = GameCommandKind.ExploreLocation, PlayerId = 1 };
            Invoke(controller, "AddCommandParameter", command, ExploreLocationCommandHandler.EventInfluenceSlotIdsParameter);

            Assert.That(
                command.Parameters[ExploreLocationCommandHandler.EventInfluenceSlotIdsParameter],
                Is.EqualTo("location:A:0,route:R1:0"));
        }

        [Test]
        public void BuildFacilitySelection_CreatesBuildCommandWithFacilitySlotAndPaymentMode()
        {
            var controller = CreateController("YC.Presentation.BuildFacilitySelectionController");

            var method = controller.GetType().GetMethod(
                "CreateCommand",
                new[] { typeof(int), typeof(string), typeof(int), typeof(string) });
            Assert.That(method, Is.Not.Null);
            var command = (GameCommand)method.Invoke(
                controller,
                new object[]
                {
                    1,
                    FacilityCardDatabase.TradeDistrict,
                    3,
                    BuildFacilityService.PaymentModeGold
                });

            Assert.That(command.Kind, Is.EqualTo(GameCommandKind.BuildFacility));
            Assert.That(command.PlayerId, Is.EqualTo(1));
            Assert.That(command.TargetId, Is.EqualTo(FacilityCardDatabase.TradeDistrict));
            Assert.That(command.Parameters[BuildFacilityCommandHandler.CityBoardSlotIndexParameter], Is.EqualTo("3"));
            Assert.That(command.Parameters[BuildFacilityCommandHandler.PaymentModeParameter], Is.EqualTo(BuildFacilityService.PaymentModeGold));
        }

        [Test]
        public void CityStyleSelection_ReportsAvailabilityAndCreatesDeclareCommand()
        {
            var controller = CreateController("YC.Presentation.CityStyleSelectionController");
            var state = CreateCityStyleActionState();
            AddFacility(state, FacilityCardDatabase.SourceStoneRefinery, 0);
            AddFacility(state, FacilityCardDatabase.UrbanizedArea, 1);
            AddFacility(state, FacilityCardDatabase.TradeDistrict, 2);

            var options = (IList)Invoke(controller, "BuildOptions", state, 1);
            Assert.That(options.Count, Is.EqualTo(1));
            Assert.That((bool)GetProperty(options[0], "CanDeclare"), Is.True);
            Assert.That(GetProperty(options[0], "Reason"), Is.EqualTo("可宣告"));

            var command = (GameCommand)Invoke(controller, "CreateCommand", 1, CityStyleDatabase.SourceStoneIndustrialHub);
            Assert.That(command.Kind, Is.EqualTo(GameCommandKind.DeclareCityStyle));
            Assert.That(command.PlayerId, Is.EqualTo(1));
            Assert.That(command.TargetId, Is.EqualTo(CityStyleDatabase.SourceStoneIndustrialHub));
            Assert.That(command.Parameters[DeclareCityStyleCommandHandler.CityStyleIdParameter], Is.EqualTo(CityStyleDatabase.SourceStoneIndustrialHub));
        }

        [Test]
        public void BuildInfoPanel_TypeExistsForRightSideBuildPanel()
        {
            var type = Type.GetType("YC.Presentation.BuildInfoPanel, Assembly-CSharp", false);

            Assert.That(type, Is.Not.Null);
            Assert.That(BuildFacilityService.CityBoardSlotCount, Is.EqualTo(12));
        }

        [Test]
        public void BuildInfoPanel_RefreshCreatesInteractiveBuildHotspotsWithoutEmptySlotLabels()
        {
            var owner = new GameObject("Build Info Panel Test");
            GameObject canvasObject = null;
            try
            {
                var type = Type.GetType("YC.Presentation.BuildInfoPanel, Assembly-CSharp", false);
                Assert.That(type, Is.Not.Null);
                var panel = owner.AddComponent(type);
                var state = CreateBuildInfoPanelState();
                var clickedFacilityId = string.Empty;
                var clickedCityStyleId = string.Empty;
                var clickedSlotIndex = -1;

                type.GetEvent("FacilityClicked").AddEventHandler(panel, new Action<string>(id => clickedFacilityId = id));
                type.GetEvent("CityStyleClicked").AddEventHandler(panel, new Action<string>(id => clickedCityStyleId = id));
                type.GetEvent("CityBoardSlotClicked").AddEventHandler(panel, new Action<int>(slotIndex => clickedSlotIndex = slotIndex));
                type.GetMethod("Initialize").Invoke(panel, new object[] { owner.transform });
                type.GetMethod("Refresh").Invoke(panel, new object[] { state, 1 });

                canvasObject = GameObject.Find("Build Info Panel Canvas");
                Assert.That(canvasObject, Is.Not.Null);

                var buttons = canvasObject.GetComponentsInChildren<Button>(true);
                Assert.That(CountButtonsByNamePrefix(buttons, "槽位 "), Is.EqualTo(12));
                Assert.That(CountButtonsByNamePrefix(buttons, "设施 "), Is.EqualTo(state.Decks.FacilitySupply.Count));
                Assert.That(CountButtonsByNamePrefix(buttons, "城市样式 "), Is.EqualTo(state.Decks.CityStyleSupply.Count));

                var boardImage = FindRectTransformByName(canvasObject, "城市面板底图");
                Assert.That(boardImage, Is.Not.Null);
                Assert.That(boardImage.rect.height / boardImage.rect.width, Is.EqualTo(3801f / 2059f).Within(0.01f));
                Assert.That(HasTextContaining(canvasObject, "空位 "), Is.False);
                Assert.That(GetButtonLabel(FindButtonByName(buttons, "槽位 4")), Is.EqualTo("源石精炼厂"));
                AssertCityBoardSlotIsCenteredOnBoard(FindButtonByName(buttons, "槽位 2"), boardImage, 0.502f, 0.162f);

                Assert.That(HasText(canvasObject, "剩余牌堆：1"), Is.True);
                Assert.That(HasTextContaining(canvasObject, "玩家一：源石工业中枢"), Is.True);
                Assert.That(HasTextContaining(canvasObject, "玩家二：暂无宣告"), Is.True);
                Assert.That(AllTextRenderersAreMaskable(canvasObject), Is.True);

                InvokeButtonByName(buttons, "槽位 12");
                InvokeButtonByName(buttons, "设施 1");
                InvokeButtonByName(buttons, "城市样式 1");

                Assert.That(clickedSlotIndex, Is.EqualTo(11));
                Assert.That(clickedFacilityId, Is.EqualTo(FacilityCardDatabase.TradeDistrict));
                Assert.That(clickedCityStyleId, Is.EqualTo(CityStyleDatabase.SourceStoneIndustrialHub));
            }
            finally
            {
                if (canvasObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(canvasObject);
                }

                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void BuildInfoPanel_RefreshRotatesDeclaredCityStyleSlotsOnly()
        {
            var owner = new GameObject("Build Info Panel Used Slot Test");
            GameObject canvasObject = null;
            try
            {
                var type = Type.GetType("YC.Presentation.BuildInfoPanel, Assembly-CSharp", false);
                Assert.That(type, Is.Not.Null);
                var panel = owner.AddComponent(type);
                var state = CreateBuildInfoPanelUsedSlotState();

                type.GetMethod("Initialize").Invoke(panel, new object[] { owner.transform });
                type.GetMethod("Refresh").Invoke(panel, new object[] { state, 1 });

                canvasObject = GameObject.Find("Build Info Panel Canvas");
                Assert.That(canvasObject, Is.Not.Null);

                var buttons = canvasObject.GetComponentsInChildren<Button>(true);
                AssertCityBoardSlotRotation(FindButtonByName(buttons, "槽位 1"), 0f);
                AssertCityBoardSlotRotation(FindButtonByName(buttons, "槽位 4"), 0f);
                Assert.That(CountTexts(canvasObject, "已使用"), Is.EqualTo(0));

                var player = state.FindPlayer(1);
                player.DeclaredCityStyleIds.Add(CityStyleDatabase.SourceStoneIndustrialHub);
                player.DeclaredCityStyles.Add(new CityStyleDeclarationState
                {
                    CityStyleId = CityStyleDatabase.SourceStoneIndustrialHub,
                    UsedFacilityIds =
                    {
                        FacilityCardDatabase.SourceStoneRefinery,
                        FacilityCardDatabase.UrbanizedArea,
                        FacilityCardDatabase.TradeDistrict
                    },
                    UsedCityBoardSlotIndexes = { 0, 1, 2 }
                });

                type.GetMethod("Refresh").Invoke(panel, new object[] { state, 1 });

                buttons = canvasObject.GetComponentsInChildren<Button>(true);
                AssertCityBoardSlotRotation(FindButtonByName(buttons, "槽位 1"), 180f);
                AssertCityBoardSlotRotation(FindButtonByName(buttons, "槽位 2"), 180f);
                AssertCityBoardSlotRotation(FindButtonByName(buttons, "槽位 3"), 180f);
                AssertCityBoardSlotRotation(FindButtonByName(buttons, "槽位 4"), 0f);
                Assert.That(CountTexts(canvasObject, "已使用"), Is.EqualTo(3));
            }
            finally
            {
                if (canvasObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(canvasObject);
                }

                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void PendingChoiceView_PrefersPendingCardSessionAndFallsBackToLegacyChoice()
        {
            var state = new GameState
            {
                PendingCardSession = new PendingCardSessionState
                {
                    SessionId = "session-new",
                    ScenarioId = "scenario-new",
                    ChoiceType = ExploreLocationCommandHandler.ExploreEventChoiceType,
                    CardId = "event_yellow_01",
                    PlayerId = 1,
                    TargetId = "mine-b",
                    OptionIds = { "0", "1" }
                },
                PendingChoice = new PendingChoiceState
                {
                    ChoiceId = "legacy",
                    ChoiceType = MoveCityCommandHandler.MoveCityEventChoiceType,
                    CardId = "event_green_01",
                    PlayerId = 2,
                    TargetId = "A-02",
                    OptionIds = { "0" }
                }
            };

            var view = CardFlowStateAdapter.GetPendingChoiceView(state);

            Assert.That(view, Is.Not.Null);
            Assert.That(view.IsFromPendingCardSession, Is.True);
            Assert.That(view.ChoiceType, Is.EqualTo(ExploreLocationCommandHandler.ExploreEventChoiceType));
            Assert.That(view.CardId, Is.EqualTo("event_yellow_01"));
            Assert.That(view.PlayerId, Is.EqualTo(1));
            Assert.That(view.TargetId, Is.EqualTo("mine-b"));

            state.PendingCardSession = null;
            view = CardFlowStateAdapter.GetPendingChoiceView(state);

            Assert.That(view, Is.Not.Null);
            Assert.That(view.IsFromPendingCardSession, Is.False);
            Assert.That(view.ChoiceType, Is.EqualTo(MoveCityCommandHandler.MoveCityEventChoiceType));
            Assert.That(view.CardId, Is.EqualTo("event_green_01"));
            Assert.That(view.PlayerId, Is.EqualTo(2));
            Assert.That(view.TargetId, Is.EqualTo("A-02"));
        }

        private static EventCardDefinition CreateInfluenceChoiceCard(int amount)
        {
            return new EventCardDefinition
            {
                CardId = "test-card",
                ChoiceDescriptions = { "place influence" },
                ChoiceRewards = { new ResourceSet() },
                ChoicePendingEffects =
                {
                    new List<EventEffect>
                    {
                        EventEffect.PlaceInfluence(EventEffectTargetScope.CurrentLocationOrAdjacentRoute, amount)
                    }
                }
            };
        }

        private static object CreateController(string typeName)
        {
            var type = Type.GetType(typeName + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, "Missing presentation controller type " + typeName + ".");
            return Activator.CreateInstance(type);
        }

        private static object Invoke(object target, string methodName, params object[] args)
        {
            var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "Missing method " + methodName + ".");
            return method.Invoke(target, args);
        }

        private static object GetProperty(object target, string propertyName)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null, "Missing property " + propertyName + ".");
            return property.GetValue(target);
        }

        private static GameState CreateCityStyleActionState()
        {
            return new GameState
            {
                Phase = GamePhase.ActionRound1,
                Round = 1,
                ActionRound = 1,
                StartPlayerId = 1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Color = PlayerColor.Blue
                    }
                },
                Decks =
                {
                    CityStyleSupply = { CityStyleDatabase.SourceStoneIndustrialHub }
                }
            };
        }

        private static GameState CreateBuildInfoPanelState()
        {
            var state = new GameState
            {
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Name = "玩家一",
                        DeclaredCityStyleIds = { CityStyleDatabase.SourceStoneIndustrialHub }
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Name = "玩家二"
                    }
                }
            };

            state.Decks.FacilitySupply.Add(FacilityCardDatabase.TradeDistrict);
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.EquipmentWarehouse);
            state.Decks.FacilityDeck.Add(FacilityCardDatabase.FederalOffice);
            state.Decks.CityStyleSupply.Add(CityStyleDatabase.SourceStoneIndustrialHub);
            state.Map.Facilities.Add(new FacilityPlacement
            {
                PlayerId = 1,
                FacilityCardId = FacilityCardDatabase.SourceStoneRefinery,
                CityBoardSlotIndex = 3
            });

            return state;
        }

        private static GameState CreateBuildInfoPanelUsedSlotState()
        {
            var state = new GameState
            {
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Name = "玩家一"
                    }
                }
            };

            AddFacility(state, FacilityCardDatabase.SourceStoneRefinery, 0);
            AddFacility(state, FacilityCardDatabase.UrbanizedArea, 1);
            AddFacility(state, FacilityCardDatabase.TradeDistrict, 2);
            AddFacility(state, FacilityCardDatabase.EquipmentWarehouse, 3);
            return state;
        }

        private static int CountButtonsByNamePrefix(Button[] buttons, string prefix)
        {
            var count = 0;
            for (var i = 0; i < buttons.Length; i++)
            {
                if (buttons[i].name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static void InvokeButtonByName(Button[] buttons, string name)
        {
            for (var i = 0; i < buttons.Length; i++)
            {
                if (buttons[i].name == name)
                {
                    buttons[i].onClick.Invoke();
                    return;
                }
            }

            Assert.Fail("Missing button: " + name);
        }

        private static Button FindButtonByName(Button[] buttons, string name)
        {
            for (var i = 0; i < buttons.Length; i++)
            {
                if (buttons[i].name == name)
                {
                    return buttons[i];
                }
            }

            Assert.Fail("Missing button: " + name);
            return null;
        }

        private static void AssertCityBoardSlotRotation(Button button, float expectedZ)
        {
            Assert.That(button, Is.Not.Null);
            var rect = button.GetComponent<RectTransform>();
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(rect.localEulerAngles.z, expectedZ)), Is.LessThan(0.1f));
        }

        private static void AssertCityBoardSlotIsCenteredOnBoard(Button button, RectTransform boardImage, float normalizedX, float normalizedY)
        {
            Assert.That(button, Is.Not.Null);
            Assert.That(boardImage, Is.Not.Null);
            var rect = button.GetComponent<RectTransform>();
            Assert.That(rect.parent, Is.EqualTo(boardImage));
            Assert.That(rect.anchoredPosition.x, Is.EqualTo((normalizedX - 0.5f) * boardImage.rect.width).Within(0.5f));
            Assert.That(rect.anchoredPosition.y, Is.EqualTo((0.5f - normalizedY) * boardImage.rect.height).Within(0.5f));
        }

        private static string GetButtonLabel(Button button)
        {
            Assert.That(button, Is.Not.Null);
            var text = button.GetComponentInChildren<Text>(true);
            return text == null ? string.Empty : text.text;
        }

        private static RectTransform FindRectTransformByName(GameObject root, string name)
        {
            var rects = root.GetComponentsInChildren<RectTransform>(true);
            for (var i = 0; i < rects.Length; i++)
            {
                if (rects[i].name == name)
                {
                    return rects[i];
                }
            }

            return null;
        }

        private static bool HasText(GameObject root, string expected)
        {
            var texts = root.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < texts.Length; i++)
            {
                if (texts[i].text == expected)
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountTexts(GameObject root, string expected)
        {
            var count = 0;
            var texts = root.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < texts.Length; i++)
            {
                if (texts[i].text == expected)
                {
                    count++;
                }
            }

            return count;
        }

        private static bool HasTextContaining(GameObject root, string expected)
        {
            var texts = root.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < texts.Length; i++)
            {
                if (texts[i].text.Contains(expected))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool AllTextRenderersAreMaskable(GameObject root)
        {
            var texts = root.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < texts.Length; i++)
            {
                if (texts[i] != null && !texts[i].maskable)
                {
                    return false;
                }
            }

            return true;
        }

        private static void AddFacility(GameState state, string facilityId, int slotIndex)
        {
            state.Map.Facilities.Add(new FacilityPlacement
            {
                PlayerId = 1,
                FacilityCardId = facilityId,
                CityBoardSlotIndex = slotIndex
            });
        }
    }
}
