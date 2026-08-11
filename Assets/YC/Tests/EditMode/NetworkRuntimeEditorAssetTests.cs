using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace YC.Tests.EditMode
{
    public sealed class NetworkRuntimeEditorAssetTests
    {
        [Test]
        public void PersistentPrefabAndProductionScenes_AreReady()
        {
            InvokeReadiness("ValidateReadyForBuild");
        }

        [Test]
        public void EditorBuilder_InitializesThemeBeforeOpeningProductionScenes()
        {
            var source = ReadSource("YC/Editor/NetworkRuntimeEditorAssetBuilder.cs");
            var rebuild = ExtractMethod(
                source,
                "public static void RebuildPrefabAndConfigureStartScene()",
                "private static GameObject BuildPrefab()");
            AssertBefore(
                rebuild,
                "UiThemeBuildReadiness.InitializeRequiredTheme();",
                "InstallSingleStartSceneInstance(prefab);");

            var installStart = ExtractMethod(
                source,
                "private static void InstallSingleStartSceneInstance",
                "private static void RemoveGameSceneInstances");
            AssertBefore(
                installStart,
                "UiThemeBuildReadiness.InitializeRequiredTheme();",
                "EditorSceneManager.OpenScene(StartScenePath");

            var inspectGameScene = ExtractMethod(
                source,
                "private static void RemoveGameSceneInstances",
                "private static bool RemoveMatchingRoots");
            AssertBefore(
                inspectGameScene,
                "UiThemeBuildReadiness.InitializeRequiredTheme();",
                "EditorSceneManager.OpenScene(GameScenePath");
        }

        [Test]
        public void ProductionEnsures_FailFastWithoutCreatingCoreComponents()
        {
            AssertPersistentEnsure(
                "YC/Infrastructure/Multiplayer/MirrorNetworkRuntime.cs",
                "public static MirrorNetworkRuntime Ensure()");
            AssertPersistentEnsure(
                "YC/Infrastructure/Multiplayer/SteamBootstrap.cs",
                "public static SteamBootstrap Ensure()");
            AssertPersistentEnsure(
                "YC/Infrastructure/Multiplayer/MirrorCommandTransport.cs",
                "public static MirrorCommandTransport Ensure()");

            AssertDuplicateRootIsDeactivatedBeforeDestroy(
                "YC/Infrastructure/Multiplayer/SteamBootstrap.cs");
            AssertDuplicateRootIsDeactivatedBeforeDestroy(
                "YC/Infrastructure/Multiplayer/MirrorNetworkRuntime.cs");
            AssertDuplicateRootIsDeactivatedBeforeDestroy(
                "YC/Infrastructure/Multiplayer/MirrorCommandTransport.cs");

            var commandSource = ReadSource(
                "YC/Infrastructure/Multiplayer/MirrorCommandTransport.cs");
            var commandDestroy = ExtractMethod(
                commandSource,
                "private void OnDestroy()",
                "private void OnServerConnected");
            AssertBefore(commandDestroy, "if (Instance != this) return;", "Shutdown();");
        }

        [Test]
        public void TransportActivation_IsDeferredUntilValidatedStartRequest()
        {
            var source = ReadSource(
                "YC/Infrastructure/Multiplayer/MirrorNetworkRuntime.cs");
            StringAssert.Contains("[DefaultExecutionOrder(-24000)]", source);
            StringAssert.Contains(
                "[DefaultExecutionOrder(-25000)]",
                ReadSource("YC/Infrastructure/Multiplayer/SteamBootstrap.cs"));
            StringAssert.Contains(
                "[DefaultExecutionOrder(-23000)]",
                ReadSource("YC/Infrastructure/Multiplayer/MirrorCommandTransport.cs"));
            var awake = ExtractMethod(
                source,
                "private void Awake()",
                "public bool TryValidatePersistentConfiguration");
            StringAssert.Contains("LocalTransport.enabled = false;", awake);
            StringAssert.Contains("Transport.enabled = false;", awake);
            StringAssert.Contains("Manager.transport = ActiveTransport;", awake);
            StringAssert.Contains("Mirror.Transport.active = ActiveTransport;", awake);
            StringAssert.DoesNotContain("ActivateConfiguredTransport();", awake);

            var startHost = ExtractMethod(
                source,
                "public void StartHost()",
                "public void StartLocalHost()");
            AssertBefore(
                startHost,
                "if (!SteamBootstrap.IsInitialized)",
                "ActivateConfiguredTransport();");

            var startLocalHost = ExtractMethod(
                source,
                "public void StartLocalHost()",
                "public void StartClient");
            AssertBefore(
                startLocalHost,
                "if (!IsLocalTestMode)",
                "ActivateConfiguredTransport();");

            var startClient = ExtractMethod(
                source,
                "public void StartClient",
                "public void StartLocalClient");
            AssertBefore(
                startClient,
                "if (!SteamBootstrap.IsInitialized)",
                "ActivateConfiguredTransport();");
            AssertBefore(
                startClient,
                "if (hostSteamId == 0)",
                "ActivateConfiguredTransport();");

            var startLocalClient = ExtractMethod(
                source,
                "public void StartLocalClient",
                "public void SetLocalClientIdentityTicket");
            AssertBefore(
                startLocalClient,
                "if (string.IsNullOrWhiteSpace(host))",
                "ActivateConfiguredTransport();");

            var activate = ExtractMethod(
                source,
                "private void ActivateConfiguredTransport()",
                "private void OnWaitingRoomLocalIdentity");
            StringAssert.Contains(
                "LocalTransport.enabled = ActiveTransport == LocalTransport;",
                activate);
            StringAssert.Contains(
                "Transport.enabled = ActiveTransport == Transport;",
                activate);
        }

        [Test]
        public void SceneYamlGate_RejectsDuplicatesRemovalAndDangerousOverrides()
        {
            var readiness = RequireReadinessType();
            var validate = readiness.GetMethod(
                "ValidateSavedSceneYaml",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(validate, Is.Not.Null);

            var scriptGuids = new[] { "runtime-script", "manager-script" };
            var valid = PrefabBlock();
            Assert.DoesNotThrow(() => InvokeYamlGate(validate, valid, scriptGuids, 1));
            Assert.DoesNotThrow(() => InvokeYamlGate(
                validate,
                "--- !u!1 &1\nGameObject:\n  m_Name: Sample\n",
                scriptGuids,
                0));

            AssertYamlRejected(validate, valid + PrefabBlock(), scriptGuids, 1);
            AssertYamlRejected(validate, valid, scriptGuids, 0);
            AssertYamlRejected(
                validate,
                PrefabBlock(Override(11, "m_IsActive", "0")),
                scriptGuids,
                1);
            AssertYamlRejected(
                validate,
                PrefabBlock(Override(22, "m_Enabled", "0")),
                scriptGuids,
                1);
            AssertYamlRejected(
                validate,
                PrefabBlock(Override(44, "m_Enabled", "1")),
                scriptGuids,
                1);
            AssertYamlRejected(
                validate,
                PrefabBlock(Override(33, "maxConnections", "8")),
                scriptGuids,
                1);
            AssertYamlRejected(
                validate,
                PrefabBlock(
                    string.Empty,
                    "  - {fileID: 22, guid: prefab, type: 3}\n"),
                scriptGuids,
                1);
            AssertYamlRejected(
                validate,
                PrefabBlock(
                    string.Empty,
                    string.Empty,
                    "  - {fileID: 11, guid: prefab, type: 3}\n"),
                scriptGuids,
                1);
            AssertYamlRejected(
                validate,
                valid +
                "--- !u!114 &9\nMonoBehaviour:\n" +
                "  m_Script: {fileID: 11500000, guid: runtime-script, type: 3}\n",
                scriptGuids,
                1);
        }

        private static void AssertPersistentEnsure(string relativePath, string signature)
        {
            var source = ReadSource(relativePath);
            var ensure = ExtractMethod(source, signature, "private void Awake()");
            StringAssert.Contains("throw new InvalidOperationException", ensure);
            StringAssert.DoesNotContain("new GameObject(", ensure);
            StringAssert.DoesNotContain("AddComponent<", ensure);
            StringAssert.DoesNotContain("FindObjectOfType", ensure);
        }

        private static void AssertDuplicateRootIsDeactivatedBeforeDestroy(string relativePath)
        {
            var source = ReadSource(relativePath);
            var awake = ExtractMethod(source, "private void Awake()", "private void OnDestroy");
            AssertBefore(awake, "gameObject.SetActive(false);", "Destroy(gameObject);");
        }

        private static string PrefabBlock(
            string modifications = "",
            string removedComponents = "",
            string removedGameObjects = "")
        {
            return
                "--- !u!1001 &1\n" +
                "PrefabInstance:\n" +
                "  m_Modification:\n" +
                "    m_Modifications:\n" + modifications +
                "    m_RemovedComponents:\n" + removedComponents +
                "    m_RemovedGameObjects:\n" + removedGameObjects +
                "    m_AddedGameObjects: []\n" +
                "    m_AddedComponents: []\n" +
                "  m_SourcePrefab: {fileID: 100100000, guid: prefab, type: 3}\n";
        }

        private static string Override(long id, string property, string value)
        {
            return
                "    - target: {fileID: " + id + ", guid: prefab, type: 3}\n" +
                "      propertyPath: " + property + "\n" +
                "      value: " + value + "\n" +
                "      objectReference: {fileID: 0}\n";
        }

        private static void AssertYamlRejected(
            MethodInfo method,
            string yaml,
            string[] scriptGuids,
            int expectedCount)
        {
            var exception = Assert.Throws<TargetInvocationException>(
                () => InvokeYamlGate(method, yaml, scriptGuids, expectedCount));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        private static void InvokeYamlGate(
            MethodInfo method,
            string yaml,
            string[] scriptGuids,
            int expectedCount)
        {
            method.Invoke(
                null,
                new object[]
                {
                    "test-scene",
                    yaml,
                    "prefab",
                    11L,
                    22L,
                    33L,
                    44L,
                    55L,
                    66L,
                    77L,
                    scriptGuids,
                    expectedCount
                });
        }

        private static void AssertBefore(string source, string first, string second)
        {
            var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
            var secondIndex = source.IndexOf(second, StringComparison.Ordinal);
            Assert.That(firstIndex, Is.GreaterThanOrEqualTo(0), first);
            Assert.That(secondIndex, Is.GreaterThan(firstIndex), second);
        }

        private static string ExtractMethod(string source, string start, string end)
        {
            var startIndex = source.IndexOf(start, StringComparison.Ordinal);
            var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
            Assert.That(startIndex, Is.GreaterThanOrEqualTo(0), start);
            Assert.That(endIndex, Is.GreaterThan(startIndex), end);
            return source.Substring(startIndex, endIndex - startIndex);
        }

        private static string ReadSource(string relativePath)
        {
            return File.ReadAllText(Path.Combine(UnityEngine.Application.dataPath, relativePath));
        }

        private static Type RequireReadinessType()
        {
            var type = Type.GetType(
                "YC.Editor.NetworkRuntimeBuildReadiness, Assembly-CSharp-Editor",
                false);
            Assert.That(type, Is.Not.Null);
            return type;
        }

        private static void InvokeReadiness(string methodName)
        {
            var method = RequireReadinessType().GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            method.Invoke(null, null);
        }
    }
}
