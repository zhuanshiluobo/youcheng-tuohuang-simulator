"""独立作者：完整三棱锥内光学返工。保留旧版，真实透明壳和内部有限镜片。
Unity shader 和 Blender 使用同一组源坐标边界与镜片；不存在表面碎镜纹理。
"""
import bpy, bmesh, json, math, random, shutil
from pathlib import Path
from mathutils import Vector

ROOT=Path('F:/游城拓荒模拟器')
KEY='RephasicEnantiomer'
ART=ROOT/'ArtSource/Materials'/KEY
GAME=ROOT/'Assets/YC/Presentation/Models'/KEY
random.seed(7807)
bpy.context.preferences.filepaths.save_version=0
print('OPTICAL_SOURCE_INSPECT',bpy.app.version_string,bpy.data.filepath,
      [(s.name,len(s.objects)) for s in bpy.data.scenes])
for src,dst in [(ART/(KEY+'_Finished.blend'),ART/(KEY+'_BeforeOpticalRevision.blend')),
                (GAME/(KEY+'_Finished.fbx'),ART/(KEY+'_BeforeOpticalRevision.fbx')),
                (GAME/'material_manifest.json',ART/'material_manifest_BeforeOpticalRevision.json')]:
    if src.exists() and not dst.exists(): shutil.copy2(src,dst)
old=bpy.context.scene
old.name=KEY+'_SurfaceMirrorThirdVersion_Preserved'
oldroot=bpy.data.objects.get(KEY)
if oldroot: oldroot.name=KEY+'_SurfaceMirrorThirdVersion'
scene=bpy.data.scenes.new(KEY+'_Finished')
bpy.context.window.scene=scene
scene.unit_settings.system='METRIC'
scene.unit_settings.scale_length=1
root=bpy.data.objects.new(KEY,None)
scene.collection.objects.link(root)
root['说明']='完整等边三角底三棱锥；内部有限金银镜片形成多跳折射/反射。壳表面没有马赛克、碎镜贴片或照明纹理。'

def lin(v): return v/12.92 if v<=.04045 else ((v+.055)/1.055)**2.4
def material(name,color,metallic=0,rough=.2,trans=0,ior=1.52):
    m=bpy.data.materials.new(name);m.use_nodes=True
    p=next(n for n in m.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    rgba=tuple(lin(v) for v in color)+(1,)
    p.inputs['Base Color'].default_value=rgba
    p.inputs['Metallic'].default_value=metallic
    p.inputs['Roughness'].default_value=rough
    p.inputs['Transmission Weight'].default_value=trans
    p.inputs['IOR'].default_value=ior
    m.diffuse_color=rgba
    m['export_srgb']=list(color)+[1];m['export_metallic']=metallic;m['export_smoothness']=1-rough
    return m

glass=material('RE4_OpticalGlassShell',(.995,.995,.99),0,.012,1,1.52)
silver=material('RE4_InternalSilverMirror',(.94,.95,.94),1,.025)
gold=material('RE4_InternalGoldMirror',(.96,.77,.34),1,.026)
pale=material('RE4_InternalPaleGoldMirror',(.99,.90,.65),1,.03)
bronze=material('RE4_InternalBronzeMirror',(.57,.38,.16),1,.04)
mirror_mats=[silver,gold,pale,bronze]
white=material('RE4_BlockoutWhite',(.69,.69,.69),0,.6)

def mesh(name,verts,faces,mat):
    data=bpy.data.meshes.new(name+'_Mesh');data.from_pydata(verts,[],faces);data.update()
    bm=bmesh.new();bm.from_mesh(data);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(data);bm.free()
    obj=bpy.data.objects.new(name,data);scene.collection.objects.link(obj);obj.parent=root;data.materials.append(mat)
    return obj

# 只复制原版的低承台实体。旧版碎镜、表面马赛克和前侧展片完全不进入新成品。
prefixes=('低多边深色底座','底座金色细夹层','低多边银镜托面','顶面暗金晶体承台')
for obj in old.objects:
    if any(obj.name.startswith(prefix) for prefix in prefixes):
        cp=obj.copy();cp.data=obj.data.copy();cp.name='RE4_'+obj.name.split('.')[0]
        scene.collection.objects.link(cp);cp.parent=root
        for slot in cp.material_slots:
            slot.material=slot.material.copy()
            slot.material.name='RE4_'+slot.material.name.removesuffix('.001')

A=Vector((-.96,-.554,.24));B=Vector((.96,-.554,.24));C=Vector((0,1.109,.24));T=Vector((0,0,1.74))
vertices=[A,B,C,T]
tri_faces=[(0,1,3),(1,2,3),(2,0,3),(0,2,1)]
shell=mesh('完整透明三棱锥光学壳',vertices,tri_faces,glass)
shell['光学']='闭合四个平面，IOR=1.52；Unity 多跳解析光线与 Cycles 物理折射均在壳内。'
planes=[]
center=sum(vertices,Vector())/4
for face in shell.data.polygons:
    p=Vector(shell.data.vertices[face.vertices[0]].co)
    n=Vector(face.normal).normalized()
    assert n.dot(center-p)<0, '法线须朝外'
    planes.append(list(n)+[n.dot(p)])
# FBX 轴转换只影响 vertex 和 normal。额外 UV 按原始 Blender 源坐标存储。
# UV0 留普通三角坐标；UV1=源xy；UV2=源z和边界面编号。
uv0=shell.data.uv_layers.new(name='UVMap')
uvxy=shell.data.uv_layers.new(name='OpticalSourceXY')
uvzf=shell.data.uv_layers.new(name='OpticalSourceZFace')
for face in shell.data.polygons:
    for k,li in enumerate(face.loop_indices):
        p=shell.data.vertices[shell.data.loops[li].vertex_index].co
        uv0.data[li].uv=[(0,0),(1,0),(0,1)][k]
        uvxy.data[li].uv=(p.x,p.y);uvzf.data[li].uv=(p.z,float(face.index))

# 镜片位于光学体积内部，各片有真实厚度；不接触壳，不投到表面。
# 以随机四面体重心坐标严格限制在内部，三种尺寸带来有限遮挡与纵深。
mirrors=[]
for i in range(36):
    zratio=.10+.71*random.random()
    weights=[random.random()+.36 for _ in range(3)]
    weights=[w/sum(weights)*(1-zratio) for w in weights]
    pc=A*weights[0]+B*weights[1]+C*weights[2]+T*zratio
    n=Vector((random.uniform(-1,1),random.uniform(-1,1),random.uniform(-.8,.8))).normalized()
    tangent=n.cross(Vector((0,0,1)) if abs(n.z)<.85 else Vector((0,1,0))).normalized()
    bitangent=n.cross(tangent)
    size=[.17,.105,.062][i%3]
    scale=1
    points=[]
    for k in range(3):
        angle=2*math.pi*k/3+random.uniform(-.3,.3)
        points.append(pc+(tangent*math.cos(angle)+bitangent*math.sin(angle))*size*random.uniform(.65,1.12))
    # 面外最多保留距离界面 .018 的安全余量，直到整个镜片都在四面体内。
    while any(Vector(pl[:3]).dot(p)>pl[3]-.018 for pl in planes for p in points):
        scale*=.8;points=[pc+(p-pc)*.8 for p in points]
        if scale<.08: break
    kind=random.choices(range(4),[38,32,24,6],k=1)[0]
    normal=(points[1]-points[0]).cross(points[2]-points[0]).normalized()
    half=.0015
    pts=[p+normal*half for p in points]+[p-normal*half for p in points]
    obj=mesh('内部有限反射镜片_%02d'%i,pts,[(0,1,2),(5,4,3),(0,3,4,1),(1,4,5,2),(2,5,3,0)],mirror_mats[kind])
    obj['说明']='内部实体镜片；位置、边界及颜色与专用 shader 常量一致。'
    mirrors.append({'points':[list(p) for p in points],'reflectance':list(mirror_mats[kind]['export_srgb'])[:3]})

# 独立白模场景保留真实三棱锥与承台；可从任意方向核查轮廓及厚度。
block=bpy.data.scenes.new(KEY+'_OpticalBlockout')
mapping={}
for obj in list(scene.objects):
    cp=obj.copy()
    if obj.data:
        cp.data=obj.data.copy();cp.data.materials.clear();cp.data.materials.append(white)
        for p in cp.data.polygons:p.material_index=0
    block.collection.objects.link(cp);mapping[obj]=cp
for obj,cp in mapping.items(): cp.parent=mapping.get(obj.parent)

world=bpy.data.worlds.new('RE4_中性光学棚拍');world.use_nodes=True;scene.world=world
bg=next(n for n in world.node_tree.nodes if n.type=='BACKGROUND')
bg.inputs['Color'].default_value=(.075,.075,.075,1);bg.inputs['Strength'].default_value=.5
def area(name,loc,energy,size,scale,target=(0,0,.8)):
    data=bpy.data.lights.new(name,'AREA');data.energy=energy;data.shape='RECTANGLE';data.size=size;data.size_y=size*scale
    obj=bpy.data.objects.new(name,data);scene.collection.objects.link(obj);obj.location=loc
    obj.rotation_euler=(Vector(target)-obj.location).to_track_quat('-Z','Y').to_euler()
area('RE4_主反射卡_不导出',(1,-3,4.5),700,3,.25)
area('RE4_银反射卡_不导出',(-3,-1,2.5),480,2,.7)
area('RE4_背侧反射卡_不导出',(1,3,3.5),850,2.8,.4)
groundmat=material('RE4_PreviewGround',(.18,.19,.20),0,.75)
ground=mesh('RE4_预览地板_不导出',[(-200,-200,0),(200,-200,0),(200,200,0),(-200,200,0)],[(0,1,2,3)],groundmat)
ground.parent=None
camdata=bpy.data.cameras.new('RE4_预览相机_不导出');cam=bpy.data.objects.new('RE4_预览相机_不导出',camdata)
scene.collection.objects.link(cam);scene.camera=cam;camdata.type='ORTHO';camdata.ortho_scale=2.42
target=Vector((0,0,.89))
views={'OpticalFront':(2.8,-6,3.8),'OpticalSide':(6.5,0,3.0),'OpticalBack':(0,6.5,3.4),'OpticalQuarter':(-5,-5,3.8)}
def camera(loc):cam.location=loc;cam.rotation_euler=(target-cam.location).to_track_quat('-Z','Y').to_euler()
camera(views['OpticalFront'])
scene.render.engine='CYCLES';scene.cycles.samples=72;scene.cycles.use_denoising=True
scene.cycles.max_bounces=18;scene.cycles.transmission_bounces=14;scene.cycles.glossy_bounces=12
scene.cycles.transparent_max_bounces=12
scene.render.resolution_x=800;scene.render.resolution_y=800;scene.render.resolution_percentage=100
scene.view_settings.view_transform='AgX'
bpy.context.view_layer.update()
meshes=[o for o in scene.objects if o.type=='MESH' and o.parent==root]
corners=[o.matrix_world@Vector(p) for o in meshes for p in o.bound_box]
lo=[min(p[i] for p in corners) for i in range(3)];hi=[max(p[i] for p in corners) for i in range(3)]
manifest={'model':KEY,'materials':{},'bounds':{'min':lo,'max':hi,'size':[hi[i]-lo[i] for i in range(3)]},
 'front':'-Y','unit':'meter','reference_camera':{'position':list(views['OpticalFront']),'target':list(target)},
 'source_reference':'用户附图；https://prts.wiki/w/重相位对映体；内部镜片几何和背侧为推定。',
 'pyramid_vertices':dict(zip(['A','B','C','Tip'],map(list,vertices))),
 'revision':'第四版：真实透明三棱锥、36 个内部有限实体金银镜片；没有表面碎镜马赛克；随视角追踪入口折射、内部反射和出口折射',
 'optics':{'shader':'YC/Collection/RephasicOpticalPyramid','shader_path':'Assets/YC/Presentation/Models/RephasicEnantiomer/RephasicOpticalPyramid.shader',
  'cube_property':'_OpticalCube','cube_asset':'Assets/YC/Presentation/Models/D32Steel/D32SteelStudioReflection.asset',
  'IOR':1.52,'max_bounces':8,'coordinate_uv_channels':{'source_xy':1,'source_z_face_id':2},
  'planes':planes,'finite_internal_mirrors':mirrors,'render_notes':'Unity 为八跳有限三角镜片/四界面解析近似；Blender Cycles 为真实几何折射。不是表面纹理或二维碎镜图案。'}}
for m in {m.name:m for o in meshes for m in o.data.materials}.values():
    props={'color':list(m['export_srgb']),'metallic':m['export_metallic'],'smoothness':m['export_smoothness'],'transparent':False}
    if m==glass:props.update({'shader':manifest['optics']['shader'],'IOR':1.52,'CubeIntensity':.95,'Exposure':1.18})
    manifest['materials'][m.name]=props
(GAME/'material_manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')

def fv(values):return 'float%d('%len(values)+','.join('%.8f'%v for v in values)+')'
constants='static const float4 opticalPlanes[4]={'+','.join(fv(p) for p in planes)+'};\n'
for channel in range(3):
    constants+='static const float3 mirror%s[36]={'%('ABC'[channel])+','.join(fv(m['points'][channel]) for m in mirrors)+'};\n'
constants+='static const float3 mirrorTint[36]={'+','.join(fv([lin(v) for v in m['reflectance']]) for m in mirrors)+'};\n'
template=(ART/'RephasicOpticalPyramid.template.txt').read_text(encoding='utf-8')
(GAME/'RephasicOpticalPyramid.shader').write_text(template.replace('__OPTICAL_GEOMETRY__',constants),encoding='utf-8')

bpy.ops.object.select_all(action='DESELECT');root.select_set(True)
for obj in meshes:obj.select_set(True)
bpy.context.view_layer.objects.active=root
bpy.ops.export_scene.fbx(filepath=str(GAME/(KEY+'_Finished.fbx')),use_selection=True,
 object_types={'EMPTY','MESH'},apply_unit_scale=True,apply_scale_options='FBX_SCALE_UNITS',axis_forward='-Z',axis_up='Y',
 use_mesh_modifiers=True,mesh_smooth_type='FACE',use_tspace=False,add_leaf_bones=False,bake_anim=False,path_mode='AUTO')
bpy.ops.wm.save_as_mainfile(filepath=str(ART/(KEY+'_Finished.blend')))
for name,loc in views.items():
    camera(loc);scene.render.filepath=str(ART/(KEY+'_'+name+'.png'));bpy.ops.render.render(write_still=True)
scene.view_layers[0].material_override=white
for name,loc in list(views.items())[:3]:
    camera(loc);scene.render.filepath=str(ART/(KEY+'_'+name+'_Blockout.png'));bpy.ops.render.render(write_still=True)
scene.view_layers[0].material_override=None
camera(views['OpticalFront']);bpy.ops.wm.save_as_mainfile(filepath=str(ART/(KEY+'_Finished.blend')))
print('OPTICAL_MODEL_READY',KEY,'meshes',len(meshes),'internal_mirrors',len(mirrors),'planes',planes,'bounds',manifest['bounds'])
