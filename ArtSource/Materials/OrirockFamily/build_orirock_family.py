"""在独立场景制作固源岩、固源岩组和提纯源岩；不覆盖已完成模型和收藏室场景。"""
import bpy
import bmesh
import math
import json
import random
import itertools
import traceback
from pathlib import Path
from mathutils import Vector, Matrix

OUT = Path(__file__).resolve().parent
PROJECT = OUT.parents[2]
STATUS = OUT / '制作状态.json'
SIZE = 1024
jobs = []

def status(stage, **extra):
    STATUS.write_text(json.dumps({'stage': stage, **extra}, ensure_ascii=False, indent=2), encoding='utf-8')

def linear(v):
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4

def color(rgb):
    return tuple(linear(v / 255) for v in rgb) + (1,)

def stone_material(prefix, name, rgb, roughness, fine):
    mat = bpy.data.materials.new(prefix + '_' + name)
    mat.use_nodes = True
    mat.diffuse_color = color(rgb)
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = next(n for n in nodes if n.type == 'BSDF_PRINCIPLED')
    bsdf.inputs['Metallic'].default_value = 0
    bsdf.inputs['Roughness'].default_value = roughness
    coord = nodes.new('ShaderNodeTexCoord')
    broad = nodes.new('ShaderNodeTexNoise')
    broad.inputs['Scale'].default_value = 9
    broad.inputs['Detail'].default_value = 4
    broad.inputs['Roughness'].default_value = 0.7
    links.new(coord.outputs['Generated'], broad.inputs['Vector'])
    ramp = nodes.new('ShaderNodeValToRGB')
    ramp.color_ramp.elements[0].position = 0.18
    ramp.color_ramp.elements[0].color = color(tuple(int(v * 0.82) for v in rgb))
    ramp.color_ramp.elements[1].position = 0.84
    ramp.color_ramp.elements[1].color = color(tuple(min(255, int(v * 1.12)) for v in rgb))
    links.new(broad.outputs['Fac'], ramp.inputs['Fac'])
    grain = nodes.new('ShaderNodeTexNoise')
    grain.inputs['Scale'].default_value = 160
    grain.inputs['Detail'].default_value = 2
    links.new(coord.outputs['Generated'], grain.inputs['Vector'])
    # 微孔与颗粒由程序纹理表达，主轮廓保持图标中的大块断裂面。
    pores = nodes.new('ShaderNodeTexVoronoi')
    pores.inputs['Scale'].default_value = 95
    links.new(coord.outputs['Generated'], pores.inputs['Vector'])
    pore_ramp = nodes.new('ShaderNodeValToRGB')
    pore_ramp.color_ramp.elements[0].position = 0.06
    pore_ramp.color_ramp.elements[0].color = (0.16, 0.16, 0.16, 1)
    pore_ramp.color_ramp.elements[1].position = 0.20
    pore_ramp.color_ramp.elements[1].color = (1, 1, 1, 1)
    links.new(pores.outputs['Distance'], pore_ramp.inputs['Fac'])
    mix = nodes.new('ShaderNodeMixRGB')
    mix.blend_type = 'MULTIPLY'
    mix.inputs[0].default_value = 0.20 if fine else 0.30
    links.new(ramp.outputs['Color'], mix.inputs[1])
    links.new(pore_ramp.outputs['Color'], mix.inputs[2])
    links.new(mix.outputs[0], bsdf.inputs['Base Color'])
    bump = nodes.new('ShaderNodeBump')
    bump.inputs['Strength'].default_value = 0.16 if fine else 0.30
    bump.inputs['Distance'].default_value = 0.004 if fine else 0.010
    links.new(grain.outputs['Fac'], bump.inputs['Height'])
    links.new(bump.outputs[0], bsdf.inputs['Normal'])
    rough = nodes.new('ShaderNodeMapRange')
    rough.inputs['To Min'].default_value = roughness - 0.045
    rough.inputs['To Max'].default_value = min(1, roughness + 0.06)
    links.new(grain.outputs['Fac'], rough.inputs['Value'])
    links.new(rough.outputs[0], bsdf.inputs['Roughness'])
    return mat

def convex_mesh(name, planes):
    """半空间交集形成封闭石块，天然切面不依赖不稳定的布尔修改器。"""
    vertices = []
    for selected in itertools.combinations(planes, 3):
        a = Matrix([p[0] for p in selected])
        if abs(a.determinant()) < 1e-5:
            continue
        v = a.inverted() @ Vector([p[1] for p in selected])
        if all(Vector(n).dot(v) <= d + 1e-4 for n, d in planes):
            if not any((v - old).length < 1e-4 for old in vertices):
                vertices.append(v)
    bm = bmesh.new()
    for v in vertices:
        bm.verts.new(v)
    hull = bmesh.ops.convex_hull(bm, input=list(bm.verts))
    unused = [v for v in hull.get('geom_unused', []) if isinstance(v, bmesh.types.BMVert)]
    if unused:
        bmesh.ops.delete(bm, geom=unused, context='VERTS')
    bmesh.ops.dissolve_limit(bm, angle_limit=0.002, verts=list(bm.verts), edges=list(bm.edges))
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    mesh.update()
    return mesh

def rock(job, name, position, scale, seed, cut=0.32, rotation=(0, 0, 0), wedge=False):
    rnd = random.Random(seed)
    planes = [((1,0,0),1),((-1,0,0),1),((0,1,0),1),((0,-1,0),1),((0,0,1),1),((0,0,-1),1)]
    for signs in itertools.product([-1,1], repeat=3):
        normal = tuple(signs[i] * rnd.uniform(0.87,1.13) for i in range(3))
        planes.append((normal, sum(abs(v) for v in normal) - rnd.uniform(cut * 0.55, cut * 1.4)))
    if wedge:
        planes.extend([((1,0,0.8),1.30),((-1,0,0.75),1.1),((0,-1,0.65),1.18),((0.2,1,0.8),1.25)])
    mesh = convex_mesh(name + '_网格', planes)
    obj = bpy.data.objects.new(name, mesh)
    job['geometry'].objects.link(obj)
    obj.parent = job['root']
    obj.location = position
    obj.scale = scale
    obj.rotation_euler = tuple(math.radians(v) for v in rotation)
    for mat in job['materials']:
        mesh.materials.append(mat)
    # 黄褐矿质与灰蓝岩质是材质分区，并非把图标的阴影或背景烘焙入颜色。
    for poly in mesh.polygons:
        n = poly.normal
        poly.material_index = 0 if n.x > 0.50 else 1 if n.z > 0.48 else 2 if n.y < -0.50 else 3
    finish_mesh(job, obj, 0.009 if job['key'] == 'OrirockConcentration' else 0.014)
    return obj

def hex_column(job, name, position, radius, height, rotation):
    planes = []
    for i in range(6):
        a = math.radians(30 + i * 60)
        planes.append(((math.cos(a), math.sin(a), 0), math.cos(math.pi/6)))
    planes.extend([((0,0,1),0.5),((0,0,-1),0.5)])
    mesh = convex_mesh(name + '_网格', planes)
    obj = bpy.data.objects.new(name, mesh)
    job['geometry'].objects.link(obj)
    obj.parent = job['root']
    obj.location = position
    obj.scale = (radius, radius, height)
    obj.rotation_euler = tuple(math.radians(v) for v in rotation)
    for mat in job['materials']:
        mesh.materials.append(mat)
    for poly in mesh.polygons:
        n = poly.normal
        poly.material_index = 0 if n.z > 0.75 else 1 if n.x > 0.25 else 3 if n.y > 0 else 2
    finish_mesh(job, obj, 0.018)
    return obj

def finish_mesh(job, obj, width):
    bpy.context.view_layer.objects.active = obj
    for o in bpy.context.selected_objects:
        o.select_set(False)
    obj.select_set(True)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    bevel = obj.modifiers.new('断面边缘', 'BEVEL')
    bevel.width = width
    bevel.segments = 2
    bpy.ops.object.modifier_apply(modifier=bevel.name)
    # 大断面保持平整；倒角单独平滑，避免天然石块变成软圆球。
    for face in obj.data.polygons:
        face.use_smooth = face.area < 0.015
    job['meshes'].append(obj)
    obj.select_set(False)

def preview_setup(job):
    scene = job['scene']
    camera_data = bpy.data.cameras.new(job['key'] + '_预览相机')
    camera = bpy.data.objects.new(job['name'] + '_预览相机', camera_data)
    job['preview'].objects.link(camera)
    camera.location = (3.8,-6.2,4.0)
    camera.rotation_euler = (-camera.location).to_track_quat('-Z','Y').to_euler()
    camera_data.type = 'ORTHO'
    camera_data.ortho_scale = 3.35
    scene.camera = camera
    for name, location, energy, size in [('主光',(3,-4,6),650,5),('补光',(-4,-2,3),280,4),('轮廓光',(1,4,4),420,3)]:
        data = bpy.data.lights.new(job['key'] + '_' + name, 'AREA')
        data.energy = energy
        data.shape = 'DISK'
        data.size = size
        obj = bpy.data.objects.new(job['name'] + '_' + name, data)
        job['preview'].objects.link(obj)
        obj.location = location
        obj.rotation_euler = (-obj.location).to_track_quat('-Z','Y').to_euler()
    world = bpy.data.worlds.new(job['name'] + '_中性预览环境')
    world.use_nodes = True
    bg = next(n for n in world.node_tree.nodes if n.type == 'BACKGROUND')
    bg.inputs['Color'].default_value = (0.10,0.12,0.15,1)
    bg.inputs['Strength'].default_value = 0.45
    scene.world = world
    scene.render.engine = 'BLENDER_EEVEE'
    scene.view_settings.view_transform = 'AgX'
    scene.render.resolution_x = scene.render.resolution_y = 1000
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = 'PNG'
    scene.render.film_transparent = True
    scene.render.filepath = str(job['dir'] / (job['key'] + '_Preview.png'))

def create_job(key, name, level):
    scene = bpy.data.scenes.new(name + '_材质版')
    bpy.context.window.scene = scene
    scene.unit_settings.system = 'METRIC'
    geometry = bpy.data.collections.new(name + '_模型')
    preview = bpy.data.collections.new(name + '_预览_不导出')
    scene.collection.children.link(geometry)
    scene.collection.children.link(preview)
    root = bpy.data.objects.new(key + '_Root', None)
    geometry.objects.link(root)
    root['参考'] = 'https://prts.wiki/w/' + name
    root['说明'] = '按单视图图标自制，背面推定；圆环不是模型分件。'
    root['本体等级'] = level
    directory = OUT / key
    directory.mkdir(exist_ok=True)
    (directory / 'textures').mkdir(exist_ok=True)
    palette = [(183,166,119),(135,128,93),(68,81,85),(95,98,77)]
    if key == 'OrirockConcentration':
        palette = [(197,173,122),(140,133,94),(99,103,77),(121,121,87)]
    materials = [stone_material(key, label, rgb, 0.68 if level==4 else 0.82, level==4)
        for label,rgb in zip(['黄褐矿质','橄榄岩质','灰蓝岩质','暗色断面'],palette)]
    job = dict(key=key,name=name,level=level,scene=scene,geometry=geometry,preview=preview,
        root=root,meshes=[],materials=materials,dir=directory)
    jobs.append(job)
    return job

j = create_job('OrirockCube','固源岩',2)
rock(j,'固块_完整主体',(0,0,0.03),(0.71,0.65,0.73),44,0.38,(2,-5,-7))
rock(j,'左下崩裂碎角',(-0.60,-0.46,-0.65),(0.19,0.22,0.16),45,0.65,(15,10,-20),True)
rock(j,'底部碎片',(0.39,-0.44,-0.68),(0.15,0.20,0.10),46,0.75,(-2,8,21),True)

j = create_job('OrirockCluster','固源岩组',3)
rock(j,'后部高岩',(-0.13,0.27,0.33),(0.56,0.46,0.60),61,0.44,(4,-15,-9))
rock(j,'右侧岩块',(0.53,0.04,-0.15),(0.44,0.47,0.48),62,0.52,(8,-13,16))
rock(j,'左前岩块',(-0.57,-0.34,-0.30),(0.49,0.39,0.37),63,0.40,(-4,15,-15))
rock(j,'中间斜向断片',(-0.06,-0.43,-0.11),(0.24,0.22,0.55),64,0.58,(8,-22,7))
rock(j,'前部基岩',(0.35,-0.38,-0.48),(0.39,0.30,0.20),65,0.64,(6,9,18),True)

j = create_job('OrirockConcentration','提纯源岩',4)
hex_column(j,'提纯柱_后左',(-0.47,0.20,0.21),0.62,0.96,(-4,-5,0))
hex_column(j,'提纯柱_后右',(0.53,0.26,0.05),0.61,0.93,(3,5,6))
hex_column(j,'提纯柱_前方',(-0.12,-0.67,-0.29),0.62,0.89,(0,-4,-5))

for job in jobs:
    scene = job['scene']
    bpy.context.window.scene = scene
    # 将重心和整体包围盒统一到模型根；展示尺寸仍由 Unity 独立预制体决定。
    bpy.context.view_layer.update()
    points = [o.matrix_world @ Vector(c) for o in job['meshes'] for c in o.bound_box]
    low = Vector([min(p[i] for p in points) for i in range(3)])
    high = Vector([max(p[i] for p in points) for i in range(3)])
    center = (low + high) / 2
    for obj in job['meshes']:
        obj.location -= center
        obj.select_set(True)
    bpy.context.view_layer.objects.active = job['meshes'][0]
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(60),island_margin=0.018)
    bpy.ops.object.mode_set(mode='OBJECT')
    preview_setup(job)
    scene['说明'] = '独立源岩系模型；预览设备不进入游戏 FBX。背面和厚度为单图推定。'
    bpy.data.libraries.write(str(job['dir'] / (job['key'] + '_Blockout.blend')),{scene},fake_user=True)

status('四种造型已完成，准备烘焙',materials=[j['name'] for j in jobs])

def bake_job(job):
    scene = job['scene']
    bpy.context.window.scene = scene
    # 天然断面可能是非共面多边形；明确三角化后再烘焙，避免 FBX/Unity 各自剖分产生差异。
    import bmesh
    for obj in job['meshes']:
        bm=bmesh.new()
        bm.from_mesh(obj.data)
        bmesh.ops.triangulate(bm,faces=list(bm.faces))
        bm.to_mesh(obj.data)
        bm.free()
        obj.data.update()
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = 12
    scene.cycles.bake_type = 'EMIT'
    targets = {}
    for mat in job['materials']:
        target = mat.node_tree.nodes.new('ShaderNodeTexImage')
        target.label = '游戏贴图烘焙目标（程序源材质仍保留）'
        targets[mat] = target
    for o in scene.objects:
        o.select_set(o in job['meshes'])
    bpy.context.view_layer.objects.active = job['meshes'][0]
    images = {}
    def bake(name,kind,space='Non-Color'):
        status(job['name'] + '：烘焙 ' + name)
        image = bpy.data.images.new(job['key'] + '_' + name,width=SIZE,height=SIZE,alpha=True)
        image.colorspace_settings.name = space
        for mat,target in targets.items():
            target.image = image
            mat.node_tree.nodes.active = target
        bpy.ops.object.bake(type=kind,margin=8)
        image.filepath_raw = str(job['dir'] / 'textures' / (job['key'] + '_' + name + '.png'))
        image.file_format = 'PNG'
        image.save()
        images[name] = image
        return image
    def emission_color():
        for mat in job['materials']:
            nodes,links = mat.node_tree.nodes,mat.node_tree.links
            bsdf = next(n for n in nodes if n.type=='BSDF_PRINCIPLED')
            output = next(n for n in nodes if n.type=='OUTPUT_MATERIAL')
            emit = nodes.new('ShaderNodeEmission')
            links.new(bsdf.inputs['Base Color'].links[0].from_socket,emit.inputs['Color'])
            links.new(emit.outputs[0],output.inputs['Surface'])
    def restore():
        for mat in job['materials']:
            nodes,links = mat.node_tree.nodes,mat.node_tree.links
            links.new(next(n for n in nodes if n.type=='BSDF_PRINCIPLED').outputs[0],next(n for n in nodes if n.type=='OUTPUT_MATERIAL').inputs['Surface'])
    emission_color()
    bake('BaseColor','EMIT','sRGB')
    restore()
    bake('Normal','NORMAL')
    scene.world.color = (1,1,1)
    bake('Occlusion','AO')
    rough = bake('Roughness','ROUGHNESS')
    import numpy as np
    values = np.empty(SIZE * SIZE * 4,dtype=np.float32)
    rough.pixels.foreach_get(values)
    mask_values = np.zeros_like(values).reshape((-1,4))
    mask_values[:,3] = 1 - values.reshape((-1,4))[:,0]
    mask = bpy.data.images.new(job['key']+'_MetallicSmoothness',width=SIZE,height=SIZE,alpha=True)
    mask.colorspace_settings.name = 'Non-Color'
    mask.pixels.foreach_set(mask_values.reshape(-1))
    mask.filepath_raw = str(job['dir']/'textures'/(job['key']+'_MetallicSmoothness.png'))
    mask.file_format='PNG'
    mask.save()
    images['MetallicSmoothness']=mask
    for image in images.values():
        image.pack()
    for obj in scene.objects:
        obj.select_set(obj==job['root'] or obj in job['meshes'])
    bpy.context.view_layer.objects.active = job['root']
    bpy.ops.export_scene.fbx(filepath=str(job['dir']/(job['key']+'_Finished.fbx')),
        use_selection=True,object_types={'EMPTY','MESH'},axis_forward='-Z',axis_up='Y',
        apply_unit_scale=True,use_mesh_modifiers=True,mesh_smooth_type='FACE',add_leaf_bones=False,
        bake_anim=False,path_mode='STRIP')
    scene.render.engine='BLENDER_EEVEE'
    bpy.data.libraries.write(str(job['dir']/(job['key']+'_Finished.blend')),{scene},fake_user=True)
    bpy.ops.render.render(write_still=True)
    for obj in job['meshes']:
        obj.data.calc_loop_triangles()
    return dict(name=job['name'],key=job['key'],parts=len(job['meshes']),
        triangles=sum(len(o.data.loop_triangles) for o in job['meshes']))

def run():
    try:
        result=[]
        for job in jobs:
            result.append(bake_job(job))
        for area in bpy.context.screen.areas:
            if area.type=='VIEW_3D':
                space=area.spaces.active
                space.shading.type='MATERIAL'
                space.overlay.show_overlays=False
                space.region_3d.view_rotation=jobs[-1]['scene'].camera.rotation_euler.to_quaternion()
                space.region_3d.view_distance=4.7
                space.region_3d.view_location=(0,0,0)
                space.region_3d.view_perspective='ORTHO'
        status('完成',models=result)
    except Exception:
        status('失败',error=traceback.format_exc())
    return None

bpy.app.timers.register(run,first_interval=1)
print('已创建四个独立场景，异步烘焙并导出。原 D32钢文件未保存或覆盖。')
