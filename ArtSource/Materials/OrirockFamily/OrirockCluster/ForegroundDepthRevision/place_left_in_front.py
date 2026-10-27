"""修正用户明确的遮挡关系：左下在前，上部退后；不改截面和正面投影。"""
from pathlib import Path
import bpy
import bmesh
import json
import math
from mathutils import Vector, Quaternion

OUT = Path(__file__).resolve().parent
OUT.mkdir(exist_ok=True)
base = bpy.app.driver_namespace['cluster_square_revision']
work = base['scene']
if bpy.context.scene != work or bpy.app.is_job_running('RENDER'):
    raise RuntimeError('当前场景不符或渲染仍在进行。')
scene = bpy.data.scenes.new('固源岩组_左下在前_深度修正')
scene.world = work.world.copy()
scene.render.engine = work.render.engine
scene.unit_settings.system = work.unit_settings.system
scene.unit_settings.scale_length = work.unit_settings.scale_length
scene.view_settings.view_transform = work.view_settings.view_transform
scene.view_settings.look = work.view_settings.look
scene.view_settings.exposure = work.view_settings.exposure
scene.view_settings.gamma = work.view_settings.gamma
scene.cycles.samples = 24
scene.cycles.use_denoising = True
scene.render.resolution_x = scene.render.resolution_y = 1000
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = 'PNG'
scene.render.image_settings.color_mode = 'RGBA'
scene.render.film_transparent = True
objects, collections = {}, {}
for old in work.collection.children:
    new = bpy.data.collections.new(old.name + '_前后修正')
    scene.collection.children.link(new)
    new.hide_render, new.hide_viewport = old.hide_render, old.hide_viewport
    collections[old] = new
    for obj in old.objects:
        if obj == base['solid']:
            continue
        if obj in objects:
            new.objects.link(objects[obj])
            continue
        copy = obj.copy()
        if obj.data:
            copy.data = obj.data.copy()
        new.objects.link(copy)
        objects[obj] = copy
for old, new in objects.items():
    new.parent = objects.get(old.parent)
    new.matrix_world = old.matrix_world.copy()
scene.camera = objects[work.camera]
bpy.context.window.scene = scene
parts = [objects[p] for p in base['parts']]
root = objects[base['root']]
preview = collections[base['preview']]
floor = base['floor']
right, up, front = [base[k].copy() for k in ('right', 'up', 'front')]
camera_location = base['camera_location'].copy()
camera_quat = base['camera_quat'].copy()

# 沿参考相机视线退后，不改变任何顶点的正面屏幕坐标。
# 当前交叠区域的最大深度差约0.7204，0.75足以让左下完整显露。
depth_retreat = .75
offset = -front * depth_retreat
inverse = parts[0].matrix_world.inverted().to_3x3()
local_offset = inverse @ offset
for vertex in parts[0].data.vertices:
    vertex.co += local_offset
parts[0].data.update()
# 上部退后后，小块不再靠着它；沿视线微退0.02使小块搭接原后撑。
# 不移动它的正面轮廓，也不改变方块或缺口结构。
small_contact_retreat = .02
for vertex in parts[2].data.vertices:
    vertex.co -= front * small_contact_retreat
parts[2].data.update()
projection_delta_pixels = max(abs(offset.dot(right)), abs(offset.dot(up))) / .016
top_min_z = min((parts[0].matrix_world @ v.co).z for v in parts[0].data.vertices)
if top_min_z < floor - 1e-6:
    raise RuntimeError('上部退后会穿过地板，停止保存此方案。')

game = collections[base['game']]
solid = parts[0].copy()
solid.data = parts[0].data.copy()
solid.parent = root
solid.name = '固源岩组_完整实体_左下在前'
game.objects.link(solid)
bpy.context.view_layer.objects.active = solid
for obj in scene.objects:
    obj.select_set(obj == solid)
for part in parts[1:]:
    mod = solid.modifiers.new('七个方块实体衔接', 'BOOLEAN')
    mod.operation = 'UNION'
    mod.solver = 'EXACT'
    mod.material_mode = 'TRANSFER'
    mod.object = part
    bpy.ops.object.modifier_apply(modifier=mod.name)
bm = bmesh.new()
bm.from_mesh(solid.data)
bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=1e-6)
bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
bmesh.ops.triangulate(bm, faces=list(bm.faces))
remaining = set(bm.verts)
components = []
while remaining:
    stack, count = [remaining.pop()], 0
    while stack:
        vertex = stack.pop()
        count += 1
        for edge in vertex.link_edges:
            neighbor = edge.other_vert(vertex)
            if neighbor in remaining:
                remaining.remove(neighbor)
                stack.append(neighbor)
    components.append(count)
geometry = dict(boundary_edges=sum(e.is_boundary for e in bm.edges),
                non_manifold_edges=sum(not e.is_manifold for e in bm.edges),
                volume=abs(bm.calc_volume(signed=True)), components=components,
                model_zmin=min(v.co.z for v in bm.verts), floor_z=floor)
bm.to_mesh(solid.data)
bm.free()
solid.data.calc_loop_triangles()
geometry['triangles'] = len(solid.data.loop_triangles)
solid.data.uv_layers.active_index = 0
for uv in solid.data.uv_layers:
    uv.active_render = (uv.name == 'UVMap')
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=.02)
bpy.ops.object.mode_set(mode='OBJECT')
materials = list(solid.data.materials)
cuts = json.loads(json.dumps(base['cuts']))
for cut in cuts:
    if cut['block'] == 0:
        cut['plane_world_points'] = [list(Vector(p) + offset) for p in cut['plane_world_points']]
preserved = []
scene['制作说明'] = '只将上部沿视线退后，让左下块在前；正面投影、标准方块与真实截面保持。'
scene['已导入Unity'] = False


def set_front_view():
    scene.camera.location = camera_location
    scene.camera.rotation_euler = camera_quat.to_euler()
    scene.camera.data.ortho_scale = work.camera.data.ortho_scale
    for area in bpy.context.screen.areas:
        if area.type == 'VIEW_3D':
            space = area.spaces.active
            space.shading.type = 'MATERIAL'
            space.overlay.show_overlays = False
            space.region_3d.view_rotation = camera_quat
            space.region_3d.view_location = Vector((0, 0, 0))
            space.region_3d.view_distance = 4.1
            space.region_3d.view_perspective = 'ORTHO'
    for obj in preview.objects:
        obj.hide_set(True)


def save_stage(renders):
    bpy.data.libraries.write(str(OUT / 'OrirockCluster_LeftForeground.blend'), {scene}, fake_user=True)
    (OUT / '制作状态.json').write_text(json.dumps(dict(
        stage='左下在前，上部退后；整体正面尚未验收', scene=scene.name,
        geometry=geometry, cuts=cuts, renders=renders,
        top_depth_retreat=depth_retreat, top_world_offset=list(offset),
        small_block_contact_retreat=small_contact_retreat,
        front_projection_change_pixels=projection_delta_pixels,
        left_cube_unchanged=True, floor_contact_unchanged=True,
        main_shapes_and_materials_unchanged=True, prior_scene_preserved=work.name,
        unity_imported=False, approved_for_import=False,
        full_reference_match_accepted=False), ensure_ascii=False, indent=2), encoding='utf-8')


set_front_view()
save_stage({})
bpy.app.driver_namespace['cluster_square_revision'] = globals().copy()
print(json.dumps(dict(scene=scene.name, top_depth_retreat=depth_retreat,
    front_projection_change_pixels=projection_delta_pixels, top_min_z=top_min_z,
    geometry=geometry), ensure_ascii=False))
