"""在现有聚合剂上细化；原始白模、历史文件与当前 Blender 会话不变。"""
import bpy, math, json, shutil
from pathlib import Path
from mathutils import Vector, Matrix

ART = Path('F:/游城拓荒模拟器/ArtSource/Materials/PolymerizationPreparation')
GAME = Path('F:/游城拓荒模拟器/Assets/YC/Presentation/Models/PolymerizationPreparation')
OUT = ART / 'ReferenceRefinement'
OUT.mkdir(exist_ok=True)
bpy.ops.wm.open_mainfile(filepath=str(ART / 'PolymerizationPreparation_Finished.blend'))
scene = bpy.data.scenes['PolymerizationPreparation_Finished']
bpy.context.window.scene = scene
root = scene.objects['PolymerizationPreparation_Root']
manifest = json.loads((GAME / 'material_manifest.json').read_text(encoding='utf8'))
# 只读取既有源文件；细化版本另存，旧导出可回退。
for src in [GAME/'PolymerizationPreparation_Finished.fbx', GAME/'material_manifest.json',
            GAME.parent.parent/'Collection/Prefabs/PolymerizationPreparationDisplay.prefab']:
    dest = OUT / ('BeforeRefinement_' + src.name)
    if src.exists() and not dest.exists(): shutil.copy2(src, dest)

prefix = 'PolymerizationPreparation_'
M = {k[len(prefix):]: bpy.data.materials[k] for k in manifest['materials'] if k in bpy.data.materials}
def setmat(key, color, metal, rough, transmission=0):
    mat=M[key]
    p=next(n for n in mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    linear=lambda v: v/12.92 if v<=.04045 else ((v+.055)/1.055)**2.4
    p.inputs['Base Color'].default_value=tuple(linear(v) for v in color[:3])+(1,)
    p.inputs['Metallic'].default_value=metal
    p.inputs['Roughness'].default_value=rough
    p.inputs['Alpha'].default_value=color[3]
    p.inputs['Transmission Weight'].default_value=transmission
    mat.diffuse_color=color
    manifest['materials'][prefix+key].update(color=list(color),metallic=metal,smoothness=1-rough)
    return mat
setmat('金属银边',(.65,.74,.77,1),.78,.25)
setmat('青灰托盘内衬',(.16,.29,.31,1),.42,.39)
setmat('青色聚合层板',(.015,.62,.69,1),.04,.20,.12)
setmat('青蓝透光盖板',(.70,.90,.94,.22),0,.09,1)
# Blender 采用真实折射玻璃，Unity 使用对应透明表面；避免半透明混合造成乳白。
glass_node=next(n for n in M['青蓝透光盖板'].node_tree.nodes if n.type=='BSDF_PRINCIPLED')
glass_node.inputs['Alpha'].default_value=1
glass_node.inputs['IOR'].default_value=1.45
setmat('层板亮边',(.17,.77,.81,1),.12,.22)
setmat('黄绿标签',(.45,.79,.04,1),0,.52)
for key in ['层板亮边']:
    next(n for n in M[key].node_tree.nodes if n.type=='BSDF_PRINCIPLED').inputs['Emission Strength'].default_value=0
    manifest['materials'][prefix+key].pop('emission',None)

# 保持原归一化尺度和中心，细节用源尺寸定义。
front = scene.objects['前侧横置管_银灰封帽']
factor = scene.objects['前黄绿长标签'].dimensions.x / 1.10
offset = front.location - Vector((-.49,-.39,.046))*factor
def world(p): return Vector(p)*factor+offset
def unlink(o):
    for c in list(o.users_collection): c.objects.unlink(o)
def finish(o,name,mat,bevel=0):
    o.name=name; o.data.name=name+'_Mesh'; o.data.materials.append(mat); o.parent=root
    if bevel:
        mod=o.modifiers.new('精细倒角','BEVEL'); mod.width=bevel*factor; mod.segments=3
        mod=o.modifiers.new('加权法线','WEIGHTED_NORMAL'); mod.keep_sharp=True
    return o
def cube(name,p,dims,mat,bevel=.001):
    bpy.ops.mesh.primitive_cube_add(size=1,location=world(p)); o=bpy.context.object
    o.dimensions=Vector(dims)*factor
    bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
    return finish(o,name,mat,bevel)
def lathe(name,profile,a,q,mat,existing=None):
    N=48; verts=[]; faces=[]
    for z,r in profile:
        verts.extend([a+q@Vector((r*math.cos(j*math.tau/N),r*math.sin(j*math.tau/N),z)) for j in range(N)])
    for k in range(len(profile)-1):
        for j in range(N):
            h=(j+1)%N; faces.append((k*N+j,k*N+h,(k+1)*N+h,(k+1)*N+j))
    mesh=bpy.data.meshes.new(name+'_Mesh'); mesh.from_pydata(verts,[],faces); mesh.update()
    if existing:
        o=existing; o.data=mesh; o.matrix_world=Matrix.Identity(4)
        o.modifiers.clear(); o.data.materials.append(mat)
    else:
        o=bpy.data.objects.new(name,mesh); scene.collection.objects.link(o); finish(o,name,mat)
    for p in mesh.polygons:p.use_smooth=True
    return o

# 第二根以右前管底为支点翘起，封帽端抬高约 17 度。
middle_a=scene.objects['中部斜置管_银灰封帽'].location.copy()
middle_center=scene.objects['中部斜置管_透明玻璃套管'].location.copy()
middle_b=middle_center*2-middle_a
old_axis=(middle_a-middle_b).normalized()
new_axis=(old_axis*math.cos(math.radians(17))+Vector((0,0,1))*math.sin(math.radians(17))).normalized()
tilt=old_axis.rotation_difference(new_axis).to_matrix().to_4x4()
tilt=Matrix.Translation(middle_b)@tilt@Matrix.Translation(-middle_b)
for ob in list(scene.objects):
    if ob.name.startswith('中部斜置管_'):ob.matrix_world=tilt@ob.matrix_world
bpy.context.view_layer.update()
for name in ['前侧横置管','中部斜置管','后部斜立管']:
    cap=scene.objects[name+'_银灰封帽']
    shell=scene.objects[name+'_透明玻璃套管']
    a=cap.location.copy(); direction=(shell.location-a).normalized()
    L=(shell.location-a).length*2
    q=direction.to_track_quat('Z','Y')
    r=(.070 if name.startswith('后') else .065 if name.startswith('中') else .063)*factor
    # 闭合的双层试管壁，圆弧封底，顶部环口由封帽遮蔽。
    end=L-r
    profile=[(0,r),(end,r)]
    for i in range(1,13):
        t=i*math.pi/24; profile.append((end+r*math.sin(t),max(r*math.cos(t),.00001)))
    ri=r*.86
    profile.append((end+ri,.00001))
    for i in range(11,-1,-1):
        t=i*math.pi/24;profile.append((end+ri*math.sin(t),max(ri*math.cos(t),.00001)))
    profile.extend([(0,ri),(0,r)])
    lathe(name+'_透明玻璃套管',profile,a,q,M['青蓝透光盖板'],shell)
    # 聚合液与外玻璃之间保留间隙，底部随玻璃内腔圆润封闭。
    lr=r*.76; start=L*.29
    fluid=[(start,.00001),(start-.003*factor,lr*.80),(start,lr),(end,lr)]
    for i in range(1,13):
        t=i*math.pi/24;fluid.append((end+lr*math.sin(t),max(lr*math.cos(t),.00001)))
    lathe(name+'_青蓝聚合液',fluid,a,q,M['青色聚合层板'],scene.objects[name+'_青蓝聚合液'])
    unlink(scene.objects[name+'_玻璃底部厚圈'])
    men=scene.objects[name+'_液面反光']; men.location=a+direction*start
    # 封帽顶部为银灰实面、细环和小凹口，避免黑洞般的敞口视觉。
    top=scene.objects[name+'_帽顶内凹']; top.data.materials.clear(); top.data.materials.append(M['金属银边'])
    for j in range(2):
        z=(-.013+j*.024)*factor
        lathe(name+'_封帽环槽_'+str(j),[(z,r*1.08),(z+.003*factor,r*1.08)],a,q,M['凹槽黑'])
    lathe(name+'_帽顶同心压环',[(-.041*factor,r*.42),(-.041*factor,r*.69)],a,q,M['凹槽黑'])
    # 低对比细刻度，贴在上半玻璃壁上，不使用发光条。
    for j in range(5):
        z=L*(.37+j*.095); ang=.15
        # 弧段刻度采用真实窄条面。
        v=[]
        for dz in [0,.003*factor]:
            for k in range(9):
                t=ang+k*.085;v.append(a+q@Vector((r*1.006*math.cos(t),r*1.006*math.sin(t),z+dz)))
        me=bpy.data.meshes.new(name+'_刻度');me.from_pydata(v,[],[(k,k+1,k+10,k+9) for k in range(8)]);me.update()
        ob=bpy.data.objects.new(name+'_刻度_'+str(j),me);scene.collection.objects.link(ob);finish(ob,ob.name,M['金属银边'])

# 三组独立夹架：两端 C 形卡箍、两根细长侧梁和端部连接耳。
# 去掉旧方块支座及后管中腰抱箍，避免与参考新增夹架重叠。
for name in ['前管左夹','前管右夹','中管左夹','中管右夹','后管底部卡座',
             '后管背框接片','后管背侧薄抱箍','后管支撑短连线']:
    ob=scene.objects.get(name)
    if ob:unlink(ob)
def rod(name,p1,p2,r,mat):
    p1,p2=Vector(p1),Vector(p2);d=p2-p1
    return lathe(name,[(0,r),(d.length,r)],p1,d.to_track_quat('Z','Y'),mat)
for name in ['前侧横置管','中部斜置管','后部斜立管']:
    shell=scene.objects[name+'_透明玻璃套管']
    a=scene.objects[name+'_银灰封帽'].location.copy()
    # 圆底最末一点在源数据中的位置，沿原管轴取得端点。
    capaxis=scene.objects[name+'_封帽亮沿'].location-a
    d=capaxis.normalized()
    L=max((v.co-a).dot(d) for v in shell.data.vertices)
    r=(.070 if name.startswith('后') else .065 if name.startswith('中') else .063)*factor
    up=Vector((0,0,1));up=(up-d*up.dot(d)).normalized()
    side=d.cross(up).normalized()
    ends=[a+d*(L*.105),a+d*(L*.875)]
    for idx,c in enumerate(ends):
        N=40;verts=[];faces=[];width=.036*factor
        # 开口在上方；两端夹爪包住管身约 300 度。
        for zz,rr in [(-width/2,r*1.016),(width/2,r*1.016),(-width/2,r*1.16),(width/2,r*1.16)]:
            for j in range(N+1):
                t=math.radians(31+298*j/N)
                verts.append(c+d*zz+(up*math.cos(t)+side*math.sin(t))*rr)
        K=N+1
        for j in range(N):
            faces.extend([(j,j+1,K+j+1,K+j),(2*K+j,3*K+j,3*K+j+1,2*K+j+1),
                          (j,2*K+j,2*K+j+1,j+1),(K+j,K+j+1,3*K+j+1,3*K+j)])
        faces.extend([(0,K,3*K,2*K),(N,2*K+N,3*K+N,K+N)])
        me=bpy.data.meshes.new(name+'_端夹');me.from_pydata(verts,[],faces);me.update()
        ob=bpy.data.objects.new(name+'_端部卡箍_'+str(idx+1),me);scene.collection.objects.link(ob);finish(ob,ob.name,M['金属银边'],.0015)
        for pp in me.polygons:pp.use_smooth=True
        # 两侧夹爪外伸短耳，让夹持构件区别于管盖装饰环。
        for sign in [-1,1]:
            u=up*.65+side*(sign*.76)
            rod(name+'_夹爪短耳_%s_%s'%(idx,sign),c+u*r*1.1,c+u*r*1.42,.007*factor,M['金属银边'])
    for sign in [-1,1]:
        radial=(-up*.64+side*(sign*.83))*r*1.19
        rod(name+'_细长夹架侧梁_'+str(sign),ends[0]+radial,ends[1]+radial,.006*factor,M['金属银边'])
    # 下端通过小脚与盒内衬相接，高端连接后框，支撑结构为单视图推定。
    if not name.startswith('后'):
        c=ends[1]-up*r*1.16
        foot=Vector((c.x,c.y,world((0,0,-.044)).z))
        rod(name+'_夹架短支脚',foot,c,.014*factor,M['容器深灰'])
    if name.startswith('前'):
        c=ends[0]-up*r*1.16
        rod(name+'_另一端短支脚',Vector((c.x,c.y,world((0,0,-.044)).z)),c,.014*factor,M['容器深灰'])
    elif name.startswith('中'):
        rod(name+'_后框斜撑',world((-.41,.596,.065)),ends[0]-up*r*1.15,.010*factor,M['金属银边'])
    else:
        c=ends[1]-up*r*1.15
        rod(name+'_下端固定短撑',world((.48,.32,-.040)),c,.013*factor,M['容器深灰'])
        rod(name+'_后框固定细撑',world((.29,.612,.07)),ends[1]-up*r*1.12,.010*factor,M['金属银边'])

# 参考中的细密纵纹只在内衬出现，保留浅盒空间。
ribs=[]
for j in range(62):
    ribs.append(cube('内衬压纹_%02d'%j,(-.714+j*.0234,0,-.044),(.0028,1.15,.0018),M['青灰托盘内衬'],0))
bpy.ops.object.select_all(action='DESELECT')
for o in ribs:o.select_set(True)
bpy.context.view_layer.objects.active=ribs[0];bpy.ops.object.join();bpy.context.object.name='托盘内衬细密压纹'
for side in [-1,1]:
    cube('侧框高光窄沿_'+str(side),(side*.811,0,.100),(.010,1.30,.008),M['金属银边'])

# 用可编辑矢量字形成工业标签，避免原来的横杠替代字形。
for ob in list(scene.objects):
    if ob.name.startswith('标签短编码_') or ob.name.startswith('左侧印章细纹_') or ob.name.startswith('底部防滑脚_'):
        unlink(ob)
def text_mesh(name,body,p,size,mat,width=None):
    cu=bpy.data.curves.new(name,'FONT');cu.body=body;cu.size=size*factor;cu.extrude=0;cu.resolution_u=3
    ob=bpy.data.objects.new(name,cu);scene.collection.objects.link(ob);ob.location=world(p);ob.rotation_euler=(math.pi/2,0,0);ob.parent=root
    cu.materials.append(mat);bpy.context.view_layer.update()
    if width and ob.dimensions.x>width*factor:ob.scale.x*=width*factor/ob.dimensions.x
    bpy.ops.object.select_all(action='DESELECT');ob.select_set(True);bpy.context.view_layer.objects.active=ob;bpy.ops.object.convert(target='MESH')
    return ob
text_mesh('标签主文字','POLYMERIZATION PREPARATION',(-.555,-.697,-.036),.051,M['标签深色印记'],1.025)
text_mesh('标签副行','Rhodes Island  /  ADVANCED MATERIAL',(-.552,-.697,-.089),.021,M['标签深色印记'],1.00)
text_mesh('标签编号','PP-05  /  SYNTHESIS REAGENT',(-.552,-.697,-.155),.020,M['标签深色印记'],.66)
text_mesh('左侧标识','P',(-.708,-.705,-.081),.115,M['黄绿标签'],.09)
text_mesh('右侧小标识','05',(.607,-.698,-.175),.047,M['标签深色印记'],.094)
cube('标签分隔细线',(-.04,-.697,-.111),(1.015,.001,.002),M['标签深色印记'],0)
for j in range(22):
    cube('标签微型条码_%02d'%j,(.234+j*.010,-.697,-.154),(.003 if j%3 else .005,.001,.041),M['标签深色印记'],0)

# 无外部地面、展示底板或背景；盒体自身封底是容器的一部分。
floor=scene.objects.get('仅预览地面_不导出')
if floor:unlink(floor)
scene.render.film_transparent=True
scene.render.image_settings.color_mode='RGBA'
scene.render.resolution_x=1100;scene.render.resolution_y=1100
scene.cycles.samples=48
scene.render.threads_mode='FIXED';scene.render.threads=6
scene.cycles.use_denoising=True
scene.camera.data.ortho_scale=2.80
scene.camera.location=(2,-4.2,4)
target=Vector((0,0,-.06));scene.camera.rotation_euler=(target-scene.camera.location).to_track_quat('-Z','Y').to_euler()
scene['本次细化']='浅盒与三根管剂；三组两端卡箍及细长夹架；第二管封帽端翘起17度；闭合圆底玻璃、液芯、封帽环槽、管身刻度、内衬细纹、工业标签；无外部背景与底板。背面及夹架与盒体连接为单视图推定结构。'
meshes=[o for o in scene.objects if o.type=='MESH' and o.parent==root]
bpy.ops.object.select_all(action='DESELECT')
for o in [root]+meshes:o.select_set(True)
bpy.context.view_layer.objects.active=root
bpy.ops.export_scene.fbx(filepath=str(OUT/'PolymerizationPreparation_Finished.fbx'),use_selection=True,object_types={'EMPTY','MESH'},use_mesh_modifiers=True,add_leaf_bones=False,axis_forward='-Z',axis_up='Y',apply_unit_scale=True,bake_space_transform=False,mesh_smooth_type='FACE',path_mode='AUTO')
manifest['meshCount']=len(meshes)
manifest['referenceRefinement']=scene['本次细化']
manifest['editableSource']=str(OUT/'PolymerizationPreparation_Refined.blend')
(OUT/'material_manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf8')
scene.render.filepath=str(OUT/'PolymerizationPreparation_Transparent.png')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'PolymerizationPreparation_Refined.blend'),compress=True)
bpy.ops.render.render(write_still=True)
scene.camera.location=(4,-.8,2.7);scene.camera.rotation_euler=(target-scene.camera.location).to_track_quat('-Z','Y').to_euler()
scene.render.filepath=str(OUT/'PolymerizationPreparation_Side.png');scene.cycles.samples=24
bpy.ops.render.render(write_still=True)
print('REFINEMENT_DONE',len(meshes),flush=True)
