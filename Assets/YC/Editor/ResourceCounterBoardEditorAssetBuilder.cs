using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Rules;
using YC.Presentation;
using Object = UnityEngine.Object;

namespace YC.EditorTools
{
    public static class ResourceCounterBoardEditorAssetBuilder
    {
        private const float BoardScale = 1.31f;
        private const float BoardWidth = 432f;
        private const float BoardHeight = 140f;
        private const float BoardLeft = 15f;
        private const float AuxiliaryGap = 8f;
        private const float AuxiliaryHeight = 50f;
        private const float BoardTopBelowAuxiliaries = -58f;

        public const string PrefabPath =
            "Assets/YC/Presentation/Prefabs/Gameplay/ResourceCounterBoard.prefab";
        public const string LayoutProfilePath =
            "Assets/YC/Presentation/Content/ResourceCounterBoardLayoutProfile.asset";
        public const string VisualLibraryPath =
            "Assets/YC/Presentation/Sprites/ResourceCounterVisuals.asset";
        public const string NumberedGearSpritePath =
            "Assets/YC/Presentation/Sprites/ResourceCounterGear.png";

        private const string OriginiumIconPath = "Assets/YC/Data/ResourceIcons/源岩.png";
        private const string ShardIconPath = "Assets/YC/Data/ResourceIcons/源石碎片.png";
        private const string IronIconPath = "Assets/YC/Data/ResourceIcons/异铁.png";
        private const string PureIconPath = "Assets/YC/Data/ResourceIcons/至纯源石.png";

        [MenuItem("Tools/YC/Rebuild Resource Counter Board Editor Asset")]
        public static void Rebuild()
        {
            YC.Editor.UiThemeBuildReadiness.InitializeRequiredTheme();
            EnsureFolder("Assets/YC/Presentation/Prefabs/Gameplay");
            EnsureFolder("Assets/YC/Presentation/Content");
            EnsureFolder("Assets/YC/Presentation/Sprites");
            var profile = BuildOrUpdateProfile();
            var visuals = BuildOrUpdateVisualLibrary();
            var root = BuildPrefabContents(profile, visuals);
            try
            {
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(PrefabPath, ImportAssetOptions.ForceUpdate);
            Debug.Log("[ResourceCounterBoardEditorAssetBuilder] 已重建左上角齿轮资源卡板。");
        }

        public static ResourceCounterBoardLayoutProfile BuildOrUpdateProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<ResourceCounterBoardLayoutProfile>(LayoutProfilePath);
            if (profile == null)
            {
                if (AssetDatabase.LoadMainAssetAtPath(LayoutProfilePath) != null)
                    throw new InvalidOperationException("资源卡板布局 Profile 路径被不兼容资产占用。");
                profile = ScriptableObject.CreateInstance<ResourceCounterBoardLayoutProfile>();
                profile.name = "ResourceCounterBoardLayoutProfile";
                AssetDatabase.CreateAsset(profile, LayoutProfilePath);
            }

            profile.ConfigureForEditor(
                new SecondaryRectLayout
                {
                    AnchorMin = new Vector2(0f, 1f),
                    AnchorMax = new Vector2(0f, 1f),
                    Pivot = new Vector2(0f, 1f),
                    SizeDelta = new Vector2(596f, 250f),
                    AnchoredPosition = new Vector2(18f, -18f)
                },
                new Vector2(134f, 132f),
                new Vector2((BoardWidth * BoardScale - AuxiliaryGap) * 0.5f, AuxiliaryHeight),
                0.46f,
                0.32f,
                36f);
            if (!profile.TryValidateConfiguration(out var reason))
                throw new InvalidOperationException("资源卡板布局 Profile 无效：" + reason);
            EditorUtility.SetDirty(profile);
            return profile;
        }

        public static ResourceCounterVisualLibrary BuildOrUpdateVisualLibrary()
        {
            var library = AssetDatabase.LoadAssetAtPath<ResourceCounterVisualLibrary>(VisualLibraryPath);
            if (library == null)
            {
                if (AssetDatabase.LoadMainAssetAtPath(VisualLibraryPath) != null)
                    throw new InvalidOperationException("资源卡板视觉库路径被不兼容资产占用。");
                library = ScriptableObject.CreateInstance<ResourceCounterVisualLibrary>();
                library.name = "ResourceCounterVisuals";
                AssetDatabase.CreateAsset(library, VisualLibraryPath);
            }

            var assets = AssetDatabase.LoadAllAssetsAtPath(VisualLibraryPath);
            var largeGear = BuildSprite(assets, "Resource Large Gear", 128, PaintGear);
            assets = AssetDatabase.LoadAllAssetsAtPath(VisualLibraryPath);
            var smallGear = BuildSprite(assets, "Resource Small Gear", 64, PaintGear);
            var numberedGear = LoadReferenceGearSprite();
            assets = AssetDatabase.LoadAllAssetsAtPath(VisualLibraryPath);
            var dialCover = BuildSprite(assets, "Resource Dial Cover", 128, PaintDialCover);
            dialCover.texture.filterMode = FilterMode.Point;
            EditorUtility.SetDirty(dialCover.texture);
            assets = AssetDatabase.LoadAllAssetsAtPath(VisualLibraryPath);
            var readoutRing = BuildSprite(assets, "Resource Readout Ring", 64, PaintReadoutRing);
            readoutRing.texture.filterMode = FilterMode.Point;
            EditorUtility.SetDirty(readoutRing.texture);
            assets = AssetDatabase.LoadAllAssetsAtPath(VisualLibraryPath);
            var rivet = BuildSprite(assets, "Resource Rivet", 32, PaintRivet);
            assets = AssetDatabase.LoadAllAssetsAtPath(VisualLibraryPath);
            var banknote = BuildSprite(assets, "Resource Banknote", 96, PaintBanknote);

            SetReferences(
                library,
                ("largeGear", largeGear),
                ("smallGear", smallGear),
                ("numberedGear", numberedGear),
                ("dialCover", dialCover),
                ("readoutRing", readoutRing),
                ("rivet", rivet),
                ("banknote", banknote));
            EditorUtility.SetDirty(library);
            if (!library.TryValidateConfiguration(out var reason))
                throw new InvalidOperationException("资源卡板视觉库无效：" + reason);
            return library;
        }

        internal static GameObject BuildPrefabContents(
            ResourceCounterBoardLayoutProfile profile,
            ResourceCounterVisualLibrary visuals)
        {
            var layoutReason = string.Empty;
            var visualReason = string.Empty;
            if (profile == null || !profile.TryValidateConfiguration(out layoutReason))
                throw new InvalidOperationException("资源卡板缺少有效布局 Profile：" + layoutReason);
            if (visuals == null || !visuals.TryValidateConfiguration(out visualReason))
                throw new InvalidOperationException("资源卡板缺少有效视觉库：" + visualReason);

            var icons = new[]
            {
                LoadRequiredSprite(OriginiumIconPath),
                LoadRequiredSprite(ShardIconPath),
                LoadRequiredSprite(IronIconPath),
                LoadRequiredSprite(PureIconPath)
            };

            var root = CreateUiObject(
                "Resource Counter Board",
                null,
                typeof(Image),
                typeof(CanvasGroup),
                typeof(ResourceCounterBoard),
                typeof(ResourceCounterBoardView));
            var rootRect = root.GetComponent<RectTransform>();
            profile.RootLayout.ApplyTo(rootRect);
            var background = root.GetComponent<Image>();
            background.color = Color.clear;
            background.raycastTarget = false;
            var canvasGroup = root.GetComponent<CanvasGroup>();
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;

            var physicalBoard = CreateUiObject(
                "Rulebook Resource Counter",
                root.transform,
                typeof(Image));
            var physicalBoardRect = physicalBoard.GetComponent<RectTransform>();
            SetTopLeft(
                physicalBoardRect,
                new Vector2(BoardWidth, BoardHeight),
                new Vector2(BoardLeft, BoardTopBelowAuxiliaries));
            physicalBoardRect.localScale = Vector3.one * BoardScale;
            physicalBoard.GetComponent<Image>().color = new Color(0.105f, 0.085f, 0.07f, 0.985f);
            physicalBoard.GetComponent<Image>().raycastTarget = false;

            BuildRivet(physicalBoard.transform, visuals.Rivet, new Vector2(9f, -14f), new Color(0.32f, 0.25f, 0.18f, 1f));
            BuildRivet(physicalBoard.transform, visuals.Rivet, new Vector2(423f, -14f), new Color(0.32f, 0.25f, 0.18f, 1f));
            BuildRivet(physicalBoard.transform, visuals.Rivet, new Vector2(9f, -127f), new Color(0.32f, 0.25f, 0.18f, 1f));
            BuildRivet(physicalBoard.transform, visuals.Rivet, new Vector2(423f, -127f), new Color(0.32f, 0.25f, 0.18f, 1f));

            var gearTypes = new[]
            {
                ResourceType.Originium,
                ResourceType.OriginiumShard,
                ResourceType.Iron
            };
            var gearNames = new[] { "源岩", "源石", "异铁" };
            var gearCounters = new ResourceGearCounterView[3];
            for (var i = 0; i < gearCounters.Length; i++)
            {
                gearCounters[i] = BuildGearCounter(
                    physicalBoard.transform,
                    profile,
                    visuals,
                    gearTypes[i],
                    gearNames[i],
                    icons[i],
                    new Vector2(10f + i * 137f, -8f));
            }

            var auxiliaries = new[]
            {
                BuildAuxiliaryCounter(
                    root.transform,
                    profile,
                    ResourceType.PureOriginium,
                    "至纯源石",
                    icons[3],
                    new Vector2(BoardLeft, 0f)),
                BuildAuxiliaryCounter(
                    root.transform,
                    profile,
                    ResourceType.GoldVoucher,
                    "金券",
                    visuals.Banknote,
                    new Vector2(BoardLeft + profile.AuxiliaryCounterSize.x + AuxiliaryGap, 0f))
            };

            var controller = root.GetComponent<ResourceCounterBoard>();
            var view = root.GetComponent<ResourceCounterBoardView>();
            SetReferences(controller, ("view", view), ("layoutProfile", profile));
            SetReferences(view, ("controller", controller), ("root", rootRect), ("visualLibrary", visuals));
            SetObjectReferenceArray(view, "gearCounters", gearCounters);
            SetObjectReferenceArray(view, "auxiliaryCounters", auxiliaries);
            if (!controller.TryValidateConfiguration(out var reason))
                throw new InvalidOperationException("资源卡板 Prefab 配置无效：" + reason);
            return root;
        }

        private static ResourceGearCounterView BuildGearCounter(
            Transform parent,
            ResourceCounterBoardLayoutProfile profile,
            ResourceCounterVisualLibrary visuals,
            ResourceType type,
            string label,
            Sprite iconSprite,
            Vector2 position)
        {
            var column = CreateUiObject(label + " Mechanical Counter", parent, typeof(Image), typeof(Outline), typeof(ResourceGearCounterView));
            SetTopLeft(column.GetComponent<RectTransform>(), profile.GearCounterSize, position);
            column.GetComponent<Image>().color = new Color(0.155f, 0.125f, 0.105f, 0.98f);
            column.GetComponent<Image>().raycastTarget = false;
            column.GetComponent<Outline>().effectColor = new Color(0.28f, 0.22f, 0.17f, 1f);
            column.GetComponent<Outline>().effectDistance = new Vector2(1f, -1f);

            var plate = CreateUiObject("Current " + label + " Plate", column.transform, typeof(Image), typeof(Outline));
            SetTopCentered(plate.GetComponent<RectTransform>(), new Vector2(126f, 72f), new Vector2(0f, -5f));
            plate.GetComponent<Image>().color = new Color(0.72f, 0.69f, 0.61f, 0.98f);
            plate.GetComponent<Image>().raycastTarget = false;
            plate.GetComponent<Outline>().effectColor = new Color(0.22f, 0.17f, 0.13f, 1f);
            plate.GetComponent<Outline>().effectDistance = new Vector2(1f, -1f);

            var nameRibbon = CreateUiObject("Red " + label + " Ribbon", plate.transform, typeof(Image));
            SetTopCentered(nameRibbon.GetComponent<RectTransform>(), new Vector2(78f, 18f), new Vector2(0f, 2f));
            nameRibbon.GetComponent<Image>().color = new Color(0.55f, 0.17f, 0.11f, 1f);
            nameRibbon.GetComponent<Image>().raycastTarget = false;
            var currentName = CreateText(nameRibbon.transform, label + " Label", label, 14, FontStyle.Bold, TextAnchor.MiddleCenter);
            Stretch(currentName.rectTransform);
            currentName.color = new Color(0.94f, 0.90f, 0.78f, 1f);
            currentName.verticalOverflow = VerticalWrapMode.Overflow;

            var currentIcon = BuildImage(plate.transform, label + " Icon", iconSprite, Color.white);
            SetTopCentered(currentIcon.rectTransform, Vector2.one * 50f, new Vector2(0f, -17f));
            currentIcon.preserveAspect = true;

            var dialDeck = CreateUiObject("Tens And Ones Dial Deck", column.transform, typeof(Image));
            SetTopCentered(dialDeck.GetComponent<RectTransform>(), new Vector2(126f, 40f), new Vector2(0f, -77f));
            dialDeck.GetComponent<Image>().color = Color.clear;
            dialDeck.GetComponent<Image>().raycastTarget = false;

            var tensLabel = CreateText(dialDeck.transform, "Tens Label", "十位", 12, FontStyle.Bold, TextAnchor.MiddleCenter);
            SetTopCentered(tensLabel.rectTransform, new Vector2(46f, 14f), new Vector2(-30f, -1f));
            tensLabel.color = new Color(0.86f, 0.82f, 0.70f, 1f);
            tensLabel.verticalOverflow = VerticalWrapMode.Overflow;
            var onesLabel = CreateText(dialDeck.transform, "Ones Label", "个位", 12, FontStyle.Bold, TextAnchor.MiddleCenter);
            SetTopCentered(onesLabel.rectTransform, new Vector2(46f, 14f), new Vector2(30f, -1f));
            onesLabel.color = new Color(0.86f, 0.82f, 0.70f, 1f);
            onesLabel.verticalOverflow = VerticalWrapMode.Overflow;

            RectTransform tensGear;
            Image tensCover;
            Text tensDigit;
            BuildGearDial(column.transform, visuals, "Tens", -30f, out tensGear, out tensCover, out tensDigit);
            RectTransform onesGear;
            Image onesCover;
            Text onesDigit;
            BuildGearDial(column.transform, visuals, "Ones", 30f, out onesGear, out onesCover, out onesDigit);
            dialDeck.transform.SetAsLastSibling();

            var counter = column.GetComponent<ResourceGearCounterView>();
            SetEnum(counter, "resourceType", type);
            SetReferences(
                counter,
                ("resourceIcon", currentIcon),
                ("resourceName", currentName),
                ("mainGear", tensGear),
                ("idlerGear", onesGear),
                ("tensDigitText", tensDigit),
                ("onesDigitText", onesDigit),
                ("tensGearCover", tensCover),
                ("onesGearCover", onesCover));
            return counter;
        }

        private static void BuildGearDial(
            Transform parent,
            ResourceCounterVisualLibrary visuals,
            string prefix,
            float x,
            out RectTransform gear,
            out Image cover,
            out Text readout)
        {
            var gearImage = BuildImage(
                parent,
                prefix + " Numbered Gear",
                visuals.NumberedGear,
                Color.white);
            // The imported 1254px gear places its number centres about 487px
            // from the pivot. At 54px this aligns with the -99 window at y=-120.
            SetTopCenterPivoted(gearImage.rectTransform, new Vector2(54f, 54f), new Vector2(x, -120f));
            gear = gearImage.rectTransform;

            cover = BuildImage(
                parent,
                prefix + " Gear Cover",
                visuals.DialCover,
                new Color(0.19f, 0.145f, 0.11f, 1f));
            SetTopCentered(cover.rectTransform, new Vector2(64f, 40f), new Vector2(x, -84f));

            var readoutRing = BuildImage(
                parent,
                prefix + " Readout Ring",
                visuals.ReadoutRing,
                new Color(0.82f, 0.73f, 0.55f, 1f));
            SetTopCenterPivoted(readoutRing.rectTransform, new Vector2(16f, 16f), new Vector2(x, -99f));

            var pointer = CreateText(parent, prefix + " Fixed Pointer", "▼", 7, FontStyle.Bold, TextAnchor.MiddleCenter);
            SetTopCentered(pointer.rectTransform, new Vector2(12f, 8f), new Vector2(x, -85f));
            pointer.color = new Color(0.87f, 0.45f, 0.23f, 1f);

            readout = CreateText(parent, prefix + " Value State", "0", 1, FontStyle.Normal, TextAnchor.MiddleCenter);
            SetTopCenterPivoted(readout.rectTransform, Vector2.one, new Vector2(x, -99f));
            readout.color = Color.clear;

            pointer.transform.SetAsLastSibling();
            readoutRing.transform.SetAsLastSibling();
        }

        private static ResourceAuxiliaryCounterView BuildAuxiliaryCounter(
            Transform parent,
            ResourceCounterBoardLayoutProfile profile,
            ResourceType type,
            string label,
            Sprite iconSprite,
            Vector2 position)
        {
            var slot = CreateUiObject(label + " Entity Slot", parent, typeof(Image), typeof(Outline), typeof(ResourceAuxiliaryCounterView));
            SetTopLeft(slot.GetComponent<RectTransform>(), profile.AuxiliaryCounterSize, position);
            slot.GetComponent<Image>().color = UiTheme.PanelBackgroundLighter;
            slot.GetComponent<Image>().raycastTarget = false;
            slot.GetComponent<Outline>().effectColor = UiTheme.GoldOutlineThin;
            slot.GetComponent<Outline>().effectDistance = new Vector2(1f, -1f);

            var content = CreateUiObject("Pulse Content", slot.transform);
            Stretch(content.GetComponent<RectTransform>());
            var icon = BuildImage(content.transform, label + " Icon", iconSprite, Color.white);
            SetCentered(icon.rectTransform, new Vector2(40f, 38f), new Vector2(-76f, 0f));
            icon.preserveAspect = true;
            var name = CreateText(content.transform, label + " Label", label, 16, FontStyle.Bold, TextAnchor.MiddleLeft);
            SetCentered(name.rectTransform, new Vector2(94f, 32f), new Vector2(-8f, 0f));
            var amount = CreateText(content.transform, "Exact Amount", "0", 27, FontStyle.Bold, TextAnchor.MiddleRight);
            SetCentered(amount.rectTransform, new Vector2(54f, 38f), new Vector2(72f, 0f));
            amount.color = Color.white;
            amount.horizontalOverflow = HorizontalWrapMode.Overflow;
            amount.resizeTextForBestFit = true;
            amount.resizeTextMinSize = 16;
            amount.resizeTextMaxSize = 27;

            var flash = BuildImage(slot.transform, "Update Flash", null, new Color(1f, 0.82f, 0.35f, 0f));
            Stretch(flash.rectTransform);
            var counter = slot.GetComponent<ResourceAuxiliaryCounterView>();
            SetEnum(counter, "resourceType", type);
            SetReferences(
                counter,
                ("contentRoot", content.GetComponent<RectTransform>()),
                ("resourceIcon", icon),
                ("resourceName", name),
                ("amountText", amount),
                ("flashOverlay", flash));
            return counter;
        }

        private static void BuildRivet(Transform parent, Sprite sprite, Vector2 position)
        {
            BuildRivet(parent, sprite, position, new Color(0.82f, 0.69f, 0.4f, 0.9f));
        }

        private static void BuildRivet(Transform parent, Sprite sprite, Vector2 position, Color color)
        {
            var rivet = BuildImage(parent, "Rivet", sprite, color);
            var rect = rivet.rectTransform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = rect.anchorMin;
            rect.pivot = Vector2.one * 0.5f;
            rect.sizeDelta = Vector2.one * 18f;
            rect.anchoredPosition = position;
        }

        private static Image BuildImage(Transform parent, string name, Sprite sprite, Color color)
        {
            var objectValue = CreateUiObject(name, parent, typeof(Image));
            var image = objectValue.GetComponent<Image>();
            image.sprite = sprite;
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static Text CreateText(Transform parent, string name, string value, int size, FontStyle style, TextAnchor alignment)
        {
            var objectValue = CreateUiObject(name, parent, typeof(Text));
            var text = objectValue.GetComponent<Text>();
            text.text = value;
            text.alignment = alignment;
            text.color = UiTheme.GoldText;
            text.fontSize = size;
            text.fontStyle = style;
            text.font = UiEditorAssetReferences.CjkFont;
            text.raycastTarget = false;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static Sprite BuildSprite(Object[] assets, string name, int size, Action<Texture2D> painter)
        {
            var textureName = name + " Texture";
            var texture = assets.OfType<Texture2D>().FirstOrDefault(item => item.name == textureName);
            if (texture == null)
            {
                texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = textureName };
                AssetDatabase.AddObjectToAsset(texture, VisualLibraryPath);
            }
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            painter(texture);
            EditorUtility.SetDirty(texture);

            assets = AssetDatabase.LoadAllAssetsAtPath(VisualLibraryPath);
            var sprite = assets.OfType<Sprite>().FirstOrDefault(item => item.name == name);
            if (sprite == null)
            {
                sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), Vector2.one * 0.5f, size);
                sprite.name = name;
                AssetDatabase.AddObjectToAsset(sprite, VisualLibraryPath);
            }
            return sprite;
        }

        private static void PaintGear(Texture2D texture)
        {
            var size = texture.width;
            var center = (size - 1) * 0.5f;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = (x - center) / size;
                var dy = (y - center) / size;
                var radius = Mathf.Sqrt(dx * dx + dy * dy);
                var angle = Mathf.Atan2(dy, dx);
                var tooth = Mathf.Cos(angle * 12f) > 0.15f ? 0.485f : 0.42f;
                var inside = radius <= tooth && radius >= 0.12f;
                var groove = radius > 0.29f && radius < 0.33f;
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, inside ? (groove ? 0.72f : 1f) : 0f));
            }
            texture.Apply(false, false);
        }

        private static void PaintDialCover(Texture2D texture)
        {
            var centerX = (texture.width - 1) * 0.5f;
            var holeCenterY = texture.height * 0.625f;
            // This sprite is displayed in a 64x40 rect. Keep a roughly circular
            // 14px aperture behind the 16px ring so the complete printed digit
            // remains visible without exposing its neighbours.
            var holeRadiusX = texture.width * 0.11f;
            var holeRadiusY = texture.height * 0.175f;
            for (var y = 0; y < texture.height; y++)
            for (var x = 0; x < texture.width; x++)
            {
                var dx = (x - centerX) / holeRadiusX;
                var dy = (y - holeCenterY) / holeRadiusY;
                texture.SetPixel(x, y, dx * dx + dy * dy <= 1f ? Color.clear : Color.white);
            }
            texture.Apply(false, false);
        }

        private static void PaintReadoutRing(Texture2D texture)
        {
            var center = (texture.width - 1) * 0.5f;
            for (var y = 0; y < texture.height; y++)
            for (var x = 0; x < texture.width; x++)
            {
                var radius = Vector2.Distance(new Vector2(x, y), Vector2.one * center) / texture.width;
                texture.SetPixel(x, y, radius >= 0.41f && radius <= 0.48f ? Color.white : Color.clear);
            }
            texture.Apply(false, false);
        }

        private static void PaintRivet(Texture2D texture)
        {
            var size = texture.width;
            var center = (size - 1) * 0.5f;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var radius = Vector2.Distance(new Vector2(x, y), Vector2.one * center) / size;
                var alpha = radius < 0.43f ? 1f : 0f;
                var shade = Mathf.Clamp01(1.2f - radius * 1.8f);
                texture.SetPixel(x, y, new Color(shade, shade, shade, alpha));
            }
            texture.Apply(false, false);
        }

        private static void PaintBanknote(Texture2D texture)
        {
            var width = texture.width;
            var height = texture.height;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var nx = (float)x / (width - 1);
                var ny = (float)y / (height - 1);
                var body = nx > 0.08f && nx < 0.92f && ny > 0.24f && ny < 0.76f;
                var border = body && (nx < 0.13f || nx > 0.87f || ny < 0.29f || ny > 0.71f);
                var coin = body && Vector2.Distance(new Vector2(nx, ny), new Vector2(0.5f, 0.5f)) < 0.14f;
                var alpha = body ? 1f : 0f;
                var value = border || coin ? 1f : 0.72f;
                texture.SetPixel(x, y, new Color(value, value, value, alpha));
            }
            texture.Apply(false, false);
        }

        private static Sprite LoadReferenceGearSprite()
        {
            AssetDatabase.ImportAsset(
                NumberedGearSpritePath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(NumberedGearSpritePath) as TextureImporter;
            if (importer == null)
                throw new InvalidOperationException("无法导入指定齿轮贴图：" + NumberedGearSpritePath);

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Bilinear;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 2048;
            importer.SaveAndReimport();

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(NumberedGearSpritePath);
            if (sprite == null)
                throw new InvalidOperationException("指定齿轮贴图未生成 Sprite：" + NumberedGearSpritePath);
            return sprite;
        }

        private static Sprite LoadRequiredSprite(string path)
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null) throw new InvalidOperationException("缺少资源图标 Sprite：" + path);
            return sprite;
        }

        private static GameObject CreateUiObject(string name, Transform parent, params Type[] components)
        {
            var types = new Type[components.Length + 1];
            types[0] = typeof(RectTransform);
            Array.Copy(components, 0, types, 1, components.Length);
            var objectValue = new GameObject(name, types);
            objectValue.layer = LayerMask.NameToLayer("UI");
            objectValue.transform.SetParent(parent, false);
            return objectValue;
        }

        private static void SetEnum(Object target, string propertyName, ResourceType value)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(propertyName);
            if (property == null) throw new InvalidOperationException(target.GetType().Name + " missing property " + propertyName + ".");
            property.intValue = (int)value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetReferences(Object target, params (string property, Object value)[] references)
        {
            var serialized = new SerializedObject(target);
            foreach (var reference in references)
            {
                var property = serialized.FindProperty(reference.property);
                if (property == null) throw new InvalidOperationException(target.GetType().Name + " missing property " + reference.property + ".");
                property.objectReferenceValue = reference.value;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetObjectReferenceArray<T>(Object target, string propertyName, T[] values) where T : Object
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(propertyName);
            if (property == null || !property.isArray)
                throw new InvalidOperationException(target.GetType().Name + " missing array property " + propertyName + ".");
            property.arraySize = values.Length;
            for (var i = 0; i < values.Length; i++)
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetTopLeft(RectTransform rect, Vector2 size, Vector2 position)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = rect.anchorMin;
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static void SetTopCentered(RectTransform rect, Vector2 size, Vector2 position)
        {
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = rect.anchorMin;
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static void SetTopCenterPivoted(RectTransform rect, Vector2 size, Vector2 centerPosition)
        {
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = rect.anchorMin;
            rect.pivot = Vector2.one * 0.5f;
            rect.sizeDelta = size;
            rect.anchoredPosition = centerPosition;
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

        private static void StretchInset(RectTransform rect, float inset)
        {
            Stretch(rect);
            rect.offsetMin = Vector2.one * inset;
            rect.offsetMax = Vector2.one * -inset;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var separator = path.LastIndexOf('/');
            var parent = path.Substring(0, separator);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(separator + 1));
        }
    }
}
