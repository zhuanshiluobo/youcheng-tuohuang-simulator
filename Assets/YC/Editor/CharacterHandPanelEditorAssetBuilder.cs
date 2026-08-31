using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation;

namespace YC.EditorTools
{
    public static class CharacterHandPanelEditorAssetBuilder
    {
        private struct PreviewPane
        {
            public Text Title;
            public RectTransform Content;
            public GridLayoutGroup Grid;
            public GameObject EmptyObject;
        }

        public const string PrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/CharacterHandPanel.prefab";
        public const string GrayscaleMaterialPath =
            "Assets/YC/Presentation/Materials/UiGrayscale.mat";
        public const string GrayscaleShaderPath =
            "Assets/YC/Presentation/Shaders/UiGrayscale.shader";
        public const string HandEntranceClipPath =
            "Assets/YC/Presentation/Animations/CharacterHandEntrance/CharacterHandReveal.anim";
        public const string HandReturnClipPath =
            "Assets/YC/Presentation/Animations/CharacterHandEntrance/CharacterHandReturn.anim";

        [MenuItem("Tools/YC/Rebuild Character Hand Panel Editor Asset")]
        public static void Rebuild()
        {
            YC.Editor.UiThemeBuildReadiness.InitializeRequiredTheme();
            YC.Editor.SecondaryLayoutEditorAssetBuilder.RebuildAssets();
            EnsureFolder("Assets/YC/Presentation/Prefabs/Gameplay");
            EnsureFolder("Assets/YC/Presentation/Materials");
            var profile = YC.Editor.SecondaryLayoutEditorAssetBuilder.LoadRequiredCharacterHandProfile();
            var grayscaleMaterial = LoadOrCreateGrayscaleMaterial();
            var root = BuildPrefabContents(profile, grayscaleMaterial);
            try
            {
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(PrefabPath, ImportAssetOptions.ForceUpdate);
        }

        internal static GameObject BuildPrefabContents(
            CharacterHandLayoutProfile profile,
            Material grayscaleMaterial)
        {
            if (profile == null)
                throw new InvalidOperationException("缺少手牌布局 Profile。");
            if (!profile.TryValidateConfiguration(out var reason))
                throw new InvalidOperationException("手牌布局 Profile 无效：" + reason);
            if (grayscaleMaterial == null)
                throw new InvalidOperationException("手牌预制体缺少灰度材质。");

            var root = CreateUiObject(
                "Character Hand Panel",
                null,
                typeof(CharacterHandPanel),
                typeof(CharacterHandPanelView));
            var rootRect = root.GetComponent<RectTransform>();
            Stretch(rootRect);

            var handCards = CreateUiObject("Hand Cards", root.transform, typeof(Animation));
            var handCardsRect = handCards.GetComponent<RectTransform>();
            Stretch(handCardsRect);
            var handCardsAnimation = handCards.GetComponent<Animation>();
            var handEntranceClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(HandEntranceClipPath);
            var handReturnClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(HandReturnClipPath);
            if (handEntranceClip == null || !handEntranceClip.legacy ||
                handReturnClip == null || !handReturnClip.legacy)
                throw new InvalidOperationException("缺少有效的手牌入场或收回 Animation Clip。");
            handCardsAnimation.clip = handEntranceClip;
            handCardsAnimation.AddClip(handEntranceClip, handEntranceClip.name);
            handCardsAnimation.AddClip(handReturnClip, handReturnClip.name);
            handCardsAnimation.playAutomatically = false;
            handCardsAnimation.cullingType = AnimationCullingType.AlwaysAnimate;

            var handDropArea = CreateUiObject("Hand Reorder Drop Area", root.transform);
            var handDropRect = handDropArea.GetComponent<RectTransform>();
            handDropRect.anchorMin = Vector2.zero;
            handDropRect.anchorMax = Vector2.right;
            handDropRect.pivot = Vector2.right * 0.5f;
            handDropRect.sizeDelta = Vector2.up * 360f;
            handDropRect.anchoredPosition = Vector2.zero;

            var discardButton = BuildDiscardButton(rootRect, profile);
            var discardCount = discardButton.transform.Find("Count Badge/Count").GetComponent<Text>();

            var overlay = CreateUiObject(
                "Discard Preview Overlay",
                root.transform,
                typeof(Image),
                typeof(WindowCloseInputHandler));
            var overlayRect = overlay.GetComponent<RectTransform>();
            Stretch(overlayRect);
            overlay.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.76f);
            overlay.GetComponent<Image>().raycastTarget = true;

            var panel = CreateUiObject(
                "Discard Preview Panel",
                overlay.transform,
                typeof(Image),
                typeof(Outline));
            var panelRect = panel.GetComponent<RectTransform>();
            SetCentered(panelRect, profile.ModalPanelSize, profile.ModalPanelPosition);
            panel.GetComponent<Image>().color = UiTheme.PanelBackground;
            panel.GetComponent<Outline>().effectColor = UiTheme.GoldOutline;
            panel.GetComponent<Outline>().effectDistance = Vector2.right * 3f + Vector2.down * 3f;

            var closeButton = BuildCloseButton(panelRect);
            var handPane = BuildPreviewPane(panelRect, "Hand Preview", "手牌", "暂无手牌", -350f);
            var discardPane = BuildPreviewPane(panelRect, "Discard Preview", "弃牌", "暂无弃牌", 350f);

            var templates = CreateUiObject("Templates", root.transform);
            var templatesRect = templates.GetComponent<RectTransform>();
            templatesRect.anchorMin = Vector2.zero;
            templatesRect.anchorMax = Vector2.zero;
            templatesRect.sizeDelta = Vector2.zero;
            var handCardTemplate = BuildCardTemplate(
                templates.transform,
                "Hand Card Template",
                profile.CardSize);
            var overlayCardTemplate = BuildCardTemplate(
                templates.transform,
                "Overlay Card Template",
                profile.OverlayCardSize);
            var dragGhost = BuildDragGhostTemplate(templates.transform, profile.CardSize);

            var controller = root.GetComponent<CharacterHandPanel>();
            var panelView = root.GetComponent<CharacterHandPanelView>();
            SetReferences(
                controller,
                ("view", panelView),
                ("layoutProfile", profile));
            SetReferences(
                panelView,
                ("controller", controller),
                ("root", rootRect),
                ("handCardsRoot", handCardsRect),
                ("handDropArea", handDropRect),
                ("discardButton", discardButton),
                ("discardCountText", discardCount),
                ("discardOverlayObject", overlay),
                ("discardOverlayPanel", panelRect),
                ("discardCloseButton", closeButton),
                ("discardCloseInputHandler", overlay.GetComponent<WindowCloseInputHandler>()),
                ("overlayHandTitle", handPane.Title),
                ("overlayDiscardTitle", discardPane.Title),
                ("overlayHandContent", handPane.Content),
                ("overlayDiscardContent", discardPane.Content),
                ("overlayHandGrid", handPane.Grid),
                ("overlayDiscardGrid", discardPane.Grid),
                ("overlayHandEmptyObject", handPane.EmptyObject),
                ("overlayDiscardEmptyObject", discardPane.EmptyObject),
                ("handCardTemplate", handCardTemplate),
                ("overlayCardTemplate", overlayCardTemplate),
                ("dragGhostTemplate", dragGhost),
                ("dragGhostImage", dragGhost.GetComponent<RawImage>()),
                ("dragGhostCanvasGroup", dragGhost.GetComponent<CanvasGroup>()),
                ("discardGrayscaleMaterial", grayscaleMaterial));

            overlay.SetActive(false);
            return root;
        }

        private static Button BuildDiscardButton(RectTransform parent, CharacterHandLayoutProfile profile)
        {
            var root = CreateUiObject(
                "Discard Pile Button",
                parent,
                typeof(Image),
                typeof(Button),
                typeof(Outline),
                typeof(ActionButtonPressFeedback));
            var rect = root.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.right;
            rect.anchorMax = rect.anchorMin;
            rect.pivot = Vector2.right;
            rect.sizeDelta = profile.DiscardButtonSize;
            rect.anchoredPosition = profile.DiscardButtonPosition;
            var image = root.GetComponent<Image>();
            image.color = UiTheme.ButtonBackground;
            var button = root.GetComponent<Button>();
            button.targetGraphic = image;
            var outline = root.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutline;
            outline.effectDistance = Vector2.right * 2f + Vector2.down * 2f;

            var icon = CreateText(root.transform, "Icon", "弃", 30, FontStyle.Bold, TextAnchor.MiddleCenter);
            Stretch(icon.rectTransform);
            icon.raycastTarget = false;

            var badge = CreateUiObject("Count Badge", root.transform, typeof(Image), typeof(Outline));
            var badgeRect = badge.GetComponent<RectTransform>();
            badgeRect.anchorMin = Vector2.one;
            badgeRect.anchorMax = Vector2.one;
            badgeRect.pivot = Vector2.one * 0.5f;
            badgeRect.sizeDelta = Vector2.one * 30f;
            badgeRect.anchoredPosition = Vector2.right * 3f + Vector2.down * 3f;
            badge.GetComponent<Image>().color = UiTheme.PanelBackgroundLighter;
            badge.GetComponent<Outline>().effectColor = UiTheme.GoldOutlineThin;
            badge.GetComponent<Outline>().effectDistance = Vector2.right + Vector2.down;
            var count = CreateText(badge.transform, "Count", "0", 16, FontStyle.Bold, TextAnchor.MiddleCenter);
            Stretch(count.rectTransform);
            count.raycastTarget = false;
            return button;
        }

        private static Button BuildCloseButton(RectTransform parent)
        {
            var root = CreateUiObject(
                "Close Discard Preview Button",
                parent,
                typeof(Image),
                typeof(Button),
                typeof(Outline),
                typeof(ActionButtonPressFeedback));
            var rect = root.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.one;
            rect.anchorMax = Vector2.one;
            rect.pivot = Vector2.one;
            rect.sizeDelta = Vector2.one * 48f;
            rect.anchoredPosition = Vector2.left * 14f + Vector2.down * 14f;
            var image = root.GetComponent<Image>();
            image.color = UiTheme.ButtonBackground;
            root.GetComponent<Button>().targetGraphic = image;
            root.GetComponent<Outline>().effectColor = UiTheme.GoldOutlineThin;
            root.GetComponent<Outline>().effectDistance = Vector2.right + Vector2.down;
            var label = CreateText(root.transform, "Label", "×", 28, FontStyle.Bold, TextAnchor.MiddleCenter);
            Stretch(label.rectTransform);
            label.raycastTarget = false;
            return root.GetComponent<Button>();
        }

        private static PreviewPane BuildPreviewPane(
            RectTransform parent,
            string name,
            string titleText,
            string emptyText,
            float centerX)
        {
            var pane = CreateUiObject(name, parent, typeof(Image), typeof(Outline));
            var paneRect = pane.GetComponent<RectTransform>();
            SetCentered(
                paneRect,
                Vector2.right * 660f + Vector2.up * 620f,
                Vector2.right * centerX + Vector2.down * 24f);
            pane.GetComponent<Image>().color = UiTheme.ScrollBackground;
            pane.GetComponent<Outline>().effectColor = UiTheme.GoldOutlineThin;
            pane.GetComponent<Outline>().effectDistance = Vector2.right + Vector2.down;

            var title = CreateText(pane.transform, "Title", titleText, 24, FontStyle.Bold, TextAnchor.MiddleCenter);
            var titleRect = title.rectTransform;
            titleRect.anchorMin = Vector2.up;
            titleRect.anchorMax = Vector2.one;
            titleRect.pivot = Vector2.one * 0.5f;
            titleRect.sizeDelta = Vector2.down * -50f;
            titleRect.anchoredPosition = Vector2.down * 25f;

            var scrollObject = CreateUiObject(name + " Scroll", pane.transform, typeof(ScrollRect));
            var scrollRectTransform = scrollObject.GetComponent<RectTransform>();
            scrollRectTransform.anchorMin = Vector2.zero;
            scrollRectTransform.anchorMax = Vector2.one;
            scrollRectTransform.offsetMin = Vector2.one * 14f;
            scrollRectTransform.offsetMax = Vector2.left * 14f + Vector2.down * 60f;

            var viewport = CreateUiObject("Viewport", scrollObject.transform, typeof(Image), typeof(Mask));
            var viewportRect = viewport.GetComponent<RectTransform>();
            Stretch(viewportRect);
            viewport.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);
            viewport.GetComponent<Mask>().showMaskGraphic = false;

            var content = CreateUiObject(
                "Content",
                viewport.transform,
                typeof(GridLayoutGroup),
                typeof(ContentSizeFitter));
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = Vector2.up;
            contentRect.anchorMax = Vector2.one;
            contentRect.pivot = Vector2.up + Vector2.right * 0.5f;
            contentRect.sizeDelta = Vector2.zero;
            contentRect.anchoredPosition = Vector2.zero;
            var grid = content.GetComponent<GridLayoutGroup>();
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 3;
            grid.childAlignment = TextAnchor.UpperCenter;
            grid.padding = new RectOffset(18, 18, 18, 18);
            var fitter = content.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = scrollObject.GetComponent<ScrollRect>();
            scroll.viewport = viewportRect;
            scroll.content = contentRect;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            var empty = CreateText(viewport.transform, "Empty", emptyText, 22, FontStyle.Normal, TextAnchor.MiddleCenter);
            Stretch(empty.rectTransform);
            empty.raycastTarget = false;

            return new PreviewPane
            {
                Title = title,
                Content = contentRect,
                Grid = grid,
                EmptyObject = empty.gameObject
            };
        }

        private static CharacterHandCardView BuildCardTemplate(
            Transform parent,
            string name,
            Vector2 size)
        {
            var root = CreateUiObject(
                name,
                parent,
                typeof(RawImage),
                typeof(Button),
                typeof(CanvasGroup),
                typeof(Outline),
                typeof(CardPointerInteraction),
                typeof(CharacterHandCardView));
            var rect = root.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.right * 0.5f;
            rect.anchorMax = rect.anchorMin;
            rect.pivot = Vector2.one * 0.5f;
            rect.sizeDelta = size;
            var image = root.GetComponent<RawImage>();
            image.color = Color.white;
            image.raycastTarget = true;
            var button = root.GetComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.None;
            var outline = root.GetComponent<Outline>();
            outline.effectColor = UiTheme.GoldOutlineThin;
            outline.effectDistance = Vector2.right + Vector2.down;
            var view = root.GetComponent<CharacterHandCardView>();
            SetReferences(
                view,
                ("root", rect),
                ("image", image),
                ("button", button),
                ("canvasGroup", root.GetComponent<CanvasGroup>()),
                ("outline", outline),
                ("pointerInteraction", root.GetComponent<CardPointerInteraction>()));
            root.SetActive(false);
            return view;
        }

        private static RectTransform BuildDragGhostTemplate(Transform parent, Vector2 size)
        {
            var root = CreateUiObject(
                "Hand Drag Ghost Template",
                parent,
                typeof(RawImage),
                typeof(CanvasGroup),
                typeof(Outline));
            var rect = root.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.one * 0.5f;
            rect.anchorMax = rect.anchorMin;
            rect.pivot = Vector2.one * 0.5f;
            rect.sizeDelta = size;
            root.GetComponent<RawImage>().raycastTarget = false;
            var group = root.GetComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
            root.GetComponent<Outline>().effectColor = UiTheme.GoldOutline;
            root.GetComponent<Outline>().effectDistance = Vector2.right * 2f + Vector2.down * 2f;
            root.SetActive(false);
            return rect;
        }

        private static Material LoadOrCreateGrayscaleMaterial()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(GrayscaleShaderPath);
            if (shader == null)
                throw new InvalidOperationException("缺少 UGUI 灰度 Shader：" + GrayscaleShaderPath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(GrayscaleMaterialPath);
            if (material == null)
            {
                material = new Material(shader) { name = "UI Grayscale" };
                AssetDatabase.CreateAsset(material, GrayscaleMaterialPath);
            }
            else
            {
                material.shader = shader;
                EditorUtility.SetDirty(material);
            }
            return material;
        }

        private static Text CreateText(
            Transform parent,
            string name,
            string value,
            int size,
            FontStyle style,
            TextAnchor alignment)
        {
            var root = CreateUiObject(name, parent, typeof(Text));
            var text = root.GetComponent<Text>();
            text.text = value;
            text.font = UiEditorAssetReferences.CjkFont;
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = alignment;
            text.color = UiTheme.GoldText;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static GameObject CreateUiObject(string name, Transform parent, params Type[] components)
        {
            var types = new Type[components.Length + 1];
            types[0] = typeof(RectTransform);
            Array.Copy(components, 0, types, 1, components.Length);
            var root = new GameObject(name, types);
            root.layer = LayerMask.NameToLayer("UI");
            if (parent != null) root.transform.SetParent(parent, false);
            return root;
        }

        private static void SetReferences(UnityEngine.Object target, params (string property, object value)[] values)
        {
            var serialized = new SerializedObject(target);
            for (var i = 0; i < values.Length; i++)
            {
                var property = serialized.FindProperty(values[i].property);
                if (property == null)
                    throw new InvalidOperationException(target.GetType().Name + " missing property " + values[i].property + ".");
                if (values[i].value is UnityEngine.Object objectValue)
                    property.objectReferenceValue = objectValue;
                else if (values[i].value is int intValue)
                    property.intValue = intValue;
                else
                    throw new InvalidOperationException("Unsupported serialized value: " + values[i].property);
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetCentered(RectTransform rect, Vector2 size, Vector2 position)
        {
            rect.anchorMin = Vector2.one * 0.5f;
            rect.anchorMax = rect.anchorMin;
            rect.pivot = Vector2.one * 0.5f;
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var separator = path.LastIndexOf('/');
            var parent = path.Substring(0, separator);
            var name = path.Substring(separator + 1);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
