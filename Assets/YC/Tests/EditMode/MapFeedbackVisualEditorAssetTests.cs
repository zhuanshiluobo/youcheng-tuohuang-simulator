using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace YC.Tests.EditMode
{
    public sealed class MapFeedbackVisualEditorAssetTests
    {
        private const string ShaderPath =
            "Assets/YC/Presentation/Shaders/MapFeedbackAdditive.shader";
        private const string MaterialPath =
            "Assets/YC/Presentation/Materials/MapFeedbackAdditive.mat";
        private const string AtlasPath =
            "Assets/YC/Presentation/Sprites/Map/MapVisuals.spriteatlas";
        private const string ClipPath =
            "Assets/YC/Presentation/Animations/MapPlacementFeedback.anim";
        private const string ControllerPath =
            "Assets/YC/Presentation/Animations/MapPlacementFeedback.controller";

        [Test]
        public void AssetFiles_HaveLockedGuidsAndLoadAsProductionTypes()
        {
            AssertAsset<Shader>(ShaderPath, "f862740fe7814528b2d003d0fcb25858");
            AssertAsset<Material>(MaterialPath, "788700548e2c44faacbb4f36fc68ce66");
            AssertAsset<SpriteAtlas>(AtlasPath, "7a21567e8dd54f05a50fb5a561a97aba");
            AssertAsset<AnimationClip>(ClipPath, "464deaf1f8b243d0b60131265bc7547d");
            AssertAsset<AnimatorController>(ControllerPath, "d361023f743b4961ba0a45edeb1dc974");
        }

        [Test]
        public void VisualAtlas_ContainsOnlyFivePersistentResourceIconSprites()
        {
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(AtlasPath);
            var packables = SpriteAtlasExtensions.GetPackables(atlas);
            Assert.That(packables, Has.Length.EqualTo(5));
            var expectedGuids = new[]
            {
                "1bbc1b71662b41ceb62ea42880a44838",
                "1f0384ffcff74d3ca3bd89624fb4388e",
                "0832e42990524a0294942ecbdb993e5a",
                "c55ffa89550b4d33aedeeb6ff0605be3",
                "8f1d6ce55ccf49dc9a0c65c86a894f22"
            };
            var actualGuids = packables.Select(item =>
            {
                Assert.That(
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(item, out var guid, out long localId),
                    Is.True);
                Assert.That(localId, Is.EqualTo(21300000L));
                return guid;
            });
            Assert.That(actualGuids, Is.EquivalentTo(expectedGuids));
            foreach (var platform in new[]
                     {
                         "Standalone", "WebGL", "Android", "iPhone", "tvOS", "Windows Store Apps"
                     })
            {
                Assert.That(
                    SpriteAtlasExtensions.GetPlatformSettings(atlas, platform).overridden,
                    Is.False,
                    "Atlas / " + platform);
            }
            foreach (var guid in expectedGuids)
            {
                var importer = AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid)) as TextureImporter;
                Assert.That(importer, Is.Not.Null, guid);
                Assert.That(importer.textureCompression, Is.EqualTo(TextureImporterCompression.Uncompressed));
                Assert.That(importer.crunchedCompression, Is.False);
                foreach (var platform in new[]
                         {
                             "Standalone", "WebGL", "Android", "iPhone", "tvOS", "Windows Store Apps"
                         })
                {
                    Assert.That(importer.GetPlatformTextureSettings(platform).overridden, Is.False,
                        guid + " / " + platform);
                }
            }
        }

        [Test]
        public void BlendDirectiveGate_RejectsCommentDecoysInvalidScopesWrongBlendAndAdditionalPasses()
        {
            var validate = GetReadinessMethod("ValidatePremultipliedBlendDirectives");
            AssertStringValidatorAccepts(
                validate,
                BuildShaderSource(
                    "// Blend One Zero\nBlend One One",
                    "#pragma fragment SpriteFrag",
                    "// #include \"Wrong.cginc\"\n#include \"UnitySprites.cginc\""));
            AssertStringValidatorAccepts(
                validate,
                BuildShaderSource(
                    "/* Blend One Zero */\nBlend One One",
                    "#pragma fragment SpriteFrag",
                    "#include \"UnitySprites.cginc\"",
                    "\"QuotedBlend\" = \"Blend One Zero\""));
            AssertStringValidatorRejects(
                validate,
                BuildShaderSource("// Blend One One\nBlend One Zero"));
            AssertStringValidatorRejects(
                validate,
                BuildShaderSource("Blend One One", "#pragma fragment CustomFrag"));
            AssertStringValidatorRejects(
                validate,
                BuildShaderSource("Blend One One", "#pragma fragment SpriteFrag",
                    "#include \"CustomSprites.cginc\""));
            AssertStringValidatorRejects(
                validate,
                BuildShaderSource("Blend One One", extraPass: "Pass { }") );
            AssertStringValidatorRejects(
                validate,
                BuildShaderSource("Blend One One\nBlend One One"));
        }

        [Test]
        public void SavedSceneOverrideGate_RejectsChildFeedbackMaterialAndLibraryOverrides()
        {
            const string guid = "11111111111111111111111111111111";
            const long rootGameObjectId = 101;
            const long rootTransformId = 202;
            var validate = GetReadinessMethod("ValidateSavedMapViewInstanceYaml");
            var clean = BuildMapViewInstanceYaml(guid, rootGameObjectId, rootTransformId, string.Empty);
            AssertSceneYamlAccepts(validate, clean, guid, rootGameObjectId, rootTransformId);

            AssertSceneYamlRejects(
                validate,
                clean.Replace("m_TransformParent: {fileID: 77}", "m_TransformParent: {fileID: 78}"),
                guid,
                rootGameObjectId,
                rootTransformId);
            AssertSceneYamlRejects(
                validate,
                clean.Replace(
                    "propertyPath: m_LocalPosition.x\n      value: 0",
                    "propertyPath: m_LocalPosition.x\n      value: 9"),
                guid,
                rootGameObjectId,
                rootTransformId);

            AssertSceneYamlRejects(
                validate,
                BuildMapViewInstanceYaml(
                    guid,
                    rootGameObjectId,
                    rootTransformId,
                    BuildModification(rootTransformId, guid, "m_LocalPosition.x")),
                guid,
                rootGameObjectId,
                rootTransformId);
            AssertSceneYamlRejects(
                validate,
                clean.Replace(
                    "m_RemovedComponents: []",
                    "m_RemovedComponents:\n    - {fileID: 900}"),
                guid,
                rootGameObjectId,
                rootTransformId);
            AssertSceneYamlRejects(
                validate,
                clean.Replace(
                    "m_AddedComponents: []",
                    "m_AddedComponents:\n    - targetCorrespondingSourceObject: {fileID: 900}"),
                guid,
                rootGameObjectId,
                rootTransformId);
            AssertSceneYamlRejects(
                validate,
                clean.Replace(
                    "propertyPath: m_LocalRotation.w\n      value: 1\n      objectReference: {fileID: 0}",
                    "propertyPath: m_LocalRotation.w\n      value: 1\n      objectReference: {fileID: 900}"),
                guid,
                rootGameObjectId,
                rootTransformId);

            foreach (var forbiddenPath in new[]
                     {
                         "flashRenderer", "m_Materials.Array.data[0]", "spriteLibrary"
                     })
            {
                var malicious = BuildMapViewInstanceYaml(
                    guid,
                    rootGameObjectId,
                    rootTransformId,
                    BuildModification(999, guid, forbiddenPath));
                AssertSceneYamlRejects(validate, malicious, guid, rootGameObjectId, rootTransformId);
            }
        }

        [Test]
        public void CanonicalSpriteLibraryGate_RejectsAnotherLibraryObject()
        {
            var validate = GetReadinessMethod("ValidateCanonicalSpriteLibrary");
            var viewType = Type.GetType("YC.Presentation.MapView, Assembly-CSharp", true);
            var canonical = AssetDatabase.LoadMainAssetAtPath(
                "Assets/YC/Presentation/Sprites/Map/MapVisualSprites.asset") as ScriptableObject;
            Assert.That(canonical, Is.Not.Null);
            var libraryType = canonical.GetType();
            var owner = new GameObject("Sprite Library Contract");
            owner.SetActive(false);
            var replacement = ScriptableObject.CreateInstance(libraryType);
            try
            {
                var view = owner.AddComponent(viewType) as Component;
                SetObjectReference(view, "spriteLibrary", canonical);
                Assert.DoesNotThrow(() => validate.Invoke(null, new object[] { view, canonical }));
                var exception = Assert.Throws<TargetInvocationException>(
                    () => validate.Invoke(null, new object[] { view, replacement }));
                Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(replacement);
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void ResourceTokenMappingGate_RejectsDuplicateSwapAndKeyDrift()
        {
            var validate = GetReadinessMethod("ValidateResourceTokenMappings");
            var canonical = AssetDatabase.LoadMainAssetAtPath(
                "Assets/YC/Presentation/Sprites/Map/MapVisualSprites.asset") as ScriptableObject;
            Assert.That(canonical, Is.Not.Null);
            var temporary = UnityEngine.Object.Instantiate(canonical);
            try
            {
                AssertObjectValidatorAccepts(validate, temporary);
                var serialized = new SerializedObject(temporary);
                var mappings = serialized.FindProperty("resourceTokens");
                Assert.That(mappings, Is.Not.Null);
                Assert.That(mappings.arraySize, Is.EqualTo(5));

                var first = mappings.GetArrayElementAtIndex(0);
                var second = mappings.GetArrayElementAtIndex(1);
                var originalSecondType = second.FindPropertyRelative("resourceType").enumValueIndex;
                var originalSecondAmount = second.FindPropertyRelative("amount").intValue;
                second.FindPropertyRelative("resourceType").enumValueIndex =
                    first.FindPropertyRelative("resourceType").enumValueIndex;
                second.FindPropertyRelative("amount").intValue =
                    first.FindPropertyRelative("amount").intValue;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssertObjectValidatorRejects(validate, temporary);
                second.FindPropertyRelative("resourceType").enumValueIndex = originalSecondType;
                second.FindPropertyRelative("amount").intValue = originalSecondAmount;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssertObjectValidatorAccepts(validate, temporary);

                var firstSprite = first.FindPropertyRelative("sprite").objectReferenceValue;
                var secondSprite = second.FindPropertyRelative("sprite").objectReferenceValue;
                first.FindPropertyRelative("sprite").objectReferenceValue = secondSprite;
                second.FindPropertyRelative("sprite").objectReferenceValue = firstSprite;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssertObjectValidatorRejects(validate, temporary);
                first.FindPropertyRelative("sprite").objectReferenceValue = firstSprite;
                second.FindPropertyRelative("sprite").objectReferenceValue = secondSprite;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssertObjectValidatorAccepts(validate, temporary);

                first.FindPropertyRelative("amount").intValue = 99;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssertObjectValidatorRejects(validate, temporary);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temporary);
            }
        }

        [Test]
        public void ShaderAndRuntimeSource_LockPremultipliedAdditiveAndAnimatorDrivenSemantics()
        {
            var shaderSource = File.ReadAllText(ShaderPath);
            Assert.That(shaderSource, Does.Contain("\"CanUseSpriteAtlas\" = \"True\""));
            Assert.That(shaderSource, Does.Contain("Blend One One"));
            Assert.That(shaderSource, Does.Not.Contain("Blend SrcAlpha One"));
            Assert.That(shaderSource, Does.Contain("UnitySprites.cginc"));

            var feedbackSource = File.ReadAllText(
                "Assets/YC/Presentation/MapPlacementFeedback.cs");
            Assert.That(feedbackSource, Does.Contain("AnimatorUpdateMode.UnscaledTime"));
            Assert.That(feedbackSource, Does.Contain("animator.Play"));
            Assert.That(feedbackSource, Does.Contain("animator.Update(0f)"));
            Assert.That(feedbackSource, Does.Contain("OnPlacementAnimationCompleted"));
            Assert.That(feedbackSource, Does.Not.Contain("StartCoroutine("));
            Assert.That(feedbackSource, Does.Not.Contain("Time.unscaledDeltaTime"));

            var clipYaml = File.ReadAllText(ClipPath);
            Assert.That(clipYaml, Does.Contain("functionName: OnPlacementAnimationCompleted"));
            Assert.That(clipYaml, Does.Contain("messageOptions: 0"));
            Assert.That(clipYaml, Does.Not.Contain("messageOptions: 1"));
        }

        [Test]
        public void MaterialGate_RejectsKeywordFloatTextureAndDisabledPassMutations()
        {
            AssertMaterialMutationRejected(material =>
                material.shaderKeywords = new[] { "PIXELSNAP_ON" });
            AssertMaterialMutationRejected(material => material.SetFloat("PixelSnap", 1f));
            AssertMaterialMutationRejected(material => material.SetFloat("_EnableExternalAlpha", 1f));
            AssertMaterialMutationRejected(material =>
                material.SetTexture("_MainTex", Texture2D.whiteTexture));
            AssertMaterialMutationRejected(material =>
                material.SetTexture("_AlphaTex", Texture2D.whiteTexture));
            AssertMaterialMutationRejected(material =>
            {
                var serialized = new SerializedObject(material);
                var disabled = serialized.FindProperty("m_DisabledShaderPasses");
                if (disabled == null)
                {
                    disabled = serialized.FindProperty("disabledShaderPasses");
                }
                Assert.That(disabled, Is.Not.Null);
                disabled.arraySize = 1;
                disabled.GetArrayElementAtIndex(0).stringValue = "Always";
                serialized.ApplyModifiedPropertiesWithoutUndo();
            });
        }

        [Test]
        public void ControllerGate_RejectsRawBehavioursSyncAndParameterActivation()
        {
            var validate = GetReadinessMethod("ValidateControllerRawContract");
            var valid = new object[]
            {
                -1, 0, 0, false, false, false, false,
                string.Empty, string.Empty, string.Empty, string.Empty
            };
            AssertValidatorAccepts(validate, valid);

            AssertControllerRawMutationRejected(validate, valid, 0, 0, "syncedLayerIndex=0");
            AssertControllerRawMutationRejected(validate, valid, 1, 1, "stateMachineBehaviourCount=1");
            AssertControllerRawMutationRejected(validate, valid, 2, 1, "stateBehaviourCount=1");
            AssertControllerRawMutationRejected(validate, valid, 3, true, "speedParameterActive=true");
            AssertControllerRawMutationRejected(validate, valid, 4, true, "mirrorParameterActive=true");
            AssertControllerRawMutationRejected(validate, valid, 5, true, "cycleOffsetParameterActive=true");
            AssertControllerRawMutationRejected(validate, valid, 6, true, "timeParameterActive=true");
            AssertControllerRawMutationRejected(validate, valid, 7, "Speed", "speedParameter=Speed");
            AssertControllerRawMutationRejected(validate, valid, 8, "Mirror", "mirrorParameter=Mirror");
            AssertControllerRawMutationRejected(validate, valid, 9, "Cycle", "cycleOffsetParameter=Cycle");
            AssertControllerRawMutationRejected(validate, valid, 10, "Time", "timeParameter=Time");
        }

        [Test]
        public void BuilderAndReadiness_ExposeAssetOnlyAndFullProductionGates()
        {
            var builder = Type.GetType(
                "YC.Editor.MapFeedbackVisualEditorAssetBuilder, Assembly-CSharp-Editor",
                false);
            var readiness = Type.GetType(
                "YC.Editor.MapFeedbackVisualBuildReadiness, Assembly-CSharp-Editor",
                false);
            Assert.That(builder, Is.Not.Null);
            Assert.That(readiness, Is.Not.Null);
            Assert.That(
                builder.GetMethod("RebuildAssetFiles", BindingFlags.Public | BindingFlags.Static),
                Is.Not.Null);
            Assert.That(
                readiness.GetMethod("ValidateGeneratedAssets", BindingFlags.Public | BindingFlags.Static),
                Is.Not.Null);
            Assert.That(
                readiness.GetMethod("ValidateReadyForBuild", BindingFlags.Public | BindingFlags.Static),
                Is.Not.Null);
            var builderSource = File.ReadAllText(
                "Assets/YC/Editor/MapFeedbackVisualEditorAssetBuilder.cs");
            var readinessSource = File.ReadAllText(
                "Assets/YC/Editor/MapFeedbackVisualBuildReadiness.cs");
            Assert.That(builderSource, Does.Contain("ClearPlatformTextureSettings"));
            Assert.That(readinessSource, Does.Contain("GetPlatformTextureSettings(platform).overridden"));
            Assert.That(readinessSource, Does.Contain("SpriteLibraryPath"));
        }

        [Test]
        public void FeedbackTopologyGate_RejectsCrossOwnerNameParentPulseAndAnimatorBypasses()
        {
            var readiness = Type.GetType(
                "YC.Editor.MapFeedbackVisualBuildReadiness, Assembly-CSharp-Editor",
                true);
            var validate = readiness.GetMethod(
                "ValidateFeedbackTopology",
                BindingFlags.NonPublic | BindingFlags.Static);
            var feedbackType = Type.GetType(
                "YC.Presentation.MapPlacementFeedback, Assembly-CSharp",
                true);
            var pulseType = Type.GetType(
                "YC.Presentation.MapHighlightPulse, Assembly-CSharp",
                true);
            Assert.That(validate, Is.Not.Null);

            var root = new GameObject("Feedback Topology Contract");
            root.SetActive(false);
            try
            {
                CreateFeedbackOwner(
                    root.transform,
                    feedbackType,
                    "Owner A",
                    out var feedbackA,
                    out var flashA,
                    out var ringA);
                CreateFeedbackOwner(
                    root.transform,
                    feedbackType,
                    "Owner B",
                    out var feedbackB,
                    out var flashB,
                    out _);

                var pulseObject = new GameObject("Pulse");
                pulseObject.transform.SetParent(root.transform, false);
                var pulseBorder = pulseObject.AddComponent<SpriteRenderer>();
                var pulse = pulseObject.AddComponent(pulseType) as Component;
                SetObjectReference(pulse, "border", pulseBorder);

                AssertTopologyValid(validate, root);

                SetObjectReference(feedbackA, "flashRenderer", flashB);
                AssertTopologyInvalid(validate, root);
                SetObjectReference(feedbackA, "flashRenderer", flashA);

                flashA.gameObject.name = "Wrong Flash Name";
                AssertTopologyInvalid(validate, root);
                flashA.gameObject.name = "Placement Flash";

                ringA.transform.SetParent(root.transform, false);
                AssertTopologyInvalid(validate, root);
                ringA.transform.SetParent(feedbackA.transform, false);

                SetObjectReference(pulse, "border", flashA);
                AssertTopologyInvalid(validate, root);
                SetObjectReference(pulse, "border", pulseBorder);

                var animatorA = feedbackA.GetComponent<Animator>();
                var animatorB = feedbackB.GetComponent<Animator>();
                SetObjectReference(feedbackA, "animator", animatorB);
                AssertTopologyInvalid(validate, root);
                SetObjectReference(feedbackA, "animator", animatorA);
                AssertTopologyValid(validate, root);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void FeedbackPresentationGate_RejectsEnabledSpriteMaterialAndTransformMutations()
        {
            var validate = GetReadinessMethod("ValidateFeedbackPresentationContract");
            var feedbackType = Type.GetType(
                "YC.Presentation.MapPlacementFeedback, Assembly-CSharp",
                true);
            var canonicalLibrary = AssetDatabase.LoadMainAssetAtPath(
                "Assets/YC/Presentation/Sprites/Map/MapVisualSprites.asset") as ScriptableObject;
            var builder = Type.GetType(
                "YC.Editor.MapFeedbackVisualEditorAssetBuilder, Assembly-CSharp-Editor",
                true);
            var assets = builder.GetMethod(
                    "LoadRequiredAssets",
                    BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, null);
            var ringSprite = canonicalLibrary.GetType().GetProperty("PlacementFeedbackRing")
                .GetValue(canonicalLibrary) as Sprite;
            var otherSprite = canonicalLibrary.GetType().GetProperty("Hotspot")
                .GetValue(canonicalLibrary) as Sprite;
            var material = assets.GetType().GetProperty("FeedbackMaterial").GetValue(assets) as Material;
            var controller = assets.GetType().GetProperty("PlacementController").GetValue(assets)
                as RuntimeAnimatorController;
            Assert.That(ringSprite, Is.Not.Null);
            Assert.That(material, Is.Not.Null);
            Assert.That(controller, Is.Not.Null);

            var owner = new GameObject("Feedback Presentation Contract");
            owner.SetActive(false);
            try
            {
                AttachFeedback(owner, feedbackType, out var feedback, out var flash, out var ring);
                var animator = owner.GetComponent<Animator>();
                ((Behaviour)feedback).enabled = true;
                flash.sprite = ringSprite;
                ring.sprite = ringSprite;
                flash.sharedMaterial = material;
                ring.sharedMaterial = material;
                flash.enabled = false;
                ring.enabled = false;
                animator.enabled = false;
                animator.runtimeAnimatorController = controller;
                animator.updateMode = AnimatorUpdateMode.UnscaledTime;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.applyRootMotion = false;
                AssertValidatorAccepts(validate, feedback, canonicalLibrary, assets);

                ((Behaviour)feedback).enabled = false;
                AssertValidatorRejects(validate, feedback, canonicalLibrary, assets);
                ((Behaviour)feedback).enabled = true;
                flash.enabled = true;
                AssertValidatorRejects(validate, feedback, canonicalLibrary, assets);
                flash.enabled = false;
                ring.sprite = otherSprite;
                AssertValidatorRejects(validate, feedback, canonicalLibrary, assets);
                ring.sprite = ringSprite;
                flash.sharedMaterial = null;
                AssertValidatorRejects(validate, feedback, canonicalLibrary, assets);
                flash.sharedMaterial = material;
                ring.transform.localPosition = Vector3.right;
                AssertValidatorRejects(validate, feedback, canonicalLibrary, assets);
                ring.transform.localPosition = Vector3.zero;
                ring.transform.localScale = Vector3.one * 2f;
                AssertValidatorRejects(validate, feedback, canonicalLibrary, assets);
                ring.transform.localScale = Vector3.one;
                AssertValidatorAccepts(validate, feedback, canonicalLibrary, assets);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [Test]
        public void InteractionOwnerGate_RejectsSwappedHotspotAndInfluenceSlotReferences()
        {
            var validate = GetReadinessMethod("ValidateFeedbackTopology");
            var hotspotType = Type.GetType("YC.Presentation.MapHotspot, Assembly-CSharp", true);
            var pulseType = Type.GetType("YC.Presentation.MapHighlightPulse, Assembly-CSharp", true);
            var feedbackType = Type.GetType("YC.Presentation.MapPlacementFeedback, Assembly-CSharp", true);
            var viewType = Type.GetType("YC.Presentation.MapView, Assembly-CSharp", true);
            var clickType = Type.GetType("YC.Presentation.InfluenceSlotClickTarget, Assembly-CSharp", true);
            var root = new GameObject("Interaction Owner Contract");
            root.SetActive(false);
            try
            {
                CreateHotspotOwner(root.transform, hotspotType, pulseType, feedbackType, "A",
                    out var hotspotA, out var hotspotRendererA, out var hotspotPulseA,
                    out var hotspotFeedbackA);
                CreateHotspotOwner(root.transform, hotspotType, pulseType, feedbackType, "B",
                    out _, out var hotspotRendererB, out var hotspotPulseB,
                    out var hotspotFeedbackB);
                AssertTopologyValid(validate, root);

                AssertSwappedReferenceRejected(
                    validate, root, hotspotA, "spriteRenderer", hotspotRendererB, hotspotRendererA);
                AssertSwappedReferenceRejected(
                    validate, root, hotspotA, "highlightPulse", hotspotPulseB, hotspotPulseA);
                AssertSwappedReferenceRejected(
                    validate, root, hotspotA, "placementFeedback", hotspotFeedbackB, hotspotFeedbackA);

                var view = root.AddComponent(viewType) as Component;
                CreateInfluenceSlotOwner(view.transform, pulseType, feedbackType, clickType, "slot-a",
                    out var slotRendererA, out var slotColliderA, out var slotClickA,
                    out var slotBorderA, out var slotPulseA, out var slotFeedbackA);
                CreateInfluenceSlotOwner(view.transform, pulseType, feedbackType, clickType, "slot-b",
                    out var slotRendererB, out var slotColliderB, out var slotClickB,
                    out var slotBorderB, out var slotPulseB, out var slotFeedbackB);
                ConfigureInfluenceSlot(view, 0, "slot-a", slotRendererA, slotColliderA, slotClickA,
                    slotBorderA, slotPulseA, slotFeedbackA);
                ConfigureInfluenceSlot(view, 1, "slot-b", slotRendererB, slotColliderB, slotClickB,
                    slotBorderB, slotPulseB, slotFeedbackB);
                AssertTopologyValid(validate, root);

                AssertSwappedArrayReferenceRejected(
                    validate, root, view, 0, "renderer", slotRendererB, slotRendererA);
                AssertSwappedArrayReferenceRejected(
                    validate, root, view, 0, "borderRenderer", slotBorderB, slotBorderA);
                AssertSwappedArrayReferenceRejected(
                    validate, root, view, 0, "borderPulse", slotPulseB, slotPulseA);
                AssertSwappedArrayReferenceRejected(
                    validate, root, view, 0, "placementFeedback", slotFeedbackB, slotFeedbackA);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void GeneratedAssets_PassExactAssetReadiness()
        {
            InvokeReadiness("ValidateGeneratedAssets");
        }

        [Test]
        public void ProductionPrefabAndSpatialContracts_PassFullReadiness()
        {
            InvokeReadiness("ValidateReadyForBuild");
        }

        private static void AssertAsset<T>(string path, string expectedGuid) where T : UnityEngine.Object
        {
            Assert.That(File.Exists(path), Is.True, path);
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(expectedGuid), path);
            Assert.That(AssetDatabase.LoadAssetAtPath<T>(path), Is.Not.Null, path);
        }

        private static void InvokeReadiness(string methodName)
        {
            var readiness = Type.GetType(
                "YC.Editor.MapFeedbackVisualBuildReadiness, Assembly-CSharp-Editor",
                true);
            var method = readiness.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, methodName);
            Assert.DoesNotThrow(() => method.Invoke(null, null), methodName);
        }

        private static void CreateFeedbackOwner(
            Transform parent,
            Type feedbackType,
            string name,
            out Component feedback,
            out SpriteRenderer flash,
            out SpriteRenderer ring)
        {
            var owner = new GameObject(name);
            owner.transform.SetParent(parent, false);
            AttachFeedback(owner, feedbackType, out feedback, out flash, out ring);
        }

        private static void AttachFeedback(
            GameObject owner,
            Type feedbackType,
            out Component feedback,
            out SpriteRenderer flash,
            out SpriteRenderer ring)
        {
            feedback = owner.AddComponent(feedbackType) as Component;
            var animator = owner.AddComponent<Animator>();

            var flashObject = new GameObject("Placement Flash");
            flashObject.transform.SetParent(owner.transform, false);
            flash = flashObject.AddComponent<SpriteRenderer>();
            var ringObject = new GameObject("Placement Expanding Ring");
            ringObject.transform.SetParent(owner.transform, false);
            ring = ringObject.AddComponent<SpriteRenderer>();

            SetObjectReference(feedback, "flashRenderer", flash);
            SetObjectReference(feedback, "expandingRingRenderer", ring);
            SetObjectReference(feedback, "animator", animator);
        }

        private static void CreateHotspotOwner(
            Transform parent,
            Type hotspotType,
            Type pulseType,
            Type feedbackType,
            string id,
            out Component hotspot,
            out SpriteRenderer renderer,
            out Component pulse,
            out Component feedback)
        {
            var owner = new GameObject("Hotspot " + id);
            owner.transform.SetParent(parent, false);
            renderer = owner.AddComponent<SpriteRenderer>();
            pulse = owner.AddComponent(pulseType) as Component;
            SetObjectReference(pulse, "border", renderer);
            AttachFeedback(owner, feedbackType, out feedback, out _, out _);
            hotspot = owner.AddComponent(hotspotType) as Component;
            SetObjectReference(hotspot, "spriteRenderer", renderer);
            SetObjectReference(hotspot, "highlightPulse", pulse);
            SetObjectReference(hotspot, "placementFeedback", feedback);
        }

        private static void CreateInfluenceSlotOwner(
            Transform parent,
            Type pulseType,
            Type feedbackType,
            Type clickType,
            string id,
            out SpriteRenderer renderer,
            out CircleCollider2D collider,
            out Component click,
            out SpriteRenderer border,
            out Component pulse,
            out Component feedback)
        {
            var owner = new GameObject("InfluenceSlot " + id);
            owner.transform.SetParent(parent, false);
            renderer = owner.AddComponent<SpriteRenderer>();
            collider = owner.AddComponent<CircleCollider2D>();
            click = owner.AddComponent(clickType) as Component;
            AttachFeedback(owner, feedbackType, out feedback, out _, out _);
            var borderOwner = new GameObject("MovableInfluenceBorder");
            borderOwner.transform.SetParent(owner.transform, false);
            border = borderOwner.AddComponent<SpriteRenderer>();
            pulse = borderOwner.AddComponent(pulseType) as Component;
            SetObjectReference(pulse, "border", border);
        }

        private static void ConfigureInfluenceSlot(
            Component view,
            int index,
            string id,
            SpriteRenderer renderer,
            CircleCollider2D collider,
            Component click,
            SpriteRenderer border,
            Component pulse,
            Component feedback)
        {
            var serialized = new SerializedObject(view);
            var slots = serialized.FindProperty("influenceSlots");
            slots.arraySize = Math.Max(slots.arraySize, index + 1);
            var entry = slots.GetArrayElementAtIndex(index);
            entry.FindPropertyRelative("slotId").stringValue = id;
            entry.FindPropertyRelative("renderer").objectReferenceValue = renderer;
            entry.FindPropertyRelative("collider").objectReferenceValue = collider;
            entry.FindPropertyRelative("clickTarget").objectReferenceValue = click;
            entry.FindPropertyRelative("borderRenderer").objectReferenceValue = border;
            entry.FindPropertyRelative("borderPulse").objectReferenceValue = pulse;
            entry.FindPropertyRelative("placementFeedback").objectReferenceValue = feedback;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssertSwappedReferenceRejected(
            MethodInfo validate,
            GameObject root,
            Component target,
            string property,
            UnityEngine.Object swapped,
            UnityEngine.Object original)
        {
            SetObjectReference(target, property, swapped);
            AssertTopologyInvalid(validate, root);
            SetObjectReference(target, property, original);
            AssertTopologyValid(validate, root);
        }

        private static void AssertSwappedArrayReferenceRejected(
            MethodInfo validate,
            GameObject root,
            Component view,
            int index,
            string property,
            UnityEngine.Object swapped,
            UnityEngine.Object original)
        {
            SetInfluenceSlotReference(view, index, property, swapped);
            AssertTopologyInvalid(validate, root);
            SetInfluenceSlotReference(view, index, property, original);
            AssertTopologyValid(validate, root);
        }

        private static void SetInfluenceSlotReference(
            Component view,
            int index,
            string property,
            UnityEngine.Object value)
        {
            var serialized = new SerializedObject(view);
            var slot = serialized.FindProperty("influenceSlots").GetArrayElementAtIndex(index);
            slot.FindPropertyRelative(property).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetObjectReference(
            Component target,
            string propertyName,
            UnityEngine.Object value)
        {
            Assert.That(target, Is.Not.Null);
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(propertyName);
            Assert.That(property, Is.Not.Null, propertyName);
            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssertTopologyValid(MethodInfo validate, GameObject root)
        {
            Assert.DoesNotThrow(() => validate.Invoke(null, new object[] { root }));
        }

        private static void AssertTopologyInvalid(MethodInfo validate, GameObject root)
        {
            var exception = Assert.Throws<TargetInvocationException>(
                () => validate.Invoke(null, new object[] { root }));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        private static MethodInfo GetReadinessMethod(string name)
        {
            var readiness = Type.GetType(
                "YC.Editor.MapFeedbackVisualBuildReadiness, Assembly-CSharp-Editor",
                true);
            var method = readiness.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, name);
            return method;
        }

        private static void AssertStringValidatorAccepts(MethodInfo validate, string value)
        {
            Assert.DoesNotThrow(() => validate.Invoke(null, new object[] { value }));
        }

        private static void AssertStringValidatorRejects(MethodInfo validate, string value)
        {
            var exception = Assert.Throws<TargetInvocationException>(
                () => validate.Invoke(null, new object[] { value }));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        private static void AssertObjectValidatorAccepts(
            MethodInfo validate,
            UnityEngine.Object value)
        {
            Assert.DoesNotThrow(() => validate.Invoke(null, new object[] { value }));
        }

        private static void AssertObjectValidatorRejects(
            MethodInfo validate,
            UnityEngine.Object value)
        {
            var exception = Assert.Throws<TargetInvocationException>(
                () => validate.Invoke(null, new object[] { value }));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        private static void AssertValidatorAccepts(MethodInfo validate, params object[] arguments)
        {
            Assert.DoesNotThrow(() => validate.Invoke(null, arguments));
        }

        private static void AssertValidatorRejects(MethodInfo validate, params object[] arguments)
        {
            var exception = Assert.Throws<TargetInvocationException>(
                () => validate.Invoke(null, arguments));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        private static void AssertValidatorRejects(
            string label,
            MethodInfo validate,
            params object[] arguments)
        {
            var exception = Assert.Throws<TargetInvocationException>(
                () => validate.Invoke(null, arguments),
                label + " 应被 Controller readiness 拒绝");
            Assert.That(
                exception.InnerException,
                Is.TypeOf<InvalidOperationException>(),
                label);
        }

        private static void AssertControllerRawMutationRejected(
            MethodInfo validate,
            object[] valid,
            int argumentIndex,
            object invalidValue,
            string label)
        {
            var mutation = (object[])valid.Clone();
            mutation[argumentIndex] = invalidValue;
            AssertValidatorRejects(label, validate, mutation);
        }

        private static void AssertMaterialMutationRejected(Action<Material> mutate)
        {
            var validate = GetReadinessMethod("ValidateShaderAndMaterial");
            var canonical = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            Assert.That(canonical, Is.Not.Null);
            var temporary = new Material(canonical);
            try
            {
                AssertObjectValidatorAccepts(validate, temporary);
                mutate(temporary);
                AssertObjectValidatorRejects(validate, temporary);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temporary);
            }
        }

        private static string BuildShaderSource(
            string blend = "Blend One One",
            string fragment = "#pragma fragment SpriteFrag",
            string include = "#include \"UnitySprites.cginc\"",
            string tagDecoy = "\"QuotedMarker\" = \"SubShader Pass Blend One One SpriteFrag\"",
            string extraPass = "")
        {
            return
                "Shader \"Test\" {\n" +
                "Properties { _Name (\"Blend One One\", Float) = 0 }\n" +
                "SubShader {\n" +
                "Tags { " + tagDecoy + " }\n" +
                blend + "\n" +
                "Pass {\n" +
                "CGPROGRAM\n" +
                "#pragma vertex SpriteVert\n" +
                fragment + "\n" +
                include + "\n" +
                "ENDCG\n" +
                "}\n" +
                extraPass + "\n" +
                "}\n" +
                "}\n";
        }

        private static string BuildMapViewInstanceYaml(
            string guid,
            long rootGameObjectId,
            long rootTransformId,
            string extraModification)
        {
            return
                "--- !u!1 &70\n" +
                "GameObject:\n" +
                "  m_PrefabInstance: {fileID: 0}\n" +
                "  m_Component:\n" +
                "  - component: {fileID: 77}\n" +
                "  m_Name: MapRoot\n" +
                "--- !u!4 &77\n" +
                "Transform:\n" +
                "  m_GameObject: {fileID: 70}\n" +
                "--- !u!1001 &1\n" +
                "PrefabInstance:\n" +
                "  m_Modification:\n" +
                "    serializedVersion: 3\n" +
                "    m_TransformParent: {fileID: 77}\n" +
                "    m_Modifications:\n" +
                BuildModification(rootGameObjectId, guid, "m_Name", "MapView") +
                BuildModification(rootTransformId, guid, "m_LocalPosition.x") +
                BuildModification(rootTransformId, guid, "m_LocalPosition.y") +
                BuildModification(rootTransformId, guid, "m_LocalPosition.z") +
                BuildModification(rootTransformId, guid, "m_LocalRotation.w", "1") +
                BuildModification(rootTransformId, guid, "m_LocalRotation.x") +
                BuildModification(rootTransformId, guid, "m_LocalRotation.y") +
                BuildModification(rootTransformId, guid, "m_LocalRotation.z") +
                BuildModification(rootTransformId, guid, "m_LocalEulerAnglesHint.x") +
                BuildModification(rootTransformId, guid, "m_LocalEulerAnglesHint.y") +
                BuildModification(rootTransformId, guid, "m_LocalEulerAnglesHint.z") +
                extraModification +
                "    m_RemovedComponents: []\n" +
                "    m_RemovedGameObjects: []\n" +
                "    m_AddedGameObjects: []\n" +
                "    m_AddedComponents: []\n" +
                "  m_SourcePrefab: {fileID: 100100000, guid: " + guid + ", type: 3}\n";
        }

        private static string BuildModification(
            long localId,
            string guid,
            string propertyPath,
            string value = "0")
        {
            return
                "    - target: {fileID: " + localId + ", guid: " + guid + ", type: 3}\n" +
                "      propertyPath: " + propertyPath + "\n" +
                "      value: " + value + "\n" +
                "      objectReference: {fileID: 0}\n";
        }

        private static void AssertSceneYamlAccepts(
            MethodInfo validate,
            string yaml,
            string guid,
            long rootGameObjectId,
            long rootTransformId)
        {
            Assert.DoesNotThrow(() => validate.Invoke(
                null,
                new object[] { "memory.unity", yaml, guid, rootGameObjectId, rootTransformId }));
        }

        private static void AssertSceneYamlRejects(
            MethodInfo validate,
            string yaml,
            string guid,
            long rootGameObjectId,
            long rootTransformId)
        {
            var exception = Assert.Throws<TargetInvocationException>(() => validate.Invoke(
                null,
                new object[] { "memory.unity", yaml, guid, rootGameObjectId, rootTransformId }));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }

    }
}
