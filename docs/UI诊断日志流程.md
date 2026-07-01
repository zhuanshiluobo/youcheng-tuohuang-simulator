# UI 诊断日志流程

本文档定义 UI 问题排查时的日志录入、导出和读取方式。后续修改信息面板、行动面板、提示框、规则书等表现层问题前，应优先按本流程采集一次日志，避免只凭截图猜测。

## 1. 录入

运行时代码统一通过 `YC.Presentation.UiDiagnosticLog.Record(...)` 录入结构化日志。

建议字段：

- `category`：模块或问题域，例如 `InfoPanel`、`ActionPanel`、`Font`、`Mask`。
- `message`：一句话说明当前观察点。
- `context`：相关 `MonoBehaviour`、`GameObject` 或组件。记录器会自动写入对象名、类型和层级路径。
- `data`：额外结构化文本，例如 `rect=... cull=... font=...`。
- `severity`：`Info`、`Warning` 或 `Error`。

示例：

```csharp
UiDiagnosticLog.Record(
    "InfoPanel",
    "Row text canvas renderer state",
    text,
    "label=玩家 rect=" + rect.rect + " cull=" + renderer.cull,
    UiDiagnosticSeverity.Warning);
```

录入规则：

- 只记录能帮助判断分支的信息，不记录重复噪声。
- 临时诊断点应集中在一个方法里，问题解决后删除或降级为可复用检查。
- 需要跨帧观察时，记录关键帧，不在每帧无条件输出。

## 2. 导出

Editor 菜单：

```text
YC / Dev / UI Diagnostics / Export Snapshot
```

导出位置：

```text
Application.persistentDataPath/YCDiagnostics/ui-diagnostic-YYYYMMDD_HHMMSS.json
```

日志文件包含：

- 导出时间、Unity 版本、平台、持久化目录。
- 当前运行时缓冲区里的全部 UI 诊断条目。
- 每条日志的时间、帧号、级别、分类、消息、对象名、对象类型、层级路径和附加数据。

导出前如需清空旧缓冲，可使用：

```text
YC / Dev / UI Diagnostics / Clear Runtime Buffer
```

## 3. 读取

Editor 菜单读取最新日志：

```text
YC / Dev / UI Diagnostics / Read Latest Snapshot
```

Editor 菜单打开目录：

```text
YC / Dev / UI Diagnostics / Open Log Folder
```

读取时优先关注：

- `Category` 是否对应当前问题模块。
- `HierarchyPath` 是否指向预期对象。
- `Data` 中的关键状态是否和截图一致。
- UI 裁剪类问题重点看 `rect`、`anchoredPosition`、`CanvasRenderer.cull`、`maskable`、父级 `Mask/RectMask2D` 状态。

## 4. 信息面板问题当前结论

当前信息面板正文不显示问题已有一次人工诊断结果：

- 行容器存在且可见。
- `Label` 与 `Value` 的 `RectTransform`、文本内容、颜色和字体正常。
- 子文本 `CanvasRenderer.cull=True`。

因此后续修改应围绕 `RectMask2D` 裁剪、`MaskableGraphic.maskable`、文本层级和 Canvas 重建顺序验证，不应再优先怀疑数据刷新或字体资源缺失。

## 5. 后续修改前检查清单

1. 复现问题并截图。
2. 清空运行时缓冲。
3. 触发相关诊断录入。
4. 导出 JSON。
5. 读取最新 JSON，确认至少包含一个能区分根因分支的字段。
6. 再开始修改代码。
