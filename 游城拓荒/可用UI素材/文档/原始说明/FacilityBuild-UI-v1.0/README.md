# 设施建设窗口 · 1.0.0

完整图包为 `FacilityBuild-UI-v1.0.zip`，独立说明为 `FacilityBuild-Layout-v1.0.md` 喵

本包采用固定三栏：左侧显示本次结算效果与费用，中间展示可变数量的设施候选，右侧完整显示玩家城市，底部保留统一确认按钮喵

窗口沿用企业选择页的深色模块，确认按钮沿用原卡牌选择窗口的黄色贴图，原设施卡与原城市板保持比例和独立内容喵

## 核心约定

| 项目 | 约定 |
|---|---|
| 候选卡 | 原卡 600 × 850，比例 12:17 |
| 城市板 | 实际源 2059 × 3801，完整等比 |
| 候选数量 | 常规 6 张展示为 3 列 × 2 行，其他数量由本次规则提供 |
| 延伸枢纽 | 独立后续步骤，展示实际余下 0～3 张 |
| 效果区域 | 固定位置，效果过多时独立纵向滚动 |
| 费用摘要 | 固定在效果列表下方 |
| 支付能力 | 不按原价支付能力过滤候选 |
| 确认行为 | 提交设施、城市目标与效果选择意图，由规则层结算 |

详细比例、分层、按钮映射和交互边界见 [设施建设说明](docs/FACILITY_BUILD.md) 喵

原设施图集位于 `originals/facility-atlas.jpg`，原城市板位于 `originals/city-board.png`，原黄色确认按钮的映射记录在 `shared/card-picker/button-primary.json` 喵

真实模块配对见 `assets/component-map.json`，窗口与三栏使用可复用九宫格组件，原卡与城市索引见 `sources/card-and-city-map.json`，没有将每个面板烘焙为固定尺寸大图喵

三栏几何与卡面位置读取 `preview/data.js`，其他规则、候选集合和报价由本次操作传入，组件不定义额外游戏规则喵

## 预览与接入

| 文件 | 内容 |
|---|---|
| [preview/index.html](preview/index.html) | 普通建设、多候选、库存不足与延伸枢纽交互示例 |
| [preview/standard.png](preview/standard.png) | 普通建设实际素材拼装图 |
| [preview/many-candidates.png](preview/many-candidates.png) | 多候选纵向浏览拼装图 |
| [preview/extension-three.png](preview/extension-three.png) | 独立延伸选择拼装图 |
| `preview/data.js` | 最终几何、原图映射与显式演示数据 |
| `preview/scene.js` | HTML 与静态预览共用的场景拼装 |
| `preview/verification.json` | 实际检查范围 |

三张独立预览文件分别为 `FacilityBuild-Standard-v1.0.png`、`FacilityBuild-Many-v1.0.png`、`FacilityBuild-Extension-v1.0.png` 喵

预览通过 `window.facilityBuildPreview.open(configOrId)` 接收操作数据，报价、效果子选择和最终建设交由调用方，完整参数见布局说明喵

静态预览使用实际素材拼装后由 Inkscape 渲染，HTML 浏览器运行未验证，不能把静态图或脚本检查等同于浏览器交互测试喵

在图包根目录运行 `node tools/render_preview.mjs` 可重建预览，需要 Node.js、Inkscape 与 Noto CJK 或微软雅黑等中文字体喵
