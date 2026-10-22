# 城市样式详情与宣告窗口 · 1.0.0

本窗口共用详情与宣告布局：左侧完整显示本局 6 张城市样式原牌，右侧显示完整玩家城市，下方保留选中样式的条件、奖励及不可宣告原因喵

完整图包为 `CityPattern-UI-v1.0.zip`，独立说明为 `CityPattern-Layout-v1.0.md` 喵

## 核心约定

| 项目 | 约定 |
|---|---|
| 原牌比例 | 930 × 600，31:20，保留右侧状态栏 |
| 本局组成 | 4 张Ⅰ类、2 张Ⅱ类 |
| 详情模式 | 查看全部玩家标记，不提交宣告 |
| 宣告模式 | 预选后确认，点击原牌不执行宣告 |
| 使用状态 | Ⅰ两槽、Ⅱ五槽保持原位置 |
| 后续宣告 | 独立额外标记带，不刷新使用次数 |
| 城市显示 | 2059 × 3801 原比例完整显示 |
| 原卡详情 | 原牌与当前标记一起放大 |

精确矩形、标记分层与规则边界见 [城市样式说明](docs/CITY_PATTERN.md)，完整原卡与槽位映射见 `sources/pattern-card-map.json` 喵

军工化区域采用扩展版，警备队辖区采用 6 分版，原牌条件与奖励不会由 UI 重新编写喵

宣告与使用样式特殊行动分别处理，确认结果由游戏规则层执行，预览不直接修改玩家标记、设施或分数喵

## 查看文件

| 文件 | 用途 |
|---|---|
| [离线交互预览](preview/index.html) | 切换详情与宣告、选择组合、放大原卡 |
| [详情预览](preview/details.png) | 实际素材拼装 PNG |
| [宣告预览](preview/declaration.png) | 实际素材拼装 PNG |
| [完整说明](docs/CITY_PATTERN.md) | 坐标、文字、贴图、状态和 API |
| `assets/component-map.json` | 模块、边框和按钮接入参数 |
| `sources/pattern-card-map.json` | 6 张完整原牌、版本和安全槽位 |
| `preview/data.js`、`scene.js`、`app.js` | 数据、共享渲染和交互 |
| `preview/verification.json` | 实际检查范围 |

独立预览文件名为 `CityPattern-Details-v1.0.png` 和 `CityPattern-Declaration-v1.0.png`，图包中的拆件保持底图、边框、内容和运行时文字分离喵

## 接入与重建

入口为 `window.cityPatternPreview.open(configOrId)`，内置场景名为 `details` 和 `declaration`，真实配置可提供 `patterns`、同步 `validateMatch`、可异步 `onConfirm` 和 `onClose` 喵

确认只返回样式版本、设施组合和标记落点等宣告意图，回调返回 `false` 时保留选择，规则层负责实际结算喵

在图包根目录执行 `node tools/render_preview.mjs` 可用 Node.js 与 Inkscape 重建两张预览，中文字体使用 Noto Sans CJK SC、微软雅黑或兼容替代字体喵

静态图与 HTML 共用场景代码，已完成静态目检、路径及语法检查和有限 Node DOM 逻辑验证，尚未进行真实浏览器交互实测喵
