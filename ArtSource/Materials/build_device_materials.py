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

def bipolar():
    copper = material('红铜框架', (.69,.17,.075,1), .80,.63)
    edge = material('石墨边条', (.105,.115,.12,1), .68,.56)
    silver = material('银色卡扣', (.62,.67,.68,1), .88,.71)
    sheet = material('纳米片深红', (.50,.028,.012,1), .35,.66)
    hot = material('橙红热核', (1,.15,.018,1), .08,.6, (.82,.105,.01))
    hotspot = material('炽热琥珀裂片',(1,.52,.09,1),.05,.52,(1,.28,.012))
    gold = material('细金属导路', (.94,.44,.12,1), .72,.58)
    dark = material('后侧隔热底片', (.115,.06,.038,1), .12,.42)
    # 图标可见的三层矩形载架错位，前层热片有实体厚度。
    frames = [(-.27, -.17, .055), (.035,.12,-.035), (.27,-.10,.06)]
    frame_matrices = []
    for index,(z,yaw,tilt) in enumerate(frames):
        g = group('载架组_%d' % (index+1), ((index-1)*.065,(index-1)*.055,z), (tilt,.025,yaw))
        frame_matrices.append(Matrix.Translation(g.location) @ g.rotation_euler.to_matrix().to_4x4())
        rail_frame('矩形载架_%d' % (index+1),1.9,1.14,0,copper,edge,g)
        for x in (-.84,.84):
            for y in (-.47,.47):
                cube('银色角部夹片_%d_%s_%s' %(index,x,y),(.13,.105,.026),(x,y,.042),silver,.007,g)
                cylinder('角部紧固件_%d_%s_%s' %(index,x,y),.024,.015,(x,y,.06),edge,g,16)
        if index < 2:
            cube('纳米层_%d' %(index+1),(1.60,.83,.038),(0,0,.025),sheet,.02,g)
            cube('背面隔热片_%d' %(index+1),(1.53,.77,.015),(0,0,-.005),dark,.009,g)
            # 每块热片上的断续发热条和细铜导路，避免单块均匀平板。
            for row in range(5):
                y = -.31 + row*.155
                for col in range(4):
                    x = -.60 + col*.40
                    length = .26 if (row+col)%3 else .33
                    cube('发热层_%d_条_%d_%d'%(index,row,col),(length,.056,.008),
                         (x,y,.05),hot,.009,g,rot=(0,0,(-1 if row%2 else 1)*.12))
                cube('导线_%d_%d'%(index,row),(1.43,.013,.007),(0,y+.045,.051),gold,.003,g)
            for x in (-.71,.71):
                cube('纳米片边缘接点_%d_%s'%(index,x),(.035,.72,.012),(x,0,.048),silver,.003,g)
            for j in range(9):
                x = -.55+(j%3)*.5
                y = -.25+(j//3)*.24
                skew = .035 if j%2 else -.025
                polygon = [(x-.09,y-.035),(x+.03,y-.04),(x+.095,y+skew),
                           (x+.015,y+.04),(x-.07,y+.02)]
                prism('热核不规则裂片_%d_%d'%(index,j),polygon,.058,.01,hotspot,g)
        else:
            # 上层只有交错的细支承条，热核透过开放框架可读。
            for x in (-.58,.58):
                cube('上层压片_%s'%x,(.026,1.04,.026),(x,0,-.005),silver,.005,g)
    for i,(x,y) in enumerate(((-.84,-.47),(.84,-.47),(-.84,.47),(.84,.47))):
        for layer in range(2):
            a = frame_matrices[layer] @ Vector((x,y,0))
            b = frame_matrices[layer+1] @ Vector((x,y,0))
            beam('层间红铜斜撑_%d_%d'%(i,layer),a,b,.025,copper)
    # 红色纳米片正面与后面均有端盖，侧视不会成为纸片。
    cube('前侧银色端子',(1.40,.045,.048),(0,-.49,-.17),silver,.009,rot=(.06,0,-.17))

def polymer():
    graphite = material('容器深灰',(.19,.225,.24,1),.62,.55)
    black = material('凹槽黑',(.055,.085,.09,1),.30,.44)
    silver = material('金属银边',(.57,.65,.68,1),.86,.76)
    teal = material('青色聚合层板',(.055,.55,.61,1),.30,.62)
    cyan = material('层板亮边',(.08,.85,.88,1),.10,.69, (.02,.25,.29))
    glass = material('青蓝透光盖板',(.07,.59,.67,.32),.04,.92,transparent=True)
    green = material('黄绿标签',(.57,.78,.09,1),.06,.49)
    printmat = material('标签深色印记',(.07,.17,.13,1),.05,.48)
    gold = material('锁扣黄铜',(.79,.56,.17,1),.72,.63)
    w,d = 1.67,1.25
    cube('容器封闭底板',(w,d,.14),(0,0,-.30),graphite,.045)
    cube('底板内衬',(1.48,1.06,.06),(0,0,-.205),black,.018)
    for side in (-1,1):
        cube('侧壁_%s'%side,(.115,d-.02,.39),(side*(w-.115)/2,0,-.08),graphite,.025)
        cube('前后壁_%s'%side,(w-.12,.11,.39),(0,side*(d-.11)/2,-.08),graphite,.025)
        cube('侧壁上银边_%s'%side,(.07,d+.035,.07),(side*(w-.035)/2,0,.138),silver,.015)
        cube('前后银边_%s'%side,(w+.03,.065,.07),(0,side*(d+.003)/2,.138),silver,.015)
        cube('側部凹槽_%s'%side,(.017,d-.21,.105),(side*(w/2+.002),0,-.16),black,.003)
        for j in range(6):
            cube('侧部肋条_%s_%s'%(side,j),(.022,.038,.10),
                 (side*(w/2+.009),-.40+j*.16,-.16),silver,.004)
    # 独立阶梯层板从前至后升高；透光盖板下有完整内部结构。
    for i in range(7):
        y = -.42+i*.135
        z = -.06+i*.023
        cube('内部聚合层板_%02d'%i,(1.29,.106,.045),(0,y,z),teal,.012)
        cube('层板青色亮边_%02d'%i,(1.22,.016,.016),(0,y-.046,z+.024),cyan,.004)
        for x in (-.55,.55):
            cube('层板端接_%d_%s'%(i,x),(.095,.09,.012),(x,y,z+.029),silver,.004)
    # 完整玻璃有厚度与透明度；内层始终由实体层板提供形状。
    cube('青蓝玻璃顶面',(1.40,1.01,.033),(0,0,.163),glass,.016)
    for x in (-.695,.695):
        cube('顶盖纵框_%s'%x,(.024,1.04,.035),(x,0,.181),silver,.005)
    for y in (-.502,.502):
        cube('顶盖横框_%s'%y,(1.42,.027,.035),(0,y,.181),silver,.005)
    # 图中前侧显眼的黄绿标签沿容器折角延续，正背面均有收口。
    cube('前侧黄绿标签',(1.25,.015,.135),(-.04,-d/2-.007,-.13),green,.006)
    cube('标签右侧折角',(.015,.21,.135),(.617,-.535,-.13),green,.005)
    cube('前侧黑色标签铭牌',(.34,.019,.071),(-.41,-d/2-.019,-.129),printmat,.003)
    for j in range(5):
        cube('标签编码_%d'%j,(.013,.021,.047),(-.56+j*.031,-d/2-.031,-.129),green,.001)
    for j,width in enumerate((.21,.12,.27)):
        cube('标签字段_%d'%j,(width,.021,.011),(-.10+j*.21,-d/2-.023,-.105),printmat,.002)
    cube('前侧黄绿底条',(1.43,.04,.047),(0,-d/2-.015,-.284),green,.009)
    cube('背部黄绿封签',(.52,.017,.08),(.17,d/2+.006,-.16),green,.004)
    for x in (-.66,.66):
        for y in (-.46,.46):
            cylinder('盖板锁钉_%s_%s'%(x,y),.024,.017,(x,y,.207),graphite,vertices=16)
    # 顶部短连线/卡扣在单张图标背部可见，采用可拆卸 U 形连接件。
    for i,x in enumerate((-.39,.35)):
        beam('背部连线立柱_%d_A'%i,(x,.54,.19),(x,.54,.37),.024,silver)
        beam('背部连线上横_%d'%i,(x,.54,.37),(x+.19,.54,.37),.026,silver)
        beam('背部连线立柱_%d_B'%i,(x+.19,.54,.37),(x+.19,.54,.20),.024,silver)
        cube('黄铜锁扣_%d'%i,(.105,.07,.06),(x+.095,.54,.35),gold,.009)
    for x in (-.66,.66):
        cube('底部防滑脚_%s'%x,(.17,.84,.044),(x,0,-.391),black,.016)

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

def save_and_render(key):
    root,bounds = normalize_model()
    scene = bpy.context.scene
    scene.name = key + '_Finished'
    scene.unit_settings.system = 'METRIC'
    scene.unit_settings.scale_length = 1
    scene['参考说明'] = '依据用户提供的单视图图标自制；无稀有度圆环。不可见背部及内部装配为合理推定。'
    source = PROJECT/'ArtSource'/'Materials'/key
    game = PROJECT/'Assets'/'YC'/'Presentation'/'Models'/key
    source.mkdir(parents=True,exist_ok=True)
    game.mkdir(parents=True,exist_ok=True)
    white_scene(root)
    cam = studio(bounds)
    bpy.ops.object.select_all(action='DESELECT')
    for obj in [root]+model_meshes:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = root
    bpy.ops.export_scene.fbx(filepath=str(game/(key+'_Finished.fbx')),use_selection=True,
                            object_types={'EMPTY','MESH'},use_mesh_modifiers=True,
                            add_leaf_bones=False,axis_forward='-Z',axis_up='Y',
                            apply_unit_scale=True,bake_space_transform=False,
                            mesh_smooth_type='FACE',use_tspace=True,path_mode='AUTO')
    manifest = {'materials':manifest_materials,'model':key,'bounds':bounds,
                'reference':'用户附图；不包含稀有度圆环；背面及内部为单视图推定',
                'meshCount':len(model_meshes),'units':'m','sourceAxes':'Z up, front -Y'}
    (game/'material_manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
    cam.location = (2.35,-4.2,3.15) if key == 'PolymerizationPreparation' else (2.0,-4.3,3.1)
    cam.rotation_euler = (-cam.location).to_track_quat('-Z','Y').to_euler()
    scene.render.filepath = str(source/(key+'_Preview.png'))
    bpy.ops.wm.save_as_mainfile(filepath=str(source/(key+'_Finished.blend')),compress=True)
    bpy.ops.render.render(write_still=True)
    cam.location = (4.2,-1.5,1.4)
    cam.rotation_euler = (-cam.location).to_track_quat('-Z','Y').to_euler()
    scene.render.filepath = str(source/(key+'_SidePreview.png'))
    bpy.ops.render.render(write_still=True)
    print('DEVICE_MODEL_DONE', key, len(model_meshes), bounds)

requested = sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else []
for key,build in [('BipolarNanoflake',bipolar),('PolymerizationPreparation',polymer)]:
    if requested and key not in requested:
        continue
    current_key = key
    model_meshes = []
    manifest_materials = {}
    bpy.ops.wm.read_factory_settings(use_empty=True)
    build()
    save_and_render(key)
