using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace YC.Presentation.Editor
{
    /// <summary>UI-005 的一次性共享 HUD 资产迁移；不参与测试或运行时生成。</summary>
    public static class Ui005AssetMigration
    {
        private const string HudPath = "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab";
        private const string BaseRoot = "Assets/YC/Presentation/Ui005/Sprites/Bases/";
        private const string IconRoot = "Assets/YC/Presentation/Ui005/Sprites/Icons/";
        private const string FontPath = "Assets/YC/Presentation/Ui002/Fonts/FangZhengHeiTiJianTi-1.ttf";

        public static void Patch()
        {
            ImportSprites();
            var root = PrefabUtility.LoadPrefabContents(HudPath);
            try
            {
                var tabs = Required(root.transform, "Action Tabs");
                if (tabs.GetComponent<UiMainActionTabs>() != null)
                    throw new InvalidOperationException("UI-005 按钮已接入；拒绝重复写入。");
                var mainFace = Required(root.transform, "Main Action Face");
                var region = Required(root.transform, "Action Region");
                var main = StretchGroup(mainFace, "Main Actions");
                var quick = StretchGroup(mainFace, "Quick Actions");
                var city = StretchGroup(mainFace, "City Style Actions");
                var groups = new[] { main, quick, city };
                var tabNames = new[] { "main-actions", "quick-actions", "city-patterns" };
                var tabButtons = new Button[3];
                var tabStates = new UiMainButtonState[3];
                for (var i = 0; i < 3; i++)
                {
                    var background = Required(tabs, "Tab Background " + i);
                    var label = Required(tabs, "Tab Label " + i).GetComponent<Text>();
                    label.transform.SetParent(background, false);
                    SetRect(label.rectTransform, 44, 2, 132, 44);
                    label.alignment = TextAnchor.MiddleCenter;
                    label.fontSize = 19;
                    label.fontStyle = FontStyle.Normal;
                    label.font = Font();
                    var icon = Icon(background, 12, 8, 32);
                    tabButtons[i] = background.gameObject.AddComponent<Button>();
                    tabStates[i] = Configure(background.GetComponent<Image>(), icon, label,
                        "tab", tabNames[i], true, false, i == 0);
                }

                var header = Required(region, "Header Title").gameObject;
                var headers = new GameObject[3];
                headers[0] = header;
                for (var i = 1; i < 3; i++)
                {
                    headers[i] = UnityEngine.Object.Instantiate(header, header.transform.parent);
                    headers[i].name = i == 1 ? "Quick Header Title" : "City Header Title";
                    headers[i].GetComponent<Text>().text = i == 1 ? "快速行动" : "城市样式";
                    headers[i].SetActive(false);
                }

                var actions = new[]
                {
                    ("部署 Button", "deploy", "部署"),
                    ("调度 Button", "dispatch", "调度"),
                    ("探索 Button", "explore", "探索"),
                    ("城市移动 Button", "city-move", "城市移动"),
                    ("建设 Button", "build", "建设"),
                    ("特殊行动 Button", "special", "特殊行动")
                };
                Button build = null;
                Button special = null;
                for (var i = 0; i < actions.Length; i++)
                {
                    var item = actions[i];
                    var node = i < 4 ? Required(mainFace, item.Item1) :
                        NewButton(main, item.Item1, item.Item3).transform;
                    node.SetParent(main, false);
                    SetRect((RectTransform)node, i % 2 == 0 ? 2 : 272,
                        (i / 2) * 65, 258, 55);
                    var label = Required(node, "Label").GetComponent<Text>();
                    SetRect(label.rectTransform, 74, 0, 178, 55);
                    label.alignment = TextAnchor.MiddleCenter;
                    label.fontSize = 21;
                    label.fontStyle = FontStyle.Normal;
                    label.font = Font();
                    var icon = Icon(node, 17, 8.5f, 38);
                    var divider = Rect(node, "Icon Divider", 65, 13, 1, 29);
                    var dividerImage = divider.gameObject.AddComponent<Image>();
                    dividerImage.color = new Color(.72f, .69f, .58f, .62f);
                    dividerImage.raycastTarget = false;
                    var button = node.GetComponent<Button>();
                    if (button == null) button = node.gameObject.AddComponent<Button>();
                    Configure(node.GetComponent<Image>(), icon, label, "action", item.Item2,
                        false, false, false);
                    if (i == 4) build = button;
                    if (i == 5) { special = button; button.interactable = false; }
                }

                var useCharacter = Required(mainFace, "使用角色牌 Button");
                var declare = Required(mainFace, "宣告样式 Button");
                useCharacter.SetParent(quick, false);
                declare.SetParent(city, false);
                SetRect((RectTransform)useCharacter, 2, 0, 258, 55);
                SetRect((RectTransform)declare, 2, 0, 258, 55);
                var flip = Required(root.transform, "Action Panel Flip Button") as RectTransform;
                SetRect(flip, 450, -40, 74, 32);

                var panelView = root.GetComponentInChildren<ActionPanelView>(true);
                var panelData = new SerializedObject(panelView);
                panelData.FindProperty("buildButton").objectReferenceValue = build;
                panelData.FindProperty("specialButton").objectReferenceValue = special;
                panelData.ApplyModifiedPropertiesWithoutUndo();

                var tabController = tabs.gameObject.AddComponent<UiMainActionTabs>();
                var tabData = new SerializedObject(tabController);
                AssignArray(tabData.FindProperty("tabs"), tabButtons);
                AssignArray(tabData.FindProperty("visuals"), tabStates);
                AssignArray(tabData.FindProperty("sections"), new[]
                    { main.gameObject, quick.gameObject, city.gameObject });
                AssignArray(tabData.FindProperty("headerTitles"), headers);
                tabData.FindProperty("selectedIndex").intValue = 0;
                tabData.ApplyModifiedPropertiesWithoutUndo();
                quick.gameObject.SetActive(false);
                city.gameObject.SetActive(false);

                var undo = Required(root.transform, "Undo Button");
                var undoLabel = Required(undo, "Undo Label").GetComponent<Text>();
                undoLabel.text = "撤销";
                undoLabel.font = Font();
                undoLabel.fontSize = 20;
                undoLabel.fontStyle = FontStyle.Normal;
                undoLabel.alignment = TextAnchor.MiddleCenter;
                SetRect(undoLabel.rectTransform, 47, 0, 80, 48);
                var undoIcon = Required(undo, "Undo Icon").GetComponent<Image>();
                SetRect(undoIcon.rectTransform, 12, 9, 30, 30);
                Configure(undo.GetComponent<Image>(), undoIcon, undoLabel,
                    "undo", "undo", false, false, false);

                var end = Required(root.transform, "结束本回合 Button");
                var endLabel = Required(end, "Label").GetComponent<Text>();
                endLabel.font = Font();
                endLabel.fontSize = 21;
                endLabel.fontStyle = FontStyle.Normal;
                endLabel.alignment = TextAnchor.MiddleCenter;
                SetRect(endLabel.rectTransform, 73, 0, 154, 52);
                var endIcon = Icon(end, 38, 11, 30);
                Configure(end.GetComponent<Image>(), endIcon, endLabel,
                    "primary", "end", false, true, false);

                PrefabUtility.SaveAsPrefabAsset(root, HudPath);
                Debug.Log("UI005 BUTTON ASSET PATCH OK " + HudPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        public static void PatchModules()
        {
            var root = PrefabUtility.LoadPrefabContents(HudPath);
            try
            {
                var surface = Required(root.transform, "UI003 Main Surface");
                if (surface.GetComponent<GameplayMainModules>() != null)
                    throw new InvalidOperationException("UI-005 主模块已接入；拒绝重复写入。");
                var dataGroups = new GameObject[3];
                var emptyGroups = new GameObject[3];
                var names = new Text[3];
                var scores = new Text[3];
                var hands = new Text[3];
                var styles = new Text[3];
                var colors = new Image[3];
                for (var i = 0; i < 3; i++)
                {
                    var seat = Required(surface, "Opponent Seat " + (i + 1));
                    emptyGroups[i] = Required(seat, "Opponent Empty State").gameObject;
                    var group = Rect(seat, "Opponent Data", 82, 43, 320, 59);
                    dataGroups[i] = group.gameObject;
                    var swatch = Rect(group, "Player Color", 0, 6, 16, 16);
                    colors[i] = swatch.gameObject.AddComponent<Image>();
                    colors[i].raycastTarget = false;
                    names[i] = Label(group, "Player Name", 22, 0, 205, 28, "", 17, false);
                    names[i].resizeTextForBestFit = true;
                    names[i].resizeTextMinSize = 11;
                    names[i].resizeTextMaxSize = 17;
                    Label(group, "Score Caption", 231, 0, 34, 28, "分数", 14, false);
                    scores[i] = Label(group, "Player Score", 266, 0, 50, 28, "0", 19, true);
                    Label(group, "Hand Caption", 22, 29, 52, 24, "手牌", 14, false);
                    hands[i] = Label(group, "Hand Count", 75, 29, 45, 24, "0", 18, true);
                    Label(group, "Style Caption", 143, 29, 50, 24, "样式", 14, false);
                    styles[i] = Label(group, "Style Count", 196, 29, 45, 24, "0", 18, true);
                    group.gameObject.SetActive(false);
                }

                var faceDown = Required(surface, "Face Down Region");
                var slot = Required(faceDown, "Face Down Card Slot");
                var backRect = Rect(slot, "Covered Card Back", 4, 12, 78, 112);
                var covered = backRect.gameObject.AddComponent<RawImage>();
                covered.raycastTarget = false;
                covered.gameObject.SetActive(false);
                var frame = Required(slot, "Card Frame");
                backRect.SetSiblingIndex(frame.GetSiblingIndex());
                var coveredEmpty = Required(faceDown, "Face Down Empty State").gameObject;
                var discard = Required(surface, "Discard Region");
                Label(discard, "Deck Caption", 152, 4, 63, 32, "角色牌堆", 14, false);
                var deckCount = Label(discard, "Deck Count", 215, 4, 42, 32, "0", 18, true);

                var cooperation = Required(surface, "Cooperation Region");
                Required(cooperation, "Cooperation Rows").gameObject.SetActive(false);
                Required(cooperation, "Cooperation Empty State").gameObject.SetActive(true);

                var self = Required(surface, "Self Summary Region");
                SetRect((RectTransform)Required(self, "Self Summary Title"), 16, 5, 98, 38);
                var selfName = Required(self, "Self Summary Value").GetComponent<Text>();
                SetRect(selfName.rectTransform, 110, 5, 184, 38);
                selfName.resizeTextForBestFit = true;
                selfName.resizeTextMinSize = 10;
                selfName.resizeTextMaxSize = 18;
                var values = Required(self, "Resource Values");
                SetRect((RectTransform)values, 300, 4, 580, 40);
                for (var i = 0; i < 4; i++)
                {
                    var chip = Required(values, "Resource Chip " + i);
                    SetRect((RectTransform)chip, i * 110, 2, 106, 36);
                    SetRect((RectTransform)Required(chip, "Resource Amount"), 35, 0, 68, 36);
                }
                var voucher = Required(values, "Voucher Chip");
                SetRect((RectTransform)voucher, 440, 2, 135, 36);
                SetRect((RectTransform)Required(voucher, "Voucher Amount"), 54, 0, 77, 36);

                var modules = surface.gameObject.AddComponent<GameplayMainModules>();
                var data = new SerializedObject(modules);
                AssignArray(data.FindProperty("opponentData"), dataGroups);
                AssignArray(data.FindProperty("opponentEmpty"), emptyGroups);
                AssignArray(data.FindProperty("opponentName"), names);
                AssignArray(data.FindProperty("opponentScore"), scores);
                AssignArray(data.FindProperty("opponentHandCount"), hands);
                AssignArray(data.FindProperty("opponentStyleCount"), styles);
                AssignArray(data.FindProperty("opponentColor"), colors);
                data.FindProperty("coveredBack").objectReferenceValue = covered;
                data.FindProperty("coveredEmpty").objectReferenceValue = coveredEmpty;
                data.FindProperty("characterDeckCount").objectReferenceValue = deckCount;
                data.ApplyModifiedPropertiesWithoutUndo();
                var hud = root.GetComponent<GameplayInteractionHudView>();
                var hudData = new SerializedObject(hud);
                hudData.FindProperty("mainModules").objectReferenceValue = modules;
                hudData.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, HudPath);
                Debug.Log("UI005 MODULE ASSET PATCH OK " + HudPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        public static void PatchHandNavigation()
        {
            var root = PrefabUtility.LoadPrefabContents(HudPath);
            try
            {
                var hand = Required(root.transform, "Hand Region");
                if (Find(hand, "Hand Previous Page") != null)
                    throw new InvalidOperationException("手牌导航已存在；拒绝重复写入。");
                var previous = NavigationButton(hand, "Hand Previous Page", 349, "‹");
                var page = Label(hand, "Hand Page", 377, 4, 44, 27, "1 / 1", 16, false);
                page.font = AssetDatabase.LoadAssetAtPath<Font>(
                    "Assets/YC/Presentation/Ui002/Fonts/Novecento wide Normal Regular.ttf");
                page.alignment = TextAnchor.MiddleCenter;
                var next = NavigationButton(hand, "Hand Next Page", 424, "›");
                previous.gameObject.SetActive(false);
                next.gameObject.SetActive(false);
                page.gameObject.SetActive(false);
                var panel = root.GetComponentInChildren<CharacterHandPanel>(true);
                var data = new SerializedObject(panel);
                data.FindProperty("handPreviousButton").objectReferenceValue = previous;
                data.FindProperty("handNextButton").objectReferenceValue = next;
                data.FindProperty("handPageText").objectReferenceValue = page;
                data.FindProperty("handPageSize").intValue = 5;
                data.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, HudPath);
                Debug.Log("UI005 HAND NAVIGATION PATCH OK " + HudPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static Button NavigationButton(Transform hand, string name, float x, string caption)
        {
            var rect = Rect(hand, name, x, 4, 24, 27);
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(
                "Assets/YC/Presentation/Ui002/Sprites/Frontier31/button-secondary.png");
            image.type = Image.Type.Sliced;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var label = Label(rect, "Label", 0, 0, 24, 27, caption, 18, false);
            label.alignment = TextAnchor.MiddleCenter;
            return button;
        }

        private static Text Label(Transform parent, string name, float x, float y,
            float width, float height, string value, int size, bool number)
        {
            var rect = Rect(parent, name, x, y, width, height);
            var label = rect.gameObject.AddComponent<Text>();
            label.text = value;
            label.font = number ? AssetDatabase.LoadAssetAtPath<Font>(
                "Assets/YC/Presentation/Ui002/Fonts/Novecento NarrowBold.otf") : Font();
            if (label.font == null) throw new InvalidOperationException("缺少 UI-002 数字字体。");
            label.fontSize = size;
            label.fontStyle = FontStyle.Normal;
            label.alignment = TextAnchor.MiddleLeft;
            label.color = new Color(.17f, .16f, .14f, 1f);
            label.raycastTarget = false;
            return label;
        }

        private static void ImportSprites()
        {
            foreach (var path in AssetDatabase.FindAssets("t:Texture2D", new[]
                     { BaseRoot.TrimEnd('/'), IconRoot.TrimEnd('/') }))
            {
                var file = AssetDatabase.GUIDToAssetPath(path);
                if (!file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                var importer = AssetImporter.GetAtPath(file) as TextureImporter;
                if (importer == null) throw new InvalidOperationException("贴图导入失败：" + file);
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = 100f;
                importer.alphaIsTransparency = true;
                importer.filterMode = FilterMode.Bilinear;
                importer.spriteBorder = file.StartsWith(BaseRoot, StringComparison.Ordinal)
                    ? new Vector4(12, 12, 12, 12) : Vector4.zero;
                importer.SaveAndReimport();
            }
        }

        private static UiMainButtonState Configure(Image face, Image icon, Text label,
            string family, string glyph, bool tab, bool darkNormal, bool initiallySelected)
        {
            var oldState = face.GetComponent<UiSharedButtonState>();
            if (oldState != null) UnityEngine.Object.DestroyImmediate(oldState, true);
            var state = face.GetComponent<UiMainButtonState>();
            if (state == null) state = face.gameObject.AddComponent<UiMainButtonState>();
            var data = new SerializedObject(state);
            data.FindProperty("face").objectReferenceValue = face;
            data.FindProperty("icon").objectReferenceValue = icon;
            data.FindProperty("label").objectReferenceValue = label;
            foreach (var name in new[] { "normal", "hover", "pressed", "disabled" })
                data.FindProperty(name).objectReferenceValue = Base(family, name == "normal" ? "default" : name);
            if (tab)
            {
                data.FindProperty("selectedFace").objectReferenceValue = Base(family, "selected");
                data.FindProperty("selectedHover").objectReferenceValue = Base(family, "selected-hover");
            }
            data.FindProperty("lightIcon").objectReferenceValue = Glyph(glyph, "light");
            data.FindProperty("darkIcon").objectReferenceValue = Glyph(glyph, "dark");
            data.FindProperty("mutedIcon").objectReferenceValue = Glyph(glyph, "muted");
            data.FindProperty("darkNormalInk").boolValue = darkNormal;
            data.FindProperty("selected").boolValue = tab && initiallySelected;
            data.ApplyModifiedPropertiesWithoutUndo();
            face.sprite = Base(family, "default");
            face.type = Image.Type.Sliced;
            face.color = Color.white;
            face.raycastTarget = true;
            icon.raycastTarget = false;
            label.raycastTarget = false;
            var button = face.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = face;
            return state;
        }

        private static Button NewButton(Transform parent, string name, string caption)
        {
            var rect = Rect(parent, name, 0, 0, 258, 55);
            rect.gameObject.AddComponent<Image>();
            var button = rect.gameObject.AddComponent<Button>();
            var label = Rect(rect, "Label", 0, 0, 258, 55).gameObject.AddComponent<Text>();
            label.text = caption;
            label.font = Font();
            label.fontStyle = FontStyle.Normal;
            label.fontSize = 21;
            label.alignment = TextAnchor.MiddleCenter;
            label.raycastTarget = false;
            return button;
        }

        private static Image Icon(Transform parent, float x, float y, float size)
        {
            var rect = Rect(parent, "Icon 3.2", x, y, size, size);
            var image = rect.gameObject.AddComponent<Image>();
            image.preserveAspect = true;
            image.raycastTarget = false;
            return image;
        }

        private static RectTransform StretchGroup(Transform parent, string name)
        {
            var rect = Rect(parent, name, 0, 0, 0, 0);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return rect;
        }

        private static RectTransform Rect(Transform parent, string name,
            float x, float y, float width, float height)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            SetRect(rect, x, y, width, height);
            return rect;
        }

        private static void SetRect(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = Vector2.up;
            rect.pivot = Vector2.up;
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static void AssignArray<T>(SerializedProperty field, T[] values) where T : UnityEngine.Object
        {
            field.arraySize = values.Length;
            for (var i = 0; i < values.Length; i++)
                field.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        }

        private static Sprite Base(string family, string state)
        {
            var path = BaseRoot + family + "-" + state + ".png";
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null) throw new InvalidOperationException("缺少 3.2 底板：" + path);
            return sprite;
        }

        private static Sprite Glyph(string glyph, string ink)
        {
            var path = IconRoot + glyph + "-" + ink + ".png";
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null) throw new InvalidOperationException("缺少 3.2 图标：" + path);
            return sprite;
        }

        private static Font Font()
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            if (font == null) throw new InvalidOperationException("缺少方正字体：" + FontPath);
            return font;
        }

        private static Transform Required(Transform root, string name)
        {
            var node = Find(root, name);
            if (node == null) throw new InvalidOperationException("缺少 HUD 节点：" + name);
            return node;
        }

        public static void Inspect()
        {
            var report = new StringBuilder();
            var root = PrefabUtility.LoadPrefabContents(HudPath);
            try
            {
                foreach (var name in new[] { "Action Tabs", "Action Region", "Action Content",
                         "Action Panel", "Main Action Face", "Action Panel Flip Button",
                         "Undo Button", "结束本回合 Button", "Cooperation Rows" })
                {
                    var node = Find(root.transform, name);
                    report.AppendLine(name + ": " + (node == null ? "MISSING" : PathOf(node)));
                    if (node != null) Write(node, report, 0, 2);
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            foreach (var path in new[] { "Assets/Scenes/SampleScene.unity",
                         "Assets/Scenes/ThreePlayerScene.unity" })
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                report.AppendLine("SCENE " + path);
                foreach (var obj in scene.GetRootGameObjects())
                    foreach (var view in obj.GetComponentsInChildren<GameplayInteractionHudView>(true))
                    {
                        report.AppendLine("HUD " + PathOf(view.transform) + " source=" +
                            AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromSource(view.gameObject)));
                        var mods = PrefabUtility.GetPropertyModifications(view.gameObject);
                        if (mods == null) continue;
                        foreach (var mod in mods)
                            if (mod.target != null && mod.target.name.Contains("Action"))
                                report.AppendLine("  OVERRIDE " + mod.target.name + "." + mod.propertyPath);
                    }
            }
            var output = Path.GetFullPath("Temp/ui005-inspect.txt");
            File.WriteAllText(output, report.ToString());
            Debug.Log("UI005 INSPECT " + output);
        }

        private static void Write(Transform node, StringBuilder text, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;
            text.Append(' ', depth * 2).Append(node.name);
            var rect = node as RectTransform;
            if (rect != null)
                text.Append(" anchor=").Append(rect.anchorMin).Append('/').Append(rect.anchorMax)
                    .Append(" pivot=").Append(rect.pivot).Append(" pos=").Append(rect.anchoredPosition)
                    .Append(" size=").Append(rect.sizeDelta);
            var button = node.GetComponent<Button>();
            if (button != null) text.Append(" BUTTON");
            var image = node.GetComponent<Image>();
            if (image != null) text.Append(" IMAGE:").Append(image.sprite == null ? "null" : image.sprite.name);
            var label = node.GetComponent<Text>();
            if (label != null) text.Append(" TEXT:").Append(label.text);
            text.AppendLine();
            for (var i = 0; i < node.childCount; i++) Write(node.GetChild(i), text, depth + 1, maxDepth);
        }

        private static Transform Find(Transform root, string name)
        {
            if (root.name == name) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = Find(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private static string PathOf(Transform node)
        {
            var path = node.name;
            while (node.parent != null) { node = node.parent; path = node.name + "/" + path; }
            return path;
        }
    }
}
