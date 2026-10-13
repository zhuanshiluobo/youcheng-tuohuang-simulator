"""提纯源岩独立正面修订；只写本脚本所在目录，不访问活动 Blender。"""
import bpy
import bmesh
import json
import math
import sys
from pathlib import Path
from mathutils import Vector, Matrix

OUT = Path(__file__).resolve().parent
KEY = 'OrirockConcentration'
FINAL = '--final' in sys.argv
SIZE = 1024
PX = 0.0125
CAMERA_POSITION = Vector((0.7, -6.8, 8.4))
CAMERA_ROTATION = (-CAMERA_POSITION).to_track_quat('-Z', 'Y')
RIGHT = CAMERA_ROTATION @ Vector((1, 0, 0))
UP = CAMERA_ROTATION @ Vector((0, 1, 0))
HORIZONTAL = Matrix(((RIGHT.x, RIGHT.y), (UP.x, UP.y))).inverted()


def progress(stage, **more):
    print('提纯源岩：' + stage, flush=True)
    (OUT / '制作状态.json').write_text(json.dumps(dict(stage=stage, **more), ensure_ascii=False, indent=2), encoding='utf-8')


def linear(v):
    v /= 255.0
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4


def rgba(rgb):
    return tuple(linear(v) for v in rgb) + (1,)


def pixel_to_world(pixel, z):
    """图标顶面反投影到真实水平平面；模型有完整高度、背面及底面。"""
    xy = HORIZONTAL @ Vector(((pixel[0] - 91.5) * PX, (91.5 - pixel[1]) * PX - UP.z * z))
    return Vector((xy.x, xy.y, z))


def stone_material(name, rgb, roughness, grain_strength=0.16):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    mat.diffuse_color = rgba(rgb)
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    principled = next(n for n in nodes if n.type == 'BSDF_PRINCIPLED')
    principled.inputs['Metallic'].default_value = 0
    principled.inputs['Roughness'].default_value = roughness
    coord = nodes.new('ShaderNodeTexCoord')
    broad = nodes.new('ShaderNodeTexNoise')
    broad.label = '低对比矿质色差；不制造随机材质三角斑块'
    broad.inputs['Scale'].default_value = 28
    broad.inputs['Detail'].default_value = 3.0
    broad.inputs['Roughness'].default_value = 0.63
    links.new(coord.outputs['Object'], broad.inputs['Vector'])
    ramp = nodes.new('ShaderNodeValToRGB')
    ramp.color_ramp.elements[0].position = 0.28
    ramp.color_ramp.elements[0].color = rgba(tuple(int(v * 0.78) for v in rgb))
    ramp.color_ramp.elements[1].position = 0.72
    ramp.color_ramp.elements[1].color = rgba(tuple(min(255, int(v * 1.13)) for v in rgb))
    links.new(broad.outputs['Fac'], ramp.inputs[0])
    grain = nodes.new('ShaderNodeTexNoise')
    grain.label = '细密天然颗粒；主断面保持平整'
    grain.inputs['Scale'].default_value = 165
    grain.inputs['Detail'].default_value = 2
    grain.inputs['Roughness'].default_value = 0.72
    links.new(coord.outputs['Object'], grain.inputs['Vector'])
    grain_ramp = nodes.new('ShaderNodeValToRGB')
    grain_ramp.color_ramp.elements[0].position = 0.30
    grain_ramp.color_ramp.elements[0].color = (0.25, 0.25, 0.25, 1)
    grain_ramp.color_ramp.elements[1].position = 0.70
    grain_ramp.color_ramp.elements[1].color = (1, 1, 1, 1)
    links.new(grain.outputs['Fac'], grain_ramp.inputs[0])
    mix = nodes.new('ShaderNodeMixRGB')
    mix.blend_type = 'MULTIPLY'
    mix.inputs[0].default_value = 0.50
    links.new(ramp.outputs['Color'], mix.inputs[1])
    links.new(grain_ramp.outputs['Color'], mix.inputs[2])
    links.new(mix.outputs[0], principled.inputs['Base Color'])
    pores = nodes.new('ShaderNodeTexVoronoi')
    pores.inputs['Scale'].default_value = 155
    links.new(coord.outputs['Object'], pores.inputs['Vector'])
    pore_ramp = nodes.new('ShaderNodeValToRGB')
    pore_ramp.color_ramp.elements[0].position = 0.035
    pore_ramp.color_ramp.elements[0].color = (0.13, 0.13, 0.13, 1)
    pore_ramp.color_ramp.elements[1].position = 0.17
    pore_ramp.color_ramp.elements[1].color = (0.6, 0.6, 0.6, 1)
    links.new(pores.outputs['Distance'], pore_ramp.inputs[0])
    detail = nodes.new('ShaderNodeMixRGB')
    detail.blend_type = 'MULTIPLY'
    detail.inputs[0].default_value = 0.18
    links.new(grain.outputs['Fac'], detail.inputs[1])
    links.new(pore_ramp.outputs['Color'], detail.inputs[2])
    bump = nodes.new('ShaderNodeBump')
    bump.inputs['Strength'].default_value = grain_strength * 1.55
    bump.inputs['Distance'].default_value = 0.0035
    links.new(detail.outputs[0], bump.inputs['Height'])
    links.new(bump.outputs[0], principled.inputs['Normal'])
    rough = nodes.new('ShaderNodeMapRange')
    rough.inputs['To Min'].default_value = roughness - 0.04
    rough.inputs['To Max'].default_value = min(1, roughness + 0.055)
    links.new(grain.outputs['Fac'], rough.inputs['Value'])
    links.new(rough.outputs[0], principled.inputs['Roughness'])
    return mat


def lerp(a, b, t):
    return tuple(a[i] * (1 - t) + b[i] * t for i in range(2))


SPECS = [
    dict(name='提纯源岩_左后_宽顶短壁', top=[(17.5,40),(39.5,10.5),(84,7),(113.5,32.5),(97,62.5),(45.5,67)],
         bottom=[(20,66),(42,36),(85,32),(110,58),(94,86),(46.5,91.5)], z=0.50, height=0.51,
         rgb=(195,168,121), front_rgb=(96,84,51), ridge=[0.47,0.54,0.57,0.62,0.41,0.65], inset=[0.012,0.008,0.012,0.006,0.026,0.021]),
    dict(name='提纯源岩_右后_斜切与下阶', top=[(112,35),(130.5,34.5),(164,58.5),(153,92.5),(99.5,97),(80.5,79)],
         bottom=[(115,73),(130,71),(156,96),(143.5,134),(110.5,137),(88,116)], z=0.15, height=0.77,
         rgb=(203,171,122), front_rgb=(106,90,57), ridge=[0.57,0.62,0.70,0.56,0.65,0.49], inset=[0.009,0.005,0.009,0.017,0.028,0.020]),
    dict(name='提纯源岩_前方_宽扁断块', top=[(39.5,84),(81.5,82.5),(112,110.5),(100.5,137.5),(47.5,142),(20.5,109)],
         bottom=[(43,107),(82,106),(109,135),(99.5,164),(52,165),(29.5,134.5)], z=-0.18, height=0.48,
         rgb=(133,110,72), front_rgb=(68,65,39), ridge=[0.47,0.60,0.49,0.55,0.40,0.72], inset=[0.012,0.008,0.023,0.029,0.014,0.008]),
]


def build_stone(spec, collection, root, materials):
    # 18-point outlines retain the six principal corners, with just two supporting
    # points on each broad edge. Their matching bands form one watertight shell.
    samples = [(edge, t) for edge in range(6) for t in (0.0, 0.34, 0.72)]
    tops, bottoms, ridges, insets = [], [], [], []
    for edge, t in samples:
        j = (edge + 1) % 6
        top = lerp(spec['top'][edge], spec['top'][j], t)
        # Deliberate small fracture steps, chiefly on the lower front rim.
        if edge == 4 and t == 0.34:
            top = (top[0], top[1] - 0.65)
        if edge == 3 and t == 0.72:
            top = (top[0] - 0.45, top[1] - 0.35)
        tops.append(Vector(top))
        bottoms.append(Vector(lerp(spec['bottom'][edge], spec['bottom'][j], t)))
        ridge = spec['ridge'][edge] * (1-t) + spec['ridge'][j] * t
        # Shared fractured ridge follows a tooth-shaped boundary, rather than
        # repeating a straight shelf around a regular hexagonal column.
        if edge in (3,4,5) and t == 0.34:
            ridge += 0.13 if edge != 5 else -0.10
        elif edge in (3,4,5) and t == 0.72:
            ridge -= 0.11 if edge != 3 else 0.04
        ridges.append(ridge)
        insets.append(spec['inset'][edge] * (1-t) + spec['inset'][j] * t)
    count = len(tops)
    center = sum(tops, Vector((0,0))) / count
    verts, faces, indices = [], [], []
    # Small edge break, broad upper panel, recessed continuous fracture shelf,
    # lower prism panel and uneven heel. Heights vary along each shared edge.
    for band in range(6):
        for i, (a, b) in enumerate(zip(tops, bottoms)):
            f = (0, 0.035, ridges[i], ridges[i]+0.09, 0.96, 1)[band]
            p = a.lerp(b, f)
            outward = (a-center).normalized()
            offset = (0, 0.35, 0.15, -insets[i]/PX, -0.2, -0.65)[band]
            p += outward * offset
            z = spec['z'] - spec['height'] * f
            verts.append(tuple(pixel_to_world(p, z)))
    faces.append(tuple(range(count-1, -1, -1)))
    indices.append(0)
    for band in range(5):
        for i in range(count):
            j = (i+1) % count
            faces.append((band*count+i, band*count+j, (band+1)*count+j, (band+1)*count+i))
            edge, t = samples[i]
            # Spatially continuous mineral zones, never random face materials.
            if band == 0:
                idx = 1
            elif spec is SPECS[0] and edge == 5:
                idx = 4 if band >= 2 else 3
            elif spec is SPECS[0] and edge == 4:
                idx = 2
            elif spec is SPECS[1] and edge == 3:
                idx = 4 if band == 3 and t >= 0.34 else 2
            elif spec is SPECS[1] and edge == 4:
                idx = 4 if band >= 2 else 3
            elif spec is SPECS[2] and edge == 3:
                idx = 2
            elif spec is SPECS[2] and edge == 4:
                idx = 4 if band >= 2 else 3
            elif edge in (2,3):
                idx = 5 if band >= 2 else 1
            else:
                idx = 2 if band < 3 else 5
            indices.append(idx)
    faces.append(tuple(range(5*count, 6*count)))
    indices.append(5)
    mesh = bpy.data.meshes.new(spec['name'] + '_闭合连续阶面')
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(spec['name'], mesh)
    collection.objects.link(obj)
    obj.parent = root
    for mat in materials:
        mesh.materials.append(mat)
    for face, idx in zip(mesh.polygons, indices):
        face.material_index = idx
        face.use_smooth = False
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bm.to_mesh(mesh)
    bm.free()
    # Fractional-pixel bevel only; keeps the traced flat top and sharp block silhouette.
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bevel = obj.modifiers.new('天然棱口微倒角', 'BEVEL')
    bevel.width = 0.003
    bevel.segments = 1
    bevel.limit_method = 'ANGLE'
    bevel.angle_limit = math.radians(26)
    bpy.ops.object.modifier_apply(modifier=bevel.name)
    obj.select_set(False)
    obj['结构'] = '六个主要顶角；单个闭合网格包含上壁、连续凹阶、下壁与底面'
    obj['背面说明'] = '背面及不可见底部根据正面短石料结构推定'
    return obj


def point_at(obj, target=(0,0,0)):
    obj.rotation_euler = (Vector(target)-obj.location).to_track_quat('-Z','Y').to_euler()


def setup_preview(scene, collection):
    data = bpy.data.cameras.new('正面参考预览相机')
    camera = bpy.data.objects.new('正面参考预览相机_不导出', data)
    collection.objects.link(camera)
    camera.location = CAMERA_POSITION
    camera.rotation_euler = CAMERA_ROTATION.to_euler()
    data.type = 'ORTHO'
    data.ortho_scale = 2.23
    scene.camera = camera
    for name, pos, energy, size in [
        ('中性主光',(0,-0.3,7),450,4.5),
        ('弱补光',(3,-4,2),40,4.5),
        ('顶部柔光',(2,4,5),130,3.0),
    ]:
        ld = bpy.data.lights.new(name,'AREA')
        ld.energy, ld.size = energy, size
        ld.shape = 'DISK'
        lamp = bpy.data.objects.new(name+'_不导出',ld)
        collection.objects.link(lamp)
        lamp.location = pos
        point_at(lamp)
    world = bpy.data.worlds.new('低强度中性环境')
    world.use_nodes = True
    bg = next(n for n in world.node_tree.nodes if n.type=='BACKGROUND')
    bg.inputs['Color'].default_value = (0.21,0.21,0.21,1)
    bg.inputs['Strength'].default_value = 0.45
    scene.world = world
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = 64 if FINAL else 32
    scene.cycles.use_denoising = False
    scene.cycles.device = 'CPU'
    scene.render.resolution_x = scene.render.resolution_y = 960
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGBA'
    scene.render.film_transparent = True
    scene.view_settings.view_transform = 'Standard'
    scene.view_settings.look = 'Medium High Contrast' if False else 'None'
    scene.view_settings.exposure = 0
    scene.view_settings.gamma = 1
    return camera


def bake_textures(scene, meshes, source_materials):
    progress('烘焙 1024 游戏贴图')
    (OUT / 'textures').mkdir(exist_ok=True)
    scene.cycles.samples = 12
    scene.render.bake.margin = 12
    scene.render.bake.use_clear = True
    scene.render.bake.use_selected_to_active = False
    scene.render.bake.normal_space = 'TANGENT'
    for o in scene.objects:
        o.select_set(o in meshes)
    bpy.context.view_layer.objects.active = meshes[0]
    targets = {}
    for mat in source_materials:
        node = mat.node_tree.nodes.new('ShaderNodeTexImage')
        node.label = '游戏图集烘焙目标；程序材质保留'
        targets[mat] = node
    result = {}
    def bake(label, kind, space):
        progress('烘焙 '+label)
        image = bpy.data.images.new(KEY+'_FrontRevision_'+label,width=SIZE,height=SIZE,alpha=True)
        image.colorspace_settings.name = space
        for mat, node in targets.items():
            node.image = image
            mat.node_tree.nodes.active = node
        bpy.ops.object.bake(type=kind, margin=12)
        result[label] = image
        return image
    for mat in source_materials:
        nodes, links = mat.node_tree.nodes, mat.node_tree.links
        bsdf = next(n for n in nodes if n.type=='BSDF_PRINCIPLED')
        output = next(n for n in nodes if n.type=='OUTPUT_MATERIAL')
        emit = nodes.new('ShaderNodeEmission')
        emit.label = '基础色烘焙临时输出'
        links.new(bsdf.inputs['Base Color'].links[0].from_socket,emit.inputs['Color'])
        links.new(emit.outputs[0],output.inputs['Surface'])
    bake('BaseColor','EMIT','sRGB')
    for mat in source_materials:
        nodes, links = mat.node_tree.nodes, mat.node_tree.links
        links.new(next(n for n in nodes if n.type=='BSDF_PRINCIPLED').outputs[0],next(n for n in nodes if n.type=='OUTPUT_MATERIAL').inputs['Surface'])
        for node in list(nodes):
            if node.type=='EMISSION':
                nodes.remove(node)
    bake('Normal','NORMAL','Non-Color')
    bake('AO','AO','Non-Color')
    rough = bake('_RoughnessWork','ROUGHNESS','Non-Color')
    import numpy as np
    values = np.empty(SIZE*SIZE*4,dtype=np.float32)
    rough.pixels.foreach_get(values)
    values = values.reshape((-1,4))
    packed = np.zeros_like(values)
    packed[:,3] = np.clip(1-values[:,0],0,1)
    metal = bpy.data.images.new(KEY+'_FrontRevision_MetallicSmoothness',width=SIZE,height=SIZE,alpha=True)
    metal.colorspace_settings.name = 'Non-Color'
    metal.pixels.foreach_set(packed.ravel())
    result['MetallicSmoothness'] = metal
    result.pop('_RoughnessWork')
    for label, image in result.items():
        image.filepath_raw = str(OUT/'textures'/(KEY+'_FrontRevision_'+label+'.png'))
        image.file_format = 'PNG'
        image.save()
        image.pack()
    # The unused roughness work target is retained in-memory only.
    for mat, node in targets.items():
        node.image = result['BaseColor']
    return result


def game_material(images):
    mat = bpy.data.materials.new(KEY+'_游戏PBR_单图集')
    mat.use_nodes = True
    mat.use_fake_user = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = next(n for n in nodes if n.type=='BSDF_PRINCIPLED')
    for label, pos in [('BaseColor',(-700,340)),('Normal',(-700,30)),('AO',(-700,-280)),('MetallicSmoothness',(-350,-280))]:
        node = nodes.new('ShaderNodeTexImage')
        node.image = images[label]
        node.label = label
        node.location = pos
        if label=='BaseColor':
            links.new(node.outputs['Color'],bsdf.inputs['Base Color'])
        elif label=='Normal':
            normal = nodes.new('ShaderNodeNormalMap')
            links.new(node.outputs['Color'],normal.inputs['Color'])
            links.new(normal.outputs['Normal'],bsdf.inputs['Normal'])
        elif label=='AO':
            node.label = 'AO：Unity 环境遮蔽输入；不乘入 BaseColor'
        else:
            split = nodes.new('ShaderNodeSeparateColor')
            links.new(node.outputs['Color'],split.inputs[0])
            links.new(split.outputs[0],bsdf.inputs['Metallic'])
            invert = nodes.new('ShaderNodeMath')
            invert.operation = 'SUBTRACT'
            invert.inputs[0].default_value = 1
            links.new(node.outputs['Alpha'],invert.inputs[1])
            links.new(invert.outputs[0],bsdf.inputs['Roughness'])
    mat['Unity贴图通道'] = 'BaseColor=sRGB；Normal/AO=Non-Color；MetallicSmoothness.R=Metallic,A=1-Roughness'
    return mat


def main():
    # Factory-startup background process only. No object/asset from the shared session.
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.name = '提纯源岩_正面修订_可编辑源'
    scene.unit_settings.system = 'METRIC'
    scene.unit_settings.scale_length = 1.0
    bpy.context.preferences.filepaths.save_version = 0
    scene['说明'] = 'PRTS 单视图短六边石料；真实闭合三维网格，非贴片；背面与厚度推定。'
    geometry = bpy.data.collections.new('提纯源岩_仅模型')
    preview = bpy.data.collections.new('预览设备_绝不导出')
    scene.collection.children.link(geometry)
    scene.collection.children.link(preview)
    root = bpy.data.objects.new(KEY+'_Root',None)
    geometry.objects.link(root)
    root['参考'] = 'ArtSource/Materials/OrirockFamily/reference/OrirockConcentration_PRTS.png'
    root['尺寸说明'] = '根缩放1；几何最大轴1.65米；较旧源约缩小22.5%，展示缩放仍由主代理决定。'
    side = [
        stone_material('断棱_褐金矿质',(148,126,80),0.75),
        stone_material('上阶_褐橄榄岩壁',(83,80,51),0.82,0.20),
        stone_material('左侧_灰蓝断面',(54,67,69),0.84,0.18),
        stone_material('下阶_灰蓝矿心',(66,76,71),0.84,0.22),
        stone_material('下部_暖褐断棱',(108,96,62),0.82,0.18),
    ]
    meshes, materials = [], side.copy()
    for spec in SPECS:
        topmat = stone_material(spec['name']+'_顶面_褐金颗粒',spec['rgb'],0.72)
        frontmat = stone_material(spec['name']+'_连续深褐断面',spec['front_rgb'],0.82,0.20)
        local_side=side.copy()
        local_side[1]=frontmat
        materials.extend((topmat,frontmat))
        meshes.append(build_stone(spec,geometry,root,[topmat]+local_side))
    bpy.context.view_layer.update()
    points = [obj.matrix_world @ v.co for obj in meshes for v in obj.data.vertices]
    low = Vector(tuple(min(p[i] for p in points) for i in range(3)))
    high = Vector(tuple(max(p[i] for p in points) for i in range(3)))
    center = (low+high)/2
    scale = 1.65/max(high-low)
    for obj in meshes:
        for v in obj.data.vertices:
            v.co = (v.co-center)*scale
        # Fix FBX triangulation before UV/baking. Materials cover continuous panels.
        bm=bmesh.new()
        bm.from_mesh(obj.data)
        bmesh.ops.triangulate(bm,faces=list(bm.faces),quad_method='BEAUTY',ngon_method='BEAUTY')
        bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
        bm.to_mesh(obj.data)
        bm.free()
        obj.data.update()
    for obj in scene.objects:
        obj.select_set(obj in meshes)
    bpy.context.view_layer.objects.active=meshes[0]
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(54),island_margin=0.025,area_weight=0.2)
    bpy.ops.object.mode_set(mode='OBJECT')
    camera = setup_preview(scene,preview)
    # Target is the projected model centre; retain the user's reference position/direction.
    progress('三块连续阶面已完成，正面渲染', scale=scale)
    source_path=OUT/(KEY+'_FrontRevision.blend' if FINAL else KEY+'_FrontRevision_Draft.blend')
    scene.render.filepath=str(OUT/('正面_修订03_程序源.png' if FINAL else '正面_修订01.png'))
    bpy.ops.wm.save_as_mainfile(filepath=str(source_path))
    bpy.ops.render.render(write_still=True)
    if not FINAL:
        progress('首轮渲染完成，等待视觉对照')
        return
    images = bake_textures(scene,meshes,materials)
    game = game_material(images)
    source_assignments=[]
    for obj in meshes:
        source_assignments.append(([mat for mat in obj.data.materials],[p.material_index for p in obj.data.polygons]))
        obj.data.materials.clear()
        obj.data.materials.append(game)
        for p in obj.data.polygons:
            p.material_index=0
    for o in scene.objects:
        o.select_set(o == root or o in meshes)
    bpy.context.view_layer.objects.active=root
    progress('导出根与三块闭合网格')
    fbx=OUT/(KEY+'_FrontRevision.fbx')
    bpy.ops.export_scene.fbx(filepath=str(fbx),use_selection=True,object_types={'EMPTY','MESH'},
        axis_forward='-Z',axis_up='Y',apply_unit_scale=True,global_scale=1.0,
        use_mesh_modifiers=True,mesh_smooth_type='FACE',use_tspace=True,
        add_leaf_bones=False,bake_anim=False,path_mode='STRIP')
    scene.cycles.samples=64
    scene.render.filepath=str(OUT/'正面_最终_游戏贴图.png')
    progress('实际游戏贴图材质正面渲染')
    bpy.ops.render.render(write_still=True)
    front_rotation=list(camera.rotation_euler)
    front_direction=list(camera.rotation_euler.to_quaternion() @ Vector((0,0,-1)))
    camera.location=(-3.4,4.5,3.1)
    point_at(camera)
    camera.data.ortho_scale=2.35
    scene.render.filepath=str(OUT/'背侧_实体检查.png')
    scene.cycles.samples=40
    progress('背侧实体检查渲染')
    bpy.ops.render.render(write_still=True)
    camera.location=CAMERA_POSITION
    camera.rotation_euler=CAMERA_ROTATION.to_euler()
    camera.data.ortho_scale=2.23
    scene.render.filepath=str(OUT/'正面_修订03_程序源.png')
    scene.cycles.samples=64
    for obj, (mats, idx) in zip(meshes,source_assignments):
        obj.data.materials.clear()
        for mat in mats:
            obj.data.materials.append(mat)
        for face, value in zip(obj.data.polygons,idx):
            face.material_index=value
    # Keep the source shader graph as the editable default in the saved .blend.
    for image in images.values():
        image.filepath='//textures/'+Path(image.filepath).name
    for obj in scene.objects:
        obj.select_set(obj in meshes)
    bpy.context.view_layer.objects.active=meshes[0]
    for area in bpy.context.screen.areas:
        if area.type=='VIEW_3D':
            area.spaces.active.region_3d.view_rotation=CAMERA_ROTATION
            area.spaces.active.region_3d.view_perspective='ORTHO'
            area.spaces.active.region_3d.view_location=(0,0,0)
            area.spaces.active.region_3d.view_distance=3.3
            area.spaces.active.shading.type='MATERIAL'
    bpy.ops.wm.save_as_mainfile(filepath=str(source_path))
    counts=[]
    for obj in meshes:
        obj.data.calc_loop_triangles()
        bm=bmesh.new(); bm.from_mesh(obj.data)
        counts.append(dict(name=obj.name,vertices=len(obj.data.vertices),triangles=len(obj.data.loop_triangles),
                           non_manifold_edges=sum(1 for e in bm.edges if not e.is_manifold)))
        bm.free()
    points=[v.co for obj in meshes for v in obj.data.vertices]
    low=[min(p[i] for p in points) for i in range(3)]
    high=[max(p[i] for p in points) for i in range(3)]
    info=dict(material=KEY,material_zh='提纯源岩',blender_version=bpy.app.version_string,
              blend=str(source_path),fbx=str(fbx),
              textures={label:str(OUT/'textures'/(KEY+'_FrontRevision_'+label+'.png')) for label in images},
              texture_resolution=[1024,1024],color_spaces={'BaseColor':'sRGB','Normal':'Non-Color','AO':'Non-Color','MetallicSmoothness':'Non-Color'},
              metallic_smoothness_channels={'R':'Metallic=0 (非金属石料)','A':'1 - Roughness'},
              renders={'front_source':str(OUT/'正面_修订03_程序源.png'),'front_game_textures':str(OUT/'正面_最终_游戏贴图.png'),'rear':str(OUT/'背侧_实体检查.png'),'iteration_01':str(OUT/'正面_修订01.png'),'iteration_02':str(OUT/'正面_修订02_程序源.png')},
              front_camera={'position':list(CAMERA_POSITION),'direction':front_direction,'rotation_euler_rad':front_rotation,'type':'ORTHO','ortho_scale':2.23,'target':[0,0,0]},
              mesh_count=len(meshes),triangle_count=sum(c['triangles'] for c in counts),meshes=counts,
              bounds={'min':low,'max':high,'size':[high[i]-low[i] for i in range(3)]},
              root={'name':root.name,'location':list(root.location),'rotation_euler':list(root.rotation_euler),'scale':list(root.scale)},
              unit_system='METRIC',unit_scale=1.0,fbx_axes={'axis_forward':'-Z','axis_up':'Y'},
              reference=str(OUT.parent.parent/'reference'/'OrirockConcentration_PRTS.png'),
              assumptions=['背面、底面与真实厚度为单视图推定','以(.7,-6.8,8.4)正面为参考视向；其他角度不受图标约束','收藏室实际缩放与光照未在本任务中修改或验证'],
              export_contents='仅单位根与三个闭合网格；不含预览相机、灯光、地板、背景',
              visual_review='待检查修订03与烘焙材质实际渲染后补充')
    (OUT/'revision_info.json').write_text(json.dumps(info,ensure_ascii=False,indent=2),encoding='utf-8')
    progress('制作、烘焙、导出与实际渲染完成',triangles=info['triangle_count'])


if __name__=='__main__':
    try:
        main()
    except Exception:
        import traceback
        progress('失败',error=traceback.format_exc())
        raise
