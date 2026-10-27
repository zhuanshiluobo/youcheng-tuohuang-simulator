"""复制已验收的顶部白模，补齐电子单元剩余结构；仅在独立后台运行。"""
import bpy, bmesh, math, json, hashlib, struct, sys, runpy
from pathlib import Path
from mathutils import Vector, Matrix

if not bpy.app.background:
    raise RuntimeError('本脚本仅用于独立后台进程。')
OUT=Path(__file__).resolve().parent
SOURCE=OUT.parent/'TopWhiteReview'/'CrystallineElectronicUnit_TopWhite.blend'
bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
scene=bpy.context.scene
scene.name='晶体电子单元_完整白模验收'
white=bpy.data.materials['白模_中性哑光']
top=bpy.data.objects['顶部白模_整体']

def top_fingerprint():
    h=hashlib.sha256()
    for o in sorted(top.children,key=lambda x:x.name):
        h.update(o.name.encode('utf-8'))
        for v in o.data.vertices:h.update(struct.pack('fff',*v.co))
        for p in o.data.polygons:
            for idx in p.vertices:h.update(struct.pack('I',idx))
        for values in (o.location,o.rotation_euler,o.scale):h.update(struct.pack('fff',*values))
    return h.hexdigest()

original_top_hash=top_fingerprint()
root=bpy.data.objects.new('晶体电子单元_完整白模',None)
scene.collection.objects.link(root)
top.parent=root
top.location.z=.820
housing=bpy.data.collections.new('新增_八角箱体与凹口')
terminals=bpy.data.collections.new('新增_四根单段铜柱')
rear=bpy.data.collections.new('新增_左后水平底座与铁皮支撑')
details=bpy.data.collections.new('新增_铭牌与侧壁散热槽')
for coll in (housing,terminals,rear,details):scene.collection.children.link(coll)
created=[]

def mesh_obj(name,verts,faces,coll= housing,bevel=.008,parent=None):
    data=bpy.data.meshes.new(name+'_网格')
    data.from_pydata(verts,[],faces);data.update()
    bm=bmesh.new();bm.from_mesh(data)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(data);bm.free()
    ob=bpy.data.objects.new(name,data);coll.objects.link(ob)
    ob.parent=parent or root
    data.materials.append(white)
    if bevel:
        mod=ob.modifiers.new('边缘小倒角','BEVEL');mod.width=bevel;mod.segments=2
    created.append(ob)
    return ob

def box(name,loc,size,coll=housing,bevel=.008,parent=None,rz=0):
    x,y,z=[v/2 for v in size]
    verts=[(-x,-y,-z),(x,-y,-z),(x,y,-z),(-x,y,-z),
           (-x,-y,z),(x,-y,z),(x,y,z),(-x,y,z)]
    faces=[(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)]
    ob=mesh_obj(name,verts,faces,coll,bevel,parent)
    ob.location=loc;ob.rotation_euler.z=rz
    return ob

def octagon(radius):
    return [(radius*math.cos(-math.pi/2+i*math.pi/4),radius*math.sin(-math.pi/2+i*math.pi/4)) for i in range(8)]

def profile_shell(name,profile,coll=housing,bevel=.009):
    # 半径/高度剖面绕八边形闭合：支持真实凹槽、唇边和底部内收。
    verts=[(x,y,z) for radius,z in profile for x,y in octagon(radius)]
    faces=[]
    for j in range(len(profile)):
        nj=(j+1)%len(profile)
        for i in range(8):faces.append((j*8+i,j*8+(i+1)%8,nj*8+(i+1)%8,nj*8+i))
    return mesh_obj(name,verts,faces,coll,bevel)

def slab(name,radius,z0,z1,coll=housing,bevel=.008):
    ring=octagon(radius)
    verts=[(x,y,z) for z in (z0,z1) for x,y in ring]
    faces=[tuple(reversed(range(8))),tuple(range(8,16))]
    faces.extend((i,(i+1)%8,(i+1)%8+8,i+8) for i in range(8))
    return mesh_obj(name,verts,faces,coll,bevel)

# 外壳封底。上盖向中央真实槽底倾斜，顶部只整体平移嵌入。
profile_shell('上下等边_直八棱柱箱体',[(1.66,.075),(1.66,.79),(1.10,.79),(1.10,.075)])
slab('封闭八角底盖',1.675,.025,.085,bevel=.006)
profile_shell('底沿窄护边',[(1.675,.035),(1.675,.086),(1.625,.086),(1.625,.035)],bevel=.006)
slab('中央凹口封闭槽底',1.109,.765,.820,bevel=.003)
profile_shell('八角台面_宽斜面凹口',[(1.675,.80),(1.675,.940),(1.12,.940),(1.10,.827),(1.10,.810),(1.215,.80)],bevel=.006)
profile_shell('上盖接缝下压边',[(1.667,.767),(1.667,.793),(1.621,.793),(1.621,.767)],bevel=.004)

# 侧壁面板有槽底、上下框与齿条；槽是几何凹进，而非涂黑条纹。
outline=octagon(1.66)
face_frames={}
for i in range(8):
    a=Vector((*outline[i],0));b=Vector((*outline[(i+1)%8],0))
    tangent=(b-a).normalized();normal=Vector((tangent.y,-tangent.x,0))
    center=(a+b)/2
    frame=bpy.data.objects.new(f'侧面_{i:02d}_局部基准',None);details.objects.link(frame);frame.parent=root
    # 局部 X 沿面、Y 朝内、Z 向上。
    frame.location=center
    frame.rotation_euler=(0,0,math.atan2(tangent.y,tangent.x))
    face_frames[i]=frame
    length=(b-a).length
    if i==7:
        box('正面铭牌_浅凹底板',(0,-.008,.408),(length-.07,.025,.615),details,.005,frame)
        for z in (.115,.699):box(f'铭牌横边_{z}',(0,-.032,z),(length-.065,.024,.030),details,.003,frame)
        for x in (-(length-.095)/2,(length-.095)/2):box(f'铭牌竖边_{x}',(x,-.032,.407),(.027,.024,.615),details,.003,frame)
        box('铭牌左侧识别小框',(-.438,-.038,.42),(.174,.019,.448),details,.003,frame)
        box('识别小框内层',(-.438,-.050,.42),(.127,.012,.399),details,.002,frame)
    elif i==0:
        # 侧板基面与散热齿之间的厚度差构成 7 道横向凹槽。
        box(f'散热面_{i:02d}_槽底',(0,-.007,.394),(length-.07,.023,.565),details,.003,frame)
        for x in (-(length-.102)/2,(length-.102)/2):box(f'散热面_{i:02d}_端边_{x}',(x,-.027,.39),(.031,.037,.596),details,.003,frame)
        for j in range(8):
            box(f'散热面_{i:02d}_横齿_{j+1}',(0,-.031,.112+j*.077),(length-.072,.045,.030),details,.003,frame)

def text_obj(name,body,loc,size,parent,scale_x=1):
    data=bpy.data.curves.new(name,'FONT');data.body=body;data.size=size;data.extrude=.003;data.bevel_depth=.0005
    data.materials.append(white)
    ob=bpy.data.objects.new(name,data);details.objects.link(ob);ob.parent=parent
    ob.location=loc;ob.rotation_euler.x=math.pi/2;ob.scale.x=scale_x
    created.append(ob)
    return ob

front=face_frames[7]
text_obj('铭牌凸字_07','07',(.175,-.051,.172),.48,front,.72)
text_obj('铭牌凸字_TYPE','TYPE:',(-.275,-.051,.259),.112,front,.84)

def cylinder(name,loc,radius,depth,coll=terminals,parent=None,segments=24,bevel=.006):
    verts=[(radius*math.cos(i*2*math.pi/segments),radius*math.sin(i*2*math.pi/segments),z) for z in (-depth/2,depth/2) for i in range(segments)]
    faces=[tuple(reversed(range(segments))),tuple(range(segments,2*segments))]
    faces.extend((i,(i+1)%segments,(i+1)%segments+segments,i+segments) for i in range(segments))
    ob=mesh_obj(name,verts,faces,coll,bevel,parent);ob.location=loc
    for p in ob.data.polygons:
        if len(p.vertices)==4:p.use_smooth=True
    return ob

# 沿八角底座自身的前后中轴镜像，矩形柱脚及多边形顶帽同向。
post_angle=math.radians(22.5)
post_u=Vector((math.cos(post_angle),math.sin(post_angle)))
post_v=Vector((-math.sin(post_angle),math.cos(post_angle)))
post_positions=[]
for label,lateral,longitudinal in [('左侧后柱',-1.22,.31),('左侧前柱',-1.22,-.31),('右侧后柱',1.22,.31),('右侧前柱',1.22,-.31)]:
    p=post_u*lateral+post_v*longitudinal
    post_positions.append((label,p.x,p.y))
for label,x,y in post_positions:
    base=.940
    start=len(created)
    box(label+'_底座',(x,y,base+.014),(.195,.200,.028),terminals,.006)
    cylinder(label+'_下端圈',(x,y,base+.038),.1225,.025,segments=12)
    cylinder(label+'_单段铜柱身',(x,y,base+.173),.116,.245)
    cylinder(label+'_多边形顶帽',(x,y,base+.313),.137,.039,segments=8)
    cylinder(label+'_顶帽小铆钉',(x,y,base+.338),.024,.010,segments=12,bevel=.002)
    for ob in created[start:]:ob.rotation_euler.z=post_angle

# 用户明确：水平底板加轴端两侧直立薄铁皮，整体没有俯仰倾斜。
assembly=bpy.data.objects.new('左后水平底座与双侧铁皮支撑_整体',None);rear.objects.link(assembly);assembly.parent=root
assembly.location=(-.70,.99,.9675)
assembly.rotation_euler=(0,0,math.radians(25))
box('后组件_水平窄底板',(0,0,0),(.85,.38,.055),rear,.003,assembly)
for side,label in [(-1,'左'),(1,'右')]:
    box('后组件_'+label+'侧直立铁皮',(side*.367,0,.305),(.030,.350,.550),rear,.0025,assembly)
    box('后组件_'+label+'侧折边',(side*.365,0,.033),(.092,.350,.012),rear,.002,assembly)
box('后组件_中央方形核心',(0,0,.430),(.340,.300,.300),rear,.009,assembly)
for side,label in [(-1,'左'),(1,'右')]:
    for part,x,radius,length in [('端短铜绕组',.245,.120,.150),('端支承轴',.344,.057,.051),('端绕组外缘',.313,.124,.014)]:
        ob=cylinder('后组件_'+label+part,(side*x,0,.430),radius,length,rear,assembly,32,.002)
        ob.rotation_euler.y=math.pi/2

# 台面上小铭牌与三角标记以低浮雕表达，保持统一白模。
plaque=box('台面小识别牌',(.65,-1.02,.952),(.36,.25,.018),details,.004,rz=math.radians(22.5))
for x in (-.133,.133):
    p=plaque.matrix_local@Vector((x,0,.014))
    box('识别牌边线'+str(x),p,(.012,.197,.009),details,.002,rz=math.radians(22.5))
triangle=[(.27,-1.18,.954),(.47,-1.16,.954),(.335,-.99,.954)]
for i in range(3):
    a,b=Vector(triangle[i]),Vector(triangle[(i+1)%3]);delta=b-a
    ob=box('台面三角标记_'+str(i),(a+b)/2,(delta.length,.013,.008),details,.002,rz=math.atan2(delta.y,delta.x))

# 对照相机保持已验收顶部的方向，只扩大取景容纳完整箱体。
target=Vector((0,0,1.2775))
def aim(ob,point):ob.rotation_euler=(Vector(point)-ob.location).to_track_quat('-Z','Y').to_euler()
camera_specs={'Reference':('参考角度',(0,-8,5.8965),3.94),
              'Side':('侧面观察',(7,-1,4.8375),4.05),
              'Back':('背面观察_推定',(0,8,5.8965),3.94),
              'Top':('顶部观察',(0,0,8),3.80)}
for key,(name,loc,scale) in camera_specs.items():
    cam=bpy.data.objects[name];cam.location=loc;cam.data.ortho_scale=scale
    aim(cam,target if key!='Top' else (0,0,0))
scene.camera=bpy.data.objects['参考角度']
for name,loc,power,size in [('主光',(-4,-5,7),850,4),('右侧补光',(4,-2,5),390,3),('后轮廓光',(1,5,7),640,3)]:
    light=bpy.data.objects[name];light.location=loc;light.data.energy=power;light.data.size=size;aim(light,target)
scene.render.resolution_x=1100;scene.render.resolution_y=1100
scene.cycles.samples=48
scene.render.film_transparent=True
for ob in scene.objects:ob.select_set(False)
root.select_set(True);bpy.context.view_layer.objects.active=root
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='VIEW_3D':
            sp=area.spaces.active;sp.show_region_ui=False;sp.overlay.show_overlays=False
            sp.region_3d.view_perspective='ORTHO';sp.region_3d.view_location=target
            sp.region_3d.view_rotation=scene.camera.rotation_euler.to_quaternion();sp.region_3d.view_distance=5.65

for ob in scene.objects:
    if ob.type in {'LIGHT','CAMERA'}:ob.hide_set(True)
assert original_top_hash==top_fingerprint(),'顶部网格或层片布局发生非预期变化'
manifest={'source_top':str(SOURCE),'top_preserved':True,'top_hash':original_top_hash,
          'top_change':'仅整体上移 0.820，嵌入中央凹口；32 层网格、角度、位置和比例保持原状。',
          'scope':'完整白模：八角箱体、凹槽台面、4 根单段短铜柱、单组后部方形核心与两端短绕组、水平底板与两侧铁皮支撑、铭牌和散热槽。',
          'user_confirmed':'整体为上下同尺寸的直八棱柱，背后无立柱；左后为水平底座和两侧直立铁皮支撑；左右铜柱镜像，上盖外侧边加高。',
          'uncertain':'背面与底面采用封闭简单结构，左后绕组内部连接为参考图不可见部分的推定。',
          'upright_post_count':4,'new_part_count':len(created),'no_unity_changes':True}
# 最新验收细节沿用同一局部修正，防止完整重建退回空标签和偏心支架。
detail_notes=runpy.run_path(str(OUT/'complete_centered_details.py'))['apply_revision'](save=False)
manifest['centered_details_revision']=detail_notes['centered_details_revision']
manifest['new_part_count']=sum(o.type in {'MESH','FONT'} for coll in (housing,terminals,rear,details) for o in coll.objects)
(OUT/'full_white_notes.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'CrystallineElectronicUnit_FullWhite.blend'))
for key,(name,_,_) in camera_specs.items():
    if '--pilot' in sys.argv and key!='Reference':continue
    scene.camera=bpy.data.objects[name];scene.render.filepath=str(OUT/f'FullWhite_{key}.png')
    bpy.ops.render.render(write_still=True,scene=scene.name)
print('FULL_WHITE_COMPLETE',str(OUT))
