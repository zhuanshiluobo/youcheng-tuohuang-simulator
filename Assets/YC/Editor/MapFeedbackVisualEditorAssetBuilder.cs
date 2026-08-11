using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using YC.Domain.Rules;
using Object = UnityEngine.Object;

namespace YC.Editor
{
    public static class MapFeedbackVisualEditorAssetBuilder
    {
        public const string ShaderPath =
            "Assets/YC/Presentation/Shaders/MapFeedbackAdditive.shader";
        public const string ShaderGuid = "f862740fe7814528b2d003d0fcb25858";
        public const string MaterialPath =
            "Assets/YC/Presentation/Materials/MapFeedbackAdditive.mat";
        public const string MaterialGuid = "788700548e2c44faacbb4f36fc68ce66";
        public const string AtlasPath =
            "Assets/YC/Presentation/Sprites/Map/MapVisuals.spriteatlas";
        public const string AtlasGuid = "7a21567e8dd54f05a50fb5a561a97aba";
        public const string ClipPath =
            "Assets/YC/Presentation/Animations/MapPlacementFeedback.anim";
        public const string ClipGuid = "464deaf1f8b243d0b60131265bc7547d";
        public const string ControllerPath =
            "Assets/YC/Presentation/Animations/MapPlacementFeedback.controller";
        public const string ControllerGuid = "d361023f743b4961ba0a45edeb1dc974";
        public const string SpriteLibraryPath =
            "Assets/YC/Presentation/Sprites/Map/MapVisualSprites.asset";
        public const string ShaderName = "YC/Map Feedback Additive";
        public const string StateName = "MapPlacementFeedback";
        public const string FlashPath = "Placement Flash";
        public const string RingPath = "Placement Expanding Ring";
        public const string CompletionEventName = "OnPlacementAnimationCompleted";
        public const float Duration = 0.15f;
        public const int ExpectedAtlasSpriteCount = 5;
        public const int ExpectedResourceIconSpriteCount = 5;

        internal static readonly string[] ResourceIconOverridePlatforms =
        {
            "Standalone", "WebGL", "Android", "iPhone", "tvOS", "Windows Store Apps"
        };

        internal static readonly AtlasSourceIdentity[] LockedAtlasSources =
        {
            new AtlasSourceIdentity("源岩", "1bbc1b71662b41ceb62ea42880a44838", 21300000L),
            new AtlasSourceIdentity("源石碎片", "1f0384ffcff74d3ca3bd89624fb4388e", 21300000L),
            new AtlasSourceIdentity("异铁", "0832e42990524a0294942ecbdb993e5a", 21300000L),
            new AtlasSourceIdentity("至纯源石", "c55ffa89550b4d33aedeeb6ff0605be3", 21300000L),
            new AtlasSourceIdentity("源岩x2", "8f1d6ce55ccf49dc9a0c65c86a894f22", 21300000L)
        };

        internal static readonly GeneratedSpriteIdentity[] LockedGeneratedSprites =
        {
            new GeneratedSpriteIdentity("MapHotspot"),
            new GeneratedSpriteIdentity("MapInfluenceEmpty"),
            new GeneratedSpriteIdentity("MapInfluenceOccupied"),
            new GeneratedSpriteIdentity("MapInfluenceBorder"),
            new GeneratedSpriteIdentity("MapPlacementRing"),
            new GeneratedSpriteIdentity("MapMobileCity"),
            new GeneratedSpriteIdentity("MapScoreMarker"),
            new GeneratedSpriteIdentity("MapScoreMarkerBorder")
        };

        internal static readonly ResourceTokenIdentity[] LockedResourceTokens =
        {
            new ResourceTokenIdentity(ResourceType.Originium, 0, "1bbc1b71662b41ceb62ea42880a44838", 21300000L),
            new ResourceTokenIdentity(ResourceType.OriginiumShard, 0, "1f0384ffcff74d3ca3bd89624fb4388e", 21300000L),
            new ResourceTokenIdentity(ResourceType.Iron, 0, "0832e42990524a0294942ecbdb993e5a", 21300000L),
            new ResourceTokenIdentity(ResourceType.PureOriginium, 0, "c55ffa89550b4d33aedeeb6ff0605be3", 21300000L),
            new ResourceTokenIdentity(ResourceType.Originium, 2, "8f1d6ce55ccf49dc9a0c65c86a894f22", 21300000L)
        };

        [MenuItem("YC/Build/Map Feedback Visuals/Rebuild Asset Files")]
        public static void RebuildAssetFilesMenu()
        {
            RebuildAssetFiles();
            MapFeedbackVisualBuildReadiness.ValidateGeneratedAssets();
            Debug.Log(
                "[MapFeedbackVisualEditorAssetBuilder] 已重建 Material、SpriteAtlas、AnimationClip 与 AnimatorController；" +
                "尚未改写 MapView Prefab。完成 Prefab 接线后请运行完整 readiness。");
        }

        [MenuItem("YC/Build/Map Feedback Visuals/Rebuild Assets And Configure Map View")]
        public static void RebuildAssetsAndConfigureMapViewMenu()
        {
            YC.EditorTools.MapViewEditorAssetBuilder.Rebuild();
            MapFeedbackVisualBuildReadiness.ValidateReadyForBuild();
            Debug.Log(
                "[MapFeedbackVisualEditorAssetBuilder] 已重建视觉资产，接线 99 Animator/297 renderer，" +
                "并通过地图与空间布局完整门禁。");
        }

        public static MapFeedbackVisualAssetSet RebuildAssetFiles()
        {
            EnsureFolder("Assets/YC/Presentation/Shaders");
            EnsureFolder("Assets/YC/Presentation/Materials");
            EnsureFolder("Assets/YC/Presentation/Animations");
            EnsureFolder("Assets/YC/Presentation/Sprites/Map");

            var shader = RequireAsset<Shader>(ShaderPath, ShaderGuid);
            if (shader.name != ShaderName)
            {
                throw new InvalidOperationException("地图反馈 Shader 名称与锁定名称不一致。");
            }

            var material = BuildMaterial(shader);
            var clip = BuildClip();
            var controller = BuildController(clip);
            ConfigureLockedResourceIconImporters();
            var sources = ResolveLockedAtlasSources();
            var atlas = BuildAtlas(sources);

            AssetDatabase.SaveAssets();
            SpriteAtlasUtility.PackAtlases(
                new[] { atlas },
                EditorUserBuildSettings.activeBuildTarget,
                false);
            AssetDatabase.SaveAssets();

            // Packing reimports both the atlas and its sources. Do not carry objects
            // from the pre-pack import generation into validation or prefab wiring.
            atlas = RequireAsset<SpriteAtlas>(AtlasPath, AtlasGuid);
            sources = ResolveLockedAtlasSources();
            for (var i = 0; i < sources.Count; i++)
            {
                if (!atlas.CanBindTo(sources[i]))
                {
                    throw new InvalidOperationException(
                        "MapVisuals SpriteAtlas 无法绑定锁定 Sprite：" + sources[i].name);
                }
            }

            var result = LoadRequiredAssets();
            return result;
        }

        public static MapFeedbackVisualAssetSet LoadRequiredAssets()
        {
            return new MapFeedbackVisualAssetSet(
                RequireAsset<Material>(MaterialPath, MaterialGuid),
                RequireAsset<SpriteAtlas>(AtlasPath, AtlasGuid),
                RequireAsset<AnimationClip>(ClipPath, ClipGuid),
                RequireAsset<AnimatorController>(ControllerPath, ControllerGuid));
        }

        internal static IReadOnlyList<Sprite> ResolveLockedAtlasSources()
        {
            var result = new List<Sprite>(LockedAtlasSources.Length);
            for (var i = 0; i < LockedAtlasSources.Length; i++)
            {
                var identity = LockedAtlasSources[i];
                var path = AssetDatabase.GUIDToAssetPath(identity.Guid);
                if (string.IsNullOrEmpty(path))
                {
                    throw new InvalidOperationException(
                        "无法由锁定 GUID 解析 Atlas Sprite：" + identity.Name);
                }

                Sprite match = null;
                var assets = AssetDatabase.LoadAllAssetsAtPath(path);
                for (var assetIndex = 0; assetIndex < assets.Length; assetIndex++)
                {
                    if (!(assets[assetIndex] is Sprite sprite) ||
                        !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                            sprite,
                            out var guid,
                            out long localId) ||
                        guid != identity.Guid || localId != identity.LocalId)
                    {
                        continue;
                    }

                    match = sprite;
                    break;
                }

                if (match == null || !string.Equals(match.name, identity.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Atlas Sprite 的名称/GUID/localID 已漂移：" + identity.Name);
                }

                result.Add(match);
            }

            if (result.Count != ExpectedAtlasSpriteCount || result.Distinct().Count() != result.Count)
            {
                throw new InvalidOperationException("Atlas 锁定 Sprite 必须恰好为 5 个且互不重复。");
            }

            return result;
        }

        private static void ConfigureLockedResourceIconImporters()
        {
            var configuredCount = 0;
            for (var i = 0; i < LockedAtlasSources.Length; i++)
            {
                var identity = LockedAtlasSources[i];
                if (identity.LocalId != 21300000L)
                {
                    continue;
                }

                configuredCount++;
                var path = AssetDatabase.GUIDToAssetPath(identity.Guid);
                var importer = string.IsNullOrEmpty(path)
                    ? null
                    : AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                {
                    throw new InvalidOperationException(
                        "Atlas resource icon GUID did not resolve to a TextureImporter: " + identity.Guid);
                }

                var changed = false;
                if (importer.textureCompression != TextureImporterCompression.Uncompressed ||
                    importer.crunchedCompression)
                {
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    importer.crunchedCompression = false;
                    changed = true;
                }

                var actualPlatforms = EnumerateTextureImporterPlatformNames(importer);
                for (var platformIndex = 0; platformIndex < actualPlatforms.Count; platformIndex++)
                {
                    var platform = actualPlatforms[platformIndex];
                    if (string.Equals(platform, "DefaultTexturePlatform", StringComparison.Ordinal) ||
                        !importer.GetPlatformTextureSettings(platform).overridden)
                    {
                        continue;
                    }
                    importer.ClearPlatformTextureSettings(platform);
                    changed = true;
                }

                // Keep the known Unity build-target aliases explicit as a forward-compatible
                // repair path even when an importer has not serialized a row for one yet.
                for (var platformIndex = 0;
                     platformIndex < ResourceIconOverridePlatforms.Length;
                     platformIndex++)
                {
                    var platform = ResourceIconOverridePlatforms[platformIndex];
                    if (!importer.GetPlatformTextureSettings(platform).overridden)
                    {
                        continue;
                    }
                    importer.ClearPlatformTextureSettings(platform);
                    changed = true;
                }

                if (changed)
                {
                    importer.SaveAndReimport();
                }
            }

            if (configuredCount != ExpectedResourceIconSpriteCount)
            {
                throw new InvalidOperationException(
                    "Locked Atlas resource icon count must be exactly " +
                    ExpectedResourceIconSpriteCount + ".");
            }
        }

        private static Material BuildMaterial(Shader shader)
        {
            var existing = AssetDatabase.LoadMainAssetAtPath(MaterialPath);
            if (existing != null && !(existing is Material))
            {
                throw new InvalidOperationException("地图反馈 Material 路径已被其他类型资产占用。");
            }

            var material = existing as Material;
            if (material == null)
            {
                material = new Material(shader) { name = "MapFeedbackAdditive" };
                AssetDatabase.CreateAsset(material, MaterialPath);
            }

            material.shader = shader;
            material.name = "MapFeedbackAdditive";
            material.renderQueue = 3000;
            material.enableInstancing = false;
            material.shaderKeywords = Array.Empty<string>();
            material.SetColor("_Color", Color.white);
            material.SetFloat("PixelSnap", 0f);
            material.SetFloat("_EnableExternalAlpha", 0f);
            material.SetTexture("_AlphaTex", null);
            material.SetTexture("_MainTex", null);
            ClearSerializedArray(material, "m_ValidKeywords", "m_InvalidKeywords", "m_DisabledShaderPasses");
            EditorUtility.SetDirty(material);
            RequireExpectedGuid(MaterialPath, MaterialGuid);
            return material;
        }

        private static AnimationClip BuildClip()
        {
            var existing = AssetDatabase.LoadMainAssetAtPath(ClipPath);
            if (existing != null && !(existing is AnimationClip))
            {
                throw new InvalidOperationException("地图反馈 AnimationClip 路径已被其他类型资产占用。");
            }

            var clip = existing as AnimationClip;
            if (clip == null)
            {
                clip = new AnimationClip { name = StateName };
                AssetDatabase.CreateAsset(clip, ClipPath);
            }

            var oldCurves = AnimationUtility.GetCurveBindings(clip);
            for (var i = 0; i < oldCurves.Length; i++)
            {
                AnimationUtility.SetEditorCurve(clip, oldCurves[i], null);
            }
            var oldObjectCurves = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            for (var i = 0; i < oldObjectCurves.Length; i++)
            {
                AnimationUtility.SetObjectReferenceCurve(clip, oldObjectCurves[i], null);
            }

            clip.name = StateName;
            clip.legacy = false;
            clip.frameRate = 60f;
            clip.wrapMode = WrapMode.Once;

            SetLinearCurve(clip, FlashPath, typeof(Transform), "m_LocalScale.x", 1.2f, 1f);
            SetLinearCurve(clip, FlashPath, typeof(Transform), "m_LocalScale.y", 1.2f, 1f);
            SetLinearCurve(clip, FlashPath, typeof(Transform), "m_LocalScale.z", 1.2f, 1f);
            SetLinearCurve(clip, FlashPath, typeof(SpriteRenderer), "m_Color.a", 1f, 0.35f);
            SetLinearCurve(clip, RingPath, typeof(Transform), "m_LocalScale.x", 1f, 1.8f);
            SetLinearCurve(clip, RingPath, typeof(Transform), "m_LocalScale.y", 1f, 1.8f);
            SetLinearCurve(clip, RingPath, typeof(Transform), "m_LocalScale.z", 1f, 1.8f);
            SetLinearCurve(clip, RingPath, typeof(SpriteRenderer), "m_Color.a", 1f, 0f);

            AnimationUtility.SetAnimationEvents(
                clip,
                new[]
                {
                    new AnimationEvent
                    {
                        time = Duration,
                        functionName = CompletionEventName,
                        messageOptions = SendMessageOptions.RequireReceiver
                    }
                });
            EditorUtility.SetDirty(clip);
            RequireExpectedGuid(ClipPath, ClipGuid);
            return clip;
        }

        private static void SetLinearCurve(
            AnimationClip clip,
            string path,
            Type type,
            string propertyName,
            float start,
            float end)
        {
            var binding = EditorCurveBinding.FloatCurve(path, type, propertyName);
            AnimationUtility.SetEditorCurve(
                clip,
                binding,
                AnimationCurve.Linear(0f, start, Duration, end));
        }

        private static AnimatorController BuildController(AnimationClip clip)
        {
            var existing = AssetDatabase.LoadMainAssetAtPath(ControllerPath);
            if (existing != null && !(existing is AnimatorController))
            {
                throw new InvalidOperationException("地图反馈 AnimatorController 路径已被其他类型资产占用。");
            }

            var controller = existing as AnimatorController;
            if (controller == null)
            {
                controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            }

            if (controller.layers.Length != 1 ||
                controller.layers[0].stateMachine == null ||
                controller.layers[0].stateMachine.stateMachines.Length != 0 ||
                controller.layers[0].stateMachine.anyStateTransitions.Length != 0 ||
                controller.layers[0].stateMachine.entryTransitions.Length != 0)
            {
                throw new InvalidOperationException(
                    "地图反馈 AnimatorController 必须保持单层、无嵌套状态机和无隐式跳转。请先修复目标资产。 ");
            }

            controller.parameters = Array.Empty<AnimatorControllerParameter>();
            var layer = controller.layers[0];
            layer.name = "Base Layer";
            // Unity serializes the unsynced Base Layer with defaultWeight 0;
            // its effective runtime weight remains 1.
            layer.defaultWeight = 0f;
            layer.avatarMask = null;
            layer.blendingMode = AnimatorLayerBlendingMode.Override;
            layer.iKPass = false;
            layer.syncedLayerIndex = -1;

            var stateMachine = layer.stateMachine;
            AnimatorState state = null;
            var childStates = stateMachine.states;
            for (var i = 0; i < childStates.Length; i++)
            {
                if (state == null && childStates[i].state.name == StateName)
                {
                    state = childStates[i].state;
                    continue;
                }
                stateMachine.RemoveState(childStates[i].state);
            }
            if (state == null)
            {
                state = stateMachine.AddState(StateName, new Vector3(280f, 120f, 0f));
            }
            var transitions = state.transitions;
            for (var i = 0; i < transitions.Length; i++)
            {
                state.RemoveTransition(transitions[i]);
            }

            state.name = StateName;
            state.motion = clip;
            state.speed = 1f;
            state.cycleOffset = 0f;
            state.mirror = false;
            state.speedParameterActive = false;
            state.mirrorParameterActive = false;
            state.cycleOffsetParameterActive = false;
            state.timeParameterActive = false;
            state.speedParameter = string.Empty;
            state.mirrorParameter = string.Empty;
            state.cycleOffsetParameter = string.Empty;
            state.timeParameter = string.Empty;
            state.iKOnFeet = false;
            state.writeDefaultValues = true;
            ClearSerializedArray(state, "m_StateMachineBehaviours");
            ClearSerializedArray(stateMachine, "m_StateMachineBehaviours");
            stateMachine.defaultState = state;
            controller.layers = new[] { layer };
            EditorUtility.SetDirty(state);
            EditorUtility.SetDirty(stateMachine);
            EditorUtility.SetDirty(controller);
            RequireExpectedGuid(ControllerPath, ControllerGuid);
            return controller;
        }

        private static SpriteAtlas BuildAtlas(IReadOnlyList<Sprite> sources)
        {
            var existing = AssetDatabase.LoadMainAssetAtPath(AtlasPath);
            if (existing != null && !(existing is SpriteAtlas))
            {
                throw new InvalidOperationException("地图视觉 SpriteAtlas 路径已被其他类型资产占用。");
            }

            var atlas = existing as SpriteAtlas;
            if (atlas == null)
            {
                atlas = new SpriteAtlas { name = "MapVisuals" };
                AssetDatabase.CreateAsset(atlas, AtlasPath);
            }

            var previous = SpriteAtlasExtensions.GetPackables(atlas);
            if (previous.Length > 0)
            {
                SpriteAtlasExtensions.Remove(atlas, previous);
            }
            SpriteAtlasExtensions.Add(atlas, sources.Cast<Object>().ToArray());
            SpriteAtlasExtensions.SetIncludeInBuild(atlas, true);

            var packing = SpriteAtlasExtensions.GetPackingSettings(atlas);
            packing.padding = 4;
            packing.blockOffset = 1;
            packing.enableRotation = false;
            packing.enableTightPacking = false;
            packing.enableAlphaDilation = false;
            SpriteAtlasExtensions.SetPackingSettings(atlas, packing);

            var texture = SpriteAtlasExtensions.GetTextureSettings(atlas);
            texture.anisoLevel = 1;
            texture.filterMode = FilterMode.Bilinear;
            texture.generateMipMaps = false;
            texture.readable = false;
            texture.sRGB = true;
            SpriteAtlasExtensions.SetTextureSettings(atlas, texture);

            var platform = SpriteAtlasExtensions.GetPlatformSettings(
                atlas,
                "DefaultTexturePlatform");
            platform.maxTextureSize = 2048;
            platform.resizeAlgorithm = TextureResizeAlgorithm.Mitchell;
            platform.format = TextureImporterFormat.Automatic;
            platform.textureCompression = TextureImporterCompression.Uncompressed;
            platform.compressionQuality = 100;
            platform.crunchedCompression = false;
            platform.overridden = false;
            SpriteAtlasExtensions.SetPlatformSettings(atlas, platform);

            var actualPlatforms = EnumerateSpriteAtlasPlatformNames(atlas);
            for (var platformIndex = 0; platformIndex < actualPlatforms.Count; platformIndex++)
            {
                var platformName = actualPlatforms[platformIndex];
                if (string.Equals(platformName, "DefaultTexturePlatform", StringComparison.Ordinal))
                {
                    continue;
                }

                var actualPlatform = SpriteAtlasExtensions.GetPlatformSettings(atlas, platformName);
                if (!actualPlatform.overridden)
                {
                    continue;
                }
                actualPlatform.overridden = false;
                actualPlatform.textureCompression = TextureImporterCompression.Uncompressed;
                actualPlatform.crunchedCompression = false;
                SpriteAtlasExtensions.SetPlatformSettings(atlas, actualPlatform);
            }

            for (var platformIndex = 0;
                 platformIndex < ResourceIconOverridePlatforms.Length;
                 platformIndex++)
            {
                var overridePlatform = SpriteAtlasExtensions.GetPlatformSettings(
                    atlas,
                    ResourceIconOverridePlatforms[platformIndex]);
                overridePlatform.overridden = false;
                overridePlatform.textureCompression = TextureImporterCompression.Uncompressed;
                overridePlatform.crunchedCompression = false;
                SpriteAtlasExtensions.SetPlatformSettings(atlas, overridePlatform);
            }

            EditorUtility.SetDirty(atlas);
            RequireExpectedGuid(AtlasPath, AtlasGuid);
            return atlas;
        }

        internal static IReadOnlyList<string> EnumerateTextureImporterPlatformNames(
            TextureImporter importer)
        {
            if (importer == null)
            {
                throw new ArgumentNullException(nameof(importer));
            }

            // Unity versions that expose GetAllPlatformSettings provide the most direct
            // view. Reflection keeps this editor code source-compatible across 2022 LTS
            // patch releases while the SerializedObject fallback still fails closed.
            var method = typeof(TextureImporter).GetMethod(
                "GetAllPlatformSettings",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                Type.EmptyTypes,
                null);
            if (method != null &&
                method.Invoke(importer, null) is TextureImporterPlatformSettings[] settings)
            {
                return NormalizePlatformNames(settings.Select(item => item.name));
            }

            return EnumerateSerializedPlatformNames(
                new SerializedObject(importer),
                "m_PlatformSettings",
                "platformSettings");
        }

        internal static IReadOnlyList<string> EnumerateSpriteAtlasPlatformNames(SpriteAtlas atlas)
        {
            if (atlas == null)
            {
                throw new ArgumentNullException(nameof(atlas));
            }
            return EnumerateSerializedPlatformNames(
                new SerializedObject(atlas),
                "m_EditorData.platformSettings");
        }

        private static IReadOnlyList<string> EnumerateSerializedPlatformNames(
            SerializedObject serialized,
            params string[] propertyPaths)
        {
            SerializedProperty platforms = null;
            for (var i = 0; i < propertyPaths.Length && platforms == null; i++)
            {
                platforms = serialized.FindProperty(propertyPaths[i]);
            }
            if (platforms == null || !platforms.isArray)
            {
                throw new InvalidOperationException(
                    serialized.targetObject.name + " 缺少可动态枚举的 platformSettings 数组。");
            }

            var names = new List<string>(platforms.arraySize);
            for (var i = 0; i < platforms.arraySize; i++)
            {
                var element = platforms.GetArrayElementAtIndex(i);
                var name = element.FindPropertyRelative("m_BuildTarget") ??
                           element.FindPropertyRelative("buildTarget");
                if (name == null || string.IsNullOrWhiteSpace(name.stringValue))
                {
                    throw new InvalidOperationException(
                        serialized.targetObject.name + " 含无法识别 buildTarget 的 platformSettings 项。");
                }
                names.Add(name.stringValue);
            }
            return NormalizePlatformNames(names);
        }

        private static IReadOnlyList<string> NormalizePlatformNames(IEnumerable<string> names)
        {
            var result = names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            if (!result.Contains("DefaultTexturePlatform", StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "platformSettings 必须显式包含 DefaultTexturePlatform。");
            }
            return result;
        }

        private static void ClearSerializedArray(Object target, params string[] propertyNames)
        {
            var serialized = new SerializedObject(target);
            for (var i = 0; i < propertyNames.Length; i++)
            {
                var property = serialized.FindProperty(propertyNames[i]);
                if (property == null && propertyNames[i] == "m_DisabledShaderPasses")
                {
                    property = serialized.FindProperty("disabledShaderPasses");
                }
                if (property == null || !property.isArray)
                {
                    throw new InvalidOperationException(
                        target.name + " 缺少可清理的序列化数组 " + propertyNames[i] + "。");
                }
                property.arraySize = 0;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static T RequireAsset<T>(string path, string expectedGuid) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                throw new InvalidOperationException("缺少地图反馈视觉资产：" + path);
            }
            RequireExpectedGuid(path, expectedGuid);
            return asset;
        }

        private static void RequireExpectedGuid(string path, string expectedGuid)
        {
            var actual = AssetDatabase.AssetPathToGUID(path);
            if (!string.Equals(actual, expectedGuid, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    path + " GUID 已漂移。预期 " + expectedGuid + "，实际 " + actual + "。");
            }
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
                throw new InvalidOperationException("无法创建视觉资产目录：" + path);
            }
            var parent = path.Substring(0, separator);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(separator + 1));
        }
    }

    public sealed class MapFeedbackVisualAssetSet
    {
        public MapFeedbackVisualAssetSet(
            Material feedbackMaterial,
            SpriteAtlas visualAtlas,
            AnimationClip placementClip,
            RuntimeAnimatorController placementController)
        {
            FeedbackMaterial = feedbackMaterial;
            VisualAtlas = visualAtlas;
            PlacementClip = placementClip;
            PlacementController = placementController;
        }

        public Material FeedbackMaterial { get; }
        public SpriteAtlas VisualAtlas { get; }
        public AnimationClip PlacementClip { get; }
        public RuntimeAnimatorController PlacementController { get; }
    }

    internal readonly struct AtlasSourceIdentity
    {
        public AtlasSourceIdentity(string name, string guid, long localId)
        {
            Name = name;
            Guid = guid;
            LocalId = localId;
        }

        public string Name { get; }
        public string Guid { get; }
        public long LocalId { get; }
    }

    internal readonly struct GeneratedSpriteIdentity
    {
        public GeneratedSpriteIdentity(string spriteName)
        {
            SpriteName = spriteName;
            TextureName = spriteName + "Texture";
        }

        public string SpriteName { get; }
        public string TextureName { get; }
    }

    internal readonly struct ResourceTokenIdentity
    {
        public ResourceTokenIdentity(ResourceType resourceType, int amount, string guid, long localId)
        {
            ResourceType = resourceType;
            Amount = amount;
            Guid = guid;
            LocalId = localId;
        }

        public ResourceType ResourceType { get; }
        public int Amount { get; }
        public string Guid { get; }
        public long LocalId { get; }
    }
}
