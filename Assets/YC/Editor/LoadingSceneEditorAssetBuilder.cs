using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation;

namespace YC.EditorTools
{
    public static class LoadingSceneEditorAssetBuilder
    {
        public const string ScenePath = "Assets/Scenes/LoadingScene.unity";
        internal static readonly Vector3 OriginiteInitialEuler =
            new Vector3(-19.6f, -178.5f, -177.1f);
        private const string ModelPath = "Assets/YC/Presentation/Models/Originite/Originite_Final.fbx";
        private const string ModelFolder = "Assets/YC/Presentation/Models/Originite";
        private const string RenderTexturePath = ModelFolder + "/OriginiteLoading.renderTexture";
        private const string ShellMaterialPath = ModelFolder + "/OriginiteShell.mat";
        private const string ShardMaterialPath = ModelFolder + "/OriginiteShard.mat";
        private const string ShardOneMaterialPath = ModelFolder + "/OriginiteShard01.mat";
        private const string ShardTwoMaterialPath = ModelFolder + "/OriginiteShard02.mat";
        private const string CoreMaterialPath = ModelFolder + "/OriginiteCore.mat";
        private const string CoreGlowMaterialPath = ModelFolder + "/OriginiteCoreGlow.mat";
        private const int LoadingModelLayer = 31;

        [MenuItem("Tools/YC/Rebuild Loading Scene")]
        public static void Rebuild()
        {
            RebuildScene();
            AssetDatabase.SaveAssets();
            Debug.Log("[LoadingSceneEditorAssetBuilder] Rebuilt " + ScenePath + ".");
        }

        public static void RebuildScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("Scene Transition", typeof(LoadingSceneController));
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.08f, 0.045f, 0.018f);
            RenderSettings.reflectionIntensity = 0.45f;

            var renderTexture = EnsureRenderTexture();
            var modelPivot = CreateOriginiteModel(scene, root.transform);

            var cameraObject = new GameObject("Loading Camera", typeof(Camera));
            cameraObject.transform.SetParent(root.transform, false);
            cameraObject.transform.localPosition = new Vector3(0f, 0f, -5f);
            var loadingCamera = cameraObject.GetComponent<Camera>();
            loadingCamera.clearFlags = CameraClearFlags.SolidColor;
            // The RawImage is composited over the full-screen black image. Keep the
            // render texture background transparent, otherwise its opaque black
            // square is blended a second time during the reveal and becomes visible.
            loadingCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            loadingCamera.cullingMask = 1 << LoadingModelLayer;
            loadingCamera.depth = -100f;
            loadingCamera.useOcclusionCulling = false;
            loadingCamera.allowHDR = true;
            loadingCamera.orthographic = true;
            loadingCamera.orthographicSize = 1.24f;
            loadingCamera.nearClipPlane = 0.1f;
            loadingCamera.farClipPlane = 20f;
            loadingCamera.targetTexture = renderTexture;

            CreateModelLight(
                "Originite Key Light",
                root.transform,
                new Vector3(-2.5f, 2.8f, -3f),
                new Color(1f, 0.82f, 0.65f),
                2f);
            CreateModelLight(
                "Originite Rim Light",
                root.transform,
                new Vector3(2.6f, -1.2f, -2f),
                new Color(1f, 0.58f, 0.3f),
                1.2f);

            var canvasObject = new GameObject(
                "Loading Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster),
                typeof(CanvasGroup));
            canvasObject.layer = LayerMask.NameToLayer("UI");
            canvasObject.transform.SetParent(root.transform, false);

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var canvasGroup = canvasObject.GetComponent<CanvasGroup>();
            canvasGroup.alpha = 0f;
            canvasGroup.blocksRaycasts = true;
            canvasGroup.interactable = true;

            var blackScreen = new GameObject("Black Screen", typeof(RectTransform), typeof(Image));
            blackScreen.layer = LayerMask.NameToLayer("UI");
            blackScreen.transform.SetParent(canvasObject.transform, false);
            var rect = blackScreen.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var image = blackScreen.GetComponent<Image>();
            image.color = Color.black;
            image.raycastTarget = true;

            var modelDisplay = CreateUiObject(
                "Originite Display",
                canvasObject.transform,
                typeof(RawImage),
                typeof(LoadingModelDragController));
            SetCenteredRect(
                modelDisplay.GetComponent<RectTransform>(),
                new Vector2(640f, 640f),
                new Vector2(0f, 56f));
            var rawImage = modelDisplay.GetComponent<RawImage>();
            rawImage.texture = renderTexture;
            rawImage.color = Color.white;
            rawImage.raycastTarget = false;
            modelDisplay.GetComponent<LoadingModelDragController>().ConfigureForEditor(modelPivot.transform);

            var progressRoot = CreateUiObject("Loading Progress", canvasObject.transform, typeof(CanvasGroup));
            var progressRect = progressRoot.GetComponent<RectTransform>();
            SetBottomCenteredRect(progressRect, new Vector2(760f, 110f), new Vector2(0f, 36f));
            var progressGroup = progressRoot.GetComponent<CanvasGroup>();
            progressGroup.alpha = 0f;
            progressGroup.blocksRaycasts = false;
            progressGroup.interactable = false;
            progressRoot.SetActive(false);

            var tipsText = CreateText(
                progressRoot.transform,
                "Tips Text",
                "拓荒提示：合理规划路线，可以更快连接城市。",
                20,
                new Vector2(-65f, 20f),
                new Vector2(570f, 38f));
            tipsText.fontStyle = FontStyle.Normal;
            tipsText.alignment = TextAnchor.MiddleLeft;
            tipsText.color = new Color(0.78f, 0.76f, 0.7f, 1f);
            var tipsGroup = tipsText.gameObject.AddComponent<CanvasGroup>();
            tipsGroup.alpha = 1f;

            var progressText = CreateText(
                progressRoot.transform,
                "Progress Text",
                "加载中 0%",
                20,
                new Vector2(310f, 20f),
                new Vector2(110f, 38f));
            progressText.alignment = TextAnchor.MiddleRight;

            var track = CreateUiObject("Progress Track", progressRoot.transform, typeof(Image));
            SetCenteredRect(track.GetComponent<RectTransform>(), new Vector2(700f, 10f), new Vector2(0f, -18f));
            track.GetComponent<Image>().color = new Color(0.2f, 0.18f, 0.14f, 1f);
            track.GetComponent<Image>().raycastTarget = false;

            var fill = CreateUiObject("Progress Fill", track.transform, typeof(Image));
            var fillRect = fill.GetComponent<RectTransform>();
            fillRect.anchorMin = new Vector2(0f, 0.5f);
            fillRect.anchorMax = new Vector2(0f, 0.5f);
            fillRect.pivot = new Vector2(0f, 0.5f);
            fillRect.anchoredPosition = Vector2.zero;
            fillRect.sizeDelta = new Vector2(0f, 10f);
            fill.GetComponent<Image>().color = new Color(0.35f, 0.82f, 0.78f, 1f);
            fill.GetComponent<Image>().raycastTarget = false;

            root.GetComponent<LoadingSceneController>().ConfigureForEditor(
                loadingCamera,
                modelPivot,
                modelDisplay,
                canvasGroup,
                progressRoot,
                progressGroup,
                progressRect,
                progressText,
                tipsText,
                tipsGroup,
                fillRect,
                700f,
                0.8f,
                1.04f);
            EditorSceneManager.SaveScene(scene, ScenePath);
            EnsureInBuildSettings();
        }

        internal static GameObject CreateOriginiteModel(
            UnityEngine.SceneManagement.Scene scene,
            Transform parent)
        {
            var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (modelAsset == null)
            {
                throw new System.InvalidOperationException("无法加载加载场景源石模型：" + ModelPath);
            }

            var pivot = new GameObject("Originite Model Pivot");
            pivot.transform.SetParent(parent, false);
            SetLayerRecursively(pivot, LoadingModelLayer);

            var model = new GameObject("Originite Model");
            model.transform.SetParent(pivot.transform, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one;
            SetLayerRecursively(model, LoadingModelLayer);

            var geometry = PrefabUtility.InstantiatePrefab(modelAsset, scene) as GameObject;
            if (geometry == null)
            {
                throw new System.InvalidOperationException("无法实例化加载场景源石模型：" + ModelPath);
            }

            geometry.name = "Originite Geometry";
            geometry.transform.SetParent(model.transform, false);
            geometry.transform.localPosition = Vector3.zero;
            geometry.transform.localRotation = Quaternion.identity;
            geometry.transform.localScale = Vector3.one;
            SetLayerRecursively(geometry, LoadingModelLayer);
            ApplyModelMaterials(geometry);
            // Calculate the geometric bounds in pivot-local space. This keeps the
            // source stone centred without moving either user-facing root transform.
            CenterModel(geometry, pivot.transform);
            // Keep the user-approved initial pose stable when either scene is rebuilt.
            pivot.transform.localRotation = Quaternion.Euler(OriginiteInitialEuler);
            var serializedPivot = new SerializedObject(pivot.transform);
            serializedPivot.FindProperty("m_LocalEulerAnglesHint").vector3Value = OriginiteInitialEuler;
            serializedPivot.ApplyModifiedPropertiesWithoutUndo();
            return pivot;
        }

        private static void CenterModel(GameObject model, Transform pivot)
        {
            var renderers = model.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                return;
            }

            var hasPoint = false;
            var pivotLocalBounds = new Bounds();
            foreach (var renderer in renderers)
            {
                Bounds meshBounds;
                if (renderer is SkinnedMeshRenderer skinnedRenderer)
                {
                    meshBounds = skinnedRenderer.localBounds;
                }
                else
                {
                    var meshFilter = renderer.GetComponent<MeshFilter>();
                    if (meshFilter == null || meshFilter.sharedMesh == null)
                    {
                        continue;
                    }

                    meshBounds = meshFilter.sharedMesh.bounds;
                }

                var center = meshBounds.center;
                var extents = meshBounds.extents;
                for (var x = -1; x <= 1; x += 2)
                {
                    for (var y = -1; y <= 1; y += 2)
                    {
                        for (var z = -1; z <= 1; z += 2)
                        {
                            var meshLocalCorner = center + Vector3.Scale(
                                extents,
                                new Vector3(x, y, z));
                            var worldCorner = renderer.transform.TransformPoint(meshLocalCorner);
                            var pivotLocalCorner = pivot.InverseTransformPoint(worldCorner);
                            if (!hasPoint)
                            {
                                pivotLocalBounds = new Bounds(pivotLocalCorner, Vector3.zero);
                                hasPoint = true;
                            }
                            else
                            {
                                pivotLocalBounds.Encapsulate(pivotLocalCorner);
                            }
                        }
                    }
                }
            }

            if (hasPoint)
            {
                model.transform.localPosition -= pivotLocalBounds.center;
            }
        }

        private static void ApplyModelMaterials(GameObject model)
        {
            var shell = EnsureGlassMaterial();
            var shardZero = EnsureMaterial(
                ShardMaterialPath,
                new Color(0.48f, 0.34f, 0.12f, 1f),
                new Color(0.3f, 0.18f, 0.035f, 1f),
                0.75f,
                0.8f,
                false);
            var shardOne = EnsureMaterial(
                ShardOneMaterialPath,
                new Color(0.38f, 0.3f, 0.12f, 1f),
                new Color(0.18f, 0.12f, 0.03f, 1f),
                0.78f,
                0.7f,
                false);
            var shardTwo = EnsureMaterial(
                ShardTwoMaterialPath,
                new Color(0.52f, 0.24f, 0.06f, 1f),
                new Color(0.45f, 0.16f, 0.015f, 1f),
                0.78f,
                0.85f,
                false);
            var core = EnsureMaterial(
                CoreMaterialPath,
                new Color(1f, 0.7f, 0.3f, 1f),
                new Color(2f, 1.4f, 0.5f, 1f),
                0f,
                0.5f,
                false);
            var coreGlow = EnsureCoreGlowMaterial();

            var renderers = model.GetComponentsInChildren<Renderer>(true);
            for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var renderer = renderers[rendererIndex];
                var sourceMaterials = renderer.sharedMaterials;
                var assignedMaterials = new Material[sourceMaterials.Length];
                for (var materialIndex = 0; materialIndex < sourceMaterials.Length; materialIndex++)
                {
                    var sourceName = sourceMaterials[materialIndex] == null
                        ? string.Empty
                        : sourceMaterials[materialIndex].name;
                    if (sourceName == "Rhombus")
                    {
                        assignedMaterials[materialIndex] = core;
                    }
                    else if (sourceName == "Crystal")
                    {
                        assignedMaterials[materialIndex] = shell;
                    }
                    else if (sourceName == "shard_001_material")
                    {
                        assignedMaterials[materialIndex] = shardOne;
                    }
                    else if (sourceName == "shard_002_material")
                    {
                        assignedMaterials[materialIndex] = shardTwo;
                    }
                    else
                    {
                        assignedMaterials[materialIndex] = shardZero;
                    }
                }

                renderer.sharedMaterials = assignedMaterials;
            }

            CreateCoreGlow(model, coreGlow);
        }

        private static Material EnsureGlassMaterial()
        {
            var shader = Shader.Find("YC/Originite Glass");
            if (shader == null)
            {
                throw new System.InvalidOperationException("当前项目缺少 YC/Originite Glass Shader。");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(ShellMaterialPath);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, ShellMaterialPath);
            }

            material.shader = shader;
            material.SetColor("_Tint", new Color(0.01f, 0.004f, 0.001f, 1f));
            material.SetColor("_EdgeColor", new Color(0.16f, 0.035f, 0.005f, 1f));
            material.SetFloat("_Opacity", 0.82f);
            material.SetFloat("_Refraction", 0.012f);
            material.SetFloat("_FresnelPower", 2.6f);
            material.SetFloat("_EdgeStrength", 0.45f);
            material.SetFloat("_InteriorBrightness", 1.05f);
            material.renderQueue = 3050;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Material EnsureCoreGlowMaterial()
        {
            var shader = Shader.Find("YC/Originite Core Glow");
            if (shader == null)
            {
                throw new System.InvalidOperationException("当前项目缺少 YC/Originite Core Glow Shader。");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(CoreGlowMaterialPath);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, CoreGlowMaterialPath);
            }

            material.shader = shader;
            material.SetColor("_Color", new Color(1f, 0.45f, 0.15f, 1f));
            material.SetFloat("_Intensity", 0.25f);
            material.SetFloat("_FresnelPower", 2f);
            material.renderQueue = 3025;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void CreateCoreGlow(GameObject model, Material material)
        {
            var core = model.GetComponentsInChildren<MeshRenderer>(true)
                .FirstOrDefault(renderer => renderer.name == "core_rhombus");
            if (core == null)
            {
                return;
            }

            var sourceFilter = core.GetComponent<MeshFilter>();
            if (sourceFilter == null || sourceFilter.sharedMesh == null)
            {
                return;
            }

            var glowObject = new GameObject("Originite Core Glow", typeof(MeshFilter), typeof(MeshRenderer));
            glowObject.layer = LoadingModelLayer;
            glowObject.transform.SetParent(core.transform.parent, false);
            glowObject.transform.localPosition = core.transform.localPosition;
            glowObject.transform.localRotation = core.transform.localRotation;
            glowObject.transform.localScale = core.transform.localScale * 1.035f;
            glowObject.GetComponent<MeshFilter>().sharedMesh = sourceFilter.sharedMesh;
            glowObject.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static Material EnsureMaterial(
            string path,
            Color color,
            Color emission,
            float metallic,
            float smoothness,
            bool transparent)
        {
            var shader = Shader.Find("Standard");
            if (shader == null)
            {
                throw new System.InvalidOperationException("当前项目缺少 Standard Shader。");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;
            material.color = color;
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Glossiness", smoothness);
            material.SetColor("_EmissionColor", emission);
            if (transparent)
            {
                material.SetFloat("_Mode", 3f);
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.DisableKeyword("_ALPHATEST_ON");
                material.DisableKeyword("_ALPHABLEND_ON");
                material.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            else
            {
                material.SetFloat("_Mode", 0f);
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                material.SetInt("_ZWrite", 1);
                material.DisableKeyword("_ALPHATEST_ON");
                material.DisableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.renderQueue = -1;
            }

            if (emission.maxColorComponent > 0.0001f)
            {
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                material.EnableKeyword("_EMISSION");
            }
            else
            {
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                material.DisableKeyword("_EMISSION");
            }

            EditorUtility.SetDirty(material);
            return material;
        }

        private static RenderTexture EnsureRenderTexture()
        {
            var renderTexture = AssetDatabase.LoadAssetAtPath<RenderTexture>(RenderTexturePath);
            if (renderTexture != null)
            {
                if (renderTexture.format != RenderTextureFormat.ARGBHalf || renderTexture.antiAliasing != 4)
                {
                    renderTexture.Release();
                    renderTexture.format = RenderTextureFormat.ARGBHalf;
                    renderTexture.antiAliasing = 4;
                    EditorUtility.SetDirty(renderTexture);
                }
                return renderTexture;
            }

            renderTexture = new RenderTexture(768, 768, 24, RenderTextureFormat.ARGBHalf)
            {
                name = "Originite Loading Render Texture",
                antiAliasing = 4,
                useMipMap = false,
                autoGenerateMips = false
            };
            AssetDatabase.CreateAsset(renderTexture, RenderTexturePath);
            return renderTexture;
        }

        private static void CreateModelLight(
            string name,
            Transform parent,
            Vector3 position,
            Color color,
            float intensity)
        {
            var lightObject = new GameObject(name, typeof(Light));
            lightObject.transform.SetParent(parent, false);
            lightObject.transform.localPosition = position;
            var light = lightObject.GetComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.range = 10f;
            light.cullingMask = 1 << LoadingModelLayer;
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            root.layer = layer;
            for (var index = 0; index < root.transform.childCount; index++)
            {
                SetLayerRecursively(root.transform.GetChild(index).gameObject, layer);
            }
        }

        private static Text CreateText(
            Transform parent,
            string name,
            string value,
            int fontSize,
            Vector2 position,
            Vector2 size)
        {
            var textObject = CreateUiObject(name, parent, typeof(Text));
            SetCenteredRect(textObject.GetComponent<RectTransform>(), size, position);
            var text = textObject.GetComponent<Text>();
            text.text = value;
            text.font = UiEditorAssetReferences.CjkFont;
            text.fontSize = fontSize;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(0.86f, 0.75f, 0.55f, 1f);
            text.raycastTarget = false;
            return text;
        }

        private static GameObject CreateUiObject(string name, Transform parent, params System.Type[] components)
        {
            var types = new System.Type[components.Length + 1];
            types[0] = typeof(RectTransform);
            System.Array.Copy(components, 0, types, 1, components.Length);
            var gameObject = new GameObject(name, types);
            gameObject.layer = LayerMask.NameToLayer("UI");
            gameObject.transform.SetParent(parent, false);
            return gameObject;
        }

        private static void SetCenteredRect(RectTransform rect, Vector2 size, Vector2 position)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static void SetBottomCenteredRect(RectTransform rect, Vector2 size, Vector2 position)
        {
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static void EnsureInBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (scenes.Any(scene => scene.path == ScenePath))
            {
                return;
            }

            var loadingScene = new EditorBuildSettingsScene(ScenePath, true);
            var startSceneIndex = scenes.FindIndex(scene => scene.path == StartMenuEditorAssetBuilder.ScenePath);
            scenes.Insert(startSceneIndex < 0 ? 0 : startSceneIndex + 1, loadingScene);
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
