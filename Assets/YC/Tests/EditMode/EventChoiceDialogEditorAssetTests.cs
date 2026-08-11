using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YC.Domain.Cards;
using YC.Domain.Facilities;
using YC.Domain.Maps;
using YC.Domain.State;
using YC.Presentation;
using YC.Presentation.Workflows;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class EventChoiceDialogEditorAssetTests
    {
        private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";
        private const string LayoutProfilePath =
            "Assets/YC/Presentation/Content/EventChoiceDialogLayoutProfile.asset";
        private const string LayoutProfileGuid = "f6b125d7c0934eb2a8405c6e7d19f438";
        private const string LayoutManifestPath =
            "Assets/YC/Editor/Data/event_choice_dialog_layout_manifest.json";
        private const string LayoutManifestGuid = "c2a9bc63da0f4d5ba412ef8b93d671e4";
        private const string LayoutManifestSha256 =
            "0594CCEF465B749522FB09747D601645D65B69958495DA7FAF0DB02D2FB64760";
        private const string EventChoiceDialogPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/EventChoiceDialog.prefab";
        private const string EventChoiceDialogPrefabGuid = "3fe2d5d7571d9e4488d62bd519017c21";
        private const string SharedUiVisualsPath =
            "Assets/YC/Presentation/Sprites/SharedUiVisuals.asset";
        private const string SharedUiVisualsGuid = "accc4ac362a0a4b4b824d05cbff434f8";
        private const long TriangleUpSpriteLocalId = 8710250400556581170L;
        private const long TriangleDownSpriteLocalId = 9192213887596980247L;

        private Type dialogType;
        private Type viewType;
        private object dialog;
        private RectTransform canvas;
        private Object layoutProfile;
        private SerializedObject serializedLayoutProfile;

        [SetUp]
        public void SetUp()
        {
            var scene = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
            Assert.That(scene.GetRootGameObjects(), Has.Length.EqualTo(6));
            var hudType = GetRuntimeType("YC.Presentation.GameplayInteractionHudView");
            Component hud = null;
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length && hud == null; i++)
            {
                hud = roots[i].GetComponentInChildren(hudType, true);
            }

            Assert.That(hud, Is.Not.Null);
            var registry = GetObjectReference<Component>(hud, "dialogRegistry");
            Assert.That(AssetDatabase.GetAssetPath(
                GetObjectReference<Component>(registry, "eventChoiceDialogPrefab")),
                Is.EqualTo("Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/EventChoiceDialog.prefab"));
            canvas = GetObjectReference<Canvas>(hud, "canvas").GetComponent<RectTransform>();
            dialogType = GetRuntimeType("YC.Presentation.EventChoiceDialog");
            viewType = GetRuntimeType("YC.Presentation.EventChoiceDialogView");
            var layoutProfileType = GetRuntimeType("YC.Presentation.EventChoiceDialogLayoutProfile");
            layoutProfile = AssetDatabase.LoadAssetAtPath(LayoutProfilePath, layoutProfileType);
            Assert.That(layoutProfile, Is.Not.Null);
            serializedLayoutProfile = new SerializedObject(layoutProfile);
            dialog = Activator.CreateInstance(
                dialogType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new object[] { registry, new Func<RectTransform>(() => canvas) },
                null);
            Assert.That(dialog, Is.Not.Null);
        }

        [TearDown]
        public void TearDown()
        {
            if (dialog != null)
            {
                Invoke("Hide");
            }
        }

        [Test]
        public void LayoutProfileAndPrefab_AreUniqueLockedAndReadyForBuild()
        {
            Assert.That(
                AssetDatabase.AssetPathToGUID(LayoutProfilePath),
                Is.EqualTo(LayoutProfileGuid).IgnoreCase);
            Assert.That(AssetDatabase.FindAssets("t:EventChoiceDialogLayoutProfile"),
                Is.EqualTo(new[] { LayoutProfileGuid }));
            Assert.That(AssetDatabase.LoadMainAssetAtPath(LayoutProfilePath), Is.SameAs(layoutProfile));
            Assert.That(
                RequireLayoutProperty("sourceManifestSha256").stringValue,
                Is.EqualTo(LayoutManifestSha256).IgnoreCase);

            var validateProfile = layoutProfile.GetType().GetMethod("TryValidateConfiguration");
            var validationArguments = new object[] { null };
            Assert.That(validateProfile, Is.Not.Null);
            Assert.That(validateProfile.Invoke(layoutProfile, validationArguments), Is.EqualTo(true));
            Assert.That(validationArguments[0], Is.EqualTo(string.Empty));

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(EventChoiceDialogPrefabPath);
            Assert.That(prefab, Is.Not.Null);
            var prefabView = prefab.GetComponent(viewType);
            Assert.That(prefabView, Is.Not.Null);
            Assert.That(
                new SerializedObject(prefabView).FindProperty("layoutProfile").objectReferenceValue,
                Is.SameAs(layoutProfile));

            var profileDependencyCount = 0;
            var sharedVisualDependencyCount = 0;
            var dependencies = AssetDatabase.GetDependencies(EventChoiceDialogPrefabPath, true);
            for (var i = 0; i < dependencies.Length; i++)
            {
                if (dependencies[i] == LayoutProfilePath)
                {
                    profileDependencyCount++;
                }

                if (dependencies[i] == SharedUiVisualsPath)
                {
                    sharedVisualDependencyCount++;
                }
            }

            Assert.That(profileDependencyCount, Is.EqualTo(1));
            Assert.That(sharedVisualDependencyCount, Is.EqualTo(1));
            AssertControlledMetaGuid(SharedUiVisualsPath, SharedUiVisualsGuid);
            var sharedVisuals = AssetDatabase.LoadAssetAtPath(
                SharedUiVisualsPath,
                GetRuntimeType("YC.Presentation.UiVisualAssetLibrary"));
            Assert.That(sharedVisuals, Is.Not.Null);
            var triangleUp = (Sprite)sharedVisuals.GetType().GetProperty("TriangleUp")
                .GetValue(sharedVisuals, null);
            var triangleDown = (Sprite)sharedVisuals.GetType().GetProperty("TriangleDown")
                .GetValue(sharedVisuals, null);
            AssertAssetIdentity(
                triangleUp,
                SharedUiVisualsPath,
                SharedUiVisualsGuid,
                TriangleUpSpriteLocalId);
            AssertAssetIdentity(
                triangleDown,
                SharedUiVisualsPath,
                SharedUiVisualsGuid,
                TriangleDownSpriteLocalId);
            var collapsible = FindChild(prefab, "Event Choice Panel")
                .GetComponent(GetRuntimeType("YC.Presentation.EffectDialogCollapsiblePanel"));
            var collapsibleData = new SerializedObject(collapsible);
            Assert.That(
                collapsibleData.FindProperty("triangleUpSprite").objectReferenceValue,
                Is.SameAs(triangleUp));
            Assert.That(
                collapsibleData.FindProperty("triangleDownSprite").objectReferenceValue,
                Is.SameAs(triangleDown));
            var collapseIcon = FindChild(prefab, "Collapse Triangle").GetComponent<Image>();
            Assert.That(collapseIcon.sprite, Is.SameAs(triangleUp));
            Assert.That(collapseIcon.preserveAspect, Is.True);
            Assert.DoesNotThrow(InvokeLayoutReadiness);
        }

        [Test]
        public void ControlledColdStart_RequiresPreservedFixedMetaGuidsBeforeAnyCreateOrSave()
        {
            AssertControlledMetaGuid(LayoutManifestPath, LayoutManifestGuid);
            AssertControlledMetaGuid(LayoutProfilePath, LayoutProfileGuid);
            AssertControlledMetaGuid(EventChoiceDialogPrefabPath, EventChoiceDialogPrefabGuid);

            AssertMissingMetaGateRejects(
                "YC.Editor.EventChoiceDialogLayoutEditorAssetBuilder",
                "Assets/YC/Presentation/Content/__missing_event_layout_profile.asset");
            AssertMissingMetaGateRejects(
                "YC.EditorTools.GameplayDialogEditorAssetBuilder",
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/__missing_event_dialog.prefab");
        }

        [Test]
        public void ControlledMetaGuidGate_RequiresOneExactTopLevelLineAndActualAssetGuid()
        {
            const string expectedGuid = "11111111111111111111111111111111";
            const string wrongGuid = "22222222222222222222222222222222";
            var validator = GetEditorType("YC.Editor.EventChoiceDialogControlledMetaGuid")
                .GetMethod(
                    "ValidateMetaGuidText",
                    BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(validator, Is.Not.Null);

            var validMeta = "fileFormatVersion: 2\nguid: " + expectedGuid + "\nNativeFormatImporter:\n";
            Assert.DoesNotThrow(() => validator.Invoke(
                null,
                new object[] { validMeta, expectedGuid, expectedGuid }));
            AssertMetaGuidTextGateRejects(
                validator,
                "fileFormatVersion: 2\n# guid: " + expectedGuid + "\nNativeFormatImporter:\n",
                expectedGuid,
                expectedGuid);
            AssertMetaGuidTextGateRejects(
                validator,
                "guid: " + expectedGuid + "\nguid: " + expectedGuid + "\n",
                expectedGuid,
                expectedGuid);
            AssertMetaGuidTextGateRejects(
                validator,
                validMeta,
                expectedGuid,
                wrongGuid);
        }

        [Test]
        public void TargetedRebuild_PreservesUnrelatedDialogAndHudFingerprintsAndGuids()
        {
            var unrelatedPaths = new[]
            {
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/EffectDialogShell.prefab",
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/DispatchDecisionDialog.prefab",
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/CityStyleDeclarationPreviewDialog.prefab",
                "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab",
                SharedUiVisualsPath
            };
            var hashes = new string[unrelatedPaths.Length];
            var guids = new string[unrelatedPaths.Length];
            for (var i = 0; i < unrelatedPaths.Length; i++)
            {
                hashes[i] = ComputeFileSha256(unrelatedPaths[i]);
                guids[i] = AssetDatabase.AssetPathToGUID(unrelatedPaths[i]);
                Assert.That(guids[i], Is.Not.Empty, unrelatedPaths[i]);
            }

            InvokeEditorStatic(
                "YC.Editor.EventChoiceDialogLayoutEditorAssetBuilder",
                "RebuildProfile");
            InvokeEditorStatic(
                "YC.EditorTools.GameplayDialogEditorAssetBuilder",
                "RebuildEventChoiceDialogOnly");

            for (var i = 0; i < unrelatedPaths.Length; i++)
            {
                Assert.That(ComputeFileSha256(unrelatedPaths[i]), Is.EqualTo(hashes[i]), unrelatedPaths[i]);
                Assert.That(AssetDatabase.AssetPathToGUID(unrelatedPaths[i]),
                    Is.EqualTo(guids[i]).IgnoreCase,
                    unrelatedPaths[i]);
            }

            Assert.That(AssetDatabase.AssetPathToGUID(LayoutProfilePath),
                Is.EqualTo(LayoutProfileGuid).IgnoreCase);
            Assert.That(AssetDatabase.AssetPathToGUID(EventChoiceDialogPrefabPath),
                Is.EqualTo(EventChoiceDialogPrefabGuid).IgnoreCase);
            Assert.DoesNotThrow(InvokeLayoutReadiness);
        }

        [Test]
        public void LayoutReadiness_RejectsIdentityTopologyAndDeepPresentationMutations()
        {
            AssertPrefabGateRejects(root =>
            {
                var serializedView = new SerializedObject(root.GetComponent(viewType));
                serializedView.FindProperty("layoutProfile").objectReferenceValue = null;
                serializedView.ApplyModifiedPropertiesWithoutUndo();
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "Title").GetComponent<RectTransform>().sizeDelta += Vector2.one;
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "Event Choice Host").GetComponent<RectTransform>().anchorMax =
                    Vector2.one * 0.5f;
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "PaymentRecipientButton").GetComponent<Outline>().effectDistance =
                    Vector2.zero;
            });
            AssertPrefabGateRejects(root =>
            {
                var serializedView = new SerializedObject(root.GetComponent(viewType));
                var eventMode = serializedView.FindProperty("eventCardMode").objectReferenceValue;
                var modeFields = new[]
                {
                    "explorePathMode",
                    "explorePaymentMode",
                    "resourceCollectionPaymentMode",
                    "buildFacilityFocusMode",
                    "buildFacilityConfirmationMode",
                    "legacyCityStyleOptionsMode",
                    "characterSecondEffectDecisionMode"
                };
                for (var i = 0; i < modeFields.Length; i++)
                {
                    serializedView.FindProperty(modeFields[i]).objectReferenceValue = eventMode;
                }

                serializedView.ApplyModifiedPropertiesWithoutUndo();
            });
            AssertPrefabGateRejects(root =>
            {
                var serializedView = new SerializedObject(root.GetComponent(viewType));
                var eventHost = serializedView.FindProperty("eventChoiceHost").objectReferenceValue;
                var pathHost = serializedView.FindProperty("explorePathHost").objectReferenceValue;
                serializedView.FindProperty("eventChoiceHost").objectReferenceValue = pathHost;
                serializedView.FindProperty("explorePathHost").objectReferenceValue = eventHost;
                serializedView.ApplyModifiedPropertiesWithoutUndo();
            });
            AssertPrefabGateRejects(root =>
            {
                var serializedView = new SerializedObject(root.GetComponent(viewType));
                var choice = serializedView.FindProperty("choiceRowTemplate");
                var path = serializedView.FindProperty("pathRowTemplate");
                foreach (var field in new[] { "root", "button", "label" })
                {
                    choice.FindPropertyRelative(field).objectReferenceValue =
                        path.FindPropertyRelative(field).objectReferenceValue;
                }

                serializedView.ApplyModifiedPropertiesWithoutUndo();
            });
            AssertPrefabGateRejects(root =>
            {
                var serializedView = new SerializedObject(root.GetComponent(viewType));
                serializedView.FindProperty("overlayRect").objectReferenceValue =
                    serializedView.FindProperty("panel").objectReferenceValue;
                serializedView.ApplyModifiedPropertiesWithoutUndo();
            });
            AssertPrefabGateRejects(root =>
            {
                var title = FindChild(root, "Title").GetComponent<Text>();
                title.fontSize++;
                title.resizeTextMaxSize++;
            });
            AssertPrefabGateRejects(root =>
            {
                var close = FindChild(root, "Close");
                close.GetComponent<RectTransform>().pivot = Vector2.zero;
                close.GetComponent<Outline>().effectDistance = Vector2.zero;
            });
            AssertPrefabGateRejects(root =>
            {
                var closeLabel = FindChild(root, "Close").GetComponentInChildren<Text>(true);
                Object.DestroyImmediate(closeLabel.GetComponent<Outline>());
            });
            AssertPrefabGateRejects(root =>
            {
                var ordinaryLabel = FindChild(root, "Collapse").GetComponentInChildren<Text>(true);
                ordinaryLabel.gameObject.AddComponent<Outline>();
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "Receiver").GetComponent<Text>().color = Color.magenta;
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "Pay Bank").GetComponentInChildren<Text>(true).resizeTextMinSize = 1;
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "Build Details").GetComponent<RectTransform>().anchorMin = Vector2.zero;
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "LegacyCityStyleRow").transform.Find("Summary")
                    .GetComponent<RectTransform>().anchorMax = Vector2.one;
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "Continue Character Second Effect")
                    .GetComponentInChildren<Text>(true).resizeTextMinSize = 1;
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "Collapsed Summary").GetComponent<Text>().alignment = TextAnchor.LowerRight;
            });
            AssertPrefabGateRejects(root =>
            {
                var injected = new GameObject(
                    "Injected Active Graphic",
                    typeof(RectTransform),
                    typeof(Image));
                injected.transform.SetParent(
                    FindChild(root, "BuildFacilityFocus Mode").transform,
                    false);
            });
            AssertPrefabGateRejects(root =>
            {
                SetCollapsibleSpriteReference(root, "triangleUpSprite", null);
            });
            AssertPrefabGateRejects(root =>
            {
                SetCollapsibleSpriteReference(root, "triangleDownSprite", null);
            });
            AssertPrefabGateRejects(root =>
            {
                var down = GetCollapsibleSpriteReference(root, "triangleDownSprite");
                SetCollapsibleSpriteReference(root, "triangleUpSprite", down);
            });
            AssertPrefabGateRejects(root =>
            {
                var up = GetCollapsibleSpriteReference(root, "triangleUpSprite");
                SetCollapsibleSpriteReference(root, "triangleDownSprite", up);
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "Collapse Triangle").GetComponent<Image>().sprite = null;
            });
            AssertPrefabGateRejects(root =>
            {
                var down = GetCollapsibleSpriteReference(root, "triangleDownSprite");
                FindChild(root, "Collapse Triangle").GetComponent<Image>().sprite = down;
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "Collapse Triangle").GetComponent<Image>().preserveAspect = false;
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "Event Choice Panel").SetActive(false);
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "Action Area").SetActive(false);
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "Confirm Explore").SetActive(false);
            });
            AssertPrefabGateRejects(root =>
            {
                root.GetComponent<Canvas>().enabled = false;
            });
            AssertPrefabGateRejects(root =>
            {
                root.GetComponent<Canvas>().targetDisplay = 1;
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "Title").GetComponent<Text>().enabled = false;
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "Confirm Explore").GetComponent<Image>().enabled = false;
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "ChoiceRow").GetComponent<Button>().interactable = false;
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "PathRow").GetComponent<Button>().targetGraphic = null;
            });
            AssertPrefabGateRejects(root =>
            {
                root.GetComponent<RectTransform>().localScale = Vector3.one;
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "Event Choice Panel").transform.localScale = Vector3.zero;
            });
            AssertPrefabGateRejects(root =>
            {
                FindChild(root, "ResourceCollectionRecipientButton").transform.localScale =
                    Vector3.zero;
            });
        }

        [Test]
        public void EightModes_AreMutuallyExclusiveAndUseExpectedOverlayPolicy()
        {
            var card = CreateEventCard();
            var payments = CreatePayments();
            Invoke(
                "ShowEventCardOptions",
                card,
                "资源点 A",
                payments,
                new Dictionary<string, int>(),
                new Func<int, string>(id => "玩家" + id),
                new Action<int>(_ => { }),
                new Action<string, int>((_, __) => { }));
            AssertMode("Event Choice Overlay", "eventCardMode", false);

            var previous = FindChild(canvas.gameObject, "Event Choice Overlay");
            Invoke(
                "ShowExplorePathOptions",
                new List<ExplorePathChoice> { new ExplorePathChoice(new MapPath(), "路线一") },
                new Action<int>(_ => { }));
            Assert.That(previous == null, Is.True, "Show→Show 必须清理旧 Event prefab 实例。");
            AssertMode("Explore Path Overlay", "explorePathMode", true);

            Invoke(
                "ShowExplorePaymentOptions",
                payments,
                new Dictionary<string, int>(),
                new Func<int, string>(id => "玩家" + id),
                new Action<string, int>((_, __) => { }),
                new Action(() => { }));
            AssertMode("Explore Payment Overlay", "explorePaymentMode", true);

            Invoke(
                "ShowResourceCollectionPaymentOptions",
                "R1",
                2,
                new List<int> { 2 },
                new Func<int, string>(id => "玩家" + id),
                new Action<int>(_ => { }),
                new Action(() => { }),
                new Action(() => { }));
            AssertMode("Resource Collection Payment Overlay", "resourceCollectionPaymentMode", false);

            Invoke("ShowBuildFacilityFocus", CreateBuildModel(BuildFacilityDraftPhase.Focused, _ => { }));
            AssertMode("Build Facility Focus Overlay", "buildFacilityFocusMode", true);

            Invoke("ShowBuildFacilityConfirmation", CreateBuildModel(BuildFacilityDraftPhase.Confirming, _ => { }));
            AssertMode("Build Facility Confirmation Overlay", "buildFacilityConfirmationMode", true);

            Invoke(
                "ShowCityStyleOptions",
                new List<CityStyleOptionViewModel>
                {
                    new CityStyleOptionViewModel
                    {
                        CityStyleId = "style-1",
                        Name = "兼容样式",
                        CanDeclare = true
                    }
                },
                new Action<string>(_ => { }),
                new Action(() => { }));
            AssertMode("City Style Overlay", "legacyCityStyleOptionsMode", true);

            Invoke(
                "ShowCharacterSecondEffectDecision",
                "角色牌",
                "第二效果",
                new Action(() => { }),
                new Action(() => { }));
            AssertMode("Character Second Effect Overlay", "characterSecondEffectDecisionMode", true);
            Assert.That(EditorSceneManager.GetActiveScene().GetRootGameObjects(), Has.Length.EqualTo(6));

            Invoke("Hide");
            Assert.That(FindAnyEventChoiceView(), Is.Null);
            Assert.That(EditorSceneManager.GetActiveScene().GetRootGameObjects(), Has.Length.EqualTo(6));
        }

        [Test]
        public void EventCardRuntimeInstance_NormalizesPrefabRootToVisibleFullCanvasRect()
        {
            Invoke(
                "ShowEventCardOptions",
                CreateEventCard(),
                "资源点 A",
                CreatePayments(),
                new Dictionary<string, int>(),
                new Func<int, string>(id => "玩家" + id),
                new Action<int>(_ => { }),
                new Action<string, int>((_, __) => { }));

            Canvas.ForceUpdateCanvases();
            var root = FindChild(canvas.gameObject, "Event Choice Overlay");
            var rootRect = root.GetComponent<RectTransform>();

            Assert.That(rootRect.localScale, Is.EqualTo(Vector3.one));
            Assert.That(rootRect.localRotation, Is.EqualTo(Quaternion.identity));
            Assert.That(rootRect.anchorMin, Is.EqualTo(Vector2.zero));
            Assert.That(rootRect.anchorMax, Is.EqualTo(Vector2.one));
            Assert.That(rootRect.pivot, Is.EqualTo(new Vector2(0.5f, 0.5f)));
            Assert.That(rootRect.anchoredPosition3D, Is.EqualTo(Vector3.zero));
            Assert.That(rootRect.sizeDelta, Is.EqualTo(Vector2.zero));
            Assert.That(rootRect.rect.size, Is.EqualTo(canvas.rect.size));
            Assert.That(FindChild(root, "Choice Panel").activeInHierarchy, Is.True);
            Assert.That(FindChild(root, "Title").activeInHierarchy, Is.True);
            Assert.That(FindChild(root, "Choice 1").activeInHierarchy, Is.True);
        }

        [Test]
        public void ProfileDrivenGeometry_CoversDynamicTextCountsRowsAndAllPanelModes()
        {
            var card = CreateEventCard();
            var payments = CreatePayments();
            Invoke(
                "ShowEventCardOptions",
                card,
                "资源点 A",
                payments,
                new Dictionary<string, int>(),
                new Func<int, string>(id => "玩家" + id),
                new Action<int>(_ => { }),
                new Action<string, int>((_, __) => { }));

            var eventRoot = FindChild(canvas.gameObject, "Event Choice Overlay");
            var titleHeight = LayoutVector("EventTitleLayout.SizeDelta").y;
            var metadataHeight = LayoutVector("EventMetadataLayout.SizeDelta").y;
            var descriptionHeight = LayoutFloat("EventCardDescriptionMinHeight");
            var descriptionTop = LayoutFloat("EventCardTopPadding") + titleHeight +
                                 LayoutFloat("EventCardTitleMetadataGap") + metadataHeight +
                                 LayoutFloat("EventCardMetadataDescriptionGap");
            var descriptionBottom = descriptionTop + descriptionHeight;
            var firstPaymentCenter = descriptionBottom + LayoutFloat("EventCardPaymentFirstRowGap");
            var firstChoiceCenter = firstPaymentCenter +
                                    LayoutVector("PaymentRouteTemplateLayout.SizeDelta").y * 0.5f +
                                    LayoutFloat("EventCardPaymentChoiceGap") +
                                    LayoutVector("ChoiceRowTemplateLayout.SizeDelta").y * 0.5f;
            var lastChoiceBottom = firstChoiceCenter + LayoutFloat("EventCardChoiceStep") +
                                   LayoutVector("ChoiceRowTemplateLayout.SizeDelta").y * 0.5f;
            var eventExpandedHeight = lastChoiceBottom + LayoutFloat("EventCardToggleButtonTopGap") +
                                      LayoutFloat("EventCardToggleButtonTopInset");
            AssertRectSizeAndPosition(
                FindChild(eventRoot, "Choice Panel").GetComponent<RectTransform>(),
                new Vector2(LayoutFloat("EventCardPanelWidth"), eventExpandedHeight),
                LayoutVector("EventCardPanelPosition"));
            AssertRectSizeAndPosition(
                FindChild(eventRoot, "Description").GetComponent<RectTransform>(),
                new Vector2(LayoutVector("EventDescriptionLayout.SizeDelta").x, descriptionHeight),
                new Vector2(
                    LayoutVector("EventDescriptionLayout.AnchoredPosition").x,
                    -(descriptionTop + descriptionHeight * 0.5f)));
            AssertVectorApproximately(
                FindChild(eventRoot, "Payment R1").GetComponent<RectTransform>().anchoredPosition,
                new Vector2(LayoutVector("PaymentRouteTemplateLayout.AnchoredPosition").x,
                    -firstPaymentCenter));
            AssertVectorApproximately(
                FindChild(eventRoot, "Payment Recipient R1 2").GetComponent<RectTransform>().anchoredPosition,
                new Vector2(
                    LayoutFloat("PaymentRecipientFirstOffsetX"),
                    LayoutVector("PaymentRecipientTemplateLayout.AnchoredPosition").y));
            var eventPanel = FindChild(eventRoot, "Choice Panel").GetComponent<RectTransform>();
            var firstRecipient = FindChild(eventRoot, "Payment Recipient R1 2").GetComponent<RectTransform>();
            var secondRecipient = FindChild(eventRoot, "Payment Recipient R1 3").GetComponent<RectTransform>();
            var recipientAnchorCenter =
                (firstRecipient.anchorMin.x + firstRecipient.anchorMax.x) * 0.5f;
            var firstRecipientPanelX =
                (recipientAnchorCenter - 0.5f) * eventPanel.sizeDelta.x +
                firstRecipient.anchoredPosition.x;
            var secondRecipientPanelX =
                (recipientAnchorCenter - 0.5f) * eventPanel.sizeDelta.x +
                secondRecipient.anchoredPosition.x;
            Assert.That(firstRecipientPanelX, Is.EqualTo(114f).Within(0.001f));
            Assert.That(secondRecipientPanelX - firstRecipientPanelX,
                Is.EqualTo(110f).Within(0.001f));
            Assert.That(
                (firstRecipient.anchorMax.x - firstRecipient.anchorMin.x) * eventPanel.sizeDelta.x +
                firstRecipient.sizeDelta.x,
                Is.EqualTo(440.8f).Within(0.001f));
            Assert.That(firstRecipient.transform.parent.parent.GetComponent<RectTransform>().anchoredPosition.y,
                Is.EqualTo(-firstPaymentCenter).Within(0.001f));
            AssertVectorApproximately(
                FindChild(eventRoot, "Choice 2").GetComponent<RectTransform>().anchoredPosition,
                new Vector2(
                    LayoutVector("ChoiceRowTemplateLayout.AnchoredPosition").x,
                    -firstChoiceCenter - LayoutFloat("EventCardChoiceStep")));

            Invoke(
                "ShowExplorePathOptions",
                new List<ExplorePathChoice>
                {
                    new ExplorePathChoice(new MapPath(), "路线一"),
                    new ExplorePathChoice(new MapPath(), "路线二")
                },
                new Action<int>(_ => { }));
            var pathRoot = FindChild(canvas.gameObject, "Explore Path Overlay");
            AssertRectSizeAndPosition(
                FindChild(pathRoot, "Path Panel").GetComponent<RectTransform>(),
                new Vector2(
                    LayoutFloat("ExplorePathPanelWidth"),
                    LayoutFloat("ExplorePathPanelBaseHeight") +
                    2f * LayoutFloat("ExplorePathPanelRowStep")),
                LayoutVector("ExplorePathPanelPosition"));
            AssertVectorApproximately(
                FindChild(pathRoot, "Path Choice 2").GetComponent<RectTransform>().anchoredPosition,
                new Vector2(
                    LayoutVector("PathRowTemplateLayout.AnchoredPosition").x,
                    -LayoutFloat("ExplorePathFirstRowOffset") -
                    LayoutFloat("ExplorePathPanelRowStep")));

            Invoke(
                "ShowExplorePaymentOptions",
                payments,
                new Dictionary<string, int>(),
                new Func<int, string>(id => "玩家" + id),
                new Action<string, int>((_, __) => { }),
                new Action(() => { }));
            var paymentRoot = FindChild(canvas.gameObject, "Explore Payment Overlay");
            AssertRectSizeAndPosition(
                FindChild(paymentRoot, "Payment Panel").GetComponent<RectTransform>(),
                new Vector2(
                    LayoutFloat("ExplorePaymentPanelWidth"),
                    LayoutFloat("ExplorePaymentPanelBaseHeight") +
                    LayoutFloat("ExplorePaymentPanelRowStep")),
                LayoutVector("ExplorePaymentPanelPosition"));
            AssertVectorApproximately(
                FindChild(paymentRoot, "Confirm Explore").GetComponent<RectTransform>().anchoredPosition,
                new Vector2(
                    LayoutVector("ExploreConfirmTemplateLayout.AnchoredPosition").x,
                    -LayoutFloat("ExplorePaymentConfirmBaseOffset") -
                    LayoutFloat("ExplorePaymentPanelRowStep")));
            AssertVectorApproximately(
                FindChild(paymentRoot, "Payment R1").GetComponent<RectTransform>().anchoredPosition,
                new Vector2(
                    LayoutVector("PaymentRouteTemplateLayout.AnchoredPosition").x,
                    -LayoutFloat("ExplorePaymentFirstRouteOffset")));

            Invoke(
                "ShowResourceCollectionPaymentOptions",
                "R1",
                2,
                new List<int> { 2, 3 },
                new Func<int, string>(id => "玩家" + id),
                new Action<int>(_ => { }),
                new Action(() => { }),
                new Action(() => { }));
            var resourceRoot = FindChild(canvas.gameObject, "Resource Collection Payment Overlay");
            AssertRectSizeAndPosition(
                FindChild(resourceRoot, "Resource Collection Payment Panel").GetComponent<RectTransform>(),
                new Vector2(
                    LayoutFloat("ResourcePaymentPanelWidth"),
                    LayoutFloat("ResourcePaymentPanelBaseHeight") +
                    2f * LayoutFloat("ResourcePaymentPanelRowStep")),
                LayoutVector("ResourcePaymentPanelPosition"));
            AssertVectorApproximately(
                FindChild(resourceRoot, "Pay Player 3").GetComponent<RectTransform>().anchoredPosition,
                new Vector2(
                    LayoutVector("ResourceRecipientTemplateLayout.AnchoredPosition").x,
                    -LayoutFloat("ResourcePaymentFirstRowOffset") -
                    LayoutFloat("ResourcePaymentPanelRowStep")));

            Invoke("ShowBuildFacilityFocus", CreateBuildModel(BuildFacilityDraftPhase.Focused, _ => { }));
            AssertRectSizeAndPosition(
                FindChild(canvas.gameObject, "Build Facility Focus Panel").GetComponent<RectTransform>(),
                LayoutVector("BuildFacilityFocusPanelSize"),
                LayoutVector("BuildFacilityFocusPanelPosition"));
            Invoke("ShowBuildFacilityConfirmation", CreateBuildModel(BuildFacilityDraftPhase.Confirming, _ => { }));
            AssertRectSizeAndPosition(
                FindChild(canvas.gameObject, "Build Facility Confirmation Panel").GetComponent<RectTransform>(),
                LayoutVector("BuildFacilityConfirmationPanelSize"),
                LayoutVector("BuildFacilityConfirmationPanelPosition"));

            Invoke(
                "ShowCityStyleOptions",
                new List<CityStyleOptionViewModel>
                {
                    new CityStyleOptionViewModel { CityStyleId = "style-1", Name = "样式一", CanDeclare = true },
                    new CityStyleOptionViewModel { CityStyleId = "style-2", Name = "样式二", CanDeclare = true }
                },
                new Action<string>(_ => { }),
                new Action(() => { }));
            var cityRoot = FindChild(canvas.gameObject, "City Style Overlay");
            AssertRectSizeAndPosition(
                FindChild(cityRoot, "City Style Panel").GetComponent<RectTransform>(),
                new Vector2(
                    LayoutFloat("LegacyCityStylePanelWidth"),
                    LayoutFloat("LegacyCityStylePanelBaseHeight") +
                    2f * LayoutFloat("LegacyCityStylePanelRowStep")),
                LayoutVector("LegacyCityStylePanelPosition"));
            AssertVectorApproximately(
                FindChild(cityRoot, "City Style 1").GetComponent<RectTransform>().anchoredPosition,
                new Vector2(
                    LayoutVector("LegacyCityStyleTemplateLayout.AnchoredPosition").x,
                    -LayoutFloat("LegacyCityStyleFirstRowOffset") -
                    LayoutFloat("LegacyCityStylePanelRowStep")));
            var cityPanel = FindChild(cityRoot, "City Style Panel").GetComponent<RectTransform>();
            var cityRow = FindChild(cityRoot, "City Style 1").GetComponent<RectTransform>();
            var citySummary = cityRow.Find("Summary").GetComponent<RectTransform>();
            var cityReason = cityRow.Find("Reason").GetComponent<RectTransform>();
            var cityDeclare = FindChild(cityRow.gameObject, "Declare City Style 1")
                .GetComponent<RectTransform>();
            Assert.That(
                (citySummary.anchorMax.x - citySummary.anchorMin.x) * cityPanel.sizeDelta.x,
                Is.EqualTo(510.4f).Within(0.001f));
            Assert.That(
                ((citySummary.anchorMin.x + citySummary.anchorMax.x) * 0.5f - 0.5f) *
                cityPanel.sizeDelta.x,
                Is.EqualTo(-132f).Within(0.001f));
            Assert.That(
                ((cityReason.anchorMin.x + cityReason.anchorMax.x) * 0.5f - 0.5f) *
                cityPanel.sizeDelta.x,
                Is.EqualTo(211.2f).Within(0.001f));
            Assert.That(
                ((cityDeclare.anchorMin.x + cityDeclare.anchorMax.x) * 0.5f - 0.5f) *
                cityPanel.sizeDelta.x,
                Is.EqualTo(343.2f).Within(0.001f));
            Assert.That(citySummary.anchoredPosition.y + cityRow.anchoredPosition.y,
                Is.EqualTo(-176f).Within(0.001f));

            Invoke(
                "ShowCharacterSecondEffectDecision",
                "角色牌",
                "第二效果",
                new Action(() => { }),
                new Action(() => { }));
            var characterRoot = FindChild(canvas.gameObject, "Character Second Effect Overlay");
            AssertRectSizeAndPosition(
                FindChild(characterRoot, "Character Second Effect Dialog").GetComponent<RectTransform>(),
                LayoutVector("CharacterSecondEffectPanelSize"),
                LayoutVector("CharacterSecondEffectPanelPosition"));
            AssertRectLayout(
                FindChild(characterRoot, "Character Second Effect Title").GetComponent<RectTransform>(),
                "CharacterTitleLayout");
            AssertRectLayout(
                FindChild(characterRoot, "Character Second Effect Description").GetComponent<RectTransform>(),
                "CharacterDescriptionLayout");
        }

        [Test]
        public void EventCardCollapse_IsTransparentPassthroughStableAndPreservesDragPosition()
        {
            Invoke(
                "ShowEventCardOptions",
                CreateEventCard(),
                "资源点 A",
                CreatePayments(),
                new Dictionary<string, int>(),
                new Func<int, string>(id => "玩家" + id),
                new Action<int>(_ => { }),
                new Action<string, int>((_, __) => { }));
            var root = FindChild(canvas.gameObject, "Event Choice Overlay");
            var view = root.GetComponent(viewType);
            var panel = GetObjectReference<RectTransform>(view, "panel");
            var overlay = GetObjectReference<Image>(view, "overlayImage");
            var collapsible = GetObjectReference<Component>(view, "collapsiblePanel");
            var collapseButton = GetObjectReference<Button>(view, "collapseButton")
                .GetComponent<RectTransform>();
            Assert.That(collapseButton.anchorMin, Is.EqualTo(new Vector2(0.5f, 0f)));
            Assert.That(collapseButton.anchorMax, Is.EqualTo(new Vector2(0.5f, 0f)));
            Assert.That(collapseButton.pivot, Is.EqualTo(new Vector2(0.5f, 0.5f)));
            Assert.That(collapseButton.sizeDelta, Is.EqualTo(new Vector2(220f, 32f)));
            Assert.That(collapseButton.anchoredPosition, Is.EqualTo(new Vector2(0f, 22f)));
            var clampToCanvasBounds = collapsible.GetType().GetField(
                "clampToCanvasBounds",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(clampToCanvasBounds, Is.Not.Null);
            Assert.That(
                (bool)clampToCanvasBounds.GetValue(collapsible),
                Is.False,
                "Event Card 必须保持 HEAD 的自由拖拽语义，不得钳制到零尺寸 Prefab 根。");
            var expandedPosition = panel.anchoredPosition;

            Invoke("CollapseForMapInteraction");
            var firstCollapsedPosition = panel.anchoredPosition;
            Invoke("CollapseForMapInteraction");
            Assert.That(panel.anchoredPosition, Is.EqualTo(firstCollapsedPosition));
            Assert.That(overlay.raycastTarget, Is.False);
            Assert.That(overlay.color.a, Is.EqualTo(0f).Within(0.001f));
            Assert.That(collapseButton.anchorMin, Is.EqualTo(new Vector2(1f, 0.5f)));
            Assert.That(collapseButton.anchorMax, Is.EqualTo(new Vector2(1f, 0.5f)));
            Assert.That(collapseButton.pivot, Is.EqualTo(new Vector2(0.5f, 0.5f)));
            Assert.That(collapseButton.sizeDelta, Is.EqualTo(new Vector2(126f, 34f)));
            Assert.That(collapseButton.anchoredPosition, Is.EqualTo(new Vector2(-76f, 0f)));

            var dragDelta = new Vector2(36f, 18f);
            var canvasScaleFactor = canvas.GetComponent<Canvas>().scaleFactor;
            var anchoredDragDelta = dragDelta /
                                    (canvasScaleFactor <= 0f ? 1f : canvasScaleFactor);
            collapsible.GetType().GetMethod("OnDrag").Invoke(
                collapsible,
                new object[] { new PointerEventData(EventSystem.current) { delta = dragDelta } });
            var draggedCollapsedPosition = panel.anchoredPosition;
            Assert.That(
                draggedCollapsedPosition.x,
                Is.EqualTo(firstCollapsedPosition.x + anchoredDragDelta.x).Within(0.01f));
            collapsible.GetType().GetMethod("Toggle").Invoke(collapsible, null);
            Assert.That(
                panel.anchoredPosition.x,
                Is.EqualTo(expandedPosition.x + anchoredDragDelta.x).Within(0.01f));
            Assert.That(
                panel.anchoredPosition.y,
                Is.EqualTo(expandedPosition.y + anchoredDragDelta.y).Within(0.01f));
            Assert.That(overlay.raycastTarget, Is.False);
            Assert.That(collapseButton.anchorMin, Is.EqualTo(new Vector2(0.5f, 0f)));
            Assert.That(collapseButton.anchorMax, Is.EqualTo(new Vector2(0.5f, 0f)));
            Assert.That(collapseButton.pivot, Is.EqualTo(new Vector2(0.5f, 0.5f)));
            Assert.That(collapseButton.sizeDelta, Is.EqualTo(new Vector2(220f, 32f)));
            Assert.That(collapseButton.anchoredPosition, Is.EqualTo(new Vector2(0f, 22f)));
        }

        [Test]
        public void ExplorePaymentRecipient_RepaintsWithoutTerminalHideAndNewCallbacksRemainUsable()
        {
            var payments = CreatePayments();
            var recipients = new Dictionary<string, int>();
            var recipientCount = 0;
            var confirmCount = 0;
            Action<string, int> selectRecipient = null;
            selectRecipient = (routeId, playerId) =>
            {
                recipientCount++;
                recipients[routeId] = playerId;
                Invoke(
                    "ShowExplorePaymentOptions",
                    payments,
                    recipients,
                    new Func<int, string>(id => "玩家" + id),
                    selectRecipient,
                    new Action(() => confirmCount++));
            };

            Invoke(
                "ShowExplorePaymentOptions",
                payments,
                recipients,
                new Func<int, string>(id => "玩家" + id),
                selectRecipient,
                new Action(() => confirmCount++));
            var first = FindChild(canvas.gameObject, "Explore Payment Overlay");
            var recipient = FindChild(first, "Payment Recipient R1 2").GetComponent<Button>();
            recipient.onClick.Invoke();
            recipient.onClick.Invoke();
            Assert.That(recipientCount, Is.EqualTo(1));
            Assert.That(first == null, Is.True);

            var second = FindChild(canvas.gameObject, "Explore Payment Overlay");
            Assert.That(second, Is.Not.Null);
            var confirm = FindChild(second, "Confirm Explore").GetComponent<Button>();
            confirm.onClick.Invoke();
            confirm.onClick.Invoke();
            Assert.That(confirmCount, Is.EqualTo(1));
            Assert.That(second == null, Is.False, "多步确认回调不得误 Hide 当前窗口。");
        }

        [Test]
        public void TerminalCallbacks_HideBeforeDispatchAndDispatchOnlyOnce()
        {
            var resourceCount = 0;
            var resourceHidden = false;
            Invoke(
                "ShowResourceCollectionPaymentOptions",
                "R1",
                2,
                new List<int> { 2 },
                new Func<int, string>(id => "玩家" + id),
                new Action<int>(_ =>
                {
                    resourceCount++;
                    resourceHidden = FindChild(canvas.gameObject, "Resource Collection Payment Overlay") == null;
                }),
                new Action(() => { }),
                new Action(() => { }));
            var resourceRoot = FindChild(canvas.gameObject, "Resource Collection Payment Overlay");
            var payPlayer = FindChild(resourceRoot, "Pay Player 2").GetComponent<Button>().onClick;
            payPlayer.Invoke();
            payPlayer.Invoke();
            Assert.That(resourceCount, Is.EqualTo(1));
            Assert.That(resourceHidden, Is.True);

            var characterCount = 0;
            var characterHidden = false;
            Invoke(
                "ShowCharacterSecondEffectDecision",
                "角色牌",
                "第二效果",
                new Action(() =>
                {
                    characterCount++;
                    characterHidden = FindChild(canvas.gameObject, "Character Second Effect Overlay") == null;
                }),
                new Action(() => { }));
            var characterRoot = FindChild(canvas.gameObject, "Character Second Effect Overlay");
            var continueClick = FindChild(characterRoot, "Continue Character Second Effect").GetComponent<Button>().onClick;
            continueClick.Invoke();
            continueClick.Invoke();
            Assert.That(characterCount, Is.EqualTo(1));
            Assert.That(characterHidden, Is.True);

            var styleCount = 0;
            var styleHidden = false;
            Invoke(
                "ShowCityStyleOptions",
                new List<CityStyleOptionViewModel>
                {
                    new CityStyleOptionViewModel
                    {
                        CityStyleId = "style-1",
                        Name = "兼容样式",
                        CanDeclare = true
                    }
                },
                new Action<string>(_ =>
                {
                    styleCount++;
                    styleHidden = FindChild(canvas.gameObject, "City Style Overlay") == null;
                }),
                new Action(() => { }));
            var styleRoot = FindChild(canvas.gameObject, "City Style Overlay");
            var declareClick = FindChild(styleRoot, "Declare City Style 0").GetComponent<Button>().onClick;
            declareClick.Invoke();
            declareClick.Invoke();
            Assert.That(styleCount, Is.EqualTo(1));
            Assert.That(styleHidden, Is.True);
        }

        [Test]
        public void BuildIntents_CloseAndRightClick_DispatchOnceWithoutPrematureHide()
        {
            var intents = new List<BuildFacilityIntent>();
            Invoke("ShowBuildFacilityFocus", CreateBuildModel(BuildFacilityDraftPhase.Focused, intents.Add));
            var focus = FindChild(canvas.gameObject, "Build Facility Focus Overlay");
            var resource = FindChild(focus, "Choose Resource Payment").GetComponent<Button>().onClick;
            resource.Invoke();
            resource.Invoke();
            AssertIntent<BuildFacilityIntent.SelectPayment>(intents);
            Assert.That(focus == null, Is.False);

            intents.Clear();
            Invoke("ShowBuildFacilityFocus", CreateBuildModel(BuildFacilityDraftPhase.Focused, intents.Add));
            focus = FindChild(canvas.gameObject, "Build Facility Focus Overlay");
            var close = FindChild(focus, "Close Build Facility Focus Button").GetComponent<Button>().onClick;
            close.Invoke();
            close.Invoke();
            AssertIntent<BuildFacilityIntent.Cancel>(intents);
            Assert.That(focus == null, Is.False);

            intents.Clear();
            Invoke("ShowBuildFacilityFocus", CreateBuildModel(BuildFacilityDraftPhase.Focused, intents.Add));
            focus = FindChild(canvas.gameObject, "Build Facility Focus Overlay");
            var closeHandler = focus.GetComponent(GetRuntimeType("YC.Presentation.WindowCloseInputHandler"));
            closeHandler.GetType().GetMethod("RequestClose").Invoke(closeHandler, null);
            closeHandler.GetType().GetMethod("RequestClose").Invoke(closeHandler, null);
            AssertIntent<BuildFacilityIntent.Cancel>(intents);

            intents.Clear();
            Invoke("ShowBuildFacilityConfirmation", CreateBuildModel(BuildFacilityDraftPhase.Confirming, intents.Add));
            var confirmation = FindChild(canvas.gameObject, "Build Facility Confirmation Overlay");
            var back = FindChild(confirmation, "Back To Build Payment").GetComponent<Button>().onClick;
            back.Invoke();
            back.Invoke();
            AssertIntent<BuildFacilityIntent.Back>(intents);

            intents.Clear();
            Invoke("ShowBuildFacilityConfirmation", CreateBuildModel(BuildFacilityDraftPhase.Confirming, intents.Add));
            confirmation = FindChild(canvas.gameObject, "Build Facility Confirmation Overlay");
            var confirm = FindChild(confirmation, "Confirm Build Facility").GetComponent<Button>().onClick;
            confirm.Invoke();
            confirm.Invoke();
            AssertIntent<BuildFacilityIntent.Confirm>(intents);
        }

        [Test]
        public void RuntimeController_HasOnlyExplicitRegistryConstructionAndNoFixedUiConstruction()
        {
            var source = System.IO.File.ReadAllText(
                System.IO.Path.Combine(UnityEngine.Application.dataPath, "YC/Presentation/EventChoiceDialog.cs"));
            StringAssert.DoesNotContain("new GameObject", source);
            StringAssert.DoesNotContain("AddComponent", source);
            StringAssert.DoesNotContain("Resources.", source);
            StringAssert.DoesNotContain("Find(", source);
            StringAssert.Contains("EventChoiceDialog(GameplayDialogRegistry configuredRegistry", source);
            StringAssert.DoesNotContain("RectTransform canvasTransform", source);
            StringAssert.Contains("InstantiateEventChoiceDialog(parent)", source);
        }

        [Test]
        public void SourceGate_UsesStableConsumerHashesAndRejectsTransformBypasses()
        {
            var dialogSource = System.IO.File.ReadAllText(
                System.IO.Path.Combine(UnityEngine.Application.dataPath, "YC/Presentation/EventChoiceDialog.cs"));
            var viewSource = System.IO.File.ReadAllText(
                System.IO.Path.Combine(UnityEngine.Application.dataPath, "YC/Presentation/EventChoiceDialogView.cs"));
            const string receiverAssignment =
                "view.ResourcePaymentReceiverText.text = receiverLabel;";
            StringAssert.Contains(receiverAssignment, dialogSource);
            var anchoredPositionMutation = dialogSource.Replace(
                receiverAssignment,
                receiverAssignment + Environment.NewLine +
                "            view.ResourcePaymentReceiverText.rectTransform.anchoredPosition = Vector2.down * 999f;");
            Assert.That(
                anchoredPositionMutation.Split(new[] { "new Vector2" }, StringSplitOptions.None).Length,
                Is.EqualTo(dialogSource.Split(new[] { "new Vector2" }, StringSplitOptions.None).Length),
                "负测必须证明不依赖 new Vector2 计数才能拒绝固定覆盖。");

            AssertConsumerSourceGateAccepts(dialogSource, viewSource);
            AssertConsumerSourceGateAccepts(
                AddTrailingWhitespaceAndNormalizeNewlines(dialogSource),
                AddTrailingWhitespaceAndNormalizeNewlines(viewSource));
            AssertConsumerSourceGateRejects(anchoredPositionMutation, viewSource);
            AssertConsumerSourceGateRejects(
                dialogSource.Replace(
                    receiverAssignment,
                    receiverAssignment + Environment.NewLine +
                    "            view.ResourcePaymentReceiverText.rectTransform.localPosition = Vector3.one;"),
                viewSource);
            AssertConsumerSourceGateRejects(
                dialogSource.Replace(
                    receiverAssignment,
                    receiverAssignment + Environment.NewLine +
                    "            view.ResourcePaymentReceiverText.rectTransform.Translate(Vector3.one);"),
                viewSource);
        }

        [Test]
        public void SampleSceneGate_RejectsHudReferenceOverridesRemovalDuplicateAndWrongSource()
        {
            var sceneYaml = System.IO.File.ReadAllText(
                System.IO.Path.GetFullPath(SampleScenePath));
            AssertSampleSceneYamlGateAccepts(sceneYaml);
            StringAssert.Contains("5963824492678046178", sceneYaml);
            StringAssert.Contains("propertyPath: m_Name", ExtractHudInstanceBlock(sceneYaml));

            AssertSampleSceneYamlGateRejects(InsertHudModification(
                sceneYaml,
                "7007614311448219973",
                "eventChoiceDialogPrefab",
                "{fileID: 0}"));
            AssertSampleSceneYamlGateRejects(InsertHudModification(
                sceneYaml,
                "7007614311448219973",
                "eventChoiceDialogPrefab",
                "{fileID: 2364430295288198237, guid: 00000000000000000000000000000000, type: 3}"));
            AssertSampleSceneYamlGateRejects(InsertHudModification(
                sceneYaml,
                "7007614311448219973",
                "m_Script",
                "{fileID: 11500000, guid: 00000000000000000000000000000000, type: 3}"));
            AssertSampleSceneYamlGateRejects(InsertHudModification(
                sceneYaml,
                "65381164276375644",
                "dialogRegistry",
                "{fileID: 0}"));
            AssertSampleSceneYamlGateRejects(InsertHudModification(
                sceneYaml,
                "5963824492678046178",
                "m_IsActive",
                "{fileID: 0}"));

            const string sourceLine =
                "m_SourcePrefab: {fileID: 100100000, guid: 97d4d860d450f7247969c8aca5a9d934, type: 3}";
            StringAssert.Contains(sourceLine, sceneYaml);
            AssertSampleSceneYamlGateRejects(sceneYaml.Replace(
                sourceLine,
                "m_SourcePrefab: {fileID: 100100000, guid: 00000000000000000000000000000000, type: 3}"));

            var hudBlock = ExtractHudInstanceBlock(sceneYaml);
            AssertSampleSceneYamlGateRejects(
                sceneYaml + Environment.NewLine + hudBlock);
            AssertSampleSceneYamlGateRejects(ReplaceInHudInstanceBlock(
                sceneYaml,
                "    m_RemovedComponents: []",
                "    m_RemovedComponents:" + Environment.NewLine +
                "    - {fileID: 7007614311448219973, guid: 97d4d860d450f7247969c8aca5a9d934, type: 3}"));
            AssertSampleSceneYamlGateRejects(ReplaceInHudInstanceBlock(
                sceneYaml,
                "    m_RemovedComponents: []",
                "    m_RemovedComponents:" + Environment.NewLine +
                "    - {fileID: 65381164276375644, guid: 97d4d860d450f7247969c8aca5a9d934, type: 3}"));
            AssertSampleSceneYamlGateRejects(ReplaceInHudInstanceBlock(
                sceneYaml,
                "    m_RemovedGameObjects: []",
                "    m_RemovedGameObjects:" + Environment.NewLine +
                "    - {fileID: 6753386143783961735, guid: 97d4d860d450f7247969c8aca5a9d934, type: 3}"));
            AssertSampleSceneYamlGateRejects(ReplaceInHudInstanceBlock(
                sceneYaml,
                "    m_RemovedGameObjects: []",
                "    m_RemovedGameObjects:" + Environment.NewLine +
                "    - {fileID: 5963824492678046178, guid: 97d4d860d450f7247969c8aca5a9d934, type: 3}"));
        }

        private void AssertMode(string rootName, string selectedProperty, bool blocksBackground)
        {
            var root = FindChild(canvas.gameObject, rootName);
            Assert.That(root, Is.Not.Null, rootName);
            var view = root.GetComponent(viewType);
            Assert.That(view, Is.Not.Null);
            var serialized = new SerializedObject(view);
            var modeProperties = new[]
            {
                "eventCardMode",
                "explorePathMode",
                "explorePaymentMode",
                "resourceCollectionPaymentMode",
                "buildFacilityFocusMode",
                "buildFacilityConfirmationMode",
                "legacyCityStyleOptionsMode",
                "characterSecondEffectDecisionMode"
            };
            var activeCount = 0;
            for (var i = 0; i < modeProperties.Length; i++)
            {
                var block = serialized.FindProperty(modeProperties[i]).objectReferenceValue as GameObject;
                Assert.That(block, Is.Not.Null, modeProperties[i]);
                if (block.activeSelf)
                {
                    activeCount++;
                }
                Assert.That(block.activeSelf, Is.EqualTo(modeProperties[i] == selectedProperty), modeProperties[i]);
            }

            Assert.That(activeCount, Is.EqualTo(1));
            var overlay = GetObjectReference<Image>(view, "overlayImage");
            Assert.That(overlay.raycastTarget, Is.EqualTo(blocksBackground));
            Assert.That(overlay.color.a, Is.EqualTo(LayoutFloat("OverlayAlpha")).Within(0.001f));

            var titlePrefix = GetTitlePrefix(selectedProperty);
            var title = GetObjectReference<Text>(view, "titleText");
            AssertRectLayout(title.rectTransform, titlePrefix + "TitleLayout");
            AssertTextStyle(title, titlePrefix + "TitleTextStyle");
            AssertColorApproximately(title.color, ThemeColor("GoldText"));

            var closeButton = GetObjectReference<Button>(view, "closeButton");
            var closeLabel = GetObjectReference<Text>(view, "closeButtonLabel");
            if (selectedProperty == "resourceCollectionPaymentMode" ||
                selectedProperty == "legacyCityStyleOptionsMode")
            {
                Assert.That(closeButton.gameObject.activeSelf, Is.True);
                Assert.That(closeLabel.text, Is.EqualTo("X"));
                AssertButtonStyle(closeButton, closeLabel, "ManualCloseButtonLayout", "ManualCloseButtonStyle");
            }
            else if (selectedProperty == "buildFacilityFocusMode" ||
                     selectedProperty == "buildFacilityConfirmationMode")
            {
                Assert.That(closeButton.gameObject.activeSelf, Is.True);
                Assert.That(closeLabel.text, Is.EqualTo("×"));
                AssertButtonStyle(closeButton, closeLabel, "BuildCloseButtonLayout", "BuildCloseButtonStyle");
            }
            else
            {
                Assert.That(closeButton.gameObject.activeSelf, Is.False);
            }

            Assert.That(EditorSceneManager.GetActiveScene().GetRootGameObjects(), Has.Length.EqualTo(6));
        }

        private static string GetTitlePrefix(string selectedProperty)
        {
            switch (selectedProperty)
            {
                case "eventCardMode":
                    return "Event";
                case "explorePathMode":
                    return "ExplorePath";
                case "explorePaymentMode":
                    return "ExplorePayment";
                case "resourceCollectionPaymentMode":
                    return "ResourcePayment";
                case "buildFacilityFocusMode":
                    return "BuildFocus";
                case "buildFacilityConfirmationMode":
                    return "BuildConfirmation";
                case "legacyCityStyleOptionsMode":
                    return "LegacyCityStyle";
                case "characterSecondEffectDecisionMode":
                    return "Character";
                default:
                    throw new ArgumentOutOfRangeException(nameof(selectedProperty), selectedProperty, null);
            }
        }

        private object Invoke(string methodName, params object[] arguments)
        {
            var method = dialogType.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, methodName);
            return method.Invoke(dialog, arguments);
        }

        private void InvokeLayoutReadiness()
        {
            var readiness = GetEditorType("YC.Editor.EventChoiceDialogLayoutBuildReadiness");
            var validate = readiness.GetMethod(
                "ValidateReadyForBuild",
                BindingFlags.Public | BindingFlags.Static);
            Assert.That(validate, Is.Not.Null);
            validate.Invoke(null, null);
        }

        private static void InvokeEditorStatic(string typeName, string methodName)
        {
            var type = GetEditorType(typeName);
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, typeName + "." + methodName);
            method.Invoke(null, null);
        }

        private static void AssertMissingMetaGateRejects(string typeName, string missingAssetPath)
        {
            var type = GetEditorType(typeName);
            var method = type.GetMethod(
                "EnsureControlledMetaGuid",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, typeName + ".EnsureControlledMetaGuid");
            var exception = Assert.Throws<TargetInvocationException>(() =>
                method.Invoke(null, new object[]
                {
                    missingAssetPath,
                    "00000000000000000000000000000000"
                }));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        private static void AssertMetaGuidTextGateRejects(
            MethodInfo validator,
            string metaText,
            string expectedGuid,
            string actualAssetGuid)
        {
            var exception = Assert.Throws<TargetInvocationException>(() =>
                validator.Invoke(
                    null,
                    new object[] { metaText, expectedGuid, actualAssetGuid }));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        private static void AssertControlledMetaGuid(string assetPath, string expectedGuid)
        {
            var metaPath = System.IO.Path.GetFullPath(assetPath + ".meta");
            Assert.That(System.IO.File.Exists(metaPath), Is.True, metaPath);
            var matches = System.Text.RegularExpressions.Regex.Matches(
                System.IO.File.ReadAllText(metaPath),
                @"(?m)^guid:[ \t]*(?<guid>[0-9a-fA-F]{32})[ \t]*\r?$");
            Assert.That(matches.Count, Is.EqualTo(1), metaPath);
            Assert.That(
                matches[0].Groups["guid"].Value,
                Is.EqualTo(expectedGuid).IgnoreCase,
                metaPath);
            Assert.That(
                AssetDatabase.AssetPathToGUID(assetPath),
                Is.EqualTo(expectedGuid).IgnoreCase,
                assetPath);
        }

        private static void AssertAssetIdentity(
            Object asset,
            string expectedPath,
            string expectedGuid,
            long expectedLocalId)
        {
            Assert.That(asset, Is.Not.Null);
            Assert.That(EditorUtility.IsPersistent(asset), Is.True);
            Assert.That(AssetDatabase.GetAssetPath(asset), Is.EqualTo(expectedPath));
            Assert.That(
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    asset,
                    out var guid,
                    out long localId),
                Is.True);
            Assert.That(guid, Is.EqualTo(expectedGuid).IgnoreCase);
            Assert.That(localId, Is.EqualTo(expectedLocalId));
        }

        private static string ComputeFileSha256(string assetPath)
        {
            var absolutePath = System.IO.Path.GetFullPath(assetPath);
            Assert.That(System.IO.File.Exists(absolutePath), Is.True, absolutePath);
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(System.IO.File.ReadAllBytes(absolutePath)))
                    .Replace("-", string.Empty);
            }
        }

        private static void AssertConsumerSourceGateAccepts(
            string dialogSource,
            string viewSource)
        {
            var method = GetEditorType("YC.Editor.EventChoiceDialogLayoutBuildReadiness")
                .GetMethod(
                    "ValidateConsumerSourceContract",
                    BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            Assert.DoesNotThrow(() => method.Invoke(
                null,
                new object[] { dialogSource, viewSource }));
        }

        private static void AssertConsumerSourceGateRejects(
            string dialogSource,
            string viewSource)
        {
            var method = GetEditorType("YC.Editor.EventChoiceDialogLayoutBuildReadiness")
                .GetMethod(
                    "ValidateConsumerSourceContract",
                    BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            var exception = Assert.Throws<TargetInvocationException>(() =>
                method.Invoke(null, new object[] { dialogSource, viewSource }));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        private static string AddTrailingWhitespaceAndNormalizeNewlines(string source)
        {
            var lines = source
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                lines[i] += " \t";
            }

            return string.Join("\n", lines);
        }

        private static void AssertSampleSceneYamlGateAccepts(string sceneYaml)
        {
            var method = GetEditorType("YC.Editor.EventChoiceDialogLayoutBuildReadiness")
                .GetMethod(
                    "ValidateSampleSceneYaml",
                    BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            Assert.DoesNotThrow(() => method.Invoke(null, new object[] { sceneYaml }));
        }

        private static void AssertSampleSceneYamlGateRejects(string sceneYaml)
        {
            var method = GetEditorType("YC.Editor.EventChoiceDialogLayoutBuildReadiness")
                .GetMethod(
                    "ValidateSampleSceneYaml",
                    BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            var exception = Assert.Throws<TargetInvocationException>(() =>
                method.Invoke(null, new object[] { sceneYaml }));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        private static string InsertHudModification(
            string sceneYaml,
            string targetLocalId,
            string propertyPath,
            string objectReference)
        {
            const string modificationsMarker = "    m_Modifications:";
            var blockStart = sceneYaml.IndexOf(
                "--- !u!1001 &1599970241",
                StringComparison.Ordinal);
            Assert.That(blockStart, Is.GreaterThanOrEqualTo(0));
            var marker = sceneYaml.IndexOf(
                modificationsMarker,
                blockStart,
                StringComparison.Ordinal);
            Assert.That(marker, Is.GreaterThanOrEqualTo(blockStart));
            var insertion = sceneYaml.IndexOf('\n', marker);
            Assert.That(insertion, Is.GreaterThan(marker));
            insertion++;
            var newLine = sceneYaml.Contains("\r\n") ? "\r\n" : "\n";
            var entry =
                "    - target: {fileID: " + targetLocalId +
                ", guid: 97d4d860d450f7247969c8aca5a9d934, type: 3}" + newLine +
                "      propertyPath: " + propertyPath + newLine +
                "      value: " + newLine +
                "      objectReference: " + objectReference + newLine;
            return sceneYaml.Insert(insertion, entry);
        }

        private static string ExtractHudInstanceBlock(string sceneYaml)
        {
            var start = sceneYaml.IndexOf(
                "--- !u!1001 &1599970241",
                StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            var end = sceneYaml.IndexOf("\n--- ", start + 4, StringComparison.Ordinal);
            if (end < 0)
            {
                end = sceneYaml.Length;
            }

            return sceneYaml.Substring(start, end - start);
        }

        private static string ReplaceInHudInstanceBlock(
            string sceneYaml,
            string oldValue,
            string newValue)
        {
            var block = ExtractHudInstanceBlock(sceneYaml);
            StringAssert.Contains(oldValue, block);
            var mutatedBlock = block.Replace(oldValue, newValue);
            return sceneYaml.Replace(block, mutatedBlock);
        }

        private void AssertPrefabGateRejects(Action<GameObject> mutate)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(EventChoiceDialogPrefabPath);
            Assert.That(prefab, Is.Not.Null);
            var clone = Object.Instantiate(prefab);
            try
            {
                mutate(clone);
                var readiness = GetEditorType("YC.Editor.EventChoiceDialogLayoutBuildReadiness");
                var validate = readiness.GetMethod(
                    "ValidatePrefabContents",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(validate, Is.Not.Null);
                var exception = Assert.Throws<TargetInvocationException>(() =>
                {
                    validate.Invoke(null, new object[] { clone, layoutProfile });
                });
                Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
            }
            finally
            {
                Object.DestroyImmediate(clone);
            }
        }

        private static Sprite GetCollapsibleSpriteReference(
            GameObject root,
            string propertyName)
        {
            var panel = FindChild(root, "Event Choice Panel");
            Assert.That(panel, Is.Not.Null);
            var collapsible = panel.GetComponent(
                GetRuntimeType("YC.Presentation.EffectDialogCollapsiblePanel"));
            Assert.That(collapsible, Is.Not.Null);
            var property = new SerializedObject(collapsible).FindProperty(propertyName);
            Assert.That(property, Is.Not.Null, propertyName);
            var sprite = property.objectReferenceValue as Sprite;
            Assert.That(sprite, Is.Not.Null, propertyName);
            return sprite;
        }

        private static void SetCollapsibleSpriteReference(
            GameObject root,
            string propertyName,
            Sprite sprite)
        {
            var panel = FindChild(root, "Event Choice Panel");
            Assert.That(panel, Is.Not.Null);
            var collapsible = panel.GetComponent(
                GetRuntimeType("YC.Presentation.EffectDialogCollapsiblePanel"));
            Assert.That(collapsible, Is.Not.Null);
            var serialized = new SerializedObject(collapsible);
            var property = serialized.FindProperty(propertyName);
            Assert.That(property, Is.Not.Null, propertyName);
            property.objectReferenceValue = sprite;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private SerializedProperty RequireLayoutProperty(string propertyPath)
        {
            serializedLayoutProfile.UpdateIfRequiredOrScript();
            var property = serializedLayoutProfile.FindProperty(propertyPath);
            Assert.That(property, Is.Not.Null, propertyPath);
            return property;
        }

        private float LayoutFloat(string propertyPath)
        {
            return RequireLayoutProperty("values." + propertyPath).floatValue;
        }

        private Vector2 LayoutVector(string propertyPath)
        {
            return RequireLayoutProperty("values." + propertyPath).vector2Value;
        }

        private int LayoutInt(string propertyPath)
        {
            return RequireLayoutProperty("values." + propertyPath).intValue;
        }

        private bool LayoutBool(string propertyPath)
        {
            return RequireLayoutProperty("values." + propertyPath).boolValue;
        }

        private void AssertTextStyle(Text text, string stylePropertyPath)
        {
            Assert.That(text, Is.Not.Null);
            Assert.That(text.fontSize, Is.EqualTo(LayoutInt(stylePropertyPath + ".FontSize")));
            Assert.That(text.resizeTextMinSize, Is.EqualTo(LayoutInt(stylePropertyPath + ".ResizeMinSize")));
            Assert.That(text.resizeTextMaxSize, Is.EqualTo(LayoutInt(stylePropertyPath + ".ResizeMaxSize")));
            Assert.That((int)text.fontStyle, Is.EqualTo(LayoutInt(stylePropertyPath + ".FontStyle")));
            Assert.That((int)text.alignment, Is.EqualTo(LayoutInt(stylePropertyPath + ".Alignment")));
            Assert.That((int)text.horizontalOverflow,
                Is.EqualTo(LayoutInt(stylePropertyPath + ".HorizontalOverflow")));
            Assert.That((int)text.verticalOverflow,
                Is.EqualTo(LayoutInt(stylePropertyPath + ".VerticalOverflow")));
            Assert.That(text.resizeTextForBestFit,
                Is.EqualTo(LayoutBool(stylePropertyPath + ".ResizeTextForBestFit")));
            Assert.That(text.raycastTarget,
                Is.EqualTo(LayoutBool(stylePropertyPath + ".RaycastTarget")));
        }

        private void AssertButtonStyle(
            Button button,
            Text label,
            string layoutPropertyPath,
            string stylePropertyPath)
        {
            Assert.That(button, Is.Not.Null);
            Assert.That(label, Is.Not.Null);
            AssertRectLayout(button.GetComponent<RectTransform>(), layoutPropertyPath);
            AssertTextStyle(label, stylePropertyPath + ".LabelStyle");
            var outline = button.GetComponent<Outline>();
            Assert.That(outline, Is.Not.Null);
            AssertVectorApproximately(
                outline.effectDistance,
                LayoutVector(stylePropertyPath + ".OutlineDistance"));
            AssertColorApproximately(
                button.GetComponent<Image>().color,
                ThemeColor("ButtonBackground"));
            AssertColorApproximately(outline.effectColor, ThemeColor("GoldOutlineThin"));
            AssertVectorApproximately(label.rectTransform.anchorMin, Vector2.zero);
            AssertVectorApproximately(label.rectTransform.anchorMax, Vector2.one);
            AssertVectorApproximately(
                label.rectTransform.offsetMin,
                LayoutVector(stylePropertyPath + ".LabelOffsetMin"));
            AssertVectorApproximately(
                label.rectTransform.offsetMax,
                LayoutVector(stylePropertyPath + ".LabelOffsetMax"));
            var expectsLabelOutline = LayoutBool(stylePropertyPath + ".LabelHasOutline");
            var labelOutline = label.GetComponent<Outline>();
            if (expectsLabelOutline)
            {
                Assert.That(labelOutline, Is.Not.Null);
                Assert.That(labelOutline.enabled, Is.True);
                AssertVectorApproximately(
                    labelOutline.effectDistance,
                    LayoutVector(stylePropertyPath + ".LabelOutlineDistance"));
                AssertColorApproximately(
                    labelOutline.effectColor,
                    ThemeColor("DarkShadowLight"));
            }
            else if (labelOutline != null)
            {
                Assert.That(labelOutline.enabled, Is.False);
            }
        }

        private void AssertRectSizeAndPosition(
            RectTransform rect,
            Vector2 expectedSize,
            Vector2 expectedPosition)
        {
            Assert.That(rect, Is.Not.Null);
            AssertVectorApproximately(rect.sizeDelta, expectedSize);
            AssertVectorApproximately(rect.anchoredPosition, expectedPosition);
        }

        private void AssertRectLayout(RectTransform rect, string layoutPropertyPath)
        {
            Assert.That(rect, Is.Not.Null);
            AssertVectorApproximately(
                rect.anchorMin,
                LayoutVector(layoutPropertyPath + ".AnchorMin"));
            AssertVectorApproximately(
                rect.anchorMax,
                LayoutVector(layoutPropertyPath + ".AnchorMax"));
            AssertVectorApproximately(
                rect.pivot,
                LayoutVector(layoutPropertyPath + ".Pivot"));
            AssertRectSizeAndPosition(
                rect,
                LayoutVector(layoutPropertyPath + ".SizeDelta"),
                LayoutVector(layoutPropertyPath + ".AnchoredPosition"));
        }

        private static void AssertVectorApproximately(Vector2 actual, Vector2 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.001f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.001f));
        }

        private static void AssertColorApproximately(Color actual, Color expected)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.001f));
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.001f));
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.001f));
            Assert.That(actual.a, Is.EqualTo(expected.a).Within(0.001f));
        }

        private Component FindAnyEventChoiceView()
        {
            var roots = EditorSceneManager.GetActiveScene().GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                var match = roots[i].GetComponentInChildren(viewType, true);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static EventCardDefinition CreateEventCard()
        {
            return new EventCardDefinition
            {
                CardId = "event-test",
                Name = "事件测试（4）",
                Description = "测试事件说明",
                ChoiceDescriptions = new List<string> { "选择一", "选择二" },
                ChoiceRewards = new List<ResourceSet> { new ResourceSet(), new ResourceSet() }
            };
        }

        private static List<ExplorePaymentChoice> CreatePayments()
        {
            return new List<ExplorePaymentChoice>
            {
                new ExplorePaymentChoice("R1", new List<int> { 2, 3 })
            };
        }

        private static BuildFacilityDraftViewModel CreateBuildModel(
            BuildFacilityDraftPhase phase,
            Action<BuildFacilityIntent> dispatch)
        {
            return new BuildFacilityDraftViewModel(
                phase,
                null,
                null,
                new FacilityCardDefinition
                {
                    FacilityId = "facility-test",
                    Name = "测试设施",
                    ResourceCost = new ResourceSet { Originium = 2 },
                    GoldVoucherCost = 3,
                    Score = 4,
                    EffectType = "测试效果"
                },
                1,
                BuildFacilityService.PaymentModeResources,
                string.Empty,
                new List<int> { 1 },
                dispatch);
        }

        private static void AssertIntent<T>(IReadOnlyList<BuildFacilityIntent> intents)
            where T : BuildFacilityIntent
        {
            Assert.That(intents, Has.Count.EqualTo(1));
            Assert.That(intents[0], Is.TypeOf<T>());
        }

        private static GameObject FindChild(GameObject root, string objectName)
        {
            if (root == null)
            {
                return null;
            }

            var transforms = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null && transforms[i].name == objectName)
                {
                    return transforms[i].gameObject;
                }
            }

            return null;
        }

        private static Type GetRuntimeType(string typeName)
        {
            var type = Type.GetType(typeName + ", Assembly-CSharp", false);
            Assert.That(type, Is.Not.Null, typeName);
            return type;
        }

        private static Color ThemeColor(string propertyName)
        {
            var property = GetRuntimeType("YC.Presentation.UiTheme").GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Static);
            Assert.That(property, Is.Not.Null, "UiTheme." + propertyName);
            return (Color)property.GetValue(null, null);
        }

        private static Type GetEditorType(string typeName)
        {
            var type = Type.GetType(typeName + ", Assembly-CSharp-Editor", false);
            Assert.That(type, Is.Not.Null, typeName);
            return type;
        }

        private static T GetObjectReference<T>(Component target, string propertyName) where T : Object
        {
            var property = new SerializedObject(target).FindProperty(propertyName);
            Assert.That(property, Is.Not.Null, target.GetType().Name + "." + propertyName);
            var value = property.objectReferenceValue as T;
            Assert.That(value, Is.Not.Null, target.GetType().Name + "." + propertyName);
            return value;
        }
    }
}
