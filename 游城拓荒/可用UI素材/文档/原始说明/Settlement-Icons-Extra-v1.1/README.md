# 插入效果备用图标扩展 v1.1

本包新增 **40 个 UI 图标**，延续 `Settlement-Effects-v1.0` 的圆润线条与配色。不修改之前的按钮素材，独立拷贝即可使用。

范围为卡牌操作、结算顺序、条件与触发、目标与范围、批量与调整。根据本次要求，未重绘卡面已有的资源、金券、分数、企业标志、玩家标记，以及其他现成的规则图示。这里的图标只是 UI 动作或状态的备用表达，不为游戏追加规则，也不替代卡面原符号。

## 尺寸与文件

每个图标有 `dark`、`light`、`muted` 三种配色，每种均提供 SVG、24×24 PNG、48×48 PNG（`@2x`）、96×96 PNG（`@4x`）。合计 120 个独立 SVG、360 个透明 PNG。所有图形保持完整轮廓，使用圆角线端，不切掉边角。

| 后缀 | 颜色 | 适合背景 |
| --- | --- | --- |
| `dark` | `#38372E` | 暖米色可用按钮 |
| `light` | `#F0E4C7` | 深色按钮、处理中按钮 |
| `muted` | `#A9A392` | 深灰色不可用按钮 |

继续在原按钮中等比显示为 **22×22 逻辑像素**，图文间距 **8 px**。画布 24×24，绘制线宽 1.7，不做九宫格、不按按钮宽度拉伸，也不把图标放大到整个按钮高度。1×／2×／4× 只是导出精度，显示尺寸相同。

`preview/overview.png` 为图标总览；`preview/actual-size.png` 展示图标在 44 px 高按钮里的实际尺寸及三种配色。`icons.json` 提供中文名称、文件路径与用途索引；`icons-sprite.svg` 提供 40 个以 `ui-` 开头的 symbol。

## 接入示例

保留原来的按钮尺寸与样式，将本包 `assets/icons-extra/` 复制到项目的素材目录，给原组件传入图标路径即可：

```js
strip.add({
  id: 'resolution-42:retrieve-card',
  label: '收回手牌',
  icon: 'assets/icons-extra/ui-card-retrieve-dark.png',
  monochrome: true
});
```

纯图标按钮沿用 48×44，并提供完整的 `ariaLabel`／悬停说明。复杂操作优先搭配文字，不要求玩家仅凭小图标猜出规则。状态变化可切换对应色版；不要给整张按钮贴图套同一颜色滤镜。

“前置插入／后置追加”“已满足／等待条件”等相近图形保留成对设计，使用时可以按实际交互挑选。`trigger-once` 与 `trigger-ready` 分别建议表示单次时机和当前就绪，含义以项目文案为准。

## 图标索引

| 名称 | 文件 ID | 建议用途 |
| --- | --- | --- |
| 查看卡牌 | `ui-card-inspect` | 点击查看、检查卡牌详情 |
| 翻面 | `ui-card-flip` | 正反面切换、揭示卡牌 |
| 收回手牌 | `ui-card-retrieve` | 收回、回收至手牌 |
| 放回牌堆 | `ui-card-return` | 归还、放回原牌堆 |
| 移出至游戏盒 | `ui-card-box` | 移出本局、放回游戏盒 |
| 交换卡牌 | `ui-card-swap` | 交换两张牌、替换当前选择 |
| 调整牌序 | `ui-card-reorder` | 整理牌堆、改变卡牌先后顺序 |
| 暂存卡牌 | `ui-card-reserve` | 保留、暂存、延后处理该牌 |
| 前置插入 | `ui-insert-before` | 在当前结算之前插入 |
| 后置追加 | `ui-insert-after` | 在当前结算之后追加 |
| 关联结算 | `ui-chain-link` | 两个效果相互关联、连锁触发 |
| 分支选择 | `ui-chain-branch` | 进入不同结算分支 |
| 合并结算 | `ui-chain-merge` | 多个来源合并处理 |
| 再次结算 | `ui-chain-repeat` | 重复执行、再触发一次 |
| 延后处理 | `ui-chain-defer` | 排队等待、暂缓当前效果 |
| 跳过此项 | `ui-chain-skip` | 忽略本次可选结算 |
| 条件满足 | `ui-condition-met` | 条件列表已满足、允许插入 |
| 等待条件 | `ui-condition-wait` | 尚需另一项条件或步骤 |
| 触发提示 | `ui-trigger-notify` | 出现新的响应时机或可用效果 |
| 执行一次 | `ui-trigger-once` | 单次执行、处理一个效果 |
| 效果就绪 | `ui-trigger-ready` | 已准备完成、可响应 |
| 暂不可用 | `ui-trigger-blocked` | 当前时机或条件不允许 |
| 暂停效果 | `ui-effect-suspend` | 暂停处理、等待玩家决定 |
| 机会结束 | `ui-effect-expire` | 时机已过、临时效果到期 |
| 选择自己 | `ui-target-self` | 以当前玩家为对象 |
| 选择他人 | `ui-target-other` | 指定另一名玩家 |
| 选择多名 | `ui-target-many` | 选取多个玩家或多个对象 |
| 全部目标 | `ui-target-all` | 所有符合条件的对象 |
| 手动指定 | `ui-target-pick` | 进入点击选择目标模式 |
| 限定区域 | `ui-scope-area` | 在某个区域内选择或生效 |
| 相邻目标 | `ui-scope-adjacent` | 仅选择相邻位置或邻接对象 |
| 沿途范围 | `ui-scope-path` | 路径、沿途或连续目标 |
| 批量处理 | `ui-batch-use` | 统一执行多项可用效果 |
| 调整数量 | `ui-quantity-adjust` | 设置一次处理的数量 |
| 替代支付 | `ui-substitute` | 使用另一个来源或对象替代 |
| 复制效果 | `ui-copy-effect` | 复制一个效果或生成副本 |
| 调整数值 | `ui-adjust-value` | 修改费用、阈值或效果参数 |
| 指定顺序 | `ui-choose-order` | 选择多个效果的执行顺序 |
| 预览结果 | `ui-preview-result` | 执行前查看可能的结果 |
| 固定效果 | `ui-pin-effect` | 保留关注、固定常用选项 |

## 验证

总览和按钮示例直接使用导出的 PNG 拼装，已检查小尺寸辨识、透明边距、三色几何一致性及输出尺寸。贴图不含中文、数字、玩家配色或卡牌原画，不会把示例含义写死到素材里。
