using System;
using System.IO;
using NUnit.Framework;
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
            Assert.That(File.ReadAllText(path), Does.Match(@"""noEngineReferences""\s*:\s*true\b"));
        }

        [Test]
        public void WorkflowAssembly_HasNoUnityReference()
        {
            // 检查整个工作流程序集的依赖边界，不限定具体实现类或继承方式。
            foreach (var reference in typeof(TurnActionPresenter).Assembly.GetReferencedAssemblies())
            {
                Assert.That(reference.Name.StartsWith("UnityEngine", StringComparison.Ordinal), Is.False);
                Assert.That(reference.Name.StartsWith("UnityEditor", StringComparison.Ordinal), Is.False);
            }
        }

        [Test]
        public void SceneController_DoesNotConstructCommandsOrMutateDomainState()
        {
            var root = Path.Combine(AssetsPath, "YC/Presentation");
            var paths = Directory.GetFiles(root, "MobileCityInteractionController*.cs", SearchOption.TopDirectoryOnly);
            Assert.That(paths, Is.Not.Empty);

            // 包括所有 partial 文件；只约束业务边界，不限定文件拆分、行数或内部委托写法。
            foreach (var path in paths)
            {
                var source = File.ReadAllText(path);
                StringAssert.DoesNotContain("new GameCommand", source, path);
                Assert.That(source, Does.Not.Match(@"new\s+\w+Command\s*\("), path);
                Assert.That(
                    source,
                    Does.Not.Match(@"\.(Resources|Decks|Phase|CurrentPlayerId|ActedMainActionThisTurn|Pending\w*)\s*=(?!=)"),
                    path);
            }
        }

        [Test]
        public void SceneController_DestroyCancelsRegisteredInteractions()
        {
            var path = Path.Combine(AssetsPath, "YC/Presentation/MobileCityInteractionController.cs");
            var source = File.ReadAllText(path);
            var destroyBody = ExtractMethodBody(source, "private void OnDestroy()");

            StringAssert.Contains("interactionRouter.CancelAll()", destroyBody);
        }

        [Test]
        public void SceneController_RegistersActiveWorkflowsBeforeDefaultMapRoute()
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
            var defaultMapRouteRegistration =
                "interactionRouter.Register(mapInteractionRouter);";
            var workflowRegistrations = new[]
            {
                "interactionRouter.Register(turnActionPresenter.BuildInteraction);",
                "interactionRouter.Register(turnActionPresenter.MoveInteraction);",
                "interactionRouter.Register(influenceActionPresenter);",
                "interactionRouter.Register(explorationEventPresenter);",
                "interactionRouter.Register(resourceCollectionPresenter);"
            };

            StringAssert.Contains(defaultMapRouteRegistration, routingBody);
            for (var i = 0; i < workflowRegistrations.Length; i++)
            {
                StringAssert.Contains(workflowRegistrations[i], routingBody);
                Assert.That(
                    routingBody.IndexOf(workflowRegistrations[i], StringComparison.Ordinal),
                    Is.LessThan(routingBody.IndexOf(defaultMapRouteRegistration, StringComparison.Ordinal)),
                    workflowRegistrations[i]);
            }

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
                "facilityInteraction = new FacilityInteractionUiCoordinator(";
            const string buildConstruction =
                "buildFacilityInteraction = new BuildFacilityInteractionUiCoordinator(";

            StringAssert.Contains(facilityConstruction, source);
            StringAssert.Contains(buildConstruction, source);
            var buildIndex = source.IndexOf(buildConstruction, StringComparison.Ordinal);
            Assert.That(source.IndexOf(buildConstruction, buildIndex + buildConstruction.Length, StringComparison.Ordinal), Is.EqualTo(-1));
            Assert.That(
                source.IndexOf(buildConstruction, StringComparison.Ordinal),
                Is.GreaterThan(source.IndexOf(facilityConstruction, StringComparison.Ordinal)));
            StringAssert.DoesNotContain("EnsureBuildInfoPanel", source);
            StringAssert.Contains("[SerializeField] private BuildInfoPanel buildInfoPanel;", source);
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

        // 以下保留命令结算、输入消费等接线回归检查，不比较完整方法体。
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

            StringAssert.Contains("facilityInteraction.Synchronize()", body);
            StringAssert.DoesNotContain("facilityInteraction.IsActive", body);
        }

        private static string ExtractMethodBody(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), signature);
            var afterSignature = start + signature.Length;
            var arrow = source.IndexOf("=>", afterSignature, StringComparison.Ordinal);
            var brace = source.IndexOf('{', afterSignature);
            if (arrow >= 0 && (brace < 0 || arrow < brace))
                return "return " + source.Substring(arrow + 2, source.IndexOf(';', arrow) - arrow - 1);
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
