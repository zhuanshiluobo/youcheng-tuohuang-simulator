#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation;

namespace YC.Editor
{
    /// <summary>手动创建本次授权的新玩家选择资产；现有资产存在时拒绝覆盖。</summary>
    public static class PlayerSelectionAssetInstaller
    {
        private const string Output = "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/PlayerSelectionPage.prefab";
        private const string ArtPath = "Assets/YC/Presentation/PlayerSelection/Artwork/";
        private static Font font;
        private static readonly Color Ink = new Color32(48,47,42,255), Muted = new Color32(130,121,99,255);
        private static Sprite Art(string id) => AssetDatabase.LoadAssetAtPath<Sprite>(ArtPath+id+".png") ?? throw new Exception("缺少贴图 "+id);
        private static RectTransform Rect(string name, Transform parent)
        {
            var rect = (RectTransform)new GameObject(name,typeof(RectTransform)).transform;
            rect.gameObject.layer = 5; rect.SetParent(parent,false); return rect;
        }
        private static void Stretch(RectTransform rect, float inset = 0)
        { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = Vector2.one*inset; rect.offsetMax = -Vector2.one*inset; }
        private static LayoutElement Size(RectTransform rect, float width = -1, float height = -1, float minWidth = 0,
            float minHeight = 0, float flexibleWidth = 0, float flexibleHeight = 0)
        {
            var e = rect.gameObject.AddComponent<LayoutElement>();
            e.minWidth = minWidth; e.minHeight = minHeight; e.preferredWidth = width; e.preferredHeight = height;
            e.flexibleWidth = flexibleWidth; e.flexibleHeight = flexibleHeight; return e;
        }
        private static VerticalLayoutGroup Vertical(RectTransform rect, RectOffset padding = null)
        {
            var l = rect.gameObject.AddComponent<VerticalLayoutGroup>(); l.padding = padding ?? new RectOffset();
            l.childControlWidth = l.childControlHeight = true; l.childForceExpandWidth = true; l.childForceExpandHeight = false;
            l.childScaleWidth = l.childScaleHeight = false; return l;
        }
        private static HorizontalLayoutGroup Horizontal(RectTransform rect, float gap = 0)
        {
            var l = rect.gameObject.AddComponent<HorizontalLayoutGroup>(); l.spacing = gap; l.childAlignment = TextAnchor.MiddleCenter;
            l.childControlWidth = l.childControlHeight = true; l.childForceExpandWidth = false; l.childForceExpandHeight = true;
            l.childScaleWidth = l.childScaleHeight = false; return l;
        }
        private static void Gap(Transform parent, string name, float preferred, float minimum = -1, float flexible = 0)
        { Size(Rect(name,parent),height:preferred,minHeight:minimum < 0 ? preferred : minimum,flexibleHeight:flexible); }
        private static Image Picture(string name, Transform parent, string art, bool sliced = false, bool raycast = false)
        {
            var image = Rect(name,parent).gameObject.AddComponent<Image>(); image.sprite = Art(art);
            image.type = sliced ? Image.Type.Sliced : Image.Type.Simple; image.raycastTarget = raycast; image.color = Color.white; return image;
        }
        private static Image Overlay(string name, RectTransform parent, string art, bool sliced = false, float inset = 0)
        {
            var image = Picture(name,parent,art,sliced); Stretch(image.rectTransform,inset);
            image.gameObject.AddComponent<LayoutElement>().ignoreLayout = true; return image;
        }
        private static Text Label(string name, Transform parent, string text, int size, Color color,
            TextAnchor alignment = TextAnchor.MiddleLeft)
        {
            var t = Rect(name,parent).gameObject.AddComponent<Text>(); t.text = text; t.font = font; t.fontSize = size;
            t.fontStyle = FontStyle.Normal; t.alignment = alignment; t.color = color; t.raycastTarget = false;
            t.resizeTextForBestFit = true; t.resizeTextMinSize = Math.Min(20,size); t.resizeTextMaxSize = size;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Truncate; return t;
        }
        private static void Bind(UnityEngine.Object target, params object[] fields)
        {
            var so = new SerializedObject(target);
            for (var i = 0; i < fields.Length; i += 2)
            {
                var p = so.FindProperty((string)fields[i]); var value = fields[i+1];
                if (value is UnityEngine.Object[] array)
                { p.arraySize = array.Length; for (var j = 0; j < array.Length; j++) p.GetArrayElementAtIndex(j).objectReferenceValue = array[j]; }
                else if (value is string[] strings)
                { p.arraySize = strings.Length; for (var j = 0; j < strings.Length; j++) p.GetArrayElementAtIndex(j).stringValue = strings[j]; }
                else p.objectReferenceValue = value as UnityEngine.Object;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        private static Button ActionButton(string name, Transform parent, string artPrefix, string text, float width,
            float height = 64, bool close = false)
        {
            var image = Picture(name,parent,artPrefix+(close ? "-default" : "-normal"),true,true);
            Size(image.rectTransform,width,height,width,height);
            var button = image.gameObject.AddComponent<Button>(); button.targetGraphic = image; button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState { highlightedSprite = Art(artPrefix+"-hover"), pressedSprite = Art(artPrefix+"-pressed"),
                disabledSprite = Art(artPrefix+"-disabled"), selectedSprite = Art(artPrefix+"-hover") };
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            if (close) { var icon = Overlay("Close Icon",image.rectTransform,"icon-close-dark",inset:16); icon.preserveAspect = true; }
            else { var label = Label("Label",image.transform,text,27,artPrefix == "cancel" ? new Color32(238,235,221,255) : Ink,TextAnchor.MiddleCenter); Stretch(label.rectTransform,8); }
            return button;
        }
        private static PlayerSelectionOptionView Option(RectTransform row)
        {
            var fill = Picture("Player Option Template",row,"option-fill-default",true,true); var rect = fill.rectTransform;
            Size(rect,284,320,204,320); Vertical(rect,new RectOffset(20,20,28,4));
            var frame = Overlay("Option Frame",rect,"option-frame-default",true);
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = fill; button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            var avatarRow = Rect("Avatar Row",rect); Size(avatarRow,height:164,minHeight:164);
            var avatarLayout = Horizontal(avatarRow); avatarLayout.childForceExpandHeight = false;
            var avatarBase = Picture("Avatar Fill",avatarRow,"avatar-fill"); Size(avatarBase.rectTransform,164,164,164,164);
            var avatar = Rect("Avatar Image",avatarBase.transform).gameObject.AddComponent<RawImage>(); Stretch(avatar.rectTransform,4); avatar.raycastTarget = false;
            var empty = Overlay("Empty Avatar Icon",avatarBase.rectTransform,"icon-empty-seat-muted",inset:54); empty.preserveAspect = true;
            Overlay("Avatar Frame",avatarBase.rectTransform,"avatar-frame",true);
            Gap(rect,"Avatar Name Gap",18);
            var name = Label("Player Name",rect,"玩家",26,Ink,TextAnchor.MiddleCenter); Size(name.rectTransform,height:42,minHeight:42);
            Gap(rect,"Name Color Gap",4);
            var colorRow = Rect("Color Row",rect); Size(colorRow,height:40,minHeight:40);
            var colors = Horizontal(colorRow,12); colors.padding = new RectOffset(30,0,0,0); colors.childForceExpandHeight = false;
            var marker = Picture("Player Color Marker",colorRow,"marker-green"); marker.preserveAspect = true; Size(marker.rectTransform,32,32,32,32);
            var colorText = Label("Color Name",colorRow,"绿色标记",20,Ink); Size(colorText.rectTransform,110,40,minHeight:40);
            var disabled = Rect("Disabled State",rect); Size(disabled,height:24,minHeight:24); Horizontal(disabled,6);
            var lockIcon = Picture("Lock Icon",disabled,"icon-lock-muted"); lockIcon.preserveAspect = true; Size(lockIcon.rectTransform,16,16,16,16);
            var reason = Label("Disabled Reason",disabled,"",18,Muted,TextAnchor.MiddleCenter); Size(reason.rectTransform,0,24,flexibleWidth:1);
            var checkbox = Overlay("Checkbox",rect,"checkbox-unchecked");
            checkbox.rectTransform.anchorMin = checkbox.rectTransform.anchorMax = new Vector2(1,1);
            checkbox.rectTransform.pivot = new Vector2(1,1); checkbox.rectTransform.anchoredPosition = new Vector2(-16,-12); checkbox.rectTransform.sizeDelta = new Vector2(32,32);
            var check = Overlay("Check Icon",checkbox.rectTransform,"icon-check-dark",inset:5); check.preserveAspect = true;
            var self = Label("Self Tag",rect,"你",20,Muted); self.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            self.rectTransform.anchorMin = self.rectTransform.anchorMax = new Vector2(0,1); self.rectTransform.pivot = new Vector2(0,1);
            self.rectTransform.anchoredPosition = new Vector2(14,-12); self.rectTransform.sizeDelta = new Vector2(70,28);
            var view = rect.gameObject.AddComponent<PlayerSelectionOptionView>();
            Bind(view,"button",button,"fill",fill,"frame",frame,"marker",marker,"checkbox",checkbox,"check",check,
                "avatar",avatar,"emptyAvatar",empty.gameObject,"selfTag",self.gameObject,"disabledState",disabled.gameObject,
                "playerName",name,"colorName",colorText,"disabledReason",reason,
                "fills",new UnityEngine.Object[] {Art("option-fill-default"),Art("option-fill-hover"),Art("option-fill-selected"),Art("option-fill-disabled")},
                "frames",new UnityEngine.Object[] {Art("option-frame-default"),Art("option-frame-hover"),Art("option-frame-selected"),Art("option-frame-disabled")},
                "markers",new UnityEngine.Object[] {Art("marker-green"),Art("marker-yellow"),Art("marker-blue"),Art("marker-red")},
                "checkboxes",new UnityEngine.Object[] {Art("checkbox-unchecked"),Art("checkbox-checked"),Art("checkbox-disabled")},
                "colorLabels",new[] {"绿色标记","黄色标记","蓝色标记","红色标记"});
            view.gameObject.SetActive(false); return view;
        }
        public static void CreateNewPage()
        {
            if (File.Exists(Output)) throw new InvalidOperationException("玩家选择页面已存在；禁止重建覆盖。");
            AssetDatabase.Refresh(); font = AssetDatabase.LoadAssetAtPath<Font>("Assets/YC/Presentation/CommonUi/Fonts/FangZhengHeiTiJianTi-1.ttf");
            var root = Rect("Player Selection Page",null);
            try
            {
                Stretch(root); root.sizeDelta = Vector2.zero;
                var canvas = root.gameObject.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.overrideSorting = true; canvas.sortingOrder = 118;
                var scaler = root.gameObject.AddComponent<CanvasScaler>(); scaler.enabled = false;
                root.gameObject.AddComponent<GraphicRaycaster>();
                var rootLayout = Vertical(root,new RectOffset(0,0,30,0)); rootLayout.childAlignment = TextAnchor.MiddleCenter;
                var dim = Overlay("Dim Overlay",root,"dim-overlay"); dim.raycastTarget = true;
                var placement = Rect("Window Placement",root); Size(placement,height:660,minHeight:634); Horizontal(placement);
                var window = Picture("Window Fill",placement,"window-fill",raycast:true).rectTransform;
                Size(window,1344,minWidth:1024,flexibleHeight:1); Vertical(window,new RectOffset(56,56,34,32));
                Overlay("Window Frame",window,"window-frame",true);
                var accent = Overlay("Title Accent",window,"accent"); accent.rectTransform.anchorMin = accent.rectTransform.anchorMax = new Vector2(0,1);
                accent.rectTransform.pivot = new Vector2(0,1); accent.rectTransform.anchoredPosition = new Vector2(32,-38); accent.rectTransform.sizeDelta = new Vector2(4,48);
                var header = Rect("Header",window); var headerLayout = Vertical(header); headerLayout.spacing = 4;
                var titleRow = Rect("Title Row",header); Size(titleRow,height:56,minHeight:56); var titleLayout = Horizontal(titleRow,19); titleLayout.padding.left = 2;
                var icon = Picture("Players Icon",titleRow,"icon-players-dark"); icon.preserveAspect = true; Size(icon.rectTransform,40,40,40,40);
                var title = Label("Title",titleRow,"选择玩家",38,Ink); Size(title.rectTransform,0,52,flexibleWidth:1);
                var close = ActionButton("Close Button",titleRow,"close",null,64,56,true);
                var instruction = Label("Instruction",header,"请选择 1 位玩家",25,Ink); Size(instruction.rectTransform,height:36,minHeight:36);
                var description = Label("Context Description",header,"",22,Muted); Size(description.rectTransform,height:36,minHeight:36); description.gameObject.SetActive(false);
                Gap(window,"Header Divider Gap",6);
                var divider = Picture("Header Divider",window,"divider"); Size(divider.rectTransform,height:2,minHeight:2);
                Gap(window,"Options Top Gap",22);
                var row = Rect("Player Options Row",window); Size(row,height:320,minHeight:320); Horizontal(row,32);
                var template = Option(row);
                var empty = Label("Empty State",row,"暂无实际玩家",26,Muted,TextAnchor.MiddleCenter); Stretch(empty.rectTransform);
                empty.gameObject.AddComponent<LayoutElement>().ignoreLayout = true; empty.gameObject.SetActive(false);
                Gap(window,"Body Footer Gap",38,4,1);
                var footerDivider = Picture("Footer Divider",window,"divider"); Size(footerDivider.rectTransform,height:2,minHeight:2);
                Gap(window,"Footer Top Gap",36,4,1);
                var footer = Rect("Footer",window); Size(footer,height:72,minHeight:72); Horizontal(footer,32);
                var textGroup = Rect("Selection Status",footer); Size(textGroup,0,72,flexibleWidth:1); var textLayout = Vertical(textGroup); textLayout.spacing = 6;
                var summary = Label("Selection Count",textGroup,"",26,Ink); Size(summary.rectTransform,height:38,minHeight:38);
                var hint = Label("Selection Hint",textGroup,"",20,Muted); Size(hint.rectTransform,height:28,minHeight:28);
                var actions = Rect("Action Buttons",footer); Size(actions,432,64,432,64); var actionsLayout = Horizontal(actions,32); actionsLayout.childForceExpandHeight = false;
                var cancel = ActionButton("Cancel Button",actions,"cancel","取消",176);
                var confirm = ActionButton("Confirm Button",actions,"confirm","确认选择",224);
                var view = root.gameObject.AddComponent<PlayerSelectionPageView>();
                Bind(view,"window",window,"optionsRow",row,"optionTemplate",template,"title",title,"description",description,
                    "instruction",instruction,"summary",summary,"hint",hint,"emptyState",empty.gameObject,"confirm",confirm,"cancel",cancel,"close",close);
                root.gameObject.SetActive(false);
                var saved = PrefabUtility.SaveAsPrefabAsset(root.gameObject,Output); AssetDatabase.SaveAssets();
                var component = saved.GetComponent<PlayerSelectionPageView>();
                if (!component.TryValidateConfiguration(out var reason)) throw new Exception(reason);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(component,out string guid,out long fileId);
                Directory.CreateDirectory("Logs/PlayerSelection-20261002");
                File.WriteAllText("Logs/PlayerSelection-20261002/created-asset.json","{\"guid\":\""+guid+"\",\"fileID\":"+fileId+"}");
                Debug.Log("玩家选择新页面创建完成，使用 PageHost 原生布局；未保存场景或其他预制体。");
            }
            finally { UnityEngine.Object.DestroyImmediate(root.gameObject); }
        }
    }
}
#endif
