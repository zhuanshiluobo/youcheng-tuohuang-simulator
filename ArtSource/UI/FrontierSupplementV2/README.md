# 游城拓荒 · 移动城市调度终端皮肤 V2

日期：2026-10-09。**全部制作完成，主体和两项定向修订均已通过用户复核；未接入正式对局。**

本版先查阅本项目规则书封面、城市面板、设施卡、3.5 主框预览、实际对局截图与桌游官网，再重新制作。风格依据与 V1 差异见 [style-references.md](style-references.md)。

## 查看

- `revision.html` / `revision-approval.png`：本轮仅两项修订的交互页与预览。
- `checkbox-motion.gif`：原生动画帧的放大循环示意。
- `approval-status.json`：主体完成、修订范围与 Unity 动画资产记录。

当前完成状态以 `approval-status.json` 为准。截图和浏览器检查报告保留复核前的原始记录，不重新生成或覆盖历史证据。
- `approval.png`：初版对局组合图，保留作为主体获认可时的留档；本轮最新修订见 `revision-approval.png`。
- `preview.html`：可交互的完整审批页，包括下拉、输入状态、复选框、拨片、滑块、等待、断线、重试与关闭。
- `preview-desktop.png` / `preview-narrow.png`：初版浏览器预览留档；本轮窄屏修订见 `revision-narrow.png`。
- `manifest.json`：每张图的逻辑尺寸、像素尺寸、状态、用途、GUID 和切片。

背景采用项目已有截图，本轮叠加构图仅为皮肤审批组合图，不是 Unity 新界面的运行证据。样例文字和操作不会写入游戏，也不会连接真实 Steam 房间。

## 资产

原有 91 张 PNG 未重绘。本轮仅新增 15 张：5 个 1424×60 紧凑消息背景、2 个勾选分层图、8 个勾选动画帧。当前共 106 张 PNG，另附两段 Unity 原生动画。按钮只是本补充包的候选设计，不自动替换既有行动按钮。

Unity 目录：`Assets/YC/Presentation/CommonUi/Sprites/FrontierSupplementV2`。可编辑 SVG 位于本目录 `sources`；所有控件文字由上层 UI 组件绘制，不烘焙到 PNG。

### 导入和切片

- PNG 为 2 倍像素密度，PPU = 200，透明单 Sprite，`.meta` 独立 GUID，不修改已有引用。
- `manifest.json` 的 `border` 顺序为 **左、下、右、上**，单位是 PNG 像素，与 Unity `spriteBorder` 对应。
- 九宫格背景应使用 `Image.Type.Sliced`；图标、完整勾选态、旋钮和拨片握柄使用 `Simple`。
- 调度底板原图 1536×1024，逻辑尺寸 768×512，推荐固定 3:2 显示。必要伸展时保护标题带和边角；不要按普通整图拉伸做长窄窗口。
- 小控件无粗装甲、铆钉或大警戒纹；装饰集中于大面板边缘，保护正文信息密度。

### 生成来源

无字底板使用内建图像生成工具，以项目城市板和已批准 UI 预览为参考。原图与最终提示词保留在 `generated/dispatch-panel.png`、`generated/prompt.txt`。

其余资产使用原生 SVG 几何导出，纸纹来自项目既有 `Frontier31/paper-bg.png`，没有新增字体或外部美术依赖。构件导出脚本为 `build_assets.py`，需要本机 PyMuPDF 与 Pillow。浏览器检查为有限代表性检查，不重建 Unity 场景，也没有新增测试门禁。

`references/city-board.png` 与 `references/facility-001.jpg` 是当前项目 `StreamingAssets/Content/core/artwork` 中的原图副本，只服务于审批对照，不是新制作的 UI 资产。

## 当前边界

V1 原稿和所有旧素材均保留。已有场景、预制体、主题和业务脚本未改。两项修订分别对应用户图一与图二；图三的候选窗口与图四的现有角色牌窗口不重绘、不替换。真正接入后的 Unity 显示还需要按用户批准的目标资产确认。

## 本轮修订

复选框预览使用底板与勾选图分层，选中展开、取消回收；状态标签跟随实际值，禁用态不播放动画，并尊重系统减少动态效果设置。原有六张完整勾选状态贴图不变。`animations/CheckboxSelect.anim` 和 `CheckboxCancel.anim` 绑定同一对象上的 `UnityEngine.UI.Image.m_Sprite`，共 8 帧；原生片段含末帧保持，长度约 197/157 毫秒。不新增动画控制器或运行时框架，也不自动给旧预制体挂动画。

消息适配依据是共享 HUD 的 `Resolution Summary Slot`：RectTransform、LayoutElement 高度均为 60，首选宽度 1424、弹性宽度为 1；三人和四人场景未覆盖高度。标题字号 19、摘要字号 18，预览保留它们的比例锚点区域。新背景为深灰、薄边、语义色细标记，兼容现有浅色文字；原有纸质通知仍保留给已认可的大窗口。

新消息 PNG 为 2848×120，九宫格边距 `[28,20,28,20]`（左、下、右、上）。消息变长时应由现有文字区域换行或截断，不增高常驻底栏。新增资产可运行 `python build_assets.py --revision-only` 导出，不重写原有 91 张图；复选框片段的制作代码保存在 `build-checkbox-clips.cs.txt`，只针对新资产，不调用任何界面生成器。
