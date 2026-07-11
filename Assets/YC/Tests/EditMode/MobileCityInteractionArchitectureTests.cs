using System;
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
            var path = Path.Combine(AssetsPath, "YC/Presentation/MobileCityInteractionController.cs");
            var source = File.ReadAllText(path);
            Assert.That(File.ReadAllLines(path).Length, Is.LessThanOrEqualTo(1000));
            StringAssert.DoesNotContain("new GameCommand", source);
            StringAssert.DoesNotContain("pendingDispatch", source);
            StringAssert.DoesNotContain("PendingEvent", source);
            Assert.That(source, Does.Not.Match(@"private\s+[^\r\n(]*CollectionSelection[^\r\n(]*;"));
            StringAssert.DoesNotContain(".Resources =", source);
            StringAssert.DoesNotContain(".Decks =", source);

            var refreshBody = ExtractMethodBody(source, "private void RefreshAllFromState()");
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
