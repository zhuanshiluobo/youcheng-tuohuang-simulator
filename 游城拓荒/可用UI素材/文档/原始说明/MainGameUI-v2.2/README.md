# 主界面可拆分图包 · 2.2.0

本版仅重绘顶栏，按已确认效果图的比例、轮廓与留白采用哑光灰褐底、黄色小旗与右侧灰色三分区，屏幕 `y ≥ 72` 的模块保持原有布局与素材，交付包为 `MainGameUI-v2.2.zip` 喵

概念图用于对齐比例、轮廓和视觉方向，实际交付效果请查看由本包 PNG 拼装的主界面预览，不承诺与概念图逐像素一致喵

## 从哪里开始

1. 阅读 [布局与分层说明](docs/UI_LAYOUT.md)，按 `layout.json` 的逻辑画布组织模块喵
2. 从 `00-screen`、`01-topbar`、`02-modules` 分别取边框与底图，使用 `03-controls` 中的按钮和页签组件喵
3. 通用组件尺寸及切片参数见 `chrome.json`，当前顶栏的 9 项素材与缩放约定以 `01-topbar/topbar-v22.json` 为优先清单喵
4. 打开 [素材目录](preview/gallery.html) 查看组件，打开 [主界面演示](preview/index.html) 查看页面组织与交互示例喵

PNG 已经导出，可以直接用于引擎，不需要先运行生成脚本喵

## 图层对应

| 用途 | 边框 | 底图 |
|---|---|---|
| 整张界面 | `00-screen/screen-frame.png` | `00-screen/screen-fill.png` |
| 顶栏 | `01-topbar/topbar-frame.png` | `01-topbar/topbar-fill.png` |
| 左侧标题装饰板 | `01-topbar/title-plaque-frame.png` | `01-topbar/title-plaque-fill.png` |
| 金色行动旗标 | `01-topbar/turn-ribbon-frame.png` | `01-topbar/turn-ribbon-fill.png` |
| 右侧灰色三分区 | `01-topbar/right-cluster-frame.png` | `01-topbar/right-cluster-fill.png` |
| 普通模块 | `02-modules/module-frame.png` | `02-modules/module-fill-dark.png` 或 `module-fill-paper.png` |
| 模块标题 | `02-modules/module-header-frame.png` | `02-modules/module-header-fill.png` |
| 卡槽 | `02-modules/card-slot-frame.png` | `02-modules/card-slot-fill.png` |
| 按钮与页签 | `03-controls` 内对应组件 | 对应状态底图，确切配对见 `chrome.json` |

边框与底图分离，文本、数字和图标由项目另外绘制，城市样式表使用整纸底与弱行分隔喵

艺术框体及按钮来自 `05-textures/art/` 中的 ImageGen 原图，通用组件 SVG 为相对引用原 PNG 的九宫格封装，v2.2 顶栏 SVG 则从新哑光原图取样和剪裁，纹理细节并非可编辑矢量路径喵

通用组件继续使用 `chrome.json`，本次顶栏单独提供 9 项素材清单，顶栏同名资产须优先读取 `01-topbar/topbar-v22.json`，不能沿用旧标题板和旗标尺寸喵

框体、按钮、标题装饰板及填充 SVG 均使用相对图像引用，需要保留 `05-textures/` 及其 `art/` 子目录，重新导出也需要完整源目录，所有导出的组件 PNG 可以独立使用喵

标题装饰板位于左侧标题文字后方，独立于整条顶栏，源尺寸为 `474 × 64`，显示在 `(16, 0, 474, 64)`，调整时保持等比和右端斜肩喵

可选 `01-topbar/title-plaque.png` 是方便直接使用的合并版，边框与底图两个独立文件仍然保留喵

## 顶栏布局

顶栏为 `(0, 0, 1920, 68)`，回合与阶段位于 `(720, 14, 320, 42)`，黄色行动旗标位于 `(1075, 7, 204, 49)`，右侧灰色组件位于 `(1436, 3, 474, 59)` 喵

标题板后保留 `230 px` 空白，行动旗标后保留 `157 px` 空白；顶栏边框仅为全幅底部 `4 px` 分隔层，不做九宫格框喵

右侧组件依次容纳宽 `242 px` 的红区状态、宽 `167 px` 的收起／展开与宽 `65 px` 的设置，必须整组等比缩放，内部控件不能分别拉伸喵

`轮到你行动` 为不可点击的状态标，收起与设置使用透明点击区，共用右侧组件底图，不套用 `03-controls` 的普通按钮框喵

红区显示两行 `红区开放` 和 `第X回合起`，未到开放回合时第二行显示 `第X回合`，始终保留开放阈值喵

红区资源点在 4 人第 4 回合、3 人第 5 回合、2 人变体第 6 回合开始开放，解除探索与城市移动的目标限制，红区航道始终可用，悬浮说明补充已开放或尚未开放状态喵

旧 `red-zone-ribbon-frame/fill` 仅为兼容保留，已弃用且不在当前预览渲染，详细坐标与接入见 [顶栏更新说明](docs/TOPBAR_UPDATE.md) 喵

## 玩家颜色与计数

| 玩家 | 精确色值 | 位值 |
|---|---|---:|
| Green | `#5FD200` | 1 |
| Yellow | `#FFBF00` | 2 |
| Blue | `#006CFF` | 4 |
| Red | `#EC0000` | 8 |

`04-markers` 保留全部 15 种非空组合和一个空组合，每种均有 SVG 及 32、64、128 像素 PNG，悬停、选中和焦点仍为独立叠层喵

企业合作同等级的玩家位值取按位或，例如绿蓝同格使用 `cooperation-05-64.png`，城市样式则使用各玩家独立的纯色数字块，仅合并同色同槽的数量喵

预览中的空等级格使用浅灰空槽表现，原 `cooperation-00` 资产保持不变，数字始终独立叠加喵

## 真实素材拼装预览

`preview/main-city-patterns.png` 与 `preview/main-actions.png` 由实际交付 PNG、原图和文字在 SVG 场景中组合，再通过 Inkscape 渲染喵

`preview/component-sheet.png` 展示实际交付的分层组件，可直接检查边框、底图、按钮与标题装饰板喵

2.2.0 另提供 `MainGameUI-Layout-v2.2.md`、`MainGameUI-Preview-v2.2.png` 与顶栏局部图 `topbar-detail.png`，便于核对新顶栏的比例与留白喵

主界面人物缩略图使用人像区域裁切，完整卡详情保留原卡比例和卡文；原城市板的字节与显示比例保持不变喵

地图主视图裁去外围后等比显示，并用独立模糊背景填充剩余区域，完整地图详情显示未裁切原图喵

HTML 提供页面行为示例，未接入游戏逻辑，本次未完成 HTML 浏览器运行验证，具体检查范围见 `preview/verification.json` 喵

原始游戏图片的来源记录在 `sources.json`，艺术生成提示词记录在 `art-prompts.json`，制作方式见 [GENERATION_NOTES.md](GENERATION_NOTES.md) 喵

公司图标从保留原文件的 `07-originals/enterprise-atlas.jpg` 裁切，原图集为 `2600 × 3700`，与玩家颜色标记分别管理喵

## 重新导出

`tools/build_topbar_matte.py` 是 v2.2 顶栏唯一重建脚本，使用 `05-textures/art/topbar-matte-source.png` 与独立无框底纹 `topbar-matte-background.png` 导出 9 项素材；旧标题板、旗标及通用框体／填充脚本会跳过新顶栏喵

`tools/build_chrome.py` 继续重建其他框体与控件，`tools/build_markers.py` 负责精确配色标记，`tools/build_fills.py` 负责其他独立填充喵

重新导出需保留完整源目录，并具备脚本所需的 Python、Pillow 与 Inkscape 环境，无需重新调用图像生成服务喵

```sh
python tools/build_chrome.py
python tools/build_markers.py
python tools/build_fills.py
python tools/build_topbar_matte.py
python tools/build_catalog.py
python tools/render_component_sheet.py
```

这些命令重建组件和目录页，主界面预览应使用包内对应的预览渲染脚本重新生成，源参数与导出结果以当前版本清单为准喵
