"""复用真实平面截角流程，切削前只对标准方块作等比刚体变换。"""
from pathlib import Path
import bpy

OUT = Path(__file__).resolve().parent
current = bpy.app.driver_namespace['cluster_square_revision']
if bpy.context.scene != current['scene'] or bpy.app.is_job_running('RENDER'):
    raise RuntimeError('先确认实际活动场景并等待渲染结束。')
if current['scene'].name != '固源岩组_三处真实平面截角':
    raise RuntimeError('本次从已确认的三处截角阶段继续。')

template = (OUT.parent / 'CornerTruncationRevision' / 'cut_reference_corners.py').read_text(encoding='utf-8')
template = template.replace("base = bpy.app.driver_namespace['cluster_square_revision']",
                            "base = bpy.app.driver_namespace['cluster_square_revision']['base']", 1)
template = template.replace('if bpy.context.scene != work or bpy.app.is_job_running(\'RENDER\'):',
                            "if bpy.app.is_job_running('RENDER'):", 1)
template = template.replace('固源岩组_三处真实平面截角', '固源岩组_标准方块正面姿态修订')
template = template.replace('OrirockCluster_PlaneCorners.blend', 'OrirockCluster_RigidFront.blend')

rigid_transform = r'''
import numpy as np
fit_records = json.loads((OUT / '标准方块正面拟合.json').read_text(encoding='utf-8'))['blocks']
rigid_changes = []
for fit in fit_records:
    index = fit['block']
    part = parts[index]
    old_row = rows[index]
    old_vertices = [Vector(v) for v in old_row['world_vertices']]
    old_length = old_row['edge_length']
    old_axes = Matrix([list((old_vertices[k] - old_vertices[0]) / old_length)
                       for k in (1, 3, 4)]).transposed()
    new_axes = Matrix(fit['world_rotation'])
    rigid = (new_axes @ old_axes.transposed()) * (fit['edge_length'] / old_length)
    x, y = fit['origin_pixels']
    new_origin = right * ((x - 91) * .016) + up * ((91 - y) * .016)
    new_origin += front * old_row['verts'][0][2]
    for vertex in part.data.vertices:
        vertex.co = new_origin + rigid @ (vertex.co - old_vertices[0])
    updated_vertices = [new_origin + rigid @ (v - old_vertices[0]) for v in old_vertices]
    contact_shift = Vector((0, 0, 0))
    if index in (1, 4):
        # 投影不变的深度平移，使最低原角点严格停留在原地板平面。
        lowest_z = min((part.matrix_world @ v.co).z for v in part.data.vertices)
        contact_shift = front * ((floor - lowest_z) / front.z)
        for vertex in part.data.vertices:
            vertex.co += contact_shift
        updated_vertices = [v + contact_shift for v in updated_vertices]
    part.data.update()
    rows[index] = dict(old_row, world_vertices=[list(v) for v in updated_vertices],
                       edge_length=fit['edge_length'])
    vectors = [updated_vertices[k] - updated_vertices[0] for k in (1, 3, 4)]
    rigid_changes.append(dict(block=index, side_lengths=[v.length for v in vectors],
        orthogonality_error=max(abs(vectors[i].normalized().dot(vectors[j].normalized()))
                                for i, j in ((0, 1), (0, 2), (1, 2))),
        transform='rotation_uniform_scale_translation_only',
        floor_depth_shift=list(contact_shift), landmark_fit=fit))
'''
template = template.replace('from mathutils import Vector, Quaternion',
                            'from mathutils import Vector, Quaternion, Matrix', 1)
template = template.replace('\n\ndef remove_false_cap_paint(material):',
                            '\n' + rigid_transform + '\n\ndef remove_false_cap_paint(material):', 1)
template = template.replace('cuts = []\nfor spec in specs:',
    "for spec in specs:\n    fit = next(r for r in fit_records if r['block'] == spec['block'])\n"
    "    spec['fractions'] = fit['fractions']\n"
    "cuts = []\nfor spec in specs:", 1)
template = template.replace('original_cube_pose_preserved=True',
                            'original_cube_pose_preserved=False, cube_equal_edges_preserved=True')
template = template.replace("whole_cube_pose_unchanged=True,",
                            "whole_cube_pose_unchanged=False, rigid_changes=rigid_changes,")
template = template.replace('三处为真实平面截角；标准立方体基础和整体摆放不变。正面全图尚未验收。',
    '三处真实平面截角；只用旋转、等比缩放和平移校准正面，标准正方体不变。正面全图尚未验收。')
template = template.replace('顶部、左块、右后块三处真实平面截角，待实际视觉确认',
    '标准等边方块正面姿态与截角修订，待实际对照')

exec(compile(template, str(OUT / 'apply_rigid_front.py'), 'exec'),
     dict(__file__=str(OUT / 'apply_rigid_front.py'), __name__='__main__'))
