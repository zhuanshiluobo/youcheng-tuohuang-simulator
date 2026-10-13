"""固源岩独立修订：参考位置约束的实体断面，不执行共享脚本入口。"""
import bpy
import bmesh
import math
import json
import sys
from pathlib import Path
from mathutils import Vector
from mathutils import noise

OUT = Path(__file__).resolve().parent
CAMERA_POSITION = Vector((3.8, -6.2, 4.0))
OUTWARD = CAMERA_POSITION.normalized()
RIGHT = Vector((0, 0, 1)).cross(OUTWARD).normalized()
UP = OUTWARD.cross(RIGHT).normalized()
PIXEL_UNIT = 0.0122
POINTS = {}
ROUND = 3
if '--round' in sys.argv:
    ROUND = int(sys.argv[sys.argv.index('--round') + 1])

def anchor(name, x, y, plane, value):
    # 沿原预览相机的正交射线在实体断面上放置控制点，背面仍有完整体积。
    p = RIGHT * ((x - 92) * PIXEL_UNIT) + UP * ((85 - y) * PIXEL_UNIT)
    axis = {'X': 0, 'Y': 1, 'Z': 2}[plane]
    p += OUTWARD * ((value - p[axis]) / OUTWARD[axis])
    POINTS[name] = p
    return name

def srgb(rgb):
    def linear(c):
        c /= 255
        return c / 12.92 if c <= 0.04045 else ((c + .055) / 1.055) ** 2.4
    return tuple(linear(c) for c in rgb) + (1,)

def rock_material(name, rgb, roughness, front=False):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    mat.diffuse_color = srgb(rgb)
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = next(n for n in nodes if n.type == 'BSDF_PRINCIPLED')
    bsdf.inputs['Roughness'].default_value = roughness
    bsdf.inputs['Metallic'].default_value = 0
    bsdf.inputs['Specular IOR Level'].default_value = .22
    coord = nodes.new('ShaderNodeTexCoord')
    coord.label = '实体坐标：连续矿质纹理，无图标投影'
    broad = nodes.new('ShaderNodeTexNoise')
    broad.inputs['Scale'].default_value = 8.5
    broad.inputs['Detail'].default_value = 4
    broad.inputs['Roughness'].default_value = .72
    links.new(coord.outputs['Object'], broad.inputs['Vector'])
    ramp = nodes.new('ShaderNodeValToRGB')
    ramp.label = '连续色差，所有相邻三角形共用'
    ramp.color_ramp.elements[0].position = .18
    ramp.color_ramp.elements[0].color = srgb(tuple(round(c * .76) for c in rgb))
    ramp.color_ramp.elements[1].position = .81
    ramp.color_ramp.elements[1].color = srgb(tuple(min(255, round(c * 1.09)) for c in rgb))
    links.new(broad.outputs['Fac'], ramp.inputs[0])
    mapping = nodes.new('ShaderNodeVectorMath')
    mapping.operation = 'MULTIPLY'
    mapping.inputs[1].default_value = (1.15, 1, .48) if front else (1, 1, 1)
    links.new(coord.outputs['Object'], mapping.inputs[0])
    grit = nodes.new('ShaderNodeTexNoise')
    grit.inputs['Scale'].default_value = 105 if not front else 66
    grit.inputs['Detail'].default_value = 2.5
    grit.inputs['Roughness'].default_value = .74
    links.new(mapping.outputs[0], grit.inputs['Vector'])
    pore = nodes.new('ShaderNodeTexVoronoi')
    pore.inputs['Scale'].default_value = 180
    links.new(coord.outputs['Object'], pore.inputs['Vector'])
    pores = nodes.new('ShaderNodeValToRGB')
    pores.color_ramp.elements[0].position = .035
    pores.color_ramp.elements[0].color = (.30, .30, .30, 1)
    pores.color_ramp.elements[1].position = .21
    pores.color_ramp.elements[1].color = (1, 1, 1, 1)
    links.new(pore.outputs['Distance'], pores.inputs[0])
    grain_color = nodes.new('ShaderNodeMapRange')
    grain_color.inputs['From Min'].default_value = .28
    grain_color.inputs['From Max'].default_value = .72
    grain_color.inputs['To Min'].default_value = .36
    grain_color.inputs['To Max'].default_value = 1.15
    links.new(grit.outputs['Fac'], grain_color.inputs['Value'])
    mix = nodes.new('ShaderNodeMixRGB')
    mix.blend_type = 'MULTIPLY'
    mix.inputs[0].default_value = .68
    links.new(ramp.outputs[0], mix.inputs[1])
    links.new(grain_color.outputs[0], mix.inputs[2])
    pore_color = nodes.new('ShaderNodeMixRGB')
    pore_color.blend_type = 'MULTIPLY'
    pore_color.inputs[0].default_value = .44
    links.new(mix.outputs[0], pore_color.inputs[1])
    links.new(pores.outputs[0], pore_color.inputs[2])
    links.new(pore_color.outputs[0], bsdf.inputs['Base Color'])
    height = nodes.new('ShaderNodeMixRGB')
    height.blend_type = 'MULTIPLY'
    height.inputs[0].default_value = .22
    links.new(grit.outputs['Fac'], height.inputs[1])
    links.new(pores.outputs[0], height.inputs[2])
    bump = nodes.new('ShaderNodeBump')
    bump.inputs['Strength'].default_value = .48
    bump.inputs['Distance'].default_value = .0044 if front else .006
    links.new(height.outputs[0], bump.inputs['Height'])
    links.new(bump.outputs[0], bsdf.inputs['Normal'])
    rough = nodes.new('ShaderNodeMapRange')
    rough.inputs['To Min'].default_value = roughness - .045
    rough.inputs['To Max'].default_value = min(1, roughness + .04)
    links.new(grit.outputs['Fac'], rough.inputs[0])
    links.new(rough.outputs[0], bsdf.inputs['Roughness'])
    mat['说明'] = '程序化岩质材质；不含参考图、不按三角形随机拼色。'
    mat.use_fake_user = True
    return mat

def add_mesh(name, points, faces, material_ids, mats, collection, root):
    names = list(points)
    mesh = bpy.data.meshes.new(name + '_Mesh')
    mesh.from_pydata([points[n] for n in names], [], [[names.index(n) for n in f] for f in faces])
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    obj.parent = root
    for mat in mats:
        mesh.materials.append(mat)
    for p, mid in zip(mesh.polygons, material_ids):
        p.material_index = mid
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bmesh.ops.triangulate(bm, faces=list(bm.faces), quad_method='BEAUTY', ngon_method='BEAUTY')
    bm.to_mesh(mesh)
    bm.free()
    for p in mesh.polygons:
        p.use_smooth = False
    return obj

def build():
    # 独立 factory-startup 文件，删除的只是本进程新建的默认场景对象。
    scene = bpy.context.scene
    for obj in list(scene.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    scene.name = '固源岩_正面修订_程序源'
    scene.unit_settings.system = 'METRIC'
    scene.unit_settings.scale_length = 1
    model = bpy.data.collections.new('固源岩_实体模型')
    devices = bpy.data.collections.new('预览设备_不导出')
    scene.collection.children.link(model)
    scene.collection.children.link(devices)
    root = bpy.data.objects.new('OrirockCube_Root', None)
    model.objects.link(root)
    root['材料'] = '固源岩 OrirockCube'
    root['参考'] = str(OUT.parent.parent / 'reference' / 'OrirockCube_PRTS.png')
    root['推定'] = '背面和厚度按单视图重建；黄色圆环、背景、投影不是模型。'
    root['修订'] = '手工定位连续断面、上沿缺口与右側阶梯崩口；根缩放1。'
    mats = [
        rock_material('OrirockCube_源_灰褐顶面', (138, 131, 91), .90),
        rock_material('OrirockCube_源_褐色连续断层', (104, 97, 67), .91),
        rock_material('OrirockCube_源_深灰前断面', (52, 67, 73), .92, True),
        rock_material('OrirockCube_源_浅黄右断面', (234, 220, 175), .86),
        rock_material('OrirockCube_源_暗侧面', (68, 70, 51), .94),
    ]
    anchor('TL', 40, 56, 'Z', .50)
    back = [anchor(n,x,y,'Z',z) for n,x,y,z in [
        ('BL0',61,41,.55), ('BL1',73,31,.575), ('B0',81,34,.535),
        ('B1',88,33,.555), ('B2',94,29,.59), ('B3',105,34,.545),
        ('B4',117,33,.565), ('B5',130,40,.54), ('B6',136,47,.495),
    ]]
    anchor('RT',148,51,'X',.65)
    anchor('TR',131,71,'X',.65)
    lip = [anchor(n,x,y,plane,val) for n,x,y,plane,val in [
        ('L0',46,66,'Y',-.48), ('L1',54,72,'Y',-.50),
        ('L2',67,77,'Y',-.48), ('L3',82,79,'Y',-.53),
        ('L4',92,74,'Y',-.50), ('L5',94,65,'Z',.51),
        ('L6',101,68,'Y',-.47), ('L7',114,74,'Y',-.43),
        ('L8',125,73,'X',.65),
    ]]
    anchor('TC',93,50,'Z',.54)
    top = ['TL'] + back + ['RT','TR'] + list(reversed(lip))
    faces = [[top[i],top[(i+1)%len(top)],'TC'] for i in range(len(top))]
    ids = [0] * len(faces)
    lower = [anchor(n,x,y,'Y',val) for n,x,y,val in [
        ('C0',39,71,-.43), ('C1',35,77,-.43), ('C2',46,76,-.51),
        ('C3',57,80,-.47), ('C4',68,83,-.45), ('C5',81,82,-.45),
        ('C6',90,87,-.43), ('C7',91,96,-.42), ('C8',101,104,-.45),
        ('C9',96,112,-.41), ('C10',108,115,-.42),
    ]]
    seam = [anchor(n,x,y,'X',val) for n,x,y,val in [
        ('S0',125,79,.65), ('S1',121,84,.65), ('S2',121,96,.65),
        ('S3',131,89,.65), ('S4',128,98,.64), ('S5',122,105,.65),
        ('S6',119,110,.62), ('S7',117,115,.64),
    ]]
    # 褐色断层的折线从顶面的缺口连续下降到右下崩口，是真实曲折断面。
    fold = [anchor(n,x,y,'Y',val) for n,x,y,val in [
        ('M0',103,84,-.49), ('M1',106,95,-.48), ('M2',113,108,-.45),
    ]]
    faces.append(['TL'] + lip[:6] + fold + ['S7'] + list(reversed(lower)))
    ids.append(1)
    faces.append(lip[5:] + ['TR'] + seam + list(reversed(fold)))
    ids.append(1)
    bottom = [anchor(n,x,y,'Y',val) for n,x,y,val in [
        ('F0',38,111,-.43), ('F1',41,123,-.44), ('F2',52,130,-.44),
        ('F3',74,136,-.47), ('F4',96,139,-.43),
    ]]
    anchor('FB',119,138,'X',.65)
    # 大灰色前断面中保留一条指定的斜向断层，形成两片连续岩面；无散落碎块。
    frontfold = [anchor(n,x,y,'Y',val) for n,x,y,val in [
        ('I0',53,96,-.515), ('I1',64,109,-.54),
        ('I2',73,120,-.50), ('I3',90,128,-.525),
    ]]
    faces.append(['C1','C2'] + frontfold + ['F4','F3','F2','F1','F0'])
    ids.append(2)
    faces.append(lower[2:] + ['S7','FB','F4'] + list(reversed(frontfold)))
    ids.append(2)
    anchor('R1',148,94,'X',.65)
    anchor('R2',140,97,'X',.63)
    anchor('R3',139,119,'X',.63)
    faces.append(['RT','TR'] + seam + ['FB','R3','R2','R1'])
    ids.append(3)
    # 左后、背面与底面是完整封闭实体，绝非朝向相机的图标贴片。
    back_bottom = POINTS['BL1'].copy()
    back_bottom.z = -.46
    POINTS['HB'] = back_bottom
    faces.append(['TL','C0','C1','F0','F1','HB','BL1','BL0'])
    ids.append(4)
    faces.append(['HB','R3','R2','R1','RT'] + list(reversed(back[1:])))
    ids.append(4)
    faces.append(['F1','F2','F3','F4','FB','R3','HB'])
    ids.append(4)
    body = add_mesh('OrirockCube_连续崩裂主体', POINTS, faces, ids, mats, model, root)
    # 左下唯一清楚可辨的小断角，以明确的楔形截面手工构造。
    cp = {}
    for n,x,y,pl,val in [
        ('a',31,125,'Y',-.51), ('b',36,120,'Y',-.47), ('c',43,117,'Y',-.45),
        ('d',49,122,'Y',-.52), ('e',53,129,'Y',-.56), ('f',51,137,'Y',-.55),
        ('g',42,138,'Y',-.57), ('h',31,132,'Y',-.55), ('i',43,126,'Y',-.64),
    ]:
        anchor(n,x,y,pl,val)
        cp[n] = POINTS.pop(n)
    chipfaces = [['a','b','c','d','i'], ['i','d','e','f'], ['a','i','f','g','h'], ['a','h','g','f','e','d','c','b']]
    chip = add_mesh('OrirockCube_左下原位断角',cp,chipfaces,[1,3,4,4],mats,model,root)
    meshes = [body,chip]
    # 小幅连续几何起伏。轮廓、崩口和矿质分区仍由上面的确定控制点决定。
    for obj in meshes:
        bm = bmesh.new()
        bm.from_mesh(obj.data)
        edge_borders = [e for e in bm.edges if len(e.link_faces)==2 and e.link_faces[0].material_index != e.link_faces[1].material_index]
        for e in edge_borders:
            e.smooth = False
        bmesh.ops.subdivide_edges(bm, edges=list(bm.edges), cuts=3, use_grid_fill=True)
        bmesh.ops.triangulate(bm, faces=list(bm.faces))
        # 连续顶面内部轻度松弛，消除扇形分面的大直线；矿质边界与外轮廓固定。
        relax = [v for v in bm.verts if {f.material_index for f in v.link_faces} == {0}]
        for _ in range(7):
            bmesh.ops.smooth_vert(bm, verts=relax, factor=.55, use_axis_x=True, use_axis_y=True, use_axis_z=True)
        bm.normal_update()
        for v in bm.verts:
            if any(not e.smooth for e in v.link_edges):
                continue
            p = v.co.copy()
            material_set = {f.material_index for f in v.link_faces}
            depth = .006 if material_set in ({0},{2}) else .003
            amount = noise.noise_vector(p * 21, noise_basis='PERLIN_ORIGINAL').x * depth
            v.co += v.normal * amount
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        for f in bm.faces:
            f.smooth = True
        bm.to_mesh(obj.data)
        bm.free()
        obj.data.update()
        # 仅在断面转折处保留硬边；没有逐三角形拼色。
        obj.data.set_sharp_from_angle(angle=.80)
    bpy.context.view_layer.update()
    vertices = [o.matrix_world @ v.co for o in meshes for v in o.data.vertices]
    low = Vector([min(v[i] for v in vertices) for i in range(3)])
    high = Vector([max(v[i] for v in vertices) for i in range(3)])
    center = (low + high) * .5
    for obj in meshes:
        for v in obj.data.vertices:
            v.co -= center
        obj['说明'] = '闭合实体；控制点按PRTS前视图定位；源程序材质保留。'
    for obj in scene.objects:
        obj.select_set(obj in meshes)
    bpy.context.view_layer.objects.active = body
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(62), island_margin=.015)
    bpy.ops.object.mode_set(mode='OBJECT')
    scene.render.engine = 'CYCLES'
    scene.cycles.device = 'CPU'
    scene.cycles.samples = 24
    scene.cycles.use_denoising = True
    scene.render.threads_mode = 'FIXED'
    scene.render.threads = 4
    scene.view_settings.view_transform = 'Standard'
    scene.view_settings.look = 'None'
    scene.view_settings.exposure = 0
    scene.view_settings.gamma = 1
    scene.render.resolution_x = scene.render.resolution_y = 800
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGBA'
    scene.render.film_transparent = True
    world = bpy.data.worlds.new('固源岩_中性预览环境')
    world.use_nodes = True
    bg = next(n for n in world.node_tree.nodes if n.type == 'BACKGROUND')
    bg.inputs['Color'].default_value = (.32,.35,.39,1)
    bg.inputs['Strength'].default_value = .40
    scene.world = world
    data = bpy.data.cameras.new('OrirockCube_正面相机')
    camera = bpy.data.objects.new('OrirockCube_正面相机',data)
    devices.objects.link(camera)
    camera.location = CAMERA_POSITION
    camera.rotation_euler = (-camera.location).to_track_quat('-Z','Y').to_euler()
    camera.data.type = 'ORTHO'
    camera.data.ortho_scale = 1.95
    scene.camera = camera
    for n,loc,energy,size in [
        ('主光',(-3,-4,7),320,4), ('右补光',(4,-2,3),240,4), ('背光',(1,4,5),150,3),
    ]:
        ld = bpy.data.lights.new('OrirockCube_'+n,'AREA')
        lo = bpy.data.objects.new('OrirockCube_'+n,ld)
        devices.objects.link(lo)
        lo.location = loc
        lo.rotation_euler = (-lo.location).to_track_quat('-Z','Y').to_euler()
        ld.energy = energy
        ld.shape = 'DISK'
        ld.size = size
    scene['说明'] = '固源岩 FrontRevision；模型与程序材质可编辑。预览设备不导出。'
    scene['参考像素尺度'] = PIXEL_UNIT
    scene['源参考中心偏移'] = list(center)
    bpy.context.view_layer.update()
    for area in bpy.context.screen.areas:
        if area.type == 'VIEW_3D':
            area.spaces.active.region_3d.view_rotation = camera.rotation_euler.to_quaternion()
            area.spaces.active.region_3d.view_distance = 3
            area.spaces.active.region_3d.view_location = (0,0,0)
            area.spaces.active.region_3d.view_perspective = 'ORTHO'
            area.spaces.active.shading.type = 'MATERIAL'
    return scene,root,meshes

if __name__ == '__main__':
    scene,root,meshes = build()
    bpy.ops.wm.save_as_mainfile(filepath=str(OUT/f'OrirockCube_FrontDraft_v{ROUND:02d}.blend'))
    scene.render.filepath = str(OUT/f'OrirockCube_Front_v{ROUND:02d}.png')
    bpy.ops.render.render(write_still=True)
    result = {'version':bpy.app.version_string,'meshes':[]}
    for obj in meshes:
        bm=bmesh.new(); bm.from_mesh(obj.data)
        obj.data.calc_loop_triangles()
        result['meshes'].append({'name':obj.name,'triangles':len(obj.data.loop_triangles),'vertices':len(obj.data.vertices),'non_manifold_edges':sum(not e.is_manifold for e in bm.edges)})
        bm.free()
    (OUT/'draft_info.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print('FRONT_PREVIEW_READY',json.dumps(result,ensure_ascii=False))
