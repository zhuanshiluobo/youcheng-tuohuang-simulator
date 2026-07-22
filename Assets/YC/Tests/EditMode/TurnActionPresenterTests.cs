using System.Collections.Generic;
using NUnit.Framework;
using YC.Application.Gameplay;
using YC.Domain.CityStyles;
using YC.Domain.Commands;
using YC.Domain.Events;
using YC.Domain.Exploration;
using YC.Domain.Facilities;
using YC.Domain.Harvest;
using YC.Domain.Influence;
using YC.Domain.Maps;
using YC.Domain.Rules;
using YC.Domain.SpecialActions;
using YC.Domain.State;
using YC.Presentation;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class TurnActionPresenterTests
    {
        [Test]
        public void ActionPhaseViewModel_ReflectsMainActionAndEndAvailability()
        {
            var fixture = CreateFixture();

            var before = fixture.Presenter.BuildActionPanelViewModel();
            Assert.That(before.CanMoveCity, Is.True);
            Assert.That(before.CanBuild, Is.True);
            Assert.That(before.CanEndAction, Is.False);

            fixture.Context.State.FindPlayer(1).ActedMainActionThisTurn = true;
            var after = fixture.Presenter.BuildActionPanelViewModel();
            Assert.That(after.CanMoveCity, Is.False);
            Assert.That(after.CanBuild, Is.False);
            Assert.That(after.CanEndAction, Is.True);
        }

        [Test]
        public void ActionPhaseViewModel_WithAdditionalBudget_AllowsAnotherMainActionAndEarlyEnd()
        {
            var fixture = CreateFixture();
            var player = fixture.Context.State.FindPlayer(1);
            player.RemainingMainActionsThisTurn = 2;
            player.CompletedMainActionsThisTurn = 1;
            player.ActedMainActionThisTurn = false;

            var viewModel = fixture.Presenter.BuildActionPanelViewModel();

            Assert.That(viewModel.CanMoveCity, Is.True);
            Assert.That(viewModel.CanBuild, Is.True);
            Assert.That(viewModel.CanEndAction, Is.True);
        }

        [Test]
        public void CharacterAction_RequiresCoveredUnusedCard()
        {
            var fixture = CreateFixture();
            var player = fixture.Context.State.FindPlayer(1);

            Assert.That(fixture.Presenter.BuildActionPanelViewModel().CanUseCharacter, Is.False);

            player.CoveredCharacterCardId = "character-red-texas";
            Assert.That(fixture.Presenter.BuildActionPanelViewModel().CanUseCharacter, Is.True);

            player.UsedCharacterThisRound = true;
            Assert.That(fixture.Presenter.BuildActionPanelViewModel().CanUseCharacter, Is.False);
        }

        [Test]
        public void CharacterAction_WhenLockedForCurrentActionTurn_IsUnavailable()
        {
            var fixture = CreateFixture();
            var player = fixture.Context.State.FindPlayer(1);
            player.CoveredCharacterCardId = "character-red-texas";
            player.CharacterCardLockedThisTurn = true;

            Assert.That(fixture.Presenter.BuildActionPanelViewModel().CanUseCharacter, Is.False);
        }

        [Test]
        public void CharacterCoverPhase_PromptsCurrentPlayerToCoverFirst()
        {
            var fixture = CreateFixture();
            fixture.Context.State.Phase = GamePhase.CharacterCover;
            fixture.Context.State.CurrentPlayerId = 1;
            fixture.Context.State.FindPlayer(1).CoveredCharacterCardId = string.Empty;

            Assert.That(fixture.Presenter.BuildActionPanelViewModel().StatusText,
                Is.EqualTo("入场阶段：请先盖放角色卡"));
        }

        [Test]
        public void ResourceCollectionAndCleanup_ExposeTheirOwnEndRules()
        {
            var fixture = CreateFixture();
            fixture.Context.State.Phase = GamePhase.ResourceCollection;
            Assert.That(fixture.Presenter.CanEndCurrentAction(), Is.True);
            Assert.That(fixture.Presenter.BuildActionPanelViewModel().Mode,
                Is.EqualTo(InteractionMode.ResolvingResourceCollection));

            fixture.Context.State.FindPlayer(1).HasCollectedResourcesThisRound = true;
            fixture.Context.State.FindPlayer(2).HasCollectedResourcesThisRound = true;
            Assert.That(fixture.Presenter.CanEndCurrentAction(), Is.False);

            fixture.Context.State.Phase = GamePhase.Cleanup;
            fixture.Context.State.StartPlayerId = 1;
            fixture.Context.State.CurrentPlayerId = 1;
            fixture.Context.LocalPlayerId = 1;
            Assert.That(fixture.Presenter.CanEndCurrentAction(), Is.True);
        }

        [Test]
        public void HotseatSynchronization_SelectsCurrentOrFirstUncollectedPlayer()
        {
            var fixture = CreateFixture();
            fixture.Context.State.CurrentPlayerId = 2;
            fixture.Presenter.SynchronizeFromState();
            Assert.That(fixture.Context.LocalPlayerId, Is.EqualTo(2));

            fixture.Context.State.Phase = GamePhase.ResourceCollection;
            fixture.Context.State.FindPlayer(1).HasCollectedResourcesThisRound = true;
            fixture.Context.State.FindPlayer(2).HasCollectedResourcesThisRound = false;
            fixture.Context.LocalPlayerId = 1;
            fixture.Presenter.SynchronizeFromState();
            Assert.That(fixture.Context.LocalPlayerId, Is.EqualTo(2));
        }

        [Test]
        public void InitialPlacement_HighlightsDockAndBuildsCommand()
        {
            var fixture = CreateFixture();
            fixture.Context.State.Phase = GamePhase.Entrance;
            fixture.Context.State.FindPlayer(1).CityLocationId = string.Empty;

            Assert.That(fixture.Presenter.BuildInitialPlacementHighlights().Count, Is.EqualTo(2));
            fixture.Presenter.PlaceInitialCity("A");

            Assert.That(fixture.Commands.LastCommand.Kind, Is.EqualTo(GameCommandKind.ChooseInitialLocation));
            Assert.That(fixture.Commands.LastCommand.TargetId, Is.EqualTo("A"));
            Assert.That(fixture.View.RefreshFromStateCount, Is.EqualTo(1));
        }

        [Test]
        public void MoveCity_SuccessFailureAndHostWaitUseDistinctOutcomes()
        {
            var fixture = CreateFixture();
            fixture.Presenter.BeginMoveAction();
            Assert.That(fixture.Flow.CurrentMode, Is.EqualTo(InteractionMode.ResolvingMoveTarget));
            Assert.That(fixture.View.Highlights.Count, Is.EqualTo(1));

            fixture.Commands.NextResult = Rejected("blocked");
            fixture.Presenter.MoveCity("B");
            Assert.That(fixture.View.Prompt, Is.EqualTo("blocked"));

            fixture.Commands.NextResult = Success(false);
            fixture.Presenter.MoveCity("B");
            Assert.That(fixture.View.Prompt, Does.Contain("等待确认"));

            fixture.Commands.NextResult = Success(true);
            fixture.Presenter.MoveCity("B");
            Assert.That(fixture.View.CompletedAction, Is.EqualTo("城市移动"));
            Assert.That(fixture.Flow.CurrentMode, Is.EqualTo(InteractionMode.ChooseAction));
        }

        [Test]
        public void Build_DraftsLocallyAndSubmitsOnlyAfterFinalConfirmation()
        {
            var fixture = CreateFixture();
            fixture.Presenter.BeginBuildAction();
            Assert.That(fixture.View.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Selecting));
            Assert.That(fixture.Commands.LastCommand, Is.Null);

            fixture.Presenter.BeginBuildFacilityDrag(FacilityCardDatabase.TradeDistrict);
            Assert.That(fixture.View.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Dragging));
            fixture.Presenter.DropBuildFacility(3);
            Assert.That(fixture.View.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Focused));
            fixture.Presenter.SelectBuildFacilityPayment(BuildFacilityService.PaymentModeGold);
            Assert.That(fixture.View.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Confirming));
            Assert.That(fixture.Commands.LastCommand, Is.Null, "支付预选不得提交命令。");

            fixture.Commands.NextResult = Success(false);
            fixture.Presenter.ConfirmBuildFacility();
            Assert.That(fixture.Commands.LastCommand.Kind, Is.EqualTo(GameCommandKind.BuildFacility));
            Assert.That(fixture.Commands.LastCommand.TargetId, Is.EqualTo(FacilityCardDatabase.TradeDistrict));
            Assert.That(fixture.Commands.LastCommand.Parameters[BuildFacilityCommandHandler.CityBoardSlotIndexParameter], Is.EqualTo("3"));
            Assert.That(fixture.Commands.LastCommand.Parameters[BuildFacilityCommandHandler.PaymentModeParameter], Is.EqualTo(BuildFacilityService.PaymentModeGold));
            Assert.That(fixture.View.Prompt, Does.Contain("等待确认"));
        }

        [Test]
        public void BuildEscape_CancelsDraftAndRestoresActionSelection()
        {
            var fixture = CreateFixture();
            fixture.Presenter.BeginBuildFacilityDrag(FacilityCardDatabase.TradeDistrict);
            fixture.Presenter.DropBuildFacility(3);
            Assert.That(fixture.View.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Focused));
            Assert.That(fixture.Flow.CurrentMode, Is.EqualTo(InteractionMode.ResolvingBuildFocus));

            Assert.That(fixture.Presenter.HandleBuildFacilityEscape(), Is.True);

            Assert.That(fixture.Presenter.BuildBuildFacilityDraftViewModel(), Is.Null);
            Assert.That(fixture.View.BuildDraft, Is.Null);
            Assert.That(fixture.Flow.CurrentMode, Is.EqualTo(InteractionMode.ChooseAction));
            Assert.That(fixture.View.Prompt, Is.Not.Empty);
            Assert.That(fixture.Presenter.HandleBuildFacilityEscape(), Is.False);
        }

        [Test]
        public void BuildAvailability_SupportsDirectSupplyDragAndShowsExhaustedMessage()
        {
            var fixture = CreateFixture();

            var available = fixture.Presenter.BuildBuildFacilityAvailabilityViewModel();
            Assert.That(available.DraggableFacilityIds, Does.Contain(FacilityCardDatabase.TradeDistrict));
            Assert.That(available.UnavailableMessage, Is.Empty);
            Assert.That(fixture.Presenter.BuildBuildFacilityDraftViewModel(), Is.Null);

            fixture.Presenter.BeginBuildFacilityDrag(FacilityCardDatabase.TradeDistrict);
            Assert.That(fixture.View.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Dragging));

            fixture.Presenter.RejectBuildFacilityDrop();
            Assert.That(fixture.Presenter.BuildBuildFacilityDraftViewModel(), Is.Null);
            Assert.That(fixture.View.BuildDraft, Is.Null);
            Assert.That(fixture.Flow.CurrentMode, Is.EqualTo(InteractionMode.ChooseAction));
            fixture.Context.State.FindPlayer(1).ActedMainActionThisTurn = true;
            var exhausted = fixture.Presenter.BuildBuildFacilityAvailabilityViewModel();
            Assert.That(exhausted.DraggableFacilityIds, Is.Empty);
            Assert.That(exhausted.UnavailableMessage, Is.EqualTo("本行动轮行动次数已用尽。"));
        }

        [Test]
        public void BuildAvailability_UsesAdditionalBuildWhenMainActionIsSpent()
        {
            var fixture = CreateFixture();
            fixture.Context.State.FindPlayer(1).ActedMainActionThisTurn = true;
            fixture.Context.State.PendingCardSession = new PendingCardSessionState
            {
                SessionId = "additional-build",
                ScenarioId = FacilityPendingChoiceTypes.ScenarioId,
                ChoiceType = FacilityPendingChoiceTypes.BuildAdditionalFacility,
                CardId = FacilityCardDatabase.SimpleEngineeringCamp,
                PlayerId = 1,
                OptionIds = { FacilityCardDatabase.TradeDistrict }
            };

            var availability = fixture.Presenter.BuildBuildFacilityAvailabilityViewModel();

            Assert.That(availability.UsesSpecialBuild, Is.True);
            Assert.That(availability.DraggableFacilityIds, Does.Contain(FacilityCardDatabase.TradeDistrict));
            Assert.That(availability.LegalSlotIndexes, Has.Count.EqualTo(BuildFacilityService.CityBoardSlotCount));
            Assert.That(availability.UnavailableMessage, Is.Empty);
        }

        [Test]
        public void BuildAvailability_ExtensionHubUsesSharedLegalSlotsWithoutPublicCardDrag()
        {
            var fixture = CreateFixture();
            fixture.Context.State.PendingCardSession = new PendingCardSessionState
            {
                SessionId = "extension-hub-build",
                ScenarioId = FacilityPendingChoiceTypes.ScenarioId,
                ChoiceType = FacilityPendingChoiceTypes.BuildExtensionHub,
                CardId = FacilityCardDatabase.LogisticsHub,
                PlayerId = 1,
                OptionIds = { FacilityCardDatabase.ExtensionHubBlue }
            };
            fixture.Context.State.Map.Facilities.Add(new FacilityPlacement
            {
                PlayerId = 1,
                FacilityCardId = FacilityCardDatabase.TradeDistrict,
                CityBoardSlotIndex = 3
            });

            var availability = fixture.Presenter.BuildBuildFacilityAvailabilityViewModel();

            Assert.That(availability.UsesSpecialBuild, Is.True);
            Assert.That(availability.DraggableFacilityIds, Is.Empty);
            Assert.That(availability.LegalSlotIndexes, Has.Count.EqualTo(BuildFacilityService.CityBoardSlotCount - 1));
            Assert.That(new List<int>(availability.LegalSlotIndexes).Contains(3), Is.False);
            Assert.That(availability.UnavailableMessage, Is.Empty);
        }

        [Test]
        public void Declare_CreatesCommandAndKeepsNetworkingWaitSemantics()
        {
            var fixture = CreateFixture();
            fixture.Commands.NextResult = Success(false);
            fixture.Presenter.SubmitDeclareCityStyle("style-test", new[] { 3, 1 });
            Assert.That(fixture.Commands.LastCommand.Kind, Is.EqualTo(GameCommandKind.DeclareCityStyle));
            Assert.That(fixture.Commands.LastCommand.TargetId, Is.EqualTo("style-test"));
            Assert.That(
                fixture.Commands.LastCommand.Parameters[DeclareCityStyleCommandHandler.UsedCityBoardSlotIndexesParameter],
                Is.EqualTo("1,3"));
            Assert.That(fixture.View.Prompt, Does.Contain("等待确认"));
        }

        [Test]
        public void Declare_LocalSuccessShowsConciseCompletionPrompt()
        {
            var fixture = CreateFixture();
            fixture.Commands.NextResult = Success(true);

            fixture.Presenter.SubmitDeclareCityStyle("style-test", new[] { 1, 3 });

            Assert.That(fixture.View.Prompt, Is.EqualTo("城市样式宣告完成。"));
        }

        [Test]
        public void BeginDeclare_ProvidesPreviewModelAndDoesNotInterruptActiveMainAction()
        {
            var fixture = CreateFixture();

            fixture.Presenter.BeginDeclareCityStyle(CityStyleDatabase.MaterialRelayStation);

            Assert.That(fixture.View.CityStyleOptions, Is.Not.Null);
            Assert.That(
                fixture.View.CityStyleOptions.InitialCityStyleId,
                Is.EqualTo(CityStyleDatabase.MaterialRelayStation));
            Assert.That(fixture.View.CityStyleOptions.CityBoardSlots, Has.Count.EqualTo(12));
            Assert.That(fixture.View.CityStyleOptions.CityStyleMarkers, Is.Not.Null);
            Assert.That(
                typeof(CityStyleOptionsViewModel).GetProperty("State"),
                Is.Null,
                "城市样式弹窗 ViewModel 不应暴露可变 GameState。");
            Assert.That(fixture.View.CityStyleOptions.ValidateSelection, Is.Not.Null);
            Assert.That(fixture.View.CityStyleOptions.ConfirmSelection, Is.Not.Null);
            Assert.That(fixture.View.Prompt, Is.Empty, "样式预览不应再显示重复的全局操作提示。");

            fixture.View.CityStyleOptions = null;
            fixture.Presenter.BeginExploreAction();
            fixture.Presenter.BeginDeclareCityStyle(CityStyleDatabase.MilitaryIndustrialArea);

            Assert.That(fixture.View.CityStyleOptions, Is.Null);
            Assert.That(fixture.Flow.CurrentMode, Is.EqualTo(InteractionMode.ResolvingExploreTarget));
            Assert.That(fixture.View.Prompt, Does.Contain("正在进行的行动"));

            fixture.Presenter.OpenCityStylePreview(CityStyleDatabase.MilitaryIndustrialArea);

            Assert.That(fixture.View.CityStyleOptions, Is.Not.Null);
            Assert.That(
                fixture.View.CityStyleOptions.InitialCityStyleId,
                Is.EqualTo(CityStyleDatabase.MilitaryIndustrialArea));
            for (var i = 0; i < fixture.View.CityStyleOptions.Options.Count; i++)
            {
                Assert.That(fixture.View.CityStyleOptions.Options[i].CanDeclare, Is.False);
            }
            Assert.That(fixture.Flow.CurrentMode, Is.EqualTo(InteractionMode.ResolvingExploreTarget));
            Assert.That(fixture.View.Prompt, Is.Empty, "不可宣告原因应显示在样式预览内，不应占用全局提示区。");
        }

        [Test]
        public void CityStylePreview_ConfirmKeepsDialogOpenWhenSubmissionIsRejected()
        {
            var fixture = CreateFixture();
            fixture.Presenter.BeginDeclareCityStyle(CityStyleDatabase.MilitaryIndustrialArea);

            fixture.Commands.NextResult = Rejected("state changed");
            var rejected = fixture.View.CityStyleOptions.ConfirmSelection(
                CityStyleDatabase.MilitaryIndustrialArea,
                new[] { 0, 1 });

            Assert.That(rejected, Is.False);
            Assert.That(fixture.View.Prompt, Is.EqualTo("state changed"));

            fixture.Commands.NextResult = Success(false);
            var sentToHost = fixture.View.CityStyleOptions.ConfirmSelection(
                CityStyleDatabase.MilitaryIndustrialArea,
                new[] { 0, 1 });

            Assert.That(sentToHost, Is.True);
            Assert.That(fixture.View.Prompt, Does.Contain("等待确认"));
        }

        [Test]
        public void CityStylePreview_ExposesOnlyEligibleOwnedMarkersAndSubmitsSpecialAction()
        {
            var fixture = CreateFixture();
            var player = fixture.Context.State.FindPlayer(1);
            player.DeclaredCityStyles.Add(new CityStyleDeclarationState
            {
                InfluenceMarkerId = "military-marker",
                CityStyleId = CityStyleDatabase.MilitaryIndustrialArea,
                MarkerArea = CityStyleMarkerAreas.Unused,
                UnlockedSpecialActionId = SpecialActionDatabase.MilitaryIndustrialArea,
                RemainingSpecialActionUses = 1
            });
            player.DeclaredCityStyles.Add(new CityStyleDeclarationState
            {
                InfluenceMarkerId = "hub-marker",
                CityStyleId = CityStyleDatabase.SourceStoneIndustrialHub,
                MarkerArea = CityStyleMarkerAreas.UsesTwo,
                UnlockedSpecialActionId = SpecialActionDatabase.SourceStoneIndustrialHub,
                RemainingSpecialActionUses = 2
            });
            player.DeclaredCityStyles.Add(new CityStyleDeclarationState
            {
                InfluenceMarkerId = "relay-marker",
                CityStyleId = CityStyleDatabase.MaterialRelayStation,
                MarkerArea = CityStyleMarkerAreas.Declared,
                RemainingSpecialActionUses = 0
            });

            fixture.Presenter.OpenCityStylePreview(CityStyleDatabase.MilitaryIndustrialArea);

            var markers = fixture.View.CityStyleOptions.CityStyleMarkers;
            var military = FindMarker(markers, "military-marker");
            var hub = FindMarker(markers, "hub-marker");
            var relay = FindMarker(markers, "relay-marker");
            Assert.That(military.CanDragForSpecialAction, Is.True);
            Assert.That(military.SpecialActionId, Is.EqualTo(SpecialActionDatabase.MilitaryIndustrialArea));
            Assert.That(military.LegalDropArea, Is.EqualTo(CityStyleMarkerAreas.Used));
            Assert.That(hub.CanDragForSpecialAction, Is.True);
            Assert.That(hub.LegalDropArea, Is.EqualTo(SpecialActionMarkerAreas.UsedFromTwo));
            Assert.That(relay.CanDragForSpecialAction, Is.False, "物资中继站标记不得拖动发动特殊行动。");
            Assert.That(relay.LegalDropArea, Is.Empty);

            fixture.Commands.NextResult = Rejected("状态已经变化");
            Assert.That(
                fixture.View.CityStyleOptions.TryUseSpecialAction(
                    military.SpecialActionId,
                    military.MarkerId,
                    0,
                    0),
                Is.False);
            Assert.That(fixture.View.Prompt, Is.EqualTo("状态已经变化"));

            fixture.Commands.NextResult = Success(true);
            Assert.That(
                fixture.View.CityStyleOptions.TryUseSpecialAction(
                    military.SpecialActionId,
                    military.MarkerId,
                    0,
                    0),
                Is.True);
            Assert.That(fixture.Commands.LastCommand.Kind, Is.EqualTo(GameCommandKind.UseSpecialAction));
            Assert.That(fixture.Commands.LastCommand.SourceId, Is.EqualTo("military-marker"));
            Assert.That(fixture.Commands.LastCommand.TargetId, Is.EqualTo(SpecialActionDatabase.MilitaryIndustrialArea));
            Assert.That(
                fixture.Commands.LastCommand.Parameters[UseSpecialActionCommandHandler.DeclarationMarkerIdParameter],
                Is.EqualTo("military-marker"));
            Assert.That(
                fixture.Commands.LastCommand.Parameters.ContainsKey(
                    UseSpecialActionCommandHandler.OriginiumAmountParameter),
                Is.False,
                "非复合特殊行动不应携带复合支付参数。");
            Assert.That(
                fixture.Commands.LastCommand.Parameters.ContainsKey(
                    UseSpecialActionCommandHandler.IronAmountParameter),
                Is.False,
                "非复合特殊行动不应携带复合支付参数。");
            Assert.That(fixture.View.RefreshFromStateCount, Is.EqualTo(1));
        }

        [Test]
        public void CompositeSpecialAction_FirstCommandCarriesConfirmedMaterialAllocation()
        {
            var fixture = CreateFixture();
            var player = fixture.Context.State.FindPlayer(1);
            player.Resources.Originium = 2;
            player.Resources.OriginiumShard = 1;
            player.Resources.Iron = 3;
            player.DeclaredCityStyles.Add(new CityStyleDeclarationState
            {
                InfluenceMarkerId = "composite-marker",
                CityStyleId = CityStyleDatabase.CompositePowerSystem,
                MarkerArea = CityStyleMarkerAreas.Unused,
                UnlockedSpecialActionId = SpecialActionDatabase.CompositePowerSystem,
                RemainingSpecialActionUses = 1
            });
            fixture.Presenter.OpenCityStylePreview(CityStyleDatabase.CompositePowerSystem);
            var marker = FindMarker(
                fixture.View.CityStyleOptions.CityStyleMarkers,
                "composite-marker");
            Assert.That(marker.CanDragForSpecialAction, Is.True);
            Assert.That(marker.MaximumOriginiumPayment, Is.EqualTo(2));
            Assert.That(marker.MaximumIronPayment, Is.EqualTo(3));

            Assert.That(
                fixture.View.CityStyleOptions.TryUseSpecialAction(
                    marker.SpecialActionId,
                    marker.MarkerId,
                    2,
                    0),
                Is.False);
            Assert.That(fixture.Commands.LastCommand, Is.Null);
            Assert.That(fixture.View.Prompt, Does.Contain("合计 3"));

            fixture.Commands.NextResult = Success(true);
            Assert.That(
                fixture.View.CityStyleOptions.TryUseSpecialAction(
                    marker.SpecialActionId,
                    marker.MarkerId,
                    2,
                    1),
                Is.True);
            Assert.That(fixture.Commands.LastCommand.Kind, Is.EqualTo(GameCommandKind.UseSpecialAction));
            Assert.That(
                fixture.Commands.LastCommand.Parameters[UseSpecialActionCommandHandler.OriginiumAmountParameter],
                Is.EqualTo("2"));
            Assert.That(
                fixture.Commands.LastCommand.Parameters[UseSpecialActionCommandHandler.IronAmountParameter],
                Is.EqualTo("1"));
        }

        [Test]
        public void ActionPanelStatus_WhenExtraMainActionsRemain_ShowsContinuationAndEarlyEndOptions()
        {
            var fixture = CreateFixture();
            var player = fixture.Context.State.FindPlayer(1);
            player.CompletedMainActionsThisTurn = 1;
            player.RemainingMainActionsThisTurn = 2;
            player.ActedMainActionThisTurn = false;

            var panel = fixture.Presenter.BuildActionPanelViewModel();

            Assert.That(panel.StatusText, Does.Contain("剩余额外主要行动：2"));
            Assert.That(panel.StatusText, Does.Contain("可继续主要/快速行动或结束行动"));
            Assert.That(panel.CanExplore, Is.True);
            Assert.That(panel.CanEndAction, Is.True);
        }

        [Test]
        public void ActionPanelSpecialActionAvailability_UsesPerMarkerOptionInsteadOfGlobalUsedCount()
        {
            var fixture = CreateFixture();
            AddMilitarySpecialActionMarker(fixture, "panel-marker");
            var player = fixture.Context.State.FindPlayer(1);
            player.UsedSpecialActionIdsThisRound.Add(SpecialActionDatabase.CompositePowerSystem);

            Assert.That(fixture.Presenter.BuildActionPanelViewModel().CanUseSpecialAction, Is.True);

            player.UsedSpecialActionIdsThisRound.Add(SpecialActionDatabase.MilitaryIndustrialArea);
            Assert.That(fixture.Presenter.BuildActionPanelViewModel().CanUseSpecialAction, Is.False);
        }

        [Test]
        public void CityStylePreview_DuringBuildDraft_DisablesMarkerAndRejectsDirectSubmission()
        {
            var fixture = CreateFixture();
            var markerState = AddMilitarySpecialActionMarker(fixture, "draft-marker");
            fixture.Presenter.BeginBuildFacilityDrag(FacilityCardDatabase.TradeDistrict);

            fixture.Presenter.OpenCityStylePreview(CityStyleDatabase.MilitaryIndustrialArea);

            var marker = FindMarker(fixture.View.CityStyleOptions.CityStyleMarkers, markerState.InfluenceMarkerId);
            Assert.That(marker.CanDragForSpecialAction, Is.False);
            Assert.That(marker.SpecialActionDisabledReason, Does.Contain("建设草稿"));
            Assert.That(
                fixture.View.CityStyleOptions.TryUseSpecialAction(
                    marker.SpecialActionId,
                    marker.MarkerId,
                    0,
                    0),
                Is.False);
            Assert.That(fixture.Commands.LastCommand, Is.Null);
            Assert.That(fixture.View.Prompt, Does.Contain("建设草稿"));
        }

        [Test]
        public void SpecialActionSubmission_FromPreviousSelectionMode_ClearsModeAndHighlightsAfterSuccess()
        {
            var fixture = CreateFixture();
            var markerState = AddMilitarySpecialActionMarker(fixture, "mode-marker");
            fixture.Presenter.BeginDeployAction();
            Assert.That(fixture.Flow.CurrentMode, Is.EqualTo(InteractionMode.ResolvingDeployTarget));
            Assert.That(fixture.View.Highlights, Is.Not.Empty);
            fixture.Presenter.OpenCityStylePreview(CityStyleDatabase.MilitaryIndustrialArea);
            var marker = FindMarker(fixture.View.CityStyleOptions.CityStyleMarkers, markerState.InfluenceMarkerId);
            Assert.That(marker.CanDragForSpecialAction, Is.False, "旧选择模式中不应向玩家显示可拖标记。");

            fixture.Commands.NextResult = Success(true);
            var accepted = fixture.View.CityStyleOptions.TryUseSpecialAction(
                marker.SpecialActionId,
                marker.MarkerId,
                0,
                0);

            Assert.That(accepted, Is.True);
            Assert.That(fixture.Flow.CurrentMode, Is.EqualTo(InteractionMode.ChooseAction));
            Assert.That(fixture.Influence.Mode, Is.EqualTo(InteractionMode.ChooseAction));
            Assert.That(fixture.View.Highlights, Is.Empty);
            Assert.That(fixture.View.RefreshFromStateCount, Is.EqualTo(1));
        }

        [Test]
        public void CityStylePreview_CloseDuringBuildDraft_RestoresBuildDraft()
        {
            var fixture = CreateFixture();
            fixture.Presenter.BeginBuildFacilityDrag(FacilityCardDatabase.TradeDistrict);
            fixture.Presenter.DropBuildFacility(3);
            Assert.That(fixture.View.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Focused));

            fixture.Presenter.OpenCityStylePreview(CityStyleDatabase.MilitaryIndustrialArea);
            Assert.That(fixture.View.CityStyleOptions, Is.Not.Null);
            for (var i = 0; i < fixture.View.CityStyleOptions.Options.Count; i++)
            {
                Assert.That(fixture.View.CityStyleOptions.Options[i].CanDeclare, Is.False);
            }

            fixture.View.BuildDraft = null;
            fixture.View.CityStyleOptions.Cancel();

            Assert.That(fixture.View.BuildDraft, Is.Not.Null);
            Assert.That(fixture.View.BuildDraft.Phase, Is.EqualTo(BuildFacilityDraftPhase.Focused));
            Assert.That(fixture.View.Prompt, Does.Contain("返回当前建设选择"));
        }

        [Test]
        public void SwitchingWorkflow_CancelsPreviousSelectionAndTracksDynamicMode()
        {
            var fixture = CreateFixture();
            fixture.Presenter.BeginExploreAction();
            Assert.That(fixture.Flow.IsActive(fixture.Exploration), Is.True);
            Assert.That(fixture.Flow.CurrentMode, Is.EqualTo(InteractionMode.ResolvingExploreTarget));

            fixture.Presenter.BeginDeployAction();
            Assert.That(fixture.Exploration.Mode, Is.EqualTo(InteractionMode.ChooseAction));
            Assert.That(fixture.Flow.IsActive(fixture.Influence), Is.True);
            Assert.That(fixture.Flow.CurrentMode, Is.EqualTo(InteractionMode.ResolvingDeployTarget));
        }

        [Test]
        public void EndAction_WhenSentToHost_DoesNotRefreshOrResetWorkflow()
        {
            var fixture = CreateFixture();
            fixture.Context.State.FindPlayer(1).ActedMainActionThisTurn = true;
            fixture.Commands.NextResult = Success(false);
            fixture.Presenter.BeginExploreAction();

            fixture.Presenter.EndCurrentAction();

            Assert.That(fixture.Commands.LastCommand.Kind, Is.EqualTo(GameCommandKind.EndAction));
            Assert.That(fixture.View.RefreshFromStateCount, Is.Zero);
            Assert.That(fixture.View.Prompt, Does.Contain("等待确认"));
        }

        [Test]
        public void EndAction_SinglePlayerActionRound1_ContinuesIntoActionRound2WithoutWaiting()
        {
            var fixture = CreateFixture();
            fixture.Context.State.Players.RemoveAt(1);
            fixture.Context.State.FindPlayer(1).ActedMainActionThisTurn = true;
            ApplyEndActionCommandsLocally(fixture);

            fixture.Presenter.EndCurrentAction();

            var panel = fixture.Presenter.BuildActionPanelViewModel();
            Assert.That(fixture.Context.State.Phase, Is.EqualTo(GamePhase.ActionRound2));
            Assert.That(fixture.Context.State.ActionRound, Is.EqualTo(2));
            Assert.That(fixture.Context.State.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(panel.Mode, Is.EqualTo(InteractionMode.ChooseAction));
            Assert.That(panel.IsWaitingForOtherPlayers, Is.False);
            Assert.That(panel.CanMoveCity, Is.True);
            Assert.That(panel.CanEndAction, Is.False);
            Assert.That(fixture.View.Prompt, Is.EqualTo(panel.StatusText));
            Assert.That(fixture.View.Prompt, Does.Not.Contain("等待下一位玩家"));
        }

        [Test]
        public void EndAction_SinglePlayerActionRound2_EntersResourceCollectionPrompt()
        {
            var fixture = CreateFixture();
            fixture.Context.State.Players.RemoveAt(1);
            fixture.Context.State.Phase = GamePhase.ActionRound2;
            fixture.Context.State.ActionRound = 2;
            fixture.Context.State.FindPlayer(1).ActedMainActionThisTurn = true;
            ConfigureCollectionTarget(fixture, 1);
            fixture.View.RefreshFromStateAction = fixture.Presenter.BeginResourceCollection;
            ApplyEndActionCommandsLocally(fixture);

            fixture.Presenter.EndCurrentAction();

            var panel = fixture.Presenter.BuildActionPanelViewModel();
            Assert.That(fixture.Context.State.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            Assert.That(fixture.Context.State.FindPlayer(1).HasCollectedResourcesThisRound, Is.False);
            Assert.That(fixture.Resource.CurrentQuery, Is.Not.Null);
            Assert.That(fixture.Resource.CurrentQuery.IsValid, Is.True);
            Assert.That(fixture.Resource.SelectedLocationIds, Is.EquivalentTo(new[] { "B" }));
            Assert.That(
                fixture.View.Highlights.Exists(highlight =>
                    highlight.TargetId == "B" &&
                    highlight.Semantic == WorkflowHighlightSemantic.CollectionSelected),
                Is.True);
            Assert.That(panel.Mode, Is.EqualTo(InteractionMode.ResolvingResourceCollection));
            Assert.That(panel.IsWaitingForOtherPlayers, Is.False);
            Assert.That(panel.CanEndAction, Is.True);
            Assert.That(fixture.View.Prompt, Is.EqualTo(panel.StatusText));
            Assert.That(fixture.View.Prompt, Does.StartWith("采集阶段："));
            Assert.That(fixture.View.Prompt, Does.Not.Contain("已提交采集"));
        }

        [Test]
        public void EndAction_ActionRound2_WithUnpaidToll_ShowsCollectionPaymentPromptAndRouteHighlight()
        {
            var fixture = CreateFixture();
            fixture.Context.State.Phase = GamePhase.ActionRound2;
            fixture.Context.State.ActionRound = 2;
            fixture.Context.State.CurrentPlayerId = 2;
            fixture.Context.LocalPlayerId = 2;
            fixture.Context.State.FindPlayer(1).ActedMainActionThisTurn = true;
            fixture.Context.State.FindPlayer(2).ActedMainActionThisTurn = true;
            ConfigureCollectionTarget(fixture, 2);
            fixture.View.RefreshFromStateAction = fixture.Presenter.BeginResourceCollection;
            ApplyEndActionCommandsLocally(fixture);

            fixture.Presenter.EndCurrentAction();

            var panel = fixture.Presenter.BuildActionPanelViewModel();
            Assert.That(fixture.Context.State.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            Assert.That(fixture.Context.State.CurrentPlayerId, Is.EqualTo(1));
            Assert.That(fixture.Context.LocalPlayerId, Is.EqualTo(1));
            Assert.That(fixture.Context.State.FindPlayer(1).HasCollectedResourcesThisRound, Is.False);
            Assert.That(fixture.Context.State.FindPlayer(2).HasCollectedResourcesThisRound, Is.False);
            Assert.That(fixture.Resource.CurrentQuery, Is.Not.Null);
            Assert.That(fixture.Resource.CurrentQuery.IsValid, Is.True);
            Assert.That(
                fixture.View.Highlights.Exists(highlight =>
                    highlight.TargetId == "R1" &&
                    highlight.Semantic == WorkflowHighlightSemantic.CollectionPaymentRequired),
                Is.True);
            Assert.That(panel.StatusText, Does.StartWith("采集阶段："));
            Assert.That(panel.StatusText, Does.Contain("支付路费"));
            Assert.That(panel.StatusText, Does.Not.Contain("已提交采集"));
            Assert.That(fixture.View.Prompt, Is.EqualTo(panel.StatusText));
        }

        [Test]
        public void CollectResource_AfterActualMultiplayerSubmission_ShowsSubmittedWaitingStatus()
        {
            var fixture = CreateFixture();
            fixture.Context.ControlsCurrentPlayerLocally = false;
            fixture.Context.State.Phase = GamePhase.ResourceCollection;
            fixture.Context.State.ActionRound = 0;
            fixture.Context.State.FindPlayer(1).HasCollectedResourcesThisRound = false;
            fixture.Context.State.FindPlayer(2).HasCollectedResourcesThisRound = false;
            ConfigureCollectionTarget(fixture, 1);
            fixture.View.RefreshFromStateAction = () =>
            {
                fixture.Presenter.SynchronizeFromState();
                fixture.Presenter.BeginResourceCollection();
            };
            ApplyCollectionCommandsLocally(fixture);
            fixture.Presenter.BeginResourceCollection();

            fixture.Presenter.EndCurrentAction();

            var panel = fixture.Presenter.BuildActionPanelViewModel();
            Assert.That(fixture.Commands.LastCommand.Kind, Is.EqualTo(GameCommandKind.CollectResource));
            Assert.That(fixture.Context.State.FindPlayer(1).HasCollectedResourcesThisRound, Is.True);
            Assert.That(fixture.Context.State.FindPlayer(2).HasCollectedResourcesThisRound, Is.False);
            Assert.That(fixture.Context.State.Phase, Is.EqualTo(GamePhase.ResourceCollection));
            Assert.That(panel.Mode, Is.EqualTo(InteractionMode.WaitingForNextPlayer));
            Assert.That(panel.IsWaitingForOtherPlayers, Is.True);
            Assert.That(panel.CanEndAction, Is.False);
            Assert.That(panel.StatusText, Does.Contain("已提交采集"));
            Assert.That(panel.StatusText, Does.Contain("等待其他玩家"));
        }

        [Test]
        public void EndAction_NetworkMultiplayerStillWaitsForRemoteCurrentPlayer()
        {
            var fixture = CreateFixture();
            fixture.Context.ControlsCurrentPlayerLocally = false;
            fixture.Context.State.FindPlayer(1).ActedMainActionThisTurn = true;
            ApplyEndActionCommandsLocally(fixture);

            fixture.Presenter.EndCurrentAction();

            var panel = fixture.Presenter.BuildActionPanelViewModel();
            Assert.That(fixture.Context.State.CurrentPlayerId, Is.EqualTo(2));
            Assert.That(fixture.Context.LocalPlayerId, Is.EqualTo(1));
            Assert.That(panel.Mode, Is.EqualTo(InteractionMode.WaitingForNextPlayer));
            Assert.That(panel.IsWaitingForOtherPlayers, Is.True);
            Assert.That(panel.CanMoveCity, Is.False);
            Assert.That(fixture.View.Prompt, Is.EqualTo(panel.StatusText));
            Assert.That(fixture.View.Prompt, Does.Contain("等待玩家 2 行动"));
        }

        private static void ApplyEndActionCommandsLocally(Fixture fixture)
        {
            var handler = new EndActionCommandHandler();
            fixture.Commands.SubmitHandler = command =>
            {
                var result = handler.Handle(fixture.Context.State, command);
                return new WorkflowSubmissionResult(result, result.Succeeded);
            };
        }

        private static CityStyleMarkerViewModel FindMarker(
            IReadOnlyList<CityStyleMarkerViewModel> markers,
            string markerId)
        {
            for (var i = 0; i < markers.Count; i++)
            {
                if (markers[i] != null && markers[i].MarkerId == markerId)
                {
                    return markers[i];
                }
            }

            Assert.Fail("找不到城市样式标记：" + markerId);
            return null;
        }

        private static CityStyleDeclarationState AddMilitarySpecialActionMarker(
            Fixture fixture,
            string markerId)
        {
            var marker = new CityStyleDeclarationState
            {
                InfluenceMarkerId = markerId,
                CityStyleId = CityStyleDatabase.MilitaryIndustrialArea,
                MarkerArea = CityStyleMarkerAreas.Unused,
                UnlockedSpecialActionId = SpecialActionDatabase.MilitaryIndustrialArea,
                RemainingSpecialActionUses = 1
            };
            fixture.Context.State.FindPlayer(1).DeclaredCityStyles.Add(marker);
            return marker;
        }

        private static void ApplyCollectionCommandsLocally(Fixture fixture)
        {
            var handler = new CollectResourceCommandHandler(
                new ResourceCollectionService(fixture.MapQuery));
            fixture.Commands.SubmitHandler = command =>
            {
                var result = handler.Handle(fixture.Context.State, command);
                return new WorkflowSubmissionResult(result, result.Succeeded);
            };
        }

        private static void ConfigureCollectionTarget(Fixture fixture, int routeOwnerPlayerId)
        {
            fixture.Context.State.Map.ResourceTokens.Add(new ResourceTokenState
            {
                LocationId = "B",
                ResourceType = ResourceType.Iron,
                Amount = 1
            });
            fixture.Context.State.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = 1,
                SlotId = InfluenceService.GetLocationSlotId("B", 0),
                LocationId = "B"
            });
            fixture.Context.State.Map.Influences.Add(new InfluencePlacement
            {
                PlayerId = routeOwnerPlayerId,
                SlotId = InfluenceService.GetRouteSlotId("R1", 0),
                RouteId = "R1"
            });
        }

        private static Fixture CreateFixture()
        {
            var map = new GameMapDefinition
            {
                MapId = "turn-presenter",
                Locations =
                {
                    new MapLocationDefinition { LocationId = "A", CanDockCity = true, InfluenceSlotCount = 1 },
                    new MapLocationDefinition { LocationId = "B", CanDockCity = true, InfluenceSlotCount = 1 }
                },
                Routes =
                {
                    new MapRouteDefinition
                    {
                        RouteId = "R1",
                        FromLocationId = "A",
                        ToLocationId = "B",
                        InfluenceSlotCount = 1
                    }
                }
            };
            var context = new FakeContext
            {
                State = new GameState
                {
                    Phase = GamePhase.ActionRound1,
                    Round = 1,
                    MaxRounds = 8,
                    ActionRound = 1,
                    StartPlayerId = 1,
                    CurrentPlayerId = 1,
                    Players =
                    {
                        new PlayerState
                        {
                            PlayerId = 1,
                            CityLocationId = "A",
                            Color = PlayerColor.Red,
                            Resources = new ResourceSet { GoldVoucher = 100 }
                        },
                        new PlayerState { PlayerId = 2, CityLocationId = string.Empty, Color = PlayerColor.Blue }
                    },
                    Decks =
                    {
                        FacilitySupply = { FacilityCardDatabase.TradeDistrict }
                    }
                }
            };
            var commands = new FakeCommandPort { NextResult = Success(true) };
            var view = new FakeView();
            var flow = new InteractionFlowCoordinator();
            flow.ResetToChooseAction();
            var mapQuery = new MapQueryService(map);
            var influenceService = new InfluenceService(mapQuery);
            var resource = new ResourceCollectionPresenter(
                context, commands, view, mapQuery, new ResourceCollectionService(mapQuery));
            var influence = new InfluenceActionPresenter(
                context, commands, view, mapQuery, influenceService);
            var exploration = new ExplorationEventPresenter(
                context, commands, view, mapQuery, new ExplorationService(mapQuery), influenceService);
            return new Fixture
            {
                Context = context,
                Commands = commands,
                View = view,
                Flow = flow,
                MapQuery = mapQuery,
                Resource = resource,
                Influence = influence,
                Exploration = exploration,
                Presenter = new TurnActionPresenter(
                    context, commands, view, flow, mapQuery, resource, influence, exploration)
            };
        }

        private static WorkflowSubmissionResult Success(bool appliedLocally)
        {
            return new WorkflowSubmissionResult(
                CommandResult.SuccessResult(new List<GameEvent>(), "ok"), appliedLocally);
        }

        private static WorkflowSubmissionResult Rejected(string reason)
        {
            return new WorkflowSubmissionResult(
                CommandResult.Invalid(ValidationResult.Failure(CommandErrorCode.InvalidTarget, reason)), false);
        }

        private sealed class Fixture
        {
            public FakeContext Context;
            public FakeCommandPort Commands;
            public FakeView View;
            public InteractionFlowCoordinator Flow;
            public MapQueryService MapQuery;
            public ResourceCollectionPresenter Resource;
            public InfluenceActionPresenter Influence;
            public ExplorationEventPresenter Exploration;
            public TurnActionPresenter Presenter;
        }

        private sealed class FakeContext : IWritableGameplayContext
        {
            public GameState State;
            public int LocalPlayerId { get; set; } = 1;
            public bool ControlsCurrentPlayerLocally { get; set; } = true;
            public GameState CurrentState { get { return State; } }
            public void SetLocalPlayerId(int playerId) { LocalPlayerId = playerId; }
        }

        private sealed class FakeCommandPort : IGameCommandPort
        {
            public GameCommand LastCommand;
            public WorkflowSubmissionResult NextResult;
            public System.Func<GameCommand, WorkflowSubmissionResult> SubmitHandler;

            public WorkflowSubmissionResult Submit(GameCommand command)
            {
                LastCommand = command;
                return SubmitHandler == null ? NextResult : SubmitHandler(command);
            }
        }

        private sealed class FakeView : ITurnActionView, IResourceCollectionView,
            IInfluenceActionView, IExplorationEventView
        {
            public readonly List<WorkflowHighlight> Highlights = new List<WorkflowHighlight>();
            public string Prompt = string.Empty;
            public string CompletedAction = string.Empty;
            public int RefreshFromStateCount;
            public BuildFacilityDraftViewModel BuildDraft;
            public CityStyleOptionsViewModel CityStyleOptions;
            public System.Action RefreshFromStateAction;

            public void ShowPrompt(string message) { Prompt = message; }
            public void SetHighlights(IReadOnlyList<WorkflowHighlight> highlights)
            {
                Highlights.Clear();
                if (highlights != null) Highlights.AddRange(highlights);
            }
            public void ClearHighlights() { Highlights.Clear(); }
            public void RefreshFromState()
            {
                RefreshFromStateCount += 1;
                RefreshFromStateAction?.Invoke();
            }
            public void RefreshInformation() { }
            public void RefreshActionPanel() { }
            public void ShowPendingChoice() { }
            public void CompleteMainActionPresentation(string actionName) { CompletedAction = actionName; }
            public void ShowBuildFacilityDraft(BuildFacilityDraftViewModel viewModel) { BuildDraft = viewModel; }
            public void HideBuildFacilityDraft() { BuildDraft = null; }
            public void ShowCityStyleOptions(CityStyleOptionsViewModel viewModel) { CityStyleOptions = viewModel; }
            public void ShowRoutePaymentOptions(string routeId, int cost, IReadOnlyList<int> recipients) { }
            public string GetPlayerDisplayName(int playerId) { return "Player " + playerId; }
            public void RefreshSelectionView() { }
            public void SetInteractionMode(InteractionMode mode) { }
            public void ShowDispatchDecision(DispatchDecisionViewModel viewModel) { }
            public void HideDispatchDecision() { }
            public void RefreshInfluencePreview(bool pending, string source, string target) { }
            public void CompleteAction(string actionName) { CompletedAction = actionName; }
            public void ShowExplorePathOptions(ExplorePathOptionsViewModel viewModel) { }
            public void ShowExplorePaymentOptions(ExplorePaymentOptionsViewModel viewModel) { }
            public void ShowEventCardOptions(EventCardOptionsViewModel viewModel) { }
            public void CollapseEventOptions() { }
            public void HideEventOptions() { }
            public void RefreshResourceAndInfluence() { }
        }
    }
}
