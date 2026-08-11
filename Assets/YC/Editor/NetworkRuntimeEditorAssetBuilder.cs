using System;
using Mirror;
using Mirror.FizzySteam;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using YC.Infrastructure.Multiplayer;
using Object = UnityEngine.Object;

namespace YC.Editor
{
    public static class NetworkRuntimeEditorAssetBuilder
    {
        public const string PrefabPath =
            "Assets/YC/Presentation/Prefabs/Infrastructure/NetworkRuntimeRoot.prefab";
        public const string StartScenePath = "Assets/Scenes/StartScene.unity";
        public const string GameScenePath = "Assets/Scenes/SampleScene.unity";
        public const string RootName = "Network Runtime Root";

        [MenuItem("YC/Build/Network Runtime/Rebuild Prefab And Configure Start Scene")]
        public static void RebuildPrefabAndConfigureStartScene()
        {
            UiThemeBuildReadiness.InitializeRequiredTheme();
            EnsureFolder("Assets/YC/Presentation/Prefabs/Infrastructure");
            var prefab = BuildPrefab();
            InstallSingleStartSceneInstance(prefab);
            RemoveGameSceneInstances(prefab);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            NetworkRuntimeBuildReadiness.ValidateReadyForBuild();
            Debug.Log(
                "[NetworkRuntimeEditorAssetBuilder] Rebuilt persistent network prefab, " +
                "wired StartScene once, and kept SampleScene free of duplicates.");
        }

        private static GameObject BuildPrefab()
        {
            var root = new GameObject(RootName);
            root.SetActive(false);

            try
            {
                var localTransport = root.AddComponent<TelepathyTransport>();
                var steamTransport = root.AddComponent<FizzySteamworks>();
                var manager = root.AddComponent<NetworkManager>();
                root.AddComponent<SteamBootstrap>();
                root.AddComponent<MirrorCommandTransport>();
                var runtime = root.AddComponent<MirrorNetworkRuntime>();

                localTransport.port = LocalMirrorTestMode.MirrorPort;
                localTransport.enabled = false;
                steamTransport.AllowSteamRelay = true;
                steamTransport.UseNextGenSteamNetworking = true;
                steamTransport.enabled = false;

                manager.transport = steamTransport;
                manager.maxConnections = 4;
                manager.autoCreatePlayer = false;
                manager.dontDestroyOnLoad = true;

                var runtimeData = new SerializedObject(runtime);
                SetObjectReference(runtimeData, "networkManager", manager);
                SetObjectReference(runtimeData, "steamTransport", steamTransport);
                SetObjectReference(runtimeData, "localTransport", localTransport);
                runtimeData.ApplyModifiedPropertiesWithoutUndo();

                if (!runtime.TryValidatePersistentConfiguration(out var reason))
                {
                    throw new InvalidOperationException(
                        "NetworkRuntimeRoot 编辑器接线验证失败：" + reason);
                }

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            var contents = PrefabUtility.LoadPrefabContents(PrefabPath);
            contents.SetActive(true);
            PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath);
            PrefabUtility.UnloadPrefabContents(contents);
            return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        }

        private static void InstallSingleStartSceneInstance(GameObject prefab)
        {
            UiThemeBuildReadiness.InitializeRequiredTheme();
            var scene = EditorSceneManager.OpenScene(StartScenePath, OpenSceneMode.Single);
            RemoveMatchingRoots(scene, prefab);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            instance.name = RootName;
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        private static void RemoveGameSceneInstances(GameObject prefab)
        {
            UiThemeBuildReadiness.InitializeRequiredTheme();
            var scene = EditorSceneManager.OpenScene(GameScenePath, OpenSceneMode.Single);
            if (!RemoveMatchingRoots(scene, prefab))
            {
                return;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        private static bool RemoveMatchingRoots(Scene scene, GameObject prefab)
        {
            var removed = false;
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                var source = PrefabUtility.GetCorrespondingObjectFromSource(roots[i]);
                if (roots[i].name != RootName && source != prefab)
                {
                    continue;
                }

                Object.DestroyImmediate(roots[i]);
                removed = true;
            }

            return removed;
        }

        private static void SetObjectReference(
            SerializedObject serializedObject,
            string propertyName,
            Object value)
        {
            var property = serializedObject.FindProperty(propertyName);
            if (property == null)
            {
                throw new InvalidOperationException(
                    serializedObject.targetObject.GetType().Name +
                    " 缺少序列化字段 " + propertyName + "。");
            }

            property.objectReferenceValue = value;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            var separator = path.LastIndexOf('/');
            if (separator <= 0)
            {
                throw new InvalidOperationException("无法创建资产目录：" + path);
            }

            var parent = path.Substring(0, separator);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(separator + 1));
        }
    }
}
