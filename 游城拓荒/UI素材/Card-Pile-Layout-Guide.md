# 卡牌自然散摊与堆叠：接入说明

适用于当前主界面的手牌区、弃牌区和盖放角色区。本文只说明排布与交互，不新增素材，也不改变已有卡面与卡背。

## 1. 核心做法

为每张牌单独设置：中心位置 `(cx, cy)`、旋转角 `angle`、绘制层级 `z`。卡牌保持相同尺寸，围绕各自中心旋转。

自然感来自小幅、稳定、互不规律的偏移。不要把牌的位置和角度都绑定在同一条圆弧上，也不要每次刷新重新随机。

| 区域 | 默认形态 | 默认点击行为 |
|---|---|---|
| 手牌 | 横向散摊，高低和角度轻微错落 | 点击单张牌查看；具体使用入口沿用游戏流程 |
| 弃牌 | 正面朝上的紧凑牌堆，主要看顶牌 | 点击整个牌堆，打开弃牌列表 |
| 盖放角色 | 背面朝上的错位叠放，各牌保留可点击部分 | 点击对应牌，按原有权限打开查看 |

计数写在区域标题中，例如“弃牌区 · 7”“盖放角色 · 3”。不要用露出来的边缘数量代替准确计数。

## 2. 尺寸与坐标约定

- `Ws`、`Hs`：现有**单张 card slot 的布局宽、高**，包含槽位自身必须保留的边框。
- `W`、`H`：**真正可摆放卡牌的内容区**宽、高，不包含标题条。
- 坐标原点在内容区左上角，X 向右、Y 向下；正角度表示顺时针。
- 所有牌的旋转中心固定在自身中心 `(0.5, 0.5)`。
- 卡面在 slot 内按原始比例完整显示，不因旋转或重叠而压扁。

**盖放区高度不固定为 `1.5×Hs`。** `W`、`H` 必须读取容器当前的实际尺寸。`Hs` 只作为卡槽比例和既有尺寸的参考，不锁定显示高度。

运行时先在归一化坐标中排牌，再根据可用宽高计算整组缩放。`H/Hs` 可以变化，不设置“必须一张半高”的分支。若读到的是包括标题的整个面板高度，先扣掉标题和内边距。

若 slot 自带透明留白或边框，它的比例不一定就是卡图的 `12:17`。应使用项目中实际的 `Ws/Hs`，不要从卡图比例反推 slot 外框尺寸。

## 3. 盖放区：优先采用的三张排布

### 3.1 初始位置

先在归一化坐标中生成三张牌，再根据容器宽高缩放并居中。下表中的 `w`、`h` 是最终计算出的显示卡槽尺寸，不能直接写死成源 slot 的尺寸。

| 从左到右 | 中心 X | 中心 Y 偏移 | 角度 | z，越大越靠前 |
|---|---:|---:|---:|---:|
| 第一张 | `0` | `-0.04 × h` | `-5°` | 0 |
| 第二张 | `0.68 × w` | `+0.06 × h` | `+3°` | 2 |
| 第三张 | `1.36 × w` | `-0.015 × h` | `-2°` | 1 |

中间一张盖在两边之上。相邻中心间隔为卡宽的 68%；不旋转时，横向重叠约 32%。牌尺寸变化后，这些偏移跟着同比变化。

### 3.2 自适应宽高

先把单张 slot 的高设为 `1`，宽设为 `aspect = Ws/Hs`。根据上表生成牌组并计算旋转后包围盒 `(Bw, Bh)`。扣除边距后的区域为 `(Aw, Ah)`：

```text
h = min(Aw / Bw, Ah / Bh, maxCardHeight)
w = h × aspect
```

因此，区域变矮时牌会变小，区域变高时牌可以变大；如果宽度已经限制了牌组，继续增高不会把牌强行拉长，剩余空间自然成为上下留白。`maxCardHeight` 是可选的放大上限，默认不限制。

示例使用 slot 比例 `120:170`、内容区宽 `320`、内边距 `12`、三张牌与上述角度。最终尺寸近似为：

| 内容区高 H | 最终单牌高 h | 主导限制 |
|---:|---:|---|
| 140 | 101.16 | 高度 |
| 255 | 171.56 | 宽度 |
| 360 | 171.56 | 宽度 |

这里 `255/170=1.5` 只是表中的一个样例，不是布局常量。窗口尺寸、标题高度或缩放变化后，重新读取内容区大小并计算。

### 3.3 不同张数

| 张数 | 建议 |
|---|---|
| 0 | 显示“暂无盖放牌”；不放虚假的卡背 |
| 1 | 居中，角度 `0°` 或很小的固定倾角 |
| 2 | 中心间距约 `0.68×w`，角度可取 `-4°、+3°`，稍微错开上下位置 |
| 3 | 使用上面的标准排布 |
| 4 张及以上 | 按实际宽度计算；放不下时启用横向滚动，不继续压成难以点击的窄缝 |

窄区域可以把中心间隔从 `0.68×w` 降低到约 `0.55×w`，但这只是初始设计范围，不是任何角度组合都保证可点击的数学下界。达到下限仍溢出时，保留牌的尺寸，使用横向滚动。

在每张牌的实际可见部分检查命中面积。基础 UI 坐标中可先以至少 `44×44` 的可操作区域作为目标，并在项目的实际缩放下确认。空间不足就减少遮挡或改用滚动，不用扩大透明点击框来抢占邻牌。

如果加入底部滚动条，要先从内容区高度中减去轨道与间距，再用剩余高度计算包围盒。滚动条不覆盖卡面。

## 4. 手牌：散摊而非圆弧

手牌仍按横向顺序排列，但每张牌的高度和倾角单独决定。

```text
cx(i) = i × step
cy(i) = 稳定的小幅偏移
angle(i) = 稳定的小幅角度
```

以下参数以最终显示卡槽宽高 `w/h` 为基准，随整组缩放：

- `step` 取 `0.82～0.95×w`，使大部分卡面可见。
- Y 偏移在 `-0.06～+0.06×h` 之间。
- 角度控制在约 `-7°～+7°`；相邻牌不必严格正负交替。
- 通常按从左到右的顺序绘制，右边的牌压住左边少量边缘。

六张样例可先使用：

| i | Y 偏移 / h | 角度 |
|---|---:|---:|
| 0 | -0.03 | -6° |
| 1 | +0.04 | -2° |
| 2 | -0.02 | +3° |
| 3 | +0.05 | -4° |
| 4 | -0.04 | +5° |
| 5 | +0.01 | +2° |

这组角度不随位置单调变化，因此不会形成扇形。正式实现时可以根据**卡牌实例 ID**产生固定偏移；同种卡的不同实例也可有不同姿态。

牌数增加时，优先保留最小可辨识面积，再采用横向滚动。不要无限缩小卡牌或无限增加重叠。完整文字通过已有卡牌查看窗口阅读。

## 5. 弃牌区：用少量可见层表达牌堆

弃牌区是整体入口，不必为每张弃牌保留可点击条带。

- 顶牌使用游戏规则指定的实际顶牌，不能为了画面好看随意挑一张。
- 背后最多画 4 层，总共最多 5 张可见卡片；其余数量由标题准确显示。
- 被压住的牌只小幅错位：X 约 `±0.08×w`，Y 约 `±0.04×h`，角度约 `±6°`。
- 顶牌用约 `-3°` 的固定角度，保持主体可辨识；顶牌绘制在最前面。
- 各层阴影轻而近，不随着实际弃牌数量无限加厚。

示例偏移表，按从底到顶绘制：

| 层 | X / w | Y / h | 角度 |
|---|---:|---:|---:|
| 底层 | -0.07 | +0.03 | -6° |
| 第二层 | +0.06 | -0.02 | +4° |
| 第三层 | -0.03 | -0.03 | -1° |
| 第四层 | +0.04 | +0.02 | +6° |
| 顶层 | 0 | 0 | -3° |

实际只有一两张弃牌时只画对应张数，不画多余假层。点击牌堆区域打开列表；后面的装饰层不要分别抢占点击事件。

## 6. 旋转后包围盒与居中

不能只检查未旋转矩形。旋转后的宽高为：

```text
boundsWidth  = |w × cos(angle)| + |h × sin(angle)|
boundsHeight = |w × sin(angle)| + |h × cos(angle)|
```

以下 JavaScript 只计算布局，不依赖具体引擎。输入容器的当前 `W/H`，不传入固定的高度倍数。`minCardHeight` 是可读性阈值：如果为了横向塞下所有牌而缩得过小，就改为横向滚动。高度本身太小时，优先保证不越界。

```js
function groupBounds(cards) {
  if (!cards.length) return null;
  let left = Infinity, top = Infinity;
  let right = -Infinity, bottom = -Infinity;
  for (const c of cards) {
    const a = c.angle * Math.PI / 180;
    const bw = Math.abs(c.w * Math.cos(a))
             + Math.abs(c.h * Math.sin(a));
    const bh = Math.abs(c.w * Math.sin(a))
             + Math.abs(c.h * Math.cos(a));
    left = Math.min(left, c.cx - bw / 2);
    right = Math.max(right, c.cx + bw / 2);
    top = Math.min(top, c.cy - bh / 2);
    bottom = Math.max(bottom, c.cy + bh / 2);
  }
  return { left, top, right, bottom,
    width: right - left, height: bottom - top };
}

function layoutCoveredCards(count, slotW, slotH, W, H, {
  padding = 12,
  minCardHeight = 96,
  maxCardHeight = Infinity
} = {}) {
  if (!Number.isInteger(count) || count < 0 ||
      ![slotW, slotH, W, H].every(v => Number.isFinite(v) && v > 0) ||
      !Number.isFinite(padding) || padding < 0 ||
      !Number.isFinite(minCardHeight) || minCardHeight <= 0 ||
      !(typeof maxCardHeight === 'number' && maxCardHeight > 0)) throw new Error('Invalid layout input');
  // 极小容器也保留正的可用尺寸。
  const px = Math.min(padding, W * 0.1);
  const py = Math.min(padding, H * 0.1);
  const Aw = W - 2 * px, Ah = H - 2 * py;
  if (!count) return { cards: [], scrollX: false, contentWidth: W };
  const aspect = slotW / slotH;
  const angles = [-5, 3, -2, 4, -3, 1];
  const offsets = [-0.04, 0.06, -0.015, 0.025, -0.03, 0.04];
  function make(step) {
    return Array.from({length: count}, (_, i) => ({
      id: i, cx: i * step * aspect,
      cy: count === 1 ? 0 : offsets[i % offsets.length],
      w: aspect, h: 1,
      angle: count === 1 ? 0 : angles[i % angles.length],
      z: count === 3 ? [0, 2, 1][i] : i
    }));
  }
  let cards = make(0.68), b = groupBounds(cards);
  // 一张旋转牌也必须能完整放进视口的宽度。
  const singleWidth = Math.max(...cards.map(c => groupBounds([c]).width));
  const heightLimit = Math.min(Ah / b.height, maxCardHeight, Aw / singleWidth);
  const readableFloor = Math.min(minCardHeight, heightLimit);
  let h = Math.min(heightLimit, Aw / b.width);
  if (count > 1 && h < readableFloor) {
    // 先适度收紧间隔；仍不够才滚动。
    cards = make(0.55);
    b = groupBounds(cards);
    h = Math.min(heightLimit, Aw / b.width);
  }
  const scrollX = count > 1 && h < readableFloor;
  if (scrollX) h = heightLimit;
  const groupW = b.width * h;
  const dx = scrollX ? px - b.left * h
    : W / 2 - (b.left + b.right) * h / 2;
  const dy = H / 2 - (b.top + b.bottom) * h / 2;
  return {
    cards: cards.map(c => ({ ...c,
      cx: c.cx * h + dx, cy: c.cy * h + dy,
      w: c.w * h, h
    })),
    scrollX,
    contentWidth: scrollX ? groupW + 2 * px : W
  };
}
```

调用示例：

```js
const result = layoutCoveredCards(
  coveredCards.length,
  slotReferenceWidth, slotReferenceHeight,
  contentRect.width, contentRect.height
);
```

示例代码的 `id: i` 只是返回索引；接入时关联到实际卡牌实例 ID。角度表用于说明初始姿态，正式项目可以替换为基于实例 ID 的稳定偏移。

当 `scrollX=true` 时，把 `contentWidth` 设为滚动内容宽度。若滚动条本身占高度，先按预留轨道高度重新调用一次布局；本轮保留轨道预留，避免“显示轨道→缩小→不需要轨道→又变大”的反复跳变。

牌数、内容区尺寸或 UI 布局变化时重算；普通鼠标移动不重算。整个牌组统一缩放，宽高和中心偏移一起变化，卡面比例保持不变。

## 7. 点击与悬停

### 精确命中旋转矩形

盖放区和手牌区都按 **z 从大到小** 测试。把鼠标位置转换到单张牌未旋转前的局部坐标，再判断是否落在矩形内：

```js
function hitCard(pointerX, pointerY, c) {
  const dx = pointerX - c.cx, dy = pointerY - c.cy;
  const a = c.angle * Math.PI / 180;
  const x = dx * Math.cos(a) + dy * Math.sin(a);
  const y = -dx * Math.sin(a) + dy * Math.cos(a);
  return Math.abs(x) <= c.w / 2 && Math.abs(y) <= c.h / 2;
}

function pickCard(pointerX, pointerY, cards) {
  return [...cards].sort((a, b) => b.z - a.z)
    .find(c => hitCard(pointerX, pointerY, c)) ?? null;
}
```

输入指针坐标须先换算到卡牌内容区，包含全局 UI 缩放、容器偏移和滚动偏移。例如横向滚动后，用“视口内 X + scrollX”参与测试。

不要用旋转后的轴对齐大包围盒直接做精确点击，它的四个空角会抢走下面牌的事件。如果引擎已经提供准确的变换后 UI 命中与绘制顺序，直接使用其结果即可。

### 推荐悬停行为

1. 初版只加细轮廓或轻微提亮，原位置和层级不动。这最稳定，也不需要额外空间。
2. 如果需要抬起预览，使用单独的上层预览副本；副本不参与点击命中，命中仍以原始排布为准。
3. 不在鼠标刚进入时立即重新分配所有牌的位置与 z，否则容易在相邻牌之间反复切换。

悬停抬升量也根据当前剩余高度决定。空间紧张时只高亮，不强行上移半张牌；完整放大阅读交给现有卡牌查看窗口。

## 8. 稳定随机与更新

随机值只在卡牌加入区域时生成并保存，或从稳定的“卡牌实例 ID + 区域种子”计算。资源刷新、鼠标移动和窗口重绘都不重新抽取。

卡牌数量变化时，允许重新计算横向中心与整组居中，但保留每张牌原来的倾角和相对 Y 偏移。需要动画时，从旧位置平滑过渡到新位置；过渡期间要明确命中使用当前位置还是目标位置，避免一套画面、一套点击范围。

触屏不依赖悬停；露出的卡面必须能直接点选。特别拥挤时使用列表或横向滚动，而不是要求玩家精准点击一条细边。

## 9. 接入检查

- 分别看 0、1、2、3 张与溢出张数；标题计数始终与实际数量一致。
- 在真实面板大小和 UI 缩放下检查，覆盖不同的 `H/Hs` 与宽高组合，不只验证 1.5 倍高度。
- 旋转后的四角、阴影没有越过标题或被边界裁掉。
- 点击重叠区域只选到最上层那张；点击露出的下层部分能选到下层牌。
- 刷新界面时姿态不跳变；鼠标移动不会触发重新随机。
- 盖放区使用原图包卡背；查看时显示哪一面沿用既有游戏权限与查看流程。

这里给出的是引擎无关的实现规范；数值例已按旋转包围盒计算核对，尚未接入你的游戏工程。
