using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class EffectDialogLayoutEditorAssetTests
    {
        private const string ManifestPath =
            "Assets/YC/Editor/Data/effect_dialog_layout_manifest.json";
        private const string ManifestGuid = "6a219b35f3dd49a6a04a5b4ceea9bb70";
        private const string ManifestSha256 =
            "84988DCACCEE2575064C8BCAE1A4FD1D5EB533F993E31122397FF1EA9A9AF7BB";
        private const string EffectProfilePath =
            "Assets/YC/Presentation/Content/EffectDialogLayoutProfile.asset";
        private const string EffectProfileGuid = "c4df7a0a512c4f9ca6979764614620dd";
        private const string ExpandableProfilePath =
            "Assets/YC/Presentation/Content/ExpandableInfoPanelLayoutProfile.asset";
        private const string ExpandableProfileGuid = "8f1b3fc2cb09419b939e7d36abe52d4a";
        private const string EffectPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/EffectDialogShell.prefab";
        private const string EffectPrefabGuid = "a247544ff081c484cb987604277e8d90";
        private const string ExpandablePrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/ExpandableInfoPanel.prefab";
        private const string ExpandablePrefabGuid = "b57f7a980c01fa84c93597e4131f169c";

        [Test]
        public void ManifestProfilesPrefabsAndProductionChain_AreLockedAndReady()
        {
            Assert.That(ComputeFileSha256(ManifestPath), Is.EqualTo(ManifestSha256).IgnoreCase);
            AssertControlledAsset(ManifestPath, ManifestGuid);
            AssertControlledAsset(EffectProfilePath, EffectProfileGuid);
            AssertControlledAsset(ExpandableProfilePath, ExpandableProfileGuid);
            AssertControlledAsset(EffectPrefabPath, EffectPrefabGuid);
            AssertControlledAsset(ExpandablePrefabPath, ExpandablePrefabGuid);
            Assert.That(AssetDatabase.FindAssets("t:EffectDialogLayoutProfile"),
                Is.EqualTo(new[] { EffectProfileGuid }));
            Assert.That(AssetDatabase.FindAssets("t:ExpandableInfoPanelLayoutProfile"),
                Is.EqualTo(new[] { ExpandableProfileGuid }));

            var effectProfile = LoadRuntimeAsset(
                EffectProfilePath,
                "YC.Presentation.EffectDialogLayoutProfile");
            var expandableProfile = LoadRuntimeAsset(
                ExpandableProfilePath,
                "YC.Presentation.ExpandableInfoPanelLayoutProfile");
            AssertProfileValid(effectProfile);
            AssertProfileValid(expandableProfile);
            Assert.That(
                new SerializedObject(effectProfile)
                    .FindProperty("sourceManifestSha256").stringValue,
                Is.EqualTo(ManifestSha256).IgnoreCase);
            Assert.That(
                new SerializedObject(expandableProfile)
                    .FindProperty("sourceManifestSha256").stringValue,
                Is.EqualTo(ManifestSha256).IgnoreCase);
            Assert.DoesNotThrow(InvokeReadiness);
        }

        [Test]
        public void ConsumerSourceGate_UsesExactConstructorCountsAndLockedNormalizedHashes()
        {
            Assert.DoesNotThrow(() => InvokeEditorStatic(
                "YC.Editor.EffectDialogLayoutBuildReadiness",
                "ValidateSourceSemantics"));

            var targetCounts = new Dictionary<string, int>
            {
                { "Assets/YC/Presentation/EffectDialogPrimitives.cs", 9 },
                { "Assets/YC/Presentation/FacilityEffectChoiceDialog.cs", 5 },
                { "Assets/YC/Presentation/ExpandableInfoPanel.cs", 7 },
                { "Assets/YC/Presentation/EffectDialogShellView.cs", 3 },
                { "Assets/YC/Presentation/CharacterCardEffectChoiceDialog.cs", 0 },
                { "Assets/YC/Presentation/SpecialActionChoiceDialog.cs", 0 }
            };
            foreach (var pair in targetCounts)
            {
                var source = File.ReadAllText(Path.GetFullPath(pair.Key));
                Assert.That(
                    Regex.Matches(source, @"\bnew\s+Vector2\s*\(").Count,
                    Is.EqualTo(pair.Value),
                    pair.Key);
            }

            var recursiveCount = 0;
            var presentationRoot = Path.GetFullPath("Assets/YC/Presentation");
            foreach (var path in Directory.GetFiles(
                         presentationRoot,
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                recursiveCount += Regex.Matches(
                    File.ReadAllText(path),
                    @"\bnew\s+Vector2\s*\(").Count;
            }

            Assert.That(recursiveCount, Is.EqualTo(81));
        }

        [Test]
        public void CharacterConsumer_ProfileLayoutKeepsDynamicHeightsAndOneShotCallbacks()
        {
            var registry = LoadProductionRegistry();
            var canvasObject = new GameObject(
                "Character Effect Layout Test Canvas",
                typeof(RectTransform),
                typeof(Canvas));
            var canvas = canvasObject.GetComponent<RectTransform>();
            canvas.sizeDelta = new Vector2(1920f, 1080f);
            object dialog = null;
            try
            {
                var dialogType = GetRuntimeType(
                    "YC.Presentation.CharacterCardEffectChoiceDialog");
                dialog = Activator.CreateInstance(
                    dialogType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new object[] { registry, canvas },
                    null);
                var showOptions = dialogType.GetMethod(
                    "ShowOptions",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.That(showOptions, Is.Not.Null);

                AssertCharacterOptionsPanel(showOptions, dialog, canvasObject, null, 330f);
                AssertCharacterOptionsPanel(
                    showOptions,
                    dialog,
                    canvasObject,
                    CreateEffectOptions(0),
                    330f);
                AssertCharacterOptionsPanel(
                    showOptions,
                    dialog,
                    canvasObject,
                    CreateEffectOptions(1),
                    330f);
                AssertCharacterOptionsPanel(
                    showOptions,
                    dialog,
                    canvasObject,
                    CreateEffectOptions(7),
                    610f);

                var cancelCount = 0;
                showOptions.Invoke(
                    dialog,
                    new object[]
                    {
                        "Title",
                        "Description",
                        CreateEffectOptions(1),
                        new Action(() => cancelCount++)
                    });
                var overlay = FindChild(canvasObject, "Character Card Effect Overlay");
                var cancelRect = FindChild(overlay, "Cancel Character Effect")
                    .GetComponent<RectTransform>();
                Assert.That(cancelRect.anchorMin, Is.EqualTo(new Vector2(0.5f, 0f)));
                Assert.That(cancelRect.anchorMax, Is.EqualTo(new Vector2(0.5f, 0f)));
                Assert.That(cancelRect.sizeDelta, Is.EqualTo(new Vector2(180f, 44f)));
                Assert.That(cancelRect.anchoredPosition, Is.EqualTo(new Vector2(0f, 28f)));
                var cancelClick = cancelRect.GetComponent<Button>().onClick;
                cancelClick.Invoke();
                cancelClick.Invoke();
                Assert.That(cancelCount, Is.EqualTo(1));

                var resourceCancelCount = 0;
                var resourceCancelObservedHidden = false;
                var showResourceSale = dialogType.GetMethod(
                    "ShowResourceSale",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.That(showResourceSale, Is.Not.Null);
                showResourceSale.Invoke(
                    dialog,
                    new object[]
                    {
                        new[] { "Resource" },
                        new[] { 1 },
                        new[] { 2 },
                        new Action<IReadOnlyList<int>>(_ => { }),
                        new Action(() =>
                        {
                            resourceCancelCount++;
                            resourceCancelObservedHidden =
                                FindOptionalChild(canvasObject, "Character Card Effect Overlay") == null;
                        })
                    });
                overlay = FindChild(canvasObject, "Character Card Effect Overlay");
                var resourcePanel = FindChild(overlay, "Character Card Effect Panel")
                    .GetComponent<RectTransform>();
                Assert.That(resourcePanel.sizeDelta, Is.EqualTo(new Vector2(690f, 570f)));
                Assert.That(
                    FindChild(overlay, "Character Sale Label 0")
                        .GetComponent<RectTransform>().anchoredPosition.y,
                    Is.EqualTo(-166f).Within(0.001f));
                var resourceCancelClick = FindChild(overlay, "Cancel Character Effect")
                    .GetComponent<Button>().onClick;
                resourceCancelClick.Invoke();
                resourceCancelClick.Invoke();
                Assert.That(resourceCancelCount, Is.EqualTo(1));
                Assert.That(resourceCancelObservedHidden, Is.True);
            }
            finally
            {
                InvokeHideIfPresent(dialog);
                Object.DestroyImmediate(canvasObject);
            }
        }

        [Test]
        public void SpecialActionConsumer_ProfileLayoutKeepsPaymentAndCollapsedPromptSemantics()
        {
            var registry = LoadProductionRegistry();
            var canvasObject = new GameObject(
                "Special Action Layout Test Canvas",
                typeof(RectTransform),
                typeof(Canvas));
            var canvas = canvasObject.GetComponent<RectTransform>();
            canvas.sizeDelta = new Vector2(1920f, 1080f);
            object dialog = null;
            try
            {
                var dialogType = GetRuntimeType("YC.Presentation.SpecialActionChoiceDialog");
                dialog = Activator.CreateInstance(
                    dialogType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new object[] { registry, new Func<RectTransform>(() => canvas) },
                    null);
                var paymentMethod = Array.Find(
                    dialogType.GetMethods(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                    method => method.Name == "ShowCompositePayment" &&
                              method.GetParameters().Length == 4);
                Assert.That(paymentMethod, Is.Not.Null);
                var confirmCount = 0;
                var confirmObservedHidden = false;
                paymentMethod.Invoke(
                    dialog,
                    new object[]
                    {
                        2,
                        3,
                        new Action<IReadOnlyList<int>>(_ =>
                        {
                            confirmCount++;
                            confirmObservedHidden =
                                FindOptionalChild(canvasObject, "Special Action Choice Overlay") == null;
                        }),
                        new Action(() => { })
                    });
                var overlay = FindChild(canvasObject, "Special Action Choice Overlay");
                var panel = FindChild(overlay, "Special Action Choice Panel")
                    .GetComponent<RectTransform>();
                Assert.That(panel.sizeDelta, Is.EqualTo(new Vector2(650f, 500f)));
                Assert.That(panel.anchoredPosition, Is.EqualTo(Vector2.zero));
                Assert.That(overlay.GetComponent<Image>().raycastTarget, Is.True);
                var confirmClick = FindChild(overlay, "Confirm Special Action Payment")
                    .GetComponent<Button>().onClick;
                confirmClick.Invoke();
                confirmClick.Invoke();
                Assert.That(confirmCount, Is.EqualTo(1));
                Assert.That(confirmObservedHidden, Is.True);

                var promptMethod = dialogType.GetMethod(
                    "ShowCollapsibleMapPrompt",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.That(promptMethod, Is.Not.Null);
                promptMethod.Invoke(dialog, new object[] { "Title", "Description", "Summary" });
                overlay = FindChild(canvasObject, "Special Action Choice Overlay");
                panel = FindChild(overlay, "Special Action Choice Panel")
                    .GetComponent<RectTransform>();
                Assert.That(panel.sizeDelta, Is.EqualTo(new Vector2(650f, 58f)));
                Assert.That(
                    FindChild(overlay, "Special Action Expanded Content").activeSelf,
                    Is.False);
                FindChild(overlay, "Special Action Collapse Toggle")
                    .GetComponent<Button>().onClick.Invoke();
                Assert.That(panel.sizeDelta, Is.EqualTo(new Vector2(650f, 260f)));
                Assert.That(panel.anchoredPosition, Is.EqualTo(new Vector2(0f, 310f)));
            }
            finally
            {
                InvokeHideIfPresent(dialog);
                Object.DestroyImmediate(canvasObject);
            }
        }

        [Test]
        public void RemainingEffectConsumers_MissingNaNOrInfinityProfile_FailFastAtConstruction()
        {
            var consumerTypes = new[]
            {
                "YC.Presentation.CharacterCardEffectChoiceDialog",
                "YC.Presentation.SpecialActionChoiceDialog"
            };
            for (var i = 0; i < consumerTypes.Length; i++)
            {
                AssertConsumerConstructionRejects(consumerTypes[i], null);
            }

            var validProfile = LoadRuntimeAsset(
                EffectProfilePath,
                "YC.Presentation.EffectDialogLayoutProfile");
            var invalidValues = new[] { float.NaN, float.PositiveInfinity };
            for (var valueIndex = 0; valueIndex < invalidValues.Length; valueIndex++)
            {
                var invalidProfile = Object.Instantiate(validProfile);
                try
                {
                    var values = invalidProfile.GetType().GetField(
                            "values",
                            BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.GetValue(invalidProfile);
                    Assert.That(values, Is.Not.Null);
                    var rowHeightField = values.GetType().GetField("CharacterOptionsRowHeight");
                    Assert.That(rowHeightField, Is.Not.Null);
                    rowHeightField.SetValue(values, invalidValues[valueIndex]);
                    for (var consumerIndex = 0; consumerIndex < consumerTypes.Length; consumerIndex++)
                    {
                        AssertConsumerConstructionRejects(
                            consumerTypes[consumerIndex],
                            invalidProfile);
                    }
                }
                finally
                {
                    Object.DestroyImmediate(invalidProfile);
                }
            }
        }

        [Test]
        public void TargetedRebuild_PreservesEventDispatchHudAndSharedVisualFingerprints()
        {
            var unrelated = new[]
            {
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/EventChoiceDialog.prefab",
                "Assets/YC/Presentation/Content/EventChoiceDialogLayoutProfile.asset",
                "Assets/YC/Editor/Data/event_choice_dialog_layout_manifest.json",
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/DispatchDecisionDialog.prefab",
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab",
                "Assets/YC/Presentation/Sprites/SharedUiVisuals.asset",
                "Assets/YC/Presentation/Content/UiThemeCatalog.asset",
                "Assets/YC/Presentation/Content/EventCharacterCardCatalog.asset",
                "Assets/YC/Presentation/Content/CityStyleSpecialActionCatalog.asset",
                "Assets/Scenes/SampleScene.unity"
            };
            var hashes = new string[unrelated.Length];
            var guids = new string[unrelated.Length];
            for (var i = 0; i < unrelated.Length; i++)
            {
                hashes[i] = ComputeFileSha256(unrelated[i]);
                guids[i] = AssetDatabase.AssetPathToGUID(unrelated[i]);
                Assert.That(guids[i], Is.Not.Empty, unrelated[i]);
            }

            Assert.DoesNotThrow(() => InvokeEditorStatic(
                "YC.Editor.EffectDialogLayoutEditorAssetBuilder",
                "RebuildProfilesAndPrefabsMenu"));

            for (var i = 0; i < unrelated.Length; i++)
            {
                Assert.That(ComputeFileSha256(unrelated[i]), Is.EqualTo(hashes[i]), unrelated[i]);
                Assert.That(
                    AssetDatabase.AssetPathToGUID(unrelated[i]),
                    Is.EqualTo(guids[i]).IgnoreCase,
                    unrelated[i]);
            }
        }

        [Test]
        public void PrefabMutationGates_RejectWrongRectDisabledControllerAndDuplicateActionSlot()
        {
            var effectProfile = LoadRuntimeAsset(
                EffectProfilePath,
                "YC.Presentation.EffectDialogLayoutProfile");
            var effectRoot = PrefabUtility.LoadPrefabContents(EffectPrefabPath);
            try
            {
                FindChild(effectRoot, "Collapse Toggle")
                    .GetComponent<RectTransform>().sizeDelta = new Vector2(1f, 1f);
                AssertEditorGateRejects(
                    "ValidateEffectPrefabContents",
                    effectRoot,
                    effectProfile);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(effectRoot);
            }

            effectRoot = PrefabUtility.LoadPrefabContents(EffectPrefabPath);
            try
            {
                var view = effectRoot.GetComponent(
                    GetRuntimeType("YC.Presentation.EffectDialogShellView"));
                var serialized = new SerializedObject(view);
                var actions = serialized.FindProperty("actionButtons");
                actions.GetArrayElementAtIndex(1).objectReferenceValue =
                    actions.GetArrayElementAtIndex(0).objectReferenceValue;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssertEditorGateRejects(
                    "ValidateEffectPrefabContents",
                    effectRoot,
                    effectProfile);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(effectRoot);
            }

            var expandableProfile = LoadRuntimeAsset(
                ExpandableProfilePath,
                "YC.Presentation.ExpandableInfoPanelLayoutProfile");
            var expandableRoot = PrefabUtility.LoadPrefabContents(ExpandablePrefabPath);
            try
            {
                var controller = expandableRoot.GetComponent(
                    GetRuntimeType("YC.Presentation.ExpandableInfoPanel")) as Behaviour;
                Assert.That(controller, Is.Not.Null);
                controller.enabled = false;
                AssertEditorGateRejects(
                    "ValidateExpandablePrefabContents",
                    expandableRoot,
                    expandableProfile);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(expandableRoot);
            }
        }

        [Test]
        public void ControlledMetaGuidGate_RejectsMissingDuplicateWrongAndDatabaseMismatch()
        {
            var validator = GetEditorType("YC.Editor.EffectDialogLayoutControlledMetaGuid");
            var method = validator.GetMethod(
                "ValidateMetaGuidText",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            const string expected = "11111111111111111111111111111111";
            const string wrong = "22222222222222222222222222222222";
            Assert.DoesNotThrow(() => method.Invoke(
                null,
                new object[]
                {
                    "fileFormatVersion: 2\nguid: " + expected + "\nNativeFormatImporter:\n",
                    expected,
                    expected
                }));
            AssertInvocationRejects(method, "fileFormatVersion: 2\n", expected, expected);
            AssertInvocationRejects(
                method,
                "guid: " + expected + "\nguid: " + expected + "\n",
                expected,
                expected);
            AssertInvocationRejects(method, "guid: " + wrong + "\n", expected, wrong);
            AssertInvocationRejects(method, "guid: " + expected + "\n", expected, wrong);
        }

        [Test]
        public void EventChoiceDialog_WithoutSerializedEffectProfile_FailsFastAtConstruction()
        {
            var registryObject = new GameObject("Invalid Registry");
            var effectObject = new GameObject("Invalid Effect View");
            var canvasObject = new GameObject("Canvas", typeof(RectTransform));
            try
            {
                var registry = registryObject.AddComponent(
                    GetRuntimeType("YC.Presentation.GameplayDialogRegistry"));
                var invalidEffect = effectObject.AddComponent(
                    GetRuntimeType("YC.Presentation.EffectDialogShellView"));
                var serialized = new SerializedObject(registry);
                serialized.FindProperty("effectDialogShellPrefab").objectReferenceValue = invalidEffect;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                var exception = Assert.Throws<TargetInvocationException>(() =>
                    Activator.CreateInstance(
                        GetRuntimeType("YC.Presentation.EventChoiceDialog"),
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new object[]
                        {
                            registry,
                            new Func<RectTransform>(() =>
                                canvasObject.GetComponent<RectTransform>())
                        },
                        null));
                Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(
                    exception.InnerException.Message,
                    Does.Contain("Effect 布局 Profile"));
            }
            finally
            {
                Object.DestroyImmediate(canvasObject);
                Object.DestroyImmediate(effectObject);
                Object.DestroyImmediate(registryObject);
            }
        }

        private static void AssertCharacterOptionsPanel(
            MethodInfo showOptions,
            object dialog,
            GameObject canvas,
            object options,
            float expectedHeight)
        {
            showOptions.Invoke(
                dialog,
                new[] { "Title", "Description", options, null });
            var overlay = FindChild(canvas, "Character Card Effect Overlay");
            var panel = FindChild(overlay, "Character Card Effect Panel")
                .GetComponent<RectTransform>();
            Assert.That(panel.sizeDelta.x, Is.EqualTo(680f).Within(0.001f));
            Assert.That(panel.sizeDelta.y, Is.EqualTo(expectedHeight).Within(0.001f));
        }

        private static object CreateEffectOptions(int count)
        {
            var optionType = GetRuntimeType("YC.Presentation.EffectDialogOption");
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(optionType));
            var constructor = optionType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(Action), typeof(bool) },
                null);
            Assert.That(constructor, Is.Not.Null);
            for (var i = 0; i < count; i++)
            {
                list.Add(constructor.Invoke(
                    new object[] { "Option " + i, new Action(() => { }), true }));
            }

            return list;
        }

        private static Component LoadProductionRegistry()
        {
            var hud = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            Assert.That(hud, Is.Not.Null);
            var registry = hud.GetComponentInChildren(
                GetRuntimeType("YC.Presentation.GameplayDialogRegistry"),
                true);
            Assert.That(registry, Is.Not.Null);
            return registry;
        }

        private static void AssertConsumerConstructionRejects(
            string consumerTypeName,
            Object profile)
        {
            var registryObject = new GameObject("Invalid Consumer Registry");
            var effectObject = new GameObject("Invalid Consumer Effect View");
            var canvasObject = new GameObject("Invalid Consumer Canvas", typeof(RectTransform));
            try
            {
                var registry = registryObject.AddComponent(
                    GetRuntimeType("YC.Presentation.GameplayDialogRegistry"));
                var effectView = effectObject.AddComponent(
                    GetRuntimeType("YC.Presentation.EffectDialogShellView"));
                if (profile != null)
                {
                    var effectSerialized = new SerializedObject(effectView);
                    effectSerialized.FindProperty("layoutProfile").objectReferenceValue = profile;
                    effectSerialized.ApplyModifiedPropertiesWithoutUndo();
                }

                var registrySerialized = new SerializedObject(registry);
                registrySerialized.FindProperty("effectDialogShellPrefab").objectReferenceValue =
                    effectView;
                registrySerialized.ApplyModifiedPropertiesWithoutUndo();
                var canvas = canvasObject.GetComponent<RectTransform>();
                object canvasArgument = consumerTypeName.EndsWith(
                    "SpecialActionChoiceDialog",
                    StringComparison.Ordinal)
                    ? (object)new Func<RectTransform>(() => canvas)
                    : canvas;
                var exception = Assert.Throws<TargetInvocationException>(() =>
                    Activator.CreateInstance(
                        GetRuntimeType(consumerTypeName),
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new[] { (object)registry, canvasArgument },
                        null));
                Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(exception.InnerException.Message, Does.Contain("Profile"));
            }
            finally
            {
                Object.DestroyImmediate(canvasObject);
                Object.DestroyImmediate(effectObject);
                Object.DestroyImmediate(registryObject);
            }
        }

        private static void InvokeHideIfPresent(object dialog)
        {
            if (dialog == null)
            {
                return;
            }

            var hide = dialog.GetType().GetMethod(
                "Hide",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            hide?.Invoke(dialog, null);
        }

        private static GameObject FindOptionalChild(GameObject root, string objectName)
        {
            if (root == null)
            {
                return null;
            }

            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i].name == objectName)
                {
                    return transforms[i].gameObject;
                }
            }

            return null;
        }

        private static void InvokeReadiness()
        {
            InvokeEditorStatic(
                "YC.Editor.EffectDialogLayoutBuildReadiness",
                "ValidateReadyForBuild");
        }

        private static void InvokeEditorStatic(string typeName, string methodName)
        {
            var method = GetEditorType(typeName).GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, typeName + "." + methodName);
            try
            {
                method.Invoke(null, null);
            }
            catch (TargetInvocationException exception)
            {
                throw exception.InnerException ?? exception;
            }
        }

        private static void AssertEditorGateRejects(
            string methodName,
            GameObject root,
            Object profile)
        {
            var method = GetEditorType("YC.Editor.EffectDialogLayoutBuildReadiness").GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, methodName);
            var exception = Assert.Throws<TargetInvocationException>(() =>
                method.Invoke(null, new object[] { root, profile }));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        private static void AssertInvocationRejects(
            MethodInfo method,
            string meta,
            string expected,
            string actual)
        {
            var exception = Assert.Throws<TargetInvocationException>(() =>
                method.Invoke(null, new object[] { meta, expected, actual }));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        private static void AssertControlledAsset(string path, string expectedGuid)
        {
            Assert.That(File.Exists(Path.GetFullPath(path)), Is.True, path);
            Assert.That(File.Exists(Path.GetFullPath(path + ".meta")), Is.True, path + ".meta");
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(expectedGuid).IgnoreCase);
            var meta = File.ReadAllText(Path.GetFullPath(path + ".meta"));
            var matches = Regex.Matches(
                meta,
                @"(?m)^guid:[ \t]*(?<guid>[0-9a-fA-F]{32})[ \t]*\r?$");
            Assert.That(matches.Count, Is.EqualTo(1), path);
            Assert.That(matches[0].Groups["guid"].Value, Is.EqualTo(expectedGuid).IgnoreCase);
        }

        private static void AssertProfileValid(Object profile)
        {
            Assert.That(profile, Is.Not.Null);
            var method = profile.GetType().GetMethod("TryValidateConfiguration");
            Assert.That(method, Is.Not.Null);
            var arguments = new object[] { string.Empty };
            Assert.That(method.Invoke(profile, arguments), Is.EqualTo(true), arguments[0] as string);
        }

        private static Object LoadRuntimeAsset(string path, string typeName)
        {
            var asset = AssetDatabase.LoadAssetAtPath(path, GetRuntimeType(typeName));
            Assert.That(asset, Is.Not.Null, path);
            return asset;
        }

        private static GameObject FindChild(GameObject root, string objectName)
        {
            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i].name == objectName) return transforms[i].gameObject;
            }

            Assert.Fail("未找到子对象：" + objectName);
            return null;
        }

        private static string ComputeFileSha256(string path)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(
                        sha.ComputeHash(File.ReadAllBytes(Path.GetFullPath(path))))
                    .Replace("-", string.Empty);
            }
        }

        private static Type GetRuntimeType(string typeName)
        {
            var type = Type.GetType(typeName + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, typeName);
            return type;
        }

        private static Type GetEditorType(string typeName)
        {
            var type = Type.GetType(typeName + ", Assembly-CSharp-Editor", false);
            Assert.That(type, Is.Not.Null, typeName);
            return type;
        }
    }
}
