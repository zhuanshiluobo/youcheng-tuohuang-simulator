# 莱茵生命科室选择窗口 · 1.0.0

同一窗口用于开局选择科室或中途切换科室，固定展示莱茵生命企业原板，候选科室独立单选，确认前不改变当前科室或结算奖励喵

完整图包为 `RhineDepartment-UI-v1.0.zip`，独立说明为 `RhineDepartment-Layout-v1.0.md` 喵

## 原板与候选

| 内容 | 包内文件 | 源尺寸／位置 |
|---|---|---|
| 莱茵生命企业板 | `originals/enterprise-atlas.jpg` | `asset-046` 索引 2，裁切 `(1300, 0, 650, 1850)`，13:37 |
| 防卫科 | `originals/department-defense.png` | `asset-048`，516 × 1000，129:250 |
| 工程科 | `originals/department-engineering.png` | `asset-049`，516 × 1000，129:250 |
| 结构科 | `originals/department-structure.png` | `asset-050`，516 × 1000，129:250 |
| 人力科 | `originals/department-hr.png` | `asset-079`，516 × 1000，129:250 |
| 商务科 | `originals/department-business.png` | `asset-080`，516 × 1000，129:250 |
| 科考科 | `originals/department-research.png` | `asset-081`，516 × 1000，129:250 |

原板裁切、六张科室的奖励安全框及 SHA 校验值见 `sources/board-map.json`，源文件来源见 `sources/originals.json` 喵

企业板和科室板分别按各自原比例显示，不裁去规则正文、不把两种不同长宽比拉成同形，也不在候选下方添加无关编号喵

每名玩家已有合作等级只在企业板区域显示一次，候选科室不复制合作等级轨道，等级、数字与标记均为独立叠层喵

## 选择与规则边界

全部玩家共享唯一激活科室，候选数量与操作上下文由调用方提供，组件支持 2 张、3 张和任意更多候选喵

开局选择和中途切换使用同一布局，开局示例没有当前科室、四名玩家合作等级均为 0，只激活公共科室；中途切换时当前激活科室不能再次确认，仍可保留显示以查看原文和原因喵

科室板的 2–5 级内容是升级奖励，候选命中区对应整块科室板，不将各奖励行做成独立特效按钮喵

切换后仅触发切换的玩家按当前合作等级获得对应奖励，其他玩家不因此补发奖励，实际奖励由规则层结算喵

可选、选中、不可选分别用细框、强框加勾、灰色框区分，点击不可选板会清空待选项并显示原因，不能确认该项；板下放大入口仍可查看原文，取消是否允许由调用上下文决定喵

组件只返回选中的 `departmentId`，不自行切换公共科室、不修改合作等级、不发奖励，也不把确认当作后续操作的自动入口喵

## 1920 × 1080 布局

矩形均为屏幕坐标 `(x, y, width, height)`，窗口局部坐标分别减去 `(88, 68)` 喵

| 区域 | 矩形 |
|---|---|
| 模态窗口 | `(88, 68, 1744, 944)` |
| 固定企业面板 | `(124, 184, 404, 718)` |
| 企业原板 | `(214, 242, 224, 637.538461538)` |
| 科室候选面板 | `(550, 184, 1234, 718)` |
| 候选视口 | `(578, 264, 1178, 562)` |
| 候选名称带 | `(578, 828, 1178, 26)` |
| 横滚轨道／热区 | `(590, 865, 1154, 8)`／`(582, 857, 1170, 24)` |
| 固定摘要 | `(128, 936, 1120, 60)` |
| 取消按钮 | `(1280, 928, 236, 60)` |
| 主按钮 | `(1532, 928, 252, 60)` |
| 企业板放大 | `(819.46, 130, 281.081, 800)` |
| 科室板放大 | `(753.6, 140, 412.8, 800)` |

科室原板统一为 `280 × 542.635658915`，顶部 `268`，相邻间距 `42 px`；少量候选居中，超出视口时仅候选板和名称一起横滚，企业板、摘要和按钮固定喵

设候选数为 `N`、滚动值为 `scrollX`，横向布局计算如下，`N = 0` 时显示空状态，滚动条只在 `maxScroll > 0` 时显示喵

```text
padding = 28
contentWidth = padding * 2 + N * 280 + max(0, N - 1) * 42
maxScroll = max(0, contentWidth - 1178)
centerOffset = max(0, (1178 - contentWidth) / 2)
cardX(i) = 578 + padding + centerOffset + i * 322 - scrollX
thumbWidth = max(48, 1154 * 1178 / contentWidth)
thumbX = 590 + (1154 - thumbWidth) * scrollX / maxScroll
```

左右内容留白为 `28 px`，用于容纳板外奖励等级标签，2 张卡 X 为 `866、1188`，3 张为 `705、1027、1349`，溢出时第一板 X 从 `606` 开始；滚动条滑块高 `12 px`、Y 为 `863`，轨道和热区独立，`scrollX` 限制在 `0…maxScroll` 喵

| 文字或标记 | 位置 | 字号／尺寸 |
|---|---|---|
| 标题／说明 | 左锚点 `(128,119)`／`(128,155)` | 36／20 px |
| 当前玩家 | 左锚点 `(1470,119)` | 23 px |
| 企业合作等级 | X `298、330、362、394`，Y `199`，红蓝黄绿 | 方块 24 × 24，数字 18 px |
| 候选名称／放大 | 各板左右边，中心 Y `841` | 20／16 px |
| 摘要两行 | 左锚点 `(128,949)`／`(128,980)` | 24／20 px |
| 取消／确认文字 | 居中 `(1398,958)`／`(1658,958)` | 26 px |
| 奖励等级 5／4／3／2 | 右锚点 `cardX − 14`，按奖励安全框垂直居中 | 18 px，仅标签 |
| 选中勾 | `(cardX + 286, 272, 20, 20)` | 金底及独立图标 |

上述文字 Y 为垂直中心，标记颜色分别为红 `#EC0000`、蓝 `#006CFF`、黄 `#FFBF00`、绿 `#5FD200`，数字不烘焙到原板喵

`three` 为开局 3 候选，`two` 为中途 2 候选，`many` 为中途 5 候选，示例数量不限制组件可接受的实际候选数喵

切换示例的当前科室为结构科，红／蓝／黄／绿合作等级分别为 `3／2／4／0`，工程科摘要展示触发者红方当前 3 级的 `获得2异铁`，仅作本例奖励提示，不执行结算喵

## 素材分层

| 层 | 素材或数据 |
|---|---|
| 模块底图 | `shared/02-modules/module-fill-dark.png` |
| 模块透明边框 | `shared/02-modules/module-frame.png` |
| 候选状态边框 | `assets/frame-available.png`、`frame-selected.png`、`frame-unavailable.png` |
| 选中小图标 | `assets/icon-check.png`；本预览未使用锁图标 |
| 黄色主按钮／次按钮 | `shared/card-picker/button-primary.png`、`button-secondary.png` |
| 横滚轨道／滑块 | `shared/03-controls/scrollbar-track-h.png`、`scrollbar-thumb-h.png` |
| 原板内容 | `originals/` 内的企业图集和六张科室板 |
| 等级、标题、原因、按钮文字 | 运行时独立绘制 |
| 交互命中区 | 透明逻辑矩形 |

窗口底图、透明边框、原板、文字和标记可分别移动或替换，按钮沿用既有 Card-Picker 素材，文字不烘焙到按钮中喵

模块边框使用四边 24 px 的九宫格，候选状态框的源边为 20 px，候选框实际目标边宽为 8 px喵

按钮原图按可见矩形 `(35, 102, 1916, 582)` 映射目标按钮区域，透明画布不作为点击范围，也不声称按钮已提供专用九宫格喵

## 离线预览与 API

打开 `preview/index.html` 可切换 `three`、`two`、`many`，实际入口为 `window.rhineDepartmentPreview.open(configOrId)`，候选、上下文和等级数据以 `preview/data.js` 为准喵

| 配置项 | 内容 |
|---|---|
| `context` | `initial` 开局选择／`switch` 中途切换 |
| `candidateIds`、`departments` | 候选顺序及可替换科室定义 |
| `currentDepartmentId` | 当前公共科室，开局可为 `null` |
| `activePlayerId`、`playerLevels` | 触发者及全体合作等级 |
| `unavailable` | 科室 ID 到不可选原因的映射 |
| `canCancel` | 是否允许取消、关闭或 Escape 退出 |
| `initial` | `selectedId`、`inspectedId`、`scrollX` 初始状态 |
| `onConfirm({ departmentId })` | 可异步，返回 `false` 保留窗口和选择 |
| `onCancel({ context, reason: 'cancel' })` | 取消通知 |

确认接受后关闭演示窗并派发 `rhine-department-confirm`，取消派发 `rhine-department-cancel`，事件均位于 `window`，`detail` 与对应回调对象一致喵

确认等待期间阻止重复选择，操作序号避免旧异步结果影响新窗口；放大关闭后保留选择，横滚支持滑块拖动、候选区域滚轮和滚动条键盘操作喵

奖励高亮只提示触发者当前等级对应行，原板左侧的 5／4／3／2 标签不属于按钮，不会成为逐行选择入口喵

静态预览为 `preview/three.png`、`two.png`、`many.png`，对应 SVG 与 HTML 共用 `scene.js`，由 Inkscape 导出，图中合作等级和选择状态是演示数据喵

在图包根目录执行 `node tools/render_preview.mjs` 可重建预览，需要 Node.js、Inkscape 和 Noto Sans CJK SC、微软雅黑等中文字体喵

验证范围记录在 `preview/verification.json`，已完成最小函数检查，未做 DOM 模拟或真实浏览器交互实测，静态图片并非浏览器截图喵
