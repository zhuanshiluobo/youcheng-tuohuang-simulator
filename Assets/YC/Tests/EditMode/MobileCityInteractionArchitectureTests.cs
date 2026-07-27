using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class MobileCityInteractionArchitectureTests
    {
        private static string AssetsPath => UnityEngine.Application.dataPath;

        [Test]
        public void WorkflowAssembly_IsEngineIndependent()
        {
            var path = Path.Combine(AssetsPath, "YC/Presentation/Workflows/YC.Presentation.Workflows.asmdef");
            StringAssert.Contains("\"noEngineReferences\": true", File.ReadAllText(path));
        }

        [TestCase(typeof(ResourceCollectionPresenter))]
        [TestCase(typeof(InfluenceActionPresenter))]
        [TestCase(typeof(ExplorationEventPresenter))]
        [TestCase(typeof(TurnActionPresenter))]
        [TestCase(typeof(BuildInteraction))]
        [TestCase(typeof(MoveInteraction))]
        [TestCase(typeof(DeployInteraction))]
        [TestCase(typeof(DispatchInteraction))]
        [TestCase(typeof(ExploreInteraction))]
        [TestCase(typeof(CityStyleInteraction))]
        [TestCase(typeof(TurnActionPanelPresenter))]
        public void WorkflowPresenter_IsNotAMonoBehaviour_AndHasNoUnityReference(Type presenterType)
        {
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(presenterType), Is.False);
            foreach (var reference in presenterType.Assembly.GetReferencedAssemblies())
            {
                Assert.That(reference.Name.StartsWith("UnityEngine", StringComparison.Ordinal), Is.False);
            }
        }

        [Test]
        public void BuildInteraction_OwnsBuildStateAndPresenterKeepsOnlyThinCompatibilityApi()
        {
            var workflowRoot = Path.Combine(AssetsPath, "YC/Presentation/Workflows");
            var buildSource = File.ReadAllText(Path.Combine(workflowRoot, "BuildInteraction.cs"));
            var presenterSource = File.ReadAllText(
                Path.Combine(workflowRoot, "TurnActionPresenter.cs"));

            Assert.That(typeof(InteractionBase).IsAssignableFrom(typeof(BuildInteraction)), Is.True);
            Assert.That(
                typeof(TurnActionPresenter).GetProperty("BuildInteraction").PropertyType,
                Is.EqualTo(typeof(BuildInteraction)));
            StringAssert.Contains(
                "new BuildFacilitySelectionController()",
                buildSource);
            StringAssert.Contains(
                "public void Dispatch(BuildFacilityIntent intent)",
                buildSource);
            StringAssert.Contains(
                "public override void NotifyCommandSettled(string commandId)",
                buildSource);
            StringAssert.Contains(
                "RejectMutationWhileSubmitting()",
                buildSource);
            StringAssert.Contains(
                "BuildInteraction.SynchronizeFromState();",
                presenterSource);
            StringAssert.DoesNotContain(
                "BuildFacilitySelectionController",
                presenterSource);
            StringAssert.DoesNotContain(
                "DispatchBuildFacilityIntent",
                presenterSource);

            AssertThinDelegate(
                presenterSource,
                "public void BeginBuildAction()",
                "BuildInteraction.Begin();");
            AssertThinDelegate(
                presenterSource,
                "public void BeginBuildFacilityDrag(string facilityId)",
                "BuildInteraction.BeginDrag(facilityId);");
            AssertThinDelegate(
                presenterSource,
                "public void DropBuildFacility(int cityBoardSlotIndex)",
                "BuildInteraction.Drop(cityBoardSlotIndex);");
            AssertThinDelegate(
                presenterSource,
                "public void RejectBuildFacilityDrop()",
                "BuildInteraction.RejectDrop();");
            AssertThinDelegate(
                presenterSource,
                "public void BeginGhostBuildFacilityDrag()",
                "BuildInteraction.BeginGhostDrag();");
            AssertThinDelegate(
                presenterSource,
                "public bool HandleBuildFacilityEscape()",
                "return BuildInteraction.HandleEscape();");
            AssertThinDelegate(
                presenterSource,
                "public void SelectBuildFacilityPayment(string paymentMode)",
                "BuildInteraction.SelectPayment(paymentMode);");
            AssertThinDelegate(
                presenterSource,
                "public void BackToBuildFacilityPayment()",
                "BuildInteraction.BackToPayment();");
            AssertThinDelegate(
                presenterSource,
                "public void CancelBuildFacility()",
                "BuildInteraction.CancelExplicitly();");
            AssertThinDelegate(
                presenterSource,
                "public void ConfirmBuildFacility()",
                "BuildInteraction.Confirm();");
            AssertThinDelegate(
                presenterSource,
                "public BuildFacilityDraftViewModel BuildBuildFacilityDraftViewModel()",
                "return BuildInteraction.BuildDraftViewModel();");
            AssertThinDelegate(
                presenterSource,
                "public BuildFacilityAvailabilityViewModel BuildBuildFacilityAvailabilityViewModel()",
                "return BuildInteraction.BuildAvailabilityViewModel();");
        }

        [Test]
        public void MoveInteraction_OwnsInitialPlacementAndMoveState()
        {
            var workflowRoot = Path.Combine(AssetsPath, "YC/Presentation/Workflows");
            var moveSource = File.ReadAllText(
                Path.Combine(workflowRoot, "MoveInteraction.cs"));
            var presenterSource = File.ReadAllText(
                Path.Combine(workflowRoot, "TurnActionPresenter.cs"));

            Assert.That(
                typeof(InteractionBase).IsAssignableFrom(typeof(MoveInteraction)),
                Is.True);
            Assert.That(
                typeof(TurnActionPresenter).GetProperty("MoveInteraction").PropertyType,
                Is.EqualTo(typeof(MoveInteraction)));
            StringAssert.Contains("private enum MoveStage", moveSource);
            StringAssert.Contains("private void PresentMoveTargets()", moveSource);
            StringAssert.Contains(
                "private bool CanUseInitialPlacementLocation(string locationId)",
                moveSource);
            StringAssert.Contains(
                "private bool IsOccupiedByAnotherCity(string locationId)",
                moveSource);
            StringAssert.DoesNotContain("InteractionMode.Resolving", moveSource);
            StringAssert.DoesNotContain("TurnActionStage", presenterSource);
            StringAssert.DoesNotContain("PresentMoveTargets", presenterSource);
            StringAssert.DoesNotContain("CanUseInitialPlacementLocation", presenterSource);
            StringAssert.DoesNotContain("IsOccupiedByAnotherCity", presenterSource);

            AssertThinDelegate(
                presenterSource,
                "public InteractionMode Mode",
                "get { return MoveInteraction.Mode; }");
            AssertThinDelegate(
                presenterSource,
                "public bool IsSelectingMoveTarget",
                "get { return MoveInteraction.IsSelectingMoveTarget; }");
            AssertThinDelegate(
                presenterSource,
                "public bool IsAwaitingInitialPlacement",
                "get { return MoveInteraction.IsAwaitingInitialPlacement; }");
            AssertThinDelegate(
                presenterSource,
                "public void Activate()",
                "MoveInteraction.Activate();");
            AssertThinDelegate(
                presenterSource,
                "public void Cancel()",
                "MoveInteraction.Cancel();");
            AssertThinDelegate(
                presenterSource,
                "public bool IsLocalPlayersTurn()",
                "return MoveInteraction.IsLocalPlayersTurn();");
            AssertThinDelegate(
                presenterSource,
                "public void PlaceInitialCity(string locationId)",
                "MoveInteraction.PlaceInitialCity(locationId);");
            AssertThinDelegate(
                presenterSource,
                "public IReadOnlyList<WorkflowHighlight> BuildInitialPlacementHighlights()",
                "return MoveInteraction.BuildInitialPlacementHighlights();");
            AssertThinDelegate(
                presenterSource,
                "public void BeginMoveAction()",
                "MoveInteraction.Begin();");
            AssertThinDelegate(
                presenterSource,
                "public void MoveCity(string locationId)",
                "MoveInteraction.Move(locationId);");
            AssertThinDelegate(
                presenterSource,
                "public void RestoreMovePresentation()",
                "MoveInteraction.RestorePresentation();");
        }

        [Test]
        public void TurnActions_ExposeFiveExplicitInteractions_AndPresentersAreNotInteractions()
        {
            var presenterType = typeof(TurnActionPresenter);
            Assert.That(typeof(InteractionBase).IsAssignableFrom(typeof(BuildInteraction)), Is.True);
            Assert.That(typeof(InteractionBase).IsAssignableFrom(typeof(DeployInteraction)), Is.True);
            Assert.That(typeof(InteractionBase).IsAssignableFrom(typeof(DispatchInteraction)), Is.True);
            Assert.That(typeof(InteractionBase).IsAssignableFrom(typeof(MoveInteraction)), Is.True);
            Assert.That(typeof(InteractionBase).IsAssignableFrom(typeof(ExploreInteraction)), Is.True);
            Assert.That(presenterType.GetProperty("BuildInteraction").PropertyType, Is.EqualTo(typeof(BuildInteraction)));
            Assert.That(presenterType.GetProperty("DeployInteraction").PropertyType, Is.EqualTo(typeof(DeployInteraction)));
            Assert.That(presenterType.GetProperty("DispatchInteraction").PropertyType, Is.EqualTo(typeof(DispatchInteraction)));
            Assert.That(presenterType.GetProperty("MoveInteraction").PropertyType, Is.EqualTo(typeof(MoveInteraction)));
            Assert.That(presenterType.GetProperty("ExploreInteraction").PropertyType, Is.EqualTo(typeof(ExploreInteraction)));
            Assert.That(typeof(IInteraction).IsAssignableFrom(typeof(InfluenceActionPresenter)), Is.False);
            Assert.That(typeof(IInteraction).IsAssignableFrom(typeof(ExplorationEventPresenter)), Is.False);
        }

        [Test]
        public void CityStyleInteraction_OwnsSelectionAndSpecialActionMarkerLogic()
        {
            var workflowRoot = Path.Combine(AssetsPath, "YC/Presentation/Workflows");
            var interactionSource = File.ReadAllText(
                Path.Combine(workflowRoot, "CityStyleInteraction.cs"));
            var presenterSource = File.ReadAllText(
                Path.Combine(workflowRoot, "TurnActionPresenter.cs"));

            Assert.That(
                typeof(TurnActionPresenter).GetProperty("CityStyleInteraction").PropertyType,
                Is.EqualTo(typeof(CityStyleInteraction)));
            StringAssert.Contains(
                "new CityStyleSelectionController()",
                interactionSource);
            StringAssert.Contains(
                "new SpecialActionOptionQueryService(",
                interactionSource);
            StringAssert.Contains(
                "completeAction(\"特殊行动\")",
                interactionSource);
            StringAssert.DoesNotContain(
                "CityStyleSelectionController",
                presenterSource);
            StringAssert.DoesNotContain(
                "SpecialActionOptionQueryService",
                presenterSource);
            StringAssert.DoesNotContain(
                "TrySubmitSpecialAction",
                presenterSource);

            AssertThinDelegate(
                presenterSource,
                "public void BeginDeclareCityStyle(string initialCityStyleId = \"\")",
                "CityStyleInteraction.BeginDeclare(initialCityStyleId);");
            AssertThinDelegate(
                presenterSource,
                "public void OpenCityStylePreview(string initialCityStyleId)",
                "CityStyleInteraction.OpenPreview(initialCityStyleId);");
            AssertThinDelegate(
                presenterSource,
                "public void SubmitDeclareCityStyle(string cityStyleId, IReadOnlyList<int> selectedSlotIndexes)",
                "CityStyleInteraction.SubmitDeclare(cityStyleId, selectedSlotIndexes);");
        }

        [Test]
        public void TurnActionPanelPresenter_OwnsPanelGuardsModesAndStatusText()
        {
            var workflowRoot = Path.Combine(AssetsPath, "YC/Presentation/Workflows");
            var panelSource = File.ReadAllText(
                Path.Combine(workflowRoot, "TurnActionPanelPresenter.cs"));
            var presenterPath = Path.Combine(workflowRoot, "TurnActionPresenter.cs");
            var presenterSource = File.ReadAllText(presenterPath);

            Assert.That(
                typeof(TurnActionPresenter).GetProperty("ActionPanelPresenter").PropertyType,
                Is.EqualTo(typeof(TurnActionPanelPresenter)));
            StringAssert.Contains("private bool CanStartAction(bool quickAction)", panelSource);
            StringAssert.Contains("private InteractionMode ResolveDisplayedMode(", panelSource);
            StringAssert.Contains("private string BuildStatus(", panelSource);
            StringAssert.DoesNotContain("InteractionMode.Resolving", panelSource);
            StringAssert.DoesNotContain("private bool CanStartAction(", presenterSource);
            StringAssert.DoesNotContain("private InteractionMode ResolveDisplayedMode(", presenterSource);
            StringAssert.DoesNotContain("private string BuildStatus(", presenterSource);
            StringAssert.DoesNotContain("private static ActionPanelViewModel EmptyViewModel(", presenterSource);
            Assert.That(
                File.ReadAllLines(presenterPath).Length,
                Is.LessThanOrEqualTo(400),
                "动作面板职责迁出后 TurnActionPresenter 应显著瘦身。");

            AssertThinDelegate(
                presenterSource,
                "public ActionPanelViewModel BuildActionPanelViewModel()",
                "return ActionPanelPresenter.BuildViewModel();");
            AssertThinDelegate(
                presenterSource,
                "private bool CanStartMainAction()",
                "return ActionPanelPresenter.CanStartMainAction();");
            AssertThinDelegate(
                presenterSource,
                "private bool CanStartQuickAction()",
                "return ActionPanelPresenter.CanStartQuickAction();");
            AssertThinDelegate(
                presenterSource,
                "private string GetQuickActionUnavailableReason()",
                "return ActionPanelPresenter.GetQuickActionUnavailableReason();");
            AssertThinDelegate(
                presenterSource,
                "public static string BuildCompletedMainActionMessage(string actionName)",
                "return TurnActionPanelPresenter.BuildCompletedMainActionMessage(actionName);");
        }

        [Test]
        public void SceneController_ContainsOnlyOrchestrationBoundaries()
        {
            var root = Path.Combine(AssetsPath, "YC/Presentation");
            var paths = new List<string>(
                Directory.GetFiles(root, "MobileCityInteractionController*.cs", SearchOption.TopDirectoryOnly));
            paths.Sort(StringComparer.Ordinal);
            Assert.That(paths, Is.Not.Empty);

            var lineBudgetCount = 0;
            var allPartialLineCount = 0;
            var rightCardSmokeLineCount = 0;
            string mainSource = null;
            for (var i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                var fileName = Path.GetFileName(path);
                var source = File.ReadAllText(path);
                var lineCount = File.ReadAllLines(path).Length;
                allPartialLineCount += lineCount;
                if (fileName == "MobileCityInteractionController.RightCardSmoke.cs")
                {
                    rightCardSmokeLineCount = lineCount;
                }
                else
                {
                    lineBudgetCount += lineCount;
                }

                if (fileName == "MobileCityInteractionController.cs")
                {
                    mainSource = source;
                }

                StringAssert.DoesNotContain("new GameCommand", source, fileName);
                Assert.That(source, Does.Not.Match(@"new\s+\w+Command\s*\("), fileName);
                StringAssert.DoesNotContain("pendingDispatch", source, fileName);
                StringAssert.DoesNotContain("PendingEvent", source, fileName);
                Assert.That(
                    source,
                    Does.Not.Match(@"private\s+[^\r\n(]*CollectionSelection[^\r\n(]*;"),
                    fileName);
                Assert.That(
                    source,
                    Does.Not.Match(@"\.(Resources|Decks|Phase|CurrentPlayerId|ActedMainActionThisTurn|Pending\w*)\s*=(?!=)"),
                    fileName);
                StringAssert.DoesNotContain("ShowCharacterSecondEffectDecision", source, fileName);
            }

            Assert.That(
                lineBudgetCount,
                Is.LessThanOrEqualTo(1200),
                "Controller 核心职责行数超限；RightCardSmoke 仍参与实际编译，仅排除于核心职责行数预算，且仍扫描危险模式。");
            Assert.That(rightCardSmokeLineCount, Is.GreaterThan(0));
            Assert.That(
                allPartialLineCount,
                Is.EqualTo(lineBudgetCount + rightCardSmokeLineCount),
                "实际编译的原始 partial 总行数必须明确等于预算口径与 RightCardSmoke 行数之和。");
            Assert.That(mainSource, Is.Not.Null);
            var refreshBody = ExtractMethodBody(mainSource, "private void RefreshAllFromState()");
            StringAssert.DoesNotContain("Begin", refreshBody);
            StringAssert.DoesNotContain("Activate", refreshBody);
            StringAssert.DoesNotContain("Reset", refreshBody);
            StringAssert.DoesNotContain("Cancel", refreshBody);
            StringAssert.DoesNotContain("Hide", refreshBody);
        }

        [Test]
        public void SceneController_MapClickEntrypointsOnlyForwardToUnifiedRouter()
        {
            var path = Path.Combine(AssetsPath, "YC/Presentation/MobileCityInteractionController.cs");
            var source = File.ReadAllText(path);

            AssertDirectRouterForward(
                source,
                "public void OnHotspotClicked(string locationId)",
                "interactionRouter.OnLocationClicked(locationId);");
            AssertDirectRouterForward(
                source,
                "public void OnInfluenceSlotClicked(string slotId)",
                "interactionRouter.OnInfluenceSlotClicked(slotId);");
            AssertDirectRouterForward(
                source,
                "public void OnMobileCityClicked()",
                "interactionRouter.OnMobileCityClicked();");
        }

        [Test]
        public void SceneController_RegistersActiveWorkflowsBeforeLegacyFallback()
        {
            var controllerPath = Path.Combine(
                AssetsPath,
                "YC/Presentation/MobileCityInteractionController.cs");
            var characterPath = Path.Combine(
                AssetsPath,
                "YC/Presentation/CharacterCardInteraction.cs");
            var controllerSource = File.ReadAllText(controllerPath);
            var characterSource = File.ReadAllText(characterPath);
            var routingBody = ExtractMethodBody(
                controllerSource,
                "private void BuildInteractionRouting()");
            var legacyRegistration =
                "interactionRouter.Register(new LegacyMapInteractionAdapter(mapInteractionRouter));";
            var workflowRegistrations = new[]
            {
                "interactionRouter.Register(turnActionPresenter.BuildInteraction);",
                "interactionRouter.Register(turnActionPresenter.MoveInteraction);",
                "interactionRouter.Register(turnActionPresenter.DeployInteraction);",
                "interactionRouter.Register(turnActionPresenter.DispatchInteraction);",
                "interactionRouter.Register(turnActionPresenter.ExploreInteraction);",
                "interactionRouter.Register(resourceCollectionPresenter);"
            };

            StringAssert.Contains(legacyRegistration, routingBody);
            for (var i = 0; i < workflowRegistrations.Length; i++)
            {
                StringAssert.Contains(workflowRegistrations[i], routingBody);
                Assert.That(
                    routingBody.IndexOf(workflowRegistrations[i], StringComparison.Ordinal),
                    Is.LessThan(routingBody.IndexOf(legacyRegistration, StringComparison.Ordinal)),
                    workflowRegistrations[i]);
            }
            StringAssert.DoesNotContain(
                "interactionRouter.Register(explorationEventPresenter);",
                routingBody);
            StringAssert.DoesNotContain(
                "interactionRouter.Register(influenceActionPresenter);",
                routingBody);

            StringAssert.Contains(
                "buildInfoPanel.IsFacilityEffectSelectionActive",
                routingBody);
            StringAssert.Contains("hasFacilitySelection()", characterSource);
        }

        [Test]
        public void SceneController_InitializesBuildUiCoordinatorExactlyOnceAfterFacilityCoordinator()
        {
            var path = Path.Combine(AssetsPath, "YC/Presentation/MobileCityInteractionController.cs");
            var source = File.ReadAllText(path);
            const string facilityConstruction =
                "facilityEffectInteraction = new FacilityEffectInteractionUiCoordinator(";
            const string buildConstruction =
                "buildFacilityInteraction = new BuildFacilityInteractionUiCoordinator(";

            Assert.That(CountOccurrences(source, buildConstruction), Is.EqualTo(1));
            Assert.That(
                source.IndexOf(buildConstruction, StringComparison.Ordinal),
                Is.GreaterThan(source.IndexOf(facilityConstruction, StringComparison.Ordinal)));
            StringAssert.DoesNotContain(
                "BuildFacilityInteractionUiCoordinator",
                ExtractMethodBody(source, "private void EnsureBuildInfoPanel()"));
        }

        [Test]
        public void SceneController_InitializesCommandSubmissionAfterUnifiedRouter()
        {
            var path = Path.Combine(AssetsPath, "YC/Presentation/MobileCityInteractionController.cs");
            var source = File.ReadAllText(path);
            var awakeBody = ExtractMethodBody(source, "private void Awake()");

            Assert.That(
                awakeBody.IndexOf("BuildCommandSubmission(", StringComparison.Ordinal),
                Is.GreaterThan(awakeBody.IndexOf("BuildInteractionRouting();", StringComparison.Ordinal)));
        }

        [Test]
        public void SceneController_PendingPresentationUsesUnifiedRouterAndDestroyCancelsRegistry()
        {
            var path = Path.Combine(AssetsPath, "YC/Presentation/MobileCityInteractionController.cs");
            var source = File.ReadAllText(path);
            var refreshBody = ExtractMethodBody(
                source,
                "private void RefreshPendingChoiceOrHighlights()");
            var destroyBody = ExtractMethodBody(source, "private void OnDestroy()");

            StringAssert.Contains("interactionRouter.BuildActivePresentation()", refreshBody);
            StringAssert.DoesNotContain("characterMapInteraction.Synchronize()", refreshBody);
            StringAssert.DoesNotContain("specialActionInteraction.Synchronize()", refreshBody);
            StringAssert.DoesNotContain("characterCardEffectInteraction.SynchronizePending()", refreshBody);
            StringAssert.DoesNotContain("facilityEffectInteraction.Synchronize()", refreshBody);
            StringAssert.Contains("interactionPresentation.ReplacesHighlights", refreshBody);
            StringAssert.Contains(
                "workflowView.SetHighlights(interactionPresentation.Highlights)",
                refreshBody);
            StringAssert.Contains("interactionRouter.CancelAll()", destroyBody);
        }

        [Test]
        public void SceneController_EscapeUsesUnifiedRouterResult()
        {
            var controllerPath = Path.Combine(
                AssetsPath,
                "YC/Presentation/MobileCityInteractionController.cs");
            var settingsPath = Path.Combine(
                AssetsPath,
                "YC/Presentation/GameSettingsMenuController.cs");
            var controllerSource = File.ReadAllText(controllerPath);
            var settingsSource = File.ReadAllText(settingsPath);
            var escapeBody = ExtractMethodBody(
                controllerSource,
                "public bool TryHandleInteractionEscape()");

            StringAssert.Contains("interactionRouter.OnEscape()", escapeBody);
            StringAssert.Contains("InteractionResultKind.Passthrough", escapeBody);
            StringAssert.Contains(
                "interactionEscapeConsumedFrame == Time.frameCount",
                escapeBody);
            StringAssert.Contains(
                "WasInteractionEscapeConsumedThisFrame()",
                settingsSource);
            StringAssert.Contains("TryHandleInteractionEscape()", settingsSource);
            StringAssert.DoesNotContain(
                "TryHandleBuildFacilityEscape()",
                ExtractMethodBody(settingsSource, "public void HandleEscapePressed()"));
        }

        [Test]
        public void CommandSubmissionController_IsOnlyPresentationTransportOwner()
        {
            var root = Path.Combine(AssetsPath, "YC/Presentation");
            foreach (var path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(path) == "CommandSubmissionController.cs") continue;
                StringAssert.DoesNotContain("UnityNetcodeCommandTransport", File.ReadAllText(path), path);
            }
        }

        [Test]
        public void SceneController_SubmitsInteractionCommandsThroughCommandGateway()
        {
            var path = Path.Combine(
                AssetsPath,
                "YC/Presentation/MobileCityInteractionController.cs");
            var source = File.ReadAllText(path);

            StringAssert.Contains("commandGateway = new CommandGateway(gameplayAdapter);", source);
            StringAssert.DoesNotContain("gameplayAdapter.Submit(", source);
            StringAssert.Contains(
                "commandGateway.Submit(",
                ExtractMethodBody(source, "private void SubmitPendingEffectCommand(GameCommand command)"));
            var pendingEffectSubmitBody = ExtractMethodBody(
                source,
                "private void SubmitPendingEffectCommand(GameCommand command)");
            StringAssert.Contains("SubmitOutcomeKind.NoResult", pendingEffectSubmitBody);
            StringAssert.Contains(
                "interactionRouter?.NotifyCommandSettled(commandId)",
                pendingEffectSubmitBody);
            StringAssert.Contains(
                "commandGateway.Submit(",
                ExtractMethodBody(
                    source,
                    "private void SubmitCharacterCardCommand(GameCommand command, string localSuccessPrompt, string remotePrompt)"));
        }

        [Test]
        public void NetworkCommandRejection_RebuildsInteractionInsteadOfLeavingBusyStage()
        {
            var path = Path.Combine(
                AssetsPath,
                "YC/Presentation/CommandSubmissionController.cs");
            var source = File.ReadAllText(path);
            var body = ExtractMethodBody(
                source,
                "private void OnNetworkCommandRejected(RejectedGameCommandDto rejected)");

            StringAssert.Contains("commandSettled?.Invoke", body);
            StringAssert.Contains("refreshFromState();", body);
            Assert.That(
                body.IndexOf("refreshFromState();", StringComparison.Ordinal),
                Is.LessThan(body.IndexOf("setPrompt(", StringComparison.Ordinal)));
        }

        [Test]
        public void ActionCompletion_AlwaysSynchronizesFacilityEffectPresentation()
        {
            var path = Path.Combine(AssetsPath, "YC/Presentation/MobileCityInteractionController.cs");
            var source = File.ReadAllText(path);
            var body = ExtractMethodBody(source, "private void CompleteActionCommandUi(string actionName)");

            StringAssert.Contains("facilityEffectInteraction.Synchronize()", body);
            StringAssert.DoesNotContain("facilityEffectInteraction.IsActive", body);
        }

        private static void AssertDirectRouterForward(
            string source,
            string signature,
            string expectedStatement)
        {
            var body = ExtractMethodBody(source, signature);
            Assert.That(RemoveWhitespace(body), Is.EqualTo(RemoveWhitespace(expectedStatement)));
            StringAssert.DoesNotContain("specialActionInteraction", body);
            StringAssert.DoesNotContain("characterMapInteraction", body);
            StringAssert.DoesNotContain("facilityEffectInteraction", body);
            StringAssert.DoesNotContain("mapInteractionRouter", body);
        }

        private static void AssertThinDelegate(
            string source,
            string signature,
            string expectedStatement)
        {
            var body = ExtractMethodBody(source, signature);
            Assert.That(
                RemoveWhitespace(body),
                Is.EqualTo(RemoveWhitespace(expectedStatement)),
                signature);
        }

        private static int CountOccurrences(string source, string value)
        {
            var count = 0;
            var index = 0;
            while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }

        private static string RemoveWhitespace(string value)
        {
            var result = string.Empty;
            for (var i = 0; i < value.Length; i++)
            {
                if (!char.IsWhiteSpace(value[i]))
                {
                    result += value[i];
                }
            }

            return result;
        }

        private static string ExtractMethodBody(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), signature);
            start = source.IndexOf('{', start) + 1;
            var depth = 1;
            for (var i = start; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                if (source[i] != '}' || --depth != 0) continue;
                return source.Substring(start, i - start);
            }
            Assert.Fail("方法体未闭合：" + signature);
            return string.Empty;
        }
    }
}
