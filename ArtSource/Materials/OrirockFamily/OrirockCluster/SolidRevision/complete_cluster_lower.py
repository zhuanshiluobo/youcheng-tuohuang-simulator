"""只补左右碎岩和下部体积；原主岩及其材质、UV、姿态不参与修改。"""
import bpy
import bmesh
import json
import math
import traceback
from pathlib import Path
from mathutils import Vector, Quaternion

OUT = Path(__file__).resolve().parent
FAMILY = OUT.parents[1]
KEY = 'OrirockClusterLower'
SIZE = 1024
STATUS = OUT / '局部制作状态.json'
(OUT / 'textures').mkdir(exist_ok=True)


def status(stage, **extra):
    STATUS.write_text(json.dumps(dict(stage=stage, **extra), ensure_ascii=False, indent=2), encoding='utf-8')


source = (FAMILY / 'build_orirock_family.py').read_text(encoding='utf-8')
exec(compile(source[source.index('def bake_job(job):'):source.index('def run():')], str(FAMILY / 'build_orirock_family.py'), 'exec'))
old = bpy.data.scenes.get('固源岩组_正面对齐版.001')
if old is None:
    raise RuntimeError('请先加载上一版正面对齐源场景，不能用整组合并版作恢复依据。')

scene = bpy.data.scenes.new('固源岩组_顶部保留_下部实体修订')
bpy.context.window.scene = scene
scene.unit_settings.system = old.unit_settings.system
scene.unit_settings.scale_length = old.unit_settings.scale_length
preserved_collection = bpy.data.collections.new('原版主岩与其余断片_保持不动')
parts_collection = bpy.data.collections.new('下部_可编辑分块_不导出')
game_collection = bpy.data.collections.new('下部_实体成品_单独导出')
preview_collection = bpy.data.collections.new('局部修订_预览设备_不导出')
for collection in (preserved_collection, parts_collection, game_collection, preview_collection):
    scene.collection.children.link(collection)
preserved_root = bpy.data.objects.new('原版保留根', None)
preserved_collection.objects.link(preserved_root)
authoring_root = bpy.data.objects.new('下部_编辑根', None)
parts_collection.objects.link(authoring_root)
root = bpy.data.objects.new(KEY + '_Root', None)
game_collection.objects.link(root)
root['改动范围'] = '仅左前灰青断岩、后右黄褐边岩、下部衔接体；上方主岩和其他断片不合并、不重烘焙。'
original_meshes = [obj for obj in old.objects if obj.type == 'MESH']
materials = [mat.copy() for mat in original_meshes[0].data.materials]
for mat in materials:
    mat.name = '固源岩组_下部局部_' + mat.name.split('正面对齐_')[-1]

replaced = ('左前_灰青断岩', '后右_黄褐边岩', '内部_裂隙基岩')
preserved = []
for obj in old.objects:
    if obj.type == 'MESH' and not obj.name.startswith(replaced):
        copy = obj.copy()
        copy.data = obj.data.copy()
        preserved_collection.objects.link(copy)
        copy.parent = preserved_root
        copy.name = obj.name.split('.')[0] + '_原版保留'
        # 原材质数据块原样引用；这些材质不进入本轮的烘焙任务。
        preserved.append((obj, copy))
    elif obj.type in {'CAMERA', 'LIGHT'}:
        copy = obj.copy()
        copy.data = obj.data.copy()
        preview_collection.objects.link(copy)
        if obj.type == 'CAMERA':
            scene.camera = copy

scene.world = old.world.copy()
scene.view_settings.view_transform = old.view_settings.view_transform
scene.render.engine = old.render.engine
scene.render.resolution_x = scene.render.resolution_y = 1000
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = old.render.image_settings.file_format
scene.render.film_transparent = True
scene.camera.data.ortho_scale = old.camera.data.ortho_scale
scene.render.filepath = str(OUT / '正面_顶部保留.png')
camera_quat = scene.camera.rotation_euler.to_quaternion()
camera_location = scene.camera.location.copy()
right = camera_quat @ Vector((1, 0, 0))
up = camera_quat @ Vector((0, 1, 0))
front = camera_quat @ Vector((0, 0, 1))
parts = []


def point(p):
    x, y, depth = p
    return right * ((x-91)*.016) + up * ((91-y)*.016) + front*depth


def mesh_object(name, vertices, faces, indices):
    mesh = bpy.data.meshes.new(name + '_网格')
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
    bmesh.ops.triangulate(bm, faces=list(bm.faces))
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new(name, mesh)
    parts_collection.objects.link(obj)
    obj.parent = authoring_root
    parts.append(obj)
    return obj


def rock_volume(name, boundary, interior, faces, thickness, side_material):
    vertices = [point(p) for p in boundary + interior]
    n = len(boundary)
    back = len(vertices)
    cx = sum(p[0] for p in boundary)/n
    cy = sum(p[1] for p in boundary)/n
    for i, (x, y, depth) in enumerate(boundary):
        vertices.append(point((cx+(x-cx)*.85, cy+(y-cy)*.87, depth-thickness*(1.02 if i%3==0 else .93))))
    cap = len(vertices)
    vertices.append(point((cx+2, cy-3, sum(p[2] for p in boundary)/n-thickness*1.17)))
    polygons = [f for f, index in faces]
    indices = [index for f, index in faces]
    for i in range(n):
        j = (i+1)%n
        polygons.append([i, j, back+j, back+i])
        indices.append(side_material)
        polygons.append([back+i, back+j, cap])
        indices.append(side_material if i//3%2==0 else 3)
    return mesh_object(name, vertices, polygons, indices)


# 保留左上低位横岩；只把用户指出的灰青前岩补成连续断面。
rock_volume('左下_完整破断岩',
    [(12,111,.49),(17,101,.35),(31,96,.38),(45,98,.39),(54,108,.48),
     (60,126,.39),(51,144,.41),(32,149,.31),(19,138,.39)],
    [(21,115,.61),(43,110,.63),(50,127,.64),(33,138,.60),(35,102,.58)],
    [([1,2,13,9],3),([2,3,4,10,13],3),([13,10,9],7),([9,10,11,12],7),
     ([0,1,9],2),([0,9,12,8],7),([8,12,7],2),([7,12,11,6],2),([6,11,10,4,5],7)],
    .91, 7)

rock_volume('右侧_完整黄褐边岩',
    [(131,81,.02),(148,67,-.12),(163,66,-.11),(169,72,-.10),(168,91,.07),
     (162,111,.18),(156,137,.19),(140,142,.25),(126,126,.30),(132,111,.39)],
    [(149,82,.28),(158,92,.29),(152,112,.45),(146,129,.46),(137,102,.38),(166,95,.12),(162,97,.20)],
    [([0,1,10,14],4),([1,2,3,10],4),([3,4,15,16,11,10],0),
     ([4,5,15],0),([11,16,15,5,6,12],0),([10,11,12,13,14],3),([6,7,13,12],1),
     ([7,8,9,14,13],3),([9,0,14],4)], .88, 1)

# 内核上沿停在原主岩下方，不扩展至上部轮廓。完整背体连接下部各块，
# 前表面退在旧断片之后，保留浅裂隙而不让裂隙贯通至背景。
outline = [(43,96,-.12),(63,87,-.16),(89,89,-.16),(107,83,-.16),(133,89,-.18),
           (149,104,-.12),(154,136,-.08),(132,148,.04),(109,151,.08),
           (89,147,.12),(66,140,.10),(39,139,-.08),(24,128,-.10),(24,107,-.10)]
n = len(outline)
vertices = [point(p) for p in outline]
for i, (x,y,depth) in enumerate(outline):
    vertices.append(point((91+(x-91)*.97, 119+(y-119)*.94, -.63-.04*(i%3))))
for i, (x,y,depth) in enumerate(outline):
    vertices.append(point((91+(x-91)*.86, 119+(y-119)*.87, -.98-.035*(i%3))))
front_center = len(vertices)
vertices.append(point((91,119,.14)))
back_center = len(vertices)
vertices.append(point((91,117,-1.12)))
faces, indices = [], []
for i in range(n):
    j=(i+1)%n
    faces.append([i,j,front_center]); indices.append(6 if i<5 else 2)
    faces.append([i,j,n+j,n+i]); indices.append(2 if i<5 else 3 if i<9 else 7)
    faces.append([n+i,n+j,2*n+j,2*n+i]); indices.append(3 if i<5 else 1 if i<8 else 7)
    faces.append([2*n+i,2*n+j,back_center]); indices.append(3 if i<5 else 1 if i<8 else 7)
core = mesh_object('下部_连续实体基岩', vertices, faces, indices)

# 只合并新制作的下部三件；主岩与所有保留断片均不在 parts 中。
solid = core.copy()
solid.data = core.data.copy()
game_collection.objects.link(solid)
solid.parent = root
solid.name = '下部_左右碎岩与完整背体'
bpy.context.view_layer.objects.active = solid
for obj in scene.objects:
    obj.select_set(obj == solid)
for part in parts:
    if part == core:
        continue
    mod = solid.modifiers.new('下部实体衔接_' + part.name, 'BOOLEAN')
    mod.operation = 'UNION'
    mod.solver = 'EXACT'
    mod.material_mode = 'TRANSFER'
    mod.object = part
    bpy.ops.object.modifier_apply(modifier=mod.name)
bm=bmesh.new(); bm.from_mesh(solid.data)
bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=.00001)
bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
bmesh.ops.triangulate(bm, faces=list(bm.faces))
geometry=dict(boundary_edges=sum(e.is_boundary for e in bm.edges),
              non_manifold_edges=sum(not e.is_manifold for e in bm.edges),
              volume=abs(bm.calc_volume(signed=True)))
bm.to_mesh(solid.data); bm.free()
solid.data.calc_loop_triangles()
geometry['triangles']=len(solid.data.loop_triangles)
parts_collection.hide_render=True
parts_collection.hide_viewport=True

# 只为新下部打包 UV，原主岩的 UV 与原材质数据块保持不动。
for obj in scene.objects:
    obj.select_set(obj == solid)
bpy.context.view_layer.objects.active=solid
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=.012)
bpy.ops.object.mode_set(mode='OBJECT')
job=dict(key=KEY, name='固源岩组下部', scene=scene, root=root, meshes=[solid], materials=materials, dir=OUT)


def preserved_check():
    results=[]
    for original, copy in preserved:
        results.append(dict(name=original.name,
            same_vertices=[tuple(v.co) for v in original.data.vertices]==[tuple(v.co) for v in copy.data.vertices],
            same_faces=[(tuple(p.vertices),p.material_index) for p in original.data.polygons]==[(tuple(p.vertices),p.material_index) for p in copy.data.polygons],
            same_uv=[tuple(v.uv) for v in original.data.uv_layers.active.data]==[tuple(v.uv) for v in copy.data.uv_layers.active.data],
            same_materials=list(original.data.materials)==list(copy.data.materials),
            same_pose=original.matrix_world==copy.matrix_world))
    return results


def render_angles():
    paths={}
    for name,angle in [('正面',0),('左侧',90),('右侧',-90),('背面',180)]:
        q=Quaternion(up,math.radians(angle))
        scene.camera.location=q@camera_location
        scene.camera.rotation_euler=(q@camera_quat).to_euler()
        scene.render.filepath=str(OUT/(name+'_顶部保留.png'))
        bpy.ops.render.render(write_still=True)
        paths[name]=scene.render.filepath
    scene.camera.location=camera_location
    scene.camera.rotation_euler=camera_quat.to_euler()
    scene.render.filepath=paths['正面']
    return paths


def finish():
    try:
        result=bake_job(job)
        renders=render_angles()
        checks=preserved_check()
        blend=OUT/'OrirockCluster_TopPreserved.blend'
        bpy.data.libraries.write(str(blend), {scene}, fake_user=True)
        info=dict(key=KEY, item_id='04', blend=str(blend),
                  fbx=str(OUT/(KEY+'_Finished.fbx')), geometry=geometry,
                  textures={kind:str(OUT/'textures'/(KEY+'_'+kind+'.png')) for kind in ['BaseColor','Normal','Occlusion','MetallicSmoothness']},
                  renders=renders, preserved=checks,
                  changes=['保留原主岩全部几何、UV、材质和姿态','仅修左前灰青岩和右侧边岩','补完整下部衔接和背侧体积'],
                  limitations='背侧厚度由单张图标推定，不声称存在官方背面参考。')
        (OUT/'局部修订信息.json').write_text(json.dumps(info,ensure_ascii=False,indent=2),encoding='utf-8')
        for area in bpy.context.screen.areas:
            if area.type=='VIEW_3D':
                space=area.spaces.active
                space.shading.type='MATERIAL'
                space.overlay.show_overlays=False
                space.region_3d.view_rotation=camera_quat
                space.region_3d.view_location=front*(-.05)
                space.region_3d.view_distance=4.7
                space.region_3d.view_perspective='ORTHO'
        status('完成',model=result,geometry=geometry,preserved=checks,renders=renders)
    except Exception:
        status('失败',error=traceback.format_exc())
    return None


status('顶部与其余断片已原样保留，下部准备烘焙',geometry=geometry)
bpy.app.timers.register(finish,first_interval=1)
print(json.dumps({'scene':scene.name,'geometry':geometry,'preserved':preserved_check()},ensure_ascii=False))
