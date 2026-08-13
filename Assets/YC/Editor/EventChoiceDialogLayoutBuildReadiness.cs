using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation;

namespace YC.Editor
{
    public static class EventChoiceDialogLayoutBuildReadiness
    {
        internal const string EventChoiceDialogSourceSha256 =
            "3814CBEE3719C2897E39A1B382876296C3E523ED766BC36DF9C7718B229B8125";
        internal const string EventChoiceDialogViewSourceSha256 =
            "BFBF8A3489F8B71A21F5B808CFAF8AD7042935C0EE1859CE10C0743B18847971";

        public const string EventChoiceDialogPrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/EventChoiceDialog.prefab";
        public const string EventChoiceDialogPrefabGuid =
            "3fe2d5d7571d9e4488d62bd519017c21";
        internal const string SampleScenePath = "Assets/Scenes/SampleScene.unity";
        internal const string SampleSceneGuid = "2cda990e2423bbf4892e6590ba056729";
        internal const string GameplayHudPrefabGuid = "97d4d860d450f7247969c8aca5a9d934";
        internal const string GameplayHudSceneInstanceLocalId = "952091510";
        internal const string GameplayHudRegistryLocalId = "7007614311448219973";
        internal const string GameplayHudViewLocalId = "65381164276375644";
        internal const string GameplayHudRegistryGameObjectLocalId = "6753386143783961735";
        internal const string GameplayHudRootGameObjectLocalId = "5963824492678046178";
        internal const string EventChoiceDialogViewLocalId = "2364430295288198237";

        public static void ValidateReadyForBuild()
        {
            var profile = EventChoiceDialogLayoutEditorAssetBuilder.LoadRequiredProfile();
            AssertControlledMetaGuid(EventChoiceDialogPrefabPath, EventChoiceDialogPrefabGuid);
            AssertControlledMetaGuid(
                YC.EditorTools.GameplayDialogEditorAssetBuilder.SharedUiVisualsAssetPath,
                YC.EditorTools.GameplayDialogEditorAssetBuilder.SharedUiVisualsAssetGuid);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(EventChoiceDialogPrefabPath);
            if (prefab == null ||
                !string.Equals(
                    AssetDatabase.AssetPathToGUID(EventChoiceDialogPrefabPath),
                    EventChoiceDialogPrefabGuid,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog Prefab 缺失或 GUID 发生变化。");
            }

            ValidatePrefabContents(prefab, profile);
            ValidateProductionDependencies(prefab, profile);
            AssertControlledMetaGuid(SampleScenePath, SampleSceneGuid);
            ValidateSampleSceneYaml(File.ReadAllText(Path.GetFullPath(SampleScenePath)));
            ValidateSourceSemantics();
        }

        internal static void ValidatePrefabContents(
            GameObject root,
            EventChoiceDialogLayoutProfile profile)
        {
            if (root == null || profile == null)
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog Prefab 或布局 Profile 为空。");
            }

            var views = root.GetComponentsInChildren<EventChoiceDialogView>(true);
            if (views.Length != 1 || views[0].gameObject != root)
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog Prefab 必须在根节点包含唯一 View。");
            }

            var view = views[0];
            var sharedVisuals =
                YC.EditorTools.GameplayDialogEditorAssetBuilder.LoadRequiredSharedUiVisuals();
            if (!view.TryValidateConfiguration(out var reason))
            {
                throw new InvalidOperationException(
                    "EventChoiceDialogView 配置不完整：" + reason);
            }

            var serialized = new SerializedObject(view);
            var layoutProperty = serialized.FindProperty("layoutProfile");
            if (layoutProperty == null || layoutProperty.objectReferenceValue != profile ||
                view.LayoutProfile != profile)
            {
                throw new InvalidOperationException(
                    "EventChoiceDialogView 必须显式引用锁定布局 Profile。");
            }

            ValidateViewTopology(root, view, serialized, sharedVisuals);
            ValidateFixedPresentation(view, profile);
            ValidateModePresentations(root, profile);
        }

        private static void ValidateViewTopology(
            GameObject root,
            EventChoiceDialogView view,
            SerializedObject serialized,
            UiVisualAssetLibrary sharedVisuals)
        {
            var rootRect = root.GetComponent<RectTransform>();
            var overlayCanvas = GetReference<Canvas>(serialized, "overlayCanvas");
            var overlayRect = GetReference<RectTransform>(serialized, "overlayRect");
            var overlayImage = GetReference<Image>(serialized, "overlayImage");
            var closeInputHandler = GetReference<WindowCloseInputHandler>(serialized, "closeInputHandler");
            var raycaster = root.GetComponent<GraphicRaycaster>();
            if (overlayRect != rootRect || overlayCanvas.gameObject != root ||
                overlayImage.gameObject != root || closeInputHandler.gameObject != root)
            {
                throw new InvalidOperationException("EventChoiceDialog 根壳字段必须绑定根对象自身组件。");
            }

            AssertSingleComponent(root, overlayCanvas, "overlayCanvas");
            AssertSingleComponent(root, rootRect, "overlayRect");
            AssertSingleComponent(root, overlayImage, "overlayImage");
            AssertSingleComponent(root, closeInputHandler, "closeInputHandler");
            AssertSingleComponent(root, view, "EventChoiceDialogView");
            AssertActiveSelf(root, true, "Event Choice Overlay");
            AssertEnabled(overlayCanvas, "Overlay Canvas");
            AssertEnabled(raycaster, "Overlay GraphicRaycaster");
            AssertEnabled(overlayImage, "Overlay Image");
            AssertEnabled(closeInputHandler, "Overlay CloseInputHandler");
            AssertEnabled(view, "EventChoiceDialogView");
            if (overlayCanvas.renderMode != RenderMode.ScreenSpaceOverlay ||
                overlayCanvas.targetDisplay != 0)
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog 根 Canvas 必须固定为 ScreenSpaceOverlay/targetDisplay 0。");
            }
            AssertExactScreenSpaceOverlayRootComponentTypes(
                root,
                typeof(RectTransform),
                typeof(Canvas),
                typeof(GraphicRaycaster),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(WindowCloseInputHandler),
                typeof(EventChoiceDialogView));

            var panel = GetReference<RectTransform>(serialized, "panel");
            AssertDirectChild(panel, root.transform, "Event Choice Panel");
            AssertSingleComponent(panel.gameObject, panel.gameObject.GetComponent<Image>(), "Panel Image");
            AssertSingleComponent(panel.gameObject, panel.gameObject.GetComponent<Outline>(), "Panel Outline");
            var collapsiblePanel = GetReference<EffectDialogCollapsiblePanel>(serialized, "collapsiblePanel");
            if (collapsiblePanel.gameObject != panel.gameObject)
            {
                throw new InvalidOperationException("CollapsiblePanel 必须与 Event Choice Panel 同对象。");
            }

            AssertSingleComponent(panel.gameObject, collapsiblePanel, "CollapsiblePanel");
            AssertActiveSelf(panel.gameObject, true, "Event Choice Panel");
            AssertEnabled(panel.gameObject.GetComponent<Image>(), "Panel Image");
            AssertEnabled(panel.gameObject.GetComponent<Outline>(), "Panel Outline");
            AssertEnabled(collapsiblePanel, "CollapsiblePanel");
            AssertExactComponentTypes(
                panel.gameObject,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Outline),
                typeof(EffectDialogCollapsiblePanel));

            var expanded = GetReference<RectTransform>(serialized, "expandedContent");
            var title = GetReference<Text>(serialized, "titleText");
            var metadata = GetReference<Text>(serialized, "metadataText");
            var description = GetReference<Text>(serialized, "descriptionText");
            var collapsedSummary = GetReference<Text>(serialized, "collapsedSummaryText");
            var actionArea = GetReference<RectTransform>(serialized, "actionArea");
            AssertDirectChild(expanded, panel, "Expanded Content");
            AssertStretched(expanded, "Expanded Content");
            AssertDirectChild(title, expanded, "Title");
            AssertDirectChild(metadata, expanded, "Metadata");
            AssertDirectChild(description, expanded, "Description");
            AssertDirectChild(actionArea, expanded, "Action Area");
            AssertStretched(actionArea, "Action Area");
            AssertDirectChild(collapsedSummary, panel, "Collapsed Summary");
            AssertActiveSelf(expanded.gameObject, true, "Expanded Content");
            AssertActiveSelf(title.gameObject, true, "Title");
            AssertActiveSelf(metadata.gameObject, true, "Metadata");
            AssertActiveSelf(description.gameObject, true, "Description");
            AssertActiveSelf(actionArea.gameObject, true, "Action Area");
            AssertExactComponentTypes(expanded.gameObject, typeof(RectTransform));
            AssertExactComponentTypes(actionArea.gameObject, typeof(RectTransform));
            AssertExactDirectChildren(
                expanded,
                title.transform,
                metadata.transform,
                description.transform,
                actionArea);

            var dragHandle = GetReference<EffectDialogDragHandle>(serialized, "dragHandle");
            if (dragHandle.gameObject != title.gameObject)
            {
                throw new InvalidOperationException("拖拽句柄必须与共享 Title 文本同对象。");
            }

            AssertSingleComponent(title.gameObject, dragHandle, "Title DragHandle");
            AssertEnabled(dragHandle, "Title DragHandle");

            var closeButton = GetReference<Button>(serialized, "closeButton");
            var closeLabel = GetReference<Text>(serialized, "closeButtonLabel");
            var collapseButton = GetReference<Button>(serialized, "collapseButton");
            var collapseLabel = GetReference<Text>(serialized, "collapseButtonLabel");
            var collapseIcon = GetReference<Image>(serialized, "collapseButtonIcon");
            AssertDirectChild(closeButton, panel, "Close");
            AssertDirectChild(closeLabel, closeButton.transform, "Label");
            AssertDirectChild(collapseButton, panel, "Collapse");
            AssertDirectChild(collapseLabel, collapseButton.transform, "Label");
            AssertDirectChild(collapseIcon, collapseButton.transform, "Collapse Triangle");
            AssertExactDirectChildren(closeButton.transform, closeLabel.transform);
            AssertExactDirectChildren(
                collapseButton.transform,
                collapseLabel.transform,
                collapseIcon.transform);
            AssertExactComponentTypes(
                collapseIcon.gameObject,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            AssertActiveSelf(closeLabel.gameObject, true, "Close Label");
            AssertActiveSelf(collapseLabel.gameObject, true, "Collapse Label");
            AssertActiveSelf(collapseIcon.gameObject, true, "Collapse Triangle");
            AssertEnabled(collapseIcon, "Collapse Triangle Image");
            ValidateCollapseVisualContract(
                collapsiblePanel,
                collapseIcon,
                sharedVisuals);
            if (closeButton.gameObject.activeSelf || collapseButton.gameObject.activeSelf ||
                collapsedSummary.gameObject.activeSelf)
            {
                throw new InvalidOperationException("Close、Collapse 与折叠摘要必须在 Prefab 初始为 inactive。");
            }

            var modeFields = new[]
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
            var modeNames = new[]
            {
                "EventCard Mode",
                "ExplorePath Mode",
                "ExplorePayment Mode",
                "ResourceCollectionPayment Mode",
                "BuildFacilityFocus Mode",
                "BuildFacilityConfirmation Mode",
                "LegacyCityStyleOptions Mode",
                "CharacterSecondEffectDecision Mode"
            };
            if (Enum.GetValues(typeof(EventChoiceDialogMode)).Length != modeFields.Length)
            {
                throw new InvalidOperationException("EventChoiceDialogMode 枚举与序列化模式字段数量不一致。");
            }

            var modes = new GameObject[modeFields.Length];
            for (var i = 0; i < modes.Length; i++)
            {
                modes[i] = GetReference<GameObject>(serialized, modeFields[i]);
                AssertDirectChild(modes[i].transform, actionArea, modeNames[i]);
                AssertStretched(modes[i].GetComponent<RectTransform>(), modeNames[i]);
                AssertExactComponentTypes(modes[i], typeof(RectTransform));
                AssertUniqueDescendantName(root, modeNames[i], modes[i].transform);
                if (modes[i].activeSelf)
                {
                    throw new InvalidOperationException("模式块初始必须 inactive：" + modeNames[i]);
                }
            }

            AssertDistinct(modes, "8 个模式块");

            var eventChoiceHost = GetReference<RectTransform>(serialized, "eventChoiceHost");
            var eventPaymentHost = GetReference<RectTransform>(serialized, "eventPaymentRouteHost");
            var explorePathHost = GetReference<RectTransform>(serialized, "explorePathHost");
            var explorePaymentHost = GetReference<RectTransform>(serialized, "explorePaymentRouteHost");
            var resourceRecipientHost = GetReference<RectTransform>(serialized, "resourcePaymentRecipientHost");
            var legacyHost = GetReference<RectTransform>(serialized, "legacyCityStyleHost");
            var hosts = new[]
            {
                eventChoiceHost,
                eventPaymentHost,
                explorePathHost,
                explorePaymentHost,
                resourceRecipientHost,
                legacyHost
            };
            var hostParents = new[]
            {
                modes[0].transform,
                modes[0].transform,
                modes[1].transform,
                modes[2].transform,
                modes[3].transform,
                modes[6].transform
            };
            var hostNames = new[]
            {
                "Event Choice Host",
                "Event Payment Route Host",
                "Explore Path Host",
                "Explore Payment Route Host",
                "Resource Collection Recipient Host",
                "Legacy City Style Host"
            };
            for (var i = 0; i < hosts.Length; i++)
            {
                AssertDirectChild(hosts[i], hostParents[i], hostNames[i]);
                AssertStretched(hosts[i], hostNames[i]);
                AssertExactComponentTypes(hosts[i].gameObject, typeof(RectTransform));
                AssertExactDirectChildren(hosts[i]);
                AssertUniqueDescendantName(root, hostNames[i], hosts[i]);
                AssertActiveSelf(hosts[i].gameObject, true, hostNames[i]);
            }

            AssertDistinct(hosts, "6 个动态 Host");
            ValidateModeContentTopology(root, serialized, modes);
            var templateHost = ValidateTemplateTopology(root, serialized, panel);
            AssertExactDirectChildren(
                panel,
                expanded,
                closeButton.transform,
                collapseButton.transform,
                collapsedSummary.transform,
                templateHost);
            AssertExactDirectChildren(root.transform, panel);
            AssertExactDirectChildren(actionArea, Array.ConvertAll(modes, mode => mode.transform));

            AssertUniqueDescendantName(root, "Event Choice Panel", panel);
            AssertUniqueDescendantName(root, "Expanded Content", expanded);
            AssertUniqueDescendantName(root, "Title", title.transform);
            AssertUniqueDescendantName(root, "Metadata", metadata.transform);
            AssertUniqueDescendantName(root, "Description", description.transform);
            AssertUniqueDescendantName(root, "Collapsed Summary", collapsedSummary.transform);
            AssertUniqueDescendantName(root, "Action Area", actionArea);
        }

        private static void ValidateCollapseVisualContract(
            EffectDialogCollapsiblePanel collapsiblePanel,
            Image collapseIcon,
            UiVisualAssetLibrary sharedVisuals)
        {
            if (collapsiblePanel == null || collapseIcon == null || sharedVisuals == null)
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog 折叠视觉配置不完整。");
            }

            if (!collapsiblePanel.TryValidateVisualConfiguration(out var reason))
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog 折叠视觉配置不完整：" + reason);
            }

            var collapsibleData = new SerializedObject(collapsiblePanel);
            var triangleUp = GetReference<Sprite>(collapsibleData, "triangleUpSprite");
            var triangleDown = GetReference<Sprite>(collapsibleData, "triangleDownSprite");
            if (triangleUp != sharedVisuals.TriangleUp ||
                triangleDown != sharedVisuals.TriangleDown ||
                triangleUp == triangleDown ||
                collapseIcon.sprite != sharedVisuals.TriangleUp ||
                !collapseIcon.preserveAspect)
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog 折叠图标必须精确引用 SharedUiVisuals Up/Down，" +
                    "且初始使用 Up 并保持宽高比。");
            }
        }

        private static void ValidateModeContentTopology(
            GameObject root,
            SerializedObject serialized,
            GameObject[] modes)
        {
            var eventPaymentHost = GetReference<RectTransform>(serialized, "eventPaymentRouteHost");
            var eventChoiceHost = GetReference<RectTransform>(serialized, "eventChoiceHost");
            var explorePathHost = GetReference<RectTransform>(serialized, "explorePathHost");
            var explorePaymentHost = GetReference<RectTransform>(serialized, "explorePaymentRouteHost");
            var exploreConfirm = GetReference<Button>(serialized, "exploreConfirmButton");
            var exploreConfirmLabel = GetReference<Text>(serialized, "exploreConfirmLabel");
            AssertDirectChild(exploreConfirm, modes[2].transform, "Confirm Explore");
            AssertDirectChild(exploreConfirmLabel, exploreConfirm.transform, "Label");
            AssertActiveSelf(exploreConfirm.gameObject, true, "Confirm Explore");

            var receiver = GetReference<Text>(serialized, "resourcePaymentReceiverText");
            var payBank = GetReference<Button>(serialized, "resourcePaymentBankButton");
            var payBankLabel = GetReference<Text>(serialized, "resourcePaymentBankLabel");
            var resourceRecipientHost = GetReference<RectTransform>(
                serialized,
                "resourcePaymentRecipientHost");
            AssertDirectChild(receiver, modes[3].transform, "Receiver");
            AssertDirectChild(payBank, modes[3].transform, "Pay Bank");
            AssertDirectChild(payBankLabel, payBank.transform, "Label");
            if (payBank.gameObject.activeSelf)
            {
                throw new InvalidOperationException("Pay Bank 必须在 Prefab 初始为 inactive。");
            }
            AssertActiveSelf(receiver.gameObject, true, "Receiver");
            AssertActiveSelf(resourceRecipientHost.gameObject, true, "Resource Collection Recipient Host");

            var previewImage = GetReference<RawImage>(serialized, "facilityPreviewImage");
            var previewAspect = GetReference<AspectRatioFitter>(serialized, "facilityPreviewAspect");
            var previewFallback = GetReference<Text>(serialized, "facilityPreviewFallback");
            var previewContainer = previewImage.transform.parent as RectTransform;
            AssertDirectChild(previewContainer, modes[4].transform, "Facility Card Preview");
            AssertDirectChild(previewImage, previewContainer, "Facility Card Image");
            AssertDirectChild(previewFallback, previewContainer, "Facility Preview Fallback");
            if (previewAspect.gameObject != previewImage.gameObject)
            {
                throw new InvalidOperationException("Facility Preview Aspect 必须与 RawImage 同对象。");
            }

            AssertExactComponentTypes(
                previewContainer.gameObject,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            AssertExactComponentTypes(
                previewImage.gameObject,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(RawImage),
                typeof(AspectRatioFitter));
            AssertExactDirectChildren(previewImage.transform);
            AssertExactTextComponents(previewFallback, false, false);
            AssertExactDirectChildren(previewFallback.transform);
            AssertExactDirectChildren(
                previewContainer,
                previewImage.transform,
                previewFallback.transform);
            AssertActiveSelf(previewContainer.gameObject, true, "Facility Card Preview");
            if (previewImage.gameObject.activeSelf || previewFallback.gameObject.activeSelf)
            {
                throw new InvalidOperationException(
                    "Facility Card Image/Fallback 必须在 Prefab 初始为 inactive。");
            }

            AssertEnabled(previewContainer.gameObject.GetComponent<Image>(), "Facility Preview Image");
            AssertEnabled(previewImage, "Facility Card RawImage");
            AssertEnabled(previewAspect, "Facility Preview AspectRatioFitter");

            var buildDetails = AssertDirectField<Text>(
                serialized,
                "buildFocusDetailsText",
                modes[4].transform,
                "Build Details");
            var resourceButton = AssertButtonAndLabel(
                serialized,
                "buildResourceButton",
                "buildResourceLabel",
                modes[4].transform,
                "Choose Resource Payment");
            var resourceReason = AssertDirectField<Text>(
                serialized,
                "buildResourceReasonText",
                modes[4].transform,
                "Resource Payment Reason");
            var goldButton = AssertButtonAndLabel(
                serialized,
                "buildGoldButton",
                "buildGoldLabel",
                modes[4].transform,
                "Choose Gold Payment");
            var goldReason = AssertDirectField<Text>(
                serialized,
                "buildGoldReasonText",
                modes[4].transform,
                "Gold Payment Reason");
            var focusError = AssertDirectField<Text>(
                serialized,
                "buildFocusErrorText",
                modes[4].transform,
                "Build Error");

            var confirmationSummary = AssertDirectField<Text>(
                serialized,
                "buildConfirmationSummaryText",
                modes[5].transform,
                "Summary");
            var confirmationError = AssertDirectField<Text>(
                serialized,
                "buildConfirmationErrorText",
                modes[5].transform,
                "Build Error");
            var backButton = AssertButtonAndLabel(
                serialized,
                "buildBackButton",
                "buildBackLabel",
                modes[5].transform,
                "Back To Build Payment");
            var confirmButton = AssertButtonAndLabel(
                serialized,
                "buildConfirmButton",
                "buildConfirmLabel",
                modes[5].transform,
                "Confirm Build Facility");

            var legacyEmpty = AssertDirectField<Text>(
                serialized,
                "legacyCityStyleEmptyText",
                modes[6].transform,
                "Empty");
            if (legacyEmpty.gameObject.activeSelf)
            {
                throw new InvalidOperationException("Legacy City Style Empty 必须初始 inactive。");
            }

            var legacyHost = GetReference<RectTransform>(serialized, "legacyCityStyleHost");
            var continueButton = AssertButtonAndLabel(
                serialized,
                "characterContinueButton",
                "characterContinueLabel",
                modes[7].transform,
                "Continue Character Second Effect");
            var finishButton = AssertButtonAndLabel(
                serialized,
                "characterFinishButton",
                "characterFinishLabel",
                modes[7].transform,
                "Finish Character Use");

            var activeModeContent = new[]
            {
                buildDetails.gameObject,
                resourceButton.gameObject,
                resourceReason.gameObject,
                goldButton.gameObject,
                goldReason.gameObject,
                focusError.gameObject,
                confirmationSummary.gameObject,
                confirmationError.gameObject,
                backButton.gameObject,
                confirmButton.gameObject,
                legacyHost.gameObject,
                continueButton.gameObject,
                finishButton.gameObject
            };
            for (var i = 0; i < activeModeContent.Length; i++)
            {
                AssertActiveSelf(
                    activeModeContent[i],
                    true,
                    "模式常驻内容 " + activeModeContent[i].name);
            }

            AssertUniqueDescendantName(root, "Confirm Explore", exploreConfirm.transform);
            AssertUniqueDescendantName(root, "Receiver", receiver.transform);
            AssertUniqueDescendantName(root, "Pay Bank", payBank.transform);
            AssertUniqueDescendantName(root, "Facility Card Preview", previewContainer);

            AssertExactDirectChildren(
                modes[0].transform,
                eventPaymentHost,
                eventChoiceHost);
            AssertExactDirectChildren(modes[1].transform, explorePathHost);
            AssertExactDirectChildren(
                modes[2].transform,
                explorePaymentHost,
                exploreConfirm.transform);
            AssertExactDirectChildren(
                modes[3].transform,
                receiver.transform,
                resourceRecipientHost,
                payBank.transform);
            AssertExactDirectChildren(
                modes[4].transform,
                previewContainer,
                buildDetails.transform,
                resourceButton.transform,
                goldButton.transform,
                resourceReason.transform,
                goldReason.transform,
                focusError.transform);
            AssertExactDirectChildren(
                modes[5].transform,
                confirmationSummary.transform,
                confirmationError.transform,
                backButton.transform,
                confirmButton.transform);
            AssertExactDirectChildren(modes[6].transform, legacyHost, legacyEmpty.transform);
            AssertExactDirectChildren(
                modes[7].transform,
                continueButton.transform,
                finishButton.transform);
        }

        private static RectTransform ValidateTemplateTopology(
            GameObject root,
            SerializedObject serialized,
            RectTransform panel)
        {
            var templateHost = FindDirectRequired(panel, "Dynamic Templates");
            AssertStretched(templateHost, "Dynamic Templates");
            AssertExactComponentTypes(templateHost.gameObject, typeof(RectTransform));
            AssertUniqueDescendantName(root, "Dynamic Templates", templateHost);
            AssertActiveSelf(templateHost.gameObject, true, "Dynamic Templates");

            var choiceRoot = AssertButtonRowTopology(serialized, "choiceRowTemplate", templateHost, "ChoiceRow");
            var pathRoot = AssertButtonRowTopology(serialized, "pathRowTemplate", templateHost, "PathRow");
            var recipientRoot = AssertButtonRowTopology(
                serialized,
                "paymentRecipientButtonTemplate",
                templateHost,
                "PaymentRecipientButton");
            var resourceRoot = AssertButtonRowTopology(
                serialized,
                "resourceCollectionRecipientButtonTemplate",
                templateHost,
                "ResourceCollectionRecipientButton");

            var paymentRoot = GetNestedReference<RectTransform>(
                serialized,
                "paymentRouteRowTemplate",
                "root");
            var paymentLabel = GetNestedReference<Text>(
                serialized,
                "paymentRouteRowTemplate",
                "label");
            var paymentRecipientHost = GetNestedReference<RectTransform>(
                serialized,
                "paymentRouteRowTemplate",
                "recipientHost");
            AssertDirectChild(paymentRoot, templateHost, "PaymentRouteRow");
            AssertDirectChild(paymentLabel, paymentRoot, "Route Label");
            AssertDirectChild(paymentRecipientHost, paymentRoot, "Recipient Host");
            AssertStretched(paymentRecipientHost, "Payment Recipient Host");
            AssertExactComponentTypes(paymentRoot.gameObject, typeof(RectTransform));
            AssertExactComponentTypes(paymentRecipientHost.gameObject, typeof(RectTransform));
            AssertExactDirectChildren(paymentRecipientHost);
            AssertExactDirectChildren(
                paymentRoot,
                paymentLabel.transform,
                paymentRecipientHost);
            AssertActiveSelf(paymentLabel.gameObject, true, "Payment Route Label");
            AssertActiveSelf(paymentRecipientHost.gameObject, true, "Payment Recipient Host");

            var legacyRoot = GetNestedReference<RectTransform>(
                serialized,
                "legacyCityStyleRowTemplate",
                "root");
            var legacySummary = GetNestedReference<Text>(
                serialized,
                "legacyCityStyleRowTemplate",
                "summary");
            var legacyReason = GetNestedReference<Text>(
                serialized,
                "legacyCityStyleRowTemplate",
                "reason");
            var legacyDeclare = GetNestedReference<Button>(
                serialized,
                "legacyCityStyleRowTemplate",
                "declareButton");
            var legacyDeclareLabel = GetNestedReference<Text>(
                serialized,
                "legacyCityStyleRowTemplate",
                "declareLabel");
            AssertDirectChild(legacyRoot, templateHost, "LegacyCityStyleRow");
            AssertDirectChild(legacySummary, legacyRoot, "Summary");
            AssertDirectChild(legacyReason, legacyRoot, "Reason");
            AssertDirectChild(legacyDeclare, legacyRoot, "Declare");
            AssertDirectChild(legacyDeclareLabel, legacyDeclare.transform, "Declare Label");
            AssertExactComponentTypes(legacyRoot.gameObject, typeof(RectTransform));
            AssertExactDirectChildren(
                legacyRoot,
                legacySummary.transform,
                legacyReason.transform,
                legacyDeclare.transform);
            AssertExactDirectChildren(legacyDeclare.transform, legacyDeclareLabel.transform);
            AssertActiveSelf(legacySummary.gameObject, true, "Legacy Summary");
            AssertActiveSelf(legacyReason.gameObject, true, "Legacy Reason");
            AssertActiveSelf(legacyDeclare.gameObject, true, "Legacy Declare");
            AssertActiveSelf(legacyDeclareLabel.gameObject, true, "Legacy Declare Label");

            var roots = new[]
            {
                choiceRoot,
                pathRoot,
                paymentRoot,
                recipientRoot,
                resourceRoot,
                legacyRoot
            };
            AssertDistinct(roots, "6 个动态模板根");
            for (var i = 0; i < roots.Length; i++)
            {
                if (roots[i].gameObject.activeSelf)
                {
                    throw new InvalidOperationException("动态模板根必须初始 inactive：" + roots[i].name);
                }

                AssertUniqueDescendantName(root, roots[i].name, roots[i]);
            }

            AssertExactDirectChildren(templateHost, roots);
            return templateHost;
        }

        private static void ValidateFixedPresentation(
            EventChoiceDialogView view,
            EventChoiceDialogLayoutProfile profile)
        {
            var overlayRect = view.OverlayRect;
            AssertScreenSpaceOverlayRootRect(overlayRect);
            if (view.OverlayCanvas.sortingOrder != 118 ||
                !Approximately(view.OverlayImage.color, new Color(0f, 0f, 0f, profile.OverlayAlpha)) ||
                view.OverlayImage.raycastTarget != profile.EventOverlayRaycastTarget)
            {
                throw new InvalidOperationException("EventChoiceDialog Overlay 固定样式漂移。");
            }

            AssertLayout(view.Panel, profile.PanelLayout);
            var panelImage = view.Panel.GetComponent<Image>();
            var panelOutline = view.Panel.GetComponent<Outline>();
            if (panelImage == null || panelOutline == null ||
                !Approximately(panelImage.color, UiTheme.PanelBackground) ||
                !Approximately(panelOutline.effectColor, UiTheme.GoldOutline) ||
                !Approximately(panelOutline.effectDistance, profile.PanelOutlineDistance))
            {
                throw new InvalidOperationException("Event Choice Panel 固定 Image/Outline 样式漂移。");
            }

            AssertText(
                view.TitleText,
                profile.EventTitleLayout,
                profile.EventTitleTextStyle,
                UiTheme.GoldText,
                false,
                true);
            AssertText(
                view.MetadataText,
                profile.EventMetadataLayout,
                profile.EventMetadataTextStyle,
                UiTheme.LabelText);
            AssertText(
                view.DescriptionText,
                profile.EventDescriptionLayout,
                profile.EventDescriptionTextStyle,
                UiTheme.ValueText);
            AssertText(
                view.CollapsedSummaryText,
                profile.CollapsedSummaryLayout,
                profile.CollapsedSummaryTextStyle,
                UiTheme.GoldText);
            AssertButton(
                view.CollapseButton,
                view.CollapseButtonLabel,
                profile.CollapseButtonLayout,
                profile.CollapseButtonStyle,
                view.CollapseButtonIcon.transform);
            AssertCloseButton(
                view.CloseButton,
                view.CloseButtonLabel,
                profile.BuildCloseButtonLayout,
                profile.BuildCloseButtonStyle);
            AssertLayout(view.CollapseButtonIcon.rectTransform, profile.CollapseIconLayout);
            if (!Approximately(view.CollapseButtonIcon.color, UiTheme.GoldText) ||
                view.CollapseButtonIcon.raycastTarget)
            {
                throw new InvalidOperationException("Collapse icon 固定样式漂移。");
            }

            AssertButton(
                view.ExploreConfirmButton,
                view.ExploreConfirmLabel,
                profile.ExploreConfirmTemplateLayout,
                profile.ExploreConfirmButtonStyle);
            AssertText(
                view.ResourcePaymentReceiverText,
                profile.ResourceReceiverLayout,
                profile.ResourceReceiverTextStyle,
                UiTheme.ValueText);
            AssertButton(
                view.ResourcePaymentBankButton,
                view.ResourcePaymentBankLabel,
                profile.ResourceBankButtonLayout,
                profile.ResourceBankButtonStyle);

            var previewContainer = view.FacilityPreviewImage.transform.parent as RectTransform;
            AssertLayout(previewContainer, profile.FacilityPreviewContainerLayout);
            var previewBackground = previewContainer.GetComponent<Image>();
            if (previewBackground == null ||
                !Approximately(previewBackground.color, new Color(0.03f, 0.025f, 0.02f, 0.96f)) ||
                view.FacilityPreviewImage.raycastTarget ||
                view.FacilityPreviewAspect.aspectMode != AspectRatioFitter.AspectMode.FitInParent)
            {
                throw new InvalidOperationException("Facility preview 固定壳样式漂移。");
            }

            AssertStretched(view.FacilityPreviewImage.rectTransform, "Facility Card Image");
            AssertStretched(view.FacilityPreviewFallback.rectTransform, "Facility Preview Fallback");
            AssertText(
                view.BuildFocusDetailsText,
                profile.BuildFocusDetailsLayout,
                profile.BuildFocusDetailsTextStyle,
                UiTheme.ValueText);
            AssertButton(
                view.BuildResourceButton,
                view.BuildResourceLabel,
                profile.BuildResourceButtonLayout,
                profile.BuildResourceButtonStyle);
            AssertText(
                view.BuildResourceReasonText,
                profile.BuildResourceReasonLayout,
                profile.BuildResourceReasonTextStyle,
                UiTheme.LabelText);
            AssertButton(
                view.BuildGoldButton,
                view.BuildGoldLabel,
                profile.BuildGoldButtonLayout,
                profile.BuildGoldButtonStyle);
            AssertText(
                view.BuildGoldReasonText,
                profile.BuildGoldReasonLayout,
                profile.BuildGoldReasonTextStyle,
                UiTheme.LabelText);
            AssertText(
                view.BuildFocusErrorText,
                profile.BuildFocusErrorLayout,
                profile.BuildFocusErrorTextStyle,
                new Color(1f, 0.45f, 0.32f));

            AssertText(
                view.BuildConfirmationSummaryText,
                profile.BuildConfirmationSummaryLayout,
                profile.BuildConfirmationSummaryTextStyle,
                UiTheme.ValueText);
            AssertText(
                view.BuildConfirmationErrorText,
                profile.BuildConfirmationErrorLayout,
                profile.BuildConfirmationErrorTextStyle,
                new Color(1f, 0.45f, 0.32f));
            AssertButton(
                view.BuildBackButton,
                view.BuildBackLabel,
                profile.BuildBackButtonLayout,
                profile.BuildBackButtonStyle);
            AssertButton(
                view.BuildConfirmButton,
                view.BuildConfirmLabel,
                profile.BuildConfirmButtonLayout,
                profile.BuildConfirmButtonStyle);

            AssertText(
                view.LegacyCityStyleEmptyText,
                profile.LegacyCityStyleEmptyLayout,
                profile.LegacyCityStyleEmptyTextStyle,
                UiTheme.ValueText);
            AssertButton(
                view.CharacterContinueButton,
                view.CharacterContinueLabel,
                profile.CharacterContinueButtonLayout,
                profile.CharacterContinueButtonStyle);
            AssertButton(
                view.CharacterFinishButton,
                view.CharacterFinishLabel,
                profile.CharacterFinishButtonLayout,
                profile.CharacterFinishButtonStyle);

            var serialized = new SerializedObject(view);
            AssertTemplatePresentation(serialized, profile);
        }

        private static void AssertTemplatePresentation(
            SerializedObject serialized,
            EventChoiceDialogLayoutProfile profile)
        {
            AssertButton(
                GetNestedReference<Button>(serialized, "choiceRowTemplate", "button"),
                GetNestedReference<Text>(serialized, "choiceRowTemplate", "label"),
                profile.ChoiceRowTemplateLayout,
                profile.ChoiceRowButtonStyle);
            AssertButton(
                GetNestedReference<Button>(serialized, "pathRowTemplate", "button"),
                GetNestedReference<Text>(serialized, "pathRowTemplate", "label"),
                profile.PathRowTemplateLayout,
                profile.PathRowButtonStyle);

            var routeRoot = GetNestedReference<RectTransform>(
                serialized,
                "paymentRouteRowTemplate",
                "root");
            var routeLabel = GetNestedReference<Text>(
                serialized,
                "paymentRouteRowTemplate",
                "label");
            AssertLayout(routeRoot, profile.PaymentRouteTemplateLayout);
            AssertText(
                routeLabel,
                profile.PaymentRouteLabelLayout,
                profile.PaymentRouteLabelTextStyle,
                UiTheme.ValueText);
            AssertStretched(
                GetNestedReference<RectTransform>(
                    serialized,
                    "paymentRouteRowTemplate",
                    "recipientHost"),
                "Payment Recipient Host");
            AssertButton(
                GetNestedReference<Button>(serialized, "paymentRecipientButtonTemplate", "button"),
                GetNestedReference<Text>(serialized, "paymentRecipientButtonTemplate", "label"),
                profile.PaymentRecipientTemplateLayout,
                profile.PaymentRecipientButtonStyle);
            AssertButton(
                GetNestedReference<Button>(
                    serialized,
                    "resourceCollectionRecipientButtonTemplate",
                    "button"),
                GetNestedReference<Text>(
                    serialized,
                    "resourceCollectionRecipientButtonTemplate",
                    "label"),
                profile.ResourceRecipientTemplateLayout,
                profile.ResourceRecipientButtonStyle);

            var legacyRoot = GetNestedReference<RectTransform>(
                serialized,
                "legacyCityStyleRowTemplate",
                "root");
            AssertLayout(legacyRoot, profile.LegacyCityStyleTemplateLayout);
            AssertText(
                GetNestedReference<Text>(serialized, "legacyCityStyleRowTemplate", "summary"),
                profile.LegacyCityStyleSummaryLayout,
                profile.LegacyCityStyleSummaryTextStyle,
                UiTheme.ValueText);
            AssertText(
                GetNestedReference<Text>(serialized, "legacyCityStyleRowTemplate", "reason"),
                profile.LegacyCityStyleReasonLayout,
                profile.LegacyCityStyleReasonTextStyle,
                UiTheme.ValueText);
            AssertButton(
                GetNestedReference<Button>(serialized, "legacyCityStyleRowTemplate", "declareButton"),
                GetNestedReference<Text>(serialized, "legacyCityStyleRowTemplate", "declareLabel"),
                profile.LegacyCityStyleDeclareLayout,
                profile.LegacyCityStyleDeclareButtonStyle);

            if (profile.PaymentRecipientFirstOffsetX != 0f ||
                profile.PaymentRecipientStepX != 110f ||
                !Approximately(profile.PaymentRouteTemplateLayout.AnchorMin, new Vector2(0f, 1f)) ||
                !Approximately(profile.PaymentRouteTemplateLayout.AnchorMax, new Vector2(1f, 1f)) ||
                !Approximately(profile.PaymentRecipientTemplateLayout.AnchorMin, new Vector2(0.36f, 0.5f)) ||
                !Approximately(profile.PaymentRecipientTemplateLayout.AnchorMax, new Vector2(0.94f, 0.5f)))
            {
                throw new InvalidOperationException(
                    "PaymentRecipient 必须保持 HEAD 的绝对锚点与 ownerIndex * 110 坐标语义。");
            }
        }

        private static void ValidateModePresentations(
            GameObject root,
            EventChoiceDialogLayoutProfile profile)
        {
            var parent = new GameObject("EventChoiceDialog Readiness Canvas", typeof(RectTransform), typeof(Canvas));
            try
            {
                var parentCanvas = parent.GetComponent<Canvas>();
                parentCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                var clone = UnityEngine.Object.Instantiate(root, parent.transform, false);
                var view = clone.GetComponent<EventChoiceDialogView>();
                var serialized = new SerializedObject(view);
                var modeFields = new[]
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
                for (var modeIndex = 0; modeIndex < modeFields.Length; modeIndex++)
                {
                    var mode = (EventChoiceDialogMode)modeIndex;
                    view.PrepareForUse(
                        mode,
                        "Event Choice Overlay",
                        "Event Choice Panel",
                        profile.PanelLayout.SizeDelta,
                        profile.PanelLayout.AnchoredPosition,
                        false);

                    GetTitleContract(mode, profile, out var titleLayout, out var titleStyle);
                    AssertText(
                        view.TitleText,
                        titleLayout,
                        titleStyle,
                        UiTheme.GoldText,
                        false,
                        true);
                    if (!view.OverlayCanvas.overrideSorting ||
                        view.OverlayCanvas.sortingOrder != 118 ||
                        !Approximately(
                            view.OverlayImage.color,
                            new Color(0f, 0f, 0f, profile.OverlayAlpha)) ||
                        view.OverlayImage.raycastTarget != GetOverlayRaycastTarget(mode, profile))
                    {
                        throw new InvalidOperationException("逐模式 Overlay 样式漂移：" + mode);
                    }

                    for (var candidateIndex = 0; candidateIndex < modeFields.Length; candidateIndex++)
                    {
                        var block = GetReference<GameObject>(serialized, modeFields[candidateIndex]);
                        if (block.activeSelf != (candidateIndex == modeIndex))
                        {
                            throw new InvalidOperationException("模式枚举与模式块映射漂移：" + mode);
                        }
                    }

                    if (TryGetCloseContract(
                            mode,
                            profile,
                            out var closeLayout,
                            out var closeStyle,
                            out var closeLabel))
                    {
                        if (!view.CloseButton.gameObject.activeSelf || view.CloseButtonLabel.text != closeLabel)
                        {
                            throw new InvalidOperationException("逐模式 Close 可见性或标签漂移：" + mode);
                        }

                        AssertCloseButton(
                            view.CloseButton,
                            view.CloseButtonLabel,
                            closeLayout,
                            closeStyle);
                    }
                    else if (view.CloseButton.gameObject.activeSelf)
                    {
                        throw new InvalidOperationException("无 Close 模式不得显示 Close：" + mode);
                    }

                    if (mode == EventChoiceDialogMode.CharacterSecondEffectDecision)
                    {
                        AssertText(
                            view.DescriptionText,
                            profile.CharacterDescriptionLayout,
                            profile.CharacterDescriptionTextStyle,
                            UiTheme.GoldText);
                    }
                    else if (mode == EventChoiceDialogMode.EventCard)
                    {
                        AssertText(
                            view.MetadataText,
                            profile.EventMetadataLayout,
                            profile.EventMetadataTextStyle,
                            UiTheme.LabelText);
                        AssertText(
                            view.DescriptionText,
                            profile.EventDescriptionLayout,
                            profile.EventDescriptionTextStyle,
                            UiTheme.ValueText);
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent);
            }
        }

        private static void GetTitleContract(
            EventChoiceDialogMode mode,
            EventChoiceDialogLayoutProfile profile,
            out EventChoiceDialogRectLayout layout,
            out EventChoiceDialogTextStyle style)
        {
            switch (mode)
            {
                case EventChoiceDialogMode.EventCard:
                    layout = profile.EventTitleLayout;
                    style = profile.EventTitleTextStyle;
                    return;
                case EventChoiceDialogMode.ExplorePath:
                    layout = profile.ExplorePathTitleLayout;
                    style = profile.ExplorePathTitleTextStyle;
                    return;
                case EventChoiceDialogMode.ExplorePayment:
                    layout = profile.ExplorePaymentTitleLayout;
                    style = profile.ExplorePaymentTitleTextStyle;
                    return;
                case EventChoiceDialogMode.ResourceCollectionPayment:
                    layout = profile.ResourcePaymentTitleLayout;
                    style = profile.ResourcePaymentTitleTextStyle;
                    return;
                case EventChoiceDialogMode.BuildFacilityFocus:
                    layout = profile.BuildFocusTitleLayout;
                    style = profile.BuildFocusTitleTextStyle;
                    return;
                case EventChoiceDialogMode.BuildFacilityConfirmation:
                    layout = profile.BuildConfirmationTitleLayout;
                    style = profile.BuildConfirmationTitleTextStyle;
                    return;
                case EventChoiceDialogMode.LegacyCityStyleOptions:
                    layout = profile.LegacyCityStyleTitleLayout;
                    style = profile.LegacyCityStyleTitleTextStyle;
                    return;
                case EventChoiceDialogMode.CharacterSecondEffectDecision:
                    layout = profile.CharacterTitleLayout;
                    style = profile.CharacterTitleTextStyle;
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }
        }

        private static bool GetOverlayRaycastTarget(
            EventChoiceDialogMode mode,
            EventChoiceDialogLayoutProfile profile)
        {
            switch (mode)
            {
                case EventChoiceDialogMode.EventCard:
                    return profile.EventOverlayRaycastTarget;
                case EventChoiceDialogMode.ExplorePath:
                    return profile.ExplorePathOverlayRaycastTarget;
                case EventChoiceDialogMode.ExplorePayment:
                    return profile.ExplorePaymentOverlayRaycastTarget;
                case EventChoiceDialogMode.ResourceCollectionPayment:
                    return profile.ResourcePaymentOverlayRaycastTarget;
                case EventChoiceDialogMode.BuildFacilityFocus:
                    return profile.BuildFocusOverlayRaycastTarget;
                case EventChoiceDialogMode.BuildFacilityConfirmation:
                    return profile.BuildConfirmationOverlayRaycastTarget;
                case EventChoiceDialogMode.LegacyCityStyleOptions:
                    return profile.LegacyCityStyleOverlayRaycastTarget;
                case EventChoiceDialogMode.CharacterSecondEffectDecision:
                    return profile.CharacterOverlayRaycastTarget;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }
        }

        private static bool TryGetCloseContract(
            EventChoiceDialogMode mode,
            EventChoiceDialogLayoutProfile profile,
            out EventChoiceDialogRectLayout layout,
            out EventChoiceDialogButtonStyle style,
            out string label)
        {
            switch (mode)
            {
                case EventChoiceDialogMode.ResourceCollectionPayment:
                case EventChoiceDialogMode.LegacyCityStyleOptions:
                    layout = profile.ManualCloseButtonLayout;
                    style = profile.ManualCloseButtonStyle;
                    label = "X";
                    return true;
                case EventChoiceDialogMode.BuildFacilityFocus:
                case EventChoiceDialogMode.BuildFacilityConfirmation:
                    layout = profile.BuildCloseButtonLayout;
                    style = profile.BuildCloseButtonStyle;
                    label = "×";
                    return true;
                default:
                    layout = default(EventChoiceDialogRectLayout);
                    style = default(EventChoiceDialogButtonStyle);
                    label = string.Empty;
                    return false;
            }
        }

        internal static void ValidateSourceSemantics()
        {
            var dialogPath = Path.GetFullPath(
                "Assets/YC/Presentation/EventChoiceDialog.cs");
            var viewPath = Path.GetFullPath(
                "Assets/YC/Presentation/EventChoiceDialogView.cs");
            var builderPath = Path.GetFullPath(
                "Assets/YC/Editor/GameplayDialogEditorAssetBuilder.cs");
            var dialogSource = File.ReadAllText(dialogPath);
            var viewSource = File.ReadAllText(viewPath);
            var builderSource = File.ReadAllText(builderPath);
            if (Count(dialogSource, "new Vector2") != 14 ||
                Count(viewSource, "new Vector2") != 0 ||
                dialogSource.Contains("Stretch(view.") ||
                dialogSource.Contains("outline.effectDistance") ||
                dialogSource.Contains("private static void SetRect") ||
                dialogSource.Contains("private static void Stretch") ||
                dialogSource.Contains("private const float EventCard") ||
                viewSource.Contains("panel.anchorMin = panel.anchorMax") ||
                !viewSource.Contains(
                    "[SerializeField] private EventChoiceDialogLayoutProfile layoutProfile;") ||
                !viewSource.Contains("public EventChoiceDialogLayoutProfile LayoutProfile => layoutProfile;") ||
                !dialogSource.Contains("dialogRegistry.EventChoiceDialogPrefab") ||
                !builderSource.Contains(
                    "EventChoiceDialogLayoutEditorAssetBuilder.LoadRequiredProfile()") ||
                !builderSource.Contains("(\"layoutProfile\", layoutProfile)") ||
                !builderSource.Contains(
                    "EnsureControlledMetaGuid(EventChoiceDialogPrefabPath, EventChoiceDialogPrefabGuid)") ||
                !builderSource.Contains(
                    "overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay") ||
                !builderSource.Contains("overlayCanvas.targetDisplay = 0") ||
                !builderSource.Contains("LoadRequiredSharedUiVisuals()") ||
                !builderSource.Contains("collapseIcon.sprite = sharedVisuals.TriangleUp") ||
                !builderSource.Contains("collapseIcon.preserveAspect = true") ||
                !builderSource.Contains("(\"triangleUpSprite\", sharedVisuals.TriangleUp)") ||
                !builderSource.Contains("(\"triangleDownSprite\", sharedVisuals.TriangleDown)") ||
                !dialogSource.Contains("layout.PaymentRecipientFirstOffsetX +") ||
                !viewSource.Contains("ConfigureModeFixedPresentation(mode)") ||
                !viewSource.Contains("layoutProfile.OverlayAlpha"))
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog 必须只保留 14 处数据量驱动 Vector2，且不得恢复固定布局覆盖。");
            }

            ValidateConsumerSourceContract(dialogSource, viewSource);

            var consumerSources = dialogSource + "\n" + viewSource + "\n" + builderSource;
            var layoutProperties = typeof(EventChoiceDialogLayoutProfile).GetProperties(
                BindingFlags.Instance | BindingFlags.Public);
            for (var i = 0; i < layoutProperties.Length; i++)
            {
                var property = layoutProperties[i];
                if (property.DeclaringType != typeof(EventChoiceDialogLayoutProfile) ||
                    property.Name == nameof(EventChoiceDialogLayoutProfile.SourceManifestSha256))
                {
                    continue;
                }

                if (!consumerSources.Contains("." + property.Name))
                {
                    throw new InvalidOperationException(
                        "EventChoiceDialog 布局 Profile 存在未接入生产代码的字段：" + property.Name);
                }
            }
        }

        internal static void ValidateConsumerSourceContract(
            string dialogSource,
            string viewSource)
        {
            if (dialogSource == null || viewSource == null)
            {
                throw new InvalidOperationException("EventChoiceDialog consumer 源码为空。");
            }

            if (Count(dialogSource, "new Vector2") != 14 ||
                Count(viewSource, "new Vector2") != 0)
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog consumer 必须保持 new Vector2 计数 14/0。");
            }

            var dialogSha256 = ComputeNormalizedSourceSha256(dialogSource);
            var viewSha256 = ComputeNormalizedSourceSha256(viewSource);
            if (!string.Equals(
                    dialogSha256,
                    EventChoiceDialogSourceSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    viewSha256,
                    EventChoiceDialogViewSourceSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog consumer 规范化源码 SHA-256 漂移；" +
                    "固定布局写入、Transform 位移与任意语义改动均须显式更新合同。" +
                    " 实际=" + dialogSha256 + "/" + viewSha256);
            }
        }

        internal static string ComputeNormalizedSourceSha256(string source)
        {
            if (source == null)
            {
                throw new InvalidOperationException("待计算 SHA-256 的源码为空。");
            }

            var normalizedLineEndings = source
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            var lines = normalizedLineEndings.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                lines[i] = lines[i].TrimEnd(' ', '\t');
            }

            var normalized = string.Join("\n", lines)
                .TrimEnd(' ', '\t', '\n') + "\n";
            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(
                        sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized)))
                    .Replace("-", string.Empty);
            }
        }

        internal static void ValidateSampleSceneYaml(string sceneYaml)
        {
            if (string.IsNullOrWhiteSpace(sceneYaml))
            {
                throw new InvalidOperationException("SampleScene YAML 为空。");
            }

            const string prefabDocumentPattern =
                @"(?ms)^--- !u!1001 &(?<localId>\d+)\r?\nPrefabInstance:\r?\n(?<body>.*?)(?=^--- |\z)";
            var documents = Regex.Matches(sceneYaml, prefabDocumentPattern)
                .Cast<Match>()
                .ToArray();
            var sourcePattern =
                @"(?m)^[ \t]*m_SourcePrefab:\s*\{fileID:\s*100100000,\s*guid:\s*" +
                Regex.Escape(GameplayHudPrefabGuid) +
                @",\s*type:\s*3\}\s*$";
            var hudDocuments = documents
                .Where(document => Regex.IsMatch(document.Groups["body"].Value, sourcePattern))
                .ToArray();
            if (hudDocuments.Length != 1 ||
                hudDocuments[0].Groups["localId"].Value != GameplayHudSceneInstanceLocalId ||
                Regex.Matches(sceneYaml, sourcePattern).Count != 1)
            {
                throw new InvalidOperationException(
                    "SampleScene 必须且只能包含固定 localID/source GUID 的 Gameplay HUD PrefabInstance。");
            }

            var body = hudDocuments[0].Groups["body"].Value;
            var modifications = Regex.Matches(
                body,
                @"(?ms)^[ \t]*- target:\s*\{fileID:\s*(?<localId>-?\d+),[^\r\n]*\}\s*\r?\n[ \t]*propertyPath:\s*(?<property>[^\r\n]*)")
                .Cast<Match>()
                .ToArray();
            for (var i = 0; i < modifications.Length; i++)
            {
                var localId = modifications[i].Groups["localId"].Value;
                var property = modifications[i].Groups["property"].Value.Trim();
                if (localId == GameplayHudRegistryLocalId ||
                    (localId == GameplayHudViewLocalId && property == "dialogRegistry") ||
                    (localId == GameplayHudRootGameObjectLocalId && property == "m_IsActive"))
                {
                    throw new InvalidOperationException(
                        "SampleScene 不得覆盖 HUD Registry/EventChoiceDialog/根激活状态生产合同：" +
                        localId + "." + property);
                }
            }

            var removedComponents = ExtractYamlSection(
                body,
                "m_RemovedComponents:",
                "m_RemovedGameObjects:");
            if (ContainsLocalId(removedComponents, GameplayHudRegistryLocalId) ||
                ContainsLocalId(removedComponents, GameplayHudViewLocalId))
            {
                throw new InvalidOperationException(
                    "SampleScene 不得移除 GameplayDialogRegistry 或 GameplayInteractionHudView 组件。");
            }

            var removedGameObjects = ExtractYamlSection(
                body,
                "m_RemovedGameObjects:",
                "m_AddedGameObjects:");
            if (ContainsLocalId(removedGameObjects, GameplayHudRegistryGameObjectLocalId) ||
                ContainsLocalId(removedGameObjects, GameplayHudRootGameObjectLocalId))
            {
                throw new InvalidOperationException(
                    "SampleScene 不得移除 GameplayDialogRegistry 或 Gameplay HUD 根对象。");
            }
        }

        private static string ExtractYamlSection(
            string body,
            string startMarker,
            string endMarker)
        {
            var start = body.IndexOf(startMarker, StringComparison.Ordinal);
            if (start < 0)
            {
                throw new InvalidOperationException("SampleScene HUD PrefabInstance 缺少 " + startMarker);
            }

            start += startMarker.Length;
            var end = body.IndexOf(endMarker, start, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidOperationException("SampleScene HUD PrefabInstance 缺少 " + endMarker);
            }

            return body.Substring(start, end - start);
        }

        private static bool ContainsLocalId(string yamlSection, string localId)
        {
            return Regex.IsMatch(
                yamlSection,
                @"(?<!\d)" + Regex.Escape(localId) + @"(?!\d)",
                RegexOptions.CultureInvariant);
        }

        private static void ValidateProductionDependencies(
            GameObject prefab,
            EventChoiceDialogLayoutProfile profile)
        {
            var dependencies = AssetDatabase.GetDependencies(EventChoiceDialogPrefabPath, true);
            if (dependencies.Count(path => string.Equals(
                    path,
                    EventChoiceDialogLayoutEditorAssetBuilder.ProfileAssetPath,
                    StringComparison.Ordinal)) != 1 ||
                dependencies.Count(path => string.Equals(
                    path,
                    YC.EditorTools.GameplayDialogEditorAssetBuilder.SharedUiVisualsAssetPath,
                    StringComparison.Ordinal)) != 1)
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog Prefab 必须且只能依赖唯一布局 Profile 与 SharedUiVisuals。");
            }

            var hudPath = YC.EditorTools.GameplayInteractionHudEditorAssetBuilder.PrefabPath;
            var hud = AssetDatabase.LoadAssetAtPath<GameObject>(hudPath);
            var hudSource = File.ReadAllText(Path.GetFullPath(hudPath));
            var expectedEventReference =
                "eventChoiceDialogPrefab: {fileID: " + EventChoiceDialogViewLocalId +
                ", guid: " + EventChoiceDialogPrefabGuid + ", type: 3}";
            var registries = hud == null
                ? Array.Empty<GameplayDialogRegistry>()
                : hud.GetComponentsInChildren<GameplayDialogRegistry>(true);
            var expectedView = prefab.GetComponent<EventChoiceDialogView>();
            if (registries.Length != 1 || registries[0].EventChoiceDialogPrefab != expectedView ||
                Count(hudSource, expectedEventReference) != 1 ||
                AssetDatabase.GetDependencies(hudPath, true).Count(path => string.Equals(
                    path,
                    EventChoiceDialogPrefabPath,
                    StringComparison.Ordinal)) != 1 ||
                expectedView.LayoutProfile != profile)
            {
                throw new InvalidOperationException(
                    "GameplayInteractionHud 的生产 Registry 必须唯一引用锁定 EventChoiceDialog Prefab。");
            }
        }

        private static void AssertControlledMetaGuid(string assetPath, string expectedGuid)
        {
            EventChoiceDialogControlledMetaGuid.ValidateAsset(assetPath, expectedGuid);
        }

        private static T GetReference<T>(SerializedObject serialized, string propertyName)
            where T : UnityEngine.Object
        {
            var property = serialized.FindProperty(propertyName);
            var value = property == null ? null : property.objectReferenceValue as T;
            if (value == null)
            {
                throw new InvalidOperationException(
                    "EventChoiceDialogView 序列化字段缺失或类型错误：" + propertyName);
            }

            return value;
        }

        private static T GetNestedReference<T>(
            SerializedObject serialized,
            string containerName,
            string propertyName)
            where T : UnityEngine.Object
        {
            var container = serialized.FindProperty(containerName);
            var property = container == null ? null : container.FindPropertyRelative(propertyName);
            var value = property == null ? null : property.objectReferenceValue as T;
            if (value == null)
            {
                throw new InvalidOperationException(
                    "EventChoiceDialogView 嵌套序列化字段缺失或类型错误：" +
                    containerName + "." + propertyName);
            }

            return value;
        }

        private static T AssertDirectField<T>(
            SerializedObject serialized,
            string fieldName,
            Transform parent,
            string expectedName)
            where T : Component
        {
            var component = GetReference<T>(serialized, fieldName);
            AssertDirectChild(component, parent, expectedName);
            return component;
        }

        private static Button AssertButtonAndLabel(
            SerializedObject serialized,
            string buttonField,
            string labelField,
            Transform parent,
            string expectedButtonName)
        {
            var button = GetReference<Button>(serialized, buttonField);
            var label = GetReference<Text>(serialized, labelField);
            AssertDirectChild(button, parent, expectedButtonName);
            AssertDirectChild(label, button.transform, "Label");
            AssertExactDirectChildren(button.transform, label.transform);
            return button;
        }

        private static RectTransform AssertButtonRowTopology(
            SerializedObject serialized,
            string containerName,
            Transform parent,
            string expectedName)
        {
            var root = GetNestedReference<RectTransform>(serialized, containerName, "root");
            var button = GetNestedReference<Button>(serialized, containerName, "button");
            var label = GetNestedReference<Text>(serialized, containerName, "label");
            AssertDirectChild(root, parent, expectedName);
            if (button.gameObject != root.gameObject)
            {
                throw new InvalidOperationException(containerName + " 的 Button 必须位于模板根自身。");
            }

            AssertDirectChild(label, root, "Label");
            AssertExactDirectChildren(root, label.transform);
            return root;
        }

        private static RectTransform FindDirectRequired(Transform parent, string objectName)
        {
            RectTransform match = null;
            var count = 0;
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name != objectName)
                {
                    continue;
                }

                count++;
                match = child as RectTransform;
            }

            if (count != 1 || match == null)
            {
                throw new InvalidOperationException(
                    "直属节点必须唯一且为 RectTransform：" + objectName);
            }

            return match;
        }

        private static void AssertDirectChild(
            Component component,
            Transform parent,
            string expectedName)
        {
            if (component == null)
            {
                throw new InvalidOperationException("缺少直属组件节点：" + expectedName);
            }

            AssertDirectChild(component.transform, parent, expectedName);
        }

        private static void AssertDirectChild(
            Transform child,
            Transform parent,
            string expectedName)
        {
            if (child == null || child.parent != parent || child.name != expectedName)
            {
                throw new InvalidOperationException(
                    "节点名称或直属父级漂移：" + expectedName);
            }

            var matches = 0;
            for (var i = 0; i < parent.childCount; i++)
            {
                if (parent.GetChild(i).name == expectedName)
                {
                    matches++;
                }
            }

            if (matches != 1)
            {
                throw new InvalidOperationException("直属同名节点必须唯一：" + expectedName);
            }
        }

        private static void AssertUniqueDescendantName(
            GameObject root,
            string objectName,
            Transform expected)
        {
            var matches = root.GetComponentsInChildren<Transform>(true)
                .Where(candidate => candidate.name == objectName)
                .ToArray();
            if (matches.Length != 1 || matches[0] != expected)
            {
                throw new InvalidOperationException("关键节点存在同名诱饵或错接：" + objectName);
            }
        }

        private static void AssertDistinct<T>(T[] values, string label)
            where T : UnityEngine.Object
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] == null)
                {
                    throw new InvalidOperationException(label + " 包含空引用。");
                }

                for (var candidate = i + 1; candidate < values.Length; candidate++)
                {
                    if (values[i] == values[candidate])
                    {
                        throw new InvalidOperationException(label + " 必须逐项互异，禁止交换或全指同一对象。");
                    }
                }
            }
        }

        private static void AssertExactDirectChildren(
            Transform parent,
            params Transform[] expectedChildren)
        {
            if (parent == null || expectedChildren == null)
            {
                throw new InvalidOperationException("固定层级的父节点或直属子集合为空。");
            }

            for (var i = 0; i < expectedChildren.Length; i++)
            {
                if (expectedChildren[i] == null || expectedChildren[i].parent != parent)
                {
                    throw new InvalidOperationException(
                        "固定层级存在空引用或非直属子节点：" + parent.name);
                }

                for (var candidate = i + 1; candidate < expectedChildren.Length; candidate++)
                {
                    if (expectedChildren[i] == expectedChildren[candidate])
                    {
                        throw new InvalidOperationException(
                            "固定层级的预期直属子节点必须互异：" + parent.name);
                    }
                }
            }

            if (parent.childCount != expectedChildren.Length)
            {
                throw new InvalidOperationException(
                    "固定层级直属子节点集合漂移：" + parent.name);
            }

            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                var found = false;
                for (var candidate = 0; candidate < expectedChildren.Length; candidate++)
                {
                    if (child == expectedChildren[candidate])
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    throw new InvalidOperationException(
                        "固定层级包含未授权直属子节点：" + parent.name + "/" + child.name);
                }
            }
        }

        private static void AssertExactComponentTypes(
            GameObject owner,
            params Type[] expectedTypes)
        {
            if (owner == null || expectedTypes == null)
            {
                throw new InvalidOperationException("固定节点或组件类型集合为空。");
            }

            var rect = owner.GetComponent<RectTransform>();
            if (rect != null)
            {
                AssertRectTransformVisibility(rect);
            }

            AssertExactComponentTypeSet(owner, expectedTypes);
        }

        private static void AssertExactScreenSpaceOverlayRootComponentTypes(
            GameObject owner,
            params Type[] expectedTypes)
        {
            if (owner == null || expectedTypes == null)
            {
                throw new InvalidOperationException("固定根节点或组件类型集合为空。");
            }

            AssertScreenSpaceOverlayRootRect(owner.GetComponent<RectTransform>());
            AssertExactComponentTypeSet(owner, expectedTypes);
        }

        private static void AssertExactComponentTypeSet(
            GameObject owner,
            Type[] expectedTypes)
        {
            if (owner == null || expectedTypes == null)
            {
                throw new InvalidOperationException("固定节点或组件类型集合为空。");
            }

            var components = owner.GetComponents<Component>();
            if (components.Length != expectedTypes.Length)
            {
                throw new InvalidOperationException("固定节点组件集合漂移：" + owner.name);
            }

            var matched = new bool[expectedTypes.Length];
            for (var i = 0; i < components.Length; i++)
            {
                if (components[i] == null)
                {
                    throw new InvalidOperationException("固定节点包含丢失脚本：" + owner.name);
                }

                var found = false;
                for (var candidate = 0; candidate < expectedTypes.Length; candidate++)
                {
                    if (!matched[candidate] && components[i].GetType() == expectedTypes[candidate])
                    {
                        matched[candidate] = true;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    throw new InvalidOperationException(
                        "固定节点包含未授权组件：" + owner.name + "/" + components[i].GetType().Name);
                }
            }
        }

        private static void AssertScreenSpaceOverlayRootRect(RectTransform rect)
        {
            if (rect == null ||
                rect.localScale.sqrMagnitude > 0.00000001f ||
                Quaternion.Angle(rect.localRotation, Quaternion.identity) > 0.0001f ||
                rect.localPosition.sqrMagnitude > 0.00000001f ||
                rect.anchoredPosition3D.sqrMagnitude > 0.00000001f ||
                !Approximately(rect.anchorMin, Vector2.zero) ||
                !Approximately(rect.anchorMax, Vector2.zero) ||
                !Approximately(rect.sizeDelta, Vector2.zero) ||
                !Approximately(rect.pivot, Vector2.zero))
            {
                throw new InvalidOperationException(
                    "ScreenSpaceOverlay Prefab 根 RectTransform 未保持 Unity 固定归一化值。");
            }
        }

        private static void AssertRectTransformVisibility(RectTransform rect)
        {
            if (rect == null ||
                (rect.localScale - Vector3.one).sqrMagnitude > 0.00000001f ||
                Quaternion.Angle(rect.localRotation, Quaternion.identity) > 0.0001f ||
                Mathf.Abs(rect.anchoredPosition3D.z) > 0.0001f)
            {
                throw new InvalidOperationException(
                    "受控 RectTransform 的 scale/rotation/z 漂移：" +
                    (rect == null ? "<null>" : rect.name));
            }
        }

        private static void AssertActiveSelf(
            GameObject target,
            bool expected,
            string label)
        {
            if (target == null || target.activeSelf != expected)
            {
                throw new InvalidOperationException(
                    label + " 的 Prefab activeSelf 必须为 " + expected + "。");
            }
        }

        private static void AssertEnabled(Behaviour component, string label)
        {
            if (component == null || !component.enabled)
            {
                throw new InvalidOperationException(label + " 必须存在且 enabled=true。");
            }
        }

        private static void AssertExactTextComponents(
            Text text,
            bool expectOutline,
            bool expectDragHandle)
        {
            if (text == null)
            {
                throw new InvalidOperationException("固定文本为空。");
            }

            if (expectOutline && expectDragHandle)
            {
                AssertExactComponentTypes(
                    text.gameObject,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Text),
                    typeof(Outline),
                    typeof(EffectDialogDragHandle));
            }
            else if (expectOutline)
            {
                AssertExactComponentTypes(
                    text.gameObject,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Text),
                    typeof(Outline));
            }
            else if (expectDragHandle)
            {
                AssertExactComponentTypes(
                    text.gameObject,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Text),
                    typeof(EffectDialogDragHandle));
            }
            else
            {
                AssertExactComponentTypes(
                    text.gameObject,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Text));
            }
        }

        private static void AssertSingleComponent<T>(
            GameObject owner,
            T expected,
            string label)
            where T : Component
        {
            var components = owner.GetComponents<T>();
            if (expected == null || components.Length != 1 || components[0] != expected)
            {
                throw new InvalidOperationException(label + " 必须是对象上的唯一组件并保持字段身份。");
            }
        }

        private static void AssertText(
            Text text,
            EventChoiceDialogRectLayout expectedLayout,
            EventChoiceDialogTextStyle expectedStyle,
            Color expectedColor,
            bool expectOutline = false,
            bool expectDragHandle = false)
        {
            AssertLayout(text == null ? null : text.rectTransform, expectedLayout);
            if (text != null)
            {
                AssertSingleComponent(text.gameObject, text, text.name + " Text");
                AssertExactTextComponents(text, expectOutline, expectDragHandle);
                AssertExactDirectChildren(text.transform);
                AssertEnabled(text, text.name + " Text");
            }

            if (text == null || text.font != YC.EditorTools.UiEditorAssetReferences.CjkFont ||
                text.fontSize != expectedStyle.FontSize ||
                text.resizeTextMinSize != expectedStyle.ResizeMinSize ||
                text.resizeTextMaxSize != expectedStyle.ResizeMaxSize ||
                text.fontStyle != expectedStyle.FontStyle ||
                text.alignment != expectedStyle.Alignment ||
                text.horizontalOverflow != expectedStyle.HorizontalOverflow ||
                text.verticalOverflow != expectedStyle.VerticalOverflow ||
                text.resizeTextForBestFit != expectedStyle.ResizeTextForBestFit ||
                text.raycastTarget != expectedStyle.RaycastTarget ||
                !Approximately(text.color, expectedColor))
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog 固定文本样式漂移：" + (text == null ? "<null>" : text.name));
            }
        }

        private static void AssertButton(
            Button button,
            Text label,
            EventChoiceDialogRectLayout expectedLayout,
            EventChoiceDialogButtonStyle expectedStyle,
            params Transform[] expectedAdditionalChildren)
        {
            AssertButtonCore(
                button,
                label,
                expectedLayout,
                expectedStyle,
                false,
                expectedAdditionalChildren);
        }

        private static void AssertCloseButton(
            Button button,
            Text label,
            EventChoiceDialogRectLayout expectedLayout,
            EventChoiceDialogButtonStyle expectedStyle)
        {
            AssertExactCloseLabelComponents(label);
            AssertButtonCore(
                button,
                label,
                expectedLayout,
                expectedStyle,
                true,
                Array.Empty<Transform>());
        }

        private static void AssertExactCloseLabelComponents(Text label)
        {
            if (label == null)
            {
                throw new InvalidOperationException("Close Label 为空。");
            }

            AssertExactComponentTypes(
                label.gameObject,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Text),
                typeof(Outline));
        }

        private static void AssertButtonCore(
            Button button,
            Text label,
            EventChoiceDialogRectLayout expectedLayout,
            EventChoiceDialogButtonStyle expectedStyle,
            bool isCloseButtonLabel,
            Transform[] expectedAdditionalChildren)
        {
            if (button == null || label == null || button.gameObject != label.transform.parent.gameObject)
            {
                throw new InvalidOperationException("EventChoiceDialog 固定按钮或直属 Label 引用无效。");
            }

            AssertLayout(button.GetComponent<RectTransform>(), expectedLayout);
            var image = button.GetComponent<Image>();
            var outline = button.GetComponent<Outline>();
            AssertSingleComponent(button.gameObject, button, button.name + " Button");
            AssertSingleComponent(button.gameObject, image, button.name + " Image");
            AssertSingleComponent(button.gameObject, outline, button.name + " Outline");
            AssertExactComponentTypes(
                button.gameObject,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button),
                typeof(Outline));
            AssertActiveSelf(label.gameObject, true, button.name + " Label");
            AssertEnabled(button, button.name + " Button");
            AssertEnabled(image, button.name + " Image");
            AssertEnabled(outline, button.name + " Outline");
            if (!button.interactable || button.targetGraphic != image ||
                button.transition != Selectable.Transition.ColorTint)
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog Button 交互合同漂移：" + button.name);
            }
            var expectedChildren = new Transform[1 + expectedAdditionalChildren.Length];
            expectedChildren[0] = label.transform;
            Array.Copy(
                expectedAdditionalChildren,
                0,
                expectedChildren,
                1,
                expectedAdditionalChildren.Length);
            AssertExactDirectChildren(button.transform, expectedChildren);
            if (image == null || outline == null ||
                !Approximately(image.color, UiTheme.ButtonBackground) ||
                !Approximately(outline.effectColor, UiTheme.GoldOutlineThin) ||
                !Approximately(outline.effectDistance, expectedStyle.OutlineDistance))
            {
                throw new InvalidOperationException("EventChoiceDialog 固定按钮 Image/Outline 漂移：" + button.name);
            }

            var labelRect = label.rectTransform;
            if (!Approximately(labelRect.anchorMin, Vector2.zero) ||
                !Approximately(labelRect.anchorMax, Vector2.one) ||
                !Approximately(labelRect.offsetMin, expectedStyle.LabelOffsetMin) ||
                !Approximately(labelRect.offsetMax, expectedStyle.LabelOffsetMax))
            {
                throw new InvalidOperationException("EventChoiceDialog 固定按钮 Label padding 漂移：" + button.name);
            }

            var labelLayout = new EventChoiceDialogRectLayout
            {
                AnchorMin = Vector2.zero,
                AnchorMax = Vector2.one,
                Pivot = labelRect.pivot,
                SizeDelta = labelRect.sizeDelta,
                AnchoredPosition = labelRect.anchoredPosition
            };
            AssertText(
                label,
                labelLayout,
                expectedStyle.LabelStyle,
                UiTheme.GoldText,
                expectedStyle.LabelHasOutline || isCloseButtonLabel);

            var labelOutline = label.GetComponent<Outline>();
            if (expectedStyle.LabelHasOutline)
            {
                if (labelOutline == null || !labelOutline.enabled ||
                    !Approximately(labelOutline.effectColor, UiTheme.DarkShadowLight) ||
                    !Approximately(labelOutline.effectDistance, expectedStyle.LabelOutlineDistance))
                {
                    throw new InvalidOperationException("Close Label Outline 固定样式漂移：" + button.name);
                }
            }
            else if (labelOutline != null && labelOutline.enabled)
            {
                throw new InvalidOperationException("该按钮 Label 不应启用 Outline：" + button.name);
            }
        }

        private static void AssertLayout(
            RectTransform rect,
            EventChoiceDialogRectLayout expected)
        {
            if (rect == null ||
                !Approximately(rect.anchorMin, expected.AnchorMin) ||
                !Approximately(rect.anchorMax, expected.AnchorMax) ||
                !Approximately(rect.pivot, expected.Pivot) ||
                !Approximately(rect.sizeDelta, expected.SizeDelta) ||
                !Approximately(rect.anchoredPosition, expected.AnchoredPosition))
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog Prefab 固定布局漂移：" + rect.name);
            }
        }

        private static void AssertStretched(RectTransform rect, string label)
        {
            if (!Approximately(rect.anchorMin, Vector2.zero) ||
                !Approximately(rect.anchorMax, Vector2.one) ||
                !Approximately(rect.offsetMin, Vector2.zero) ||
                !Approximately(rect.offsetMax, Vector2.zero))
            {
                throw new InvalidOperationException(
                    "EventChoiceDialog Host 未在 Prefab 中固定为 Stretch：" + label);
            }
        }

        private static bool Approximately(Vector2 actual, Vector2 expected)
        {
            return Mathf.Abs(actual.x - expected.x) <= 0.0001f &&
                   Mathf.Abs(actual.y - expected.y) <= 0.0001f;
        }

        private static bool Approximately(Color actual, Color expected)
        {
            return Mathf.Abs(actual.r - expected.r) <= 0.0001f &&
                   Mathf.Abs(actual.g - expected.g) <= 0.0001f &&
                   Mathf.Abs(actual.b - expected.b) <= 0.0001f &&
                   Mathf.Abs(actual.a - expected.a) <= 0.0001f;
        }

        private static int Count(string source, string value)
        {
            var count = 0;
            var index = 0;
            while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }
    }

    internal static class EventChoiceDialogControlledMetaGuid
    {
        private static readonly Regex TopLevelGuidLine = new Regex(
            @"(?m)^guid:[ \t]*(?<guid>[0-9a-fA-F]{32})[ \t]*\r?$",
            RegexOptions.CultureInvariant);

        internal static void ValidateAsset(string assetPath, string expectedGuid)
        {
            var metaPath = Path.GetFullPath(assetPath + ".meta");
            if (!File.Exists(metaPath))
            {
                throw new InvalidOperationException(
                    "受控资产必须保留固定 GUID 的 meta 后才能创建或保存：" + metaPath);
            }

            ValidateMetaGuidText(
                File.ReadAllText(metaPath),
                expectedGuid,
                AssetDatabase.AssetPathToGUID(assetPath));
        }

        internal static void ValidateMetaGuidText(
            string metaText,
            string expectedGuid,
            string actualAssetGuid)
        {
            if (string.IsNullOrEmpty(expectedGuid) ||
                !Regex.IsMatch(
                    expectedGuid,
                    @"\A[0-9a-fA-F]{32}\z",
                    RegexOptions.CultureInvariant))
            {
                throw new InvalidOperationException("受控资产预期 GUID 格式无效。");
            }

            var matches = TopLevelGuidLine.Matches(metaText ?? string.Empty);
            if (matches.Count != 1 ||
                !string.Equals(
                    matches[0].Groups["guid"].Value,
                    expectedGuid,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "受控资产 meta 必须且只能包含一条顶层 guid，且值必须精确匹配固定 GUID。");
            }

            if (string.IsNullOrEmpty(actualAssetGuid) ||
                !string.Equals(actualAssetGuid, expectedGuid, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "AssetDatabase 中的受控资产实际 GUID 与固定 GUID 不一致。");
            }
        }
    }
}
