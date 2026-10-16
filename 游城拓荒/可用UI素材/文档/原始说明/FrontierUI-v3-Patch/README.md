# 拓荒协作 UI · v3.1 增量补丁

本包基于主界面 v2.2，提供新版独立底板、边框、顶栏，以及带撤销、结算槽和结束行动的底栏喵

- 打开 `preview/index.html` 查看可切换状态的拼装预览喵
- 阅读 `docs/INCREMENTAL_PATCH.md` 接入新布局、撤销与结算流程喵
- `asset-manifest.json` 记录素材尺寸、九宫格切分和文件位置喵
- `skin-patch.json` 记录旧素材角色到新素材的映射喵
- `layout-patch.json` 记录 1920 × 1080 的新版区域坐标喵
- `shared-v3/`、`topbar-v3/`、`bottom-v3/` 为分层素材，全部运行时文字另行显示喵

将这三个素材目录连同 `sources/` 作为新皮肤安装，再按映射切换 UI 的素材角色与布局，不要直接覆盖旧包同名图片后假定接入完成喵

PNG 可独立使用，SVG 如引用 `sources/` 中的绘制原图，需保留对应相对路径喵

`preview/originals/` 中的游戏原图仅用于真实拼装预览；正式工程继续引用已有原图，无需重复导入喵

本包提供界面素材和演示，不执行游戏规则，也不自动接入现有游戏工程喵

3.1 更新细化顶栏内容，放大预览见 `preview/topbar-detail.png`，对应新增素材位于 `topbar-v3/` 喵
