"""观察实际截角模型：材质正面、白模正面、白模斜视，结束恢复材质正面。"""
from pathlib import Path
import bpy
import math
import json
from mathutils import Vector, Quaternion

stage = bpy.app.driver_namespace['cluster_square_revision']
target_scene = stage['scene']
output_dir = Path(__file__).resolve().parent
if bpy.context.scene != target_scene or bpy.app.is_job_running('RENDER'):
    raise RuntimeError('当前场景不符或正在渲染。')
clay = bpy.data.materials.new('04_三处平面截角_观察白模_非游戏材质')
clay.use_nodes = True
shader = next(n for n in clay.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
shader.inputs['Base Color'].default_value = (.32, .32, .32, 1)
shader.inputs['Roughness'].default_value = .9
clay.diffuse_color = (.32, .32, .32, 1)


def render_corner_observation():
    result = {}
    try:
        for label, angle, white in [('截角_材质正面', 0, False),
                                    ('截角_白模正面', 0, True),
                                    ('截角_白模斜视', 28, True)]:
            rotation = Quaternion(Vector((0, 0, 1)), math.radians(angle))
            target_scene.camera.location = rotation @ stage['camera_location']
            target_scene.camera.rotation_euler = (rotation @ stage['camera_quat']).to_euler()
            target_scene.view_layers[0].material_override = clay if white else None
            target_scene.render.filepath = str(output_dir / (label + '.png'))
            bpy.ops.render.render(write_still=True)
            result[label] = target_scene.render.filepath
    finally:
        target_scene.view_layers[0].material_override = None
        stage['set_front_view']()
        stage['save_stage'](result)
        stage['renders'] = result
        (output_dir / '观察状态.json').write_text(json.dumps(dict(
            completed=len(result) == 3, renders=result), ensure_ascii=False, indent=2), encoding='utf-8')
    return None


bpy.app.driver_namespace['cluster_corner_render'] = globals().copy()
bpy.app.timers.register(render_corner_observation, first_interval=1)
print('已安排三张真实模型观察图。')
