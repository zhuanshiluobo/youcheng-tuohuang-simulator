using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Application.Gameplay;
using YC.Domain.Commands;
using YC.Domain.Facilities;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.State;
using YC.Presentation;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class FacilityEffectInteractionUiCoordinatorTests
    {
        private static readonly Color DisabledOptionColor = new Color(0.09f, 0.075f, 0.06f, 0.72f);
        private GameObject canvasObject;

        [SetUp]
        public void SetUpViewerPrefab()
        {
            ViewerPrefabTestUtility.RegisterZoomablePrefab();
        }

        [TearDown]
        public void TearDown()
        {
            if (canvasObject != null)
            {
                UnityEngine.Object.DestroyImmediate(canvasObject);
                canvasObject = null;
            }
        }

        [Test]
        public void Synchronize_SameSession_KeepsOverlayAndTradeInput()
        {
            var state = CreateState("facility-session-1", FacilityPendingChoiceTypes.ScenarioId);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            var firstOverlay = GetOverlay(fixture.Dialog);
            Assert.That(firstOverlay, Is.Not.Null);

            ClickButton(firstOverlay, "Increase 0");
            Assert.That(GetText(firstOverlay, "Value 0"), Is.EqualTo("1"));

            Assert.That(Synchronize(fixture.Coordinator), Is.True);

            var synchronizedOverlay = GetOverlay(fixture.Dialog);
            Assert.That(synchronizedOverlay, Is.SameAs(firstOverlay));
            Assert.That(GetText(synchronizedOverlay, "Value 0"), Is.EqualTo("1"));
        }

        [Test]
        public void Synchronize_ChangedSession_RebuildsOverlayAndResetsTradeInput()
        {
            var state = CreateState("facility-session-1", FacilityPendingChoiceTypes.ScenarioId);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            var firstOverlay = GetOverlay(fixture.Dialog);
            ClickButton(firstOverlay, "Increase 0");
            Assert.That(GetText(firstOverlay, "Value 0"), Is.EqualTo("1"));

            state.PendingCardSession.SessionId = "facility-session-2";
            Assert.That(Synchronize(fixture.Coordinator), Is.True);

            var rebuiltOverlay = GetOverlay(fixture.Dialog);
            Assert.That(rebuiltOverlay, Is.Not.Null);
            Assert.That(rebuiltOverlay, Is.Not.SameAs(firstOverlay));
            Assert.That(GetText(rebuiltOverlay, "Value 0"), Is.EqualTo("0"));
        }

        [Test]
        public void FreeCityMove_SubmissionInFlight_BlocksDuplicateUntilMatchingCommandSettles()
        {
            var state = CreateState(
                "free-city-move-in-flight",
                FacilityPendingChoiceTypes.ScenarioId,
                FacilityPendingChoiceTypes.FreeCityMove);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            Assert.That(TryHandleLocationClicked(fixture.Coordinator, "location-a"), Is.True);
            var firstCommand = fixture.SubmittedCommand;
            Assert.That(firstCommand, Is.Not.Null);

            Assert.That(TryHandleLocationClicked(fixture.Coordinator, "location-b"), Is.True);
            Assert.That(fixture.SubmittedCommand, Is.SameAs(firstCommand));
            Assert.That(fixture.LastPrompt, Does.Contain("请勿重复提交"));

            Invoke(fixture.Coordinator, "NotifyCommandSettled", "another-command");
            Assert.That(TryHandleLocationClicked(fixture.Coordinator, "location-c"), Is.True);
            Assert.That(fixture.SubmittedCommand, Is.SameAs(firstCommand));

            Invoke(fixture.Coordinator, "NotifyCommandSettled", firstCommand.CommandId);
            Assert.That(TryHandleLocationClicked(fixture.Coordinator, "location-d"), Is.True);
            Assert.That(fixture.SubmittedCommand, Is.Not.SameAs(firstCommand));
            Assert.That(fixture.SubmittedCommand.CommandId, Is.Not.EqualTo(firstCommand.CommandId));
        }

        [Test]
        public void FreeCityMove_SubmitThrows_ClearsSubmissionInFlight()
        {
            var state = CreateState(
                "free-city-move-submit-throws",
                FacilityPendingChoiceTypes.ScenarioId,
                FacilityPendingChoiceTypes.FreeCityMove);
            var shouldThrow = true;
            var fixture = CreateCoordinator(
                state,
                null,
                _ =>
                {
                    if (shouldThrow)
                    {
                        throw new InvalidOperationException("submit failed");
                    }
                });

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            var exception = Assert.Throws<TargetInvocationException>(
                () => TryHandleLocationClicked(fixture.Coordinator, "location-a"));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());

            shouldThrow = false;
            Assert.That(TryHandleLocationClicked(fixture.Coordinator, "location-b"), Is.True);
            Assert.That(
                fixture.SubmittedCommand.Parameters[
                    ResolveFacilityEffectCommandHandler.TargetLocationIdParameter],
                Is.EqualTo("location-b"));
        }

        [Test]
        public void Synchronize_OrdinaryEventSession_DoesNotTakeOverAndHidesFacilityOverlay()
        {
            var state = CreateState("facility-session", FacilityPendingChoiceTypes.ScenarioId);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            Assert.That(GetOverlay(fixture.Dialog), Is.Not.Null);

            state.PendingCardSession = CreatePending("event-session", "explore_event");

            Assert.That(Synchronize(fixture.Coordinator), Is.False);
            Assert.That(GetOverlay(fixture.Dialog), Is.Null);
        }

        [Test]
        public void Synchronize_WarehouseBranch_CanCollapseAndExpandLikeEventCard()
        {
            var state = CreateState(
                "warehouse-session",
                FacilityPendingChoiceTypes.ScenarioId,
                FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore);
            state.PendingCardSession.OptionIds.Clear();
            state.PendingCardSession.OptionIds.Add(FacilityPendingChoiceTypes.RemoveDispatchOption);
            state.PendingCardSession.OptionIds.Add(FacilityPendingChoiceTypes.ExploreOption);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            var overlay = GetOverlay(fixture.Dialog);
            var panel = FindChild(overlay, "Facility Effect Choice Panel").GetComponent<RectTransform>();
            var expandedContent = FindChild(overlay, "Facility Expanded Content");
            var summary = FindChild(overlay, "Facility Collapsed Summary");
            var triangle = FindChild(overlay, "Facility Collapse Triangle").GetComponent<Image>();

            Assert.That(panel.sizeDelta.y, Is.EqualTo(600f));
            Assert.That(expandedContent.activeSelf, Is.True);
            Assert.That(summary.activeSelf, Is.False);
            Assert.That(triangle.sprite, Is.Not.Null);
            Assert.That(triangle.sprite.name, Is.EqualTo("UI Triangle Up"));

            ClickButton(overlay, "Facility Collapse Toggle");

            Assert.That(panel.sizeDelta.y, Is.EqualTo(58f));
            Assert.That(expandedContent.activeSelf, Is.False);
            Assert.That(summary.activeSelf, Is.True);
            Assert.That(summary.GetComponent<Text>().text, Does.Contain("载具仓库"));
            Assert.That(overlay.GetComponent<Image>().color.a, Is.EqualTo(0f));
            Assert.That(triangle.sprite.name, Is.EqualTo("UI Triangle Down"));

            ClickButton(overlay, "Facility Collapse Toggle");

            Assert.That(panel.sizeDelta.y, Is.EqualTo(600f));
            Assert.That(expandedContent.activeSelf, Is.True);
            Assert.That(summary.activeSelf, Is.False);
            Assert.That(overlay.GetComponent<Image>().color.a, Is.EqualTo(0.22f).Within(0.001f));
            Assert.That(triangle.sprite.name, Is.EqualTo("UI Triangle Up"));
        }

        [Test]
        public void Synchronize_FreeCityMove_CanCollapseSelectTargetAndExpandAgain()
        {
            var state = CreateState(
                "free-city-move-session",
                FacilityPendingChoiceTypes.ScenarioId,
                FacilityPendingChoiceTypes.FreeCityMove);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            var overlay = GetOverlay(fixture.Dialog);
            var panel = FindChild(overlay, "Facility Effect Choice Panel").GetComponent<RectTransform>();
            var expandedContent = FindChild(overlay, "Facility Expanded Content");
            var summary = FindChild(overlay, "Facility Collapsed Summary");
            var expandedPosition = panel.anchoredPosition;

            Assert.That(panel.sizeDelta.y, Is.EqualTo(260f));
            Assert.That(expandedContent.activeSelf, Is.True);
            Assert.That(summary.activeSelf, Is.False);
            Assert.That(GetButtonLabel(overlay, "Facility Collapse Toggle"), Is.EqualTo("收起卡片"));

            ClickButton(overlay, "Facility Collapse Toggle");

            Assert.That(panel.sizeDelta.y, Is.EqualTo(58f));
            Assert.That(expandedContent.activeSelf, Is.False);
            Assert.That(summary.activeSelf, Is.True);
            Assert.That(summary.GetComponent<Text>().text, Does.Contain("高性能动力设施"));
            Assert.That(GetButtonLabel(overlay, "Facility Collapse Toggle"), Is.EqualTo("展开卡片"));
            Assert.That(overlay.GetComponent<Image>().color.a, Is.EqualTo(0f));
            Assert.That(overlay.GetComponent<Image>().raycastTarget, Is.False);

            Assert.That(TryHandleLocationClicked(fixture.Coordinator, "free-move-target"), Is.True);
            Assert.That(fixture.SubmittedCommand, Is.Not.Null);
            Assert.That(
                fixture.SubmittedCommand.Parameters[
                    ResolveFacilityEffectCommandHandler.TargetLocationIdParameter],
                Is.EqualTo("free-move-target"));

            ClickButton(overlay, "Facility Collapse Toggle");

            Assert.That(panel.sizeDelta.y, Is.EqualTo(260f));
            Assert.That(panel.anchoredPosition, Is.EqualTo(expandedPosition));
            Assert.That(expandedContent.activeSelf, Is.True);
            Assert.That(summary.activeSelf, Is.False);
            Assert.That(GetButtonLabel(overlay, "Facility Collapse Toggle"), Is.EqualTo("收起卡片"));
        }

        [Test]
        public void MercenaryHeadquarters_ShowsSharedOrBranchOptionsBeforeMapSelection()
        {
            string opponentSlot;
            string ownSlot;
            string emptySlot;
            var fixture = CreateMercenaryCoordinator(
                out opponentSlot,
                out ownSlot,
                out emptySlot);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            var overlay = GetOverlay(fixture.Dialog);
            Assert.That(overlay, Is.Not.Null);
            Assert.That(overlay.activeInHierarchy, Is.True);
            Assert.That(GetText(overlay, "Title"), Is.EqualTo("佣兵指挥部"));
            Assert.That(GetText(overlay, "Description"), Does.Contain("执行分支"));
            Assert.That(GetButtonLabel(overlay, "Option 0"), Is.EqualTo("替换 1 个影响力"));
            Assert.That(GetButtonLabel(overlay, "Option 1"), Is.EqualTo("放置 1 个影响力"));
            Assert.That(FindChild(overlay, "Facility Expanded Content"), Is.Not.Null);
            Assert.That(GetText(overlay, "Facility Collapsed Summary"), Does.Contain("选择分支"));
            Assert.That(fixture.Highlights, Is.Empty, "选择一级分支前不应提前高亮地图目标。");
            Assert.That(fixture.SubmittedCommand, Is.Null);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void MercenaryHeadquarters_EscapeReturnsToBranchChoiceWithoutSubmitting(int branch)
        {
            string opponentSlot, ownSlot, emptySlot;
            var fixture = CreateMercenaryCoordinator(out opponentSlot, out ownSlot, out emptySlot);
            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            ClickButton(GetOverlay(fixture.Dialog), "Option " + branch);

            Assert.That(InvokeBool(fixture.Coordinator, "TryHandleEscape"), Is.True);
            Assert.That(GetText(GetOverlay(fixture.Dialog), "Title"), Is.EqualTo("佣兵指挥部"));
            Assert.That(fixture.Highlights, Is.Empty);
            Assert.That(fixture.SubmittedCommand, Is.Null);
        }

        [Test]
        public void MercenaryHeadquarters_ReplaceBranchHighlightsOnlyOpponentAndSubmitsBranchAndTarget()
        {
            string opponentSlot;
            string ownSlot;
            string emptySlot;
            var fixture = CreateMercenaryCoordinator(
                out opponentSlot,
                out ownSlot,
                out emptySlot);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            ClickButton(GetOverlay(fixture.Dialog), "Option 0");

            var overlay = GetOverlay(fixture.Dialog);
            Assert.That(GetText(overlay, "Title"), Does.Contain("替换"));
            Assert.That(
                fixture.Highlights.ConvertAll(item => item.TargetId),
                Is.EqualTo(new[] { opponentSlot }));

            Assert.That(TryHandleInfluenceSlotClicked(fixture.Coordinator, ownSlot), Is.True);
            Assert.That(fixture.SubmittedCommand, Is.Null);
            Assert.That(fixture.LastPrompt, Does.Contain("高亮"));

            Assert.That(TryHandleInfluenceSlotClicked(fixture.Coordinator, opponentSlot), Is.True);
            Assert.That(fixture.SubmittedCommand, Is.Not.Null);
            Assert.That(fixture.SubmittedCommand.SourceId, Is.EqualTo(FacilityCardDatabase.MercenaryCommand));
            Assert.That(
                fixture.SubmittedCommand.OptionIds,
                Is.EqualTo(new[] { FacilityPendingChoiceTypes.ReplaceInfluenceOption }));
            Assert.That(
                fixture.SubmittedCommand.Parameters[
                    ResolveFacilityEffectCommandHandler.TargetInfluenceSlotIdParameter],
                Is.EqualTo(opponentSlot));
        }

        [Test]
        public void MercenaryHeadquarters_DeployBranchHighlightsOnlyLegalEmptySlotAndSubmitsBranchAndTarget()
        {
            string opponentSlot;
            string ownSlot;
            string emptySlot;
            var fixture = CreateMercenaryCoordinator(
                out opponentSlot,
                out ownSlot,
                out emptySlot);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            ClickButton(GetOverlay(fixture.Dialog), "Option 1");

            var overlay = GetOverlay(fixture.Dialog);
            Assert.That(GetText(overlay, "Title"), Does.Contain("放置"));
            Assert.That(
                fixture.Highlights.ConvertAll(item => item.TargetId),
                Is.EqualTo(new[] { emptySlot }));

            Assert.That(TryHandleInfluenceSlotClicked(fixture.Coordinator, opponentSlot), Is.True);
            Assert.That(fixture.SubmittedCommand, Is.Null);
            Assert.That(fixture.LastPrompt, Does.Contain("空槽位"));

            Assert.That(TryHandleInfluenceSlotClicked(fixture.Coordinator, emptySlot), Is.True);
            Assert.That(fixture.SubmittedCommand, Is.Not.Null);
            Assert.That(fixture.SubmittedCommand.SourceId, Is.EqualTo(FacilityCardDatabase.MercenaryCommand));
            Assert.That(
                fixture.SubmittedCommand.OptionIds,
                Is.EqualTo(new[] { FacilityPendingChoiceTypes.DeployInfluenceOption }));
            Assert.That(
                fixture.SubmittedCommand.Parameters[
                    ResolveFacilityEffectCommandHandler.TargetInfluenceSlotIdParameter],
                Is.EqualTo(emptySlot));
        }

        [Test]
        public void EscortDispatchCenter_HighlightsOnlyEmptyLegalSlotsAndSubmitsAfterSecondClick()
        {
            var state = CreateState(
                "escort-dispatch-session",
                FacilityPendingChoiceTypes.ScenarioId,
                FacilityPendingChoiceTypes.DeployTwoInfluences);
            var map = new GameMapDefinition { MapId = "escort-dispatch-ui-test" };
            map.Routes.Add(new MapRouteDefinition
            {
                RouteId = "escort-route",
                InfluenceSlotCount = 3
            });
            var occupiedSlot = InfluenceService.GetRouteSlotId("escort-route", 0);
            var firstSlot = InfluenceService.GetRouteSlotId("escort-route", 1);
            var secondSlot = InfluenceService.GetRouteSlotId("escort-route", 2);
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = occupiedSlot,
                RouteId = "escort-route"
            });
            var fixture = CreateCoordinator(state, map);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            var overlay = GetOverlay(fixture.Dialog);
            var panel = FindChild(overlay, "Facility Effect Choice Panel").GetComponent<RectTransform>();
            var dialogCanvas = overlay.GetComponent<Canvas>();
            var collapsiblePanelType = Type.GetType(
                "YC.Presentation.EffectDialogCollapsiblePanel, Assembly-CSharp",
                false);
            Assert.That(dialogCanvas, Is.Not.Null);
            Assert.That(dialogCanvas.overrideSorting, Is.True);
            Assert.That(dialogCanvas.sortingOrder, Is.EqualTo(119));
            Assert.That(overlay.GetComponent<GraphicRaycaster>(), Is.Not.Null);
            Assert.That(collapsiblePanelType, Is.Not.Null);
            var collapsiblePanel = panel.GetComponent(collapsiblePanelType);
            Assert.That(collapsiblePanel, Is.Not.Null);
            Assert.That(
                (bool)collapsiblePanelType.GetProperty("IsCollapsed").GetValue(collapsiblePanel, null),
                Is.True);
            Assert.That(panel.sizeDelta, Is.EqualTo(new Vector2(650f, 58f)));
            Assert.That(FindChild(overlay, "Facility Expanded Content").activeSelf, Is.False);
            Assert.That(FindChild(overlay, "Facility Collapsed Summary").activeSelf, Is.True);
            Assert.That(
                GetText(overlay, "Facility Collapsed Summary"),
                Does.Contain("护航调度中心").And.Contain("两个影响力"));
            Assert.That(GetButtonLabel(overlay, "Facility Collapse Toggle"), Is.EqualTo("展开卡片"));
            Assert.That(GetText(overlay, "Description"), Does.Not.Contain("已选"));
            Assert.That(FindChild(overlay, "Primary"), Is.Null);
            Assert.That(
                fixture.Highlights.ConvertAll(item => item.TargetId),
                Is.EquivalentTo(new[] { firstSlot, secondSlot }));

            Assert.That(TryHandleInfluenceSlotClicked(fixture.Coordinator, occupiedSlot), Is.True);
            Assert.That(fixture.SubmittedCommand, Is.Null);
            Assert.That(fixture.LastPrompt, Does.Contain("空槽位"));

            Assert.That(TryHandleInfluenceSlotClicked(fixture.Coordinator, firstSlot), Is.True);
            Assert.That(fixture.SubmittedCommand, Is.Null);
            overlay = GetOverlay(fixture.Dialog);
            panel = FindChild(overlay, "Facility Effect Choice Panel").GetComponent<RectTransform>();
            Assert.That(panel.sizeDelta, Is.EqualTo(new Vector2(650f, 58f)));
            collapsiblePanel = panel.GetComponent(collapsiblePanelType);
            Assert.That(
                (bool)collapsiblePanelType.GetProperty("IsCollapsed").GetValue(collapsiblePanel, null),
                Is.True);
            Assert.That(GetText(overlay, "Description"), Does.Not.Contain("已选"));
            Assert.That(
                fixture.Highlights.ConvertAll(item => item.TargetId),
                Is.EqualTo(new[] { secondSlot }));

            Assert.That(TryHandleInfluenceSlotClicked(fixture.Coordinator, secondSlot), Is.True);
            Assert.That(fixture.SubmittedCommand, Is.Not.Null);
            Assert.That(
                fixture.SubmittedCommand.Parameters[
                    ResolveFacilityEffectCommandHandler.InfluenceSlotIdsParameter],
                Is.EqualTo(firstSlot + "," + secondSlot));
        }

        [Test]
        public void WarehouseExploreStage_DelegatesToSharedExploreWorkflowAndCanReturnToBranch()
        {
            var state = CreateState(
                "warehouse-session",
                FacilityPendingChoiceTypes.ScenarioId,
                FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore);
            state.PendingCardSession.OptionIds.Clear();
            state.PendingCardSession.OptionIds.Add(FacilityPendingChoiceTypes.RemoveDispatchOption);
            state.PendingCardSession.OptionIds.Add(FacilityPendingChoiceTypes.ExploreOption);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            ClickButton(GetOverlay(fixture.Dialog), "Option 1");

            Assert.That(fixture.AdditionalExplorePending, Is.SameAs(state.PendingCardSession));
            Assert.That(fixture.AdditionalExploreOptionId,
                Is.EqualTo(FacilityPendingChoiceTypes.ExploreOption));
            Assert.That(TryHandleLocationClicked(fixture.Coordinator, "target"), Is.False,
                "探索地点应交给 ExplorationEventPresenter，而不是设施协调器直接提交。");
            Assert.That(fixture.SubmittedCommand, Is.Null);

            var exploreOverlay = GetOverlay(fixture.Dialog);
            var explorePanel = FindChild(exploreOverlay, "Facility Effect Choice Panel").GetComponent<RectTransform>();
            var exploreContent = FindChild(exploreOverlay, "Facility Expanded Content");
            Assert.That(explorePanel.sizeDelta.y, Is.EqualTo(58f));
            Assert.That(exploreContent.activeSelf, Is.False);
            Assert.That(GetText(exploreOverlay, "Facility Collapsed Summary"), Does.Contain("探索"));

            ClickButton(exploreOverlay, "Facility Collapse Toggle");
            Assert.That(explorePanel.sizeDelta.y, Is.EqualTo(260f));
            Assert.That(exploreContent.activeSelf, Is.True);

            ClickButton(exploreOverlay, "Back");
            Assert.That(fixture.CancelAdditionalExploreCount, Is.EqualTo(1));
            var branchOverlay = GetOverlay(fixture.Dialog);
            var branchPanel = FindChild(branchOverlay, "Facility Effect Choice Panel").GetComponent<RectTransform>();
            Assert.That(branchPanel.sizeDelta.y, Is.EqualTo(600f));
            Assert.That(FindChild(branchOverlay, "Facility Expanded Content").activeSelf, Is.True);
        }

        [Test]
        public void WarehouseBranch_WhenOnlyExploreIsLegal_DisablesRemoveAndStartsSharedExplore()
        {
            var state = CreateState(
                "warehouse-explore-only",
                FacilityPendingChoiceTypes.ScenarioId,
                FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore);
            state.PendingCardSession.OptionIds.Clear();
            state.PendingCardSession.OptionIds.Add(FacilityPendingChoiceTypes.ExploreOption);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            var overlay = GetOverlay(fixture.Dialog);
            var removeButton = FindChild(overlay, "Option 0").GetComponent<Button>();
            var exploreButton = FindChild(overlay, "Option 1").GetComponent<Button>();
            Assert.That(removeButton.interactable, Is.False);
            Assert.That(removeButton.GetComponent<Image>().color, Is.EqualTo(DisabledOptionColor));
            Assert.That(exploreButton.interactable, Is.True);
            Assert.That(FindChild(overlay, "Back"), Is.Null);

            ClickButton(overlay, "Option 0");
            Assert.That(fixture.AdditionalExplorePending, Is.Null);
            ClickButton(overlay, "Option 1");

            Assert.That(fixture.AdditionalExplorePending, Is.SameAs(state.PendingCardSession));
            Assert.That(fixture.AdditionalExploreOptionId,
                Is.EqualTo(FacilityPendingChoiceTypes.ExploreOption));
        }

        [Test]
        public void WarehouseBranch_WhenOnlyRemoveDispatchIsLegal_DisablesExploreWithoutClose()
        {
            var state = CreateState(
                "warehouse-remove-only",
                FacilityPendingChoiceTypes.ScenarioId,
                FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore);
            state.PendingCardSession.OptionIds.Clear();
            state.PendingCardSession.OptionIds.Add(FacilityPendingChoiceTypes.RemoveDispatchOption);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            var overlay = GetOverlay(fixture.Dialog);
            Assert.That(FindChild(overlay, "Option 0").GetComponent<Button>().interactable, Is.True);
            var exploreButton = FindChild(overlay, "Option 1").GetComponent<Button>();
            Assert.That(exploreButton.interactable, Is.False);
            Assert.That(exploreButton.GetComponent<Image>().color, Is.EqualTo(DisabledOptionColor));
            Assert.That(FindChild(overlay, "Back"), Is.Null);
        }

        [Test]
        public void WarehouseBranch_WhenNeitherBranchIsLegal_ShowsTwoDisabledOptionsAndClose()
        {
            var state = CreateState(
                "warehouse-close-only",
                FacilityPendingChoiceTypes.ScenarioId,
                FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore);
            state.PendingCardSession.OptionIds.Clear();
            state.PendingCardSession.OptionIds.Add(FacilityPendingChoiceTypes.SkipOption);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            var overlay = GetOverlay(fixture.Dialog);
            var removeButton = FindChild(overlay, "Option 0").GetComponent<Button>();
            var exploreButton = FindChild(overlay, "Option 1").GetComponent<Button>();
            Assert.That(removeButton.interactable, Is.False);
            Assert.That(exploreButton.interactable, Is.False);
            Assert.That(removeButton.GetComponent<Image>().color, Is.EqualTo(DisabledOptionColor));
            Assert.That(exploreButton.GetComponent<Image>().color, Is.EqualTo(DisabledOptionColor));
            Assert.That(GetButtonLabel(overlay, "Back"), Is.EqualTo("关闭"));

            ClickButton(overlay, "Back");

            Assert.That(fixture.SubmittedCommand, Is.Not.Null);
            Assert.That(fixture.SubmittedCommand.OptionIds,
                Is.EqualTo(new[] { FacilityPendingChoiceTypes.SkipOption }));
            Assert.That(fixture.AdditionalExplorePending, Is.Null);
        }

        [Test]
        public void Synchronize_ClearedWarehouseSession_HidesExploreOverlay()
        {
            var state = CreateState(
                "warehouse-session",
                FacilityPendingChoiceTypes.ScenarioId,
                FacilityPendingChoiceTypes.RemoveThenDispatchOrExplore);
            state.PendingCardSession.OptionIds.Clear();
            state.PendingCardSession.OptionIds.Add(FacilityPendingChoiceTypes.ExploreOption);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            ClickButton(GetOverlay(fixture.Dialog), "Option 1");
            Assert.That(GetOverlay(fixture.Dialog), Is.Not.Null);

            state.PendingCardSession = null;

            Assert.That(Synchronize(fixture.Coordinator), Is.False);
            Assert.That(GetOverlay(fixture.Dialog), Is.Null);
        }

        [Test]
        public void ExtensionHub_ShowsThreeSharedNameCardsAndDragSubmitsTargetSlot()
        {
            var state = CreateState(
                "extension-hub-session",
                FacilityPendingChoiceTypes.ScenarioId,
                FacilityPendingChoiceTypes.BuildExtensionHub);
            state.PendingCardSession.OptionIds.Clear();
            state.PendingCardSession.OptionIds.Add(FacilityCardDatabase.ExtensionHubBlue);
            state.PendingCardSession.OptionIds.Add(FacilityCardDatabase.ExtensionHubRed);
            state.PendingCardSession.OptionIds.Add(FacilityPendingChoiceTypes.SkipOption);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            var overlay = GetOverlay(fixture.Dialog);
            var panel = FindChild(overlay, "Facility Effect Choice Panel").GetComponent<RectTransform>();
            var expandedContent = FindChild(overlay, "Facility Expanded Content");
            var collapsedSummary = FindChild(overlay, "Facility Collapsed Summary");
            var sharedPanelType = Type.GetType(
                "YC.Presentation.EffectDialogCollapsiblePanel, Assembly-CSharp",
                false);
            Assert.That(sharedPanelType, Is.Not.Null);
            Assert.That(panel.GetComponent(sharedPanelType), Is.Not.Null);
            Assert.That(panel.sizeDelta.y, Is.EqualTo(360f));

            ClickButton(overlay, "Facility Collapse Toggle");

            Assert.That(panel.sizeDelta.y, Is.EqualTo(58f));
            Assert.That(expandedContent.activeSelf, Is.False);
            Assert.That(collapsedSummary.activeSelf, Is.True);
            Assert.That(collapsedSummary.GetComponent<Text>().text, Does.Contain("延伸枢纽"));
            Assert.That(overlay.GetComponent<Image>().color.a, Is.EqualTo(0f));

            ClickButton(overlay, "Facility Collapse Toggle");

            Assert.That(panel.sizeDelta.y, Is.EqualTo(360f));
            Assert.That(expandedContent.activeSelf, Is.True);
            Assert.That(collapsedSummary.activeSelf, Is.False);
            var blue = FindChild(overlay, "Extension Hub Card " + FacilityCardDatabase.ExtensionHubBlue);
            var yellow = FindChild(overlay, "Extension Hub Card " + FacilityCardDatabase.ExtensionHubYellow);
            var red = FindChild(overlay, "Extension Hub Card " + FacilityCardDatabase.ExtensionHubRed);
            Assert.That(blue, Is.Not.Null);
            Assert.That(yellow, Is.Not.Null);
            Assert.That(red, Is.Not.Null);
            Assert.That(blue.GetComponent<RectTransform>().sizeDelta, Is.EqualTo(new Vector2(99f, 141f)));
            Assert.That(yellow.GetComponent<RectTransform>().sizeDelta, Is.EqualTo(new Vector2(99f, 141f)));
            Assert.That(red.GetComponent<RectTransform>().sizeDelta, Is.EqualTo(new Vector2(99f, 141f)));
            var cardPointerType = Type.GetType(
                "YC.Presentation.CardPointerInteraction, Assembly-CSharp",
                false);
            Assert.That(cardPointerType, Is.Not.Null);
            Assert.That(blue.GetComponent(cardPointerType), Is.Not.Null);
            Assert.That(yellow.GetComponent(cardPointerType), Is.Not.Null);
            Assert.That(red.GetComponent(cardPointerType), Is.Not.Null);
            var blueImageRect = FindChild(blue, "Card Image").GetComponent<RectTransform>();
            Assert.That(blueImageRect.offsetMin, Is.EqualTo(new Vector2(3f, 3f)));
            Assert.That(blueImageRect.offsetMax, Is.EqualTo(new Vector2(-3f, -3f)));
            Assert.That(blue.GetComponent<Button>().interactable, Is.True);
            Assert.That(yellow.GetComponent<Button>().interactable, Is.True);
            Assert.That(yellow.GetComponent<CanvasGroup>().alpha, Is.LessThan(0.5f));
            Assert.That(FindChild(overlay, "Skip Extension Hub"), Is.Not.Null);

            var sharedNames = overlay.GetComponentsInChildren<Text>(true);
            var sharedNameCount = 0;
            for (var i = 0; i < sharedNames.Length; i++)
            {
                if (sharedNames[i].name == "Shared Name")
                {
                    sharedNameCount++;
                    Assert.That(sharedNames[i].text, Is.EqualTo("延伸枢纽"));
                }
            }

            Assert.That(sharedNameCount, Is.EqualTo(3));

            ExecuteCardClick(blue);
            AssertExtensionHubCardViewerIsOpen();
            CloseExtensionHubCardViewer();
            ExecuteCardClick(yellow);
            AssertExtensionHubCardViewerIsOpen();
            CloseExtensionHubCardViewer();

            var buildCanvasObject = new GameObject(
                "Build Info Panel Canvas",
                typeof(RectTransform),
                typeof(Canvas));
            buildCanvasObject.transform.SetParent(canvasObject.transform, false);
            var buildCanvas = buildCanvasObject.GetComponent<Canvas>();
            buildCanvas.overrideSorting = true;
            buildCanvas.sortingOrder = 99;
            var slot = new GameObject("槽位 4", typeof(RectTransform), typeof(Image));
            slot.transform.SetParent(buildCanvasObject.transform, false);
            ConfigureCityBoardDropTarget(slot, 3);
            var slotImage = new GameObject("槽位卡图", typeof(RectTransform), typeof(RawImage));
            slotImage.transform.SetParent(slot.transform, false);
            ExecuteDrag(blue, slotImage);

            Assert.That(fixture.SubmittedCommand, Is.Not.Null);
            Assert.That(fixture.SubmittedCommand.OptionIds, Does.Contain(FacilityCardDatabase.ExtensionHubBlue));
            Assert.That(
                fixture.SubmittedCommand.Parameters[BuildFacilityCommandHandler.CityBoardSlotIndexParameter],
                Is.EqualTo("3"));
        }

        [Test]
        public void ExtensionHub_DisablingCardDuringDragCancelsGhostWithoutSubmitting()
        {
            var state = CreateState(
                "extension-hub-cancel-session",
                FacilityPendingChoiceTypes.ScenarioId,
                FacilityPendingChoiceTypes.BuildExtensionHub);
            state.PendingCardSession.OptionIds.Clear();
            state.PendingCardSession.OptionIds.Add(FacilityCardDatabase.ExtensionHubBlue);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            var blue = FindChild(
                GetOverlay(fixture.Dialog),
                "Extension Hub Card " + FacilityCardDatabase.ExtensionHubBlue);
            BeginDragWithoutRelease(blue, null);
            Assert.That(GetExtensionHubDragging(fixture.Coordinator), Is.True);

            var cardPointerType = Type.GetType(
                "YC.Presentation.CardPointerInteraction, Assembly-CSharp",
                false);
            Assert.That(cardPointerType, Is.Not.Null);
            var onDisable = cardPointerType.GetMethod(
                "OnDisable",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(onDisable, Is.Not.Null);
            onDisable.Invoke(blue.GetComponent(cardPointerType), null);
            blue.SetActive(false);

            Assert.That(GameObject.Find("建设卡拖动虚影"), Is.Null);
            Assert.That(GetExtensionHubDragging(fixture.Coordinator), Is.False);
            Assert.That(fixture.SubmittedCommand, Is.Null);
        }

        [Test]
        public void SimpleEngineeringCamp_UsesStandardBuildDraftAndConfirmation()
        {
            var state = CreateState(
                "simple-engineering-camp-session",
                FacilityPendingChoiceTypes.ScenarioId,
                FacilityPendingChoiceTypes.BuildAdditionalFacility);
            state.PendingCardSession.CardId = FacilityCardDatabase.SimpleEngineeringCamp;
            state.PendingCardSession.OptionIds.Clear();
            state.PendingCardSession.OptionIds.Add(FacilityCardDatabase.TradeDistrict);
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.TradeDistrict);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            Assert.That(fixture.BuildDraft, Is.Null);
            Assert.That(
                InvokeBool(
                    fixture.Coordinator,
                    "TryBeginAdditionalBuildDrag",
                    FacilityCardDatabase.TradeDistrict),
                Is.True);
            Assert.That(
                InvokeBool(
                    fixture.Coordinator,
                    "TryHandleAdditionalBuildDrop",
                    FacilityCardDatabase.TradeDistrict,
                    3),
                Is.True);

            Assert.That(fixture.BuildDraft, Is.Not.Null);
            Assert.That(fixture.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Focused));
            Assert.That(fixture.BuildDraft.Facility.FacilityId, Is.EqualTo(FacilityCardDatabase.TradeDistrict));
            Assert.That(fixture.BuildDraft.CityBoardSlotIndex, Is.EqualTo(3));
            Assert.That(fixture.BuildDraft.SelectedOption.ResourcesPayment.IsAvailable, Is.True);
            Assert.That(fixture.BuildDraft.SelectedOption.GoldPayment.IsAvailable, Is.False);

            fixture.BuildDraft.Dispatch(
                new BuildFacilityIntent.SelectPayment(
                    BuildFacilityService.PaymentModeResources));

            Assert.That(fixture.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Confirming));
            fixture.BuildDraft.Dispatch(new BuildFacilityIntent.Confirm());

            Assert.That(fixture.SubmittedCommand, Is.Not.Null);
            Assert.That(fixture.SubmittedCommand.Kind, Is.EqualTo(GameCommandKind.ResolvePendingChoice));
            Assert.That(fixture.SubmittedCommand.OptionIds, Does.Contain(FacilityCardDatabase.TradeDistrict));
            Assert.That(
                fixture.SubmittedCommand.Parameters[BuildFacilityCommandHandler.CityBoardSlotIndexParameter],
                Is.EqualTo("3"));
            Assert.That(
                fixture.SubmittedCommand.Parameters[BuildFacilityCommandHandler.PaymentModeParameter],
                Is.EqualTo(BuildFacilityService.PaymentModeResources));
        }

        [Test]
        public void SimpleEngineeringCamp_EscapeKeepsAdditionalBuildDraftUntilExplicitCancel()
        {
            var state = CreateState(
                "simple-engineering-camp-escape-session",
                FacilityPendingChoiceTypes.ScenarioId,
                FacilityPendingChoiceTypes.BuildAdditionalFacility);
            state.PendingCardSession.CardId = FacilityCardDatabase.SimpleEngineeringCamp;
            state.PendingCardSession.OptionIds.Clear();
            state.PendingCardSession.OptionIds.Add(FacilityCardDatabase.TradeDistrict);
            state.Decks.FacilitySupply.Add(FacilityCardDatabase.TradeDistrict);
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            Assert.That(
                InvokeBool(
                    fixture.Coordinator,
                    "TryBeginAdditionalBuildDrag",
                    FacilityCardDatabase.TradeDistrict),
                Is.True);
            Assert.That(
                InvokeBool(
                    fixture.Coordinator,
                    "TryHandleAdditionalBuildDrop",
                    FacilityCardDatabase.TradeDistrict,
                    3),
                Is.True);
            Assert.That(fixture.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Focused));

            fixture.BuildDraft.Dispatch(new BuildFacilityIntent.Escape());

            Assert.That(fixture.BuildDraft, Is.Not.Null);
            Assert.That(fixture.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Ghosted));
            Assert.That(fixture.LastPrompt, Does.Contain("虚影"));
            Assert.That(fixture.SubmittedCommand, Is.Null);

            fixture.BuildDraft.Dispatch(new BuildFacilityIntent.BeginGhostDrag());
            fixture.BuildDraft.Dispatch(new BuildFacilityIntent.Drop(3));
            fixture.BuildDraft.Dispatch(
                new BuildFacilityIntent.SelectPayment(
                    BuildFacilityService.PaymentModeResources));
            Assert.That(fixture.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Confirming));

            fixture.BuildDraft.Dispatch(new BuildFacilityIntent.Escape());

            Assert.That(fixture.BuildDraft, Is.Not.Null);
            Assert.That(fixture.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Ghosted));
            Assert.That(fixture.BuildDraft.PaymentMode, Is.Empty);
            Assert.That(fixture.SubmittedCommand, Is.Null);

            fixture.BuildDraft.Dispatch(new BuildFacilityIntent.Cancel());

            Assert.That(fixture.BuildDraft, Is.Null);
            Assert.That(fixture.SubmittedCommand, Is.Null);
        }

        [Test]
        public void ExtensionHub_InvalidAndOccupiedDropsDoNotSubmit()
        {
            var state = CreateState(
                "extension-hub-invalid-drop-session",
                FacilityPendingChoiceTypes.ScenarioId,
                FacilityPendingChoiceTypes.BuildExtensionHub);
            state.PendingCardSession.OptionIds.Clear();
            state.PendingCardSession.OptionIds.Add(FacilityCardDatabase.ExtensionHubBlue);
            state.Map.Facilities.Add(new FacilityPlacement
            {
                PlayerId = 1,
                FacilityCardId = FacilityCardDatabase.TradeDistrict,
                CityBoardSlotIndex = 3
            });
            var fixture = CreateCoordinator(state);

            Assert.That(Synchronize(fixture.Coordinator), Is.True);
            var blue = FindChild(
                GetOverlay(fixture.Dialog),
                "Extension Hub Card " + FacilityCardDatabase.ExtensionHubBlue);
            var unmarked = new GameObject("非城市槽位", typeof(RectTransform), typeof(Image));
            unmarked.transform.SetParent(canvasObject.transform, false);
            ExecuteDrag(blue, unmarked);
            Assert.That(fixture.SubmittedCommand, Is.Null);
            Assert.That(fixture.LastPrompt, Does.Contain("空槽位"));

            var occupied = new GameObject("槽位 4", typeof(RectTransform), typeof(Image));
            occupied.transform.SetParent(canvasObject.transform, false);
            ConfigureCityBoardDropTarget(occupied, 3);
            ExecuteDrag(blue, occupied);
            Assert.That(fixture.SubmittedCommand, Is.Null);
            Assert.That(fixture.LastPrompt, Does.Contain("占用"));
        }

        private CoordinatorFixture CreateMercenaryCoordinator(
            out string opponentSlot,
            out string ownSlot,
            out string emptySlot)
        {
            const string routeId = "mercenary-route";
            opponentSlot = InfluenceService.GetRouteSlotId(routeId, 0);
            ownSlot = InfluenceService.GetRouteSlotId(routeId, 1);
            emptySlot = InfluenceService.GetRouteSlotId(routeId, 2);

            var state = CreateState(
                "mercenary-headquarters-session",
                FacilityPendingChoiceTypes.ScenarioId,
                FacilityPendingChoiceTypes.ReplaceOneInfluence);
            state.MapId = "mercenary-headquarters-ui-test";
            state.PendingCardSession.CardId = FacilityCardDatabase.MercenaryCommand;
            state.PendingCardSession.OptionIds.Clear();
            state.PendingCardSession.OptionIds.Add(FacilityPendingChoiceTypes.ReplaceInfluenceOption);
            state.PendingCardSession.OptionIds.Add(FacilityPendingChoiceTypes.DeployInfluenceOption);
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 2,
                SlotId = opponentSlot,
                RouteId = routeId
            });
            state.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = ownSlot,
                RouteId = routeId
            });

            var map = new GameMapDefinition { MapId = state.MapId };
            map.Routes.Add(new MapRouteDefinition
            {
                RouteId = routeId,
                InfluenceSlotCount = 3
            });
            return CreateCoordinator(state, map);
        }

        private CoordinatorFixture CreateCoordinator(
            GameState state,
            GameMapDefinition map = null,
            Action<GameCommand> onSubmit = null)
        {
            var coordinatorType = Type.GetType(
                "YC.Presentation.FacilityEffectInteractionUiCoordinator, Assembly-CSharp",
                false);
            var dialogType = Type.GetType(
                "YC.Presentation.FacilityEffectChoiceDialog, Assembly-CSharp",
                false);
            var registryType = Type.GetType(
                "YC.Presentation.GameplayDialogRegistry, Assembly-CSharp",
                false);
            var effectViewType = Type.GetType(
                "YC.Presentation.EffectDialogShellView, Assembly-CSharp",
                false);
            Assert.That(coordinatorType, Is.Not.Null, "Missing FacilityEffectInteractionUiCoordinator.");
            Assert.That(dialogType, Is.Not.Null, "Missing FacilityEffectChoiceDialog.");
            Assert.That(registryType, Is.Not.Null);
            Assert.That(effectViewType, Is.Not.Null);

            canvasObject = new GameObject(
                "Facility Effect Coordinator Test Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 15;

            var hudPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            Assert.That(hudPrefab, Is.Not.Null);
            var registry = hudPrefab.GetComponentInChildren(registryType, true);
            Assert.That(registry, Is.Not.Null);
            var dialog = Activator.CreateInstance(
                dialogType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new object[] { registry, canvasObject.GetComponent<RectTransform>() },
                null);
            var mapQuery = new MapQueryService(map ?? new GameMapDefinition { MapId = "facility-ui-test" });
            Func<GameState> getState = () => state;
            Func<int> getLocalPlayerId = () => 1;
            var highlights = new List<WorkflowHighlight>();
            Action<IReadOnlyList<WorkflowHighlight>> setHighlights = values =>
            {
                highlights.Clear();
                if (values != null)
                {
                    highlights.AddRange(values);
                }
            };
            Action clearHighlights = () => highlights.Clear();
            PendingCardSessionState additionalExplorePending = null;
            string additionalExploreOptionId = null;
            Action<PendingCardSessionState, string> beginAdditionalExplore = (pending, optionId) =>
            {
                additionalExplorePending = pending;
                additionalExploreOptionId = optionId;
            };
            var cancelAdditionalExploreCount = 0;
            Action cancelAdditionalExplore = () => cancelAdditionalExploreCount++;
            GameCommand submittedCommand = null;
            Action<GameCommand> submit = command =>
            {
                submittedCommand = command;
                onSubmit?.Invoke(command);
            };
            string lastPrompt = null;
            Action<string> setPrompt = value => lastPrompt = value;
            BuildFacilityDraftViewModel buildDraft = null;
            Action<BuildFacilityDraftViewModel> showBuildDraft = value => buildDraft = value;
            Action hideBuildDraft = () => buildDraft = null;

            var constructors = coordinatorType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(constructors.Length, Is.EqualTo(1));
            var coordinator = constructors[0].Invoke(new object[]
            {
                getState,
                getLocalPlayerId,
                mapQuery,
                dialog,
                setHighlights,
                clearHighlights,
                beginAdditionalExplore,
                cancelAdditionalExplore,
                submit,
                setPrompt
            });
            var configureBuildDraftView = coordinatorType.GetMethod(
                "ConfigureAdditionalBuildDraftView",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(configureBuildDraftView, Is.Not.Null);
            configureBuildDraftView.Invoke(coordinator, new object[] { showBuildDraft, hideBuildDraft });

            return new CoordinatorFixture(
                coordinator,
                dialog,
                () => submittedCommand,
                () => additionalExplorePending,
                () => additionalExploreOptionId,
                () => cancelAdditionalExploreCount,
                () => lastPrompt,
                () => highlights,
                () => buildDraft);
        }

        private static void ExecuteDrag(GameObject source, GameObject target)
        {
            var eventSystemObject = new GameObject("Facility Effect Test EventSystem", typeof(EventSystem));
            eventSystemObject.transform.SetParent(source.transform.root, false);
            var eventData = new PointerEventData(eventSystemObject.GetComponent<EventSystem>());
            eventData.pointerCurrentRaycast = new RaycastResult { gameObject = target };

            var behaviours = source.GetComponents<MonoBehaviour>();
            IBeginDragHandler begin = null;
            IDragHandler drag = null;
            IEndDragHandler end = null;
            for (var i = 0; i < behaviours.Length; i++)
            {
                if (begin == null)
                {
                    begin = behaviours[i] as IBeginDragHandler;
                }

                if (drag == null)
                {
                    drag = behaviours[i] as IDragHandler;
                }

                if (end == null)
                {
                    end = behaviours[i] as IEndDragHandler;
                }
            }

            Assert.That(begin, Is.Not.Null);
            Assert.That(drag, Is.Not.Null);
            Assert.That(end, Is.Not.Null);
            begin.OnBeginDrag(eventData);
            drag.OnDrag(eventData);
            var ghost = GameObject.Find("建设卡拖动虚影");
            Assert.That(ghost, Is.Not.Null);
            Assert.That(
                ghost.GetComponent<RectTransform>().sizeDelta,
                Is.EqualTo(source.GetComponent<RectTransform>().sizeDelta));
            Assert.That(ghost.GetComponent<RawImage>().raycastTarget, Is.False);
            Assert.That(ghost.GetComponent<CanvasGroup>().blocksRaycasts, Is.False);
            var dragCanvas = ghost.GetComponent<Canvas>();
            var targetCanvas = target == null ? null : target.GetComponentInParent<Canvas>();
            Assert.That(dragCanvas, Is.Not.Null);
            Assert.That(dragCanvas.overrideSorting, Is.True);
            if (targetCanvas != null)
            {
                Assert.That(dragCanvas.sortingOrder, Is.GreaterThan(targetCanvas.sortingOrder));
            }

            end.OnEndDrag(eventData);
            Assert.That(GameObject.Find("建设卡拖动虚影"), Is.Null);
        }

        private static void ExecuteCardClick(GameObject source)
        {
            var interactionType = Type.GetType(
                "YC.Presentation.CardPointerInteraction, Assembly-CSharp",
                false);
            Assert.That(interactionType, Is.Not.Null);
            var interaction = source.GetComponent(interactionType);
            Assert.That(interaction, Is.Not.Null);

            var eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                var eventSystemObject = new GameObject("Facility Effect Click Test EventSystem", typeof(EventSystem));
                eventSystemObject.transform.SetParent(source.transform.root, false);
                eventSystem = eventSystemObject.GetComponent<EventSystem>();
            }

            var eventData = new PointerEventData(eventSystem)
            {
                button = PointerEventData.InputButton.Left,
                clickCount = 1,
                position = RectTransformUtility.WorldToScreenPoint(null, source.transform.position)
            };
            ((IPointerDownHandler)interaction).OnPointerDown(eventData);
            ((IPointerClickHandler)interaction).OnPointerClick(eventData);
        }

        private static void AssertExtensionHubCardViewerIsOpen()
        {
            var viewerObject = GameObject.Find("Extension Hub Card Image Viewer");
            Assert.That(viewerObject, Is.Not.Null);
            var viewerType = Type.GetType(
                "YC.Presentation.ZoomableImageViewerController, Assembly-CSharp",
                false);
            Assert.That(viewerType, Is.Not.Null);
            var viewer = viewerObject.GetComponent(viewerType);
            Assert.That(viewer, Is.Not.Null);
            Assert.That(
                (bool)viewerType.GetProperty("IsOpen").GetValue(viewer, null),
                Is.True);
            var viewerCanvas = GameObject.Find("Extension Hub Card Viewer Canvas");
            Assert.That(viewerCanvas, Is.Not.Null);
            Assert.That(viewerCanvas.transform.parent, Is.Null);
            var panel = GameObject.Find("Extension Hub Card Panel").GetComponent<RectTransform>();
            Assert.That(panel.rect.width, Is.GreaterThan(400f));
            Assert.That(panel.rect.height, Is.GreaterThan(500f));
            var image = GameObject.Find("Extension Hub Card Image").GetComponent<RawImage>();
            Assert.That(image.texture, Is.Not.Null);
            Assert.That(image.rectTransform.rect.width, Is.GreaterThan(200f));
            Assert.That(image.rectTransform.rect.height, Is.GreaterThan(300f));
            var title = GameObject.Find("Extension Hub Card Title");
            Assert.That(title, Is.Not.Null);
            Assert.That(title.GetComponent<Text>().text, Is.EqualTo("延伸枢纽"));
        }

        private static void CloseExtensionHubCardViewer()
        {
            var viewerObject = GameObject.Find("Extension Hub Card Image Viewer");
            Assert.That(viewerObject, Is.Not.Null);
            var viewerType = Type.GetType(
                "YC.Presentation.ZoomableImageViewerController, Assembly-CSharp",
                false);
            Assert.That(viewerType, Is.Not.Null);
            viewerType.GetMethod("Close").Invoke(viewerObject.GetComponent(viewerType), null);
        }

        private static void BeginDragWithoutRelease(GameObject source, GameObject target)
        {
            var eventSystemObject = new GameObject("Facility Effect Cancel Test EventSystem", typeof(EventSystem));
            eventSystemObject.transform.SetParent(source.transform.root, false);
            var eventData = new PointerEventData(eventSystemObject.GetComponent<EventSystem>())
            {
                button = PointerEventData.InputButton.Left,
                pointerCurrentRaycast = new RaycastResult { gameObject = target }
            };

            Assert.That(
                ExecuteEvents.Execute(source, eventData, ExecuteEvents.beginDragHandler),
                Is.True);
            Assert.That(
                ExecuteEvents.Execute(source, eventData, ExecuteEvents.dragHandler),
                Is.True);
            Assert.That(GameObject.Find("建设卡拖动虚影"), Is.Not.Null);
        }

        private static void ConfigureCityBoardDropTarget(GameObject target, int slotIndex)
        {
            var targetType = Type.GetType(
                "YC.Presentation.CityBoardSlotDropTarget, Assembly-CSharp",
                false);
            Assert.That(targetType, Is.Not.Null);
            var component = target.AddComponent(targetType);
            var configure = targetType.GetMethod(
                "Configure",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(configure, Is.Not.Null);
            configure.Invoke(component, new object[] { slotIndex });
        }

        private static GameState CreateState(
            string sessionId,
            string scenarioId,
            string choiceType = FacilityPendingChoiceTypes.SellResources)
        {
            return new GameState
            {
                Players =
                {
                    new PlayerState
                    {
                        PlayerId = 1,
                        Resources =
                        {
                            Originium = 3,
                            OriginiumShard = 3,
                            Iron = 3,
                            PureOriginium = 3
                        }
                    }
                },
                PendingCardSession = CreatePending(sessionId, scenarioId, choiceType)
            };
        }

        private static PendingCardSessionState CreatePending(
            string sessionId,
            string scenarioId,
            string choiceType = FacilityPendingChoiceTypes.SellResources)
        {
            return new PendingCardSessionState
            {
                SessionId = sessionId,
                ScenarioId = scenarioId,
                ChoiceType = choiceType,
                CardId = "building_027",
                PlayerId = 1,
                OptionIds =
                {
                    FacilityPendingChoiceTypes.ConfirmOption,
                    FacilityPendingChoiceTypes.SkipOption
                }
            };
        }

        private static bool Synchronize(object coordinator)
        {
            var method = coordinator.GetType().GetMethod(
                "Synchronize",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(coordinator, null);
        }

        private static bool TryHandleLocationClicked(object coordinator, string locationId)
        {
            var method = coordinator.GetType().GetMethod(
                "TryHandleLocationClicked",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(coordinator, new object[] { locationId });
        }

        private static bool TryHandleInfluenceSlotClicked(object coordinator, string slotId)
        {
            var method = coordinator.GetType().GetMethod(
                "TryHandleInfluenceSlotClicked",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(coordinator, new object[] { slotId });
        }

        private static bool InvokeBool(object target, string methodName, params object[] arguments)
        {
            var method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing method " + methodName + ".");
            return (bool)method.Invoke(target, arguments);
        }

        private static void Invoke(object target, string methodName, params object[] arguments)
        {
            var method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing method " + methodName + ".");
            method.Invoke(target, arguments);
        }

        private static bool GetExtensionHubDragging(object coordinator)
        {
            var property = coordinator.GetType().GetProperty(
                "IsExtensionHubDragging",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null);
            return (bool)property.GetValue(coordinator, null);
        }

        private static GameObject GetOverlay(object dialog)
        {
            var shellField = dialog.GetType().GetField(
                "shell",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(shellField, Is.Not.Null);
            var shell = shellField.GetValue(dialog);
            Assert.That(shell, Is.Not.Null);
            var viewField = shell.GetType().GetField(
                "view",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(viewField, Is.Not.Null);
            var view = viewField.GetValue(shell) as Component;
            return view == null ? null : view.gameObject;
        }

        private static void ClickButton(GameObject root, string objectName)
        {
            var child = FindChild(root, objectName);
            Assert.That(child, Is.Not.Null, "Missing button " + objectName + ".");
            var button = child.GetComponent<Button>();
            Assert.That(button, Is.Not.Null);
            button.onClick.Invoke();
        }

        private static string GetText(GameObject root, string objectName)
        {
            var child = FindChild(root, objectName);
            Assert.That(child, Is.Not.Null, "Missing text " + objectName + ".");
            var text = child.GetComponent<Text>();
            Assert.That(text, Is.Not.Null);
            return text.text;
        }

        private static string GetButtonLabel(GameObject root, string objectName)
        {
            var child = FindChild(root, objectName);
            Assert.That(child, Is.Not.Null, "Missing button " + objectName + ".");
            var text = child.GetComponentInChildren<Text>(true);
            Assert.That(text, Is.Not.Null);
            return text.text;
        }

        private static GameObject FindChild(GameObject root, string objectName)
        {
            var children = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < children.Length; i++)
            {
                if (children[i] != null && children[i].name == objectName)
                {
                    return children[i].gameObject;
                }
            }

            return null;
        }

        private sealed class CoordinatorFixture
        {
            private readonly Func<GameCommand> getSubmittedCommand;
            private readonly Func<PendingCardSessionState> getAdditionalExplorePending;
            private readonly Func<string> getAdditionalExploreOptionId;
            private readonly Func<int> getCancelAdditionalExploreCount;
            private readonly Func<string> getLastPrompt;
            private readonly Func<List<WorkflowHighlight>> getHighlights;
            private readonly Func<BuildFacilityDraftViewModel> getBuildDraft;

            public CoordinatorFixture(
                object coordinator,
                object dialog,
                Func<GameCommand> getSubmittedCommand,
                Func<PendingCardSessionState> getAdditionalExplorePending,
                Func<string> getAdditionalExploreOptionId,
                Func<int> getCancelAdditionalExploreCount,
                Func<string> getLastPrompt,
                Func<List<WorkflowHighlight>> getHighlights,
                Func<BuildFacilityDraftViewModel> getBuildDraft)
            {
                Coordinator = coordinator;
                Dialog = dialog;
                this.getSubmittedCommand = getSubmittedCommand;
                this.getAdditionalExplorePending = getAdditionalExplorePending;
                this.getAdditionalExploreOptionId = getAdditionalExploreOptionId;
                this.getCancelAdditionalExploreCount = getCancelAdditionalExploreCount;
                this.getLastPrompt = getLastPrompt;
                this.getHighlights = getHighlights;
                this.getBuildDraft = getBuildDraft;
            }

            public object Coordinator { get; private set; }

            public object Dialog { get; private set; }

            public GameCommand SubmittedCommand
            {
                get { return getSubmittedCommand == null ? null : getSubmittedCommand(); }
            }

            public PendingCardSessionState AdditionalExplorePending
            {
                get { return getAdditionalExplorePending == null ? null : getAdditionalExplorePending(); }
            }

            public string AdditionalExploreOptionId
            {
                get { return getAdditionalExploreOptionId == null ? string.Empty : getAdditionalExploreOptionId(); }
            }

            public int CancelAdditionalExploreCount
            {
                get { return getCancelAdditionalExploreCount == null ? 0 : getCancelAdditionalExploreCount(); }
            }

            public string LastPrompt
            {
                get { return getLastPrompt == null ? string.Empty : getLastPrompt(); }
            }

            public List<WorkflowHighlight> Highlights
            {
                get { return getHighlights == null ? new List<WorkflowHighlight>() : getHighlights(); }
            }

            public BuildFacilityDraftViewModel BuildDraft
            {
                get { return getBuildDraft == null ? null : getBuildDraft(); }
            }
        }
    }
}
