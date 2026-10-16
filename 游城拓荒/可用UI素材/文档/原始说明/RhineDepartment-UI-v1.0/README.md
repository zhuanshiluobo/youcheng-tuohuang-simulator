# 莱茵生命科室选择窗口 · 1.0.0

固定展示莱茵生命企业原板与四名玩家合作等级，科室候选支持少量居中和更多候选横向滚动，单选后通过统一黄色按钮确认喵

企业板保持 `13:37`，科室板保持 `129:250`，底图、边框、原板、文字和标记分层，候选板不重复显示玩家等级轨道喵

开局选择与中途切换由调用上下文决定，只有一块全体共享的激活科室，组件只提交 `departmentId`，奖励与状态变更由规则层处理喵

| 交付文件 | 内容 |
|---|---|
| `RhineDepartment-UI-v1.0.zip` | 完整模块图包与离线预览 |
| `RhineDepartment-Three-v1.0.png` | 3 候选布局 |
| `RhineDepartment-Two-v1.0.png` | 2 候选布局 |
| `RhineDepartment-Many-v1.0.png` | 多候选滚动布局 |
| `RhineDepartment-Layout-v1.0.md` | 独立布局说明 |

精确布局、文字位置、素材分层与接入方式见 [科室选择说明](docs/RHINE_DEPARTMENT.md)，原板与奖励区映射见 `sources/board-map.json`，原图来源见 `sources/originals.json` 喵

## 查看与接入

打开 [离线交互预览](preview/index.html)，或查看 [三候选](preview/three.png)、[两候选](preview/two.png)、[多候选](preview/many.png) 静态图喵

接口为 `window.rhineDepartmentPreview.open(configOrId)`，内置场景为 `three`、`two`、`many`，配置与 `onConfirm({ departmentId })`、`onCancel` 的约定见完整说明喵

图包根目录运行 `node tools/render_preview.mjs` 可重建静态预览，需要 Node.js、Inkscape 与中文字体，预览由真实素材拼装，未进行真实浏览器交互实测喵
