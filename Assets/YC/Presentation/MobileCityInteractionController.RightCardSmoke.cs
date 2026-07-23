using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using YC.Application.Sessions;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.State;
using UnityEngine;
using UnityEngine.UI;

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
        private const string SharedCityStyleMirrorSmokeArg = "--yc-dev-shared-style-mirror-smoke";
        private const string SharedCityStyleObserverArg = "--yc-dev-shared-style-mirror-observer";
        private const string SharedCityStyleScreenshotPrefix = "--yc-dev-shared-style-screenshot=";

        private bool useRightCardSmokeState;
        private bool prepareSharedCityStyleSmokeState;
        private bool observeSharedCityStyleMirrorState;
        private bool sharedCityStyleScreenshotRequested;
        private string sharedCityStyleScreenshotPath = string.Empty;
        private string lastSharedCityStyleObservation = string.Empty;
        private RightCardSmokeDialog rightCardSmokeDialog = RightCardSmokeDialog.None;

        private void ReadRightCardSmokeCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            prepareSharedCityStyleSmokeState = HasCommandLineArg(args, SharedCityStyleMirrorSmokeArg);
            observeSharedCityStyleMirrorState =
                prepareSharedCityStyleSmokeState ||
                HasCommandLineArg(args, SharedCityStyleObserverArg);
            sharedCityStyleScreenshotPath = GetCommandLineValue(args, SharedCityStyleScreenshotPrefix);
            useRightCardSmokeState = HasCommandLineArg(args, RightCardSmokeArg) ||
                                     HasCommandLineArg(args, RightCardSmokeBuildArg) ||
                                     HasCommandLineArg(args, RightCardSmokeBuildFocusArg) ||
                                     HasCommandLineArg(args, RightCardSmokeBuildSummaryArg) ||
                                     HasCommandLineArg(args, RightCardSmokeBuildResultArg) ||
                                     HasCommandLineArg(args, RightCardSmokeDeclareArg) ||
                                     HasCommandLineArg(args, RightCardSmokeNoDialogArg) ||
                                     prepareSharedCityStyleSmokeState;
            if (!useRightCardSmokeState)
            {
                return;
            }

            rightCardSmokeDialog = RightCardSmokeDialog.Build;
            if (HasCommandLineArg(args, RightCardSmokeDeclareArg))
            {
                rightCardSmokeDialog = RightCardSmokeDialog.Declare;
            }
            else if (prepareSharedCityStyleSmokeState)
            {
                rightCardSmokeDialog = RightCardSmokeDialog.None;
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
            ObserveSharedCityStyleMirrorState();
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

        private static string GetCommandLineValue(string[] args, string prefix)
        {
            if (args == null || string.IsNullOrEmpty(prefix))
            {
                return string.Empty;
            }

            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i].Substring(prefix.Length).Trim();
                }
            }

            return string.Empty;
        }

        private void ObserveSharedCityStyleMirrorState()
        {
            if (!observeSharedCityStyleMirrorState || session == null || session.State == null)
            {
                return;
            }

            var markerIds = new List<string>();
            var playersWithDeclaration = 0;
            for (var i = 0; i < session.State.Players.Count; i++)
            {
                var player = session.State.Players[i];
                var foundForPlayer = false;
                if (player != null && player.DeclaredCityStyles != null)
                {
                    for (var declarationIndex = 0;
                         declarationIndex < player.DeclaredCityStyles.Count;
                         declarationIndex++)
                    {
                        var declaration = player.DeclaredCityStyles[declarationIndex];
                        if (declaration == null ||
                            declaration.CityStyleId != RightCardSmokeStateFactory.SharedCityStyleId)
                        {
                            continue;
                        }

                        foundForPlayer = true;
                        markerIds.Add(declaration.InfluenceMarkerId);
                    }
                }

                if (foundForPlayer)
                {
                    playersWithDeclaration++;
                }
            }

            markerIds.Sort(StringComparer.Ordinal);
            var observation =
                "local=P" + localPlayerId +
                " players=" + session.State.Players.Count +
                " sharedStylePlayers=" + playersWithDeclaration +
                " markerCount=" + markerIds.Count +
                " markerIds=" + string.Join(",", markerIds.ToArray());
            if (observation != lastSharedCityStyleObservation)
            {
                lastSharedCityStyleObservation = observation;
                Debug.Log("[SharedStyleMirrorObserver] " + observation);
            }

            if (!sharedCityStyleScreenshotRequested &&
                !string.IsNullOrEmpty(sharedCityStyleScreenshotPath) &&
                playersWithDeclaration == 4 &&
                markerIds.Count == 4)
            {
                sharedCityStyleScreenshotRequested = true;
                StartCoroutine(CaptureSharedCityStyleScreenshot());
            }
        }

        private IEnumerator CaptureSharedCityStyleScreenshot()
        {
            yield return new WaitForEndOfFrame();
            Canvas.ForceUpdateCanvases();

            var markerRects = FindSharedCityStyleMarkerRects();
            var overlapCount = CountOverlaps(markerRects);
            var positions = new StringBuilder();
            for (var i = 0; i < markerRects.Count; i++)
            {
                if (i > 0)
                {
                    positions.Append(";");
                }

                positions.Append(markerRects[i].Name)
                    .Append("@")
                    .Append(markerRects[i].Bounds.center.x.ToString("F1"))
                    .Append(",")
                    .Append(markerRects[i].Bounds.center.y.ToString("F1"));
            }

            var fullPath = Path.GetFullPath(sharedCityStyleScreenshotPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            ScreenCapture.CaptureScreenshot(fullPath);
            Debug.Log(
                "[SharedStyleMirrorScreenshot] local=P" + localPlayerId +
                " uiMarkerCount=" + markerRects.Count +
                " overlapCount=" + overlapCount +
                " positions=" + positions +
                " path=" + fullPath);
        }

        private static List<SharedCityStyleMarkerRect> FindSharedCityStyleMarkerRects()
        {
            var result = new List<SharedCityStyleMarkerRect>();
            var rects = FindObjectsOfType<RectTransform>();
            for (var i = 0; i < rects.Length; i++)
            {
                var rect = rects[i];
                if (rect == null ||
                    !rect.gameObject.activeInHierarchy ||
                    !rect.name.StartsWith("样式影响力 玩家", StringComparison.Ordinal))
                {
                    continue;
                }

                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                var minimumX = Mathf.Min(corners[0].x, corners[2].x);
                var minimumY = Mathf.Min(corners[0].y, corners[2].y);
                var maximumX = Mathf.Max(corners[0].x, corners[2].x);
                var maximumY = Mathf.Max(corners[0].y, corners[2].y);
                result.Add(new SharedCityStyleMarkerRect(
                    rect.name,
                    Rect.MinMaxRect(minimumX, minimumY, maximumX, maximumY)));
            }

            result.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
            return result;
        }

        private static int CountOverlaps(IReadOnlyList<SharedCityStyleMarkerRect> markers)
        {
            var overlaps = 0;
            for (var i = 0; i < markers.Count; i++)
            {
                for (var j = i + 1; j < markers.Count; j++)
                {
                    if (markers[i].Bounds.Overlaps(markers[j].Bounds))
                    {
                        overlaps++;
                    }
                }
            }

            return overlaps;
        }

        private sealed class SharedCityStyleMarkerRect
        {
            public SharedCityStyleMarkerRect(string name, Rect bounds)
            {
                Name = name ?? string.Empty;
                Bounds = bounds;
            }

            public string Name { get; private set; }
            public Rect Bounds { get; private set; }
        }
    }
}
