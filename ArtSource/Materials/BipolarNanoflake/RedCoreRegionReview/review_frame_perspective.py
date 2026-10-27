"""在已修正方框的审阅版本上，校正共同轴转角和相对尺寸。"""
import bpy, math, json, sys
from pathlib import Path
from mathutils import Matrix, Vector, Quaternion

OUT = Path('F:/游城拓荒模拟器/ArtSource/Materials/BipolarNanoflake/RedCoreRegionReview/PerspectiveReview')
source_scene = bpy.data.scenes['BipolarNanoflake_SquareFrameReview']
bpy.context.window.scene = source_scene
bpy.ops.scene.new(type='FULL_COPY')
scene = bpy.context.scene
scene.name = 'BipolarNanoflake_PerspectiveReview'
root = next(o for o in scene.objects if o.name.startswith('BipolarNanoflake_SquareReview_Root'))
root.name = 'BipolarNanoflake_PerspectiveReview_Root'
meshes = [o for o in root.children_recursive if o.type == 'MESH']
frames = sorted([o for o in meshes if o.name.startswith('BNF_铜框_')], key=lambda o:o.name)
core = next(o for o in meshes if o.name.startswith('BNF_一体红盘'))
horizontal = next(o for o in meshes if o.name.startswith('BNF_横向'))
vertical = next(o for o in meshes if o.name.startswith('BNF_纵向'))
original = {o.name:[v.co.copy() for v in o.data.vertices] for o in meshes}
original_sizes = {o.name:list(o['完成尺寸']) for o in frames}
basis = frames[0].matrix_world.to_3x3().normalized() @ Matrix.Rotation(math.radians(33),3,'X')
baseline_angle = math.degrees(basis.to_euler().x)
axis = (basis @ Vector((1,0,0))).normalized()
center = root.matrix_world.translation.copy()
camera = scene.camera
# 转角改变后保留红盘的漫反射渐变，降低盘面无色反射，避免把红盘照成灰白。
core_red=core.data.materials[0].copy()
core_red.name='BipolarNanoflake_审阅_红盘柔和反光'
core.data.materials[0]=core_red
red_bsdf=next(n for n in core_red.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
red_bsdf.inputs['Roughness'].default_value=.56
red_bsdf.inputs['Specular IOR Level'].default_value=.12
scene.render.engine = 'CYCLES'
scene.cycles.use_denoising = True
scene.render.film_transparent = True
scene.render.image_settings.file_format = 'PNG'
scene.render.image_settings.color_mode = 'RGBA'

# 数值为实体正方形边长、绝对扇形方向；所有平面绕同一局部X轴。
variants = {
    'D':dict(edges=[1.92,1.66,1.42,1.56,1.78],
             angles=[baseline_angle-60,baseline_angle-74,baseline_angle-91,baseline_angle+50,baseline_angle-60],
             camera=[.19,-5.3,5],roll=14,
             ring_angles=[baseline_angle-102,baseline_angle-28],radii=[1.035,1.035]),
    'A':dict(edges=[1.92,1.62,1.58,1.40,1.78],angles=[0,10,-15,40,0],
             camera=[2.4,-7.5,4.5],roll=3,ring_angles=[-29,64],radii=[1.075,1.04]),
    'B':dict(edges=[1.92,1.62,1.58,1.43,1.78],angles=[-2,0,-18,24,-2],
             camera=[3,-7,5],roll=0,ring_angles=[-35,60],radii=[1.075,1.04]),
    'C':dict(edges=[1.92,1.68,1.62,1.46,1.78],angles=[3,14,-10,-55,3],
             camera=[2.8,-7,4.5],roll=2,ring_angles=[-33,64],radii=[1.075,1.04]),
}
variants['E']={**variants['D'],'backlight':[0,4.7,2.1]}
variants['F']={**variants['E'],
    'edges':[1.92,1.66,1.46,1.58,1.78],
    'angles':[baseline_angle-70,baseline_angle-49,baseline_angle-91,baseline_angle+50,baseline_angle-70],
    'ring_angles':[baseline_angle-98,baseline_angle-28]}
variants['G']={**variants['F'],'keylight':[-.3,.4,-4]}
variants['H']={**variants['G'],
    'edges':[1.92,1.60,1.46,1.66,1.78],
    'angles':[baseline_angle-70,baseline_angle-49,baseline_angle-91,baseline_angle+41,baseline_angle-70],
    'cross_rail':.055,'cross_depth':.052,'keylight':[0,-1.15,-3.83]}

def orient(obj,angle):
    relative = angle-baseline_angle
    obj.rotation_euler = (basis @ Matrix.Rotation(math.radians(relative),3,'X')).to_euler()
    obj.location = center
    obj['绕共同轴角度'] = relative
    obj['共同轴方向'] = list(axis)

def apply_variant(spec):
    for index,(obj,edge,angle) in enumerate(zip(frames,spec['edges'],spec['angles'])):
        previous = original_sizes[obj.name]
        factor = edge/previous[0]
        for v,co in zip(obj.data.vertices,original[obj.name]):
            v.co = (co.x*factor,co.y*factor,co.z)
        if index==3 and 'cross_rail' in spec:
            rail=spec['cross_rail'];depth=spec['cross_depth']
            for v in obj.data.vertices:
                if abs(abs(v.co.x)-edge/2)>.00001:v.co.x=math.copysign(edge/2-rail,v.co.x)
                if abs(abs(v.co.y)-edge/2)>.00001:v.co.y=math.copysign(edge/2-rail,v.co.y)
                v.co.z=math.copysign(depth/2,v.co.z)
        else:rail=previous[2]*factor;depth=previous[3]
        obj.data.update()
        obj['完成尺寸'] = [edge,edge,rail,depth]
        obj['框条宽度'] = rail
        obj['框条厚度'] = depth
        obj['结构说明'] = '实体正方形框；统一红铜，高光由真实光照形成。'
        orient(obj,angle)
    orient(core,spec['angles'][0])
    for obj,angle,radius in zip([horizontal,vertical],spec['ring_angles'],spec['radii']):
        factor = radius/1.075
        for v,co in zip(obj.data.vertices,original[obj.name]):v.co = co*factor
        obj.data.update()
        obj['圆环中心线半径'] = radius
        obj['线径'] = .016*factor
        orient(obj,angle)
    camera.data.type = 'PERSP'
    camera.data.lens = 100
    camera.location = Vector(spec['camera']).normalized()*7.6
    camera.rotation_euler = ((center-camera.location).to_track_quat('-Z','Y') @ Quaternion((0,0,1),math.radians(spec['roll']))).to_euler()
    if 'backlight' in spec:
        light=next(o for o in scene.objects if o.type=='LIGHT' and o.name.startswith('仅预览_背光'))
        light.location=spec['backlight']
        light.rotation_euler=(center-light.location).to_track_quat('-Z','Y').to_euler()
    if 'keylight' in spec:
        light=next(o for o in scene.objects if o.type=='LIGHT' and o.name.startswith('仅预览_主光'))
        light.location=spec['keylight']
        light.rotation_euler=(center-light.location).to_track_quat('-Z','Y').to_euler()
    bpy.context.view_layer.update()

def render(label,resolution=640,samples=20):
    scene.render.resolution_x=resolution;scene.render.resolution_y=resolution
    scene.render.resolution_percentage=100;scene.cycles.samples=samples
    scene.render.filepath = str(OUT/('双极纳米片_透视修正_'+label+'.png'))
    bpy.ops.render.render(write_still=True)

arguments=sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else []
if arguments and arguments[0]=='preview':
    for name in arguments[1:]:
        apply_variant(variants[name]);render('候选'+name)
        print('PERSPECTIVE_CANDIDATE',name,flush=True)
elif not arguments:
    for name,spec in variants.items():
        apply_variant(spec)
        render('候选'+name)
        print('PERSPECTIVE_CANDIDATE',name,flush=True)
else:
    name=arguments[0];spec=variants[name];apply_variant(spec)
    root['验收状态']='待用户人工验收，未替换Unity'
    root['外框说明']='五层实体正方形，围绕共同轴校正角度和尺寸；无固定白色条带。'
    scene['修正说明']='校正方框扇形投影、各层相对大小和双环角度；保留先前各阶段场景。'
    render('整体',1000,48)
    main_camera_location=camera.location.copy();main_camera_rotation=camera.rotation_euler.copy()
    camera.data.lens=270
    render('核心近景',900,40)
    camera.data.lens=100
    camera.data.type='ORTHO';camera.data.ortho_scale=2.50
    camera.location=axis*6
    camera.rotation_euler=(-camera.location).to_track_quat('-Z','Y').to_euler()
    render('同轴侧视',900,32)
    hide={o:o.hide_render for o in meshes}
    for o in meshes:o.hide_render=(o!=frames[0])
    normal=(frames[0].matrix_world.to_3x3().normalized() @ Vector((0,0,1))).normalized()
    camera.location=normal*6
    camera.rotation_euler=frames[0].matrix_world.to_3x3().normalized().to_euler()
    camera.data.ortho_scale=2.30
    render('单框正视',900,32)
    for o,value in hide.items():o.hide_render=value
    camera.data.type='PERSP';camera.location=-main_camera_location
    camera.rotation_euler=(-camera.location).to_track_quat('-Z','Y').to_euler()
    render('背面',1000,32)
    camera.location=main_camera_location;camera.rotation_euler=main_camera_rotation
    scene.cycles.samples=48
    root.select_set(True)
    for o in meshes:o.select_set(False)
    bpy.context.view_layer.objects.active=root
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type=='VIEW_3D':
                space=area.spaces.active;space.shading.type='MATERIAL'
                space.region_3d.view_rotation=camera.rotation_euler.to_quaternion()
                space.region_3d.view_location=center;space.region_3d.view_distance=4
                space.region_3d.view_perspective='CAMERA'
    blend=OUT/'BipolarNanoflake_Perspective_Review.blend'
    fbx=OUT/'BipolarNanoflake_Perspective_Review.fbx'
    bpy.ops.object.select_all(action='DESELECT')
    for o in [root]+meshes:o.select_set(True)
    bpy.ops.export_scene.fbx(filepath=str(fbx),use_selection=True,object_types={'EMPTY','MESH'},
        use_mesh_modifiers=True,add_leaf_bones=False,axis_forward='-Z',axis_up='Y',
        apply_unit_scale=True,bake_space_transform=False,mesh_smooth_type='FACE',use_tspace=True,path_mode='STRIP')
    bpy.ops.object.select_all(action='DESELECT');root.select_set(True)
    bpy.context.view_layer.objects.active=root
    manifest={'status':'待用户人工验收；未替换Unity','variant':name,'parameters':spec,
        'sharedCenter':list(center),'sharedAxis':list(axis),'reviewBlend':str(blend),'reviewFbx':str(fbx),
        'frames':[dict(name=o.name,dimensions=list(o['完成尺寸']),center=list(o.matrix_world.translation),
            axis=list((o.matrix_world.to_3x3().normalized()@Vector((1,0,0))).normalized()),
            angleAroundCommonAxis=o['绕共同轴角度'],materials=[m.name for m in o.data.materials]) for o in frames],
        'coreMaterial':{'name':core_red.name,'roughness':.56,'specularIORLevel':.12,
            'gradientPreserved':True,'materialEmissionUnchanged':True},
        'camera':{'type':'PERSP','lens':100,'position':list(main_camera_location),'roll':spec['roll']},
        'backside':'单张参考图的背面按已有薄片结构合理推定。'}
    (OUT/'perspective_review_manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
    bpy.ops.wm.save_as_mainfile(filepath=str(blend),compress=True)
    print('PERSPECTIVE_REVIEW_COMPLETE',str(blend),flush=True)
