"""仅将用户指出的三个角做成真实平面截面，保留旧场景和未切削的姿态。"""
from pathlib import Path
import bpy
import bmesh
import json
import math
from mathutils import Vector, Quaternion

OUT = Path(__file__).resolve().parent
base = bpy.app.driver_namespace['cluster_square_revision']
work = base['scene']
if bpy.context.scene != work or bpy.app.is_job_running('RENDER'):
    raise RuntimeError('当前场景改变或仍在渲染，请先查看现场。')
rows = json.loads((OUT.parent / 'SurfaceMatchRevision' / '当前块体坐标.json').read_text(encoding='utf-8'))
scene = bpy.data.scenes.new('固源岩组_三处真实平面截角')
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
    new = bpy.data.collections.new(old.name + '_平面截角')
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


def remove_false_cap_paint(material):
    """只在新副本取消把切面误画到原平面上的局部颜色帽。"""
    for node in material.node_tree.nodes:
        if node.label == '参考图局部风化皮，颜色而非额外几何':
            for link in list(node.inputs[0].links):
                material.node_tree.links.remove(link)
            node.inputs[0].default_value = 0


gray = parts[0].data.materials[0].copy()
gray.name = '04_上块灰面_取消假截面色帽'
remove_false_cap_paint(gray)
parts[0].data.materials[0] = gray

# 每处均由三条原始等边立方体棱上的交点确定单一平面。
# 未切区域的顶点、主块矩阵、后撑以及最低接地角均不移动。
specs = [
    dict(label='上块顶部_单平面截角', block=0, corner=1,
         neighbors=[0, 2, 5], fractions=[.35, .52, .25], material_face=5),
    dict(label='左块三色交角_单平面截角', block=1, corner=0,
         neighbors=[1, 3, 4], fractions=[.15, .12, .20], material_face=2),
    dict(label='右后块三色交角_单平面截角', block=4, corner=0,
         neighbors=[1, 3, 4], fractions=[.13, .20, .10], material_face=2),
]
cuts = []
for spec in specs:
    part = parts[spec['block']]
    original = [Vector(v) for v in rows[spec['block']]['world_vertices']]
    corner = original[spec['corner']]
    points = [corner.lerp(original[j], t)
              for j, t in zip(spec['neighbors'], spec['fractions'])]
    normal = (points[1] - points[0]).cross(points[2] - points[0]).normalized()
    if normal.dot(corner - points[0]) < 0:
        normal.negate()
    # 网格在原世界坐标内，保留对象矩阵，局部空间以实际矩阵换算。
    inverse = part.matrix_world.inverted()
    plane_co = inverse @ points[0]
    plane_no = (part.matrix_world.to_3x3().transposed() @ normal).normalized()
    cap = part.data.materials[spec['material_face']].copy()
    cap.name = '04_' + spec['label'] + '_真实截面石质'
    remove_false_cap_paint(cap)
    cap_slot = len(part.data.materials)
    part.data.materials.append(cap)

    bm = bmesh.new()
    bm.from_mesh(part.data)
    bmesh.ops.bisect_plane(
        bm, geom=list(bm.verts) + list(bm.edges) + list(bm.faces),
        dist=1e-7, plane_co=plane_co, plane_no=plane_no, clear_outer=True)
    boundary = [e for e in bm.edges if e.is_boundary]
    if not boundary or any(abs((v.co - plane_co).dot(plane_no)) > 2e-6
                           for e in boundary for v in e.verts):
        bm.free()
        raise RuntimeError('切削边界不是指定平面的单个封闭边界。')
    caps = bmesh.ops.holes_fill(bm, edges=boundary, sides=0)['faces']
    if len(caps) != 1:
        bm.free()
        raise RuntimeError('此处应仅生成一个平面截面，不生成槽或多个台阶。')
    face = caps[0]
    face.material_index = cap_slot
    face.smooth = False
    # 新截面自身的平面 UV，不使用整张图标投影。
    axis_u = (inverse @ points[1]) - plane_co
    axis_u.normalize()
    axis_v = plane_no.cross(axis_u).normalized()
    coords = [(v.co - plane_co).dot(axis_u) for v in face.verts]
    heights = [(v.co - plane_co).dot(axis_v) for v in face.verts]
    umin, umax, vmin, vmax = min(coords), max(coords), min(heights), max(heights)
    for layer_name in ('UVMap', 'StoneFace'):
        uv = bm.loops.layers.uv.get(layer_name) or bm.loops.layers.uv.new(layer_name)
        for loop in face.loops:
            delta = loop.vert.co - plane_co
            loop[uv].uv = ((delta.dot(axis_u) - umin) / max(umax - umin, 1e-7),
                           (delta.dot(axis_v) - vmin) / max(vmax - vmin, 1e-7))
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    entry = dict(spec, plane_world_points=[list(v) for v in points],
                 plane_world_normal=list(normal), new_face_vertices=len(face.verts),
                 cap_area=face.calc_area(), boundary_edges=sum(e.is_boundary for e in bm.edges),
                 plane_error=max(abs((v.co - plane_co).dot(plane_no)) for v in face.verts),
                 removed_corner=True, original_cube_pose_preserved=True)
    bm.to_mesh(part.data)
    bm.free()
    part.data.update()
    cuts.append(entry)

# 重新合并七个真实块体，保留作者分件和原阶段，旧实体不进入新场景。
game = next(c for c in scene.collection.children if root in c.objects.values())
solid = parts[0].copy()
solid.data = parts[0].data.copy()
solid.parent = root
solid.name = '固源岩组_完整实体_三处真实平面截角'
game.objects.link(solid)
bpy.context.view_layer.objects.active = solid
for obj in scene.objects:
    obj.select_set(obj == solid)
for part in parts[1:]:
    mod = solid.modifiers.new('完整块体实体衔接', 'BOOLEAN')
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
geometry = dict(boundary_edges=sum(e.is_boundary for e in bm.edges),
                non_manifold_edges=sum(not e.is_manifold for e in bm.edges),
                volume=abs(bm.calc_volume(signed=True)))
bm.to_mesh(solid.data)
bm.free()
solid.data.calc_loop_triangles()
geometry.update(triangles=len(solid.data.loop_triangles),
                model_zmin=min((solid.matrix_world @ v.co).z for v in solid.data.vertices),
                floor_z=floor)
if geometry['model_zmin'] < floor - 1e-6:
    raise RuntimeError('出现地板线以下几何，不保存为有效阶段。')
solid.data.uv_layers.active_index = 0
for uv in solid.data.uv_layers:
    uv.active_render = (uv.name == 'UVMap')
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=.02)
bpy.ops.object.mode_set(mode='OBJECT')
materials = list(solid.data.materials)
preserved = []
scene['制作说明'] = '三处为真实平面截角；标准立方体基础和整体摆放不变。正面全图尚未验收。'
scene['已导入Unity'] = False


def set_front_view():
    scene.camera.location = camera_location
    scene.camera.rotation_euler = camera_quat.to_euler()
    scene.camera.data.ortho_scale = base['scene'].camera.data.ortho_scale
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
    bpy.data.libraries.write(str(OUT / 'OrirockCluster_PlaneCorners.blend'), {scene}, fake_user=True)
    (OUT / '制作状态.json').write_text(json.dumps(dict(
        stage='顶部、左块、右后块三处真实平面截角，待实际视觉确认', scene=scene.name,
        cuts=cuts, geometry=geometry, renders=renders,
        prior_scene_preserved=work.name, whole_cube_pose_unchanged=True,
        floor_contact_unchanged=True, posterior_cubes_unchanged=True,
        unity_imported=False, approved_for_import=False,
        full_reference_match_accepted=False), ensure_ascii=False, indent=2), encoding='utf-8')


set_front_view()
save_stage({})
bpy.app.driver_namespace['cluster_square_revision'] = globals().copy()
print(json.dumps(dict(scene=scene.name, cuts=cuts, geometry=geometry), ensure_ascii=False))
