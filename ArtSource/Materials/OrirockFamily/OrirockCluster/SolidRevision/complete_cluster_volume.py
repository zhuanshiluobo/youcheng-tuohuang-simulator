"""补足固源岩组真实体积与侧背断面；旧正面对齐版和其他材料不改。"""
import bpy
import bmesh
import json
import math
import traceback
from pathlib import Path
from mathutils import Vector, Quaternion, Matrix

OUT = Path(__file__).resolve().parent
FAMILY = OUT.parents[1]
SIZE = 1024
STATUS = OUT / '制作状态.json'
OUT.mkdir(exist_ok=True)
(OUT / 'textures').mkdir(exist_ok=True)

def status(stage, **extra):
    STATUS.write_text(json.dumps(dict(stage=stage, **extra), ensure_ascii=False, indent=2), encoding='utf-8')

source = (FAMILY / 'build_orirock_family.py').read_text(encoding='utf-8')
exec(compile(source[source.index('def bake_job(job):'):source.index('def run():')], str(FAMILY / 'build_orirock_family.py'), 'exec'))

old = bpy.data.scenes.get('固源岩组_正面对齐版.001')
if old is None:
    with bpy.data.libraries.load(str(OUT.parent / 'OrirockCluster_Finished.blend'), link=False) as (available, target):
        target.scenes = available.scenes
    old = target.scenes[0]
scene = bpy.data.scenes.new('固源岩组_完整实体版')
bpy.context.window.scene = scene
scene.unit_settings.system = 'METRIC'
scene.unit_settings.scale_length = 1
parts_collection = bpy.data.collections.new('固源岩组_可编辑分块')
game_collection = bpy.data.collections.new('固源岩组_实体成品_导出')
preview_collection = bpy.data.collections.new('固源岩组_多角度预览_不导出')
scene.collection.children.link(parts_collection)
scene.collection.children.link(game_collection)
scene.collection.children.link(preview_collection)
authoring_root = bpy.data.objects.new('固源岩组_分块编辑根', None)
parts_collection.objects.link(authoring_root)
root = bpy.data.objects.new('OrirockCluster_Root_完整实体', None)
game_collection.objects.link(root)
root['说明'] = '不是正面板片：统一相连实体、完整侧背体积；图标只约束正面，背侧结构由单图推定。'
root['本体等级'] = 3
original_meshes = [o for o in old.objects if o.type == 'MESH']
materials = [m.copy() for m in original_meshes[0].data.materials]
for mat in materials:
    mat.name = '固源岩组_实体岩质_' + mat.name.split('正面对齐_')[-1]
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = next(n for n in nodes if n.type == 'BSDF_PRINCIPLED')
    coord = next(n for n in nodes if n.type == 'TEX_COORD')
    grain = nodes.new('ShaderNodeTexNoise')
    grain.label = '完整实体的细颗粒，不绘制光照或随机三角拼色'
    grain.inputs['Scale'].default_value = 120
    grain.inputs['Detail'].default_value = 2
    links.new(coord.outputs['Generated'], grain.inputs['Vector'])
    ramp = nodes.new('ShaderNodeValToRGB')
    ramp.color_ramp.elements[0].position = .20
    ramp.color_ramp.elements[0].color = (.58, .58, .58, 1)
    ramp.color_ramp.elements[1].position = .79
    ramp.color_ramp.elements[1].color = (1, 1, 1, 1)
    links.new(grain.outputs['Fac'], ramp.inputs[0])
    base = bsdf.inputs['Base Color'].links[0].from_socket
    mix = nodes.new('ShaderNodeMixRGB')
    mix.blend_type = 'MULTIPLY'
    mix.inputs[0].default_value = .17
    links.new(base, mix.inputs[1])
    links.new(ramp.outputs[0], mix.inputs[2])
    links.new(mix.outputs[0], bsdf.inputs['Base Color'])

parts = []
skip = ('左侧_低位横岩', '左前_灰青断岩', '后右_黄褐边岩', '内部_裂隙基岩')
for obj in old.objects:
    if obj.type == 'MESH' and not obj.name.startswith(skip):
        copy = obj.copy()
        copy.data = obj.data.copy()
        parts_collection.objects.link(copy)
        copy.parent = authoring_root
        copy.name = obj.name.split('.')[0] + '_实体分件'
        face_materials = [face.material_index for face in copy.data.polygons]
        copy.data.materials.clear()
        for mat in materials:
            copy.data.materials.append(mat)
        for face, index in zip(copy.data.polygons, face_materials):
            face.material_index = index
        parts.append(copy)
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
scene.render.filepath = str(OUT / '正面_完整实体.png')
scene.camera.data.ortho_scale = 3.15
camera_quat = scene.camera.rotation_euler.to_quaternion()
right = camera_quat @ Vector((1, 0, 0))
up = camera_quat @ Vector((0, 1, 0))
front = camera_quat @ Vector((0, 0, 1))
camera_location = scene.camera.location.copy()

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
    # 后环和背部隆起提供真实体积，不将正面轮廓简单拉成一块薄板。
    vertices = [point(p) for p in boundary + interior]
    n = len(boundary)
    back = len(vertices)
    cx = sum(p[0] for p in boundary) / n
    cy = sum(p[1] for p in boundary) / n
    for i, (x, y, depth) in enumerate(boundary):
        vertices.append(point((cx+(x-cx)*.85, cy+(y-cy)*.87, depth-thickness*(1.02 if i%3==0 else .93))))
    cap = len(vertices)
    vertices.append(point((cx+2, cy-3, sum(p[2] for p in boundary)/n-thickness*1.17)))
    polygons = [f for f, index in faces]
    indices = [index for f, index in faces]
    for i in range(n):
        j = (i+1)%n
        polygons.append([i,j,back+j,back+i])
        indices.append(side_material)
        polygons.append([back+i,back+j,cap])
        indices.append(side_material if i//3%2==0 else 3)
    return mesh_object(name, vertices, polygons, indices)

# 修正用户指出的左下块：连贯灰青主断面、低位顶沿与真实后体，不保留凹碗状空顶。
rock_volume('左下_完整破断岩',
    [(12,111,.49),(17,101,.35),(31,96,.38),(45,98,.39),(54,108,.48),
     (60,126,.39),(51,144,.41),(32,149,.31),(19,138,.39)],
    [(21,115,.61),(43,110,.63),(50,127,.64),(33,138,.60),(35,102,.58)],
    [([1,2,13,9],3),([2,3,4,10,13],3),([13,10,9],7),([9,10,11,12],7),
     ([0,1,9],2),([0,9,12,8],7),([8,12,7],2),([7,12,11,6],2),([6,11,10,4,5],7)],
    .91, 7)

# 修正右侧块：完整黄褐外缘、断面折转与灰褐内面，不能只剩一条平面边带。
rock_volume('右侧_完整黄褐边岩',
    [(131,81,.02),(148,67,-.12),(163,66,-.11),(169,72,-.10),(168,91,.07),
     (162,111,.18),(156,137,.19),(140,142,.25),(126,126,.30),(132,111,.39)],
    [(149,82,.28),(158,92,.29),(152,112,.45),(146,129,.46),(137,102,.38),(166,95,.12),(162,97,.20)],
    [([0,1,10,14],4),([1,2,3,10],4),([3,4,15,16,11,10],0),
     ([4,5,15],0),([11,16,15,5,6,12],0),([10,11,12,13,14],3),([6,7,13,12],1),
     ([7,8,9,14,13],3),([9,0,14],4)], .88, 1)

# 内核覆盖整个岩组支承体，并拥有分区的后肩、侧腹、背部隆起。
# 表面浅裂隙仍然存在，但不可从正面裂隙直接看到背景。
outline = [(45,79,.02),(53,59,-.15),(64,37,-.25),(96,26,-.25),(125,32,-.30),
           (142,71,-.18),(164,70,-.10),(166,102,-.05),(154,136,.04),(125,151,.15),
           (98,150,.10),(73,143,.15),(36,142,.12),(20,132,.05),(19,110,.10),(34,93,.08)]
n = len(outline)
vertices = [point(p) for p in outline]
for i, (x,y,depth) in enumerate(outline):
    vertices.append(point((91+(x-91)*.94, 91+(y-91)*.96, -.65-.08*(i%3))))
for i, (x,y,depth) in enumerate(outline):
    vertices.append(point((91+(x-91)*.82, 91+(y-91)*.88, -1.10-.07*(i%3))))
front_center = len(vertices)
vertices.append(point((91,103,.33)))
u, v, w = len(vertices), len(vertices)+1, len(vertices)+2
vertices += [point((80,57,-1.30)),point((112,106,-1.39)),point((60,118,-1.29))]
faces, indices = [], []
for i in range(n):
    j=(i+1)%n
    faces.append([i,j,front_center]);indices.append(2 if i<6 or i>11 else 3)
    faces.append([i,j,n+j,n+i]);indices.append(2 if i<6 else 3 if i<11 else 7)
    faces.append([n+i,n+j,2*n+j,2*n+i]);indices.append(3 if i<5 else 1 if i<9 else 7)
back_faces = [([0,1,2,3,4,5],u,2),([5,6,7,8],v,3),([8,9,10],v,3),
              ([10,11,12,13],w,7),([13,14,15,0],w,3)]
for arc, ridge, material in back_faces:
    faces.append([2*n+i for i in arc]+[ridge]);indices.append(material)
faces += [[2*n+5,v,u],[2*n+8,v],[2*n+10,w,v],[2*n+13,w],[2*n,u,w],[u,v,w]]
indices += [3,3,2,7,3,2]
# 上面两个二点占位不是面；它们由邻接区域共用同一棱，不生成退化三角面。
valid = [(f,index) for f,index in zip(faces,indices) if len(f)>=3]
core = mesh_object('内部_连续实体基岩',vertices,[f for f,index in valid],[index for f,index in valid])

def union_parts():
    result = core.copy()
    result.data = core.data.copy()
    game_collection.objects.link(result)
    result.parent = root
    result.name = '固源岩组_连续完整实体'
    bpy.context.view_layer.objects.active = result
    for obj in scene.objects:
        obj.select_set(obj == result)
    for part in [o for o in parts if o != core]:
        mod=result.modifiers.new('实体衔接_'+part.name,'BOOLEAN')
        mod.operation='UNION'
        mod.solver='EXACT'
        mod.material_mode='TRANSFER'
        mod.object=part
        bpy.ops.object.modifier_apply(modifier=mod.name)
    bm=bmesh.new();bm.from_mesh(result.data)
    bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.00001)
    bmesh.ops.dissolve_degenerate(bm,dist=.00001,edges=list(bm.edges))
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bmesh.ops.triangulate(bm,faces=list(bm.faces))
    bm.to_mesh(result.data);bm.free()
    for face in result.data.polygons:
        face.use_smooth=False
    return result

solid = union_parts()

def rear_fault(x, y, direction, length, width, depth):
    # 从背侧真实表面定位浅断缝，只雕刻表皮，不切穿岩体。
    hit, location, normal, index = solid.ray_cast(point((x,y,-3)),front)
    if not hit:
        return
    axis = (right*direction[0]+up*direction[1]).normalized()
    axis = (axis-normal*axis.dot(normal)).normalized()
    cross = normal.cross(axis).normalized()
    center = location-normal*depth*.25
    local = [(-length*.5,-width*.25,-depth),(-length*.1,-width*.55,-depth),
             (length*.5,-width*.3,-depth),(length*.5,width*.2,-depth),
             (length*.1,width*.45,-depth),(-length*.5,width*.2,-depth)]
    coords = [center+axis*a+cross*b+normal*c for a,b,c in local]
    coords += [p+normal*depth*2.3 for p in coords]
    faces = [list(range(6)),list(range(6,12))]
    faces += [[i,(i+1)%6,6+(i+1)%6,6+i] for i in range(6)]
    mesh=bpy.data.meshes.new('背侧浅断缝_刀具网格')
    mesh.from_pydata(coords,[],faces)
    mesh.update()
    for mat in materials:
        mesh.materials.append(mat)
    for face in mesh.polygons:
        face.material_index=6
    bm=bmesh.new();bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bm.to_mesh(mesh);bm.free()
    cutter=bpy.data.objects.new('背侧浅断缝_可编辑刀具',mesh)
    parts_collection.objects.link(cutter)
    cutter.display_type='WIRE'
    cutter.hide_render=True
    mod=solid.modifiers.new('背侧浅断面','BOOLEAN')
    mod.operation='DIFFERENCE';mod.solver='EXACT';mod.material_mode='TRANSFER';mod.object=cutter
    bpy.context.view_layer.objects.active=solid
    bpy.ops.object.modifier_apply(modifier=mod.name)

rear_fault(78,67,(1,.25),.40,.026,.026)
rear_fault(116,108,(.30,1),.43,.032,.030)
rear_fault(66,129,(1,-.20),.38,.027,.026)
parts_collection.hide_render = True
parts_collection.hide_viewport = True
solid['制作说明'] = '左下与右侧块已重做；连续实体核与可编辑分块并集，内部交叠面移除。'
root['可编辑分件'] = '展开隐藏集合“固源岩组_可编辑分块”；成品集合为实际导出网格。'

# 后部预览补光属于预览设备，永不进入 FBX 或收藏室公共灯光。
data = bpy.data.lights.new('固源岩组_背侧预览补光', 'AREA')
data.energy = 320
data.shape = 'DISK'
data.size = 4
light = bpy.data.objects.new('固源岩组_背侧预览补光', data)
preview_collection.objects.link(light)
light.location = -front*4+up*3
light.rotation_euler = (-light.location).to_track_quat('-Z','Y').to_euler()

def measure(mesh):
    bm=bmesh.new();bm.from_mesh(mesh)
    boundary=sum(e.is_boundary for e in bm.edges)
    nonmanifold=sum(not e.is_manifold for e in bm.edges)
    unseen=set(bm.verts);components=0
    while unseen:
        components+=1
        queue=[unseen.pop()]
        while queue:
            vertex=queue.pop()
            for edge in vertex.link_edges:
                other=edge.other_vert(vertex)
                if other in unseen:
                    unseen.remove(other);queue.append(other)
    result={'connected_components':components,'boundary_edges':boundary,'non_manifold_edges':nonmanifold,'volume':abs(bm.calc_volume(signed=True))}
    bm.free();mesh.calc_loop_triangles()
    result['triangles']=len(mesh.loop_triangles)
    return result

solid_measure = measure(solid.data)
(OUT/'体积检查.json').write_text(json.dumps(solid_measure,ensure_ascii=False,indent=2),encoding='utf-8')
for obj in scene.objects:
    obj.select_set(obj==solid)
bpy.context.view_layer.objects.active=solid
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(60),island_margin=.012)
bpy.ops.object.mode_set(mode='OBJECT')
job=dict(key='OrirockCluster',name='固源岩组',scene=scene,root=root,meshes=[solid],materials=list(solid.data.materials),dir=OUT)

def render_angles():
    paths={}
    for name,angle in [('正面',0),('左侧',90),('右侧',-90),('背面',180)]:
        q=Quaternion(up,math.radians(angle))
        scene.camera.location=q@camera_location
        scene.camera.rotation_euler=(q@camera_quat).to_euler()
        scene.render.filepath=str(OUT/(name+'_完整实体.png'))
        bpy.ops.render.render(write_still=True)
        paths[name]=scene.render.filepath
    scene.camera.location=camera_location
    scene.camera.rotation_euler=camera_quat.to_euler()
    scene.render.filepath=paths['正面']
    return paths

def finish():
    try:
        # 不以“各分件封口”冒充完整体积；本轮需要真正相连的实体外表面。
        if solid_measure['connected_components'] != 1 or solid_measure['non_manifold_edges']:
            status('实体衔接仍需修正',geometry=solid_measure)
            return None
        result=bake_job(job)
        renders=render_angles()
        bpy.data.libraries.write(str(OUT/'OrirockCluster_Finished.blend'),{scene},fake_user=True)
        info=dict(key='OrirockCluster',item_id='04',blend=str(OUT/'OrirockCluster_Finished.blend'),
                  fbx=str(OUT/'OrirockCluster_Finished.fbx'),geometry=solid_measure,editable_parts=len(parts),
                  textures={kind:str(OUT/'textures'/('OrirockCluster_'+kind+'.png')) for kind in ['BaseColor','Normal','Occlusion','MetallicSmoothness']},
                  renders=renders,camera_position=list(camera_location),
                  changes=['左下碎岩与右侧岩块完整重做','连续实体体积替代贯通空隙','侧背隆起与完整断面','保留可编辑分块和此前阶段'],
                  limitations='背侧设计为单图推定，但以真实完整体积制作，不是正面遮挡片。')
        (OUT/'revision_info.json').write_text(json.dumps(info,ensure_ascii=False,indent=2),encoding='utf-8')
        for area in bpy.context.screen.areas:
            if area.type=='VIEW_3D':
                space=area.spaces.active
                space.shading.type='MATERIAL'
                space.overlay.show_overlays=False
                space.region_3d.view_rotation=camera_quat
                space.region_3d.view_location=front*(-.15)
                space.region_3d.view_distance=4.7
                space.region_3d.view_perspective='ORTHO'
        status('完成',model=result,geometry=solid_measure,renders=renders)
    except Exception:
        status('失败',error=traceback.format_exc())
    return None

status('完整实体已建立，检查连接并准备烘焙',geometry=solid_measure)
bpy.app.timers.register(finish,first_interval=1)
print(json.dumps({'scene':scene.name,'geometry':solid_measure,'editable_parts':len(parts)},ensure_ascii=False))
