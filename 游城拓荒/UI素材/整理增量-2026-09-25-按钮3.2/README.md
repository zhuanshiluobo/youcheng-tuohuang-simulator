# 主界面按钮 3.2 素材增量

整理日期：2026-09-25。状态：**素材已整理并校验，尚未导入 Unity。**

本目录独立保存新增按钮包；[2026-09-24 整理版](../整理版-2026-09-24/README.md)、原 ZIP、原说明和已完成 UI-003 的资产均保留。

## 1. 阅读入口

- [用户提供的说明](../MainButtons-v3.2-Notes.md)、[原 ZIP](../MainButtons-v3.2-Patch.zip)。
- [原包说明副本](原包/BUTTON_PATCH.md)、[按钮用途与布局](原包/button-roles.json)、[底板及图标参数](原包/assets/button-assets.json)。
- [逐按钮、逐状态贴图索引](清单/按钮贴图索引.json)：包含底板、图标、1x／2x／SVG 路径及 Unity 切片顺序。
- [源包文件索引](清单/源包文件索引.json)、[源文件快照](清单/源文件快照.json)、[本轮校验报告](清单/校验报告.json)。
- [项目接入规范](../../../prompt/UI换新/04-主界面按钮3.2增量规范.md)、[UI-005 补充提示词](../../../prompt/UI换新/UI-005-补充-主界面按钮3.2.md)。

## 2. 版本与替换范围

主界面整体继续采用 Frontier 实际 3.1；仅下列 11 个按钮优先采用 MainButtons 3.2。其他共用按钮、设置／收起按钮、设施供应区入口、模块框体、底栏结算槽和各专用窗口继续沿用各自依据。

| 用途 ID | 参考文字 | 底板系列 | 图标名称 | 基准矩形 x／y／宽／高 |
| --- | --- | --- | --- | --- |
| `tab.main` | 主要行动 | `tab` | `main-actions` | 1352／598／180／48 |
| `tab.quick` | 快速行动 | `tab` | `quick-actions` | 1538／598／180／48 |
| `tab.city` | 城市样式 | `tab` | `city-patterns` | 1724／598／180／48 |
| `action.deploy` | 部署 | `action` | `deploy` | 1364／700／258／55 |
| `action.dispatch` | 调度 | `action` | `dispatch` | 1634／700／258／55 |
| `action.explore` | 探索 | `action` | `explore` | 1364／765／258／55 |
| `action.move` | 城市移动 | `action` | `city-move` | 1634／765／258／55 |
| `action.build` | 建设 | `action` | `build` | 1364／830／258／55 |
| `action.special` | 特殊行动 | `action` | `special` | 1634／830／258／55 |
| `undo` | 撤销 | `undo` | `undo` | 36／1005／132／48 |
| `endAction` | 结束行动 | `primary` | `end` | 1640／1003／236／52 |

坐标采用 1920×1080、左上原点，只表达基准构图。图标矩形、文字中心、字号和行动按钮分隔线以 `button-roles.json` 为准；图标尺寸分别为页签 32、行动 38、底栏 30 个基准单位。实际游戏适配复用 UI-003 的安全区域、顶底栏和集中布局配置。

## 3. 资产构成与导入

| 内容 | 实际数量 | 说明 |
| --- | ---: | --- |
| 底板系列 | 4 | `tab`、`action`、`undo`、`primary`；源说明的“三类设计”按用途分组，不是三套系列 |
| 底板状态素材 | 18 | 页签 6 态，其他系列各 4 态 |
| 图标配色素材 | 33 | 11 图标 × `light`／`dark`／`muted` |
| 逻辑素材 | 51 | 18 底板 + 33 图标 |
| 运行时源素材文件 | 153 | 每项有 1x PNG、2x PNG、SVG；不是需要全部导入 Unity 的数量 |
| ZIP 文件成员 | 174 | 包含说明、JSON、预览和工具脚本；目录项不计 |

- 底板路径为 `原包/assets/bases/<系列>-<状态>.png`，尺寸 256×80；2x 为 512×160。
- 图标路径为 `原包/assets/icons/<图标>-<配色>.png`，尺寸 64×64；2x 为 128×128。保持等比，独立于底板和文字。
- 底板源切片顺序为左／上／右／下，均为 12；2x 均为 24。Unity Sprite border 顺序是左／下／右／上，索引已转换。不能复制 3.1 按钮的旧切片值。
- 底板使用 `Image.Type.Sliced`。`contentInset` 为 18／12／18／12，`minSize` 为 72×40；这些是底板元数据，不能替代每个按钮的图标和文字布局，也不能作为所有输入设备的可用命中下限。
- Unity 选择一套 PNG 倍率，记录导入路径、GUID、切片、PPU 与最终屏幕描边效果。若使用 2x，切片和 PPU／像素倍率配套调整，避免边框在屏幕上加倍变粗。
- 仅导入需要的底板、图标与必要配置，使用独立版本目录；预览整图、网页脚本、SVG 源和演示数据不整包搬进 `Assets`。
- 底板、图标、文字分层；图标与文字不烘焙到按钮。源字段 `textIsRuntime` 表示独立绘字，项目静态文案仍由场景／Prefab 保存。

## 4. 状态与配色

| 系列 | 状态 | 配色 |
| --- | --- | --- |
| `tab` | default／hover／pressed | `light` |
| `tab` | selected／selected-hover | `dark` |
| `action`、`undo` | default／hover／pressed | `light` |
| `primary` | default／hover／pressed | `dark` |
| 全部 | disabled | `muted` |

底板的 `recommendedInk` 对应图标配色；各配色的具体色值见图标元数据，文字保持对应对比度。禁用优先于按下、选中和悬停；选中状态独立保存。页签按下使用该系列已有 `pressed`，松开／移出后恢复当前逻辑状态，不凭空要求包里没有的 `selected-pressed` 贴图。焦点提示是可选独立层，不替代页签选中态。

## 5. 预览与校验边界

- [按钮样式及状态展示图](原包/preview/button-sheet.png)、[SVG](原包/preview/button-sheet.svg)、[HTML 状态展示](原包/preview/button-sheet.html)。
- [主界面参考图](原包/preview/main-idle.png)、[结算参考图](原包/preview/main-resolution.png)。这些包含素材演示数据，不是项目实际游戏截图。

本轮已检查按钮展示图及主界面参考图，并与 UI-003 的实际基准截图对照：当前行动区的旧六个入口与新版六格并非逐位置对应，具体交接见项目规范。尚未运行 HTML 交互或 Unity 接入。

源清单的 173 项哈希／大小全部匹配；`package-files.json` 自身是第 174 个文件，不在自身清单中。补丁安装清单的 161 项源文件匹配；原 Frontier ZIP 的 4 个指定基线哈希匹配；105 张 PNG 的签名、块 CRC 和压缩数据解压检查通过。外部说明与包内 `BUTTON_PATCH.md` 逐字节一致。

`原包/tools/apply_patch.py`、`integration/` 是源包的浏览器预览安装材料，本次只保存原文；没有运行它们，也没有把预览覆盖应用到旧整理目录。源 `QA.json` 的声明不是本项目的验收记录。

在项目根目录可运行以下只读校验：

```powershell
python tools/prepare_ui_button_patch_catalog.py --verify
```

整理脚本首次运行只创建新目录；目标已存在时拒绝再次写入。需要下一版素材时新增版本目录，保留本版。
