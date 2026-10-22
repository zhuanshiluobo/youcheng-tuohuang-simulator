#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Presentation;
using YC.Presentation.Maps;

namespace YC.Editor
{
    /// <summary>仅手动创建本次授权的两个新页面；不会重建 HUD、场景或其他界面资产。</summary>
    public static class InformationPageAssetInstaller
    {
        private const string Folder = "Assets/YC/Presentation/InformationPages/";
        private const string Prefabs = "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/Information/";
        private static Font font;
        private static readonly Color Ink = new Color32(48,47,42,255), Light = new Color32(238,235,221,255), Muted = new Color32(135,123,100,255);
        public static void InstallNewPages()
        {
            Directory.CreateDirectory(Prefabs);
            AssetDatabase.Refresh();
            font = AssetDatabase.LoadAssetAtPath<Font>("Assets/YC/Presentation/CommonUi/Fonts/FangZhengHeiTiJianTi-1.ttf");
            if (File.Exists(Prefabs+"RegionInformationPage.prefab") || File.Exists(Prefabs+"GameBoxPage.prefab"))
                throw new InvalidOperationException("页面已存在；禁止用安装入口覆盖用户布局。");
            CreateArtworkCatalog(); BuildRegion(); BuildBox(); AssetDatabase.SaveAssets();
            Debug.Log("区控与游戏盒新页面资产创建完成；没有保存或覆盖现有 HUD 和场景。");
        }
        private static void Bind(UnityEngine.Object target, params object[] pairs)
        {
            var so = new SerializedObject(target);
            for (var i=0;i<pairs.Length;i+=2)
            {
                var prop = so.FindProperty((string)pairs[i]);var value=pairs[i+1];
                if(prop==null)throw new InvalidOperationException(target.GetType()+" 缺少字段 "+pairs[i]);
                if(value is UnityEngine.Object[] objects)
                {prop.arraySize=objects.Length;for(var n=0;n<objects.Length;n++)prop.GetArrayElementAtIndex(n).objectReferenceValue=objects[n];}
                else if(value is Color[] colors)
                {prop.arraySize=colors.Length;for(var n=0;n<colors.Length;n++)prop.GetArrayElementAtIndex(n).colorValue=colors[n];}
                else if(value is UnityEngine.Object o)prop.objectReferenceValue=o;
                else if(value is Vector2 v)prop.vector2Value=v;
                else if(value is float f)prop.floatValue=f;
                else if(value is int k)prop.intValue=k;
                else if(value is bool b)prop.boolValue=b;
                else if(value is string s)prop.stringValue=s;
                else prop.objectReferenceValue=null;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        private static RectTransform Rect(string name,Transform parent,float x,float y,float w,float h)
        {
            var obj=new GameObject(name,typeof(RectTransform));obj.layer=5;
            var rect=(RectTransform)obj.transform;rect.SetParent(parent,false);
            rect.anchorMin=rect.anchorMax=new Vector2(0,1);rect.pivot=new Vector2(0,1);
            rect.anchoredPosition=new Vector2(x,-y);rect.sizeDelta=new Vector2(w,h);return rect;
        }
        private static void Stretch(RectTransform rect)
        {rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.pivot=new Vector2(.5f,.5f);rect.anchoredPosition=Vector2.zero;rect.sizeDelta=Vector2.zero;}
        private static Sprite Art(string package,string path) => AssetDatabase.LoadAssetAtPath<Sprite>(Folder+"Artwork/"+package+"/"+path+".png");
        private static Image Picture(string name,Transform parent,float x,float y,float w,float h,Sprite sprite,bool sliced=false,bool input=false)
        {
            var rect=Rect(name,parent,x,y,w,h);var image=rect.gameObject.AddComponent<Image>();
            image.sprite=sprite;image.color=Color.white;image.raycastTarget=input;image.type=sliced?Image.Type.Sliced:Image.Type.Simple;return image;
        }
        private static Text Label(string name,Transform parent,float x,float y,float w,float h,string text,int size,Color color,TextAnchor alignment=TextAnchor.MiddleLeft)
        {
            var label=Rect(name,parent,x,y,w,h).gameObject.AddComponent<Text>();label.font=font;label.fontSize=size;
            label.text=text;label.color=color;label.raycastTarget=false;label.alignment=alignment;
            label.horizontalOverflow=HorizontalWrapMode.Wrap;label.verticalOverflow=VerticalWrapMode.Truncate;
            label.resizeTextForBestFit=true;label.resizeTextMinSize=Mathf.Min(16,size);label.resizeTextMaxSize=size;return label;
        }
        private static GameObject Root(string name,string package,Rect window)
        {
            var root=new GameObject(name,typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster),typeof(InformationPageDismiss));root.layer=5;
            var canvas=root.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=31000;
            var scaler=root.GetComponent<CanvasScaler>();scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution=new Vector2(1920,1080);scaler.screenMatchMode=CanvasScaler.ScreenMatchMode.Expand;
            var dim=Picture("全屏遮罩",root.transform,0,0,1920,1080,Art(package,"window/dim-overlay"),false,true);Stretch(dim.rectTransform);
            Picture("窗口底板",root.transform,window.x,window.y,window.width,window.height,
                Art(package,package=="AreaControl"?"window/window-fill":"window/fill"),false,true);
            Picture("窗口边框",root.transform,window.x,window.y,window.width,window.height,
                Art(package,package=="AreaControl"?"window/window-frame":"window/frame"),true);
            return root;
        }
        private static InfluenceMarker2DView Marker(Transform parent,float x,float y,float size,bool numbered=false)
        {
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/Pieces/InfluenceMarker2D"+(numbered?"Numbered":"")+".prefab");
            var go=(GameObject)PrefabUtility.InstantiatePrefab(prefab,parent);
            var rect=(RectTransform)go.transform;rect.anchorMin=rect.anchorMax=new Vector2(0,1);rect.pivot=new Vector2(0,1);
            rect.anchoredPosition=new Vector2(x,-y);rect.sizeDelta=new Vector2(size,size);return go.GetComponent<InfluenceMarker2DView>();
        }
        private static void Flexible(RectTransform rect,float preferred=0,float flexible=1)
        {var e=rect.gameObject.AddComponent<LayoutElement>();e.minWidth=0;e.preferredWidth=preferred;e.flexibleWidth=flexible;e.minHeight=0;e.flexibleHeight=1;}
        private static HorizontalLayoutGroup Horizontal(RectTransform rect)
        {
            var layout=rect.gameObject.AddComponent<HorizontalLayoutGroup>();layout.childAlignment=TextAnchor.MiddleCenter;
            layout.childControlWidth=layout.childControlHeight=true;layout.childForceExpandWidth=false;layout.childForceExpandHeight=true;
            layout.spacing=12;layout.padding=new RectOffset(20,20,6,6);return layout;
        }
        private static InformationValueCell ValueCell(Transform parent,string name,float preferred=0,float flexible=1,bool header=false)
        {
            var rect=Rect(name,parent,0,0,150,70);Flexible(rect,preferred,flexible);
            var layout=Horizontal(rect);layout.padding=new RectOffset(0,0,0,0);layout.spacing=12;layout.childForceExpandHeight=false;
            var marker=Marker(rect,0,0,header?28:34);var element=marker.gameObject.AddComponent<LayoutElement>();element.preferredWidth=element.minWidth=header?28:34;element.preferredHeight=header?28:34;
            var label=Label("数值",rect,0,0,100,48,"",header?27:30,header?Light:Ink);Flexible(label.rectTransform);
            var view=rect.gameObject.AddComponent<InformationValueCell>();Bind(view,"marker",marker,"label",label);return view;
        }
        private static void BuildRegion()
        {
            var root=Root("区控页面","AreaControl",new Rect(180,80,1560,920));
            Picture("顶栏底板",root.transform,196,96,1528,116,Art("AreaControl","header/header-fill"));
            Picture("标题装饰",root.transform,214,128,4,48,Art("AreaControl","header/header-accent"));
            var icon=Picture("区控图标",root.transform,238,128,64,64,Art("AreaControl","header/region-icon"));icon.preserveAspect=true;
            Label("标题",root.transform,326,118,500,76,"区控",48,Ink);
            var map=Label("地图摘要",root.transform,1190,118,510,76,"",25,Ink,TextAnchor.MiddleRight);
            var header=Picture("表头",root.transform,216,232,1488,64,Art("AreaControl","table/table-head"),true).rectTransform;Horizontal(header);
            var regionHeading=Label("区域列标题",header,0,0,200,50,"区域",28,Light,TextAnchor.MiddleCenter);Flexible(regionHeading.rectTransform,200,0);
            var pointHeading=Label("分值列标题",header,0,0,140,50,"分值",28,Light,TextAnchor.MiddleCenter);Flexible(pointHeading.rectTransform,140,0);
            var participant=ValueCell(header,"玩家列模板",header:true);participant.gameObject.SetActive(false);
            var emptyHeader=Label("空格数列标题",header,0,0,180,50,"空格数",28,Light,TextAnchor.MiddleCenter);Flexible(emptyHeader.rectTransform,180,0);
            var rows=new RegionInformationRowView[8];
            for(var i=0;i<8;i++)
            {
                var row=Picture("区域行 "+i,root.transform,216,296+i*70,1488,70,Art("AreaControl",i%2==0?"table/row-even":"table/row-odd")).rectTransform;Horizontal(row);
                var region=ValueCell(row,"区域",200,0);
                var badge=Picture("分值徽章",row,0,0,140,54,AssetDatabase.LoadAssetAtPath<Sprite>("Assets/YC/Presentation/GameplayHud/Sprites/Icons/resource-score.png"));Flexible(badge.rectTransform,140,0);badge.preserveAspect=true;badge.rectTransform.pivot=new Vector2(.5f,.5f);
                var score=Label("分值",badge.transform,0,0,140,54,"",27,Color.white,TextAnchor.MiddleCenter);Stretch(score.rectTransform);
                var player=ValueCell(row,"玩家数值模板");player.gameObject.SetActive(false);
                var empty=ValueCell(row,"真实空格",180,0);
                Picture("行分隔线",root.transform,216,365+i*70,1488,1,Art("AreaControl","table/horizontal-rule"));
                rows[i]=row.gameObject.AddComponent<RegionInformationRowView>();Bind(rows[i],"region",region,"points",score,"participantTemplate",player,"empty",empty);
            }
            Label("底部说明",root.transform,216,908,420,50,"当前影响力",24,Ink);
            Label("关闭提示",root.transform,640,908,640,50,"点击屏幕任意处关闭",22,Muted,TextAnchor.MiddleCenter);
            var summary=Label("空格摘要",root.transform,1280,908,424,50,"",24,Muted,TextAnchor.MiddleRight);
            var view=root.AddComponent<RegionInformationPageView>();Bind(view,"dismiss",root.GetComponent<InformationPageDismiss>(),"mapSummary",map,"emptySummary",summary,"participantHeaderTemplate",participant,"emptyHeader",emptyHeader.rectTransform,"rows",Array.ConvertAll(rows,r=>(UnityEngine.Object)r));
            Save(root,"RegionInformationPage");
        }
        private static RectTransform Module(Transform root,string name,float x,float width,string title,out Text count)
        {
            var module=Rect(name,root,x,284,width,540);
            Picture("模块底板",module,0,0,width,540,Art("GameBox","modules/fill"));
            Picture("模块边框",module,0,0,width,540,Art("GameBox","modules/frame"),true);
            var heading=Picture("标题底板",module,5,5,width-10,54,Art("GameBox","modules/title-fill"),true,true);
            heading.gameObject.AddComponent<InformationContentInput>();
            Picture("标题边框",module,5,5,width-10,54,Art("GameBox","modules/title-frame"),true);
            Picture("标题装饰",module,18,17,4,30,Art("GameBox","modules/title-accent"));
            Label("模块标题",module,36,5,width-78,54,title,23,Light);
            count=Label("总数",module,width-62,5,42,54,"0",25,Light,TextAnchor.MiddleRight);
            return module;
        }
        private static InformationPileView Pile(RectTransform module,Text count,bool character)
        {
            var area=Picture("牌堆入口",module,20,80,276,360,null,false,true);area.color=Color.clear;
            var button=area.gameObject.AddComponent<Button>();button.targetGraphic=area;button.transition=Selectable.Transition.None;
            var layers=new Image[5];
            for(var i=0;i<5;i++)
            {
                layers[i]=Picture("牌堆层 "+i,area.transform,0,0,182,260,null);var rect=layers[i].rectTransform;
                rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(.5f,.5f);rect.anchoredPosition=Vector2.zero;
                var border=Picture("卡边",rect,-4,-4,190,268,Art("GameBox","piles/card-frame"),true);Stretch(border.rectTransform);border.rectTransform.sizeDelta=Vector2.one*8;
            }
            var empty=Label("空牌堆",module,20,214,276,90,character?"暂无放回的角色牌":"暂无放回的事件牌",22,Muted,TextAnchor.MiddleCenter);
            var awaiting=Label("贴图等待提示",module,20,214,276,90,"等待卡牌图像",22,Muted,TextAnchor.MiddleCenter);awaiting.gameObject.SetActive(false);
            var hint=Label("查看提示",module,20,480,276,40,"单击查看",22,Muted,TextAnchor.MiddleCenter);
            var view=area.gameObject.AddComponent<InformationPileView>();Bind(view,"layers",Array.ConvertAll(layers,r=>(UnityEngine.Object)r),"inspectButton",button,"countText",count,"emptyLabel",empty.gameObject,"awaitingImageLabel",awaiting.gameObject,"inspectHint",hint.gameObject,"maximumCardSize",character?new Vector2(182,260):new Vector2(200,250));return view;
        }
        private static void BuildBox()
        {
            var root=Root("游戏盒页面","GameBox",new Rect(135,130,1650,820));
            Picture("顶栏底板",root.transform,155,148,1610,116,Art("GameBox","header/fill"));
            Picture("标题装饰",root.transform,165,180,4,48,Art("GameBox","header/accent"));
            Picture("游戏盒图标",root.transform,185,181,64,64,Art("GameBox","header/game-box-icon")).preserveAspect=true;
            Label("标题",root.transform,266,174,180,76,"游戏盒",48,Ink);
            Label("副标题",root.transform,466,174,700,76,"已放回的组件",27,Ink);
            var strip=Picture("玩家标记条",root.transform,1308,173,438,67,Art("GameBox","markers/strip-fill"),true,true);strip.gameObject.AddComponent<InformationContentInput>();
            Picture("玩家标记条边框",root.transform,1308,173,438,67,Art("GameBox","markers/strip-frame"),true);
            Label("玩家标记标题",root.transform,1324,173,124,67,"玩家标记",23,Ink);
            var markerCatalog=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<InfluenceMarker2DCatalog>("Assets/YC/Presentation/Content/InfluenceMarker2DCatalog.asset"));
            markerCatalog.Find(InfluenceMarker2DColor.Blue).numberInk=Light;markerCatalog.Find(InfluenceMarker2DColor.Red).numberInk=Light;
            AssetDatabase.CreateAsset(markerCatalog,Folder+"GameBoxMarkerCatalog.asset");
            var markers=new InfluenceMarker2DView[4];for(var i=0;i<4;i++)
            {
                markers[i]=Marker(root.transform,1448+i*66,184,44,true);Bind(markers[i],"catalog",markerCatalog);
                markers[i].NumberText.fontSize=27;markers[i].NumberText.resizeTextMaxSize=27;markers[i].NumberText.resizeTextMinSize=16;
            }
            var character=Module(root.transform,"角色牌模块",160,316,"角色牌",out var charCount);
            var events=Module(root.transform,"事件牌模块",486,316,"事件牌",out var eventCount);
            var enterprise=Module(root.transform,"我的合作企业板模块",812,260,"我的合作企业板",out var enterpriseCount);
            var token=Module(root.transform,"Token 模块",1082,678,"Token",out var unusedCount);unusedCount.gameObject.SetActive(false);
            var charPile=Pile(character,charCount,true);var eventPile=Pile(events,eventCount,false);
            var enterpriseArea=Picture("合作企业板内容",enterprise,20,80,220,360,null,false,true);enterpriseArea.color=Color.clear;enterpriseArea.gameObject.AddComponent<InformationContentInput>();
            var board=Rect("合作企业原板",enterpriseArea.transform,0,0,220,350).gameObject.AddComponent<RawImage>();Stretch(board.rectTransform);board.raycastTarget=false;
            var aspect=board.gameObject.AddComponent<AspectRatioFitter>();aspect.aspectMode=AspectRatioFitter.AspectMode.FitInParent;aspect.aspectRatio=650f/1850;
            var enterpriseEmpty=Label("无合作企业板",enterprise,20,220,220,90,"尚未放回合作企业板",22,Muted,TextAnchor.MiddleCenter);
            var returned=Label("企业板状态",enterprise,20,480,220,40,"已放回 · 1",22,Muted,TextAnchor.MiddleCenter);
            var scrollRoot=Rect("Token 滚动",token,14,76,648,412);var scroll=scrollRoot.gameObject.AddComponent<ScrollRect>();scroll.horizontal=false;scroll.vertical=true;scroll.scrollSensitivity=30;scroll.movementType=ScrollRect.MovementType.Clamped;
            scrollRoot.gameObject.AddComponent<InformationContentInput>();
            var viewport=Picture("Token 视口",scrollRoot,0,0,624,412,null,false,true);viewport.color=Color.clear;viewport.gameObject.AddComponent<RectMask2D>();scroll.viewport=viewport.rectTransform;
            var content=Rect("Token 内容",viewport.transform,0,0,624,0);content.anchorMin=new Vector2(0,1);content.anchorMax=new Vector2(1,1);content.sizeDelta=Vector2.zero;scroll.content=content;
            var hit=Picture("滚动条命中区",scrollRoot,632,0,24,412,null,false,true);hit.color=Color.clear;
            var track=Picture("滚动轨道",hit.transform,8,0,8,412,Art("GameBox","scrollbar/track"),true);
            var sliding=Rect("滑块移动区",hit.transform,0,0,24,412);Stretch(sliding);
            var handle=Picture("滑块",sliding,8,0,8,72,Art("GameBox","scrollbar/thumb-default"),true);handle.rectTransform.anchorMin=new Vector2(0,0);handle.rectTransform.anchorMax=new Vector2(1,1);handle.rectTransform.pivot=new Vector2(.5f,.5f);handle.rectTransform.anchoredPosition=Vector2.zero;handle.rectTransform.sizeDelta=new Vector2(-16,0);
            var bar=hit.gameObject.AddComponent<Scrollbar>();bar.targetGraphic=handle;bar.handleRect=handle.rectTransform;bar.direction=Scrollbar.Direction.BottomToTop;bar.transition=Selectable.Transition.SpriteSwap;bar.spriteState=new SpriteState{highlightedSprite=Art("GameBox","scrollbar/thumb-hover"),pressedSprite=Art("GameBox","scrollbar/thumb-drag")};scroll.verticalScrollbar=bar;scroll.verticalScrollbarVisibility=ScrollRect.ScrollbarVisibility.AutoHide;
            var item=Rect("Token 条目模板",content,0,0,168,126);var image=Picture("原图",item,0,13,110,100,null);image.rectTransform.anchorMax=new Vector2(1,1);image.rectTransform.sizeDelta=new Vector2(-58,100);image.preserveAspect=true;
            var number=Label("数量",item,110,0,58,126,"",27,Ink,TextAnchor.MiddleCenter);number.rectTransform.anchorMin=number.rectTransform.anchorMax=new Vector2(1,1);number.rectTransform.anchoredPosition=new Vector2(-58,0);
            var missing=Label("无贴图提示",item,0,13,110,100,"暂无图像",18,Muted,TextAnchor.MiddleCenter);
            var tokenView=item.gameObject.AddComponent<GameBoxTokenItemView>();Bind(tokenView,"artwork",image,"count",number,"missingArtwork",missing);item.gameObject.SetActive(false);
            var tokensEmpty=Label("无 Token",token,14,220,624,90,"暂无放回的 Token",22,Muted,TextAnchor.MiddleCenter);
            Picture("页脚分隔线",root.transform,161,856,1598,2,Art("GameBox","window/footer-divider"));
            Label("关闭提示",root.transform,161,876,1598,60,"单击牌堆查看 · 点击空白处关闭",22,Muted,TextAnchor.MiddleCenter);
            var view=root.AddComponent<GameBoxPageView>();Bind(view,"dismiss",root.GetComponent<InformationPageDismiss>(),"characterPile",charPile,"eventPile",eventPile,"playerMarkers",Array.ConvertAll(markers,r=>(UnityEngine.Object)r),"enterpriseImage",board,"enterpriseAspect",aspect,"enterpriseCount",enterpriseCount,"enterpriseEmpty",enterpriseEmpty.gameObject,"enterpriseReturned",returned.gameObject,"enterprises",AssetDatabase.LoadAssetAtPath<EnterpriseBoardCatalog>("Assets/YC/Presentation/Enterprise/EnterpriseBoardCatalog.asset"),"artwork",AssetDatabase.LoadAssetAtPath<GameBoxArtworkCatalog>(Folder+"GameBoxArtworkCatalog.asset"),"tokenScroll",scroll,"tokenTemplate",tokenView,"tokensEmpty",tokensEmpty.gameObject);
            Save(root,"GameBoxPage");
        }
        private static void CreateArtworkCatalog()
        {
            var catalog=ScriptableObject.CreateInstance<GameBoxArtworkCatalog>();var so=new SerializedObject(catalog);var entries=so.FindProperty("entries");
            var art=new Dictionary<string,Sprite>();
            foreach(var guid in AssetDatabase.FindAssets("t:MapVisualSpriteLibrary"))
            {
                var library=AssetDatabase.LoadAssetAtPath<MapVisualSpriteLibrary>(AssetDatabase.GUIDToAssetPath(guid));
                foreach(var token in library.ResourceTokens)
                    if(token.Sprite!=null)art["resource-"+token.ResourceType+"-"+token.Amount]=token.Sprite;
            }
            entries.arraySize=art.Count;var i=0;foreach(var pair in art)
            {var entry=entries.GetArrayElementAtIndex(i++);entry.FindPropertyRelative("visualKey").stringValue=pair.Key;entry.FindPropertyRelative("sprite").objectReferenceValue=pair.Value;}
            so.ApplyModifiedPropertiesWithoutUndo();AssetDatabase.CreateAsset(catalog,Folder+"GameBoxArtworkCatalog.asset");
        }
        private static void Save(GameObject root,string name)
        {root.SetActive(false);PrefabUtility.SaveAsPrefabAsset(root,Prefabs+name+".prefab");UnityEngine.Object.DestroyImmediate(root);}
    }
}
#endif
