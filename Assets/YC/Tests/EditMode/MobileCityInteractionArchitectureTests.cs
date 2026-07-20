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
        public void WorkflowPresenter_IsNotAMonoBehaviour_AndHasNoUnityReference(Type presenterType)
        {
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(presenterType), Is.False);
            foreach (var reference in presenterType.Assembly.GetReferencedAssemblies())
            {
                Assert.That(reference.Name.StartsWith("UnityEngine", StringComparison.Ordinal), Is.False);
            }
        }

        [Test]
        public void SceneController_ContainsOnlyOrchestrationBoundaries()
        {
            var root = Path.Combine(AssetsPath, "YC/Presentation");
            var paths = new List<string>(
                Directory.GetFiles(root, "MobileCityInteractionController*.cs", SearchOption.TopDirectoryOnly));
            paths.Sort(StringComparer.Ordinal);
            Assert.That(paths, Is.Not.Empty);

            var productionLineCount = 0;
            string mainSource = null;
            for (var i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                var fileName = Path.GetFileName(path);
                var source = File.ReadAllText(path);
                if (fileName != "MobileCityInteractionController.RightCardSmoke.cs")
                {
                    productionLineCount += File.ReadAllLines(path).Length;
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
                productionLineCount,
                Is.LessThanOrEqualTo(1200),
                "生产 controller partial 总行数超限；RightCardSmoke 仅为开发展示适配，不计行数但仍扫描危险模式。");
            Assert.That(mainSource, Is.Not.Null);
            var refreshBody = ExtractMethodBody(mainSource, "private void RefreshAllFromState()");
            StringAssert.DoesNotContain("Begin", refreshBody);
            StringAssert.DoesNotContain("Activate", refreshBody);
            StringAssert.DoesNotContain("Reset", refreshBody);
            StringAssert.DoesNotContain("Cancel", refreshBody);
            StringAssert.DoesNotContain("Hide", refreshBody);
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
        public void ActionCompletion_AlwaysSynchronizesFacilityEffectPresentation()
        {
            var path = Path.Combine(AssetsPath, "YC/Presentation/MobileCityInteractionController.cs");
            var source = File.ReadAllText(path);
            var body = ExtractMethodBody(source, "private void CompleteActionCommandUi(string actionName)");

            StringAssert.Contains("facilityEffectInteraction.Synchronize()", body);
            StringAssert.DoesNotContain("facilityEffectInteraction.IsActive", body);
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
