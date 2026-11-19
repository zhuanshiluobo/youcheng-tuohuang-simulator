# CardPicker 次按钮 2.0 替换登记

2026-11-01，用户指定 `botton-secondary-v2.0.png` 替换 Card-Picker-UI-v1 次按钮，当前用途标准记为 **P01**。旧包中的实际文件名是 `assets/button-secondary.png`；新文件保留用户提供的 `botton` 拼写，不另建同义副本。

新图唯一候选：[botton-secondary-v2.0.png](../资产/多卡选择/Card-Picker-Secondary-v2.0/botton-secondary-v2.0.png)。SHA-256 为 `15dd7f3ff36ccd0db8dc8fb508e955388f5f5c862757e95e965632221c13a6f5`，1983×793，RGBA PNG，保留原始字节。

## 用途与替换范围

只用于专用 CardPicker 的次按钮，承接当前流程允许的取消、返回、重新选择等次操作。它直接覆盖该用途的旧图期望，不作为旧默认之外的例外。旧次按钮退出可用库，不保留为备用，也不能从其他包的同字节副本回捞。

CardPicker 原包其余六张图继续按各自登记用途使用。新图不扩展到主按钮、CardViewer、普通选项窗口、通用次按钮或其他同名控件。它不新增取消权限，不改变强制选择规则，不覆盖静态文字、坐标或布局。

## 映射与状态

新旧图画布同为 1983×793，新图修改右下角造型。沿用[原包说明](原始说明/Card-Picker-UI-v1/UI_LAYOUT.md)第 7 节及[原参数](../参数/Card-Picker-UI-v1/layout.json)的按钮主体映射：左上坐标 `R=(35,102,1916,582)`。原参数中的旧文件名只保留为来源，当前次按钮路径由 P01 覆写。

原包以 280×84 为逻辑按钮示例；当前控件尺寸以场景/预制体为准，不能据此重排。透明画布不作为点击区；不要重新裁图、生成状态图或添加九宫格。该图仅提供普通态，悬停、按下、选中、禁用承接既有 ColorTint。

本次测得新图 `alpha>0` 的包围框为 `(30,29,1924,764)`，旧图为 `(30,97,1926,658)`。这些边界包含低透明度边缘杂点，不等于按钮主体可见框，不能据此自动更改裁切 UV 或命中范围。

## 当前项目参数快照

2026-11-13 用户明确授权换图后，`Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/CardPickerDialog.prefab` 的 `Secondary Action/Button Artwork`（RawImage fileID `346028464777822654`）已引用 `Assets/YC/Presentation/CardPicker/Artwork/botton-secondary-v2.0.png`，GUID `25541315d395423fbeaa982be76b7a80`；项目图与可用库原图字节一致。原次按钮图未删除或覆盖。

| 项目 | 当前源 Prefab/导入参数 |
| --- | --- |
| 显示组件 | RawImage，白色 Tint，RaycastTarget=0 |
| UV（左下原点） | `(0.017650025,0.1374527,0.9662128,0.7339218)`，对应上述主体映射 |
| RectTransform | AnchorMin=(0,0)、AnchorMax=(1,1)、SizeDelta=(0,0)，随父按钮铺满 |
| 纹理导入 | Default Texture，sRGB，MipMap 关闭，Bilinear，Clamp，Alpha Is Transparency，最大尺寸4096 |
| 普通/悬停/按下 | RGBA `(1,1,1,1)` / `(1.08,1.08,1.08,1)` / `(0.78,0.78,0.78,1)` |
| 选中/禁用 | `(0.9607843,0.9607843,0.9607843,1)` / `(0.42,0.42,0.42,0.65)` |
| 状态驱动 | Button ColorTint，TargetGraphic 指向上述 RawImage，FadeDuration=0.1，独立状态 Sprite 均为空 |

此渲染链不使用 Sprite 切片、PPU 或 Image 每单位像素倍率；不要把其他按钮的 Sprite/Sliced 参数套到该图。上述为当前源资产快照，不是不可变样式规范，也不能代替运行画面验收。完整数据及文件指纹见[替换参数](../参数/项目实际用法/CardPicker次按钮2.0替换登记.json)。

本次只换绑指定源 Prefab 的次按钮贴图，未改变场景、按钮布局、映射或交互；已检查对局 HUD 引用与两个相关场景的覆盖情况。带图形设备的 EditMode 夹具验证了取消按钮响应；正常流程视觉验收仍待完成，不能把源 Prefab 静态记录或夹具通过当作所有运行入口已通过。
