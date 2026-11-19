# SelectionFX 程序化选择效果登记

2026-11-16，按用户指定的 `C:\Users\wzy\Downloads\NoMadCity-Unity-SelectionFX.zip` 整理入库，登记为 **FX01**。包内未声明版本号，原验证记录日期为 2026-11-13；不得自行称为 1.0 或已验收版本。

同日用户指定 `C:\Users\wzy\Downloads\NoMadCity-Unity-SelectionFX-v8.zip` 完整更新代码并保留已有 `.meta`。现行 v8 仅选中时绘制金属选框，双光点半圈换位并停顿，不额外绘制白色焦点外框。演示和对齐工具仅复制，普通测试不运行；原卡面与静态布局不变。隔离 Unity 编辑器编译及组件渲染检查通过。2026-11-18 可见独立程序从正常菜单开始的两人对局中，[企业选择画面](../../../TestResults/effect-contract-fixture-20261116-01/Project/TestResults/Preparation-visible-fixed2-20261118-r1/ui-output/05-正常选择后.png)显示已选企业的金色选框，未出现紫红色 Shader 错误；该次程序与当前源码的 Shader 和两个运行时组件哈希一致。此截图仅覆盖该页面的选中状态，不代表其他页面、动画全过程或其他显卡已验收。

这是一套 UGUI 程序化装饰组件，没有贴图、原卡图、预制体或独立勾/锁标记。现登记五个组件文件，保留原始说明与三份交付参考。其他有效贴图用途和界面层级保持；用户指定的项目 SelectionFX 代码已更新，未运行生成或对齐工具。

## 可接入文件

通过[可用组件索引](../清单/可用组件索引.json)按 FX01 查询精确路径、SHA-256、依赖和用途。根目录为 `组件/选择状态特效/NoMadCity-Unity-SelectionFX/Assets/NoMadCitySelectionFX/`。

| 文件 | 用途与依赖 |
| --- | --- |
| `Resources/NoMadCitySelection.shader` | 程序化切角框、轻外光、呼吸、扫光、灰虚线及焦点线；保留 Resources 位置，Graphic 按名称加载 |
| `Runtime/SelectionEffectGraphic.cs` | MaskableGraphic 状态与几何控制；依赖上述 Shader，不负责规则资格或确认 |
| `Runtime/SelectionEffectInput.cs` | 现有可点击候选上的 hover/EventSystem 焦点转发；依赖 Graphic，不选择、不提交 |
| `Editor/SelectionEffectGraphicEditor.cs` | 可选 Inspector；依赖 Graphic 和 UnityEditor.UI，仅放 Editor，不放运行时程序集 |

四个正式组件各自保持 v3 原字节。包内 `SelectionEffectDemo.cs` 是生成颜色占位对照对象的菜单，只按用户本次要求复制，不作为普通验证入口。没有依赖原卡图片，也没有替换 selected/available/unavailable 贴图的代码。

## 限定用途

| 对象 | 覆盖区域及约束 | 原包建议线宽／切角／外光 |
| --- | --- | --- |
| 企业整板 | 真实650:1850整板；合作等级块不发光 | 2／7／10（沿用默认） |
| 企业效果候选行 | 仅规则指定候选行；普通奖励行不变选择按钮 | 1.5／4／5 |
| 卡牌 | 对齐真实12:17卡面，不按旧选中框整图尺寸铺满；选中不改变卡面位置大小 | 2／5／9 |
| 城市样式 | 卡本体或现有候选区；查看不代表宣告或可提交，设施格预选独立 | 1.5–2／5／7 |
| 莱茵科室板 | 真实516:1000候选整板；随同一ScrollRect内容滚动裁切；奖励行不成为选择按钮 | 2／7／9 |

企业各等级行源矩形、坐标换算和科室示例尺寸完整保存在[入库参数](../参数/NoMadCity-Unity-SelectionFX/入库参数.json)及[v8原始说明](原始说明/NoMadCity-Unity-SelectionFX-v8/README.zh-CN.md)。数值单位是 Canvas 本地单位，以1920×1080 CanvasScaler为参考，不保证固定物理像素；非均匀缩放可能变形。

## 状态和接入方法

Overlay 使用 `SelectionEffectGraphic`，不再叠加 Image；放在真实候选板面下的装饰子物体，锚点铺满、offset为0，层级位于原板之上、文字和独立标记之下。RaycastTarget保持关闭；Input组件放在已有可点击候选上。原说明要求 Graphic Color 手动设置为 `#FFB026`、alpha 1，运行时脚本没有自动设为金色。

v3 中 `Available`、`Unavailable` 和未选中的悬浮/焦点均不绘制附加效果；`Selected` 绘制持续双框和沿边亮段，并带呼吸及进入扫光，重复设置相同状态不重播。Reduced Motion 关闭动画并保留静态选中框。状态通过 `SetState`、`SetHovered`、`SetFocused`、`SetReducedMotion` 设置，规则资格仍由项目决定。

每个组件独立持有材质；启用TexCoord1/2/3，销毁时不撤销Canvas通道。选中且未减弱动画时逐帧更新四个顶点。支持stencil Mask与RectMask2D硬裁切，不支持softness；不将本组件作为Mask源。外光只是透明颜色叠加，不是Bloom；几何留白为 `max(6,2×glowRadius)`，视口边缘可能裁切，性能及合批须在接入后验证。

## 与现有界面的关系

按用户 2026-11-17 的要求，以上五类已接入对象的选中态以 SelectionFX 替代旧选中框。CardPicker 旧 `card-selected.png` 已无其他有效用途，退出可用候选；仍服务其他控件的共享状态图继续保留，其他状态和独立徽标保持。原板、卡面和静态布局仍由当前场景/预制体负责。

原包未提供独立勾、锁或不可选原因标记。v3 的 `selectedBadge` 仅可绑定本效果专用的选中标记，已不控制 `unavailableBadge`；原有锁和原因由原游戏管理。缺少合适对象时报告具体缺项，不从预览取图、不生成替代图、不借包内建议恢复已删除的城市样式锁图标。旧选中框内置勾与独立新勾不能重复叠加。

组件不负责单选排他、支付、奖励、确认、持久化或无障碍朗读；`SetState(Unavailable)`不会自动禁止原Button提交。原说明关于不可选项可查看、清空待选、焦点导航、状态徽标的建议不能覆盖已有业务合同。关闭窗口、回池和更换候选时由调用方重置状态，防止异步旧回调影响新窗口。

## 核验范围与参考资料

v8 ZIP CRC 已核验，审阅后复跑包内 `Tests/validate.py`，**4241项离线数学/源码结构检查通过**。未运行Demo或对齐工具；Unity及实际画面结果另行记录。原包面向Unity 2021.3+/2022.3/Unity 6、UGUI及Built-in/URP Screen Space Overlay；这些是目标范围，不是已验证兼容性声明。Camera/World Space、HDRP、XR和移动GPU同样未验。

- [原始README](原始说明/NoMadCity-Unity-SelectionFX/README.zh-CN.md)：完整用途、方法、限制及接入验收清单。
- [入库参数](../参数/NoMadCity-Unity-SelectionFX/入库参数.json)：文件映射、参数、五种对象职责与本次核验记录。
- [原交付验证记录](../参考/NoMadCity-Unity-SelectionFX/VALIDATION.txt)、[离线检查源码](../参考/NoMadCity-Unity-SelectionFX/Tests/validate.py)、[演示生成器源码](../参考/NoMadCity-Unity-SelectionFX/Assets/NoMadCitySelectionFX/Editor/SelectionEffectDemo.cs)：只读参考，不作为正式候选；拆分归档后的相对路径不是可直接执行的安装结构。

日常运行 `python -B -X utf8 tools/ui/curate_ui_library.py --verify` 校验组件字节、依赖与登记，不执行包内代码。原始说明中的整包复制和菜单操作不构成当前任务的运行授权。
