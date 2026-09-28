using System;
using System.IO;
using System.IO.Compression;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation;

namespace YC.Editor
{
    /// <summary>仅供明确授权的卡牌选择资产搭建手动执行；测试、运行和捕获不得调用。</summary>
    public static class CardPickerAssetSetup
    {
        private const string Artwork = "Assets/YC/Presentation/CardPicker/Artwork/";
        private const string Prefab = "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/CardPickerDialog.prefab";
        private static UiFontRoles fonts;
        private static readonly Color Paper = new Color32(239, 228, 214, 255);

        public static void BuildAuthorizedCardPicker()
        {
            Directory.CreateDirectory(Artwork);
            using (var zip = ZipFile.OpenRead("游城拓荒/UI素材/Card-Picker-UI-v1.zip"))
                foreach (var name in new[] { "window.png", "card-slot.png", "scrollbar-track.png", "scrollbar-thumb.png" })
                {
                    var entry = zip.GetEntry("card-picker-ui-v1/assets/" + name);
                    if (entry == null) throw new InvalidOperationException("原包缺少 " + name);
                    using (var input = entry.Open())
                    using (var output = File.Create(Artwork + name)) input.CopyTo(output);
                }
            AssetDatabase.Refresh();
            foreach (var name in new[] { "window.png", "card-slot.png", "scrollbar-track.png", "scrollbar-thumb.png" })
            {
                var importer = (TextureImporter)AssetImporter.GetAtPath(Artwork + name);
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = 100;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = true;
                importer.maxTextureSize = 4096;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }
            fonts = AssetDatabase.LoadAssetAtPath<UiFontRoles>("Assets/YC/Presentation/CommonUi/Content/UiFontRoles.asset");
            if (fonts == null) throw new InvalidOperationException("缺少字体资产。");
            var root = Node("Card Picker", null);
            try
            {
                Stretch(root);
                var canvas = root.gameObject.AddComponent<Canvas>();
                canvas.overrideSorting = true;
                canvas.sortingOrder = 118;
                root.gameObject.AddComponent<GraphicRaycaster>();
                var shade = root.gameObject.AddComponent<Image>();
                shade.color = new Color(0, 0, 0, .48f);
                var shell = root.gameObject.AddComponent<EffectDialogShellView>();
                var outer = Vertical(root, new RectOffset(24, 24, 0, 0));
                outer.childAlignment = TextAnchor.MiddleCenter;
                outer.childForceExpandWidth = false;
                var bounds = Node("Window Bounds", root);
                Input(bounds, 1774, 887);
                var panel = Node("Card Picker Window", bounds);
                var ratio = panel.gameObject.AddComponent<AspectRatioFitter>();
                ratio.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                ratio.aspectRatio = 2;
                var surface = Raw("Window Artwork", panel, Artwork + "window.png");
                Stretch(surface.rectTransform);
                var content = Node("Window Content", panel);
                Stretch(content);
                Vertical(content, new RectOffset(96, 96, 56, 59));
                var heading = Node("Header", content);
                Input(heading, -1, 102);
                Vertical(heading, new RectOffset());
                var title = Label("Title", heading, "选择卡牌", 40, true, TextAnchor.MiddleCenter);
                Input(title.rectTransform, -1, 56);
                Space(heading, "Title Spacing", 12);
                var description = Label("Description", heading, "选择所需卡牌后确认", 24, false, TextAnchor.MiddleCenter);
                Input(description.rectTransform, -1, 34);
                Space(content, "Header Spacing", 40);
                var scrollRoot = Node("Card Scroll", content);
                var scrollInput = Input(scrollRoot, -1, 482);
                scrollInput.flexibleHeight = 1;
                var scroll = scrollRoot.gameObject.AddComponent<ScrollRect>();
                scroll.horizontal = true;
                scroll.vertical = false;
                scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.scrollSensitivity = 50;
                var viewport = Node("Viewport", scrollRoot);
                Stretch(viewport);
                viewport.gameObject.AddComponent<RectMask2D>();
                var hit = viewport.gameObject.AddComponent<Image>();
                hit.color = Color.clear;
                hit.canvasRenderer.cullTransparentMesh = false;
                var row = Node("Card Row", viewport);
                row.anchorMin = new Vector2(0, 0);
                row.anchorMax = new Vector2(0, 1);
                row.pivot = new Vector2(0, .5f);
                row.sizeDelta = Vector2.zero;
                row.gameObject.AddComponent<RectMask2D>();
                var horizontal = Horizontal(row, 16);
                horizontal.childAlignment = TextAnchor.MiddleCenter;
                horizontal.padding = new RectOffset(1, 1, 0, 0);
                horizontal.childForceExpandHeight = false;
                Input(row, -1, -1);
                var fitter = row.gameObject.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                var rowLayout = row.gameObject.AddComponent<CardPickerRowLayout>();
                scroll.viewport = viewport;
                scroll.content = row;
                var bar = Scrollbar(scrollRoot);
                scroll.horizontalScrollbar = bar;
                // 不使用AutoHideAndExpandViewport，滚动条是原视口内的独立装饰与热区。
                scroll.horizontalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
                rowLayout.ConfigureForEditor(viewport, horizontal, scroll, bar.gameObject);
                Space(content, "Footer Spacing", 64);
                var footer = Node("Footer", content);
                Input(footer, -1, 84);
                Horizontal(footer, 0);
                var summaryColumn = Node("Selection Information", footer);
                var summaryInput = Input(summaryColumn, 920, 84);
                summaryInput.flexibleWidth = 1;
                Vertical(summaryColumn, new RectOffset());
                var summary = Label("Selection Count", summaryColumn, "", 28, false, TextAnchor.MiddleLeft);
                Input(summary.rectTransform, -1, 40);
                SemanticCounter(summary);
                Space(summaryColumn, "Hint Spacing", 10);
                var hint = Label("Selection Hint", summaryColumn, "点击选择或取消；详情可放大查看", 22, false, TextAnchor.MiddleLeft);
                hint.color = new Color32(201,184,166,255);
                Input(hint.rectTransform, -1, 30);
                Input(Node("Action Spacing", footer), 72, -1);
                var secondary = Action("Secondary Action", footer, false);
                Input(Node("Button Spacing", footer), 24, -1);
                var primary = Action("Primary Action", footer, true);
                Input(Node("Footer End Spacing", footer), 6, -1);
                var templates = Node("Templates", root);
                Input(templates, 0, 0).ignoreLayout = true;
                var card = CardTemplate(templates);
                templates.gameObject.SetActive(false);
                Set(shell, "layoutProfile", AssetDatabase.LoadAssetAtPath<EffectDialogLayoutProfile>(
                    "Assets/YC/Presentation/Content/EffectDialogLayoutProfile.asset"));
                Set(shell, "overlayCanvas", canvas); Set(shell, "overlayImage", shade);
                Set(shell, "panel", panel); Set(shell, "expandedContent", content);
                Set(shell, "titleText", title); Set(shell, "descriptionText", description);
                Set(shell, "optionScroll", scroll); Set(shell, "optionContent", row);
                Set(shell, "resourceSummaryText", summary); Set(shell, "footer", footer);
                Set(shell, "facilityCardTemplate", card); Set(shell, "cardPickerRow", rowLayout);
                Set(shell, "cardPickerHint", hint);
                var serialized = new SerializedObject(shell);
                serialized.FindProperty("cardPickerSelectionHint").stringValue = hint.text;
                serialized.FindProperty("cardPickerReadOnlyHint").stringValue = "仅供查看；详情可放大卡牌";
                var actions = serialized.FindProperty("actionButtons");
                actions.arraySize = 2;
                actions.GetArrayElementAtIndex(0).objectReferenceValue = primary;
                actions.GetArrayElementAtIndex(1).objectReferenceValue = secondary;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                if (!shell.TryValidateConfiguration(out var reason)) throw new InvalidOperationException(reason);
                var saved = PrefabUtility.SaveAsPrefabAsset(root.gameObject, Prefab);
                var savedShell = saved.GetComponent<EffectDialogShellView>();
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(savedShell, out string guid, out long localId);
                const string hudPath = "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
                var yaml = File.ReadAllText(hudPath);
                var field = "  cardPickerPrefab: {fileID: " + localId + ", guid: " + guid + ", type: 3}";
                if (yaml.Contains("  cardPickerPrefab:"))
                    yaml = System.Text.RegularExpressions.Regex.Replace(yaml, @"(?m)^  cardPickerPrefab:.*$", field);
                else
                    yaml = System.Text.RegularExpressions.Regex.Replace(yaml, @"(?m)^(  effectDialogShellPrefab:[^\r\n]*)", "$1\n" + field);
                File.WriteAllText(hudPath, yaml);
                AssetDatabase.ImportAsset(hudPath);
                Debug.Log("CardPicker资产搭建完成：" + Prefab);
            }
            finally { UnityEngine.Object.DestroyImmediate(root.gameObject); }
        }

        private static RectTransform Node(string name, Transform parent)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.gameObject.layer = 5; rect.SetParent(parent, false);
            rect.sizeDelta = Vector2.zero;
            return rect;
        }
        private static void Stretch(RectTransform r)
        { r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = r.offsetMax = Vector2.zero; }
        private static LayoutElement Input(RectTransform r, float width, float height)
        {
            var input = r.GetComponent<LayoutElement>() ?? r.gameObject.AddComponent<LayoutElement>();
            input.minWidth = input.minHeight = 0;
            input.preferredWidth = width; input.preferredHeight = height;
            input.flexibleWidth = input.flexibleHeight = 0;
            return input;
        }
        private static VerticalLayoutGroup Vertical(RectTransform r, RectOffset padding)
        {
            var layout = r.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = padding; layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            return layout;
        }
        private static HorizontalLayoutGroup Horizontal(RectTransform r, float spacing)
        {
            var layout = r.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing; layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = false; layout.childForceExpandHeight = true;
            return layout;
        }
        private static void Space(RectTransform parent, string name, float height) => Input(Node(name,parent), -1,height);
        private static Text Label(string name, Transform parent, string value, int size, bool emphasis, TextAnchor align)
        {
            var label = Node(name, parent).gameObject.AddComponent<Text>();
            label.font = emphasis ? fonts.Emphasis : fonts.Regular;
            label.fontStyle = FontStyle.Normal; label.fontSize = size; label.text = value;
            label.color = Paper; label.alignment = align; label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Wrap; label.verticalOverflow = VerticalWrapMode.Truncate;
            return label;
        }
        private static RawImage Raw(string name, Transform parent, string path)
        {
            var image = Node(name, parent).gameObject.AddComponent<RawImage>();
            image.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (image.texture == null) throw new InvalidOperationException("缺少原图 " + path);
            image.raycastTarget = false;
            return image;
        }
        private static void SemanticCounter(Text source)
        {
            var host = Node("Counter Text Runs",source.transform); Stretch(host);
            var row = Horizontal(host,0); row.childAlignment = TextAnchor.MiddleLeft;
            row.childForceExpandHeight = false;
            var template = Label("Counter Fragment Template",source.transform,"",source.fontSize,false,TextAnchor.MiddleLeft);
            template.gameObject.SetActive(false);
            var semantic = source.gameObject.AddComponent<UiSemanticLabel>();
            Set(semantic,"host",host); Set(semantic,"fragmentTemplate",template); Set(semantic,"fonts",fonts);
            var data = new SerializedObject(semantic); data.FindProperty("ink").colorValue = Paper;
            data.ApplyModifiedPropertiesWithoutUndo();
        }
        private static Rect SourceUv(Texture texture, Rect topLeft)
        { return new Rect(topLeft.x/texture.width, 1-(topLeft.y+topLeft.height)/texture.height,
            topLeft.width/texture.width, topLeft.height/texture.height); }
        private static EffectDialogActionButtonView Action(string name, RectTransform parent, bool primary)
        {
            var slot = Node(name + " Slot", parent);
            var slotInput = Input(slot,280,84); slotInput.minWidth = 200; slotInput.minHeight = 64;
            var rect = Node(name,slot); Stretch(rect);
            var background = rect.gameObject.AddComponent<Image>(); background.color = Color.clear;
            background.canvasRenderer.cullTransparentMesh = false;
            var artwork = Raw("Button Artwork",rect,"Assets/YC/Presentation/Enterprise/Artwork/selection-button-"+
                (primary ? "primary" : "secondary")+".png");
            Stretch(artwork.rectTransform);
            artwork.uvRect = SourceUv(artwork.texture,new Rect(35,102,1916,582));
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = artwork;
            button.colors = Colors();
            var label = Label("Label",rect,primary ? "确认选择" : "返回",28,true,TextAnchor.MiddleCenter);
            Stretch(label.rectTransform); label.rectTransform.offsetMin = new Vector2(28,24);
            label.rectTransform.offsetMax = new Vector2(-28,-24);
            label.color = primary ? new Color32(36,28,19,255) : Paper;
            var view = rect.gameObject.AddComponent<EffectDialogActionButtonView>();
            Set(view,"button",button); Set(view,"background",background); Set(view,"label",label);
            return view;
        }
        private static ColorBlock Colors()
        {
            var colors = ColorBlock.defaultColorBlock;
            colors.highlightedColor = new Color(1.08f,1.08f,1.08f);
            colors.pressedColor = new Color(.78f,.78f,.78f);
            colors.disabledColor = new Color(.42f,.42f,.42f,.65f);
            return colors;
        }
        private static Scrollbar Scrollbar(RectTransform parent)
        {
            var rect = Node("Horizontal Scrollbar",parent);
            rect.anchorMin = new Vector2(0,0); rect.anchorMax = new Vector2(1,0);
            rect.pivot = new Vector2(.5f,0); rect.offsetMin = new Vector2(24,2); rect.offsetMax = new Vector2(-24,42);
            var hit = rect.gameObject.AddComponent<Image>(); hit.color = Color.clear;
            hit.canvasRenderer.cullTransparentMesh = false;
            var track = Raw("Track Artwork",rect,Artwork+"scrollbar-track.png");
            Stretch(track.rectTransform); track.rectTransform.offsetMin = new Vector2(0,12);
            track.rectTransform.offsetMax = new Vector2(0,-12);
            track.uvRect = SourceUv(track.texture,new Rect(82,333,2008,50));
            var slide = Node("Sliding Area",rect); Stretch(slide);
            var handle = Node("Handle",slide); Stretch(handle);
            var thumbHit = handle.gameObject.AddComponent<Image>(); thumbHit.color = Color.clear;
            thumbHit.canvasRenderer.cullTransparentMesh = false;
            var thumb = Raw("Thumb Artwork",handle,Artwork+"scrollbar-thumb.png");
            Stretch(thumb.rectTransform); thumb.rectTransform.offsetMin = new Vector2(0,14);
            thumb.rectTransform.offsetMax = new Vector2(0,-14);
            thumb.uvRect = SourceUv(thumb.texture,new Rect(92,326,1988,71));
            var bar = rect.gameObject.AddComponent<Scrollbar>();
            bar.direction = UnityEngine.UI.Scrollbar.Direction.LeftToRight;
            bar.handleRect = handle; bar.targetGraphic = thumb;
            bar.colors = Colors();
            return bar;
        }
        private static FacilityEffectCardView CardTemplate(Transform parent)
        {
            var item = Node("Card Template",parent);
            Input(item,212,311);
            var layout = Vertical(item,new RectOffset(16,16,28,28));
            layout.childForceExpandWidth = false; layout.childAlignment = TextAnchor.MiddleCenter;
            var face = Node("Card Face",item); Input(face,180,255);
            var clickImage = face.gameObject.AddComponent<Image>(); clickImage.color = Color.clear;
            clickImage.canvasRenderer.cullTransparentMesh = false;
            var button = face.gameObject.AddComponent<Button>(); button.colors = Colors();
            var slot = Node("Card Slot",face).gameObject.AddComponent<Image>();
            slot.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(Artwork+"card-slot.png");
            slot.raycastTarget = false; Map(slot.rectTransform,1053,1493,new Rect(118,130,817,1233));
            button.targetGraphic = slot;
            var art = Node("Artwork",face).gameObject.AddComponent<RawImage>(); Stretch(art.rectTransform); art.raycastTarget = false;
            var fallback = Label("Missing Artwork Label",face,"",24,false,TextAnchor.MiddleCenter); Stretch(fallback.rectTransform);
            var selected = Node("Selected Frame",face).gameObject.AddComponent<Image>();
            selected.sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/YC/Presentation/Layout/card-selected.png");
            selected.raycastTarget = false; selected.enabled = false;
            Map(selected.rectTransform,1054,1493,new Rect(80,130,896,1280));
            var outline = face.gameObject.AddComponent<Outline>(); outline.enabled = false;
            var pointer = face.gameObject.AddComponent<CardPointerInteraction>();
            var group = item.gameObject.AddComponent<CanvasGroup>();
            var details = Node("Details",item);
            Input(details,-1,24).ignoreLayout = true;
            details.anchorMin = new Vector2(0,0); details.anchorMax = new Vector2(1,0);
            details.pivot = new Vector2(.5f,0); details.offsetMin = new Vector2(16,0); details.offsetMax = new Vector2(-16,24);
            var detailHit = details.gameObject.AddComponent<Image>(); detailHit.color = new Color(0,0,0,.18f);
            var detailButton = details.gameObject.AddComponent<Button>(); detailButton.targetGraphic = detailHit;
            var detailLabel = Label("Label",details,"详情",20,false,TextAnchor.MiddleCenter); Stretch(detailLabel.rectTransform);
            var view = item.gameObject.AddComponent<FacilityEffectCardView>();
            Set(view,"cardRect",item); Set(view,"background",slot); Set(view,"button",button);
            Set(view,"outline",outline); Set(view,"canvasGroup",group); Set(view,"cardImage",art);
            Set(view,"fallbackLabel",fallback); Set(view,"pointerInteraction",pointer);
            Set(view,"detailsButton",detailButton); Set(view,"selectionImage",selected);
            return view;
        }
        private static void Map(RectTransform r,float width,float height,Rect source)
        {
            r.anchorMin = new Vector2(-source.x/source.width,-(height-source.y-source.height)/source.height);
            r.anchorMax = new Vector2((width-source.x)/source.width,1+source.y/source.height);
            r.offsetMin = r.offsetMax = Vector2.zero;
        }
        private static void Set(UnityEngine.Object target,string property,UnityEngine.Object value)
        {
            var data = new SerializedObject(target); var field = data.FindProperty(property);
            if(field == null) throw new InvalidOperationException(target.GetType().Name+" 缺少字段 "+property);
            field.objectReferenceValue = value; data.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
