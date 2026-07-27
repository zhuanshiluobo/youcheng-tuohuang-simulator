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
using YC.Domain.Harvest;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation;
using YC.Presentation.Workflows;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YC.Tests.EditMode
{
    public sealed class PresentationSelectionControllerTests
    {
        private const string YellowSourceStoneRefinery = "building_028";
        private const string RedIronRefinery = "building_032";

        [Test]
        public void ExplorePaymentRecipientSelection_BuildsDefaultsAllowsValidSelectionAndEncodesRecipients()
        {
            var controller = new ExplorePaymentRecipientSelectionController();
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
        public void ExplorePathSelection_KeepsTargetAndSelectsIndexedPathChoice()
        {
            var controller = new ExplorePathSelectionController();
            var directPath = new MapPath
            {
                LocationIds = { "A", "B" },
                RouteIds = { "R1" }
            };
            var tollPath = new MapPath
            {
                LocationIds = { "A", "C", "B" },
                RouteIds = { "R2", "R3" }
            };

            Invoke(controller, "BeginTarget", "B");
            Invoke(
                controller,
                "SetPathChoices",
                new List<MapPath> { directPath, tollPath },
                new Func<MapPath, int, string>((path, index) => "路线 " + (index + 1) + "：" + string.Join(",", path.RouteIds.ToArray())));

            Assert.That(GetProperty(controller, "TargetLocationId"), Is.EqualTo("B"));
            Assert.That(GetProperty(controller, "SelectedPath"), Is.Null);
            var pathChoices = (IList)GetProperty(controller, "PathChoices");
            Assert.That(pathChoices.Count, Is.EqualTo(2));
            Assert.That(GetField(pathChoices[1], "Label"), Is.EqualTo("路线 2：R2,R3"));

            Assert.That(Invoke(controller, "TrySelectPathChoice", 1), Is.True);
            Assert.That(GetProperty(controller, "SelectedPath"), Is.SameAs(tollPath));
            Assert.That(Invoke(controller, "TrySelectPathChoice", 2), Is.False);
        }

        [Test]
        public void ResourceCollectionSelection_TogglesLocationsBuildsRoutesAndEncodesPayments()
        {
            var controller = new ResourceCollectionSelectionController();
            Invoke(controller, "AddCandidateLocation", "A");
            Invoke(controller, "AddCandidateLocation", "B");
            Invoke(controller, "SetPathForLocation", "A", new MapPath
            {
                LocationIds = { "City", "A" },
                RouteIds = { "R1" }
            });
            Invoke(controller, "SetPathForLocation", "B", new MapPath
            {
                LocationIds = { "City", "A", "B" },
                RouteIds = { "R1", "R2" }
            });
            Invoke(controller, "AddRoute", "R1");
            Invoke(controller, "AddRoute", "R2");

            Assert.That(Invoke(controller, "ToggleLocation", "B", new Func<string, bool>(locationId => locationId == "A")).ToString(), Is.EqualTo("Unavailable"));
            Invoke(controller, "ConfirmRoutePayment", "R1", 2);
            Invoke(controller, "RefreshSelection", new Func<string, bool>(locationId => locationId != "B"));
            Assert.That(GetProperty(GetProperty(controller, "SelectedLocationIds"), "Count"), Is.EqualTo(1));

            Assert.That(Invoke(controller, "ToggleLocation", "A", new Func<string, bool>(locationId => true)).ToString(), Is.EqualTo("Removed"));
            Assert.That(GetProperty(GetProperty(controller, "SelectedLocationIds"), "Count"), Is.EqualTo(1));
            Assert.That(Invoke(controller, "ToggleLocation", "A", new Func<string, bool>(locationId => true)).ToString(), Is.EqualTo("Added"));

            var ordered = new List<MapLocationDefinition>
            {
                new MapLocationDefinition { LocationId = "B" },
                new MapLocationDefinition { LocationId = "A" }
            };
            var selectedLocations = (IList)Invoke(controller, "BuildSelectedLocationIds", ordered);
            var selectedRoutes = Invoke(controller, "BuildSelectedRouteIds", selectedLocations);

            Assert.That(selectedLocations, Is.EqualTo(new List<string> { "B", "A" }));
            Assert.That(Invoke(selectedRoutes, "SetEquals", (object)new[] { "R1", "R2" }), Is.True);
            Assert.That(Invoke(controller, "EncodePaymentRecipients", selectedRoutes), Is.EqualTo("R1=2"));
        }

        [Test]
        public void ResourceCollectionSelection_ApplyQuery_ReplacesDerivedNetworkAndPreservesPaymentChoice()
        {
            var controller = new ResourceCollectionSelectionController();
            var initialQuery = new ResourceCollectionSelectionQuery();
            initialQuery.CandidateLocationIds.Add("B");
            initialQuery.GetType()
                .GetMethod("AddRouteOption", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(initialQuery, new object[] { new ResourceCollectionRouteOption { RouteId = "R2" } });

            Invoke(controller, "ApplyQuery", initialQuery);

            Assert.That(Invoke(controller, "HasCandidateLocation", "B"), Is.True);
            Assert.That(Invoke(controller, "HasRoute", "R2"), Is.True);
            Invoke(controller, "ConfirmRoutePayment", "R2", 2);

            var expandedQuery = new ResourceCollectionSelectionQuery();
            expandedQuery.CandidateLocationIds.Add("B");
            expandedQuery.GetType()
                .GetMethod("AddPath", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(expandedQuery, new object[]
                {
                    "B",
                    new MapPath
                    {
                        LocationIds = { "A", "B" },
                        RouteIds = { "R2" }
                    }
                });

            Invoke(controller, "ApplyQuery", expandedQuery);

            Assert.That(Invoke(controller, "HasRoute", "R2"), Is.False);
            Assert.That(GetProperty(GetProperty(controller, "PaidRouteIds"), "Count"), Is.EqualTo(1));
            var pathArgs = new object[] { "B", null };
            Assert.That(controller.GetType().GetMethod("TryGetPath").Invoke(controller, pathArgs), Is.True);
            Assert.That(((MapPath)pathArgs[1]).RouteIds, Is.EqualTo(new[] { "R2" }));
        }

        [Test]
        public void MapInteractionConfirmation_SecondRequestConfirmsAndClearRestoresState()
        {
            var controller = new MapInteractionConfirmationController();
            var confirmed = false;
            var firstRequest = new object[] { "move", "A", "A", "location:A:0", new Action(() => confirmed = true), null };

            Assert.That(Invoke(controller, "Request", firstRequest), Is.False);
            Assert.That(GetProperty(controller, "HasPending"), Is.True);
            Assert.That(GetProperty(controller, "LocationId"), Is.EqualTo("A"));
            Assert.That(GetProperty(controller, "SlotId"), Is.EqualTo("location:A:0"));

            Invoke(controller, "Clear");
            Assert.That(GetProperty(controller, "HasPending"), Is.False);

            var pendingRequest = new object[] { "move", "A", "A", string.Empty, new Action(() => confirmed = true), null };
            Assert.That(Invoke(controller, "Request", pendingRequest), Is.False);
            var confirmRequest = new object[] { "move", "A", "A", string.Empty, new Action(() => Assert.Fail("第二次请求应使用首次回调")), null };
            Assert.That(Invoke(controller, "Request", confirmRequest), Is.True);
            Assert.That(GetProperty(controller, "HasPending"), Is.False);

            var callback = (Action)confirmRequest[5];
            callback();
            Assert.That(confirmed, Is.True);
        }

        [Test]
        public void EventOptionSelection_ReportsInfluenceTargetRequirement()
        {
            var controller = new EventOptionSelectionController();
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
            var controller = new EventInfluenceTargetSelectionController();
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
            var controller = new BuildFacilitySelectionController();

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
        public void BuildFacilitySelection_KeepsDraftLocalAcrossDragEscapeModifyFailureAndCancel()
        {
            var state = new GameState
            {
                Phase = GamePhase.ActionRound1,
                CurrentPlayerId = 1,
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Resources = new ResourceSet { GoldVoucher = 100 }
                    }
                },
                Decks =
                {
                    FacilitySupply = { FacilityCardDatabase.TradeDistrict }
                },
                Map =
                {
                    Facilities =
                    {
                        new FacilityPlacement
                        {
                            PlayerId = 1,
                            FacilityCardId = FacilityCardDatabase.CoreCommandTower,
                            CityBoardSlotIndex = 0
                        }
                    }
                }
            };
            var controller = new BuildFacilitySelectionController();
            controller.Begin(1);
            Assert.That(controller.Phase, Is.EqualTo(BuildFacilityDraftPhase.Selecting));

            string reason;
            Assert.That(controller.TryBeginDrag(state, FacilityCardDatabase.TradeDistrict, out reason), Is.True, reason);
            Assert.That(controller.Phase, Is.EqualTo(BuildFacilityDraftPhase.Dragging));
            Assert.That(controller.QueryLegalSlotIndexes(state), Has.None.EqualTo(0));
            Assert.That(controller.TryDrop(state, 0, out reason), Is.False);
            Assert.That(controller.Phase, Is.EqualTo(BuildFacilityDraftPhase.Selecting));
            Assert.That(controller.IsActive, Is.True, "非法落点不能隐式取消建设。");
            Assert.That(controller.FacilityId, Is.Empty);

            Assert.That(controller.TryBeginDrag(state, FacilityCardDatabase.TradeDistrict, out reason), Is.True, reason);
            Assert.That(controller.TryDrop(state, 3, out reason), Is.True, reason);
            Assert.That(controller.CollapseFocusToGhost(), Is.True);
            Assert.That(controller.Phase, Is.EqualTo(BuildFacilityDraftPhase.Ghosted));
            Assert.That(controller.CityBoardSlotIndex, Is.EqualTo(3));

            Assert.That(controller.TryBeginGhostDrag(out reason), Is.True, reason);
            Assert.That(controller.TryDrop(state, 0, out reason), Is.False);
            Assert.That(controller.Phase, Is.EqualTo(BuildFacilityDraftPhase.Ghosted));
            Assert.That(controller.CityBoardSlotIndex, Is.EqualTo(3), "非法重拖应保留旧虚影槽位。");
            Assert.That(controller.TryBeginGhostDrag(out reason), Is.True, reason);
            Assert.That(controller.TryDrop(state, 4, out reason), Is.True, reason);

            Assert.That(controller.TrySelectPayment(state, BuildFacilityService.PaymentModeGold, out reason), Is.True, reason);
            Assert.That(controller.Phase, Is.EqualTo(BuildFacilityDraftPhase.Confirming));
            var command = controller.CreateConfirmationCommand();
            Assert.That(command.Parameters[BuildFacilityCommandHandler.CityBoardSlotIndexParameter], Is.EqualTo("4"));
            controller.MarkSubmissionFailed("最终校验失败");
            Assert.That(controller.Phase, Is.EqualTo(BuildFacilityDraftPhase.Confirming));
            Assert.That(controller.ErrorMessage, Is.EqualTo("最终校验失败"));
            Assert.That(controller.BackToPayment(), Is.True);
            Assert.That(controller.Phase, Is.EqualTo(BuildFacilityDraftPhase.Focused));
            Assert.That(controller.PaymentMode, Is.Empty);

            controller.Cancel();
            Assert.That(controller.Phase, Is.EqualTo(BuildFacilityDraftPhase.Inactive));
            Assert.That(controller.FacilityId, Is.Empty);
            Assert.That(controller.CityBoardSlotIndex, Is.EqualTo(-1));
        }

        [Test]
        public void CityStyleSelection_ReportsAvailabilityAndCreatesDeclareCommand()
        {
            var controller = new CityStyleSelectionController();
            var state = CreateCityStyleActionState();
            AddFacility(state, FacilityCardDatabase.TradeDistrict, 0);
            AddFacility(state, FacilityCardDatabase.EquipmentWarehouse, 3);
            AddFacility(state, FacilityCardDatabase.UrbanizedArea, 4);
            AddFacility(state, YellowSourceStoneRefinery, 6);
            AddFacility(state, FacilityCardDatabase.OriginiumPurificationPlant, 7);
            AddFacility(state, RedIronRefinery, 8);

            var options = (IList)Invoke(controller, "BuildOptions", state, 1);
            Assert.That(options.Count, Is.EqualTo(1));
            Assert.That((bool)GetProperty(options[0], "CanDeclare"), Is.True);
            Assert.That(GetProperty(options[0], "Reason"), Is.EqualTo("可宣告"));

            var selectedSlots = new[] { 0, 3, 4, 6, 7, 8 };
            var selection = controller.ValidateSelection(
                state,
                1,
                CityStyleDatabase.SourceStoneIndustrialHub,
                selectedSlots);
            Assert.That(selection.CanConfirm, Is.True);
            Assert.That(selection.RotationDegrees, Is.Zero);

            var command = controller.CreateCommand(
                1,
                CityStyleDatabase.SourceStoneIndustrialHub,
                selectedSlots);
            Assert.That(command.Kind, Is.EqualTo(GameCommandKind.DeclareCityStyle));
            Assert.That(command.PlayerId, Is.EqualTo(1));
            Assert.That(command.TargetId, Is.EqualTo(CityStyleDatabase.SourceStoneIndustrialHub));
            Assert.That(command.Parameters[DeclareCityStyleCommandHandler.CityStyleIdParameter], Is.EqualTo(CityStyleDatabase.SourceStoneIndustrialHub));
            Assert.That(
                command.Parameters[DeclareCityStyleCommandHandler.UsedCityBoardSlotIndexesParameter],
                Is.EqualTo("0,3,4,6,7,8"));
        }

        [Test]
        public void BuildInfoPanel_TypeExistsForRightSideBuildPanel()
        {
            var type = Type.GetType("YC.Presentation.BuildInfoPanel, Assembly-CSharp", false);

            Assert.That(type, Is.Not.Null);
            Assert.That(BuildFacilityService.CityBoardSlotCount, Is.EqualTo(12));
        }

        [Test]
        public void CardImagePathCatalog_CoversAllFacilityAndCityStyleDefinitions()
        {
            var type = Type.GetType("YC.Presentation.CardImagePathCatalog, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null);

            var facilityPathMethod = type.GetMethod("TryGetFacilityImageRelativePath", BindingFlags.Public | BindingFlags.Static);
            var cityStylePathMethod = type.GetMethod("TryGetCityStyleImageRelativePath", BindingFlags.Public | BindingFlags.Static);
            Assert.That(facilityPathMethod, Is.Not.Null);
            Assert.That(cityStylePathMethod, Is.Not.Null);

            var facilityIds = new List<string>(FacilityCardDatabase.DefaultSupplyIds);
            facilityIds.AddRange(FacilityCardDatabase.ReserveIds);
            for (var i = 0; i < facilityIds.Count; i++)
            {
                AssertImagePathExists(facilityPathMethod, facilityIds[i]);
            }

            for (var i = 0; i < CityStyleDatabase.DefaultSupplyIds.Count; i++)
            {
                AssertImagePathExists(cityStylePathMethod, CityStyleDatabase.DefaultSupplyIds[i]);
            }

            Assert.That(typeof(FacilityCardDefinition).GetField("ImageRelativePath"), Is.Null);
            Assert.That(typeof(CityStyleDefinition).GetField("ImageRelativePath"), Is.Null);

            var unknownArguments = new object[] { "unknown_card", null };
            Assert.That(facilityPathMethod.Invoke(null, unknownArguments), Is.False);
            Assert.That(unknownArguments[1], Is.EqualTo(string.Empty));
        }

        [Test]
        public void EventChoiceDialog_BuildActionsDispatchExactlyOneIntentPerUiOperation()
        {
            var root = new GameObject("Build Facility Dialog Test Root", typeof(RectTransform));
            GameObject rightClickRoot = null;
            GameObject confirmationRoot = null;
            try
            {
                var dialogType = Type.GetType("YC.Presentation.EventChoiceDialog, Assembly-CSharp", false);
                Assert.That(dialogType, Is.Not.Null);
                var dialog = Activator.CreateInstance(dialogType, true);
                var focusIntents = new List<BuildFacilityIntent>();
                var model = new BuildFacilityDraftViewModel(
                    BuildFacilityDraftPhase.Focused,
                    new List<BuildFacilityOptionQueryResult>().AsReadOnly(),
                    null,
                    FacilityCardDatabase.Get(FacilityCardDatabase.TradeDistrict),
                    3,
                    string.Empty,
                    string.Empty,
                    new List<int>().AsReadOnly(),
                    dispatch: intent => focusIntents.Add(intent));

                dialogType.GetMethod("ShowBuildFacilityFocus").Invoke(
                    dialog,
                    new object[] { root.GetComponent<RectTransform>(), model });

                var resourceButton = FindButtonByName(
                    root.GetComponentsInChildren<Button>(true),
                    "Choose Resource Payment");
                var goldButton = FindButtonByName(
                    root.GetComponentsInChildren<Button>(true),
                    "Choose Gold Payment");
                var closeButton = FindButtonByName(
                    root.GetComponentsInChildren<Button>(true),
                    "Close Build Facility Focus Button");
                var panel = FindRectTransformByName(root, "Build Facility Focus Panel");
                resourceButton.onClick.Invoke();
                goldButton.onClick.Invoke();
                Assert.That(focusIntents, Has.Count.EqualTo(2));
                Assert.That(focusIntents[0], Is.TypeOf<BuildFacilityIntent.SelectPayment>());
                Assert.That(
                    ((BuildFacilityIntent.SelectPayment)focusIntents[0]).PaymentMode,
                    Is.EqualTo(BuildFacilityService.PaymentModeResources));
                Assert.That(focusIntents[1], Is.TypeOf<BuildFacilityIntent.SelectPayment>());
                Assert.That(
                    ((BuildFacilityIntent.SelectPayment)focusIntents[1]).PaymentMode,
                    Is.EqualTo(BuildFacilityService.PaymentModeGold));
                Assert.That(closeButton.GetComponent<Outline>(), Is.Not.Null);
                Assert.That(
                    closeButton.transform.GetSiblingIndex(),
                    Is.EqualTo(panel.childCount - 1),
                    "关闭按钮必须位于窗口内容的最上层，避免被标题文本拦截点击。");
                Assert.That(
                    ExecuteEvents.Execute(
                        closeButton.gameObject,
                        new PointerEventData(EventSystem.current)
                        {
                            button = PointerEventData.InputButton.Left
                        },
                        ExecuteEvents.pointerClickHandler),
                    Is.True);
                Assert.That(focusIntents, Has.Count.EqualTo(3));
                Assert.That(focusIntents[2], Is.TypeOf<BuildFacilityIntent.Cancel>());

                var inputHandlerType = Type.GetType(
                    "YC.Presentation.WindowCloseInputHandler, Assembly-CSharp",
                    false);
                Assert.That(inputHandlerType, Is.Not.Null);
                var overlay = FindRectTransformByName(root, "Build Facility Focus Overlay");
                var inputHandler = overlay.GetComponent(inputHandlerType);
                Assert.That(inputHandler, Is.Not.Null);
                inputHandlerType.GetMethod("RequestClose").Invoke(inputHandler, null);
                Assert.That(focusIntents, Has.Count.EqualTo(3), "同一窗口只能派发一次关闭意图。");

                rightClickRoot = new GameObject(
                    "Build Facility Right Click Dialog Test Root",
                    typeof(RectTransform));
                var rightClickDialog = Activator.CreateInstance(dialogType, true);
                dialogType.GetMethod("ShowBuildFacilityFocus").Invoke(
                    rightClickDialog,
                    new object[] { rightClickRoot.GetComponent<RectTransform>(), model });
                var rightClickOverlay = FindRectTransformByName(
                    rightClickRoot,
                    "Build Facility Focus Overlay");
                var rightClickHandler = rightClickOverlay.GetComponent(inputHandlerType);
                Assert.That(rightClickHandler, Is.Not.Null);
                inputHandlerType.GetMethod("RequestClose").Invoke(rightClickHandler, null);
                Assert.That(focusIntents, Has.Count.EqualTo(4));
                Assert.That(
                    focusIntents[3],
                    Is.TypeOf<BuildFacilityIntent.Cancel>(),
                    "右键关闭必须派发与关闭按钮相同的取消意图。");

                confirmationRoot = new GameObject(
                    "Build Facility Confirmation Dialog Test Root",
                    typeof(RectTransform));
                var confirmationDialog = Activator.CreateInstance(dialogType, true);
                var confirmationIntents = new List<BuildFacilityIntent>();
                var confirmationModel = new BuildFacilityDraftViewModel(
                    BuildFacilityDraftPhase.Confirming,
                    new List<BuildFacilityOptionQueryResult>().AsReadOnly(),
                    null,
                    FacilityCardDatabase.Get(FacilityCardDatabase.TradeDistrict),
                    3,
                    BuildFacilityService.PaymentModeGold,
                    string.Empty,
                    new List<int>().AsReadOnly(),
                    intent => confirmationIntents.Add(intent));
                dialogType.GetMethod("ShowBuildFacilityConfirmation").Invoke(
                    confirmationDialog,
                    new object[]
                    {
                        confirmationRoot.GetComponent<RectTransform>(),
                        confirmationModel
                    });

                FindButtonByName(
                    confirmationRoot.GetComponentsInChildren<Button>(true),
                    "Back To Build Payment").onClick.Invoke();
                FindButtonByName(
                    confirmationRoot.GetComponentsInChildren<Button>(true),
                    "Confirm Build Facility").onClick.Invoke();
                FindButtonByName(
                    confirmationRoot.GetComponentsInChildren<Button>(true),
                    "Close Build Facility Confirmation Button").onClick.Invoke();

                Assert.That(confirmationIntents, Has.Count.EqualTo(3));
                Assert.That(confirmationIntents[0], Is.TypeOf<BuildFacilityIntent.Back>());
                Assert.That(confirmationIntents[1], Is.TypeOf<BuildFacilityIntent.Confirm>());
                Assert.That(confirmationIntents[2], Is.TypeOf<BuildFacilityIntent.Cancel>());
            }
            finally
            {
                if (confirmationRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(confirmationRoot);
                }

                if (rightClickRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(rightClickRoot);
                }

                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void CollapsibleEffectDialogs_UseOneSharedPanelController()
        {
            var sharedType = Type.GetType(
                "YC.Presentation.EffectDialogCollapsiblePanel, Assembly-CSharp",
                false);
            var facilityDialogType = Type.GetType(
                "YC.Presentation.FacilityEffectChoiceDialog, Assembly-CSharp",
                false);
            var eventDialogType = Type.GetType(
                "YC.Presentation.EventChoiceDialog, Assembly-CSharp",
                false);

            Assert.That(sharedType, Is.Not.Null);
            Assert.That(facilityDialogType, Is.Not.Null);
            Assert.That(eventDialogType, Is.Not.Null);
            Assert.That(
                facilityDialogType.GetField(
                    "collapsiblePanel",
                    BindingFlags.Instance | BindingFlags.NonPublic).FieldType,
                Is.EqualTo(sharedType));
            Assert.That(
                eventDialogType.GetField(
                    "eventCardCollapsiblePanel",
                    BindingFlags.Instance | BindingFlags.NonPublic).FieldType,
                Is.EqualTo(sharedType));
            Assert.That(
                facilityDialogType.GetNestedType("FacilityCardDragHandle", BindingFlags.NonPublic),
                Is.Null);
            Assert.That(
                eventDialogType.GetNestedType("EventCardDragHandle", BindingFlags.NonPublic),
                Is.Null);
        }

        [Test]
        public void EventChoiceDialog_EventCardSharedPanelMovesCollapsesAndExpandsWithoutDrift()
        {
            var root = new GameObject(
                "Event Card Shared Panel Test Root",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(GraphicRaycaster));
            var eventSystemObject = new GameObject(
                "Event Card Shared Panel Test EventSystem",
                typeof(EventSystem));
            try
            {
                var canvas = root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.scaleFactor = 2f;
                var dialogType = Type.GetType(
                    "YC.Presentation.EventChoiceDialog, Assembly-CSharp",
                    false);
                var sharedType = Type.GetType(
                    "YC.Presentation.EffectDialogCollapsiblePanel, Assembly-CSharp",
                    false);
                Assert.That(dialogType, Is.Not.Null);
                Assert.That(sharedType, Is.Not.Null);
                var dialog = Activator.CreateInstance(dialogType, true);
                var card = new EventCardDefinition
                {
                    CardId = "event-shared-panel-test",
                    Name = "共享弹窗测试",
                    Description = "测试事件卡展开、缩小和移动状态。",
                    ChoiceDescriptions = { "结算测试选项" },
                    ChoiceRewards = { new ResourceSet() }
                };

                dialogType.GetMethod("ShowEventCardOptions").Invoke(
                    dialog,
                    new object[]
                    {
                        root.GetComponent<RectTransform>(),
                        card,
                        "测试资源点",
                        null,
                        null,
                        null,
                        new Action<int>(_ => { }),
                        new Action<string, int>((_, __) => { })
                    });

                var overlay = FindRectTransformByName(root, "Event Choice Overlay");
                var panel = FindRectTransformByName(root, "Choice Panel");
                var expandedContent = FindRectTransformByName(root, "Expanded Content");
                var collapsedSummary = FindRectTransformByName(root, "Collapsed Summary");
                var collapseButton = FindButtonByName(
                    root.GetComponentsInChildren<Button>(true),
                    "Collapse Card");
                Assert.That(overlay, Is.Not.Null);
                Assert.That(panel, Is.Not.Null);
                Assert.That(panel.GetComponent(sharedType), Is.Not.Null);
                var expandedSize = panel.sizeDelta;
                var expandedPosition = panel.anchoredPosition;

                dialogType.GetMethod("CollapseForMapInteraction").Invoke(dialog, null);

                Assert.That(panel.sizeDelta.y, Is.EqualTo(58f));
                Assert.That(expandedContent.gameObject.activeSelf, Is.False);
                Assert.That(collapsedSummary.gameObject.activeSelf, Is.True);
                Assert.That(overlay.GetComponent<Image>().color.a, Is.EqualTo(0f));
                Assert.That(overlay.GetComponent<Image>().raycastTarget, Is.False);
                Assert.That(GetButtonLabel(collapseButton), Is.EqualTo("展开卡片"));
                var collapsedPosition = panel.anchoredPosition;

                dialogType.GetMethod("CollapseForMapInteraction").Invoke(dialog, null);
                Assert.That(panel.anchoredPosition, Is.EqualTo(collapsedPosition), "重复缩小不应造成位置漂移。");

                var pointer = new PointerEventData(eventSystemObject.GetComponent<EventSystem>())
                {
                    button = PointerEventData.InputButton.Left,
                    delta = new Vector2(40f, 20f)
                };
                Assert.That(
                    ExecuteEvents.Execute(panel.gameObject, pointer, ExecuteEvents.beginDragHandler),
                    Is.True);
                Assert.That(
                    ExecuteEvents.Execute(panel.gameObject, pointer, ExecuteEvents.dragHandler),
                    Is.True);
                Assert.That(
                    panel.anchoredPosition,
                    Is.EqualTo(collapsedPosition + new Vector2(20f, 10f)));

                collapseButton.onClick.Invoke();

                Assert.That(panel.sizeDelta, Is.EqualTo(expandedSize));
                Assert.That(panel.anchoredPosition, Is.EqualTo(expandedPosition + new Vector2(20f, 10f)));
                Assert.That(expandedContent.gameObject.activeSelf, Is.True);
                Assert.That(collapsedSummary.gameObject.activeSelf, Is.False);
                Assert.That(overlay.GetComponent<Image>().color.a, Is.EqualTo(0.5f).Within(0.001f));
                Assert.That(GetButtonLabel(collapseButton), Is.EqualTo("收起卡片"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(eventSystemObject);
                UnityEngine.Object.DestroyImmediate(root);
            }
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
                var clickedCityStyleId = string.Empty;

                type.GetEvent("CityStyleClicked").AddEventHandler(panel, new Action<string>(id => clickedCityStyleId = id));
                type.GetMethod("Initialize").Invoke(panel, new object[] { owner.transform });
                type.GetMethod("Refresh").Invoke(panel, new object[] { state, 1 });

                canvasObject = GameObject.Find("Build Info Panel Canvas");
                Assert.That(canvasObject, Is.Not.Null);

                var buttons = canvasObject.GetComponentsInChildren<Button>(true);
                Assert.That(CountButtonsByNamePrefix(buttons, "槽位 "), Is.EqualTo(12));
                Assert.That(CountButtonsByNamePrefix(buttons, "BuildSlot_"), Is.EqualTo(6));
                Assert.That(CountButtonsByNamePrefix(buttons, "城市样式 "), Is.EqualTo(CityStyleDatabase.PresentationSupplyIds.Count));

                Assert.That(FindRectTransformByName(canvasObject, "Build Sidebar Panel"), Is.Not.Null);
                var boardImage = FindRectTransformByName(canvasObject, "城市面板底图");
                Assert.That(boardImage, Is.Not.Null);
                Assert.That(boardImage.rect.height / boardImage.rect.width, Is.EqualTo(3801f / 2059f).Within(0.01f));
                Assert.That(HasTextContaining(canvasObject, "空位 "), Is.False);
                Assert.That(GetButtonLabel(FindButtonByName(buttons, "槽位 4")), Is.Empty);
                var coreTowerSlot = FindButtonByName(buttons, "槽位 8");
                var emptySlot = FindButtonByName(buttons, "槽位 12");
                var cardPointerType = Type.GetType("YC.Presentation.CardPointerInteraction, Assembly-CSharp", false);
                Assert.That(cardPointerType, Is.Not.Null);
                Assert.That(coreTowerSlot.enabled, Is.True);
                Assert.That(coreTowerSlot.GetComponent(cardPointerType), Is.Not.Null);
                Assert.That(emptySlot.enabled, Is.False);
                Assert.That(emptySlot.GetComponent(cardPointerType), Is.Null);
                Assert.That(emptySlot.GetComponent<Image>().raycastTarget, Is.True);
                Assert.That(GetButtonLabel(coreTowerSlot), Is.Empty);
                Assert.That(HasChildRectTransform(coreTowerSlot.gameObject, "设施卡图"), Is.True);
                AssertCityBoardSlotIsCenteredOnBoard(FindButtonByName(buttons, "槽位 2"), boardImage, 0.502f, 0.152f);
                AssertCityBoardSlotIsCenteredOnBoard(coreTowerSlot, boardImage, 0.502f, 0.615f);
                AssertCityBoardSlotIsCenteredOnBoard(FindButtonByName(buttons, "槽位 11"), boardImage, 0.502f, 0.846f);
                AssertCityBoardSlotMatchesFacilityCardRatio(coreTowerSlot, boardImage);
                AssertFacilityCardImageIsCenteredInSlot(coreTowerSlot);
                AssertExternalCardRect(FindButtonByName(buttons, "BuildSlot_1"), new Vector2(99f, 141f), new Vector2(6f, -6f));
                AssertExternalCardRect(FindButtonByName(buttons, "BuildSlot_6"), new Vector2(99f, 141f), new Vector2(254f, -169f));
                AssertExternalCardRect(FindButtonByName(buttons, "城市样式 1"), new Vector2(143f, 91f), new Vector2(20f, -6f));
                AssertExternalCardRect(FindButtonByName(buttons, "城市样式 6"), new Vector2(143f, 91f), new Vector2(203f, -228f));
                Assert.That(CountRectTransformsByNamePrefix(canvasObject, "样式影响力 "), Is.EqualTo(2));
                var blueMarkerColor = FindRectTransformByName(
                    canvasObject,
                    "样式影响力 玩家1 标记1").GetComponent<Image>().color;
                var redMarkerColor = FindRectTransformByName(
                    canvasObject,
                    "样式影响力 玩家2 标记1").GetComponent<Image>().color;
                Assert.That(blueMarkerColor.b, Is.GreaterThan(blueMarkerColor.r));
                Assert.That(redMarkerColor.r, Is.GreaterThan(redMarkerColor.b));

                Assert.That(HasText(canvasObject, "剩余牌堆：1"), Is.False);
                Assert.That(HasTextContaining(canvasObject, "玩家一：源石工业中枢"), Is.False);
                Assert.That(HasTextContaining(canvasObject, "玩家二：暂无宣告"), Is.False);
                Assert.That(HasTextContaining(canvasObject, "当前选择"), Is.False);
                Assert.That(AllTextRenderersAreMaskable(canvasObject), Is.True);

                var buildPanelScroll = canvasObject.GetComponentInChildren<ScrollRect>(true);
                Assert.That(buildPanelScroll, Is.Null);

                type.GetMethod("SetBuildInteraction").Invoke(panel, new object[]
                {
                    true,
                    new[] { FacilityCardDatabase.TradeDistrict },
                    new[] { 11 },
                    FacilityCardDatabase.TradeDistrict
                });
                type.GetMethod("SetPendingBuildGhost").Invoke(panel, new object[]
                {
                    true,
                    FacilityCardDatabase.TradeDistrict,
                    11,
                    new Action(() => { }),
                    new Action<int>(_ => { })
                });
                Assert.That(FindRectTransformByName(canvasObject, "本地建设虚影"), Is.Not.Null);
                Assert.That(
                    Array.Exists(
                        canvasObject.GetComponentsInChildren<Button>(true),
                        button => button.name == "取消建设"),
                    Is.False,
                    "建设虚影不再提供取消建设按钮，应直接拖动其他建设牌替换草稿。");
                Assert.That(FindButtonByName(buttons, "槽位 12").GetComponent<Image>().color.a, Is.GreaterThan(0f));
                Assert.That(
                    FindButtonByName(buttons, "BuildSlot_2").GetComponentInChildren<RawImage>(true).color.a,
                    Is.EqualTo(1f).Within(0.001f),
                    "建设模式中不可建设的公共牌仍应保持不透明，并允许单击预览。");
                type.GetMethod("SetBuildAvailabilityMessage").Invoke(panel, new object[]
                {
                    "本行动轮行动次数已用尽。"
                });
                var availabilityMessage = FindRectTransformByName(canvasObject, "Build Availability Message");
                Assert.That(availabilityMessage, Is.Not.Null);
                Assert.That(availabilityMessage.GetComponent<Text>().text, Is.EqualTo("本行动轮行动次数已用尽。"));
                Assert.That(availabilityMessage.GetComponent<Text>().color.r, Is.GreaterThan(0.9f));
                type.GetMethod("SetPendingBuildGhost").Invoke(panel, new object[]
                {
                    false, string.Empty, -1, null, null
                });
                type.GetMethod("SetBuildInteraction").Invoke(panel, new object[]
                {
                    false, null, null, string.Empty
                });

                InvokeButtonByName(buttons, "槽位 8");
                AssertCardImageViewerIsOpen();
                CloseCardImageViewer();
                DoubleClickButtonByName(buttons, "槽位 8");
                AssertCardImageViewerIsClosed();
                InvokeButtonByName(buttons, "槽位 12");
                InvokeButtonByName(buttons, "BuildSlot_1");
                AssertCardImageViewerIsOpen();
                CloseCardImageViewer();
                DoubleClickButtonByName(buttons, "BuildSlot_1");
                AssertCardImageViewerIsClosed();
                InvokeButtonByName(buttons, "城市样式 1");
                AssertCardImageViewerIsClosed();
                DoubleClickButtonByName(buttons, "城市样式 1");
                AssertCardImageViewerIsClosed();
                AssertExternalCardNotSelected(FindButtonByName(buttons, "BuildSlot_1"));
                AssertExternalCardNotSelected(FindButtonByName(buttons, "BuildSlot_2"));
                AssertExternalCardNotSelected(FindButtonByName(buttons, "城市样式 1"));
                AssertExternalCardNotSelected(FindButtonByName(buttons, "城市样式 2"));

                Assert.That(clickedCityStyleId, Is.EqualTo(CityStyleDatabase.MilitaryIndustrialArea));

                InvokeButtonByName(buttons, "BuildSlot_1");
                AssertExternalCardNotSelected(FindButtonByName(buttons, "BuildSlot_1"));
                AssertCardImageViewerIsOpen();
                CloseCardImageViewer();

                InvokeButtonByName(buttons, "城市样式 1");
                AssertExternalCardNotSelected(FindButtonByName(buttons, "城市样式 1"));
                Assert.That(clickedCityStyleId, Is.EqualTo(CityStyleDatabase.MilitaryIndustrialArea));

                var selectedFacilityForEffect = string.Empty;
                var cancelledFacilityEffectSelections = 0;
                var beginFacilityEffectSelection = type.GetMethod("BeginFacilityEffectSelection");
                Assert.That(beginFacilityEffectSelection, Is.Not.Null);
                Assert.That((bool)beginFacilityEffectSelection.Invoke(panel, new object[]
                {
                    new[] { FacilityCardDatabase.TradeDistrict, FacilityCardDatabase.EquipmentWarehouse },
                    new Action<string>(id => selectedFacilityForEffect = id),
                    new Action(() => cancelledFacilityEffectSelections += 1)
                }), Is.True);
                Assert.That((bool)type.GetProperty("IsFacilityEffectSelectionActive").GetValue(panel, null), Is.True);
                AssertExternalCardSelected(FindButtonByName(buttons, "BuildSlot_1"));
                AssertExternalCardSelected(FindButtonByName(buttons, "BuildSlot_2"));

                InvokeButtonByName(buttons, "BuildSlot_2");
                Assert.That(selectedFacilityForEffect, Is.EqualTo(FacilityCardDatabase.EquipmentWarehouse));
                Assert.That((bool)type.GetProperty("IsFacilityEffectSelectionActive").GetValue(panel, null), Is.False);
                AssertCardImageViewerIsClosed();

                Assert.That((bool)beginFacilityEffectSelection.Invoke(panel, new object[]
                {
                    new[] { FacilityCardDatabase.TradeDistrict },
                    new Action<string>(id => selectedFacilityForEffect = id),
                    new Action(() => cancelledFacilityEffectSelections += 1)
                }), Is.True);
                Assert.That((bool)type.GetMethod("TryCancelFacilityEffectSelection").Invoke(panel, null), Is.True);
                Assert.That(cancelledFacilityEffectSelections, Is.EqualTo(1));
                Assert.That((bool)type.GetProperty("IsFacilityEffectSelectionActive").GetValue(panel, null), Is.False);
                InvokeButtonByName(buttons, "BuildSlot_1");
                AssertCardImageViewerIsOpen();
                CloseCardImageViewer();

                var firstBuildSlot = FindButtonByName(buttons, "BuildSlot_1");
                state.Decks.FacilitySupply.RemoveAt(state.Decks.FacilitySupply.Count - 1);
                type.GetMethod("Refresh").Invoke(panel, new object[] { state, 1 });
                var refreshedButtons = canvasObject.GetComponentsInChildren<Button>(true);
                Assert.That(CountButtonsByNamePrefix(refreshedButtons, "BuildSlot_"), Is.EqualTo(6));
                Assert.That(FindButtonByName(refreshedButtons, "BuildSlot_1"), Is.SameAs(firstBuildSlot));
                Assert.That(FindButtonByName(refreshedButtons, "BuildSlot_6").interactable, Is.False);
                Assert.That(GetButtonLabel(FindButtonByName(refreshedButtons, "BuildSlot_6")), Is.EqualTo("空卡位"));
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
        public void CityStylePreview_OpensInSelectionTogglesClicksPreservesSelectionAcrossCardsAndConfirmsExactSlots()
        {
            var host = new GameObject("City Style Preview Test Host", typeof(RectTransform));
            GameObject previewCanvas = null;
            try
            {
                var state = new GameState
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
                            Color = PlayerColor.Blue,
                            DeclaredCityStyleIds = { CityStyleDatabase.SourceStoneIndustrialHub },
                            DeclaredCityStyles =
                            {
                                new CityStyleDeclarationState
                                {
                                    CityStyleId = CityStyleDatabase.SourceStoneIndustrialHub,
                                    UsedCityBoardSlotIndexes = { 3 }
                                }
                            }
                        },
                        new PlayerState
                        {
                            PlayerId = 2,
                            Color = PlayerColor.Red,
                            DeclaredCityStyleIds = { CityStyleDatabase.MilitaryIndustrialArea },
                            DeclaredCityStyles =
                            {
                                new CityStyleDeclarationState
                                {
                                    InfluenceMarkerId = "military-red-1",
                                    CityStyleId = CityStyleDatabase.MilitaryIndustrialArea,
                                    MarkerArea = CityStyleMarkerAreas.Declared
                                }
                            }
                        }
                    },
                    Decks =
                    {
                        CityStyleSupply =
                        {
                            CityStyleDatabase.MilitaryIndustrialArea,
                            CityStyleDatabase.MaterialRelayStation
                        }
                    }
                };
                AddFacility(state, FacilityCardDatabase.SourceStoneRefinery, 0);
                AddFacility(state, FacilityCardDatabase.EquipmentWarehouse, 1);
                AddFacility(state, FacilityCardDatabase.UrbanizedArea, 3);

                var controller = new CityStyleSelectionController();
                var options = controller.BuildOptions(state, 1);
                options[1].CanDeclare = false;
                options[1].Reason = "测试用不可宣告样式卡";
                var confirmedStyleId = string.Empty;
                IReadOnlyList<int> confirmedSlots = null;
                var cancelCount = 0;
                var allowConfirmation = false;
                var model = new CityStyleOptionsViewModel(
                    options.AsReadOnly(),
                    new List<CityBoardSlotViewModel>
                    {
                        new CityBoardSlotViewModel(0, FacilityCardDatabase.SourceStoneRefinery, false),
                        new CityBoardSlotViewModel(1, FacilityCardDatabase.EquipmentWarehouse, false),
                        new CityBoardSlotViewModel(3, FacilityCardDatabase.UrbanizedArea, true)
                    }.AsReadOnly(),
                    new List<CityStyleMarkerViewModel>
                    {
                        new CityStyleMarkerViewModel(
                            CityStyleDatabase.MilitaryIndustrialArea,
                            2,
                            PlayerColor.Red,
                            CityStyleMarkerAreas.Declared)
                    }.AsReadOnly(),
                    CityStyleDatabase.MilitaryIndustrialArea,
                    (cityStyleId, slots) => controller.ValidateSelection(state, 1, cityStyleId, slots),
                    (cityStyleId, slots) =>
                    {
                        if (!allowConfirmation)
                        {
                            return false;
                        }

                        confirmedStyleId = cityStyleId;
                        confirmedSlots = new List<int>(slots).AsReadOnly();
                        return true;
                    },
                    null,
                    () => cancelCount += 1);

                var dialogType = Type.GetType(
                    "YC.Presentation.CityStyleDeclarationPreviewDialog, Assembly-CSharp",
                    false);
                Assert.That(dialogType, Is.Not.Null);
                var dialog = Activator.CreateInstance(dialogType, true);
                dialogType.GetMethod("Show").Invoke(
                    dialog,
                    new object[] { host.GetComponent<RectTransform>(), model });

                previewCanvas = GameObject.Find("City Style Declaration Preview Canvas");
                Assert.That(previewCanvas, Is.Not.Null);
                Assert.That(previewCanvas.GetComponent<Canvas>().sortingOrder, Is.EqualTo(130));
                Assert.That(
                    FindRectTransformByName(previewCanvas, "City Style Declaration Preview Panel").sizeDelta,
                    Is.EqualTo(new Vector2(1520f, 900f)));
                Assert.That(FindRectTransformByName(previewCanvas, "City Style Preview Card"), Is.Not.Null);
                Assert.That(FindRectTransformByName(previewCanvas, "City Style Preview Board"), Is.Not.Null);
                Assert.That(FindRectTransformByName(previewCanvas, "City Style Preview Metadata"), Is.Null);
                Assert.That(FindRectTransformByName(previewCanvas, "City Style Preview Counter"), Is.Null);
                Assert.That(
                    GetProperty(dialog, "CurrentCityStyleId"),
                    Is.EqualTo(CityStyleDatabase.MilitaryIndustrialArea));
                var previewMarker = FindRectTransformByName(
                    previewCanvas,
                    "样式预览影响力 玩家2 标记1");
                Assert.That(previewMarker, Is.Not.Null);
                Assert.That(previewMarker.GetComponent<Image>().color.r, Is.GreaterThan(
                    previewMarker.GetComponent<Image>().color.b));

                var buttons = previewCanvas.GetComponentsInChildren<Button>(true);
                var cardFrame = FindRectTransformByName(previewCanvas, "City Style Preview Card Frame");
                var previous = FindButtonByName(buttons, "Previous City Style");
                var next = FindButtonByName(buttons, "Next City Style");
                var close = FindButtonByName(buttons, "Close City Style Declaration Preview Button");
                Assert.That(previous.GetComponentInChildren<Text>().text, Is.EqualTo("<"));
                Assert.That(next.GetComponentInChildren<Text>().text, Is.EqualTo(">"));
                Assert.That(previous.GetComponent<RectTransform>().anchoredPosition.y,
                    Is.EqualTo(cardFrame.anchoredPosition.y));
                Assert.That(next.GetComponent<RectTransform>().anchoredPosition.y,
                    Is.EqualTo(cardFrame.anchoredPosition.y));
                Assert.That(previous.GetComponent<RectTransform>().anchoredPosition.x, Is.LessThan(0f));
                Assert.That(next.GetComponent<RectTransform>().anchoredPosition.x, Is.GreaterThan(0f));
                Assert.That(previous.interactable, Is.False);
                Assert.That(next.interactable, Is.True);
                var boardTitle = FindRectTransformByName(
                    previewCanvas,
                    "City Style Preview Board Title").GetComponent<Text>();
                var boardOutline = FindRectTransformByName(
                    previewCanvas,
                    "City Style Preview Board").GetComponent<Outline>();
                var selectableTitleColor = boardTitle.color;
                var selectableBoardOutlineColor = boardOutline.effectColor;
                Assert.That(boardTitle.text, Is.EqualTo("建设面板（单击/拖动选择）"));
                Assert.That(boardOutline.effectDistance, Is.EqualTo(new Vector2(5f, -5f)));

                next.onClick.Invoke();
                Assert.That((bool)GetProperty(dialog, "IsSelecting"), Is.False);
                Assert.That(boardTitle.text, Is.EqualTo("建设面板（单击/拖动选择）"));
                Assert.That(boardTitle.color, Is.EqualTo(selectableTitleColor));
                Assert.That(boardOutline.effectColor, Is.EqualTo(selectableBoardOutlineColor));
                Assert.That(boardOutline.effectDistance, Is.EqualTo(new Vector2(5f, -5f)));
                Assert.That(
                    FindButtonByName(buttons, "宣告槽位 1").GetComponent<Outline>().effectDistance,
                    Is.EqualTo(new Vector2(2f, -2f)),
                    "不可宣告的样式卡也应沿用统一的建设面板视觉。 ");
                previous.onClick.Invoke();
                Assert.That((bool)GetProperty(dialog, "IsSelecting"), Is.True);
                var closeLabel = close.GetComponentInChildren<Text>();
                Assert.That(closeLabel.text, Is.EqualTo("×"));
                Assert.That(closeLabel.resizeTextForBestFit, Is.True);
                Assert.That(closeLabel.GetComponent<Outline>(), Is.Not.Null);
                Assert.That(close.GetComponent<RectTransform>().anchorMin, Is.EqualTo(Vector2.one));
                var uguiUtilityType = Type.GetType("YC.Presentation.UguiUtility, Assembly-CSharp", false);
                Assert.That(uguiUtilityType, Is.Not.Null);
                Assert.That(uguiUtilityType.GetMethod("CreateViewerCloseButton"), Is.Not.Null);
                var inputHandlerType = Type.GetType(
                    "YC.Presentation.CityStyleDeclarationPreviewInputHandler, Assembly-CSharp",
                    false);
                Assert.That(inputHandlerType, Is.Not.Null);
                Assert.That(previewCanvas.GetComponentInChildren(inputHandlerType, true), Is.Not.Null);
                var previewBoard = FindRectTransformByName(previewCanvas, "City Style Preview Board");
                Assert.That(previewBoard.sizeDelta, Is.EqualTo(new Vector2(397f, 733f)));
                Assert.That(
                    previewBoard.rect.width / previewBoard.rect.height,
                    Is.EqualTo(2059f / 3801f).Within(0.001f));
                AssertCityBoardSlotIsCenteredOnBoard(
                    FindButtonByName(buttons, "宣告槽位 2"),
                    previewBoard,
                    0.502f,
                    0.152f);
                AssertCityBoardSlotIsCenteredOnBoard(
                    FindButtonByName(buttons, "宣告槽位 11"),
                    previewBoard,
                    0.502f,
                    0.846f);
                AssertCityBoardSlotMatchesFacilityCardRatio(
                    FindButtonByName(buttons, "宣告槽位 8"),
                    previewBoard);

                AssertCityBoardSlotRotation(FindButtonByName(buttons, "宣告槽位 4"), 180f);
                var usedPreviewSlot = FindButtonByName(buttons, "宣告槽位 4");
                Assert.That(usedPreviewSlot.GetComponent<Outline>().effectColor.a, Is.Zero);
                Assert.That(usedPreviewSlot.GetComponent<Outline>().effectDistance, Is.EqualTo(Vector2.zero));
                Assert.That(CountTexts(previewCanvas, "已使用"), Is.EqualTo(1));
                var confirm = FindButtonByName(buttons, "Confirm City Style Declaration");
                Assert.That(FindRectTransformByName(previewCanvas, "Begin City Style Declaration"), Is.Null);
                Assert.That(confirm.GetComponent<RectTransform>().anchoredPosition.x, Is.Zero.Within(0.01f));
                Assert.That(confirm.GetComponent<RectTransform>().anchoredPosition.y, Is.EqualTo(-310f));
                var matchStatus = FindRectTransformByName(
                    previewCanvas,
                    "City Style Match Status").GetComponent<Text>();
                Assert.That(matchStatus.raycastTarget, Is.False);
                Assert.That(confirm.interactable, Is.False);
                Assert.That((bool)GetProperty(dialog, "IsSelecting"), Is.True);

                var firstSlot = FindButtonByName(buttons, "宣告槽位 1");
                var secondSlot = FindButtonByName(buttons, "宣告槽位 2");
                ExecutePointerClick(firstSlot.gameObject, PointerEventData.InputButton.Left);
                Assert.That(
                    (IReadOnlyList<int>)GetProperty(dialog, "SelectedSlotIndexes"),
                    Is.EqualTo(new[] { 0 }));
                Assert.That(confirm.interactable, Is.False);
                ExecutePointerClick(firstSlot.gameObject, PointerEventData.InputButton.Left);
                Assert.That(
                    (IReadOnlyList<int>)GetProperty(dialog, "SelectedSlotIndexes"),
                    Is.Empty,
                    "再次单击已选建设卡应取消选中。 ");
                ExecutePointerClick(secondSlot.gameObject, PointerEventData.InputButton.Left);
                ExecutePointerClick(secondSlot.gameObject, PointerEventData.InputButton.Left);
                Assert.That((IReadOnlyList<int>)GetProperty(dialog, "SelectedSlotIndexes"), Is.Empty);

                ExecutePointerDown(firstSlot.gameObject, firstSlot.transform.position);
                ExecutePointerDrag(firstSlot.gameObject, secondSlot.transform.position);
                ExecutePointerDrag(firstSlot.gameObject, firstSlot.transform.position);
                ExecutePointerUp(firstSlot.gameObject, firstSlot.transform.position);
                ExecutePointerClickOnly(
                    firstSlot.gameObject,
                    PointerEventData.InputButton.Left,
                    true);
                Assert.That(
                    (IReadOnlyList<int>)GetProperty(dialog, "SelectedSlotIndexes"),
                    Is.EqualTo(new[] { 0, 1 }),
                    "槽位发起的拖动结束后即使收到点击事件，也不应取消刚拖选的起点。 ");

                dialogType.GetMethod("ClearCurrentSelection", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(dialog, null);
                Assert.That((IReadOnlyList<int>)GetProperty(dialog, "SelectedSlotIndexes"), Is.Empty);

                var board = FindRectTransformByName(previewCanvas, "City Style Preview Board");
                var firstSlotLocalPosition = board.InverseTransformPoint(firstSlot.transform.position);
                var boardGestureStart = board.TransformPoint(new Vector3(
                    board.rect.xMin + 2f,
                    firstSlotLocalPosition.y,
                    0f));
                ExecutePointerDown(board.gameObject, boardGestureStart);
                ExecutePointerDrag(board.gameObject, secondSlot.transform.position);
                ExecutePointerDrag(board.gameObject, firstSlot.transform.position);
                ExecutePointerUp(board.gameObject, secondSlot.transform.position);
                Assert.That(
                    (IReadOnlyList<int>)GetProperty(dialog, "SelectedSlotIndexes"),
                    Is.EqualTo(new[] { 0, 1 }),
                    "拖动重复经过建设卡时不应产生重复项或取消已有选择。 ");
                Assert.That(confirm.interactable, Is.True);
                Assert.That((bool)GetProperty(dialog, "CanConfirm"), Is.True);
                Assert.That(matchStatus.text, Does.StartWith("满足宣告条件。"));
                Assert.That(matchStatus.text, Does.Contain("已选 2 / 2 个设施色块。"));
                Assert.That(matchStatus.text, Does.Not.Contain("旋转"));
                Assert.That(matchStatus.text, Does.Not.Contain("翻转"));

                next.onClick.Invoke();
                Assert.That(
                    GetProperty(dialog, "CurrentCityStyleId"),
                    Is.EqualTo(CityStyleDatabase.MaterialRelayStation));
                Assert.That(
                    (IReadOnlyList<int>)GetProperty(dialog, "SelectedSlotIndexes"),
                    Is.EqualTo(new[] { 0, 1 }),
                    "切换样式卡时应保留已选建设卡。 ");
                Assert.That(previous.interactable, Is.True);
                Assert.That(next.interactable, Is.False);
                previous.onClick.Invoke();
                Assert.That(
                    GetProperty(dialog, "CurrentCityStyleId"),
                    Is.EqualTo(CityStyleDatabase.MilitaryIndustrialArea));
                Assert.That(
                    (IReadOnlyList<int>)GetProperty(dialog, "SelectedSlotIndexes"),
                    Is.EqualTo(new[] { 0, 1 }));
                Assert.That(confirm.interactable, Is.True);

                dialogType.GetMethod("ClearCurrentSelection", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(dialog, null);
                Assert.That((bool)GetProperty(dialog, "IsSelecting"), Is.True);
                Assert.That((IReadOnlyList<int>)GetProperty(dialog, "SelectedSlotIndexes"), Is.Empty);
                Assert.That(GameObject.Find("City Style Declaration Preview Canvas"), Is.Not.Null);

                ExecutePointerClick(firstSlot.gameObject, PointerEventData.InputButton.Left);
                ExecutePointerClick(secondSlot.gameObject, PointerEventData.InputButton.Left);
                Assert.That(confirm.interactable, Is.True);

                var inputHandler = previewCanvas.GetComponentInChildren(inputHandlerType, true);
                inputHandlerType.GetMethod(
                        "HandleRightClick",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(inputHandler, new object[] { firstSlot.gameObject });
                Assert.That(
                    (IReadOnlyList<int>)GetProperty(dialog, "SelectedSlotIndexes"),
                    Is.Empty,
                    "建设面板格子上的右键应取消当前选区。");
                Assert.That(
                    GameObject.Find("City Style Declaration Preview Canvas"),
                    Is.Not.Null,
                    "建设面板格子上的右键不应关闭样式卡预览。");
                ExecutePointerClick(firstSlot.gameObject, PointerEventData.InputButton.Left);
                ExecutePointerClick(secondSlot.gameObject, PointerEventData.InputButton.Left);

                confirm.onClick.Invoke();
                Assert.That(GameObject.Find("City Style Declaration Preview Canvas"), Is.Not.Null);
                Assert.That((bool)GetProperty(dialog, "IsSelecting"), Is.True);
                Assert.That((IReadOnlyList<int>)GetProperty(dialog, "SelectedSlotIndexes"), Is.EqualTo(new[] { 0, 1 }));
                Assert.That(confirmedStyleId, Is.Empty);

                allowConfirmation = true;
                confirm.onClick.Invoke();
                Assert.That(confirmedStyleId, Is.EqualTo(CityStyleDatabase.MilitaryIndustrialArea));
                Assert.That(confirmedSlots, Is.EqualTo(new[] { 0, 1 }));
                Assert.That(cancelCount, Is.Zero);
                Assert.That(GameObject.Find("City Style Declaration Preview Canvas"), Is.Null);

                dialogType.GetMethod("Show").Invoke(
                    dialog,
                    new object[] { host.GetComponent<RectTransform>(), model });
                previewCanvas = GameObject.Find("City Style Declaration Preview Canvas");
                var overlay = FindRectTransformByName(
                    previewCanvas,
                    "City Style Declaration Preview Overlay");
                inputHandler = previewCanvas.GetComponentInChildren(inputHandlerType, true);
                inputHandlerType.GetMethod(
                        "HandleRightClick",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(inputHandler, new object[] { overlay.gameObject });
                Assert.That(cancelCount, Is.EqualTo(1));
                Assert.That(GameObject.Find("City Style Declaration Preview Canvas"), Is.Null);
                previewCanvas = null;
            }
            finally
            {
                if (previewCanvas != null)
                {
                    UnityEngine.Object.DestroyImmediate(previewCanvas);
                }

                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void BuildInfoPanel_BuildInteractionDragsPublicCardHighlightsLegalEmptySlotsAndKeepsSharedState()
        {
            var owner = new GameObject("Build Info Panel Drag Test");
            GameObject canvasObject = null;
            try
            {
                var type = Type.GetType("YC.Presentation.BuildInfoPanel, Assembly-CSharp", false);
                Assert.That(type, Is.Not.Null);
                var panel = owner.AddComponent(type);
                var state = CreateBuildInfoPanelState();
                var originalSupply = state.Decks.FacilitySupply.ToArray();
                var originalPlacements = state.Map.Facilities.Count;
                var originalScore = state.FindPlayer(1).Score;
                var startedFacilityId = string.Empty;
                var droppedFacilityId = string.Empty;
                var droppedSlotIndex = int.MinValue;

                type.GetEvent("FacilityDragStarted").AddEventHandler(
                    panel,
                    new Action<string>(id => startedFacilityId = id));
                type.GetEvent("FacilityDropped").AddEventHandler(
                    panel,
                    new Action<string, int>((id, slotIndex) =>
                    {
                        droppedFacilityId = id;
                        droppedSlotIndex = slotIndex;
                    }));
                type.GetMethod("Initialize").Invoke(panel, new object[] { owner.transform });
                type.GetMethod("Refresh").Invoke(panel, new object[] { state, 1 });

                canvasObject = GameObject.Find("Build Info Panel Canvas");
                Assert.That(canvasObject, Is.Not.Null);
                var buttons = canvasObject.GetComponentsInChildren<Button>(true);
                var sourceButton = FindButtonByName(buttons, "BuildSlot_1");
                var nonDraggableButton = FindButtonByName(buttons, "BuildSlot_2");
                var legalEmptySlot = FindButtonByName(buttons, "槽位 12");
                var illegalEmptySlot = FindButtonByName(buttons, "槽位 11");
                var occupiedSlot = FindButtonByName(buttons, "槽位 8");

                ExecuteDrag(sourceButton, null);
                Assert.That(startedFacilityId, Is.Empty, "未激活建设交互时不得开始拖动。");
                Assert.That(droppedSlotIndex, Is.EqualTo(int.MinValue));

                type.GetMethod("SetBuildInteraction").Invoke(
                    panel,
                    new object[]
                    {
                        true,
                        new[] { FacilityCardDatabase.TradeDistrict },
                        new[] { 11, 7 },
                        FacilityCardDatabase.TradeDistrict
                    });

                AssertExternalCardSelected(sourceButton);
                Assert.That(legalEmptySlot.GetComponent<Image>().color.a, Is.GreaterThan(0f));
                Assert.That(illegalEmptySlot.GetComponent<Image>().color.a, Is.EqualTo(0f).Within(0.001f));
                Assert.That(
                    occupiedSlot.GetComponent<Outline>().effectColor,
                    Is.Not.EqualTo(legalEmptySlot.GetComponent<Outline>().effectColor),
                    "已占用槽即使出现在合法索引集合中也不应高亮。");

                AssertCardImageViewerIsClosed();
                ExecuteDragAndReleaseWithPointerClick(sourceButton, sourceButton.gameObject);
                AssertCardImageViewerIsClosed();
                InvokeButtonByName(buttons, "BuildSlot_1");
                AssertCardImageViewerIsOpen();
                CloseCardImageViewer();
                startedFacilityId = string.Empty;
                droppedFacilityId = string.Empty;
                droppedSlotIndex = int.MinValue;

                ExecuteDrag(
                    sourceButton,
                    legalEmptySlot.gameObject,
                    PointerEventData.InputButton.Right);
                Assert.That(startedFacilityId, Is.Empty, "Right mouse button must not start a facility drag.");
                Assert.That(droppedSlotIndex, Is.EqualTo(int.MinValue));

                ExecuteDrag(nonDraggableButton, legalEmptySlot.gameObject);
                Assert.That(startedFacilityId, Is.Empty, "不在可拖设施集合中的公共牌不得开始拖动。");
                Assert.That(droppedSlotIndex, Is.EqualTo(int.MinValue));

                ExecuteDrag(sourceButton, legalEmptySlot.gameObject);
                Assert.That(startedFacilityId, Is.EqualTo(FacilityCardDatabase.TradeDistrict));
                Assert.That(droppedFacilityId, Is.EqualTo(FacilityCardDatabase.TradeDistrict));
                Assert.That(droppedSlotIndex, Is.EqualTo(11));
                Assert.That(GameObject.Find("建设卡拖动虚影"), Is.Null);
                Assert.That(FindButtonByName(canvasObject.GetComponentsInChildren<Button>(true), "BuildSlot_1"), Is.SameAs(sourceButton));

                droppedSlotIndex = int.MinValue;
                ExecuteDrag(sourceButton, null);
                Assert.That(droppedSlotIndex, Is.EqualTo(-1));

                Assert.That(state.Decks.FacilitySupply, Is.EqualTo(originalSupply));
                Assert.That(state.Map.Facilities, Has.Count.EqualTo(originalPlacements));
                Assert.That(state.FindPlayer(1).Score, Is.EqualTo(originalScore));
                Assert.That(state.FindPlayer(1).ActedMainActionThisTurn, Is.False);

                type.GetMethod("SetBuildInteraction").Invoke(
                    panel,
                    new object[] { false, null, null, string.Empty });
                Assert.That(legalEmptySlot.GetComponent<Image>().color.a, Is.EqualTo(0f).Within(0.001f));
                startedFacilityId = string.Empty;
                ExecuteDrag(sourceButton, legalEmptySlot.gameObject);
                Assert.That(startedFacilityId, Is.Empty);
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
                Assert.That(CountTexts(canvasObject, "已使用"), Is.EqualTo(0));
                var usedSlot = FindButtonByName(buttons, "槽位 1");
                var unusedSlot = FindButtonByName(buttons, "槽位 4");
                Assert.That(usedSlot.GetComponent<Image>().color, Is.EqualTo(unusedSlot.GetComponent<Image>().color));
                Assert.That(
                    usedSlot.GetComponent<Outline>().effectColor,
                    Is.EqualTo(unusedSlot.GetComponent<Outline>().effectColor));
                Assert.That(
                    usedSlot.GetComponent<Outline>().effectDistance,
                    Is.EqualTo(unusedSlot.GetComponent<Outline>().effectDistance));

                InvokeButtonByName(buttons, "槽位 1");
                AssertCardImageViewerIsOpen();
                var detailTitle = GameObject.Find("Card Image Title");
                Assert.That(detailTitle, Is.Not.Null);
                Assert.That(
                    detailTitle.GetComponent<Text>().text,
                    Is.EqualTo(FacilityCardDatabase.Get(FacilityCardDatabase.SourceStoneRefinery).Name + "（已使用）"));
                CloseCardImageViewer();
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
        public void DeclareCityStyle_SettlementRefreshesPublicMarkerAndOnlyRotatesExactSelectedSlots()
        {
            var owner = new GameObject("City Style Settlement Ui Test");
            GameObject canvasObject = null;
            try
            {
                var state = new GameState
                {
                    Phase = GamePhase.ActionRound1,
                    Round = 1,
                    ActionRound = 1,
                    StartPlayerId = 1,
                    CurrentPlayerId = 1,
                    Players =
                    {
                        new PlayerState { PlayerId = 1, Color = PlayerColor.Blue },
                        new PlayerState { PlayerId = 2, Color = PlayerColor.Red }
                    },
                    Decks =
                    {
                        CityStyleSupply = { CityStyleDatabase.MilitaryIndustrialArea }
                    }
                };
                AddFacility(state, FacilityCardDatabase.SourceStoneRefinery, 0);
                AddFacility(state, FacilityCardDatabase.EquipmentWarehouse, 1);
                AddFacility(state, FacilityCardDatabase.UrbanizedArea, 3);
                var command = new GameCommand
                {
                    Kind = GameCommandKind.DeclareCityStyle,
                    PlayerId = 1,
                    TargetId = CityStyleDatabase.MilitaryIndustrialArea,
                    Parameters =
                    {
                        { DeclareCityStyleCommandHandler.UsedCityBoardSlotIndexesParameter, "0,1" }
                    }
                };

                var result = new DeclareCityStyleCommandHandler().Handle(state, command);

                Assert.That(result.Succeeded, Is.True);
                Assert.That(state.FindPlayer(1).InfluenceSupply, Is.EqualTo(29));
                var type = Type.GetType("YC.Presentation.BuildInfoPanel, Assembly-CSharp", false);
                Assert.That(type, Is.Not.Null);
                var panel = owner.AddComponent(type);
                type.GetMethod("Initialize").Invoke(panel, new object[] { owner.transform });
                type.GetMethod("Refresh").Invoke(panel, new object[] { state, 1 });

                canvasObject = GameObject.Find("Build Info Panel Canvas");
                Assert.That(canvasObject, Is.Not.Null);
                var buttons = canvasObject.GetComponentsInChildren<Button>(true);
                AssertCityBoardSlotRotation(FindButtonByName(buttons, "槽位 1"), 180f);
                AssertCityBoardSlotRotation(FindButtonByName(buttons, "槽位 2"), 180f);
                AssertCityBoardSlotRotation(FindButtonByName(buttons, "槽位 4"), 0f);
                Assert.That(CountTexts(canvasObject, "已使用"), Is.EqualTo(0));
                Assert.That(
                    FindRectTransformByName(canvasObject, "样式影响力 玩家1 标记1"),
                    Is.Not.Null);
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

        private static object GetField(object target, string fieldName)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(field, Is.Not.Null, "Missing field " + fieldName + ".");
            return field.GetValue(target);
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
                        Color = PlayerColor.Blue,
                        DeclaredCityStyleIds = { CityStyleDatabase.SourceStoneIndustrialHub },
                        DeclaredCityStyles =
                        {
                            new CityStyleDeclarationState
                            {
                                InfluenceMarkerId = "source-blue-1",
                                CityStyleId = CityStyleDatabase.SourceStoneIndustrialHub,
                                MarkerArea = CityStyleMarkerAreas.UsesTwo,
                                RemainingSpecialActionUses = 2
                            }
                        }
                    },
                    new PlayerState
                    {
                        PlayerId = 2,
                        Name = "玩家二",
                        Color = PlayerColor.Red,
                        DeclaredCityStyleIds = { CityStyleDatabase.SourceStoneIndustrialHub },
                        DeclaredCityStyles =
                        {
                            new CityStyleDeclarationState
                            {
                                InfluenceMarkerId = "source-red-1",
                                CityStyleId = CityStyleDatabase.SourceStoneIndustrialHub,
                                MarkerArea = CityStyleMarkerAreas.Declared
                            }
                        }
                    }
                }
            };

            state.Decks.FacilitySupply.Add(FacilityCardDatabase.TradeDistrict);
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.EquipmentWarehouse);
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.FederalOffice);
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.SimpleEngineeringCamp);
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.SourceStoneRefinery);
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.UrbanizedArea);
            state.Decks.FacilityDeck.Add(FacilityCardDatabase.FederalOffice);
            state.Decks.CityStyleSupply.AddRange(CityStyleDatabase.DefaultSupplyIds);
            BuildFacilityService.EnsureInitialCoreCommandTower(state, state.FindPlayer(1));
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

        private static int CountRectTransformsByNamePrefix(GameObject root, string prefix)
        {
            var count = 0;
            var rects = root.GetComponentsInChildren<RectTransform>(true);
            for (var i = 0; i < rects.Length; i++)
            {
                if (rects[i].name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    count += 1;
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
                    ExecuteEvents.Execute(
                        buttons[i].gameObject,
                        new PointerEventData(EventSystem.current) { clickCount = 1 },
                        ExecuteEvents.pointerClickHandler);
                    return;
                }
            }

            Assert.Fail("Missing button: " + name);
        }

        private static void ExecutePointerClick(GameObject target, PointerEventData.InputButton button)
        {
            Assert.That(target, Is.Not.Null);
            var pointer = new PointerEventData(EventSystem.current)
            {
                button = button,
                clickCount = 1,
                position = RectTransformUtility.WorldToScreenPoint(null, target.transform.position)
            };
            Assert.That(
                ExecuteEvents.Execute(target, pointer, ExecuteEvents.pointerDownHandler),
                Is.True,
                "Missing pointer-down handler: " + target.name);
            Assert.That(
                ExecuteEvents.Execute(target, pointer, ExecuteEvents.pointerUpHandler),
                Is.True,
                "Missing pointer-up handler: " + target.name);
            Assert.That(
                ExecutePointerClickOnly(target, button, false),
                Is.True);
        }

        private static bool ExecutePointerClickOnly(
            GameObject target,
            PointerEventData.InputButton button,
            bool dragging)
        {
            var pointer = new PointerEventData(EventSystem.current)
            {
                button = button,
                clickCount = 1,
                dragging = dragging,
                position = RectTransformUtility.WorldToScreenPoint(null, target.transform.position)
            };
            var handled = ExecuteEvents.Execute(target, pointer, ExecuteEvents.pointerClickHandler);
            Assert.That(handled, Is.True, "Missing pointer-click handler: " + target.name);
            return handled;
        }

        private static void ExecutePointerDown(GameObject target, Vector3 worldPosition)
        {
            var pointer = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                position = RectTransformUtility.WorldToScreenPoint(null, worldPosition)
            };
            Assert.That(ExecuteEvents.Execute(target, pointer, ExecuteEvents.pointerDownHandler), Is.True);
        }

        private static void ExecutePointerEnter(GameObject target, Vector3 worldPosition)
        {
            var pointer = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                position = RectTransformUtility.WorldToScreenPoint(null, worldPosition)
            };
            Assert.That(ExecuteEvents.Execute(target, pointer, ExecuteEvents.pointerEnterHandler), Is.True);
        }

        private static void ExecutePointerDrag(GameObject target, Vector3 worldPosition)
        {
            var pointer = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                position = RectTransformUtility.WorldToScreenPoint(null, worldPosition)
            };
            Assert.That(ExecuteEvents.Execute(target, pointer, ExecuteEvents.dragHandler), Is.True);
        }

        private static void ExecutePointerUp(GameObject target, Vector3 worldPosition)
        {
            var pointer = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                position = RectTransformUtility.WorldToScreenPoint(null, worldPosition)
            };
            Assert.That(ExecuteEvents.Execute(target, pointer, ExecuteEvents.pointerUpHandler), Is.True);
        }

        private static void DoubleClickButtonByName(Button[] buttons, string name)
        {
            var button = FindButtonByName(buttons, name);
            Assert.That(button, Is.Not.Null, "Missing button: " + name);
            Assert.That(
                ExecuteEvents.Execute(
                    button.gameObject,
                    new PointerEventData(EventSystem.current) { clickCount = 2 },
                    ExecuteEvents.pointerClickHandler),
                Is.True,
                "Missing double-click handler: " + name);
        }

        private static void ExecuteDrag(
            Button sourceButton,
            GameObject dropTarget,
            PointerEventData.InputButton pointerButton = PointerEventData.InputButton.Left)
        {
            Assert.That(sourceButton, Is.Not.Null);
            var pointerPosition = dropTarget == null
                ? new Vector2(-10000f, -10000f)
                : RectTransformUtility.WorldToScreenPoint(null, dropTarget.transform.position);
            var pointer = new PointerEventData(EventSystem.current)
            {
                button = pointerButton,
                position = pointerPosition,
                pointerCurrentRaycast = new RaycastResult { gameObject = dropTarget }
            };

            Assert.That(
                ExecuteEvents.Execute(sourceButton.gameObject, pointer, ExecuteEvents.beginDragHandler),
                Is.True,
                "Missing begin-drag handler: " + sourceButton.name);
            ExecuteEvents.Execute(sourceButton.gameObject, pointer, ExecuteEvents.dragHandler);

            var ghost = GameObject.Find("建设卡拖动虚影");
            if (ghost != null)
            {
                Assert.That(ghost.GetComponent<RawImage>().raycastTarget, Is.False);
                Assert.That(ghost.GetComponent<CanvasGroup>().blocksRaycasts, Is.False);
            }

            Assert.That(
                ExecuteEvents.Execute(sourceButton.gameObject, pointer, ExecuteEvents.endDragHandler),
                Is.True,
                "Missing end-drag handler: " + sourceButton.name);
        }

        private static void ExecuteDragAndReleaseWithPointerClick(Button sourceButton, GameObject dropTarget)
        {
            Assert.That(sourceButton, Is.Not.Null);
            var pointerPosition = RectTransformUtility.WorldToScreenPoint(null, dropTarget.transform.position);
            var pointer = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                clickCount = 1,
                position = pointerPosition,
                pressPosition = pointerPosition,
                pointerCurrentRaycast = new RaycastResult { gameObject = dropTarget }
            };

            ExecuteEvents.Execute(sourceButton.gameObject, pointer, ExecuteEvents.pointerDownHandler);
            Assert.That(
                ExecuteEvents.Execute(sourceButton.gameObject, pointer, ExecuteEvents.beginDragHandler),
                Is.True,
                "Missing begin-drag handler: " + sourceButton.name);
            ExecuteEvents.Execute(sourceButton.gameObject, pointer, ExecuteEvents.dragHandler);
            Assert.That(
                ExecuteEvents.Execute(sourceButton.gameObject, pointer, ExecuteEvents.endDragHandler),
                Is.True,
                "Missing end-drag handler: " + sourceButton.name);
            ExecuteEvents.Execute(sourceButton.gameObject, pointer, ExecuteEvents.pointerUpHandler);
            Assert.That(
                ExecuteEvents.Execute(sourceButton.gameObject, pointer, ExecuteEvents.pointerClickHandler),
                Is.True,
                "Missing pointer-click handler: " + sourceButton.name);
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
            var anchorCenter = (rect.anchorMin + rect.anchorMax) * 0.5f;
            Assert.That(anchorCenter.x, Is.EqualTo(normalizedX).Within(0.001f));
            Assert.That(anchorCenter.y, Is.EqualTo(1f - normalizedY).Within(0.001f));
            Assert.That(rect.offsetMin, Is.EqualTo(Vector2.zero));
            Assert.That(rect.offsetMax, Is.EqualTo(Vector2.zero));
        }

        private static void AssertCityBoardSlotMatchesFacilityCardRatio(Button button, RectTransform boardImage)
        {
            Assert.That(button, Is.Not.Null);
            Assert.That(boardImage, Is.Not.Null);
            var rect = button.GetComponent<RectTransform>();
            var normalizedSize = rect.anchorMax - rect.anchorMin;
            Assert.That(normalizedSize.x, Is.EqualTo(0.292f).Within(0.001f));
            Assert.That(normalizedSize.y, Is.EqualTo(0.224f).Within(0.001f));

            var renderedAspectRatio =
                normalizedSize.x * boardImage.rect.width /
                (normalizedSize.y * boardImage.rect.height);
            Assert.That(renderedAspectRatio, Is.EqualTo(600f / 850f).Within(0.002f));
        }

        private static void AssertFacilityCardImageIsCenteredInSlot(Button button)
        {
            Assert.That(button, Is.Not.Null);
            var imageRect = FindRectTransformByName(button.gameObject, "设施卡图");
            Assert.That(imageRect, Is.Not.Null);
            Assert.That(imageRect.anchorMin, Is.EqualTo(Vector2.zero));
            Assert.That(imageRect.anchorMax, Is.EqualTo(Vector2.one));
            Assert.That(imageRect.offsetMin, Is.EqualTo(Vector2.zero));
            Assert.That(imageRect.offsetMax, Is.EqualTo(Vector2.zero));
            Assert.That(imageRect.GetComponent<AspectRatioFitter>(), Is.Null);
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

        private static bool HasChildRectTransform(GameObject root, string name)
        {
            var rects = root.GetComponentsInChildren<RectTransform>(true);
            for (var i = 0; i < rects.Length; i++)
            {
                if (rects[i].name == name)
                {
                    return true;
                }
            }

            return false;
        }

        private static void AssertExternalCardRect(Button button, Vector2 expectedSize, Vector2 expectedPosition)
        {
            Assert.That(button, Is.Not.Null);
            var rect = button.GetComponent<RectTransform>();
            Assert.That(rect.sizeDelta.x, Is.EqualTo(expectedSize.x).Within(0.01f));
            Assert.That(rect.sizeDelta.y, Is.EqualTo(expectedSize.y).Within(0.01f));
            Assert.That(rect.anchoredPosition.x, Is.EqualTo(expectedPosition.x).Within(0.01f));
            Assert.That(rect.anchoredPosition.y, Is.EqualTo(expectedPosition.y).Within(0.01f));
        }

        private static void AssertExternalCardSelected(Button button)
        {
            AssertExternalCardOutlineDistance(button, new Vector2(3f, -3f));
        }

        private static void AssertExternalCardNotSelected(Button button)
        {
            AssertExternalCardOutlineDistance(button, new Vector2(1f, -1f));
        }

        private static void AssertExternalCardOutlineDistance(Button button, Vector2 expectedDistance)
        {
            Assert.That(button, Is.Not.Null);
            var outline = button.GetComponent<Outline>();
            Assert.That(outline, Is.Not.Null);
            Assert.That(outline.effectDistance.x, Is.EqualTo(expectedDistance.x).Within(0.01f));
            Assert.That(outline.effectDistance.y, Is.EqualTo(expectedDistance.y).Within(0.01f));
        }

        private static void AssertImagePathExists(MethodInfo pathMethod, string cardId)
        {
            var arguments = new object[] { cardId, null };
            Assert.That(pathMethod.Invoke(null, arguments), Is.True, "图片路径映射缺失：" + cardId);

            var relativePath = arguments[1] as string;
            Assert.That(relativePath, Is.Not.Null.And.Not.Empty, "图片路径为空：" + cardId);
            Assert.That(
                System.IO.File.Exists(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), relativePath)),
                Is.True,
                "图片文件不存在：" + relativePath);
        }

        private static void AssertCardImageViewerIsOpen()
        {
            var type = Type.GetType("YC.Presentation.ZoomableImageViewerController, Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null);
            var viewerObject = GameObject.Find("Card Image Viewer");
            var viewer = viewerObject == null ? null : viewerObject.GetComponent(type);
            Assert.That(viewer, Is.Not.Null);
            var isOpen = type.GetProperty("IsOpen", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(isOpen, Is.Not.Null);
            Assert.That((bool)isOpen.GetValue(viewer, null), Is.True);
        }

        private static void AssertCardImageViewerIsClosed()
        {
            var type = Type.GetType("YC.Presentation.ZoomableImageViewerController, Assembly-CSharp", false);
            var viewerObject = GameObject.Find("Card Image Viewer");
            var viewer = type == null || viewerObject == null ? null : viewerObject.GetComponent(type);
            if (viewer == null)
            {
                return;
            }

            var isOpen = type.GetProperty("IsOpen", BindingFlags.Instance | BindingFlags.Public);
            Assert.That((bool)isOpen.GetValue(viewer, null), Is.False);
        }

        private static void CloseCardImageViewer()
        {
            var type = Type.GetType("YC.Presentation.ZoomableImageViewerController, Assembly-CSharp", false);
            var viewerObject = GameObject.Find("Card Image Viewer");
            var viewer = type == null || viewerObject == null ? null : viewerObject.GetComponent(type);
            if (viewer != null)
            {
                type.GetMethod("Close", BindingFlags.Instance | BindingFlags.Public).Invoke(viewer, null);
            }
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
