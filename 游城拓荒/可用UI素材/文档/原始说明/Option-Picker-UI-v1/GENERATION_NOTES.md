# 本次批量售卖窗口素材生成记录

使用内置图像生成工具编辑售卖效果图，并生成无字正方形加减按钮底图喵
原有窗口、横条、主次按钮及单选样例继续复用喵

| 文件 | 用途 |
|---|---|
| `examples/repeat-options.png` | 批量售卖效果示意，带有示例文字与数量喵 |
| `assets/button-stepper.png` | 共用的无字符加减按钮底图，正负号由程序绘制喵 |

以下保留最终生成提示词，实际坐标与交互以 `OPTION_WINDOWS.md` 和预览配置为准喵

## 效果图

```text
Use case: ui-mockup / precise-object-edit.
Edit target: the supplied resource selling window. Preserve the original brown industrial metal frame, chipped edges, ivory paper option rows, restrained yellow accent color, Chinese typography, top inventory bar and four resource icon identities.
Change the interaction from per-row instant selling to batch quantities with ONE unified sell button. Wide landscape 16:9 complete window, no cropping.
Exact sample stocks: 源岩 6, 源石 2, 异铁 4, 至纯源石 1, 金券 20. Red/orange pointed crystal is 源石; blue crystal is 异铁; tan stone is 源岩; black orange orb is 至纯源石.
Main heading: “出售资源”. Subtitle: “贸易街区 · 批量出售”.
Each of the four ivory rows has: icon, resource name and unit price, current stock, estimated subtotal, then a clear compact minus / numeric quantity / plus control at far right. Completely remove all old per-row “出售1个” buttons. +/- controls are small square muted gold metal buttons. Quantity uses dark clear digits in an ivory inset field.
Row1 exact: “源岩”, “单价 3 金券”, “持有 6”, “小计 9 金券”, minus, quantity “3”, plus.
Row2 exact: “源石”, “单价 3 金券”, “持有 2”, “小计 3 金券”, minus, quantity “1”, plus.
Row3 exact: “异铁”, “单价 4 金券”, “持有 4”, “小计 8 金券”, minus, quantity “2”, plus.
Row4 exact: “至纯源石”, “单价 15 金券”, “持有 1”, “小计 0 金券”, minus disabled, quantity “0”, plus.
Footer left large “本批待售 6 个 · 预计获得 20 金券”, smaller “调整数量后统一售卖，确认前不会扣除资源”.
Footer right TWO buttons: secondary dark metal “放弃并结束”, then primary gold “确认售卖”. Only one confirm sale button in the whole window. Gold button labels dark brown, clear legibility.
All values are sample draft, current holdings at the top must remain unchanged; no selected card numbers. Do not add cat suffixes to UI text. Preserve generous spacing and coherent game UI visual hierarchy. This is a polished game UI mockup, not a photo, not a website.
```

## 加减按钮底图

```text
Use case: ui-mockup.
Asset type: reusable transparent game UI small square button background.
Style reference: the provided long gold game UI button. Create one matching SMALL SQUARE clipped-corner metal button, not a long button and not a montage.
Square 1:1 canvas, button centered and filling about 80% width and height. Orthographic front view, crisp thin dark bronze outer border, restrained bevel, muted ochre/gold flat surface, gentle scratches and chipped paint confined near the border. Large quiet blank center. Same hand-painted gritty industrial boardgame UI material as the reference, slightly darker and less bright than the primary confirmation button.
IMPORTANT: blank background only, NO plus, NO minus, NO numbers, NO text, NO icons. Program will draw symbols separately so both plus and minus share this asset.
Genuinely transparent RGBA around the button, no checkerboard baked in, no opaque canvas, no ground, no perspective, no external drop shadow, no additional decorative objects. Deliver one centered square sprite.
```

