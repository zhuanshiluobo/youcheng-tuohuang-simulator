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
