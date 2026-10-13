"""按用户给出的固源岩组图标修订真实闭合石块，不用图标贴片代替三维模型。"""
import bpy
import bmesh
import math
import json
import traceback
from pathlib import Path
from mathutils import Vector

OUT = Path(__file__).resolve().parent
SIZE = 1024
STATUS = OUT / '固源岩组正面修订状态.json'

def status(stage, **extra):
    STATUS.write_text(json.dumps(dict(stage=stage, **extra), ensure_ascii=False, indent=2), encoding='utf-8')

source = (OUT / 'build_orirock_family.py').read_text(encoding='utf-8')
exec(compile(source[source.index('def linear(v):'):source.index('def convex_mesh(')], str(OUT / 'build_orirock_family.py'), 'exec'))
exec(compile(source[source.index('def bake_job(job):'):source.index('def run():')], str(OUT / 'build_orirock_family.py'), 'exec'))

old = bpy.data.scenes['固源岩组_材质版']
archive = OUT / 'OrirockCluster' / 'OrirockCluster_PreFront.blend'
if not archive.exists():
    bpy.data.libraries.write(str(archive), {old}, fake_user=True)
scene = bpy.data.scenes.new('固源岩组_正面对齐版')
bpy.context.window.scene = scene
scene.unit_settings.system = 'METRIC'
geometry = bpy.data.collections.new('固源岩组_正面对齐模型')
preview = bpy.data.collections.new('固源岩组_正面对照设备_不导出')
scene.collection.children.link(geometry)
scene.collection.children.link(preview)
root = bpy.data.objects.new('OrirockCluster_Root_正面对齐', None)
geometry.objects.link(root)
root['参考'] = '用户附图与 reference/OrirockCluster_PRTS.png；圆环不属于模型。'
root['说明'] = '正面特征点指导分块、断面及遮挡；所有石块闭合，背面与厚度由单视图推定。'
root['本体等级'] = 3
for obj in old.objects:
    if obj.type in {'CAMERA', 'LIGHT'}:
        copy = obj.copy()
        copy.data = obj.data.copy()
        preview.objects.link(copy)
        if obj.type == 'CAMERA':
            scene.camera = copy
scene.world = old.world.copy()
scene.render.engine = old.render.engine
scene.view_settings.view_transform = old.view_settings.view_transform
scene.render.resolution_x = scene.render.resolution_y = 1000
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = old.render.image_settings.file_format
scene.render.film_transparent = True
scene.render.filepath = str(OUT / 'OrirockCluster' / 'OrirockCluster_Preview.png')
scene.camera.data.ortho_scale = 3.1

# 此处是石块矿质配色，不涉及稀有度背景取色。
palette = [
    ('浅黄褐断面', (216, 200, 159)),
    ('赭褐侧面', (161, 139, 91)),
    ('炭灰主岩', (67, 69, 64)),
    ('橄榄灰断面', (102, 98, 75)),
    ('棕灰顶面', (124, 108, 81)),
    ('灰褐棱面', (127, 121, 101)),
    ('深色裂缝', (35, 40, 38)),
    ('灰青前岩', (65, 75, 72)),
]
materials = [stone_material('OrirockCluster_正面对齐', label, rgb, .86, False) for label, rgb in palette]
for mat, (label, rgb) in zip(materials, palette):
    nodes = mat.node_tree.nodes
    noise = next(n for n in nodes if n.type == 'TEX_NOISE' and n.inputs['Scale'].default_value < 30)
    noise.inputs['Scale'].default_value = 28
    ramp = next(n for n in nodes if n.type == 'VALTORGB')
    ramp.color_ramp.elements[0].color = color(tuple(round(v * .90) for v in rgb))
    ramp.color_ramp.elements[1].color = color(tuple(min(255, round(v * 1.04)) for v in rgb))
    bump = next(n for n in nodes if n.type == 'BUMP')
    bump.inputs['Strength'].default_value = .20
    bump.inputs['Distance'].default_value = .006

basis = scene.camera.rotation_euler.to_quaternion()
right = basis @ Vector((1, 0, 0))
up = basis @ Vector((0, 1, 0))
toward_camera = basis @ Vector((0, 0, 1))
meshes = []

def point(p):
    # 图标特征点只用于设计三维顶点；不是投影图片或烘焙图标明暗。
    x, y, depth = p
    return right * ((x - 91) * .016) + up * ((91 - y) * .016) + toward_camera * depth

def piece(name, boundary, inside, front_faces, thickness, rear_material=3):
    vertices = [point(p) for p in boundary + inside]
    count = len(boundary)
    center_x = sum(p[0] for p in boundary) / count
    center_y = sum(p[1] for p in boundary) / count
    back_start = len(vertices)
    for x, y, depth in boundary:
        vertices.append(point((center_x + (x-center_x)*.87, center_y + (y-center_y)*.87, depth-thickness)))
    faces = [indices for indices, material in front_faces]
    indices = [material for face, material in front_faces]
    for i in range(count):
        j = (i + 1) % count
        faces.append([i, j, back_start+j, back_start+i])
        indices.append(rear_material if i % 3 else 4)
    faces.append(list(range(back_start, back_start+count)))
    indices.append(rear_material)
    mesh = bpy.data.meshes.new(name+'_闭合网格')
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    for mat in materials:
        mesh.materials.append(mat)
    for face, index in zip(mesh.polygons, indices):
        face.material_index = index
        face.use_smooth = False
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new(name, mesh)
    geometry.objects.link(obj)
    obj.parent = root
    meshes.append(obj)
    return obj

piece('主岩_不规则高块',
    [(44,79,.27),(46,53,.25),(60,35,.26),(78,25,0),(95,22,-.05),(119,26,-.10),
     (132,39,-.11),(145,69,-.08),(140,85,.04),(91,96,.24),(69,102,.21)],
    [(65,39,.29),(101,47,.30),(110,79,.31),(73,89,.32),(55,69,.30),(111,28,.05),(102,30,.07)],
    [([2,3,4,17,12,11],2),([1,2,11,12,13,14,15,0],2),([4,5,16,17],1),
     ([17,16,5,6,7,13,12],0),([7,8,9,13],1),([14,13,9,10],3),([0,15,14,10],2)], .75)

piece('后右_黄褐边岩',
    [(132,76,-.25),(154,66,-.22),(168,66,-.27),(171,89,-.18),(160,126,-.08),(146,131,-.06),(127,108,-.07)],
    [(150,84,.03),(157,107,.04)],
    [([0,1,2,7],4),([2,3,4,8,7],0),([4,5,6,8],1),([0,7,8,6],3)], .47)

piece('左侧_低位横岩',
    [(19,96,.12),(37,87,.18),(55,89,.25),(57,104,.30),(29,111,.31),(13,110,.19)],
    [(35,101,.37)],
    [([0,1,2,6],3),([2,3,4,6],4),([4,5,0,6],2)], .45)

piece('左前_灰青断岩',
    [(13,111,.44),(41,109,.48),(55,100,.38),(65,115,.48),(62,135,.50),(43,146,.39),(28,148,.28),(17,133,.40)],
    [(38,129,.59),(27,114,.60)],
    [([0,1,9],3),([1,2,3,8,9],7),([3,4,5,8],2),([5,6,7,8],7),([7,0,9,8],7)], .61)

piece('左中_倾斜浅色断片',
    [(61,81,.50),(65,91,.55),(74,112,.63),(78,137,.58),(66,145,.47),(60,131,.46),(54,103,.49)],
    [(63,113,.68)],
    [([0,1,2,7,6],0),([2,3,4,7],1),([4,5,6,7],5)], .32)

piece('中间_横向搭接岩',
    [(73,91,.42),(105,78,.38),(131,82,.27),(132,95,.33),(107,103,.47),(87,109,.52),(77,104,.58)],
    [(102,88,.62)],
    [([0,1,2,7],4),([2,3,4,7],1),([4,5,6,7],3),([6,0,7],0)], .39)

piece('右前_浅色顶面暗色断岩',
    [(96,103,.62),(116,98,.64),(135,107,.62),(149,126,.43),(145,138,.45),(120,145,.56),(102,140,.65),(95,126,.68)],
    [(115,120,.88),(127,129,.80),(140,121,.62)],
    [([0,1,2,10,8],0),([2,3,4,9,10],3),([4,5,9],1),([5,6,7,8,9],2),
     ([9,8,10],5),([7,0,8],3)], .50)

piece('前中_深色裂块',
    [(79,110,.62),(91,111,.70),(101,130,.78),(94,143,.72),(83,145,.77),(77,136,.70)],
    [(85,124,.90),(92,119,.86)],
    [([0,1,7,6],3),([1,2,7],0),([2,3,6,7],6),([3,4,6],2),([4,5,0,6],2)], .31, 6)

piece('右下_收尾碎岩',
    [(107,135,.76),(128,132,.74),(141,137,.65),(132,149,.59),(111,156,.62),(104,149,.61)],
    [(120,144,.79)],
    [([0,1,2,6],3),([2,3,4,6],4),([4,5,0,6],2)], .34)

# 原图是紧密结合的岩组，不是悬浮的九块几何片；底部连体基岩只在裂缝中露出。
piece('内部_裂隙基岩',
    [(38,115,.10),(68,99,.10),(96,94,.08),(137,108,.08),(145,139,.10),
     (124,153,.09),(99,155,.12),(70,148,.10),(50,143,.10)],
    [(91,127,.16)],
    [([0,1,2,9],6),([2,3,4,9],2),([4,5,6,9],6),([6,7,8,0,9],2)], .42, 6)

for obj in scene.objects:
    obj.select_set(obj in meshes)
bpy.context.view_layer.objects.active = meshes[0]
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=.014)
bpy.ops.object.mode_set(mode='OBJECT')
scene['说明'] = '以附图正面轮廓与连续断面为依据；全闭合体积，不用广告牌、图标贴片或随机三角斑块。'
job = dict(key='OrirockCluster', name='固源岩组', scene=scene, root=root, meshes=meshes, materials=materials, dir=OUT/'OrirockCluster')

def finish():
    try:
        result = bake_job(job)
        for area in bpy.context.screen.areas:
            if area.type == 'VIEW_3D':
                space = area.spaces.active
                space.shading.type = 'MATERIAL'
                space.overlay.show_overlays = False
                space.region_3d.view_rotation = scene.camera.rotation_euler.to_quaternion()
                space.region_3d.view_location = toward_camera * .20
                space.region_3d.view_distance = 4.6
                space.region_3d.view_perspective = 'ORTHO'
        status('完成', model=result, revision='不规则高岩、斜向薄片、横向搭接、灰褐连续断面，闭合三维石块')
    except Exception:
        status('失败', error=traceback.format_exc())
    return None

status('正面轮廓修订完成，准备烘焙')
bpy.app.timers.register(finish, first_interval=1)
print('已在独立正面对齐场景修订闭合石块与裂隙基岩；旧阶段已保留。')
