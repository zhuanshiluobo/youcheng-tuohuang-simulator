"""以实际正面对照修正模型色面与过重纹理；不改公共背景或照明。"""
from pathlib import Path
import bpy
import json
import numpy as np

OUT = Path(__file__).resolve().parent
stage = bpy.app.driver_namespace['cluster_square_revision']
scene = stage['scene']
if bpy.context.scene != scene or bpy.app.is_job_running('RENDER'):
    raise RuntimeError('场景不符或观察渲染尚未完成。')
if scene.get('参考色面已柔化', False):
    raise RuntimeError('此材质修正已应用，不重复叠乘。')


def linear(rgb):
    value = np.array(rgb, float) / 255
    return np.where(value <= .04045, value / 12.92, ((value + .055) / 1.055)**2.4)


# 源图和当前渲染在同一183像素尺度内、避开轮廓后的区域中位数。
samples = {
    (0, 0): ([64, 62, 64], [64, 65, 65]),
    (0, 2): ([216, 191, 148], [229, 189, 149]),
    (0, 5): ([107, 95, 63], [129, 109, 70]),
    (1, 0): ([39, 47, 54], [46, 55, 64]),
    (1, 2): ([90, 76, 52], [109, 100, 73]),
    (1, 5): ([183, 167, 134], [182, 160, 127]),
    (3, 2): ([198, 179, 140], [204, 177, 139]),
    (4, 2): ([107, 95, 63], [129, 109, 70]),
    (4, 5): ([216, 191, 148], [222, 195, 152]),
    (0, 6): ([127, 95, 54], [155, 131, 86]),
    (1, 6): ([90, 76, 52], [109, 100, 73]),
    (4, 6): ([107, 95, 63], [129, 109, 70]),
}
factors = {}
for (block, face), (reference, observed) in samples.items():
    material = stage['parts'][block].data.materials[face]
    factors[material] = linear(reference) / np.maximum(linear(observed), 1e-5)

material_map = {}
records = []
for old in stage['solid'].data.materials:
    if old in material_map:
        continue
    material = old.copy()
    material.name = old.name + '_原图色面柔化'
    material_map[old] = material
    multiplier = factors.get(old, np.ones(3))
    material.diffuse_color = tuple(np.array(material.diffuse_color[:3]) * multiplier) + (1,)
    nodes, links = material.node_tree.nodes, material.node_tree.links
    tone = next((node for node in nodes if node.label == '各真实色面的矿物底色校准'), None)
    grain = next((node for node in nodes if node.label == '细密颗粒颜色'), None)
    if tone is not None:
        tone.inputs[2].default_value = tuple(np.array(tone.inputs[2].default_value[:3]) * multiplier) + (1,)
    if tone is not None and grain is not None:
        # 弱化粗大的照片式杂斑；保留真实面UV、石粒、细纹与粗糙度。
        mix = nodes.new('ShaderNodeMixRGB')
        mix.blend_type = 'MIX'
        mix.label = '原图石质色面：弱化过重粗斑'
        mix.inputs[0].default_value = .5
        mix.inputs[2].default_value = material.diffuse_color
        links.new(tone.outputs[0], mix.inputs[1])
        links.new(mix.outputs[0], grain.inputs[1])
    records.append(dict(material=material.name, linear_multiplier=multiplier.tolist(),
                        coarse_texture_flattening=.5))

for obj in [stage['solid']] + stage['parts']:
    for index, old in enumerate(obj.data.materials):
        obj.data.materials[index] = material_map.get(old, old)
stage['materials'] = list(stage['solid'].data.materials)
scene['参考色面已柔化'] = True
(OUT / '色面修订.json').write_text(json.dumps(dict(
    material_changes=records, geometry_unchanged=True,
    game_background_unchanged=True, lights_unchanged=True,
    note='来自当前真实渲染的局部色面修正，仍需重新观察，非视觉验收结论。'),
    ensure_ascii=False, indent=2), encoding='utf-8')


def observe_palette():
    stage['set_front_view']()
    scene.render.filepath = str(OUT / '色面修订_材质正面.png')
    bpy.ops.render.render(write_still=True)
    renders = dict(stage.get('renders', {}))
    renders['色面修订_材质正面'] = scene.render.filepath
    stage['save_stage'](renders)
    stage['renders'] = renders
    print('模型色面修订的真实正面渲染已保存。')
    return None


bpy.app.driver_namespace['cluster_front_palette'] = globals().copy()
bpy.app.timers.register(observe_palette, first_interval=1)
print('已复制当前模型材质，弱化粗斑并校正色面，未改旧阶段和公共照明。')
