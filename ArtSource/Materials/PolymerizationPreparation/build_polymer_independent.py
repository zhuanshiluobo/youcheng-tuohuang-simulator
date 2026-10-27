import bpy
import math
import json
import sys
from pathlib import Path
from mathutils import Vector, Matrix

PROJECT = Path('F:/游城拓荒模拟器')
current_key = ''
model_meshes = []
manifest_materials = {}

def srgb_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + .055) / 1.055) ** 2.4

def material(name, color, metallic=0, smoothness=.5, emission=None, transparent=False):
    name = current_key + '_' + name
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    m.diffuse_color = tuple(color)
    p = next(n for n in m.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    p.inputs['Base Color'].default_value = tuple(srgb_linear(v) for v in color[:3]) + (color[3],)
    p.inputs['Metallic'].default_value = metallic
    p.inputs['Roughness'].default_value = 1 - smoothness
    if emission:
        p.inputs['Emission Color'].default_value = tuple(srgb_linear(v) for v in emission) + (1,)
        p.inputs['Emission Strength'].default_value = .75
    if transparent:
        p.inputs['Alpha'].default_value = color[3]
        p.inputs['Transmission Weight'].default_value = .35
        p.inputs['IOR'].default_value = 1.46
    manifest_materials[name] = {'color':list(color), 'metallic':metallic, 'smoothness':smoothness,
                                'transparent':transparent}
    if emission:
        manifest_materials[name]['emission'] = list(emission)
    return m

def finish(obj, name, mat, bevel=0):
    obj.name = name
    obj.data.name = name + '_Mesh'
    obj.data.materials.append(mat)
    if bevel:
        b = obj.modifiers.new('真实边缘倒角', 'BEVEL')
        b.width = bevel
        b.segments = 3
    for p in obj.data.polygons:
        p.use_smooth = True
    n = obj.modifiers.new('加权面法线', 'WEIGHTED_NORMAL')
    n.keep_sharp = True
    obj.modifiers.new('游戏导出三角化', 'TRIANGULATE')
    model_meshes.append(obj)
    return obj

def cube(name, dims, loc, mat, bevel=.015, parent=None, rot=None):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    obj = bpy.context.object
    obj.dimensions = dims
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    if rot:
        obj.rotation_euler = rot
    if parent:
        obj.parent = parent
    return finish(obj, name, mat, bevel)

def cylinder(name, radius, depth, loc, mat, parent=None, vertices=12, rot=None):
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=radius, depth=depth, location=loc)
    obj = bpy.context.object
    if rot:
        obj.rotation_euler = rot
    if parent:
        obj.parent = parent
    return finish(obj, name, mat, min(radius * .2, .007))

def beam(name, a, b, radius, mat, parent=None):
    a, b = Vector(a), Vector(b)
    obj = cylinder(name, radius, (b-a).length, (a+b)/2, mat, parent)
    obj.rotation_euler = (b-a).to_track_quat('Z','Y').to_euler()
    return obj

def prism(name, outline, z, depth, mat, parent=None):
    n = len(outline)
    verts = [(x,y,z-depth/2) for x,y in outline] + [(x,y,z+depth/2) for x,y in outline]
    faces = [tuple(reversed(range(n))),tuple(range(n,2*n))]
    faces += [(i,(i+1)%n,(i+1)%n+n,i+n) for i in range(n)]
    mesh = bpy.data.meshes.new(name+'_Mesh')
    mesh.from_pydata(verts,[],faces)
    mesh.update()
    obj = bpy.data.objects.new(name,mesh)
    bpy.context.scene.collection.objects.link(obj)
    if parent:
        obj.parent = parent
    return finish(obj,name,mat,.003)

def group(name, loc, rot=(0,0,0)):
    obj = bpy.data.objects.new(name, None)
    bpy.context.scene.collection.objects.link(obj)
    obj.location = loc
    obj.rotation_euler = rot
    return obj

def rail_frame(name, width, depth, z, mat, trim, parent, thickness=.075):
    for side in (-1,1):
        cube(name + '_纵梁' + str(side), (thickness, depth, .07),
             (side*(width-thickness)/2,0,z), mat, .014, parent)
        cube(name + '_横梁' + str(side), (width, thickness, .07),
             (0,side*(depth-thickness)/2,z), mat, .014, parent)
        cube(name + '_纵向深色边条' + str(side), (.018,depth-.08,.017),
             (side*(width-.022)/2,0,z+.039), trim, .003, parent)
        cube(name + '_横向深色边条' + str(side), (width-.08,.018,.017),
             (0,side*(depth-.022)/2,z+.039), trim, .003, parent)

def normalize_model():
    bpy.context.view_layer.update()
    # 固定世界矩阵，移除临时装配父级，仅导出模型根和网格。
    for obj in model_meshes:
        world = obj.matrix_world.copy()
        obj.parent = None
        obj.matrix_world = world
    bpy.context.view_layer.update()
    points = [o.matrix_world @ Vector(v) for o in model_meshes for v in o.bound_box]
    lo = Vector([min(p[i] for p in points) for i in range(3)])
    hi = Vector([max(p[i] for p in points) for i in range(3)])
    center = (hi+lo)/2
    factor = 2.0/max(hi-lo)
    for obj in model_meshes:
        obj.location = (obj.location-center)*factor
        obj.scale *= factor
        bpy.ops.object.select_all(action='DESELECT')
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.transform_apply(location=False,rotation=True,scale=True)
    root = group(current_key + '_Root',(0,0,0))
    for obj in model_meshes:
        obj.parent = root
    bpy.context.view_layer.update()
    points = [o.matrix_world @ Vector(v) for o in model_meshes for v in o.bound_box]
    lo = [min(p[i] for p in points) for i in range(3)]
    hi = [max(p[i] for p in points) for i in range(3)]
    return root, {'min':lo,'max':hi,'size':[hi[i]-lo[i] for i in range(3)],'center':[(hi[i]+lo[i])/2 for i in range(3)]}

def studio(bounds):
    scene = bpy.context.scene
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = 48
    scene.cycles.use_denoising = True
    scene.render.resolution_x = 900
    scene.render.resolution_y = 900
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = 'PNG'
    scene.render.film_transparent = False
    scene.view_settings.view_transform = 'AgX'
    world = bpy.data.worlds.new('中性摄影棚环境')
    world.use_nodes = True
    world.node_tree.nodes.get('Background').inputs[0].default_value = (.20,.23,.26,1)
    world.node_tree.nodes.get('Background').inputs[1].default_value = .8
    scene.world = world
    for name,loc,power,size in [('主光',(-3,-4,5),620,4),('侧光',(4,-1,3),550,3),('后光',(0,4,4),650,3)]:
        data = bpy.data.lights.new(name,'AREA')
        data.energy = power
        data.shape = 'DISK'
        data.size = size
        obj = bpy.data.objects.new(name,data)
        scene.collection.objects.link(obj)
        obj.location = loc
        obj.rotation_euler = (-obj.location).to_track_quat('-Z','Y').to_euler()
    floor_mat = bpy.data.materials.new('预览地面材质')
    floor_mat.diffuse_color = (.11,.125,.14,1)
    floor_mat.use_nodes = True
    p = next(n for n in floor_mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    p.inputs['Base Color'].default_value = (.07,.085,.10,1)
    p.inputs['Roughness'].default_value = .72
    bpy.ops.mesh.primitive_plane_add(size=200,location=(0,0,bounds['min'][2]-.008))
    floor = bpy.context.object
    floor.name = '仅预览地面_不导出'
    floor.data.materials.append(floor_mat)
    camdata = bpy.data.cameras.new('预览相机')
    camera = bpy.data.objects.new('预览相机',camdata)
    scene.collection.objects.link(camera)
    camdata.type = 'ORTHO'
    camdata.ortho_scale = 2.75
    scene.camera = camera
    return camera

def white_scene(root):
    scene = bpy.data.scenes.new(current_key + '_Blockout')
    scene['阶段说明'] = '独立白模几何，保留实体厚度、背面与倒角；材质版位于 Finished 场景。'
    white = bpy.data.materials.new(current_key + '_Blockout_White')
    white.diffuse_color = (.65,.65,.65,1)
    copies = {}
    for obj in [root] + model_meshes:
        cp = obj.copy()
        if obj.type == 'MESH':
            cp.data = obj.data.copy()
            cp.data.materials.clear()
            cp.data.materials.append(white)
        scene.collection.objects.link(cp)
        copies[obj] = cp
    for obj,cp in copies.items():
        cp.parent = copies.get(obj.parent)


def tube_shell(name, radius, inner_radius, length, center, mat, quat):
    """真实空心透明套管，金属端帽另设，不把玻璃当发光实体。"""
    n = 40
    verts = []
    for z, r in ((-length/2, radius), (length/2, radius),
                 (-length/2, inner_radius), (length/2, inner_radius)):
        verts += [(math.cos(j*math.tau/n)*r, math.sin(j*math.tau/n)*r, z) for j in range(n)]
    faces = []
    for j in range(n):
        k = (j+1)%n
        faces += [(j,k,n+k,n+j), (2*n+k,2*n+j,3*n+j,3*n+k),
                  (k,j,2*n+j,2*n+k), (n+j,n+k,3*n+k,3*n+j)]
    mesh = bpy.data.meshes.new(name+'_Mesh')
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    obj.location = center
    obj.rotation_euler = quat.to_euler()
    return finish(obj, name, mat, .002)


def tube(name, a, b, radius, glass, liquid, silver, dark, cyan):
    a,b = Vector(a),Vector(b)
    axis = b-a
    direction = axis.normalized()
    q = axis.to_track_quat('Z','Y')
    center = (a+b)/2
    length = axis.length
    tube_shell(name+'_透明玻璃套管',radius,radius*.85,length,center,glass,q)
    # 深色金属封帽在 a 端；b 端可见聚合液与玻璃底部。
    cap = cylinder(name+'_银灰封帽',radius*1.08,.075,a,silver,vertices=40)
    cap.rotation_euler = q.to_euler()
    cap_base = cylinder(name+'_帽顶内凹',radius*.74,.003,a-direction*.039,dark,vertices=32)
    cap_base.rotation_euler = q.to_euler()
    lip = cylinder(name+'_封帽亮沿',radius*1.12,.013,a+direction*.029,silver,vertices=40)
    lip.rotation_euler = q.to_euler()
    content_center = a+direction*(length*.57)
    fluid = cylinder(name+'_青蓝聚合液',radius*.75,length*.72,content_center,liquid,vertices=40)
    fluid.rotation_euler = q.to_euler()
    meniscus = cylinder(name+'_液面反光',radius*.76,.008,a+direction*(length*.21),cyan,vertices=40)
    meniscus.rotation_euler = q.to_euler()
    endpoint = cylinder(name+'_玻璃底部厚圈',radius*.95,.025,b-direction*.013,glass,vertices=40)
    endpoint.rotation_euler = q.to_euler()
    # 狭长的玻璃反光边只依赖光照，不给金属自发光。
    return direction


def polymer():
    graphite = material('容器深灰',(.24,.295,.32,1),.45,.52)
    black = material('凹槽黑',(.055,.085,.09,1),.25,.39)
    silver = material('金属银边',(.57,.65,.68,1),.86,.28)
    teal = material('青色聚合层板',(.018,.48,.54,1),.08,.58)
    cyan = material('层板亮边',(.06,.69,.74,1),.04,.63,(.005,.04,.05))
    glass = material('青蓝透光盖板',(.11,.49,.54,.32),.02,.75,transparent=True)
    green = material('黄绿标签',(.39,.80,.045,1),.00,.44)
    ink = material('标签深色印记',(.045,.16,.09,1),.00,.38)
    gold = material('锁扣黄铜',(.79,.56,.17,1),.72,.63)
    lining = material('青灰托盘内衬',(.075,.19,.205,1),.02,.18)
    w,d = 1.67,1.37
    # 图标为浅托盘，不是提箱：前侧板有高度，侧框薄，顶部无遮盖的管剂。
    cube('浅容器封闭底板',(w,d,.07),(0,0,-.235),graphite,.014)
    cube('托盘内部深灰底面',(1.49,1.20,.032),(0,0,-.077),black,.012)
    cube('托盘青灰内衬薄板',(1.47,1.18,.013),(0,0,-.053),lining,.009)
    for side in (-1,1):
        cube('浅灰侧壁_%s'%side,(.09,d,.30),(side*(w-.09)/2,0,-.09),graphite,.010)
        cube('前后灰薄板_%s'%side,(w-.08,.072,.30),(0,side*(d-.072)/2,-.09),graphite,.010)
        cube('左右银框_%s'%side,(.060,d+.018,.036),(side*(w-.040)/2,0,.081),silver,.008)
        cube('前后银框_%s'%side,(w+.012,.040,.036),(0,side*(d+.002)/2,.081),silver,.008)
        cube('左右内沿暗缝_%s'%side,(.019,d-.07,.018),(side*(w-.125)/2,0,.063),black,.003)
        cube('前后内沿暗缝_%s'%side,(w-.15,.015,.018),(0,side*(d-.122)/2,.063),black,.003)
    # 右边框上细密压合齿是托盘分件，不铺满中间；对应参考右侧明暗点列。
    for j in range(18):
        cube('右框密集压合齿_%02d'%j,(.032,.015,.016),(.750,-.57+j*.066,.102),black,.002)
    # 前侧长标签保留图标鲜黄绿大块比例，窄灰边和右端独立小标签。
    cube('前黄绿长标签',(1.10,.006,.225),(-.042,-d/2-.005,-.080),green,.002)
    cube('前标签左侧黑底印章',(.142,.010,.215),(-.675,-d/2-.009,-.080),black,.001)
    for x in (-.695,-.671,-.648):
        cube('左侧印章细纹_%s'%x,(.009,.006,.075),(x,-d/2-.016,-.020),green,.001)
    # 图标字码不可辨识，使用短线式工业铭牌，避免虚构可读商标。
    for row in range(2):
        for j,width in enumerate((.037,.074,.054,.084,.043,.058,.040,.060,.038,.042)):
            cube('标签短编码_%s_%s'%(row,j),(width,.006,.015),
                 (-.522+j*.100,-d/2-.010,.010-row*.035),ink,.001)
    cube('前右独立绿色标记',(.116,.007,.112),(.654,-d/2-.006,-.154),green,.002)
    # 银灰角护罩和连接收口，背面为单视图推定的无标签面板。
    for x in (-.786,.786):
        cube('前护角_%s'%x,(.10,.056,.335),(x,-d/2+.01,-.093),graphite,.007)
        cube('后护角_%s'%x,(.10,.056,.335),(x,d/2-.01,-.093),graphite,.007)
    cube('背侧中心接缝',(1.42,.008,.017),(0,d/2+.004,-.145),black,.002)
    for x in (-.65,.65):
        cube('背侧固定片_%s'%x,(.075,.014,.130),(x,d/2+.013,-.085),silver,.003)
    for x in (-.66,.66):
        cube('底部防滑脚_%s'%x,(.14,.95,.021),(x,0,-.281),black,.006)
    # 三根不同姿态的试剂管是原图主形态。两根置于浅盘，一根斜靠后框。
    tube('前侧横置管',(-.49,-.39,.046),(.45,-.35,.046),.063,glass,teal,silver,black,cyan)
    tube('中部斜置管',(-.42,.30,.070),(.50,-.04,.070),.065,glass,teal,silver,black,cyan)
    rear_a,rear_b = Vector((.12,.46,.770)),Vector((.49,.32,.035))
    tube('后部斜立管',rear_a,rear_b,.070,glass,teal,silver,black,cyan)
    # 合理推定管夹：底部卡座和后框小卡片建立真实连接，侧/背视时可读。
    for name,x,y,z,rz in [('前管左夹',-.36,-.383,-.028,.04),('前管右夹',.33,-.355,-.028,.04),
                         ('中管左夹',-.30,.255,-.009,-.354),('中管右夹',.34,.019,-.009,-.354)]:
        cube(name,(.044,.13,.072),(x,y,z),graphite,.007,rot=(0,0,rz))
    cube('后管底部卡座',(.175,.155,.062),(.489,.322,-.012),graphite,.010)
    cube('后管背框接片',(.110,.035,.085),(.275,.594,.151),silver,.004)
    rear_axis = rear_b-rear_a
    rear_q = rear_axis.to_track_quat('Z','Y')
    clamp_center = rear_a + rear_axis*.64
    tube_shell('后管背侧薄抱箍',.074,.070,.026,clamp_center,silver,rear_q)
    beam('后管支撑短连线',(.275,.580,.173),clamp_center+Vector((-.025,.060,0)),.014,silver)


def save_and_render():
    key = current_key
    root,bounds = normalize_model()
    scene = bpy.context.scene
    scene.name = key + '_Finished'
    scene.unit_settings.system = 'METRIC'
    scene.unit_settings.scale_length = 1
    scene['参考说明'] = '依据用户附图聚合剂自制：浅银灰托盘、黄绿前标签、三根青蓝管剂；无稀有度环。背板、底部与管夹为单视图推定。'
    scene['独立制作说明'] = '聚合剂子代理独立修订；第一版源保留为 FirstPass.blend。'
    source = PROJECT/'ArtSource'/'Materials'/key
    game = PROJECT/'Assets'/'YC'/'Presentation'/'Models'/key
    white_scene(root)
    cam = studio(bounds)
    scene.cycles.samples = 16
    scene.render.resolution_x = 800
    scene.render.resolution_y = 800
    bpy.ops.object.select_all(action='DESELECT')
    for obj in [root]+model_meshes:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = root
    bpy.ops.export_scene.fbx(filepath=str(game/(key+'_Finished.fbx')),use_selection=True,
                            object_types={'EMPTY','MESH'},use_mesh_modifiers=True,
                            add_leaf_bones=False,axis_forward='-Z',axis_up='Y',
                            apply_unit_scale=True,bake_space_transform=False,
                            mesh_smooth_type='FACE',use_tspace=True,path_mode='AUTO')
    target = Vector((0,0,-.06))
    views = [('Reference',(2.0,-4.2,4.0)),('Front',(0,-5,0)),
             ('Side',(5,0,0)),('Back',(0,5,0)),('Bottom',(0,-.0001,-5))]
    manifest = {'materials':manifest_materials,'model':key,'bounds':bounds,
                'reference':'用户附图聚合剂；三根管剂与浅托盘。无稀有度圆环；背面、底面、夹具为单视图推定',
                'meshCount':len(model_meshes),'units':'m','sourceAxes':'Z up, front -Y',
                'previewReferenceCamera':{'location':list(views[0][1]),'target':list(target),'orthographicScale':2.75},
                'independentRevision':'每模型独立子代理制作：三根透明聚合剂管；参考角校准后管实体左倾与俯视角；最终盒体侧壁与底深增加 50%，标签随之增高加宽',
                'referenceAngleMatch':{'azimuthDegrees':25.463345,'elevationDegrees':41.1179,
                                       'rearTubeTopSource':[.12,.46,.77],
                                       'rearTubeBottomSource':[.49,.32,.035],
                                       'frontLabelWidthSource':1.10},
                'finalDepthAdjustment':{'previousWallHeight':.20,'wallHeight':.30,
                                        'bottomLowering':.10,'frontLabelHeight':.225}}
    (game/'material_manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
    cam.location = views[0][1]
    cam.rotation_euler = (target-cam.location).to_track_quat('-Z','Y').to_euler()
    scene.render.filepath = str(source/(key+'_IndependentReference.png'))
    bpy.ops.wm.save_as_mainfile(filepath=str(source/(key+'_Finished.blend')),compress=True)
    for name,loc in views:
        cam.location = loc
        aim = target if name == 'Reference' else Vector((0,0,0))
        cam.rotation_euler = (aim-cam.location).to_track_quat('-Z','Y').to_euler()
        scene.render.filepath = str(source/(key+'_Independent'+name+'.png'))
        if name == 'Bottom':
            bpy.data.objects['仅预览地面_不导出'].hide_render = True
        bpy.ops.render.render(write_still=True)
    print('POLYMER_INDEPENDENT_DONE',len(model_meshes),bounds)


current_key = 'PolymerizationPreparation'
bpy.ops.wm.read_factory_settings(use_empty=True)
polymer()
save_and_render()
