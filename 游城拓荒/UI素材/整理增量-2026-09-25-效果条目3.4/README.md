# 效果条目 3.4 素材增量

整理日期：2026-09-25。状态：源素材已整理校验，Unity 尚待 UI-006 接入。原包、旧整理版与按钮 3.2 目录保留。

## 入口

- [原说明](../EffectRows-v3.4-Notes.md)、[原 ZIP](../EffectRows-v3.4-Patch.zip)、[包内说明副本](原包/EFFECT_ROW_PATCH.md)。
- [素材参数](原包/assets/effect-assets.json)、[组件字段约定](原包/prefab-contract.json)、[玩家组合色映射](原包/assets/marker-combinations.json)。
- [素材索引](清单/素材索引.json)、[源包文件索引](清单/源包文件索引.json)、[源文件快照](清单/源文件快照.json)、[校验报告](清单/校验报告.json)。
- [项目补充规范](../../../prompt/UI换新/06-效果条目3.4与主框3.5增量规范.md)、[UI-006 补充提示词](../../../prompt/UI换新/UI-006-补充-效果条目3.4与主框3.5.md)。

## 内容与采用范围

选择执行选项、其他玩家永续明细、快速行动共用一套暖灰棕纸面条目；背景与细边框独立，左右标记／状态区域可选。该包包含完整 3.4 条目，不要求先取得或安装 3.3。清单中的 `v33-` 是兼容 ID，实际指向 3.4 资源。

| 素材 | 数量 | 1x 尺寸／格式 |
| --- | ---: | --- |
| 条目底图／条目边框 | 各 4 | 512×112；default／hover／pressed／disabled |
| 右侧状态底座 | 4 | 64×64；同上四态 |
| 状态图标 | 9 | 64×64；use／unused／used × light／dark／muted |
| 明细开关底图 | 4 | 176×52；default／hover／pressed／expanded |
| 上／下箭头 | 6 | 64×64；各三种配色 |
| 可选插槽分隔线 | 1 | 具体尺寸和切片见素材索引 |
| 玩家拼色标记 | 15 | 64×64 PNG，mask 1～15，无额外 2x／SVG 文件 |

32 项组件素材各有 1x PNG、2x PNG、SVG，共 96 文件；加 15 个标记 PNG，共 **111 份运行时源素材文件**。原包共 144 文件，包含 6 张预览 PNG 等非运行时材料，不应整包导入 `Assets`。

底图、条目边框、状态底座和开关底图的源切片为四边 12，2x 对应 24。其余部件按索引，不统一套用 12。选择一套 PNG 倍率并匹配 PPU；图标与标记等比显示，文字独立使用项目字体。建议行高 76／紧凑 60，预览中宽松行高 84；这些是布局参考，可按实际文本增高。开关建议显示 88×26，但源图为 176×52，不要把源尺寸直接当命中尺寸。

## 语义摘要

- `choice_execute`：无左标记，右侧为 `use`；仅映射正式允许执行的选项，原有“选择后确认”的流程仍保留确认语义。
- `persistent_detail`／`quick_action`：左侧 0～2 个槽，右侧 none／unused／used／use。
- 只有 `use` 且显式 `enabled=true` 才可执行；源组件遗漏 enabled 时默认 true，项目适配必须显式传入规则判定。点击后不能自动改为 used。
- none 回收右侧空间；unused／used 为只读，不作为执行按钮焦点。禁用 use 保留操作图形，不能冒充已使用。
- 玩家颜色位：绿 1、黄 2、蓝 4、红 8；必须显式映射项目颜色。一个多色块仍是一个槽。单色 count=0 显示零；多色数量在提示中完整描述，不用 mask=0 制造空方块。
- 同时最多展开一名其他玩家，明细向下插入并推开后续摘要；城市区保留底边，按剩余高度等比 contain。列表内部滚动，滚动条留独立通道。顶栏 effect 收起按钮继续保持其原职责。

## 预览与验证

- [三种模式与标记](原包/preview/effect-rows.png)、[旧／新条目对照](原包/preview/before-after.png)。
- [玩家明细收起](原包/preview/main-collapsed-details.png)、[明细展开](原包/preview/main-expanded-details.png)、[内部滚动](原包/preview/main-scrolled-details.png)、[快速行动](原包/preview/main-quick.png)。
- [离线组件预览](原包/preview/effect-rows.html)仅为源演示，本轮未运行交互。

已校验 143 项源包清单哈希（清单自身另占 1 文件）、126 项安装源文件、85 张 PNG 的签名／CRC／压缩数据及组件尺寸。外部说明与包内说明逐字节一致。仅在内存读取原 Frontier 3.1 与按钮 3.2 清单后，8 项受支持的 3.2 基线哈希全部匹配；没有安装补丁，也未验证缺少源包的 3.3 升级分支。

包内 `integration/asset-manifest.json` 保留旧主框条目；后续同时采用主框 3.5 时，只取本补丁的效果条目范围，不能用整份演示清单把主框回退到旧版。源 QA 声明不代表本项目验收。

只读复核：`python tools/prepare_ui_effect_frame_catalog.py --package effects --verify`。
