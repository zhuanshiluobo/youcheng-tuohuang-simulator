"""在最新审阅版上补齐双红导线与青蓝环的实际连接，保留用户姿态。"""
import bpy
import bmesh
import json
import math
import shutil
import datetime
from pathlib import Path
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

scene = bpy.data.scenes['BipolarNanoflake_FixedViewFrameReview']
bpy.context.window.scene = scene
blend_path = Path(bpy.data.filepath)
folder = blend_path.parent
stamp = datetime.datetime.now(datetime.timezone(datetime.timedelta(hours=8))).strftime('%Y%m%d_%H%M%S')
backup = folder / ('BipolarNanoflake_红蓝交点修复前_' + stamp + '.blend')
shutil.copy2(blend_path, backup)
root = scene.objects['BipolarNanoflake_FixedView_Root']
core = scene.objects['核心_一体圆盘与灰色边框.001']
ring = scene.objects['青色环绕细线_闭合.001']
nodes = [scene.objects['红导线外侧节点_左'], scene.objects['红导线外侧节点_右']]
selected = list(bpy.context.selected_objects)
active = bpy.context.view_layer.objects.active
protected = [o for o in root.children_recursive if o not in nodes] + [root, scene.camera]
before = {o.name: {
    'matrix': o.matrix_world.copy(),
    'vertices': [v.co.copy() for v in o.data.vertices] if o.type == 'MESH' else None
} for o in protected}

shell_material = bpy.data.materials.new('BipolarNanoflake_红蓝交点_金属套环')
shell_material.use_nodes = True
principled = next(n for n in shell_material.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
principled.inputs['Base Color'].default_value = (.055, .080, .077, 1)
principled.inputs['Metallic'].default_value = .72
principled.inputs['Roughness'].default_value = .28
principled.inputs['Specular IOR Level'].default_value = .35

ring_to_core = core.matrix_world.inverted() @ ring.matrix_world
major_radius = 1.075
inner_radius = .00855
outer_radius = .012
half_arc = .025
arc_steps = 10
radial_steps = 24

def recalc_normals(mesh):
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bm.to_mesh(mesh)
    bm.free()
    mesh.update()

for node in nodes:
    # 重建原接线座，撤回未接触环线的细小悬空支脚。
    old_mesh = node.data
    body_material = old_mesh.materials[0]
    points = [tuple(v.co) for v in list(old_mesh.vertices)[:12]]
    body_faces = [tuple(p.vertices) for p in list(old_mesh.polygons)[:8]]
    body_mesh = bpy.data.meshes.new(node.name + '_接线座')
    body_mesh.from_pydata(points, [], body_faces)
    body_mesh.materials.append(body_material)
    body_mesh.materials.append(shell_material)
    recalc_normals(body_mesh)
    node.data = body_mesh

    # 套环沿现有青环的局部曲率生成，内孔留出装配余量。
    center_angle = math.pi if node.name.endswith('左') else 0
    points = []
    for radius in (outer_radius, inner_radius):
        for j in range(arc_steps + 1):
            angle = center_angle - half_arc + 2 * half_arc * j / arc_steps
            radial = Vector((math.cos(angle), math.sin(angle), 0))
            center = major_radius * radial
            for k in range(radial_steps):
                phi = math.tau * k / radial_steps
                point = center + radius * (math.cos(phi) * radial + math.sin(phi) * Vector((0, 0, 1)))
                points.append(tuple(ring_to_core @ point))
    stride = (arc_steps + 1) * radial_steps
    idx = lambda layer, j, k: layer * stride + j * radial_steps + k % radial_steps
    faces = []
    for j in range(arc_steps):
        for k in range(radial_steps):
            faces.append((idx(0,j,k), idx(0,j+1,k), idx(0,j+1,k+1), idx(0,j,k+1)))
            faces.append((idx(1,j,k+1), idx(1,j+1,k+1), idx(1,j+1,k), idx(1,j,k)))
    for k in range(radial_steps):
        faces.append((idx(0,0,k), idx(0,0,k+1), idx(1,0,k+1), idx(1,0,k)))
        faces.append((idx(0,arc_steps,k+1), idx(0,arc_steps,k), idx(1,arc_steps,k), idx(1,arc_steps,k+1)))
    shell_mesh = bpy.data.meshes.new(node.name + '_中空曲面套环')
    shell_mesh.from_pydata(points, [], faces)
    shell_mesh.materials.append(body_material)
    shell_mesh.materials.append(shell_material)
    recalc_normals(shell_mesh)
    for polygon in shell_mesh.polygons:
        polygon.material_index = 1
        polygon.use_smooth = polygon.index < arc_steps * radial_steps * 2
    helper = bpy.data.objects.new('临时套环_' + node.name, shell_mesh)
    scene.collection.objects.link(helper)
    helper.parent = core
    helper.matrix_parent_inverse = Matrix.Identity(4)
    bpy.context.view_layer.update()
    for obj in scene.objects:
        obj.select_set(False)
    node.select_set(True)
    bpy.context.view_layer.objects.active = node
    modifier = node.modifiers.new('接线座与中空套环连接', 'BOOLEAN')
    modifier.operation = next(i.identifier for i in modifier.bl_rna.properties['operation'].enum_items if i.identifier == 'UNION')
    modifier.solver = next(i.identifier for i in modifier.bl_rna.properties['solver'].enum_items if i.identifier == 'EXACT')
    modifier.object = helper
    bpy.ops.object.modifier_apply(modifier=modifier.name)
    bpy.data.objects.remove(helper, do_unlink=True)
    recalc_normals(node.data)
    node['交点结构'] = '青蓝闭合环穿过中空曲面套环；套环与双红线接线座形成同一实体。'
    node['套环内半径'] = inner_radius
    node['套环外半径'] = outer_radius
    node['套环弧长'] = major_radius * 2 * half_arc
    node['结构推定'] = '参考图交点的金属连接功能已补齐；精确套环截面属于单图合理推定。'

bpy.context.view_layer.update()
state_changes = []
for obj in protected:
    saved = before[obj.name]
    matrix_delta = max(abs(obj.matrix_world[i][j] - saved['matrix'][i][j]) for i in range(4) for j in range(4))
    vertex_delta = max((v.co - old).length for v, old in zip(obj.data.vertices, saved['vertices'])) if saved['vertices'] else 0
    state_changes.append({'object': obj.name, 'matrixDelta': matrix_delta, 'vertexDelta': vertex_delta})

meshes = [o for o in root.children_recursive if o.type == 'MESH']
depsgraph = bpy.context.evaluated_depsgraph_get()
trees = {}
for obj in meshes:
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    trees[obj] = BVHTree.FromPolygons([evaluated.matrix_world @ v.co for v in mesh.vertices], [tuple(p.vertices) for p in mesh.polygons], epsilon=.000001)
    evaluated.to_mesh_clear()
intersections = [{'a': a.name, 'b': b.name, 'triangleCrossings': len(trees[a].overlap(trees[b]))} for i,a in enumerate(meshes) for b in meshes[i+1:]]
report = {
    'status': '红蓝交点已制作，待实际渲染复核；未替换Unity',
    'backup': str(backup),
    'nodes': [n.name for n in nodes],
    'ring': ring.name,
    'tubeRadius': .008,
    'collarInnerRadius': inner_radius,
    'collarOuterRadius': outer_radius,
    'collarArcLength': major_radius * 2 * half_arc,
    'protectedStateChanges': state_changes,
    'intersections': intersections,
    'meshCount': len(meshes),
    'inference': '保留现有青环中心线，按参考交点补全中空金属套环；精确截面按单图合理推定。'
}
report_path = folder / 'red_blue_intersection_review.json'
report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print('JOINT_CHECK', json.dumps({'crossings': [r for r in intersections if r['triangleCrossings']], 'protectedChanges': [r for r in state_changes if r['matrixDelta'] or r['vertexDelta']], 'nodes': [(n.name, len(n.data.vertices), len(n.data.polygons)) for n in nodes]}, ensure_ascii=False), flush=True)
bpy.ops.wm.save_as_mainfile(filepath=str(blend_path), compress=True)

# 真实 Blender 渲染；临时近景设置在保存前恢复。
camera = scene.camera
old_matrix = camera.matrix_world.copy()
old_scale = camera.data.ortho_scale
old_resolution = (scene.render.resolution_x, scene.render.resolution_y, scene.render.resolution_percentage)
old_path = scene.render.filepath
old_samples = scene.cycles.samples
images = []
try:
    scene.cycles.samples = 24
    scene.cycles.use_denoising = True
    for label, scale, size, target in [
        ('双极纳米片_红蓝交点_展示视角', old_scale, (1200, 654), None),
        ('双极纳米片_红蓝交点_左侧近景', .18, (700,700), (-1.06,0,.007)),
        ('双极纳米片_红蓝交点_右侧近景', .18, (700,700), (1.06,0,.007)),
    ]:
        camera.matrix_world = old_matrix.copy()
        if target is not None:
            camera.location += core.matrix_world @ Vector(target)
        camera.data.ortho_scale = scale
        scene.render.resolution_x, scene.render.resolution_y = size
        scene.render.resolution_percentage = 100
        path = folder / (label + '.png')
        scene.render.filepath = str(path)
        bpy.context.view_layer.update()
        bpy.ops.render.render(write_still=True)
        images.append(str(path))
        print('RENDER_SAVED', str(path), flush=True)
finally:
    camera.matrix_world = old_matrix
    camera.data.ortho_scale = old_scale
    scene.render.resolution_x, scene.render.resolution_y, scene.render.resolution_percentage = old_resolution
    scene.render.filepath = old_path
    scene.cycles.samples = old_samples
    for obj in scene.objects:
        obj.select_set(False)
    for obj in selected:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = active
    bpy.context.view_layer.update()
    bpy.ops.wm.save_as_mainfile(filepath=str(blend_path), compress=True)
report['images'] = images
report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print('DONE', json.dumps({'blend': str(blend_path), 'report': str(report_path), 'images': images}, ensure_ascii=False), flush=True)
