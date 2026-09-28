using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation.Editor
{
    // Manual, UI-002-only creation of new assets. Never invoked by tests or builds.
    public static class Ui002AssetBuilder
    {
        private const string Root = "Assets/YC/Presentation/Ui002";
        private const string SpriteRoot = Root + "/Sprites";
        private const string FontRoot = Root + "/Fonts";
        private const string ContentRoot = Root + "/Content";
        private const string PrefabRoot = Root + "/Prefabs";
        private const string Manifest = "tools/ui002_sprite_manifest.json";

        [Serializable] private sealed class SpriteManifest { public SpriteRow[] sprites; }
        [Serializable] private sealed class SpriteRow
        {
            public string id;
            public string path;
            public string source;
            public string sha256;
            public int[] border;
            public int[] size;
        }

        public static void BuildNewAssets()
        {
            Directory.CreateDirectory(ContentRoot);
            Directory.CreateDirectory(PrefabRoot);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var manifest = JsonUtility.FromJson<SpriteManifest>(File.ReadAllText(Manifest));
            if (manifest == null || manifest.sprites == null || manifest.sprites.Length != 35)
                throw new InvalidOperationException("UI-002 source manifest is incomplete.");
            foreach (var row in manifest.sprites) ImportSprite(row);
            ImportLegacySprites();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var fonts = CreateFontRoles();
            var theme = CreateTheme();
            CreateFrame(theme);
            CreateButton(theme, fonts);
            CreateCardSlot(theme);
            CreateScrollView(theme);
            CreateMixedLine(fonts);
            CreateStatusRow(theme, fonts);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            VerifyAssets();
        }

        public static void VerifyAssets()
        {
            var manifest = JsonUtility.FromJson<SpriteManifest>(File.ReadAllText(Manifest));
            var guids = new HashSet<string>();
            foreach (var row in manifest.sprites)
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(row.path);
                if (sprite == null) throw new InvalidOperationException("Missing Sprite: " + row.path);
                var border = sprite.border;
                if (border != new Vector4(row.border[0], row.border[1], row.border[2], row.border[3]))
                    throw new InvalidOperationException("Wrong 9-slice border: " + row.id + " " + border);
                if (row.size == null || sprite.rect.width != row.size[0] || sprite.rect.height != row.size[1])
                    throw new InvalidOperationException("Sprite imported at wrong size: " + row.id + " " + sprite.rect);
                var guid = AssetDatabase.AssetPathToGUID(row.path);
                if (string.IsNullOrEmpty(guid) || !guids.Add(guid))
                    throw new InvalidOperationException("Missing or duplicate GUID: " + row.path);
                Debug.Log("UI002 SPRITE " + row.id + " " + guid + " " + border);
            }
            var roles = AssetDatabase.LoadAssetAtPath<UiFontRoles>(ContentRoot + "/UiFontRoles.asset");
            var reason = roles == null ? "Missing asset" : string.Empty;
            if (roles == null || !roles.TryValidate(out reason))
                throw new InvalidOperationException("Font roles invalid: " + reason);
            var settings = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab");
            var driver = settings == null ? null : settings.GetComponentInChildren<FontRefreshDriver>(true);
            if (driver == null || driver.RoleFonts != roles || !driver.TryValidateConfiguration(out reason))
                throw new InvalidOperationException("GameSettingsMenu font driver does not hold all roles: " + reason);
            if (roles.Emphasis.HasCharacter('策') == false || roles.SpecialWord.HasCharacter('计') == false ||
                roles.EffectNumber.HasCharacter('2') == false || roles.UiNumber.HasCharacter('3') == false)
                throw new InvalidOperationException("One of the required font members is missing a sample glyph.");
            var button = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabRoot + "/SharedButton.prefab");
            var buttonState = button == null ? null : button.GetComponent<UiSharedButtonState>();
            if (buttonState == null || button.GetComponent<Button>() == null ||
                button.GetComponent<Image>().raycastTarget == false)
                throw new InvalidOperationException("SharedButton hit target or states missing.");
            var buttonData = new SerializedObject(buttonState);
            foreach (var field in new[] { "theme", "face", "stateLayer" })
                if (buttonData.FindProperty(field).objectReferenceValue == null)
                    throw new InvalidOperationException("SharedButton state reference missing: " + field);
            foreach (var image in button.GetComponentsInChildren<Image>(true))
                if (image.gameObject != button && image.raycastTarget)
                    throw new InvalidOperationException("Decorative button layer intercepts clicks: " + image.name);
            var buttonInstance = UnityEngine.Object.Instantiate(button);
            try
            {
                var instanceState = buttonInstance.GetComponent<UiSharedButtonState>();
                instanceState.SetPending(true);
                if (buttonInstance.GetComponent<Button>().interactable)
                    throw new InvalidOperationException("Pending button still accepts clicks.");
                instanceState.SetPending(false);
                if (!buttonInstance.GetComponent<Button>().interactable)
                    throw new InvalidOperationException("Button did not recover its prior state.");
            }
            finally { UnityEngine.Object.DestroyImmediate(buttonInstance); }
            var rowPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabRoot + "/StatusRow.prefab");
            var rowState = rowPrefab == null ? null : rowPrefab.GetComponent<UiSelectionState>();
            if (rowState == null) throw new InvalidOperationException("StatusRow state component missing.");
            var rowData = new SerializedObject(rowState);
            foreach (var field in new[] { "stateFrame", "stateIcon", "availableFrame", "selectedFrame",
                         "unavailableFrame", "selectedIcon", "unavailableIcon" })
                if (rowData.FindProperty(field).objectReferenceValue == null)
                    throw new InvalidOperationException("StatusRow state reference missing: " + field);
            var scrollPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabRoot + "/ScrollView.prefab");
            var scroll = scrollPrefab == null ? null : scrollPrefab.GetComponent<ScrollRect>();
            if (scroll == null || scroll.viewport == null || scroll.content == null ||
                scroll.verticalScrollbar == null || scroll.verticalScrollbar.handleRect == null)
                throw new InvalidOperationException("ScrollView references are incomplete.");
            if (scroll.verticalScrollbar.GetComponent<Image>().type != UnityEngine.UI.Image.Type.Sliced ||
                scroll.verticalScrollbar.handleRect.GetComponent<Image>().type != UnityEngine.UI.Image.Type.Sliced)
                throw new InvalidOperationException("Legacy scrollbar caps would stretch.");
            var cardPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabRoot + "/CardSlot.prefab");
            var cardView = cardPrefab == null ? null : cardPrefab.GetComponent<UiCardSlotView>();
            var cardArt = cardPrefab == null ? null : cardPrefab.transform.Find("CardArtwork").GetComponent<Image>();
            if (cardView == null || cardArt == null || !cardArt.preserveAspect || cardArt.enabled)
                throw new InvalidOperationException("CardSlot art must start empty and preserve aspect.");
            foreach (var label in AssetDatabase.LoadAssetAtPath<GameObject>(PrefabRoot + "/MixedLine.prefab")
                         .GetComponentsInChildren<Text>(true))
                if (label.fontStyle != FontStyle.Normal || label.text.Contains("<b>"))
                    throw new InvalidOperationException("MixedLine uses synthetic bold: " + label.name);
            foreach (var name in new[] { "SharedFrame", "SharedButton", "CardSlot", "ScrollView", "MixedLine", "StatusRow" })
            {
                var path = PrefabRoot + "/" + name + ".prefab";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                    throw new InvalidOperationException("Missing shared prefab: " + path);
                Debug.Log("UI002 PREFAB " + path + " " + AssetDatabase.AssetPathToGUID(path));
            }
            Debug.Log("UI002 VERIFIED: 35 indexed sprites, five font roles, six prefabs.");
        }

        public static void RefineNewAssets()
        {
            ImportLegacySprites();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            RefinePrefab("CardSlot", root =>
            {
                var artwork = root.transform.Find("CardArtwork").GetComponent<Image>();
                artwork.enabled = false;
                var selected = root.transform.Find("Selected").GetComponent<Image>();
                selected.sprite = Sprite("card-frame");
                selected.type = UnityEngine.UI.Image.Type.Sliced;
                selected.preserveAspect = false;
                selected.color = new Color(1f, 0.77f, 0.25f, 0.9f);
                var cardView = root.GetComponent<UiCardSlotView>();
                if (cardView == null) cardView = root.AddComponent<UiCardSlotView>();
                var serialized = new SerializedObject(cardView);
                serialized.FindProperty("artwork").objectReferenceValue = artwork;
                serialized.FindProperty("selectedFrame").objectReferenceValue = selected;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            });
            RefinePrefab("ScrollView", root =>
            {
                var bar = root.transform.Find("Scrollbar").GetComponent<Image>();
                bar.type = UnityEngine.UI.Image.Type.Sliced;
                var handle = root.transform.Find("Scrollbar/Handle").GetComponent<Image>();
                handle.type = UnityEngine.UI.Image.Type.Sliced;
            });
            RefinePrefab("StatusRow", root =>
            {
                var selected = root.transform.Find("StateFrame")?.GetComponent<Image>() ??
                               root.transform.Find("Selected").GetComponent<Image>();
                selected.gameObject.name = "StateFrame";
                selected.enabled = true;
                selected.preserveAspect = false;
                selected.type = UnityEngine.UI.Image.Type.Sliced;
                var icon = root.transform.Find("StateIcon")?.GetComponent<Image>() ??
                    Image("StateIcon", root.transform,
                        AssetDatabase.LoadAssetAtPath<Sprite>(SpriteRoot + "/SelectionStates/icon-check.png"));
                var iconRect = (RectTransform)icon.transform;
                iconRect.anchorMin = iconRect.anchorMax = new Vector2(1, 0.5f);
                iconRect.sizeDelta = new Vector2(24, 24);
                iconRect.anchoredPosition = new Vector2(-18, 0);
                var state = root.GetComponent<UiSelectionState>();
                if (state == null) state = root.AddComponent<UiSelectionState>();
                var serialized = new SerializedObject(state);
                serialized.FindProperty("stateFrame").objectReferenceValue = selected;
                serialized.FindProperty("stateIcon").objectReferenceValue = icon;
                serialized.FindProperty("availableFrame").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<Sprite>(SpriteRoot + "/SelectionStates/frame-available.png");
                serialized.FindProperty("selectedFrame").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<Sprite>(SpriteRoot + "/SelectionStates/frame-selected.png");
                serialized.FindProperty("unavailableFrame").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<Sprite>(SpriteRoot + "/SelectionStates/frame-unavailable.png");
                serialized.FindProperty("selectedIcon").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<Sprite>(SpriteRoot + "/SelectionStates/icon-check.png");
                serialized.FindProperty("unavailableIcon").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<Sprite>(SpriteRoot + "/SelectionStates/icon-lock.png");
                serialized.ApplyModifiedPropertiesWithoutUndo();
                state.Refresh();
            });
            RefinePrefab("MixedLine", root =>
            {
                if (root.transform.Find("SpecialWord2") != null) return;
                var fonts = AssetDatabase.LoadAssetAtPath<UiFontRoles>(ContentRoot + "/UiFontRoles.asset");
                var special = Text("SpecialWord2", root.transform, fonts.SpecialWord, "计谋", 24);
                special.alignment = TextAnchor.MiddleLeft;
                AddSemantic(special, fonts, UiFontRole.SpecialWord);
                special.transform.SetSiblingIndex(6);
                var prefix = Text("RoundPrefix", root.transform, fonts.Regular, "第", 24);
                prefix.alignment = TextAnchor.MiddleLeft;
                AddSemantic(prefix, fonts, UiFontRole.Regular);
                prefix.transform.SetSiblingIndex(root.transform.Find("UiRoundNumber").GetSiblingIndex());
                var suffix = Text("RoundSuffix", root.transform, fonts.Regular, "回合", 24);
                suffix.alignment = TextAnchor.MiddleLeft;
                AddSemantic(suffix, fonts, UiFontRole.Regular);
            });
            RefinePrefab("StatusRow", root =>
            {
                root.transform.Find("Label").GetComponent<Text>().color = new Color(0.13f, 0.14f, 0.14f);
            });
            RefinePrefab("MixedLine", root =>
            {
                root.transform.Find("ResourceIcon").GetComponent<Image>().color = new Color(0.95f, 0.76f, 0.31f);
                root.transform.Find("Segment0").GetComponent<Text>().text = "选择 ";
                root.transform.Find("Segment2").GetComponent<Text>().text = " 次，使用 ";
                root.transform.Find("Segment4").GetComponent<Text>().text = " 获得 ";
                var special = root.transform.Find("SpecialWord2");
                special.GetComponent<Text>().text = "／计谋";
                special.SetSiblingIndex(4);
                root.transform.Find("RoundPrefix").GetComponent<Text>().text = "；第 ";
                root.transform.Find("RoundSuffix").GetComponent<Text>().text = " 回合";
            });
            AssetDatabase.SaveAssets();
            VerifyAssets();
        }

        private static void RefinePrefab(string name, Action<GameObject> refine)
        {
            var path = PrefabRoot + "/" + name + ".prefab";
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                refine(root);
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static void ImportSprite(SpriteRow row)
        {
            var source = Path.Combine("游城拓荒/UI素材/整理版-2026-09-24", row.source);
            using (var sha = SHA256.Create())
            using (var file = File.OpenRead(source))
            {
                var hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                if (hash != row.sha256) throw new InvalidOperationException("Source hash changed: " + row.id);
            }
            var importer = AssetImporter.GetAtPath(row.path) as TextureImporter;
            if (importer == null) throw new InvalidOperationException("No importer: " + row.path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 100f;
            importer.spriteBorder = new Vector4(row.border[0], row.border[1], row.border[2], row.border[3]);
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = row.id == "topbar-bg" || row.id == "topbar-frame" ||
                                      row.id == "bottom-bar" || row.id == "bottom-frame" ? 2048 : 1024;
            importer.SaveAndReimport();
        }

        private static void ImportLegacySprites()
        {
            foreach (var directory in new[] { SpriteRoot + "/LegacyControls", SpriteRoot + "/SelectionStates" })
            foreach (var file in Directory.GetFiles(directory, "*.png"))
            {
                var path = file.Replace('\\', '/');
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) throw new InvalidOperationException("No importer: " + path);
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                var name = Path.GetFileNameWithoutExtension(path);
                importer.spriteBorder = name.StartsWith("scrollbar-track-v") || name.StartsWith("scrollbar-thumb-v")
                    ? new Vector4(0, 8, 0, 8)
                    : name.StartsWith("scrollbar-track-h") || name.StartsWith("scrollbar-thumb-h")
                        ? new Vector4(8, 0, 8, 0)
                        : name.StartsWith("stepper-") ? new Vector4(18, 18, 18, 18)
                        : name.StartsWith("tab-") || name.StartsWith("frame-")
                            ? new Vector4(20, 20, 20, 20) : Vector4.zero;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.filterMode = FilterMode.Bilinear;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.maxTextureSize = 2048;
                importer.SaveAndReimport();
            }
        }

        private static UiFontRoles CreateFontRoles()
        {
            var path = ContentRoot + "/UiFontRoles.asset";
            var existing = AssetDatabase.LoadAssetAtPath<UiFontRoles>(path);
            if (existing != null) return existing;
            var roles = ScriptableObject.CreateInstance<UiFontRoles>();
            AssetDatabase.CreateAsset(roles, path);
            var serialized = new SerializedObject(roles);
            Set(serialized, "regular", FontRoot + "/FangZhengHeiTiJianTi-1.ttf");
            Set(serialized, "emphasis", FontRoot + "/HanYiCuHeiJian-1.ttf");
            Set(serialized, "specialWord", FontRoot + "/MicrosoftYaHei-Regular-face0.ttf");
            Set(serialized, "effectNumber", FontRoot + "/Novecento NarrowBold.otf");
            Set(serialized, "uiNumber", FontRoot + "/Novecento wide Normal Regular.ttf");
            Set(serialized, "missingGlyphFallback", "Assets/YC/Resources/Fonts/CJK/NotoSansCJKsc-Regular.otf");
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(roles);
            if (!roles.TryValidate(out var reason)) throw new InvalidOperationException(reason);
            return roles;
        }

        private static void Set(SerializedObject target, string property, string fontPath)
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(fontPath);
            if (font == null) throw new InvalidOperationException("Font failed to import: " + fontPath);
            target.FindProperty(property).objectReferenceValue = font;
        }

        private static UiSharedComponentTheme CreateTheme()
        {
            var path = ContentRoot + "/UiSharedComponentTheme.asset";
            var existing = AssetDatabase.LoadAssetAtPath<UiSharedComponentTheme>(path);
            if (existing != null) return existing;
            var theme = ScriptableObject.CreateInstance<UiSharedComponentTheme>();
            theme.outerFrame = Sprite("outer-frame");
            theme.moduleBackground = Sprite("module-bg");
            theme.moduleFrame = Sprite("module-frame");
            theme.paperBackground = Sprite("paper-bg");
            theme.headerBackground = Sprite("header-bg");
            theme.drawerFrame = Sprite("drawer-frame");
            theme.cardBackground = Sprite("card-bg");
            theme.cardFrame = Sprite("card-frame");
            theme.primaryButton = Sprite("button-primary");
            theme.secondaryButton = Sprite("button-secondary");
            theme.disabledButton = Sprite("button-disabled");
            theme.selectedOverlay = AssetDatabase.LoadAssetAtPath<Sprite>(SpriteRoot + "/SelectionStates/frame-selected.png");
            theme.scrollbarTrack = AssetDatabase.LoadAssetAtPath<Sprite>(SpriteRoot + "/LegacyControls/scrollbar-track-v.png");
            theme.scrollbarThumb = AssetDatabase.LoadAssetAtPath<Sprite>(SpriteRoot + "/LegacyControls/scrollbar-thumb-v.png");
            AssetDatabase.CreateAsset(theme, path);
            return theme;
        }

        private static Sprite Sprite(string name)
        {
            var path = SpriteRoot + "/Frontier31/" + name + ".png";
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null) throw new InvalidOperationException("Missing sprite " + path);
            return sprite;
        }

        private static GameObject UiObject(string name, Transform parent = null)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            if (parent != null) go.transform.SetParent(parent, false);
            return go;
        }

        private static RectTransform Stretch(GameObject go, Vector4 inset)
        {
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset.x, inset.w);
            rect.offsetMax = new Vector2(-inset.z, -inset.y);
            return rect;
        }

        private static Image Image(string name, Transform parent, Sprite sprite, bool sliced = false)
        {
            var go = UiObject(name, parent);
            Stretch(go, Vector4.zero);
            go.AddComponent<CanvasRenderer>();
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.type = sliced ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
            image.raycastTarget = false;
            return image;
        }

        private static Text Text(string name, Transform parent, Font font, string value, int size)
        {
            var go = UiObject(name, parent);
            go.AddComponent<CanvasRenderer>();
            var label = go.AddComponent<Text>();
            label.font = font;
            label.fontStyle = FontStyle.Normal;
            label.text = value;
            label.fontSize = size;
            label.color = new Color(0.95f, 0.91f, 0.81f);
            label.alignment = TextAnchor.MiddleCenter;
            label.raycastTarget = false;
            label.supportRichText = false;
            return label;
        }

        private static void Layout(GameObject go, Vector2 min, Vector2 preferred)
        {
            var layout = go.AddComponent<LayoutElement>();
            layout.minWidth = min.x;
            layout.minHeight = min.y;
            layout.preferredWidth = preferred.x;
            layout.preferredHeight = preferred.y;
            ((RectTransform)go.transform).sizeDelta = preferred;
        }

        private static void SaveNew(GameObject go, string name)
        {
            var path = PrefabRoot + "/" + name + ".prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
                throw new InvalidOperationException("Refusing to overwrite existing prefab: " + path);
            PrefabUtility.SaveAsPrefabAsset(go, path);
            UnityEngine.Object.DestroyImmediate(go);
        }

        private static void CreateFrame(UiSharedComponentTheme theme)
        {
            if (Exists("SharedFrame")) return;
            var root = UiObject("SharedFrame");
            Layout(root, theme.frameMinimum, theme.framePreferred);
            Image("Fill", root.transform, theme.moduleBackground);
            Image("Frame", root.transform, theme.outerFrame, true);
            var content = UiObject("Content", root.transform);
            Stretch(content, theme.frameContentInset);
            SaveNew(root, "SharedFrame");
        }

        private static void CreateButton(UiSharedComponentTheme theme, UiFontRoles fonts)
        {
            if (Exists("SharedButton")) return;
            var root = UiObject("SharedButton");
            Layout(root, theme.buttonMinimum, theme.buttonPreferred);
            root.AddComponent<CanvasRenderer>();
            var hit = root.AddComponent<Image>();
            hit.color = Color.clear;
            hit.raycastTarget = true;
            var button = root.AddComponent<Button>();
            button.targetGraphic = hit;
            button.transition = Selectable.Transition.None;
            var face = Image("Face", root.transform, theme.primaryButton, true);
            var state = Image("StateLayer", root.transform, null);
            state.color = Color.clear;
            var label = Text("Label", root.transform, fonts.Emphasis, "确认", 22);
            label.alignment = TextAnchor.MiddleCenter;
            Stretch(label.gameObject, theme.buttonContentInset);
            var visuals = root.AddComponent<UiSharedButtonState>();
            var serialized = new SerializedObject(visuals);
            serialized.FindProperty("theme").objectReferenceValue = theme;
            serialized.FindProperty("face").objectReferenceValue = face;
            serialized.FindProperty("stateLayer").objectReferenceValue = state;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            AddSemantic(label, fonts, UiFontRole.Emphasis);
            SaveNew(root, "SharedButton");
        }

        private static void CreateCardSlot(UiSharedComponentTheme theme)
        {
            if (Exists("CardSlot")) return;
            var root = UiObject("CardSlot");
            Layout(root, new Vector2(96, 137), theme.cardSlotPreferred);
            Image("Background", root.transform, theme.cardBackground);
            var artwork = Image("CardArtwork", root.transform, null);
            Stretch(artwork.gameObject, new Vector4(8, 8, 8, 8));
            artwork.enabled = false;
            artwork.preserveAspect = true;
            Image("Frame", root.transform, theme.cardFrame, true);
            var selected = Image("Selected", root.transform, theme.selectedOverlay);
            selected.enabled = false;
            selected.sprite = theme.cardFrame;
            selected.type = UnityEngine.UI.Image.Type.Sliced;
            selected.color = new Color(1f, 0.77f, 0.25f, 0.9f);
            SaveNew(root, "CardSlot");
        }

        private static void CreateScrollView(UiSharedComponentTheme theme)
        {
            if (Exists("ScrollView")) return;
            var root = UiObject("ScrollView");
            Layout(root, new Vector2(180, 120), new Vector2(360, 280));
            var scroll = root.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            var viewport = UiObject("Viewport", root.transform);
            Stretch(viewport, new Vector4(0, 0, theme.scrollbarSize.x + 4, 0));
            viewport.AddComponent<CanvasRenderer>();
            var viewportImage = viewport.AddComponent<Image>();
            viewportImage.color = Color.clear;
            viewportImage.raycastTarget = true;
            viewport.AddComponent<RectMask2D>();
            var content = UiObject("Content", viewport.transform);
            var contentRect = Stretch(content, Vector4.zero);
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = Vector2.one;
            contentRect.pivot = new Vector2(0.5f, 1);
            scroll.viewport = (RectTransform)viewport.transform;
            scroll.content = contentRect;

            var barRoot = UiObject("Scrollbar", root.transform);
            var barRect = Stretch(barRoot, Vector4.zero);
            barRect.anchorMin = new Vector2(1, 0);
            barRect.anchorMax = Vector2.one;
            barRect.sizeDelta = new Vector2(theme.scrollbarSize.x, 0);
            barRect.anchoredPosition = Vector2.zero;
            barRoot.AddComponent<CanvasRenderer>();
            var track = barRoot.AddComponent<Image>();
            track.sprite = theme.scrollbarTrack;
            track.type = UnityEngine.UI.Image.Type.Sliced;
            track.raycastTarget = true;
            var handle = Image("Handle", barRoot.transform, theme.scrollbarThumb);
            handle.type = UnityEngine.UI.Image.Type.Sliced;
            var handleRect = (RectTransform)handle.transform;
            handleRect.offsetMin = new Vector2(0, 0);
            handleRect.offsetMax = new Vector2(0, 0);
            var scrollbar = barRoot.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.targetGraphic = track;
            scrollbar.handleRect = handleRect;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            SaveNew(root, "ScrollView");
        }

        private static void CreateMixedLine(UiFontRoles fonts)
        {
            if (Exists("MixedLine")) return;
            var root = UiObject("MixedLine");
            Layout(root, new Vector2(180, 30), new Vector2(520, 32));
            root.AddComponent<UiFlowLayoutGroup>();
            var samples = new[] { "选择", "2", "次，使用", "策略", "获得", "+3" };
            var roles = new[] { UiFontRole.Regular, UiFontRole.EffectNumber, UiFontRole.Regular,
                UiFontRole.SpecialWord, UiFontRole.Regular, UiFontRole.EffectNumber };
            for (var i = 0; i < samples.Length; i++)
            {
                var label = Text("Segment" + i, root.transform, fonts.Get(roles[i]), samples[i], 24);
                label.alignment = TextAnchor.MiddleLeft;
                AddSemantic(label, fonts, roles[i]);
            }
            var icon = Image("ResourceIcon", root.transform, Sprite("icon-action"));
            ((RectTransform)icon.transform).sizeDelta = new Vector2(24, 24);
            var iconLayout = icon.gameObject.AddComponent<LayoutElement>();
            iconLayout.minWidth = iconLayout.preferredWidth = 24;
            iconLayout.minHeight = iconLayout.preferredHeight = 24;
            var round = Text("UiRoundNumber", root.transform, fonts.UiNumber, "第 3 回合", 24);
            round.text = "3";
            round.alignment = TextAnchor.MiddleLeft;
            AddSemantic(round, fonts, UiFontRole.UiNumber);
            SaveNew(root, "MixedLine");
        }

        private static void CreateStatusRow(UiSharedComponentTheme theme, UiFontRoles fonts)
        {
            if (Exists("StatusRow")) return;
            var root = UiObject("StatusRow");
            Layout(root, new Vector2(180, 56), new Vector2(340, 64));
            Image("Paper", root.transform, theme.paperBackground);
            var frame = Image("Frame", root.transform, theme.moduleFrame, true);
            frame.color = new Color(1, 1, 1, 0.65f);
            var selected = Image("Selected", root.transform, theme.selectedOverlay);
            selected.enabled = false;
            selected.preserveAspect = true;
            var label = Text("Label", root.transform, fonts.Regular, "可选择项目", 22);
            label.color = new Color(0.13f, 0.14f, 0.14f);
            label.alignment = TextAnchor.MiddleLeft;
            Stretch(label.gameObject, new Vector4(14, 4, 14, 4));
            AddSemantic(label, fonts, UiFontRole.Regular);
            SaveNew(root, "StatusRow");
        }

        private static void AddSemantic(Text label, UiFontRoles fonts, UiFontRole role)
        {
            var semantic = label.gameObject.AddComponent<UiSemanticText>();
            var serialized = new SerializedObject(semantic);
            serialized.FindProperty("fonts").objectReferenceValue = fonts;
            serialized.FindProperty("role").enumValueIndex = (int)role;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static bool Exists(string name) => AssetDatabase.LoadAssetAtPath<GameObject>(PrefabRoot + "/" + name + ".prefab") != null;
    }
}
