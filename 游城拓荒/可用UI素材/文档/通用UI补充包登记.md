# 通用 UI 补充包登记

2026-10-18 用户在确认体积后批准合并两套通用 UI 素材及制作源。来源为远端 `601dbd4`，文件取自已核验的 `4a5f9f4b0b55207a9084b95bbce24e02cef813e9` 树。本次原样保存 602 个文件，约 17.91 MiB；未改变已有场景、预制体、主题或业务脚本。

| 包 | 正式 PNG | 动画 | Unity 文件 | 制作源及预览资料 | 合计 |
| --- | ---: | ---: | ---: | ---: | ---: |
| FrontierSupplementV1 | 81 | 0 | 1.25 MiB | 1.87 MiB | 3.11 MiB |
| FrontierSupplementV2 | 106 | 2 | 3.41 MiB | 11.39 MiB | 14.79 MiB |

Unity 文件位于 `Assets/YC/Presentation/CommonUi/Sprites/FrontierSupplementV1` 与 `FrontierSupplementV2`；制作源位于同名的 `ArtSource/UI` 子目录。Unity 体积含 `.meta`；制作资料含 SVG、脚本、清单、网页及参考图，不包含其他收藏模型的 Blender 源文件。

## 用途与当前状态

- V1 提供语义图标、提示与等待反馈、输入/下拉、复选框、开关和滑块。
- V2 提供相应控件的新皮肤、调度面板，以及紧凑消息背景和复选框动画修订。
- 两包均已归档，正式页面仍未使用。各项设计语义、状态、GUID、尺寸、切片及来源见[候选包登记清单](../清单/通用UI候选包登记.json)，原包 README 与 manifest 保持原内容。
- 本轮没有指定要替换的页面或控件。后续选用时仍按当前用途标准，将确定的图片和目标用途登记到可用素材索引；不将两包直接设为现有按钮、窗口或提示的默认替代。
- 参考图、历史预览和生产用图只供对应制作资料使用，不充当正式控件缺图。制作脚本仅保存，未运行；其说明中的旧路径不构成对受限原档案的访问授权。

## 完整性核验

602 个文件经 Git 文本换行过滤后与来源 blob 全部一致。187 张 PNG 的 CRC、解压、尺寸、GUID、PPU 和切片与 manifest/meta 一致；186 项 SVG 源可解析。200 个新 GUID 与原 Assets 没有冲突。两段动画各引用 8 张包内帧图，绑定的 `UnityEngine.UI.Image` 来自项目已有 UGUI 包。未发现正式场景、预制体或业务资产引用新包。

原包 README 提到的部分历史审批截图和报告未包含在这次远端树中，本次没有补造。V1 仅提供 `preview.template.html` 模板，没有 README 所述生成后的 `preview.html`；V2 提供 `preview.html` 与 `revision.html`。

核验记录位于 `Logs/GitMergeValidation/UiSupplementImport-20261018/verification.json`。本次没有运行 Unity、制作脚本或浏览器预览；文件完整性核验不代表界面接入或画面验收通过。
