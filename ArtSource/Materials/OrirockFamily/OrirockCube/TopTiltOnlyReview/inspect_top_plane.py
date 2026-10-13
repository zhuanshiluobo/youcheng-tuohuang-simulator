"""只读核对顶面倾向及已冻结的崩口区域。"""
import bpy, json
import numpy as np
from pathlib import Path

OUT = Path(__file__).resolve().parent
with bpy.data.libraries.load(str(OUT.parent / 'LowerNotchReview/OrirockCube_LowerNotchReview.blend'), link=False) as (_, loaded):
    loaded.objects = ['固源岩_低位崩口与不规则底部']
mesh = loaded.objects[0].data
top = {i for p in mesh.polygons if p.material_index == 0 for i in p.vertices}
side = {i for p in mesh.polygons if p.material_index == 3 for i in p.vertices}
protected = {i for p in mesh.polygons if p.material_index in {1, 6, 7, 8, 9} for i in p.vertices}
report = {}
for stage, filename, scene_name in [
    ('回退基准', OUT.parent / 'UnifiedStoneReview/OrirockCube_UnifiedStoneReview.blend', '固源岩_连续岩色与竖直立面_待验收'),
    ('仅修顶部', OUT / 'OrirockCube_TopTiltOnlyReview.blend', '固源岩_仅顶部倾斜修正_待验收')]:
    bpy.ops.wm.open_mainfile(filepath=str(filename))
    body = bpy.data.scenes[scene_name].objects['固源岩_连续岩质主体']
    values = {}
    for label, ids in [('顶部', top), ('可修正顶部', top - protected), ('侧面顶边', top & side)]:
        xyz = np.array([body.data.vertices[i].co[:] for i in sorted(ids)])
        # 侧边近似共线，只拟合沿深度的斜率，避免对退化平面求解。
        axes = xyz[:, 1:2] if label == '侧面顶边' else xyz[:, :2]
        design = np.column_stack((axes, np.ones(len(xyz))))
        slope = np.linalg.lstsq(design, xyz[:, 2], rcond=None)[0]
        values[label] = {'顶点数': len(ids), '高度范围': [float(xyz[:, 2].min()), float(xyz[:, 2].max())],
                         '沿Y拟合斜率' if label == '侧面顶边' else '包含剥落唇口的拟合斜率X_Y': slope[:-1].tolist()}
    report[stage] = values
(OUT / 'top_plane_inspection.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False), flush=True)
