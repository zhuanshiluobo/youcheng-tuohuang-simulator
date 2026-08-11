using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Mirror;
using Mirror.FizzySteam;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using YC.Infrastructure.Multiplayer;

namespace YC.Editor
{
    public static class NetworkRuntimeBuildReadiness
    {
        public static void ValidateReadyForBuild()
        {
            try
            {
                var identity = LoadAndValidatePrefab();
                ValidateCanonicalPrefabIsUnique();
                ValidateBuildSceneOrder();
                ValidateScene(
                    NetworkRuntimeEditorAssetBuilder.StartScenePath,
                    identity,
                    1);
                ValidateScene(
                    NetworkRuntimeEditorAssetBuilder.GameScenePath,
                    identity,
                    0);
                ValidateProductionSourcesDoNotCreateCoreComponents();
            }
            catch (BuildFailedException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new BuildFailedException(
                    "Network runtime build readiness 失败：" + exception.Message);
            }
        }

        private static PrefabIdentity LoadAndValidatePrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                NetworkRuntimeEditorAssetBuilder.PrefabPath);
            if (prefab == null || !prefab.activeSelf ||
                !AssetDatabase.Contains(prefab) || !AssetDatabase.IsMainAsset(prefab))
            {
                throw new InvalidOperationException(
                    "缺少已启用且唯一持久化的 NetworkRuntimeRoot Prefab 主资产。");
            }

            var manager = RequireSingleRootComponent<NetworkManager>(prefab);
            var localTransport = RequireSingleRootComponent<TelepathyTransport>(prefab);
            var steamTransport = RequireSingleRootComponent<FizzySteamworks>(prefab);
            var runtime = RequireSingleRootComponent<MirrorNetworkRuntime>(prefab);
            var steamBootstrap = RequireSingleRootComponent<SteamBootstrap>(prefab);
            var commandTransport = RequireSingleRootComponent<MirrorCommandTransport>(prefab);

            if (!manager.enabled || !runtime.enabled || !steamBootstrap.enabled ||
                !commandTransport.enabled)
            {
                throw new InvalidOperationException(
                    "NetworkManager 与三个网络引导组件必须在 Prefab 中启用。");
            }

            if (localTransport.enabled || steamTransport.enabled)
            {
                throw new InvalidOperationException(
                    "两个 Transport 必须在 Prefab 中保持禁用，直到合法的网络启动请求发生。");
            }

            if (manager.transport != steamTransport || manager.maxConnections != 4 ||
                manager.autoCreatePlayer || !manager.dontDestroyOnLoad)
            {
                throw new InvalidOperationException(
                    "NetworkManager 的默认 Transport、连接上限、玩家创建或持久化设置无效。");
            }

            if (!runtime.TryValidatePersistentConfiguration(out var reason))
            {
                throw new InvalidOperationException(
                    "MirrorNetworkRuntime 序列化接线无效：" + reason);
            }

            if (GetExecutionOrder(steamBootstrap) >= GetExecutionOrder(runtime) ||
                GetExecutionOrder(runtime) >= GetExecutionOrder(commandTransport) ||
                GetExecutionOrder(runtime) >= GetExecutionOrder(manager))
            {
                throw new InvalidOperationException(
                    "SteamBootstrap、MirrorNetworkRuntime、MirrorCommandTransport 与 " +
                    "NetworkManager 的执行顺序无效。");
            }

            return PrefabIdentity.Create(
                prefab,
                runtime,
                manager,
                localTransport,
                steamTransport,
                steamBootstrap,
                commandTransport);
        }

        private static T RequireSingleRootComponent<T>(GameObject prefab) where T : Component
        {
            var components = prefab.GetComponentsInChildren<T>(true);
            if (components.Length != 1 || components[0].gameObject != prefab)
            {
                throw new InvalidOperationException(
                    typeof(T).Name + " 必须在 NetworkRuntimeRoot 根对象上恰好出现一次。");
            }

            return components[0];
        }

        private static void ValidateCanonicalPrefabIsUnique()
        {
            var guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
            for (var i = 0; i < guids.Length; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (Normalize(path) == NetworkRuntimeEditorAssetBuilder.PrefabPath)
                {
                    continue;
                }

                var candidate = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (candidate == null)
                {
                    continue;
                }

                if (candidate.GetComponentsInChildren<MirrorNetworkRuntime>(true).Length != 0 ||
                    candidate.GetComponentsInChildren<SteamBootstrap>(true).Length != 0 ||
                    candidate.GetComponentsInChildren<MirrorCommandTransport>(true).Length != 0 ||
                    candidate.GetComponentsInChildren<NetworkManager>(true).Length != 0 ||
                    candidate.GetComponentsInChildren<TelepathyTransport>(true).Length != 0 ||
                    candidate.GetComponentsInChildren<FizzySteamworks>(true).Length != 0)
                {
                    throw new InvalidOperationException(
                        "核心网络组件只能存在于 canonical NetworkRuntimeRoot Prefab：" + path);
                }
            }
        }

        private static int GetExecutionOrder(MonoBehaviour component)
        {
            var script = MonoScript.FromMonoBehaviour(component);
            if (script == null)
            {
                throw new InvalidOperationException(
                    "无法解析 " + component.GetType().Name + " 的脚本执行顺序。");
            }

            var importerOrder = MonoImporter.GetExecutionOrder(script);
            if (importerOrder != 0)
            {
                return importerOrder;
            }

            var attribute = (DefaultExecutionOrder)Attribute.GetCustomAttribute(
                component.GetType(),
                typeof(DefaultExecutionOrder));
            return attribute == null ? 0 : attribute.order;
        }

        private static void ValidateBuildSceneOrder()
        {
            var enabled = new List<string>();
            var scenes = EditorBuildSettings.scenes;
            for (var i = 0; i < scenes.Length; i++)
            {
                if (scenes[i].enabled)
                {
                    enabled.Add(Normalize(scenes[i].path));
                }
            }

            if (enabled.Count < 2 ||
                enabled[0] != NetworkRuntimeEditorAssetBuilder.StartScenePath ||
                !enabled.Contains(NetworkRuntimeEditorAssetBuilder.GameScenePath))
            {
                throw new InvalidOperationException(
                    "StartScene 必须是首个 enabled BuildSettings 场景，且 SampleScene 必须启用。");
            }
        }

        private static void ValidateScene(
            string scenePath,
            PrefabIdentity identity,
            int expectedInstanceCount)
        {
            if (!File.Exists(scenePath))
            {
                throw new InvalidOperationException("缺少正式场景：" + scenePath);
            }

            ValidateSavedSceneYaml(
                scenePath,
                File.ReadAllText(scenePath),
                identity.PrefabGuid,
                identity.RootId,
                identity.RuntimeId,
                identity.ManagerId,
                identity.LocalTransportId,
                identity.SteamTransportId,
                identity.SteamBootstrapId,
                identity.CommandTransportId,
                identity.ScriptGuids,
                expectedInstanceCount);
        }

        internal static void ValidateSavedSceneYaml(
            string scenePath,
            string yaml,
            string prefabGuid,
            long rootId,
            long runtimeId,
            long managerId,
            long localTransportId,
            long steamTransportId,
            long steamBootstrapId,
            long commandTransportId,
            string[] scriptGuids,
            int expectedInstanceCount)
        {
            if (string.IsNullOrEmpty(yaml))
            {
                throw new InvalidOperationException(scenePath + " 的场景 YAML 为空。");
            }

            var blocks = Regex.Matches(
                yaml,
                @"^--- !u!1001 &.*?(?=^--- !u!|\z)",
                RegexOptions.Multiline | RegexOptions.Singleline);
            var sourceToken =
                "m_SourcePrefab: {fileID: 100100000, guid: " + prefabGuid + ", type: 3}";
            var matching = new List<string>();
            for (var i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].Value.Contains(sourceToken))
                {
                    matching.Add(blocks[i].Value);
                }
            }

            if (matching.Count != expectedInstanceCount)
            {
                throw new InvalidOperationException(
                    scenePath + " 的 NetworkRuntimeRoot Prefab 实例数应为 " +
                    expectedInstanceCount + "，实际为 " + matching.Count + "。");
            }

            RejectDirectComponents(scenePath, yaml, scriptGuids);
            if (expectedInstanceCount == 0)
            {
                return;
            }

            var block = matching[0];
            var componentIds = new[]
            {
                runtimeId,
                managerId,
                localTransportId,
                steamTransportId,
                steamBootstrapId,
                commandTransportId
            };
            RejectRemovedObjects(scenePath, block, prefabGuid, rootId, componentIds);
            RejectOverrideValue(scenePath, block, prefabGuid, rootId, "m_IsActive", "0");
            RejectOverrideValue(scenePath, block, prefabGuid, runtimeId, "m_Enabled", "0");
            RejectOverrideValue(scenePath, block, prefabGuid, managerId, "m_Enabled", "0");
            RejectOverrideValue(scenePath, block, prefabGuid, steamBootstrapId, "m_Enabled", "0");
            RejectOverrideValue(scenePath, block, prefabGuid, commandTransportId, "m_Enabled", "0");
            RejectOverrideValue(scenePath, block, prefabGuid, localTransportId, "m_Enabled", "1");
            RejectOverrideValue(scenePath, block, prefabGuid, steamTransportId, "m_Enabled", "1");

            RejectAnyOverride(scenePath, block, prefabGuid, runtimeId, "networkManager");
            RejectAnyOverride(scenePath, block, prefabGuid, runtimeId, "localTransport");
            RejectAnyOverride(scenePath, block, prefabGuid, runtimeId, "steamTransport");
            RejectAnyOverride(scenePath, block, prefabGuid, managerId, "transport");
            RejectAnyOverride(scenePath, block, prefabGuid, managerId, "maxConnections");
            RejectAnyOverride(scenePath, block, prefabGuid, managerId, "autoCreatePlayer");
            RejectAnyOverride(scenePath, block, prefabGuid, managerId, "dontDestroyOnLoad");
            RejectAnyOverride(scenePath, block, prefabGuid, localTransportId, "port");
            RejectAnyOverride(scenePath, block, prefabGuid, steamTransportId, "AllowSteamRelay");
            RejectAnyOverride(
                scenePath,
                block,
                prefabGuid,
                steamTransportId,
                "UseNextGenSteamNetworking");
        }

        private static void RejectDirectComponents(
            string scenePath,
            string yaml,
            string[] scriptGuids)
        {
            for (var i = 0; i < scriptGuids.Length; i++)
            {
                var pattern =
                    @"m_Script:\s*\{fileID:\s*-?\d+,\s*guid:\s*" +
                    Regex.Escape(scriptGuids[i]) + @",\s*type:\s*3\}";
                if (Regex.IsMatch(yaml, pattern, RegexOptions.IgnoreCase))
                {
                    throw new InvalidOperationException(
                        scenePath + " 包含 NetworkRuntimeRoot Prefab 之外的核心网络组件。");
                }
            }
        }

        private static void RejectRemovedObjects(
            string scenePath,
            string block,
            string prefabGuid,
            long rootId,
            long[] componentIds)
        {
            var removedComponents = ExtractSection(
                block,
                "m_RemovedComponents:",
                "m_RemovedGameObjects:");
            for (var i = 0; i < componentIds.Length; i++)
            {
                if (ContainsPrefabReference(removedComponents, prefabGuid, componentIds[i]))
                {
                    throw new InvalidOperationException(
                        scenePath + " 从 NetworkRuntimeRoot 实例移除了核心网络组件。");
                }
            }

            var removedGameObjects = ExtractSection(
                block,
                "m_RemovedGameObjects:",
                "m_AddedGameObjects:");
            if (ContainsPrefabReference(removedGameObjects, prefabGuid, rootId))
            {
                throw new InvalidOperationException(
                    scenePath + " 从 NetworkRuntimeRoot 实例移除了持久化根对象。");
            }
        }

        private static string ExtractSection(string block, string startToken, string endToken)
        {
            var start = block.IndexOf(startToken, StringComparison.Ordinal);
            var end = block.IndexOf(endToken, StringComparison.Ordinal);
            if (start < 0 || end <= start)
            {
                throw new InvalidOperationException(
                    "NetworkRuntimeRoot Prefab override 结构不完整。");
            }

            return block.Substring(start, end - start);
        }

        private static bool ContainsPrefabReference(
            string source,
            string prefabGuid,
            long localId)
        {
            var pattern =
                @"fileID:\s*" + localId + @",\s*guid:\s*" +
                Regex.Escape(prefabGuid) + @",\s*type:\s*\d+";
            return Regex.IsMatch(source, pattern, RegexOptions.IgnoreCase);
        }

        private static void RejectOverrideValue(
            string scenePath,
            string block,
            string prefabGuid,
            long localId,
            string propertyPath,
            string forbiddenValue)
        {
            var pattern = BuildOverridePattern(prefabGuid, localId, propertyPath) +
                          @"\s*value:\s*" + Regex.Escape(forbiddenValue) + @"(?:\s|$)";
            if (Regex.IsMatch(block, pattern, RegexOptions.IgnoreCase))
            {
                throw new InvalidOperationException(
                    scenePath + " 通过 override 破坏了 " + propertyPath + "。");
            }
        }

        private static void RejectAnyOverride(
            string scenePath,
            string block,
            string prefabGuid,
            long localId,
            string propertyPath)
        {
            if (Regex.IsMatch(
                    block,
                    BuildOverridePattern(prefabGuid, localId, propertyPath),
                    RegexOptions.IgnoreCase))
            {
                throw new InvalidOperationException(
                    scenePath + " 不得 override 核心网络字段 " + propertyPath + "。");
            }
        }

        private static string BuildOverridePattern(
            string prefabGuid,
            long localId,
            string propertyPath)
        {
            return
                @"target:\s*\{fileID:\s*" + localId +
                @",\s*guid:\s*" + Regex.Escape(prefabGuid) +
                @",\s*type:\s*\d+\}\s*\r?\n\s*propertyPath:\s*" +
                Regex.Escape(propertyPath) + @"\s*\r?\n";
        }

        private static void ValidateProductionSourcesDoNotCreateCoreComponents()
        {
            var relativePaths = new[]
            {
                "Assets/YC/Infrastructure/Multiplayer/MirrorNetworkRuntime.cs",
                "Assets/YC/Infrastructure/Multiplayer/SteamBootstrap.cs",
                "Assets/YC/Infrastructure/Multiplayer/MirrorCommandTransport.cs"
            };
            for (var i = 0; i < relativePaths.Length; i++)
            {
                var source = File.ReadAllText(relativePaths[i]);
                if (source.Contains("new GameObject(") || source.Contains("AddComponent<"))
                {
                    throw new InvalidOperationException(
                        relativePaths[i] + " 仍在运行时创建核心网络组件。");
                }
            }
        }

        private static string Normalize(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/');
        }

        private sealed class PrefabIdentity
        {
            public string PrefabGuid;
            public long RootId;
            public long RuntimeId;
            public long ManagerId;
            public long LocalTransportId;
            public long SteamTransportId;
            public long SteamBootstrapId;
            public long CommandTransportId;
            public string[] ScriptGuids;

            public static PrefabIdentity Create(
                GameObject prefab,
                params MonoBehaviour[] components)
            {
                var identity = new PrefabIdentity
                {
                    PrefabGuid = AssetDatabase.AssetPathToGUID(
                        NetworkRuntimeEditorAssetBuilder.PrefabPath),
                    ScriptGuids = new string[components.Length]
                };
                if (string.IsNullOrEmpty(identity.PrefabGuid) ||
                    !TryGetLocalId(prefab, identity.PrefabGuid, out identity.RootId))
                {
                    throw new InvalidOperationException(
                        "无法解析 NetworkRuntimeRoot Prefab 根对象的持久化标识。");
                }

                var ids = new long[components.Length];
                for (var i = 0; i < components.Length; i++)
                {
                    if (!TryGetLocalId(components[i], identity.PrefabGuid, out ids[i]))
                    {
                        throw new InvalidOperationException(
                            "无法解析 " + components[i].GetType().Name + " 的持久化标识。");
                    }

                    var script = MonoScript.FromMonoBehaviour(components[i]);
                    if (script == null ||
                        !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                            script,
                            out identity.ScriptGuids[i],
                            out long scriptId) ||
                        string.IsNullOrEmpty(identity.ScriptGuids[i]) || scriptId == 0)
                    {
                        throw new InvalidOperationException(
                            "无法解析 " + components[i].GetType().Name + " 的脚本标识。");
                    }
                }

                identity.RuntimeId = ids[0];
                identity.ManagerId = ids[1];
                identity.LocalTransportId = ids[2];
                identity.SteamTransportId = ids[3];
                identity.SteamBootstrapId = ids[4];
                identity.CommandTransportId = ids[5];
                return identity;
            }

            private static bool TryGetLocalId(
                UnityEngine.Object asset,
                string expectedGuid,
                out long localId)
            {
                return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                           asset,
                           out var guid,
                           out localId) &&
                       guid == expectedGuid && localId != 0;
            }
        }
    }
}
