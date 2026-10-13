"""核对回退版只改了顶部高度，并从保存的文件渲染正面。"""
import bpy, json
from pathlib import Path

OUT = Path(__file__).resolve().parent
SOURCE = OUT.parent / 'UnifiedStoneReview/OrirockCube_UnifiedStoneReview.blend'
TARGET = OUT / 'OrirockCube_TopTiltOnlyReview.blend'
REGIONS = OUT.parent / 'LowerNotchReview/OrirockCube_LowerNotchReview.blend'

def snapshot(scene):
    meshes = {}
    for obj in scene.objects:
        if obj.type == 'MESH':
            meshes[obj.name] = {
                'vertices': [tuple(v.co) for v in obj.data.vertices],
                'faces': [tuple(p.vertices) for p in obj.data.polygons],
                'matrix': [list(row) for row in obj.matrix_world],
                'materials': [m.name if m else None for m in obj.data.materials],
            }
    c = scene.camera
    return {'meshes': meshes, 'camera': {
        'matrix': [list(row) for row in c.matrix_world],
        'type': c.data.type, 'scale': c.data.ortho_scale,
    }}

bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
before = snapshot(bpy.data.scenes['固源岩_连续岩色与竖直立面_待验收'])
bpy.ops.wm.open_mainfile(filepath=str(TARGET))
scene = bpy.data.scenes['固源岩_仅顶部倾斜修正_待验收']
bpy.context.window.scene = scene
after = snapshot(scene)
with bpy.data.libraries.load(str(REGIONS), link=False) as (available, loaded):
    loaded.objects = ['固源岩_低位崩口与不规则底部']
regions = loaded.objects[0].data
top = {i for p in regions.polygons if p.material_index == 0 for i in p.vertices}
side = {i for p in regions.polygons if p.material_index == 3 for i in p.vertices}
protected = {i for p in regions.polygons if p.material_index in {1, 6, 7, 8, 9} for i in p.vertices}
name = '固源岩_连续岩质主体'
a, b = before['meshes'][name], after['meshes'][name]
changed = {i for i, (p, q) in enumerate(zip(a['vertices'], b['vertices'])) if p != q}
report = {
    '回退基准': str(SOURCE),
    '验收文件': str(TARGET),
    '正面相机与缩放相同': before['camera'] == after['camera'],
    '主体拓扑相同': a['faces'] == b['faces'],
    '主体变换相同': a['matrix'] == b['matrix'],
    '材质槽相同': a['materials'] == b['materials'],
    '仅高度变化顶点数': len(changed),
    '横向位置变化顶点数': sum(p[:2] != q[:2] for p, q in zip(a['vertices'], b['vertices'])),
    '崩口变化顶点数': len(changed & protected),
    '顶部以外变化顶点数': len(changed - top),
    '其他网格完全相同': all(before['meshes'][n] == after['meshes'][n] for n in before['meshes'] if n != name),
    '侧面顶边高度差': max(b['vertices'][i][2] for i in top & side) - min(b['vertices'][i][2] for i in top & side),
    '状态': '仅供 Blender 验收，未导出或导入 Unity',
}
(OUT / 'restoration_verified.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False), flush=True)
if not (report['正面相机与缩放相同'] and report['主体拓扑相同'] and report['主体变换相同'] and report['材质槽相同'] and report['其他网格完全相同'] and report['横向位置变化顶点数'] == 0 and report['崩口变化顶点数'] == 0 and report['顶部以外变化顶点数'] == 0):
    raise RuntimeError('恢复文件超出顶部高度修正范围。')
scene.render.filepath = str(OUT / 'restored_front_verified.png')
bpy.ops.render.render(write_still=True)
print('RESTORED_VERSION_VERIFIED', flush=True)
