"""在现有白模旁创建 D32钢材质版，不覆盖白模或其他场景。"""
import bpy
import math
import json
import traceback
from pathlib import Path
from mathutils import Vector

OUT = Path(__file__).resolve().parent
TEXTURES = OUT / 'textures'
TEXTURES.mkdir(exist_ok=True)
SOURCE = bpy.data.scenes.get('D32钢_白模')
if SOURCE is None:
    raise RuntimeError('请先打开 D32Steel_Blockout.blend。')

scene = bpy.data.scenes.new('D32钢_材质版')
scene.unit_settings.system = SOURCE.unit_settings.system
scene.unit_settings.scale_length = SOURCE.unit_settings.scale_length
bpy.context.window.scene = scene
collection = bpy.data.collections.new('D32钢_材质模型')
scene.collection.children.link(collection)
preview = bpy.data.collections.new('D32钢_材质预览')
scene.collection.children.link(preview)
copies = {}
for old in SOURCE.objects:
    new = old.copy()
    if old.data is not None:
        new.data = old.data.copy()
    (collection if old.type in {'MESH', 'EMPTY'} else preview).objects.link(new)
    copies[old] = new
for old, new in copies.items():
    if old.parent in copies:
        new.parent = copies[old.parent]
root = copies[next(o for o in SOURCE.objects if o.name == 'D32Steel_Root')]
root['制作阶段'] = '材质版：冷银蓝钢、暗色背面、倒角高光、细微拉丝、独立 UV 和 PBR 贴图'
meshes = [copies[o] for o in SOURCE.objects if o.type == 'MESH']

def linear_channel(value):
    return value / 12.92 if value <= 0.04045 else ((value + 0.055) / 1.055) ** 2.4

def material(name, rgb, metallic, roughness):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    color = tuple(linear_channel(c / 255) for c in rgb) + (1,)
    mat.diffuse_color = color
    node = next(n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    node.inputs['Base Color'].default_value = color
    node.inputs['Metallic'].default_value = metallic
    node.inputs['Roughness'].default_value = roughness
    if node.inputs.get('Anisotropic'):
        node.inputs['Anisotropic'].default_value = 0.28
    nodes = mat.node_tree.nodes
    links = mat.node_tree.links
    coordinate = nodes.new('ShaderNodeTexCoord')
    noise = nodes.new('ShaderNodeTexNoise')
    noise.inputs['Scale'].default_value = 185
    noise.inputs['Detail'].default_value = 2
    links.new(coordinate.outputs['Generated'], noise.inputs['Vector'])
    bump = nodes.new('ShaderNodeBump')
    bump.inputs['Strength'].default_value = 0.12
    bump.inputs['Distance'].default_value = 0.0011
    links.new(noise.outputs['Fac'], bump.inputs['Height'])
    links.new(bump.outputs['Normal'], node.inputs['Normal'])
    rough = nodes.new('ShaderNodeMapRange')
    rough.inputs['To Min'].default_value = roughness - 0.035
    rough.inputs['To Max'].default_value = roughness + 0.035
    links.new(noise.outputs['Fac'], rough.inputs['Value'])
    links.new(rough.outputs['Result'], node.inputs['Roughness'])
    mat['金属度'] = metallic
    mat['基础粗糙度'] = roughness
    return mat

steel = material('D32Steel_SilverBlue', (135, 169, 181), 0.72, 0.31)
front = material('D32Steel_FrontSilver', (181, 205, 211), 0.74, 0.25)
inner = material('D32Steel_InnerBlue', (103, 140, 158), 0.76, 0.36)
ring = material('D32Steel_RingBlue', (87, 132, 153), 0.78, 0.28)
back = material('D32Steel_DarkBack', (68, 100, 118), 0.80, 0.40)
edge = material('D32Steel_EdgeSilver', (171, 199, 206), 0.75, 0.27)
materials = [steel, front, inner, ring, back, edge]

for old, obj in copies.items():
    if obj.type != 'MESH':
        continue
    base = front if old.name.startswith('主片') else inner if old.name.startswith('内带') else ring if old.name.startswith(('细环', '外环')) else steel
    obj.data.materials.clear()
    for mat in [base, back, edge]:
        obj.data.materials.append(mat)
    for mod in obj.modifiers:
        if mod.type == 'SOLIDIFY':
            mod.material_offset = 1
            mod.material_offset_rim = 2
        elif mod.type == 'BEVEL':
            mod.segments = 3
            mod.material = 2
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    for mod in list(obj.modifiers):
        bpy.ops.object.modifier_apply(modifier=mod.name)
    obj.select_set(False)

# 为整个模型打包一个独立 UV 图集，细环和背面同样有贴图。
for obj in meshes:
    obj.select_set(True)
bpy.context.view_layer.objects.active = meshes[0]
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.009)
bpy.ops.object.mode_set(mode='OBJECT')

scene.camera = copies[SOURCE.camera]
scene.world = SOURCE.world.copy()
world_background = next(n for n in scene.world.node_tree.nodes if n.type == 'BACKGROUND')
world_background.inputs['Color'].default_value = (0.10, 0.14, 0.19, 1)
world_background.inputs['Strength'].default_value = 0.5
scene.render.engine = SOURCE.render.engine
scene.view_settings.view_transform = SOURCE.view_settings.view_transform
scene.render.resolution_x = scene.render.resolution_y = 1100
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = 'PNG'
scene.render.filepath = str(OUT / 'D32Steel_Finished_Preview.png')
scene['说明'] = '根据 PRTS 单张图标完善的冷银蓝金属版本，背面仍为推定；金环为稀有度背景，不属于模型。'

for area in bpy.context.screen.areas:
    if area.type == 'VIEW_3D':
        space = area.spaces.active
        space.shading.type = 'MATERIAL'
        space.overlay.show_overlays = False
        space.region_3d.view_rotation = scene.camera.rotation_euler.to_quaternion()
        space.region_3d.view_distance = 4.9
        space.region_3d.view_location = (0, 0, 0)
        space.region_3d.view_perspective = 'ORTHO'

status_file = OUT / 'D32Steel_FinishStatus.json'
def status(stage, **extra):
    status_file.write_text(json.dumps({'stage': stage, **extra}, ensure_ascii=False, indent=2), encoding='utf-8')
status('材质和 UV 已完成', mesh_parts=len(meshes))
bpy.ops.wm.save_as_mainfile(filepath=str(OUT / 'D32Steel_Finished.blend'))

def bake_and_export():
    try:
        bpy.context.window.scene = scene
        scene.render.engine = 'CYCLES'
        scene.cycles.samples = 16
        images = {}
        targets = {}
        for mat in materials:
            target = mat.node_tree.nodes.new('ShaderNodeTexImage')
            targets[mat] = target
        def bake(name, kind, color_space='Non-Color'):
            status('烘焙 ' + name)
            image = bpy.data.images.new('D32Steel_' + name, width=2048, height=2048, alpha=True)
            image.colorspace_settings.name = color_space
            for mat, target in targets.items():
                target.image = image
                mat.node_tree.nodes.active = target
            for obj in scene.objects:
                obj.select_set(obj in meshes)
            bpy.context.view_layer.objects.active = meshes[0]
            bpy.ops.object.bake(type=kind, margin=8)
            image.filepath_raw = str(TEXTURES / ('D32Steel_' + name + '.png'))
            image.file_format = 'PNG'
            image.save()
            images[name] = image
            return image

        def emission_mode(channel):
            for mat in materials:
                nodes, links = mat.node_tree.nodes, mat.node_tree.links
                principled = next(n for n in nodes if n.type == 'BSDF_PRINCIPLED')
                output = next(n for n in nodes if n.type == 'OUTPUT_MATERIAL')
                emission = nodes.new('ShaderNodeEmission')
                emission.inputs['Color'].default_value = principled.inputs['Base Color'].default_value if channel == 'color' else (mat['金属度'],) * 3 + (1,)
                links.new(emission.outputs[0], output.inputs['Surface'])
        def restore_surface():
            for mat in materials:
                nodes, links = mat.node_tree.nodes, mat.node_tree.links
                links.new(next(n for n in nodes if n.type == 'BSDF_PRINCIPLED').outputs[0], next(n for n in nodes if n.type == 'OUTPUT_MATERIAL').inputs['Surface'])

        emission_mode('color')
        bake('BaseColor', 'EMIT', 'sRGB')
        restore_surface()
        bake('Normal', 'NORMAL')
        bake('Occlusion', 'AO')
        bake('Roughness', 'ROUGHNESS')
        emission_mode('metallic')
        metallic = bake('Metallic', 'EMIT')
        restore_surface()
        # Unity Standard 的金属度在 R，光滑度在 A，不把粗糙度误当光滑度。
        import numpy as np
        values = np.empty(2048 * 2048 * 4, dtype=np.float32)
        rough_values = np.empty_like(values)
        metallic.pixels.foreach_get(values)
        images['Roughness'].pixels.foreach_get(rough_values)
        packed = values.reshape((-1, 4))
        packed[:, 1:3] = 0
        packed[:, 3] = 1 - rough_values.reshape((-1, 4))[:, 0]
        mask = bpy.data.images.new('D32Steel_MetallicSmoothness', width=2048, height=2048, alpha=True)
        mask.colorspace_settings.name = 'Non-Color'
        mask.pixels.foreach_set(packed.reshape(-1))
        mask.filepath_raw = str(TEXTURES / 'D32Steel_MetallicSmoothness.png')
        mask.file_format = 'PNG'
        mask.save()
        mask.pack()
        for image in images.values():
            image.pack()
        # Blender 中继续保留可编辑的程序化材质，FBX + 烘焙贴图用于游戏。
        for obj in scene.objects:
            obj.select_set(obj == root or obj in meshes)
        bpy.context.view_layer.objects.active = root
        bpy.ops.export_scene.fbx(filepath=str(OUT / 'D32Steel_Finished.fbx'), use_selection=True,
            object_types={'EMPTY', 'MESH'}, axis_forward='-Z', axis_up='Y',
            apply_unit_scale=True, use_mesh_modifiers=True, mesh_smooth_type='FACE',
            add_leaf_bones=False, bake_anim=False, path_mode='STRIP')
        scene.render.engine = SOURCE.render.engine
        bpy.ops.wm.save_as_mainfile(filepath=str(OUT / 'D32Steel_Finished.blend'))
        bpy.ops.render.render(write_still=True)
        status('完成', mesh_parts=len(meshes), triangles=sum(len(o.data.loop_triangles) for o in meshes),
            blend=str(OUT / 'D32Steel_Finished.blend'), fbx=str(OUT / 'D32Steel_Finished.fbx'))
    except Exception:
        status('失败', error=traceback.format_exc())
    return None

bpy.app.timers.register(bake_and_export, first_interval=1.0)
print('已建立独立材质版，开始烘焙 PBR 贴图并导出。')
