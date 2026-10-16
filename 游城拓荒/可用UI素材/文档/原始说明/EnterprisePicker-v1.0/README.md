# 企业与企业特效选择窗口 · 1.0.1

本次将两种模式的确认按钮统一为最初角色牌选择窗口的黄色切角按钮，直接复用原 PNG，按钮文字仍由程序独立绘制喵

本包为两种操作提供同一套选择窗口：`company` 选择整家企业，`effect` 选择原企业板内的企业特效行，企业确认不会自动进入特效选择喵

普通升级奖励不是特效候选，不叠特殊选择框，选中、可选与不可选分别显示，不可选条目仍可查看原文和原因喵

状态徽标统一放在企业板右侧留白，选中勾标带独立金色底块，完整原板仍保持原比例喵

## 从哪里开始

1. 阅读 [布局与接入说明](docs/ENTERPRISE_PICKER.md)，了解两种模式、候选边界与原图比例喵
2. 使用 `originals/enterprise-atlas.jpg` 的原企业板内容，框体、底图、状态叠层与文字分别绘制喵
3. 从 `assets/selection-assets.json` 获取三种状态边框、弱化层和勾选／锁定图标；确认按钮使用 `shared/card-picker/button-primary.png`，映射参数见同目录 JSON，窗口和取消按钮沿用其它 `shared/` 组件喵
4. 打开 [离线交互预览](preview/index.html)，查看 4 家候选、2 家候选和企业特效三个场景，演示数据不作为游戏规则定义喵

## 核心约定

| 项目 | 约定 |
|---|---|
| 企业图集 | 2600 × 3700，4 列 × 2 行 |
| 单张企业板 | 650 × 1850，完整显示比例 13:37 |
| 列表容量 | 2 张居中、4 张同屏、更多横向滚动 |
| 合作等级 | 每板上方 4 个玩家颜色数字块 |
| 整企业确认 | 返回 `companyId` |
| 特效确认 | 返回 `companyId`、`effectId`、`tier` |
| 规则判定 | 由调用方提供资格、原因、候选与取消权限 |

玩家颜色保持 Green `#5FD200`、Yellow `#FFBF00`、Blue `#006CFF`、Red `#EC0000`，数字独立叠加，不覆盖原板文字喵

原图集保持独立，装饰资产不包含文字或数字，组件只返回选择结果，不执行支付、升级、奖励结算或特效触发喵

新状态边框的艺术源图保留在 `sources/effect-frame-source.png`，生产素材拆成边框、弱化层与图标，确切参数见 `assets/selection-assets.json` 喵

## 预览与数据

| 文件 | 内容 |
|---|---|
| [preview/index.html](preview/index.html) | 离线交互示例入口 |
| [preview/company-choice.png](preview/company-choice.png) | 整企业选择的实际素材拼装图 |
| [preview/effect-choice.png](preview/effect-choice.png) | 企业特效选择的实际素材拼装图 |
| `preview/data.js` | 两种模式共用的数据、规则资格示例与精确布局 |
| `preview/app.js` | 选择、详情、横滚与确认／取消回调示例 |
| `preview/verification.json` | 已执行检查及验证范围 |
| `assets/selection-assets.json` | 新状态框、弱化层与图标清单 |
| `shared/card-picker/button-primary.png` | 与角色牌选择窗口相同的确认按钮原贴图 |
| `shared/card-picker/button-primary.json` | 确认按钮可见区域、位置、文字与状态参数 |
| `sources/originals.json` | 原企业图集及背景来源记录 |
| `sources/generation.json` | 新状态框的生成来源记录 |

两张静态图由实际 PNG、原图内容和独立文字在 SVG 中组合，再使用 Inkscape 渲染，不是 HTML 浏览器截图喵

HTML 提供选择与查看详情等交互示例，本次未进行浏览器运行验证，准确检查范围以 `preview/verification.json` 为准喵

在图包根目录运行 `node tools/render_preview.mjs` 可以重新生成静态拼装图，需要 Node.js、Inkscape 与可用的中文字体（如 Noto Sans CJK SC 或微软雅黑），状态素材可由 `python tools/build_selection_assets.py` 重建喵
