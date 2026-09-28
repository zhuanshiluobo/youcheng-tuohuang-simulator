"""一次性修改指定共享 HUD YAML；只在 UI-003 人工执行，不属于测试或启动流程。"""

from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
PREFAB = ROOT / "Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab"
text = PREFAB.read_text(encoding="utf-8")
if "m_Name: Persistent Gameplay Bars" in text:
    raise SystemExit("UI-003 常驻栏已经存在，停止重复写入。")

base = 7003000000000000000
next_id = base
blocks = []


def uid():
    global next_id
    next_id += 1
    return next_id


def add(typ, ident, body):
    blocks.append(f"--- !u!{typ} &{ident}\n{body}")


def node(name, parent, anchor_min=(0, 0), anchor_max=(1, 1), pivot=(0.5, 0.5),
         pos=(0, 0), size=(0, 0), children=(), image=None, button=False,
         label=None, font=None, font_size=22, raycast=False, extra_components=(),
         text_color=(0.94, 0.85, 0.66, 1)):
    go, rect = uid(), uid()
    renderer = uid() if image is not None or label is not None else None
    graphic = uid() if renderer else None
    btn = uid() if button else None
    components = [rect] + ([renderer, graphic] if renderer else []) + ([btn] if btn else []) + list(extra_components)
    comp_lines = "\n".join(f"  - component: {{fileID: {c}}}" for c in components)
    add(1, go, f"""GameObject:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  serializedVersion: 6
  m_Component:
{comp_lines}
  m_Layer: 5
  m_Name: {name}
  m_TagString: Untagged
  m_Icon: {{fileID: 0}}
  m_NavMeshLayer: 0
  m_StaticEditorFlags: 0
  m_IsActive: 1
""")
    child_lines = "\n".join(f"  - {{fileID: {c}}}" for c in children)
    add(224, rect, f"""RectTransform:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {go}}}
  m_LocalRotation: {{x: 0, y: 0, z: 0, w: 1}}
  m_LocalPosition: {{x: 0, y: 0, z: 0}}
  m_LocalScale: {{x: 1, y: 1, z: 1}}
  m_ConstrainProportionsScale: 0
  m_Children:{chr(10) + child_lines if child_lines else ' []'}
  m_Father: {{fileID: {parent}}}
  m_LocalEulerAnglesHint: {{x: 0, y: 0, z: 0}}
  m_AnchorMin: {{x: {anchor_min[0]}, y: {anchor_min[1]}}}
  m_AnchorMax: {{x: {anchor_max[0]}, y: {anchor_max[1]}}}
  m_AnchoredPosition: {{x: {pos[0]}, y: {pos[1]}}}
  m_SizeDelta: {{x: {size[0]}, y: {size[1]}}}
  m_Pivot: {{x: {pivot[0]}, y: {pivot[1]}}}
""")
    if renderer:
        add(222, renderer, f"""CanvasRenderer:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {go}}}
  m_CullTransparentMesh: 1
""")
        if image is not None:
            sprite = image
            add(114, graphic, f"""MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {go}}}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: 11500000, guid: fe87c0e1cc204ed48ad3b37840f39efc, type: 3}}
  m_Name:
  m_EditorClassIdentifier:
  m_Material: {{fileID: 0}}
  m_Color: {{r: 1, g: 1, b: 1, a: 1}}
  m_RaycastTarget: {1 if raycast else 0}
  m_RaycastPadding: {{x: 0, y: 0, z: 0, w: 0}}
  m_Maskable: 1
  m_OnCullStateChanged:
    m_PersistentCalls:
      m_Calls: []
  m_Sprite: {{fileID: 21300000, guid: {sprite}, type: 3}}
  m_Type: 1
  m_PreserveAspect: 0
  m_FillCenter: 1
  m_FillMethod: 4
  m_FillAmount: 1
  m_FillClockwise: 1
  m_FillOrigin: 0
  m_UseSpriteMesh: 0
  m_PixelsPerUnitMultiplier: 1
""")
        else:
            add(114, graphic, f"""MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {go}}}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: 11500000, guid: 5f7201a12d95ffc409449d95f23cf332, type: 3}}
  m_Name:
  m_EditorClassIdentifier:
  m_Material: {{fileID: 0}}
  m_Color: {{r: {text_color[0]}, g: {text_color[1]}, b: {text_color[2]}, a: {text_color[3]}}}
  m_RaycastTarget: 0
  m_RaycastPadding: {{x: 0, y: 0, z: 0, w: 0}}
  m_Maskable: 1
  m_OnCullStateChanged:
    m_PersistentCalls:
      m_Calls: []
  m_FontData:
    m_Font: {{fileID: 12800000, guid: {font}, type: 3}}
    m_FontSize: {font_size}
    m_FontStyle: 0
    m_BestFit: 0
    m_MinSize: 12
    m_MaxSize: {font_size}
    m_Alignment: 4
    m_AlignByGeometry: 0
    m_RichText: 0
    m_HorizontalOverflow: 0
    m_VerticalOverflow: 0
    m_LineSpacing: 1
  m_Text: {label}
""")
    if btn:
        add(114, btn, f"""MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {go}}}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: 11500000, guid: 4e29b1a8efbd4b44bb3f3716e73f07ff, type: 3}}
  m_Name:
  m_EditorClassIdentifier:
  m_Navigation:
    m_Mode: 3
    m_WrapAround: 0
    m_SelectOnUp: {{fileID: 0}}
    m_SelectOnDown: {{fileID: 0}}
    m_SelectOnLeft: {{fileID: 0}}
    m_SelectOnRight: {{fileID: 0}}
  m_Transition: 1
  m_Colors:
    m_NormalColor: {{r: 1, g: 1, b: 1, a: 1}}
    m_HighlightedColor: {{r: 0.96, g: 0.96, b: 0.96, a: 1}}
    m_PressedColor: {{r: 0.78, g: 0.78, b: 0.78, a: 1}}
    m_SelectedColor: {{r: 0.96, g: 0.96, b: 0.96, a: 1}}
    m_DisabledColor: {{r: 0.78, g: 0.78, b: 0.78, a: 0.5}}
    m_ColorMultiplier: 1
    m_FadeDuration: 0.1
  m_SpriteState:
    m_HighlightedSprite: {{fileID: 0}}
    m_PressedSprite: {{fileID: 0}}
    m_SelectedSprite: {{fileID: 0}}
    m_DisabledSprite: {{fileID: 0}}
  m_AnimationTriggers:
    m_NormalTrigger: Normal
    m_HighlightedTrigger: Highlighted
    m_PressedTrigger: Pressed
    m_SelectedTrigger: Selected
    m_DisabledTrigger: Disabled
  m_Interactable: 1
  m_TargetGraphic: {{fileID: {graphic}}}
  m_OnClick:
    m_PersistentCalls:
      m_Calls: []
""")
    return go, rect, graphic, btn


def sprite(name):
    path = ROOT / "Assets/YC/Presentation/Ui002/Sprites/Frontier31" / (name + ".png.meta")
    return re.search(r"^guid: (\w+)$", path.read_text(), re.M).group(1)


font_regular = "43e9d69dcca63724db85852acd46004d"
font_emphasis = "5c5a187d1eb86944c93f847ec521c324"
font_ui_number = "0a6c131175d7afb4daca1a87a679f835"

# 先分配父节点 ID，随后添加子节点；序列化块的物理顺序不影响 Unity 引用。
canvas_go, canvas_rect, canvas_component, scaler_component, raycaster_component, frame_component = [uid() for _ in range(6)]
safe_go, safe_rect = uid(), uid()
top_go, top_rect, top_renderer, top_image = [uid() for _ in range(4)]
bottom_go, bottom_rect, bottom_renderer, bottom_image = [uid() for _ in range(4)]

content_go, content_rect, _, _ = node("Gameplay ContentRect", 51262339496131993)
top_frame = node("Top Frame", top_rect, image=sprite("topbar-frame"), raycast=False)[1]
bottom_frame = node("Bottom Frame", bottom_rect, image=sprite("bottom-frame"), raycast=False)[1]
round_go, round_rect, round_text, _ = node("Round Number", top_rect, (0, .5), (0, .5), pos=(95, 0), size=(90, 54), label='"0"', font=font_ui_number, font_size=32, text_color=(.25, .18, .1, 1))
phase_go, phase_rect, phase_text, _ = node("Current Phase", top_rect, (0, .5), (0, .5), pos=(300, 0), size=(280, 55), label='""', font=font_regular, text_color=(.25, .18, .1, 1))
red_go, red_rect, red_text, _ = node("Red Zone State", top_rect, (.5, .5), (.5, .5), pos=(0, 0), size=(260, 55), label='""', font=font_regular, text_color=(.25, .18, .1, 1))
settings_label = node("Settings Label", 0, label='"设置"', font=font_emphasis, font_size=20, text_color=(.25, .18, .1, 1))
settings_go, settings_rect, _, settings_btn = node("Settings Button", top_rect, (1, .5), (1, .5), pos=(-84, 0), size=(136, 54), children=(settings_label[1],), image=sprite("topbar-tool-default"), button=True, raycast=True)
fold_label = node("Effect Fold Label", 0, label='"无待处理结算"', font=font_emphasis, font_size=18, text_color=(.25, .18, .1, 1))
fold_go, fold_rect, _, fold_btn = node("Effect Fold Button", top_rect, (1, .5), (1, .5), pos=(-236, 0), size=(152, 54), children=(fold_label[1],), image=sprite("topbar-tool-default"), button=True, raycast=True)
long_go, long_rect, long_text, _ = node("Action Summary", bottom_rect, (.5, .5), (.5, .5), pos=(0, 0), size=(630, 62), label='""', font=font_regular, font_size=18)
short_go, short_rect, short_text, _ = node("Short Action Summary", bottom_rect, (.5, .5), (.5, .5), pos=(0, 0), size=(330, 62), label='""', font=font_regular, font_size=17)
undo_label = node("Undo Label", 0, label='"撤销暂不可用"', font=font_regular, font_size=18)
undo_go, undo_rect, _, undo_btn = node("Undo Button", bottom_rect, (0, .5), (0, .5), pos=(115, 0), size=(190, 62), children=(undo_label[1],), image=sprite("undo-disabled"), button=True, raycast=True)

# 修正按钮标签父引用。
for child, parent in ((settings_label[1], settings_rect), (fold_label[1], fold_rect), (undo_label[1], undo_rect)):
    for i, block in enumerate(blocks):
        if block.startswith(f"--- !u!224 &{child}\n"):
            blocks[i] = block.replace("m_Father: {fileID: 0}", f"m_Father: {{fileID: {parent}}}")
            break

for go, rect, children, image in (
    (top_go, top_rect, (top_frame, round_rect, phase_rect, red_rect, settings_rect, fold_rect), sprite("topbar-bg")),
    (bottom_go, bottom_rect, (bottom_frame, long_rect, short_rect, undo_rect, 4064016372654079838), sprite("bottom-bar")),
):
    comps = f"  - component: {{fileID: {rect}}}\n  - component: {{fileID: {top_renderer if go == top_go else bottom_renderer}}}\n  - component: {{fileID: {top_image if go == top_go else bottom_image}}}"
    add(1, go, f"""GameObject:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  serializedVersion: 6
  m_Component:
{comps}
  m_Layer: 5
  m_Name: {'Persistent Top Bar' if go == top_go else 'Persistent Bottom Bar'}
  m_TagString: Untagged
  m_Icon: {{fileID: 0}}
  m_NavMeshLayer: 0
  m_StaticEditorFlags: 0
  m_IsActive: 1
""")
    child_lines = "\n".join(f"  - {{fileID: {c}}}" for c in children)
    is_top = go == top_go
    add(224, rect, f"""RectTransform:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {go}}}
  m_LocalRotation: {{x: 0, y: 0, z: 0, w: 1}}
  m_LocalPosition: {{x: 0, y: 0, z: 0}}
  m_LocalScale: {{x: 1, y: 1, z: 1}}
  m_ConstrainProportionsScale: 0
  m_Children:
{child_lines}
  m_Father: {{fileID: {safe_rect}}}
  m_LocalEulerAnglesHint: {{x: 0, y: 0, z: 0}}
  m_AnchorMin: {{x: 0, y: {1 if is_top else 0}}}
  m_AnchorMax: {{x: 1, y: {1 if is_top else 0}}}
  m_AnchoredPosition: {{x: 0, y: 0}}
  m_SizeDelta: {{x: 0, y: {72 if is_top else 96}}}
  m_Pivot: {{x: 0.5, y: {1 if is_top else 0}}}
""")
    renderer = top_renderer if is_top else bottom_renderer
    graphic = top_image if is_top else bottom_image
    add(222, renderer, f"""CanvasRenderer:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {go}}}
  m_CullTransparentMesh: 1
""")
    add(114, graphic, f"""MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {go}}}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: 11500000, guid: fe87c0e1cc204ed48ad3b37840f39efc, type: 3}}
  m_Name:
  m_EditorClassIdentifier:
  m_Material: {{fileID: 0}}
  m_Color: {{r: 1, g: 1, b: 1, a: 1}}
  m_RaycastTarget: 1
  m_RaycastPadding: {{x: 0, y: 0, z: 0, w: 0}}
  m_Maskable: 1
  m_OnCullStateChanged:
    m_PersistentCalls:
      m_Calls: []
  m_Sprite: {{fileID: 21300000, guid: {image}, type: 3}}
  m_Type: 1
  m_PreserveAspect: 0
  m_FillCenter: 1
  m_FillMethod: 4
  m_FillAmount: 1
  m_FillClockwise: 1
  m_FillOrigin: 0
  m_UseSpriteMesh: 0
  m_PixelsPerUnitMultiplier: 1
""")

add(1, safe_go, f"""GameObject:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  serializedVersion: 6
  m_Component:
  - component: {{fileID: {safe_rect}}}
  m_Layer: 5
  m_Name: Screen Safe Area
  m_TagString: Untagged
  m_Icon: {{fileID: 0}}
  m_NavMeshLayer: 0
  m_StaticEditorFlags: 0
  m_IsActive: 1
""")
add(224, safe_rect, f"""RectTransform:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {safe_go}}}
  m_LocalRotation: {{x: 0, y: 0, z: 0, w: 1}}
  m_LocalPosition: {{x: 0, y: 0, z: 0}}
  m_LocalScale: {{x: 1, y: 1, z: 1}}
  m_ConstrainProportionsScale: 0
  m_Children:
  - {{fileID: {top_rect}}}
  - {{fileID: {bottom_rect}}}
  m_Father: {{fileID: {canvas_rect}}}
  m_LocalEulerAnglesHint: {{x: 0, y: 0, z: 0}}
  m_AnchorMin: {{x: 0, y: 0}}
  m_AnchorMax: {{x: 1, y: 1}}
  m_AnchoredPosition: {{x: 0, y: 0}}
  m_SizeDelta: {{x: 0, y: 0}}
  m_Pivot: {{x: 0.5, y: 0.5}}
""")
add(1, canvas_go, f"""GameObject:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  serializedVersion: 6
  m_Component:
  - component: {{fileID: {canvas_rect}}}
  - component: {{fileID: {canvas_component}}}
  - component: {{fileID: {scaler_component}}}
  - component: {{fileID: {raycaster_component}}}
  - component: {{fileID: {frame_component}}}
  m_Layer: 5
  m_Name: Persistent Gameplay Bars
  m_TagString: Untagged
  m_Icon: {{fileID: 0}}
  m_NavMeshLayer: 0
  m_StaticEditorFlags: 0
  m_IsActive: 1
""")
add(224, canvas_rect, f"""RectTransform:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {canvas_go}}}
  m_LocalRotation: {{x: 0, y: 0, z: 0, w: 1}}
  m_LocalPosition: {{x: 0, y: 0, z: 0}}
  m_LocalScale: {{x: 0, y: 0, z: 0}}
  m_ConstrainProportionsScale: 0
  m_Children:
  - {{fileID: {safe_rect}}}
  m_Father: {{fileID: 6352980119307510113}}
  m_LocalEulerAnglesHint: {{x: 0, y: 0, z: 0}}
  m_AnchorMin: {{x: 0, y: 0}}
  m_AnchorMax: {{x: 0, y: 0}}
  m_AnchoredPosition: {{x: 0, y: 0}}
  m_SizeDelta: {{x: 0, y: 0}}
  m_Pivot: {{x: 0, y: 0}}
""")
add(223, canvas_component, f"""Canvas:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {canvas_go}}}
  m_Enabled: 1
  serializedVersion: 3
  m_RenderMode: 0
  m_Camera: {{fileID: 0}}
  m_PlaneDistance: 100
  m_PixelPerfect: 0
  m_ReceivesEvents: 1
  m_OverrideSorting: 0
  m_OverridePixelPerfect: 0
  m_SortingBucketNormalizedSize: 0
  m_VertexColorAlwaysGammaSpace: 0
  m_AdditionalShaderChannelsFlag: 0
  m_UpdateRectTransformForStandalone: 0
  m_SortingLayerID: 0
  m_SortingOrder: 30000
  m_TargetDisplay: 0
""")
for target, script, fields in (
    (scaler_component, "0cd44c1031e13a943bb63640046fad76", """  m_UiScaleMode: 1
  m_ReferencePixelsPerUnit: 100
  m_ScaleFactor: 1
  m_ReferenceResolution: {x: 1920, y: 1080}
  m_ScreenMatchMode: 0
  m_MatchWidthOrHeight: 0.5
  m_PhysicalUnit: 3
  m_FallbackScreenDPI: 96
  m_DefaultSpriteDPI: 96
  m_DynamicPixelsPerUnit: 1
  m_PresetInfoIsWorld: 0
"""),
    (raycaster_component, "dc42784cf147c0c48a680349fa168899", """  m_IgnoreReversedGraphics: 1
  m_BlockingObjects: 0
  m_BlockingMask:
    serializedVersion: 2
    m_Bits: 4294967295
"""),
    (frame_component, "350ec42a39a44ccaba837780f73e8734", f"""  barCanvas: {{fileID: {canvas_component}}}
  safeArea: {{fileID: {safe_rect}}}
  topBar: {{fileID: {top_rect}}}
  bottomBar: {{fileID: {bottom_rect}}}
  contentRect: {{fileID: {content_rect}}}
  roundNumberText: {{fileID: {round_text}}}
  phaseText: {{fileID: {phase_text}}}
  redZoneText: {{fileID: {red_text}}}
  summaryText: {{fileID: {long_text}}}
  shortSummaryText: {{fileID: {short_text}}}
  settingsButton: {{fileID: {settings_btn}}}
  foldButton: {{fileID: {fold_btn}}}
  foldButtonText: {{fileID: {fold_label[2]}}}
  undoButton: {{fileID: {undo_btn}}}
  endActionButton: {{fileID: 7982745628680004908}}
  longSummary: {{fileID: {long_go}}}
  shortSummary: {{fileID: {short_go}}}
  redZoneClosedText: "红区未开放"
  redZoneOpenText: "红区已开放"
  foldVisibleText: "收起"
  foldSuspendedText: "展开"
  foldUnavailableText: "无待处理结算"
  setupPhaseText: "准备"
  entrancePhaseText: "入场"
  roundStartPhaseText: "回合开始"
  characterCoverPhaseText: "盖放角色牌"
  firstActionPhaseText: "第一行动轮"
  secondActionPhaseText: "第二行动轮"
  collectionPhaseText: "采集"
  cleanupPhaseText: "收尾"
  finalScoringPhaseText: "最终计分"
  gameOverPhaseText: "终局"
  wideMinWidth: 1540
  compactMaxWidth: 1060
  compactMaxHeight: 670
  minimumControlSpacing: 12
  modeHysteresis: 24
  wideTopHeight: 72
  standardTopHeight: 80
  compactTopHeight: 104
  wideBottomHeight: 96
  standardBottomHeight: 104
  compactBottomHeight: 120
  contentGap: 8
  compactTopRowOffset: 24
  compactStatusRowOffset: -29
  normalStatusHeight: 55
  compactStatusHeight: 42
"""),
):
    add(114, target, f"""MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {canvas_go}}}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: 11500000, guid: {script}, type: 3}}
  m_Name:
  m_EditorClassIdentifier:
{fields}""")

# 修改已存在的父子关系及 View 引用。每处都要求唯一命中。
def replace_once(old, new):
    global text
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"期望唯一命中，实际 {count}: {old[:90]}")
    text = text.replace(old, new, 1)


replace_once("  - {fileID: 3468181478635267104}\n  m_Father: {fileID: 0}",
             f"  - {{fileID: 3468181478635267104}}\n  - {{fileID: {canvas_rect}}}\n  m_Father: {{fileID: 0}}")
replace_once("  - {fileID: 8909324864607229589}\n  m_Father: {fileID: 6352980119307510113}",
             f"  - {{fileID: 8909324864607229589}}\n  - {{fileID: {content_rect}}}\n  m_Father: {{fileID: 6352980119307510113}}")
replace_once("  - {fileID: 4064016372654079838}\n  - {fileID: 3955035920663877645}",
             "  - {fileID: 3955035920663877645}")
replace_once("  m_Father: {fileID: 7957545507481678560}\n  m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}\n  m_AnchorMin: {x: 0.5, y: 1}\n  m_AnchorMax: {x: 0.5, y: 1}\n  m_AnchoredPosition: {x: 0, y: -340}\n  m_SizeDelta: {x: 150, y: 42}",
             f"  m_Father: {{fileID: {bottom_rect}}}\n  m_LocalEulerAnglesHint: {{x: 0, y: 0, z: 0}}\n  m_AnchorMin: {{x: 1, y: 0.5}}\n  m_AnchorMax: {{x: 1, y: 0.5}}\n  m_AnchoredPosition: {{x: -118, y: 0}}\n  m_SizeDelta: {{x: 205, y: 62}}")
replace_once("  canvas: {fileID: 9063159915585620305}\n  tabletopCanvas:",
             f"  canvas: {{fileID: 9063159915585620305}}\n  frame: {{fileID: {frame_component}}}\n  tabletopCanvas:")

PREFAB.write_text(text + "".join(blocks), encoding="utf-8")
print(f"已更新 {PREFAB}；新增 {len(blocks)} 个序列化对象。")
