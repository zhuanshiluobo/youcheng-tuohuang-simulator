# 提纯源岩正式模型

最终源文件：`OrirockConcentration_Finished.blend`。

- `提纯源岩_正式模型`：同向正六棱柱组成的连续嵌合岩体，主侧棱竖直，三分件共 3296 个三角面。保留现有 Unity 对象与网格名称。
- `逐块编辑_完整岩柱`：三个分别制作缺口的完整岩块，分开展示便于检查。顶缘、侧棱和底缘的小崩口有不同宽度、深度、间距与偏斜方向。
- 各个面共用统一黄褐石材，明暗由灯光和遮挡产生。没有背景、圆环和底板。
- 文件内保留程序化石材、游戏贴图材质和四张已打包的 2048×2048 贴图；外部副本位于 `textures/`。基础色不含光照，遮蔽独立保存；金属度为零，光滑度在打包图 A 通道。
- `OrirockConcentration_Finished.fbx` 是正式游戏导出，只包含模型根与三个网格。`preview_*.png` 为 Blender 预览。

Unity 使用 `Assets/YC/Presentation/Models/OrirockFamily/OrirockConcentration/` 下的原模型、材质与贴图路径，展示引用沿用 `OrirockConcentrationDisplay.prefab`。模型源保留制作尺寸，展示姿态与缩放由该预制体管理。

已在 Unity 重新导入，确认三个网格原有 GUID 和子资源 ID、材质及预制体引用有效，四张贴图实际导入为 2048×2048。已运行收藏室并选中 05 提纯源岩观察；实际游戏截图为 `Artifacts/OrirockFamily/05_提纯源岩_最终.png`。展示预制体和收藏室场景文件未改动。

历史修订目录、旧导出、旧贴图、备份和旧预览已清理。正式源不依赖历史目录。参考图保留于 `../reference/OrirockConcentration_PRTS.png`；模型依据参考自制，背面及单视图不可见结构为推定。
