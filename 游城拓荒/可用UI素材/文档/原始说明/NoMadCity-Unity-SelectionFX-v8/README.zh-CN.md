# v8：去掉选中框外额外出现的细白焦点框

根据你补充的企业特效截图，金色框已围住正确的正文区域，明显可见其外侧还有一圈更大的浅白细线。代码中确实存在对应的额外绘制：v3～v7 的 Shader 在 focused=true 时，会在金框外 **7 个 Canvas 单位** 再画 1.5 单位宽的白色焦点线。

`SelectionEffectInput.OnSelect` 会把 focused 打开。EventSystem 的选中并不只来自键盘，鼠标点击 Button 也可能触发，所以看起来像“金框没盖住另一个框”。仅靠截图不能排除原贴图残留，但这条额外焦点线是已确认的代码行为，与图中的双轮廓相符。

## 本版修复

- 删除本效果额外的白色焦点外框，包括对应 alpha 和提白计算。
- 不移动 Overlay，不改变尺寸/锚点，不隐藏原 Image，不切换原贴图，不改命中区或原游戏键盘导航。
- 选中金框、内侧副线、金属纹理、更亮效果、双光点 0.8秒半圈换位 + 0.4秒停顿完整保留。
- 未选中仍不绘制任何本效果；Runtime 脚本与 v7/v6 保持相同，不改变原业务。
- `SetFocused` / 输入转发 API 保留以兼容已有调用，但本装饰 Shader 不再响应它绘制额外外框。原游戏自身的键盘导航可视反馈由原游戏继续负责。

## 更新与验收

如果目前是 v6/v7，只需覆盖 `Resources/NoMadCitySelection.shader`，保留 `.meta` 后 Reimport；如果仍是 v3/v4，请覆盖本包整个同名目录（尤其 Runtime/SelectionEffectGraphic.cs），保留 `.meta`，以同时取得最终半圈停顿、纹理与相对选中计时。不要并装重复脚本。

1. 在同一企业特效项上点击选中：外侧那圈额外浅白细线应不再由本效果产生；金框和动画仍在。
2. 鼠标点击和键盘导航分别测试，原游戏自己的导航操作不应受影响。
3. 如果浅色轮廓仍在，临时关闭这个 SelectionEffectGraphic 看它是否仍存在：仍存在就来自其他图形/原贴图；再提供原框与 Overlay 的层级/RectTransform/Image设置，才能决定对齐还是仅隐藏独立旧视觉框。不要盲目关掉带点击/Mask/内容的对象。
4. **不必先执行 v7 的重挂对齐操作。** v7 解决的是实际矩形不一致，无法移除 Shader 自己画出的焦点框。本图不足以证明还需要重挂层级。

离线检查确认 Shader 不再包含焦点外线分支，并保留其余选中行为；没有 Unity 编辑器，**未执行 Unity 编译、Play Mode、GPU 渲染或构建**。不能只凭静态截图宣称项目问题已实测解决。以下保留 v7 对齐工具与历史说明作为必要时的备选。

---

# v7：企业特效框对齐辅助（不擅自猜项目尺寸）

## 已能确认的原因与边界

旧组件只按自己的 RectTransform 画框，不会自动读取原可选框。资料里“整行命中区”和“可见特效框”不是同一个矩形：例如 5 级整行 `(20,406,630,246)`，可见正文框 `(160,412,482,234)`，均为 650×1850 单板内源像素。按参考板宽 248 显示，二者左边相差约 53.42 单位，宽度相差约 56.47 单位，不能互相替用。其他等级也需用各自正文框。

另外原可选框是 320×112、20px 源边距的九宫格艺术贴图（预览目标边距 8），而程序框是切角几何轮廓；**矩形对齐不等于用一个不同形状的框完全遮住旧艺术纹理**。透明留白、Image Preserve Aspect、Sprite 九宫格、遮罩和层级都会影响实际覆盖。

没有你的当前项目、企业特效问题截图和层级数据，不能确认你遇到的是尺寸/坐标不同、旧纹理残留，还是被遮挡/裁切。以下是可靠的对齐工具，不声称已自动修好你的实际场景。

## 已有 Overlay：推荐按原可选框直接对齐

1. 在 **非 Play Mode** 选中当前 `SelectionEffectGraphic` 装饰对象。
2. 在其 Inspector 新增的 **Alignment Target** 拖入“这一行现有可选框 Image 所在 RectTransform”。不是整个企业板、命中按钮、完整等级行或正文图片。
3. 点击 **Align under target frame (reparents this overlay)**。它仅把这个 Overlay 移为目标框的子物体，锚点拉满、offset=0、局部旋转=0、缩放=1；已有对象引用及业务绑定保持。操作可 Undo。目标框的 Image、贴图、颜色、启用状态和业务数据不修改。
4. 此后原框在同一布局/缩放/滚动里变化，Overlay 作为子物体天然跟随；不再用另一套源坐标独立计算。**不要再让其他布局脚本单独改 Overlay 的尺寸位置。**
5. 选中、取消验证：未选中仍不生成任何新增装饰。若父框 GameObject 在选中态会被 SetActive(false)，它的子 Overlay 也会被隐藏；这种层级不能直接使用该方式，需要把父框改为常驻容器，或提供实际层级进一步适配。不要为此擅自关闭原命中区/遮罩。

新建可用：先选中原可选框，在菜单 **GameObject > UI > NoMadCity > Create Overlay Aligned To This Frame** 创建对齐子物体，然后由原业务调用它的 SetState。已有直接子 Overlay 时工具会选中它，不叠第二个。

此工具会改变 Overlay 的父级和遮罩继承，请在 prefab 备份/测试场景先验收。它只对齐 RectTransform 外矩形；如果 Image 使用透明内边距或 Preserve Aspect，可见边可能仍需精确 inset。不能承诺逐像素遮住九宫格旧框。

## 如果对齐后仍“盖不住”

- **位置/尺寸仍错**：提供企业特效选中截图，以及原可选框和 Overlay 的 Inspector（RectTransform、Image Type、Preserve Aspect）与层级。可以确定实际可见区域，不需要整包项目。
- **同一矩形但旧纹理露出**：这是两种轮廓叠画，不宜继续盲目加粗。需要确认原框是否独立的纯视觉 Image，然后在选中时只隐藏该独立旧框、取消选中恢复。v7 不自动做这个操作，避免误隐藏命中图形、原卡面、Mask 或无关业务对象。
- **只有一侧不见/滑动后消失**：检查父级 Mask/RectMask2D、滚动层级和外光裁切，不靠改框尺寸掩盖。

## 更新与不变项

可用 v7 完整目录覆盖 v6（保留 .meta，勿并装），新增 Editor 对齐工具。**Runtime 与 Shader 与 v6 逐字节一致**：提亮、纹理、左上/右下双光点 0.8秒半圈换位+0.4秒停顿、非选中不绘制都不变。只有工具和文档改动，不会自动移动现有场景对象；需要显式点对齐按钮。

已完成源码/几何/动画离线验证，未运行 Unity 编译、Play Mode、GPU 渲染、prefab Undo 或构建测试。以下为 v6 及历史说明。

---

# v6：选中框加入克制金属装饰，保留半圈换位与停顿

本版整合你最后三点要求：更亮、光点半圈换位后停顿、静态选框不要只是一圈单色线。

**新增静态层次**（无需素材贴图）：
- 主金框内部有明暗倒角与一条细金属刻槽，不再是纯平涂。
- 稀疏的短斜刻线模拟加工纹理，保持工业金属风格，不做随机噪点/闪烁。
- 四个切角有短加强高光与小铆钉式亮点，装饰限制在已有主框宽度内。
- 新装饰只修改原边框的颜色，不扩大透明轮廓、线宽或遮挡正文。移动光点在装饰上层，仍清楚可辨；没有额外图片，不改变原卡面。

**运动完整保留 v5**：每次选择从左上/右下同时出发，0.8 秒平滑加速再减速，转半圈换位，停 0.4 秒，再重复。并非 v4 的不停转。Reduced Motion 下只保留提亮后的静态装饰框。

**非选中保持 v3 原样**：运行时脚本与 v5 逐字节相同；Available / Unavailable 不生成任何本效果网格，悬浮/聚焦也不增加装饰。原贴图与业务状态不改动。

## 更新

从 v3 / v4 更新：覆盖本包 `Resources/NoMadCitySelection.shader` **和** `Runtime/SelectionEffectGraphic.cs`。从 v5 更新只需替换 Shader。保留已有 `.meta`，Reimport，再运行/构建。可直接覆盖整个同名目录，不要并装第二套。无需重置 prefab 或重新点预设。

## 快速验收

先选中并打开 Reduced Motion 看静态金框：应能见到刻槽、稀疏斜刻线与角上亮点；关闭 Reduced Motion 后 Replay，确认双光点按半圈换位/停顿节奏运行。取消选择后全部新增装饰应消失。不同 Canvas 缩放下细刻线可见度需要实机确认，不应靠无节制加粗解决。

已完成离线源码/几何/动画检查和 ZIP 检查；**尚未执行 Unity 编译、Play Mode、GPU 渲染或构建**，并不宣称已在你的项目实测。以下保留 v5 接入及历史说明，若冲突以 v6 本节为准。

---

# v5：更亮，左上/右下双光点半圈换位后停顿

按你最后指定的节奏实现，不再使用 v4 持续不停转的正弦速度方案：

1. **每次选中或 Replay，从左上、右下切角中点同时起步。**
2. **0.8 秒运动段**：平滑快速加速，再减速到停止，沿框各走半圈，完成换位。
3. **停留 0.4 秒**：两光点固定在交换后的角上。
4. 重复运动与停顿，1.2 秒一次换位，2.4 秒回到原位。两点始终对置。运动起止速度、加速度均为零，循环不瞬移、不倒退。

亮度沿用本次增强：原金色混入 30% 暖白金；内侧副线 alpha 0.72 → 0.90；外光系数 0.32 → 0.50；移动高光更接近象牙白。框形、线宽和光晕半径保持原样，不扩大到正文。沿边使用归一化角度映射，长宽比例会影响实际像素速度，但不会改变两点同时换位/停止的节拍。

**未选中维持 v3 原样**：非 Selected 仍清空网格后直接返回，不出现任何本效果边框；未恢复悬浮/焦点/不可用装饰，不改原游戏贴图与选择逻辑。运行时脚本只改动画时间为“从本次选中开始计时”，保证每次从指定角出发；所有非选中门控和徽标逻辑保持不变。

## 更新（重要）

从 v3 / v4 更新必须同时覆盖：
- `Assets/NoMadCitySelectionFX/Resources/NoMadCitySelection.shader`
- `Assets/NoMadCitySelectionFX/Runtime/SelectionEffectGraphic.cs`

保留项目现有 `.meta`，Reimport，重新运行/构建。只换 Shader 会继续使用旧全局时间，不能保证每次选择从左上/右下开始。也可覆盖整套同名目录；不要并装第二套。不需要重置 prefab、重绑状态或点击预设。新安装按后面原接入说明操作。

## 现场验收

1. Play Mode 下选中、取消、重新选中同一张卡，或点 DEBUG Force Selected + Replay：每次双光点都从左上/右下出发。
2. 观察至少 5 秒：两个光点同时加速、各转半圈减速停下、停留一小会，再换回来。不是全程匀速，也不是持续不停转。
3. 未选中、悬浮、键盘聚焦、不可用状态，与 v3 一样不增加任何本效果边框。原 UI 保留。
4. Reduced Motion 开启时保留提亮静态框，不出现移动光点；不擅自修改该偏好。若运行中切换 Reduced Motion，恢复时跟随本次选中经过的时间；点 Replay 可重置起点。
5. 实际 Mask / CanvasGroup / 多种尺寸与目标构建仍需在 Unity 验收。

已检查源代码、动画数学、未选中门控与 ZIP；**未运行 Unity 编译、Play Mode、GPU 渲染或实机构建**。看过你的截图，但静态截图不能验证动画。以下历史接入说明的视觉节奏、更新方式若与本节冲突，以 v5 本节为准。

---

# v3 修复说明：仅选中时叠加效果

v2 确实额外绘制了未选中 25% 透明度单线框，悬浮变为 40%，不可选为灰色虚线，焦点另有外线。这是新加的视觉，不是原贴图的问题。v3 取消所有非选中装饰，不改原板贴图，不靠降低透明度掩盖。

## 升级（请先备份）

- 用本包整个 `Assets/NoMadCitySelectionFX` 覆盖旧代码，**保留项目中已有 .meta**。不要另装第二套，也不要只换 Shader。
- 关键改动是 `SelectionEffectGraphic.cs` 在非 Selected 时清空网格后返回。Shader 文件与 v2 **逐字节一致**，选中主框、内副线、亮段、扫光、呼吸、Reduced Motion 均未改动。
- `SetState(Available)` / `SetState(Unavailable)` 会触发网格刷新，移除之前的选中框；再切回 Selected 会正常重新生成并播放入场。
- `selectedBadge` 仅能绑定本效果专用的选中标记，非 Selected 会隐藏；没有绑定就不新增标记。
- v3 删除了 `unavailableBadge` 的控制及 Inspector 项，**不会修改它原来引用的 GameObject 的激活状态**。若之前按 v2 新建了额外的锁/不可用标签，迁移时只停用那个新增对象（尤其是升级时已经 active 的对象）。不要停用原游戏的锁、原因、底板或正文；这些继续由原游戏管理。
- 非选中 hover / focus 不再由本效果增添边框。原游戏的 Button、导航、不可选资格和原因提示不变。

## 必做验收（尚未执行 Unity 测试）

1. 未选中：与关闭效果组件的原 UI 对比，不应多框；悬浮和键盘聚焦也不应出现本效果的框。
2. 选中：保持 v2 强双框和沿边亮段；至少观察 3 秒。Reduced Motion 时保留静态选中框。
3. Selected → Available → Selected → Unavailable：取消后立即无额外框，恢复选中可播放；专用选中徽标同步隐藏/显示。
4. 切窗、对象池复用、Mask、CanvasGroup、原不可用原因提示均需在实际工程检查。状态仍需由业务调用 SetState。

离线检查只覆盖源码结构、回归条件、数学与打包内容，不能证明 Unity 编译或真实 GPU 画面。没有运行 Unity 编译、Play Mode、构建或实机渲染。下面为沿用的 v2 接入细节（非选中行为以上述 v3 规则为准）。

---

# NoMadCity Unity / UGUI 程序化选择效果

这是一套可接入代码，不是已在你的游戏工程里运行过的成品。依据附件的企业、城市样式、卡牌、莱茵生命科室板设计；不附原素材，不修改原图，不切换 selected/available/unavailable 贴图。

## v2：解决“选中了但看不出，也不知道有没有动”

旧版默认 2 单位细线，只有 0.35 秒入场扫光，随后是非常轻微的整体呼吸。这不足以提供明确的持续选中反馈。不能据此断言你的工程绑定正确或动画没运行。

本版选中态不依赖动画也能辨认：常亮加粗金框＋内侧副线；另外两个象牙白短高亮段持续沿边走，2.4 秒一圈，不需要 Bloom。1.6 秒周期的外光呼吸仅作辅助。不覆盖正文，不改变原卡面，不用闪烁整卡。

### 更新与 30 秒验收

1. 先备份已有包。覆盖整个 `Assets/NoMadCitySelectionFX`，保留工程中已有 `.meta`，不要另装第二套同名脚本。新文件 `Runtime/SelectionEffectDemoControls.cs` 也要复制。
2. 原工程里选中装饰子物体的 `SelectionEffectGraphic`，点击 Inspector 的 **Apply Strong Visible Preset**。旧 prefab 会保留原序列化值，仅更新代码不会自动改变旧 Color / 线宽。按钮保留 State 和 Reduced Motion，不覆盖无障碍偏好。
3. 确认 Color alpha 为 1、对象已启用、上层 CanvasGroup alpha 不为 0，Reduced Motion 未勾选。进入 **Play Mode 的 Game 视图**，点 **DEBUG Force Selected + Replay**。应立即出现强双框和两个移动亮段；看至少 3 秒。再点 Force Available 应完全消失，只剩原有底板。不要把调试操作当作业务选择。
4. Inspector 的 **State** 应是 Selected，**Effect material loaded** 应为 True，**Last mesh animation clock** 应持续变化（48 秒回绕一次）。时钟变化只证明 CPU 网格数据在更新，不能证明 GPU 正确绘制。若状态马上恢复 Available，说明游戏逻辑覆盖了调试值，需要修正实际选择事件的 SetState 调用。
5. 若独立演示正常、实际界面不正常，优先检查覆盖层被原板/旧选框盖住、RectTransform 是否对准真实卡面、Mask 是否裁掉框、祖先 alpha、其他脚本重设 state，以及是否误把材质直接赋给普通 Image。可以先在空白测试场景使用菜单 **GameObject > UI > NoMadCity > Selection FX Comparison** 对照。
6. Reduced Motion 打开时双框仍明确保留，但沿边动画、入场扫光和呼吸全部停止，这是预期行为。诊断演示的 Freeze 按钮用于肉眼比较，不保存玩家偏好。

保持原板 Image；这个 Shader 仍依赖 SelectionEffectGraphic 的自定义顶点数据，不能单独放到普通 Image 代替选框。共用同一 Shader 算法没问题，每个组件仍独立传入状态/尺寸。只改变 Material 的 Inspector 数值不能代替这些数据。

### v2 验证边界

已执行离线源码、几何和动画数学检查；**没有 Unity 编辑器，未运行 Unity Shader/C# 编译、Play Mode、构建或 GPU 渲染**。请先跑独立对照再接实际场景。离线结果不能代替实机验收。若失败，回传 Game 视图短录屏、组件 Inspector 和 Console 第一条错误即可，不必重传 20MB 素材包。

## 历史：洋红色修复

旧版 Shader 将辅助函数命名为 `line`，与 HLSL 关键字冲突。本版把函数定义和两处调用统一改为 `SelectionOutline`；当时只改了函数名；本 v2 进一步修改视觉算法，保留已有 C# 状态接口。

更新方法：v2 请覆盖整个 `Assets/NoMadCitySelectionFX` 中的同名文件，而不是只覆盖 Shader；在 Unity 中右键 Reimport，确认 Console 没有该 Shader 编译错误后重新构建。已安装的工程无需删除旧目录、材质或 prefab。此包没有 `.meta` 文件，不要删除工程中已有的 `.meta`。

不要把此材质直接挂在普通 Image 上替代原板图片：它依赖 `SelectionEffectGraphic` 写入的自定义顶点数据。请保留原板 Image，并使用独立装饰子物体承载该组件，停用旧选框 Image。

本次已做关键词回归及原有离线检查，但环境无 Unity 编辑器/Shader 编译器，未运行 Unity 编译或构建。若仍洋红，请提供 Console 第一条相关 Shader error；若只在构建中出现，请一并提供 Player.log。

## 实现内容

- 程序化琥珀金切角框、轻微外发光；选中时1.6 秒周期的外光呼吸，并持续播放 2.4 秒一圈的双高亮沿边动画。
- 从其他状态进入 Selected 时播放 0.35 秒扫光，同一状态重复设置不会重播。
- v3 的 Available / Unavailable / 非选中 hover / 非选中 focus 均不绘制；原游戏自己处理不可选原因与键盘导航反馈。选中时可保留焦点线。
- Reduced Motion 关闭呼吸和扫光，仍保留选中强框、选中时的焦点线及可选的选中标记。
- UI Mask stencil 和 RectMask2D 硬裁切，CanvasGroup alpha；装饰层不拦截点击。
- 所有参数走顶点数据，而不是每帧修改 stencil 派生材质，避免 Mask 下材质缓存不同步。

## 适用范围与未验证内容

目标：Unity 2021.3+ / 2022.3 / Unity 6 的 UGUI（需安装 Unity UI）。使用内置 UI CG include 和普通透明 Pass，不需要 URP Shader Graph、Bloom 或后处理。设计面向 Built-in、URP 的 Screen Space Overlay；没有绑定某条渲染管线，但这些组合都尚未在 Unity 内编译验证。Camera/World Space、HDRP、XR、各移动 GPU 需自行验证，不宣称已兼容。不是 UI Toolkit 材质。

当前执行环境没有 Unity、C# 编译器或目标项目：只完成源码结构检查及几何/状态数学测试。不能将这些检查理解为 Unity Shader 编译、Play Mode、构建或实机测试通过。

RectMask2D 目前只支持硬裁切，不支持 softness。发光是透明颜色叠加，不是 HDR Bloom。边角使用平面距离近似，切角端点不是严格欧氏 SDF。数值单位为 Canvas 本地单位，配合 1920×1080 CanvasScaler；并非任何 Canvas 缩放下都固定物理像素。非均匀缩放会变形，应保持板和覆盖层等比。

每个组件持有一个可释放的材质实例，因此不保证跨组件合批。仅选中且动态时逐帧重建 4 个顶点；大量候选请做池化/可见性控制并用 Profiler 测量。不要将装饰组件所在物体用于 Mask 来源。

## 安装与快速查看

1. 将本包 Assets/NoMadCitySelectionFX 整个复制到工程 Assets。保留 Resources 目录，避免只靠 Shader.Find 导致构建剥离。
2. Unity 菜单 GameObject > UI > NoMadCity > Selection FX Comparison 生成五类比例的上下对照：上排 Available，下排 Selected。进入 Play Mode，左上角可暂停/恢复动画、重播选中扫光，显示 CPU 上传动画时钟。示例只有占位板，不接业务逻辑；使用 16:9 Game 视图。
3. 实际接入时，在现有候选板物体下新建 UI 空子物体，添加 SelectionEffectGraphic（不再添加 Image）。锚点铺满真实卡面/板面矩形，offset 为 0。不要铺满带透明留白的原贴图外框。
4. Graphic Color 设置为 #FFB026，alpha 1。默认基础线宽 3、切角 7、外光半径 10。覆盖层放在原板上方、状态标记和文字下方。
5. 关闭旧的状态边框 Image，避免双框。原板 Image 保持不变。原卡牌 card-selected.png 含勾，不要同时保留该图和新增的勾。
6. Canvas 的额外 UV 通道由脚本自动启用，不会在销毁时清除，以免破坏其他 UI。
7. 可选：将本效果专用的勾或“已选”标签绑定 selectedBadge。取消选中时会隐藏它。不要绑定原底板、正文或原游戏管理的 UI；不可用原因/锁交回原游戏管理，本组件不再管理 unavailableBadge。
8. 将 SelectionEffectInput 放在现有候选 Button/点击区，绑定 effect，仅转发 pointer hover 和 EventSystem focus。不要放在 raycastTarget=false 的装饰子物体上。Button 需正常 EventSystem/GraphicRaycaster 及键盘导航。

## 调用约定

```csharp
using NoMadCity.SelectionFX;
// 先由规则层确认资格，再设置视觉。
effect.SetState(SelectionEffectGraphic.SelectionState.Selected);
effect.SetState(SelectionEffectGraphic.SelectionState.Available);
effect.SetState(SelectionEffectGraphic.SelectionState.Unavailable);
effect.SetReducedMotion(userPrefersReducedMotion);
```

此组件不负责单选排他、支付、奖励、确认、数据持久化或 UI 无障碍朗读。你的选择控制器要把其他候选设为 Available，并再次验证确认对象。原参考要求：点击不可选项时清空待选、更新 inspected 供查看、禁用确认；不要将不可选项的 Button 彻底禁用到无法查看原文。可以让 Button 保持可交互，但点击处理只进入详情/原因分支，不选择或确认。

设置 SetState(Unavailable) 不会自动锁住原 Button 或其他入口，这必须由规则层保证。关闭窗口、切换候选、回池时由调用方重设 state、hover、focus；确认异步期间禁用提交且避免旧回调污染新窗口。

## 各界面的绑定

### 企业整板

Overlay 对应真实 650:1850 企业板，选中整板。玩家合作等级块不变、不跟着发光。原预览 8px 艺术九宫格边框不等于新 Shader 线宽：v2 基础线宽 3，Selected 实际线宽至少 4.5，增加内侧副线与持续沿边高亮，不模拟磨损金属纹理。

### 企业特效行

Overlay 只创建于规则数据明确指定的候选行，不给普通奖励行加候选框。正文框源坐标如下，按同一 boardScale、board position 和滚动偏移映射：

- 5级 (160,412,482,234)
- 4级 (160,674,482,228)
- 3级 (160,930,482,219)
- 2级 (160,1177,482,225)
- 1级 (160,1430,482,217)

以上原坐标为左上原点，UGUI 常用底部原点；若使用 top-left anchor + pivot (0,1)，anchoredPosition = (rx*scale,-ry*scale)。建议此类行框线宽 1.5、切角 4、外光 5，避免互相叠光。

### 卡牌

卡面保持 12:17。覆盖层贴真实卡面，不用原 card-selected.png 的整图尺寸。旧图 1054×1493 的安全内容区为 (80,130,896,1280)，若旧项目依赖这套映射，请先对齐实际卡面再放 Shader。新勾单独放在右上板外预留位。建议线宽 2、切角 5、外光 9。卡面位置尺寸不能因选中改变。

### 城市样式

Overlay 绑定样式卡本体/现有候选区域，不是城市整块面板。详情模式只表达 inspected；不可把查看误当宣告或可提交状态。宣告模式的设施格预选是独立状态，不应从卡牌选中框推断资格。建议线宽 1.5–2、切角 5、外光 7。

### 莱茵生命科室板

覆盖整张 516:1000 科室板；预览实际 280×542.635659。2/3/5 或更多候选共用。2–5级奖励行只用于显示/提示，不能变成选择按钮。当前激活科室在中途切换时不可再次确认。建议线宽 2、切角 7、外光 9；勾放右侧预留位，参考 (cardX+286,272) 20×20。企业板固定，候选板、Overlay、标记随同一 ScrollRect 内容滚动并统一裁切。

## 接入后必须检查

- Built-in 或你的 URP 版本下 Shader 无报错；Editor 与目标平台 Development Build 都测试。
- 2/3/5 候选、极窄特效行、极高企业板、CanvasScaler 缩放与窗口 resize。
- ScrollRect / RectMask2D 边界、嵌套 Mask stencil、CanvasGroup fade，边缘光裁切正常。
- 鼠标、触摸、键盘 focus；不可选仍可查看但无法确认。
- 快速切换选中、重复点击、取消关闭、对象池复用、timeScale=0、异步确认失败与旧回调。
- Reduced Motion 静态强框与勾仍清楚；不可选显示原因；文字从不被遮盖。
- 多个选中对象是否符合业务，单选排他由业务测试保证。
- Profile UI rebuild、材质数、overdraw。发光留白至少 2×glowRadius，父视口边缘需预留或接受裁切。

## 文件

Runtime/SelectionEffectGraphic.cs：状态与几何控制器。
Runtime/SelectionEffectInput.cs：输入视觉转发，不决定选择。
Resources/NoMadCitySelection.shader：程序化效果。
Editor/：Inspector 与对照场景生成菜单。
Tests/validate.py：离线数学/打包检查，不依赖 Unity。

Unity 文档：
https://docs.unity.com/en-us/engine/6000.0/script-reference/unityengine/canvas/additionalshaderchannels
