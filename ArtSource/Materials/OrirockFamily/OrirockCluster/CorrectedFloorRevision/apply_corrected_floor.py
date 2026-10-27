"""左下前移；按真实接地角重定地板，独立重建两块背撑。"""
from pathlib import Path
import bpy
import bmesh
import json
import math
from mathutils import Matrix, Vector

OUT = Path(__file__).resolve().parent
base = bpy.app.driver_namespace['cluster_square_revision']
work = base['scene']
if bpy.context.scene != work or bpy.app.is_job_running('RENDER'):
    raise RuntimeError('等待当前渲染结束并确认活动场景。')
if work.name != '固源岩组_标准方块正面姿态修订':
    raise RuntimeError('只从保留的标准方块姿态阶段继续。')
plan = json.loads((OUT / '新地板与后撑方案.json').read_text(encoding='utf-8'))
R = Matrix(plan['alignment_matrix'])
T = Vector(plan['world_translation'])
M = R.to_4x4()
M.translation = T
old_front = base['front'].copy()
advance = old_front * plan['left_depth_advance']
right, up, front = [R @ base[k] for k in ('right', 'up', 'front')]
camera_location = M @ base['camera_location']
camera_quat = R.to_quaternion() @ base['camera_quat']

scene = bpy.data.scenes.new('固源岩组_左下在前_重定地板与后撑')
scene.world = work.world.copy()
scene.render.engine = work.render.engine
scene.unit_settings.system = work.unit_settings.system
scene.unit_settings.scale_length = work.unit_settings.scale_length
for k in ('view_transform', 'look', 'exposure', 'gamma'):
    setattr(scene.view_settings, k, getattr(work.view_settings, k))
scene.cycles.samples = 32
scene.cycles.use_denoising = True
scene.render.resolution_x = scene.render.resolution_y = 1000
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = 'PNG'
scene.render.image_settings.color_mode = 'RGBA'
scene.render.film_transparent = True
objects, collections = {}, {}
for old in work.collection.children:
    new = bpy.data.collections.new(old.name + '_重定地板')
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
game = collections[base['game']]
preview = collections[base['preview']]

for index, part in enumerate(parts[:5]):
    for v in part.data.vertices:
        v.co = R @ (v.co + (advance if index == 1 else Vector())) + T
    part.data.update()
for obj in preview.objects:
    obj.matrix_world = M @ obj.matrix_world

rows = json.loads(json.dumps(base['rows']))
for index, row in enumerate(rows):
    row['world_vertices'] = [list(R @ (Vector(v) + (advance if index == 1 else Vector())) + T)
                             for v in row['world_vertices']]
cuts = json.loads(json.dumps(base['cuts']))
for cut in cuts:
    d = advance if cut['block'] == 1 else Vector()
    cut['plane_world_points'] = [list(R @ (Vector(v) + d) + T)
                                 for v in cut['plane_world_points']]
    cut['plane_world_normal'] = list(R @ Vector(cut['plane_world_normal']))

support_records = []
for spec in plan['support_specs']:
    part = parts[spec['index']]
    original = [v.co.copy() for v in part.data.vertices]
    center_old = sum(original, Vector()) / 8
    side_old = (original[1] - original[0]).length
    yaw = Matrix.Rotation(math.radians(spec['yaw_degrees']), 3, 'Z')
    rotation = yaw @ R
    shape = [(rotation @ (v - center_old)) * (spec['size'] / side_old) for v in original]
    cz = -min(v.z for v in shape)
    px, py = spec['center_pixels']
    xy = Matrix(((right.x, right.y), (up.x, up.y))).inverted() @ Vector((
        (px - 91) * .016 + right.z * (T.z - cz),
        (91 - py) * .016 + up.z * (T.z - cz)))
    center = Vector((xy.x, xy.y, cz))
    for v, local in zip(part.data.vertices, shape):
        v.co = local + center
    part.data.update()
    support_records.append(dict(spec, center_world=list(center),
                                 zmin=min(v.co.z for v in part.data.vertices)))

# 沿用本阶段实际观察后确认的背撑摆放；不重置回首次试摆。
refined_support_path = OUT / '后撑暗部方案.json'
if refined_support_path.exists():
    final_supports = json.loads(refined_support_path.read_text(encoding='utf-8'))['supports']
    restored_old_support = (OUT / '旧白模中央承托恢复方案.json').exists()
    parts[5].data = (base['parts'][5].data if restored_old_support else parts[6].data).copy()
    for spec in final_supports:
        for vertex, co in zip(parts[spec['index']].data.vertices, spec['vertices']):
            vertex.co = co
        parts[spec['index']].data.update()
        if restored_old_support and spec['index'] == 5:
            parts[5].name = '中后_恢复旧白模完整承托方块'
            for face in parts[5].data.polygons:
                if face.normal.dot(front) > .05:
                    face.material_index = 1
    support_records = final_supports

middle_support_path = OUT / '中央暗色支撑恢复方案.json'
if middle_support_path.exists() and not (OUT / '旧白模中央承托恢复方案.json').exists():
    middle_spec = json.loads(middle_support_path.read_text(encoding='utf-8'))
    middle = base['parts'][5].copy()
    middle.data = base['parts'][5].data.copy()
    middle.name = '中下_恢复原有暗色支撑_完整正方体'
    parts[5].users_collection[0].objects.link(middle)
    middle.parent = root
    middle.matrix_world = parts[5].matrix_world.copy()
    for vertex, co in zip(middle.data.vertices, middle_spec['vertices']):
        vertex.co = co
    middle.data.update()
    parts.append(middle)
    support_records.append(dict(middle_spec, role='正面中央原有暗色支撑'))

additional_support_path = OUT / '用户红线新增承托方案.json'
if additional_support_path.exists():
    extra_spec = json.loads(additional_support_path.read_text(encoding='utf-8'))
    extra = parts[6].copy()
    extra.data = parts[6].data.copy()
    extra.name = '后部_红线位置新增完整承托'
    parts[6].users_collection[0].objects.link(extra)
    extra.parent = root
    extra.matrix_world = parts[6].matrix_world.copy()
    for vertex, co in zip(extra.data.vertices, extra_spec['vertices']):
        vertex.co = co
    extra.data.update()
    parts.append(extra)
    support_records.append(extra_spec)

# 后续用户要求后撑倾斜错落；只覆盖背侧6、7号，不改变正面与中央承托。
staggered_path = OUT / '背撑错落摆放方案.json'
if staggered_path.exists():
    for spec in json.loads(staggered_path.read_text(encoding='utf-8'))['supports']:
        part = parts[spec['index']]
        for vertex, co in zip(part.data.vertices, spec['vertices']):
            vertex.co = co
        part.data.update()
        support_records[spec['index'] - 5] = spec

# 水平地板只是预览设备，不参与实体、不导出。
mesh = bpy.data.meshes.new('04_新接地平面_仅观察')
mesh.from_pydata([(-4,-4,0), (4,-4,0), (4,4,0), (-4,4,0)], [], [(0,1,2,3)])
floor_object = bpy.data.objects.new('04_地板Z0_仅观察不导出', mesh)
preview.objects.link(floor_object)
floor_mat = bpy.data.materials.new('04_中性灰地板_仅观察')
floor_mat.use_nodes = True
shader = next(n for n in floor_mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
shader.inputs['Base Color'].default_value = (.16, .16, .16, 1)
shader.inputs['Roughness'].default_value = .9
mesh.materials.append(floor_mat)
floor_object.hide_render = True
floor = 0.0

solid = None
geometry = {}

def rebuild_solid():
    global solid, geometry
    if solid is None:
        solid = parts[0].copy()
        solid.data = parts[0].data.copy()
        solid.parent = root
        solid.name = '固源岩组_完整实体_左下在前_新地板'
        game.objects.link(solid)
    else:
        # 替换当前新阶段自身的网格；不删除或触碰此前阶段。
        solid.data = parts[0].data.copy()
    bpy.context.view_layer.objects.active = solid
    for obj in scene.objects:
        obj.select_set(obj == solid)
    for part in parts[1:]:
        mod = solid.modifiers.new('真实完整方块衔接', 'BOOLEAN')
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
    unseen = set(bm.verts)
    components = 0
    while unseen:
        components += 1
        stack = [unseen.pop()]
        while stack:
            for edge in stack.pop().link_edges:
                for v in edge.verts:
                    if v in unseen:
                        unseen.remove(v)
                        stack.append(v)
    geometry = dict(boundary_edges=sum(e.is_boundary for e in bm.edges),
                    non_manifold_edges=sum(not e.is_manifold for e in bm.edges),
                    connected_components=components, volume=abs(bm.calc_volume(signed=True)),
                    triangles=len(bm.faces), model_zmin=min(v.co.z for v in bm.verts), floor_z=0)
    bm.to_mesh(solid.data)
    bm.free()
    solid.data.update()
    solid.data.uv_layers.active_index = 0
    for uv in solid.data.uv_layers:
        uv.active_render = (uv.name == 'UVMap')
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=.02)
    bpy.ops.object.mode_set(mode='OBJECT')

rebuild_solid()

def set_front_view():
    scene.camera.location = camera_location
    scene.camera.rotation_euler = camera_quat.to_euler()
    scene.camera.data.ortho_scale = base['scene'].camera.data.ortho_scale
    for area in bpy.context.screen.areas:
        if area.type == 'VIEW_3D':
            space = area.spaces.active
            space.shading.type = 'MATERIAL'
            space.overlay.show_overlays = True
            space.region_3d.view_rotation = camera_quat
            space.region_3d.view_location = T
            space.region_3d.view_distance = 4.1
            space.region_3d.view_perspective = 'CAMERA'
            space.region_3d.view_camera_zoom = 4
            space.region_3d.view_camera_offset = (0, 0)
            space.lock_camera = False
    for obj in preview.objects:
        obj.hide_set(obj != floor_object)

renders = {}
scene['制作说明'] = '左下向前0.76；上部右部关系不动；重新定地板与两块完整背撑。正面尚未验收。'
scene['已导入Unity'] = False

def save_stage():
    bpy.data.libraries.write(str(OUT / 'OrirockCluster_CorrectedFloor.blend'), {scene, bpy.context.workspace, bpy.context.screen}, fake_user=True)
    (OUT / '制作状态.json').write_text(json.dumps(dict(
        scene=scene.name, source_scene=work.name, ground_alignment=plan,
        left_depth_advance=plan['left_depth_advance'], top_right_relative_unchanged=True,
        cuts=cuts, rear_supports=support_records, geometry=geometry, renders=renders,
        unity_imported=False, approved_for_import=False, full_reference_match_accepted=False),
        ensure_ascii=False, indent=2), encoding='utf-8')

def render_front():
    set_front_view()
    floor_object.hide_render = True
    path = OUT / '新地板_材质正面.png'
    scene.render.filepath = str(path)
    bpy.ops.render.render(write_still=True)
    renders['material_front'] = str(path)
    save_stage()
    bpy.app.driver_namespace['cluster_square_revision'] = globals().copy()
    return None

set_front_view()
save_stage()
bpy.app.driver_namespace['cluster_square_revision'] = globals().copy()
bpy.app.timers.register(render_front, first_interval=.4)
print(json.dumps(dict(scene=scene.name, geometry=geometry, supports=support_records,
                      render_queued=True), ensure_ascii=False))
