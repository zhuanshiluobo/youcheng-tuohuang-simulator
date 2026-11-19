# NoMadCity Unity / UGUI 程序化选择效果

这是一套可接入代码，不是已在你的游戏工程里运行过的成品。依据附件的企业、城市样式、卡牌、莱茵生命科室板设计；不附原素材，不修改原图，不切换 selected/available/unavailable 贴图。

## 实现内容

- 程序化琥珀金切角框、轻微外发光；选中时约 3 秒一次的低幅呼吸。
- 从其他状态进入 Selected 时播放 0.35 秒扫光，同一状态重复设置不会重播。
- 不可选改为静态灰色虚线，不响应悬浮；独立白金色外线表达键盘焦点。
- Reduced Motion 关闭呼吸和扫光，仍保留选中强框、虚线、焦点线及独立状态标记。
- UI Mask stencil 和 RectMask2D 硬裁切，CanvasGroup alpha；装饰层不拦截点击。
- 所有参数走顶点数据，而不是每帧修改 stencil 派生材质，避免 Mask 下材质缓存不同步。

## 适用范围与未验证内容

目标：Unity 2021.3+ / 2022.3 / Unity 6 的 UGUI（需安装 Unity UI）。使用内置 UI CG include 和普通透明 Pass，不需要 URP Shader Graph、Bloom 或后处理。设计面向 Built-in、URP 的 Screen Space Overlay；没有绑定某条渲染管线，但这些组合都尚未在 Unity 内编译验证。Camera/World Space、HDRP、XR、各移动 GPU 需自行验证，不宣称已兼容。不是 UI Toolkit 材质。

当前执行环境没有 Unity、C# 编译器或目标项目：只完成源码结构检查及几何/状态数学测试。不能将这些检查理解为 Unity Shader 编译、Play Mode、构建或实机测试通过。

RectMask2D 目前只支持硬裁切，不支持 softness。发光是透明颜色叠加，不是 HDR Bloom。边角使用平面距离近似，切角端点不是严格欧氏 SDF。数值单位为 Canvas 本地单位，配合 1920×1080 CanvasScaler；并非任何 Canvas 缩放下都固定物理像素。非均匀缩放会变形，应保持板和覆盖层等比。

每个组件持有一个可释放的材质实例，因此不保证跨组件合批。仅选中且动态时逐帧重建 4 个顶点；大量候选请做池化/可见性控制并用 Profiler 测量。不要将装饰组件所在物体用于 Mask 来源。

## 安装与快速查看

1. 将本包 Assets/NoMadCitySelectionFX 整个复制到工程 Assets。保留 Resources 目录，避免只靠 Shader.Find 导致构建剥离。
2. Unity 菜单 GameObject > UI > NoMadCity > Selection FX Comparison 生成四种比例的静态对照对象。此示例只有颜色占位板，没有原图、业务选择逻辑或点击事件。进入 Play Mode 看呼吸。手动更改状态查看不同表现。
3. 实际接入时，在现有候选板物体下新建 UI 空子物体，添加 SelectionEffectGraphic（不再添加 Image）。锚点铺满真实卡面/板面矩形，offset 为 0。不要铺满带透明留白的原贴图外框。
4. Graphic Color 设置为 #FFB026，alpha 1。默认线宽 2、切角 7、外光半径 10。覆盖层放在原板上方、状态标记和文字下方。
5. 关闭旧的状态边框 Image，避免双框。原板 Image 保持不变。原卡牌 card-selected.png 含勾，不要同时保留该图和新增的勾。
6. Canvas 的额外 UV 通道由脚本自动启用，不会在销毁时清除，以免破坏其他 UI。
7. 使用独立、静态的勾或“已选”标签以及锁/“不可选：原因”标签，分别绑定 selectedBadge / unavailableBadge。可以是现有 PNG、TMP 字样或简单矢量对象，只切换显隐，不替换贴图。请把标记放在板外预留空间而非正文上；勾/锁不是本 Shader 内绘制。灰虚线与加粗可独立区分，但仅靠颜色或动画不足以表达全部状态。
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

Overlay 对应真实 650:1850 企业板，选中整板。玩家合作等级块不变、不跟着发光。原预览 8px 艺术九宫格边框不等于新 Shader 线宽：新实现建议 2px 线＋轻外光，不模拟磨损金属纹理。

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
