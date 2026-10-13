# T5 材料制作与修订

制作日期：2026-10-05。本批六个模型均由独立子代理制作或返工，主代理负责参考正面核对及实际收藏室中的旋转、侧背与复位观察。已有 D32钢也重做；藏品编号仍保留 01–06，新增五项顺延为 07–11，目录共 11 条。六项均沿用 T5“稀有”及项目现有背景基色 `#FFC802`。

| 编号 | 材料及造型参考 | 当前模型 | 可编辑源文件 |
| --- | --- | --- | --- |
| 06 | [D32钢](https://prts.wiki/w/D32钢) | 重做为球体；各方向呈中心密、外围疏的交错结构。 | [D32Steel_Finished.blend](F:/游城拓荒模拟器/ArtSource/Materials/D32Steel/D32Steel_Finished.blend) |
| 07 | [重相位对映体](https://prts.wiki/w/重相位对映体) | 三棱锥、小底座与内部实体镜片；旋转时的光斑来自内部折射与反射。 | [RephasicEnantiomer_Finished.blend](F:/游城拓荒模拟器/ArtSource/Materials/RephasicEnantiomer/RephasicEnantiomer_Finished.blend) |
| 08 | [烧结核凝晶](https://prts.wiki/w/烧结核凝晶) | 顶盖与矩形底座平行；中部为有自然空缺的不规则晶簇，不添加原长方体边角框架。 | [NucleicCrystalSinter_Finished.blend](F:/游城拓荒模拟器/ArtSource/Materials/NucleicCrystalSinter/NucleicCrystalSinter_Finished.blend) |
| 09 | [晶体电子单元](https://prts.wiki/w/晶体电子单元) | 弧面帐篷核心按 0.70 体量调整，矩形脚置于单一封底八边形凹槽；默认正面姿态正在校准，**待验收**。 | [CrystallineElectronicUnit_Finished.blend](F:/游城拓荒模拟器/ArtSource/Materials/CrystallineElectronicUnit/CrystallineElectronicUnit_Finished.blend) |
| 10 | [双极纳米片](https://prts.wiki/w/双极纳米片) | 四个不同宽高的红铜矩框同心共轴；真圆盘直径 0.70、厚 0.095，与最大框的中心平面共面。标注四色仅用于理解框位置。 | [BipolarNanoflake_Finished.blend](F:/游城拓荒模拟器/ArtSource/Materials/BipolarNanoflake/BipolarNanoflake_Finished.blend) |
| 11 | [聚合剂](https://prts.wiki/w/聚合剂) | 盒体深度从 0.20 增至 0.30，并细化银色盒体、青色试剂管与绿色标签。 | [PolymerizationPreparation_Finished.blend](F:/游城拓荒模拟器/ArtSource/Materials/PolymerizationPreparation/PolymerizationPreparation_Finished.blend) |

双极的尺寸为统一归一前的建模坐标，盘体与四框使用同一缩放；相对间隙保持不变。背面、连接方式和遮挡结构均为单视图推定，不代表官方三维设定。稀有度圆环及用户辅助标线不属于正式模型。旧版本、白模和修订阶段均保留。

普通分件采用可迁入 Unity 的 PBR 基色、金属度、平滑度与必要的常量发光；展示材质使用展品专用 PBR 着色器处理实际收藏室的受光。重相另有光学着色器：依据三棱锥边界与内部有限镜片，在 Unity 中最多进行 10 次解析折射／反射计算，形成随观察方向变化的内部光斑。这是实时光路近似，不能当作表面纹理或完整离线物理渲染，也不以常量 PBR 概括该光学效果。

实际收藏室最终三视图归档目录为 `F:/游城拓荒模拟器/ArtSource/Materials/CollectionRoomReview/FinalRevision/`，六项分别使用编号 06–11 与表中中文材料名，文件格式为 `<编号>_<中文材料名>_正面.png`、`<编号>_<中文材料名>_侧面.png`、`<编号>_<中文材料名>_背面.png`。重相另保存 `07_重相位对映体_旋转25度.png`。图像以本轮实际游戏输出为准；电子单元仍待最终正面校准，旧图及数据检查不代表视觉验收通过。

Blender 源与游戏 FBX 分开保存；FBX 只导出模型，不带源预览相机、灯光、地板和参考背景。仅修改独立展品资产及配置，公共场景和 UI 未覆盖。展品复用中性反射，并采用只作用于展品图层的局部补光；原地板、雾、公共灯及稀有度背景配色沿用项目配置。

英文名补充参考：[Rephasic Enantiomer](https://arknights.wiki.gg/wiki/Rephasic_Enantiomer)、[Nucleic Crystal Sinter](https://arknights.wiki.gg/wiki/Nucleic_Crystal_Sinter)、[Crystalline Electronic Unit](https://arknights.wiki.gg/wiki/Crystalline_Electronic_Unit)、[Bipolar Nanoflake](https://arknights.wiki.gg/wiki/Bipolar_Nanoflake)、[Polymerization Preparation](https://arknights.fandom.com/wiki/Polymerization_Preparation)。重相使用已实装英文名 Rephasic Enantiomer。

PRTS 图标仅作为原作造型参考，引用原作图标不等于取得可自由商用的三维资产许可。
