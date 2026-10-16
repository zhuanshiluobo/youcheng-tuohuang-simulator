# Main Frame v3.5 — 主界面细边框补丁

本补丁重绘主界面外框、底栏边框和结算槽边框，并替换底栏底图中残留的旧边线，让左下、右下和底边在实际拼装时更干净。

## 内容

| 原资源 ID | 新内容 | 使用方式 |
| --- | --- | --- |
| `outer-frame` | 主界面完整细边框 | 中间透明，与主背景分层 |
| `bottom-frame` | 底栏细边框 | 中间透明，与底栏底图分层 |
| `chain-slot-frame` | 结算槽细边框 | 中间透明，与原结算槽底图分层 |
| `bottom-bar` | `bottom-bar-clean` 干净底图 | 无边线、无旧角残留，内部不透明 |

前三项均提供原生 SVG、1× PNG、2× PNG 和八片拆分版本；八片由四个角与四条边组成，不含中心图层。

整图位于 `assets/`，例如 `assets/outer-frame.svg`、`assets/outer-frame.png`、`assets/outer-frame@2x.png`；拆片位于 `assets/parts/`，尺寸清单位于 `assets/frame-assets.json`。

## 接入

1. 根据 `FRAME_PATCH.md` 选择整张九宫格版本或八片版本；同一个边框只使用其中一套。
2. 替换以上四个资源引用，移除对应旧边框，保留原按钮、文字和业务逻辑。
3. 读取清单中的源像素切片参数和目标逻辑切片参数；2× PNG 的源切片数值加倍，画面上的边框厚度不加倍。
4. 在实际运行分辨率和 UI 缩放下检查左右下角、底边及结算槽。

在补丁解压目录运行以下命令，向既有主界面素材目录复制资源，并合并 `asset-manifest.json` 中的四个资源 ID：

```bash
python tools/install_assets.py "完整主界面目录"
```

如果目标目录没有该清单，或准备在引擎中手动导入，只复制资源：

```bash
python tools/install_assets.py "游戏素材目录" --assets-only
```

两种方式都会将新素材放进目标目录的 `frame-assets-v3.5/`，已有同名文件在写入前备份。脚本不改写项目源码；默认合并的清单选用 2× PNG，必须同时正确使用源像素切片和 `targetSlices` 逻辑边距。

**安装资源不等于完成渲染适配。** 旧演示的 `nine()` 若忽略 `targetSlices`，仍需按 `integration/nine_slice.mjs` 适配；引擎中则将两套参数对应到导入密度和九宫格设置。底栏底图使用普通图片填充，不走边框九宫格。仅运行安装脚本不能保证任意项目的显示已经修复。

## 预览与说明

- `preview/main-frame-preview.png`：使用原有主界面演示场景拼装的新边框效果。
- `preview/edge-comparison.png`：同一演示场景的新旧边缘对比。
- `preview/asset-sheet.png`：分层素材与边角示意。
- `FRAME_PATCH.md`：诊断依据、尺寸、九宫格和八片接入说明。
- `integration/nine_slice.mjs`：源像素与目标逻辑尺寸分离的参考实现。

预览展示补丁资源的拼装效果，不是对用户运行截图的修图，也不代表已经修改或验证用户的游戏程序。
