# 游戏盒素材清单 v1.0

PNG 为独立贴图；`@2x.png` 使用相同逻辑绘制尺寸，切片像素乘 2。所有正式 PNG 均不含文字。除原四个玩家方块和 4×4 遮罩外，提供 1× / 2×；除原方块外提供 SVG。

| ID | 1× PNG 路径 | 1× 尺寸 | 九宫格 L/T/R/B | 2× | SVG |
| --- | --- | --- | --- | --- | --- |
| window-fill | `assets/window/fill.png` | 1560×920 | 0 / 0 / 0 / 0 | 有 | 有 |
| window-frame | `assets/window/frame.png` | 512×320 | 20 / 20 / 20 / 20 | 有 | 有 |
| dim-overlay | `assets/window/dim-overlay.png` | 4×4 | 0 / 0 / 0 / 0 | — | 有 |
| header-fill | `assets/header/fill.png` | 1528×116 | 0 / 0 / 0 / 0 | 有 | 有 |
| header-accent | `assets/header/accent.png` | 4×48 | 0 / 0 / 0 / 0 | 有 | 有 |
| game-box-icon | `assets/header/game-box-icon.png` | 64×64 | 0 / 0 / 0 / 0 | 有 | 有 |
| module-fill | `assets/modules/fill.png` | 512×384 | 0 / 0 / 0 / 0 | 有 | 有 |
| module-frame | `assets/modules/frame.png` | 512×384 | 12 / 12 / 12 / 12 | 有 | 有 |
| module-title-fill | `assets/modules/title-fill.png` | 512×56 | 8 / 8 / 8 / 8 | 有 | 有 |
| module-title-frame | `assets/modules/title-frame.png` | 512×56 | 8 / 8 / 8 / 8 | 有 | 有 |
| module-title-accent | `assets/modules/title-accent.png` | 4×30 | 0 / 0 / 0 / 0 | 有 | 有 |
| footer-divider | `assets/window/footer-divider.png` | 1024×2 | 0 / 0 / 0 / 0 | 有 | 有 |
| marker-strip-fill | `assets/markers/strip-fill.png` | 512×64 | 8 / 8 / 8 / 8 | 有 | 有 |
| marker-strip-frame | `assets/markers/strip-frame.png` | 512×64 | 8 / 8 / 8 / 8 | 有 | 有 |
| player-01 | `assets/markers/cooperation-01-64.png` | 64×64 | 0 / 0 / 0 / 0 | — | — |
| player-02 | `assets/markers/cooperation-02-64.png` | 64×64 | 0 / 0 / 0 / 0 | — | — |
| player-04 | `assets/markers/cooperation-04-64.png` | 64×64 | 0 / 0 / 0 / 0 | — | — |
| player-08 | `assets/markers/cooperation-08-64.png` | 64×64 | 0 / 0 / 0 / 0 | — | — |
| generic-card-frame | `assets/piles/card-frame.png` | 256×256 | 8 / 8 / 8 / 8 | 有 | 有 |
| pile-hover | `assets/piles/hover-outline.png` | 256×256 | 12 / 12 / 12 / 12 | 有 | 有 |
| card-shadow | `assets/piles/shadow.png` | 256×256 | 18 / 18 / 18 / 18 | 有 | 有 |
| count-badge | `assets/tokens/count-badge.png` | 64×32 | 6 / 6 / 6 / 6 | 有 | 有 |
| token-item-hover | `assets/tokens/item-hover.png` | 128×96 | 8 / 8 / 8 / 8 | 有 | 有 |
| scroll-track | `assets/scrollbar/track.png` | 8×128 | 4 / 4 / 4 / 4 | 有 | 有 |
| scroll-thumb-default | `assets/scrollbar/thumb-default.png` | 8×72 | 4 / 4 / 4 / 4 | 有 | 有 |
| scroll-thumb-hover | `assets/scrollbar/thumb-hover.png` | 8×72 | 4 / 4 / 4 / 4 | 有 | 有 |
| scroll-thumb-drag | `assets/scrollbar/thumb-drag.png` | 8×72 | 4 / 4 / 4 / 4 | 有 | 有 |

完整字段见 `assets.json`。`proportionalOnly` 为 true 时只允许等比缩放；零切片的普通底图可拉伸。滚动条保持逻辑宽度 8，只纵向延展。
