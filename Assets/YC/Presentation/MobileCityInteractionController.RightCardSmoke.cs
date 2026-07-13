using System;
using YC.Domain.Facilities;
using UnityEngine;

namespace YC.Presentation
{
    public sealed partial class MobileCityInteractionController
    {
        private enum RightCardSmokeDialog
        {
            None,
            Build,
            BuildFocus,
            BuildSummary,
            BuildResult,
            Declare
        }

        private const string RightCardSmokeArg = "--yc-dev-right-card-smoke";
        private const string RightCardSmokeBuildArg = "--yc-dev-right-card-smoke-build";
        private const string RightCardSmokeBuildFocusArg = "--yc-dev-right-card-smoke-build-focus";
        private const string RightCardSmokeBuildSummaryArg = "--yc-dev-right-card-smoke-build-summary";
        private const string RightCardSmokeBuildResultArg = "--yc-dev-right-card-smoke-build-result";
        private const string RightCardSmokeDeclareArg = "--yc-dev-right-card-smoke-declare";
        private const string RightCardSmokeNoDialogArg = "--yc-dev-right-card-smoke-no-dialog";

        private bool useRightCardSmokeState;
        private RightCardSmokeDialog rightCardSmokeDialog = RightCardSmokeDialog.None;

        private void ReadRightCardSmokeCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            useRightCardSmokeState = HasCommandLineArg(args, RightCardSmokeArg) ||
                                     HasCommandLineArg(args, RightCardSmokeBuildArg) ||
                                     HasCommandLineArg(args, RightCardSmokeBuildFocusArg) ||
                                     HasCommandLineArg(args, RightCardSmokeBuildSummaryArg) ||
                                     HasCommandLineArg(args, RightCardSmokeBuildResultArg) ||
                                     HasCommandLineArg(args, RightCardSmokeDeclareArg) ||
                                     HasCommandLineArg(args, RightCardSmokeNoDialogArg);
            if (!useRightCardSmokeState)
            {
                return;
            }

            rightCardSmokeDialog = RightCardSmokeDialog.Build;
            if (HasCommandLineArg(args, RightCardSmokeDeclareArg))
            {
                rightCardSmokeDialog = RightCardSmokeDialog.Declare;
            }
            else if (HasCommandLineArg(args, RightCardSmokeBuildSummaryArg))
            {
                rightCardSmokeDialog = RightCardSmokeDialog.BuildSummary;
            }
            else if (HasCommandLineArg(args, RightCardSmokeBuildResultArg))
            {
                rightCardSmokeDialog = RightCardSmokeDialog.BuildResult;
            }
            else if (HasCommandLineArg(args, RightCardSmokeBuildFocusArg))
            {
                rightCardSmokeDialog = RightCardSmokeDialog.BuildFocus;
            }
            else if (HasCommandLineArg(args, RightCardSmokeNoDialogArg))
            {
                rightCardSmokeDialog = RightCardSmokeDialog.None;
            }
        }

        private void PrepareRightCardSmokePresentation()
        {
            if (!useRightCardSmokeState || session == null || session.State == null)
            {
                return;
            }

            if (session.State.FindPlayer(localPlayerId) == null)
            {
                Debug.LogWarning("Right card smoke setup skipped because no local player exists.");
                return;
            }

            flowCoordinator.ResetToChooseAction();
            turnActionPresenter.SynchronizeFromState();
            ClearPendingDispatch();
            workflowView.ClearHighlights();
            eventChoiceDialog.Hide();
            SynchronizeInteractionFromState();

            if (rightCardSmokeDialog == RightCardSmokeDialog.Declare)
            {
                OnDeclareCityStyleClicked();
            }
            else if (rightCardSmokeDialog == RightCardSmokeDialog.Build)
            {
                OnBuildActionClicked();
            }
            else if (rightCardSmokeDialog == RightCardSmokeDialog.BuildFocus ||
                     rightCardSmokeDialog == RightCardSmokeDialog.BuildSummary ||
                     rightCardSmokeDialog == RightCardSmokeDialog.BuildResult)
            {
                PrepareRightCardSmokeBuildDraft(
                    rightCardSmokeDialog != RightCardSmokeDialog.BuildFocus,
                    rightCardSmokeDialog == RightCardSmokeDialog.BuildResult);
            }

            Debug.Log("Right card smoke setup ready. Dialog=" + rightCardSmokeDialog + ", PlayerId=" + localPlayerId + ".");
        }

        private void PrepareRightCardSmokeBuildDraft(bool showSummary, bool confirm = false)
        {
            OnBuildActionClicked();
            var model = turnActionPresenter.BuildBuildFacilityDraftViewModel();
            if (model == null || model.Options.Count <= 0)
            {
                return;
            }

            var facilityId = model.Options[confirm && model.Options.Count > 2 ? 2 : 0].FacilityId;
            turnActionPresenter.BeginBuildFacilityDrag(facilityId);
            model = turnActionPresenter.BuildBuildFacilityDraftViewModel();
            if (model == null || model.LegalSlotIndexes.Count <= 0)
            {
                return;
            }

            turnActionPresenter.DropBuildFacility(model.LegalSlotIndexes[0]);
            if (showSummary)
            {
                turnActionPresenter.SelectBuildFacilityPayment(BuildFacilityService.PaymentModeGold);
            }

            if (confirm)
            {
                turnActionPresenter.ConfirmBuildFacility();
            }

            buildFacilityInteraction?.Synchronize();
        }

        private static bool HasCommandLineArg(string[] args, string expectedValue)
        {
            if (args == null)
            {
                return false;
            }

            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], expectedValue, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
