"""D32钢圆形包络返工预览；同半径球面大圆窄带和各向密集内片。"""
import bpy, math, json, sys, shutil
from pathlib import Path
from mathutils import Vector, Matrix, Euler

OUT=Path(__file__).resolve().parent
SOURCE=OUT.parent
ASSET=Path('F:/游城拓荒模拟器/Assets/YC/Presentation/Models/D32Steel')
PREVIEW=OUT/'previews'
PREVIEW.mkdir(exist_ok=True)
scene=bpy.data.scenes.new('D32钢_圆形包络返工')
bpy.context.window.scene=scene
scene.unit_settings.system='METRIC'
scene.unit_settings.scale_length=1
scene.render.engine='CYCLES'
scene.cycles.samples=24
scene.cycles.use_denoising=True
scene.render.threads_mode='FIXED'
scene.render.threads=6
scene.view_settings.view_transform='AgX'
scene.render.resolution_x=scene.render.resolution_y=900
scene.render.resolution_percentage=100
scene.render.image_settings.file_format='PNG'
scene.render.image_settings.color_mode='RGBA'
scene.render.film_transparent=False
geometry=bpy.data.collections.new('D32钢_各向球形叠带_成品')
stage=bpy.data.collections.new('D32钢_预览灯相机_不导出')
scene.collection.children.link(geometry)
scene.collection.children.link(stage)
root=bpy.data.objects.new('D32Steel_SphereRoot',None)
geometry.objects.link(root)
root['结构']='外层钢带中线位于同一个单位球面的大圆，钢带宽度也沿球面展开；各轴和参考视向均有圆形包络。内部钢片各向交叠，外围开放。'
root['参考']='https://prts.wiki/w/D32钢'
root['推定']='球形内部叠片深度和背侧由单张图标推定，不建稀有度圆环。'
meshes=[]
manifest={}

def linear(c): return c/12.92 if c<=.04045 else ((c+.055)/1.055)**2.4
def metal(label,rgb,metallic,roughness):
    name='D32Steel_Sphere_'+label
    m=bpy.data.materials.new(name)
    m.use_nodes=True
    p=next(n for n in m.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    srgb=[x/255 for x in rgb]
    color=tuple(linear(x) for x in srgb)+(1,)
    p.inputs['Base Color'].default_value=color
    p.inputs['Metallic'].default_value=metallic
    p.inputs['Roughness'].default_value=roughness
    m.diffuse_color=color
    manifest[name]={'color':srgb+[1],'metallic':metallic,'smoothness':1-roughness,'transparent':False}
    return m

silver=metal('亮冷银',(183,208,214),.60,.34)
steel=metal('青银钢',(134,170,183),.64,.39)
cyan=metal('冷蓝钢',(88,143,167),.58,.38)
back=metal('背侧冷银',(113,149,168),.58,.43)
edge=metal('窄银边',(190,213,219),.67,.29)

def ribbon(name,radius,width,rotation,start,extent,twist,phase,mat,
           center=(0,0,0),thickness=.014,taper=True,warp=.0,segments=72):
    """真实弯曲钢带，中心线空间弧、截面连续扭转、两面封闭厚度。"""
    rot=Euler(rotation,'XYZ').to_matrix()
    verts=[]; faces=[]
    closed=extent>math.tau-1e-5
    count=segments if closed else segments+1
    offset=Vector(center)
    for i in range(count):
        f=i/segments
        t=start+extent*f
        # 较小半径钢片中心线内收，以便中心在任何方向均保持稠密。
        rr=radius*(1+warp*math.cos(2*t+phase))
        point=Vector((rr*math.cos(t),rr*math.sin(t),radius*.085*math.sin(3*t+phase)))
        tangent=Vector((-math.sin(t),math.cos(t),.255*math.cos(3*t+phase))).normalized()
        radial=Vector((math.cos(t),math.sin(t),0))
        radial=(radial-tangent*radial.dot(tangent)).normalized()
        normal=tangent.cross(radial).normalized()
        roll=twist+.9*math.sin(1.5*t+phase)
        across=(radial*math.cos(roll)+normal*math.sin(roll)).normalized()
        w=width*(.91+.09*math.sin(2*t+phase))
        if taper and not closed: w*=.018+.982*math.sin(math.pi*f)**.65
        # 中线略凸，光照形成连续细窄反射，而不以三角贴色模拟钢片。
        for j in range(5):
            u=j/4
            v=point+(u-.5)*w*across+normal*(w*.035*math.sin(math.pi*u))
            verts.append(rot@v+offset)
    for i in range(segments):
        k=(i+1)%count
        for j in range(4): faces.append((i*5+j,k*5+j,k*5+j+1,i*5+j+1))
    me=bpy.data.meshes.new(name+'_实体网格')
    me.from_pydata(verts,[],faces);me.update()
    obj=bpy.data.objects.new(name,me);geometry.objects.link(obj);obj.parent=root
    for m in (mat,back,edge): me.materials.append(m)
    for p in me.polygons: p.use_smooth=True
    solid=obj.modifiers.new('封闭钢片厚度','SOLIDIFY')
    solid.thickness=thickness;solid.offset=0;solid.use_even_offset=False
    solid.material_offset=1;solid.material_offset_rim=2
    bevel=obj.modifiers.new('真实窄边倒角','BEVEL')
    bevel.width=.003;bevel.segments=2;bevel.material=2
    obj['物理结构']='完整空间钢片；含实体边缘与背面，不是仅向相机的面片。'
    meshes.append(obj)
    return obj

# 核心不是空球壳。六个互相正交的内弯短片覆盖各主视向的球心。
for i,rot in enumerate([(0,0,.35),(math.pi/2,0,.2),(0,math.pi/2,.4),
                        (.75,.65,1.4),(1.65,.5,2.5),(.4,1.4,.8)]):
    ribbon('球心内卷钢片_%02d'%i,.24+i*.011,.19+i*.008,rot,-1.5+i*.55,4.2,
           .15+i*.28,i*.7,[silver,cyan,steel][i%3],thickness=.012,warp=.14,segments=56)

# 三组相互垂直的中层带，再用不同方位短片补足斜向。密度均处于球心0.7R以内。
middle=[
 (.52,.30,(1.45,.15,.35),-2.1,4.9,.22,.0,silver),
 (.54,.27,(.15,1.40,-.50),-.75,4.55,.65,1.0,steel),
 (.51,.29,(.35,.20,1.35),-.45,4.7,-.55,2.0,cyan),
 (.57,.25,(1.2,.90,.75),-2.8,4.6,.2,3.1,silver),
 (.55,.27,(.85,-.80,1.75),-1.4,4.8,.3,4.0,steel),
 (.58,.23,(-.6,1.0,2.60),-.7,4.9,-.2,5.0,cyan),
 (.64,.21,(1.60,.85,2.00),-2.6,4.4,.75,5.7,silver),
 (.62,.24,(.6,-1.4,.4),-1.6,4.1,-.40,1.7,steel),
 (.62,.22,(-.8,.45,2.45),-.8,4.5,.45,2.7,silver),
 (.63,.20,(1.1,-.2,-1.30),-.6,4.8,-.45,3.7,cyan),
 (.54,.26,(.55,1.7,-.75),-2.7,4.7,.65,4.7,steel),
 (.58,.24,(1.85,-.45,.95),-1.7,4.4,-.30,5.4,silver),
]
for i,args in enumerate(middle):
    ribbon('球心密集宽带_%02d'%i,*args,thickness=.014,warp=.04)

# 少量折转尖钢片只增加核心切面，方向覆盖正/背/左右/上下，不能靠正面贴片补轮廓。
def folded_leaf(name,rotation,shift,mat,width=.28):
    rot=Euler(rotation,'XYZ').to_matrix();offset=Vector(shift)
    controls=[Vector((-.63,-.055,-.045)),Vector((-.25,.18,.06)),Vector((.22,-.17,.13)),Vector((.63,.04,-.025))]
    verts=[];faces=[];steps=30
    for i in range(steps+1):
        t=i/steps;s=1-t
        p=s**3*controls[0]+3*s*s*t*controls[1]+3*s*t*t*controls[2]+t**3*controls[3]
        tangent=(3*s*s*(controls[1]-controls[0])+6*s*t*(controls[2]-controls[1])+3*t*t*(controls[3]-controls[2])).normalized()
        across=Vector((-tangent.y,tangent.x,0)).normalized()
        normal=tangent.cross(across).normalized()
        roll=.5*math.sin(2*math.pi*t)
        across=across*math.cos(roll)+normal*math.sin(roll)
        w=width*math.sin(math.pi*t)**.65+.003
        for u in [-.5,0,.5]:
            # 中脊是实际折线，不平滑跨越两侧平面。
            v=p+across*(u*w)+normal*((1-2*abs(u))*w*.20)
            verts.append(rot@v+offset)
    for i in range(steps):
        for j in range(2):faces.append((i*3+j,(i+1)*3+j,(i+1)*3+j+1,i*3+j+1))
    me=bpy.data.meshes.new(name+'_折转尖片网格');me.from_pydata(verts,[],faces);me.update()
    obj=bpy.data.objects.new(name,me);geometry.objects.link(obj);obj.parent=root
    for m in (mat,back,edge):me.materials.append(m)
    for face in me.polygons: face.use_smooth=True
    solid=obj.modifiers.new('封闭尖片厚度','SOLIDIFY');solid.thickness=.015;solid.offset=0;solid.use_even_offset=False
    solid.material_offset=1;solid.material_offset_rim=2
    bevel=obj.modifiers.new('窄反射折边','BEVEL');bevel.width=.002;bevel.segments=2;bevel.material=2
    obj['结构']='多方向实际中脊折面和尖端，均位于球心区域。'
    meshes.append(obj)
for i,(rot,shift,mat) in enumerate([
    ((.1,.20,.90),(-.02,-.18,.08),silver),
    ((-.45,.15,-.50),(.04,.18,-.07),steel),
    ((.35,1.45,.40),(.18,.04,.02),silver),
    ((-.70,1.55,-.30),(-.18,-.02,.07),cyan),
    ((1.4,.50,-.25),(.04,.05,.18),silver),
    ((-1.4,.25,1.3),(-.05,.03,-.18),steel)]):
    folded_leaf('球心真实折转尖片_%02d'%i,rot,shift,mat)

# 少量中外层较窄钢带连接内部与外围，外围不堆表面碎片。
for i,rot in enumerate([(1.30,.25,-.40),(.2,1.05,1.45),(.7,-.75,2.45),(-.75,.65,-1.50)]):
    ribbon('疏外层连接钢带_%02d'%i,.80+i*.012,.15-i*.013,rot,-1.3+i*.6,5.2,
           -.3+i*.3,.6+i*.9,[steel,silver,cyan,silver][i],thickness=.011,warp=.025)

# 外层不是波浪圆环。每个闭合钢带中线是相同球半径的真实大圆。
# 宽度沿同一球面展开，实体厚度只向球内，不会以翻滚截面拉长某个方向。
def sphere_strip(name, plane_normal, width, mat, radius=1.0, segments=160):
    n=Vector(plane_normal).normalized()
    helper=Vector((0,0,1)) if abs(n.z)<.90 else Vector((0,1,0))
    u=n.cross(helper).normalized();v=n.cross(u).normalized()
    verts=[];faces=[]
    for i in range(segments):
        angle=math.tau*i/segments
        radial=u*math.cos(angle)+v*math.sin(angle)
        for j in range(3):
            latitude=(j/2-.5)*width/radius
            verts.append(radius*(radial*math.cos(latitude)+n*math.sin(latitude)))
    for i in range(segments):
        nxt=(i+1)%segments
        for j in range(2):faces.append((i*3+j,nxt*3+j,nxt*3+j+1,i*3+j+1))
    me=bpy.data.meshes.new(name+'_同球半径实体网格');me.from_pydata(verts,[],faces);me.update()
    obj=bpy.data.objects.new(name,me);geometry.objects.link(obj);obj.parent=root
    for m in (mat,back,edge):me.materials.append(m)
    for face in me.polygons:face.use_smooth=True
    solid=obj.modifiers.new('只向球内的真实钢带厚度','SOLIDIFY')
    solid.thickness=.009;solid.offset=-1;solid.use_even_offset=False
    solid.material_offset=1;solid.material_offset_rim=2
    bevel=obj.modifiers.new('球面窄带倒角','BEVEL');bevel.width=.0013;bevel.segments=2;bevel.material=2
    obj['几何']='完整闭合大圆；中线半径1.0、宽度沿同球面、背面和厚度封闭。'
    meshes.append(obj)

outer=[
 ((0,0,1),.042,steel),
 ((0,1,0),.048,silver),
 ((1,0,0),.044,cyan),
 ((3,-6.2,2.55),.030,silver),
 ((1,1,1),.014,silver),
 ((1,-1,1),.012,cyan),
 ((-1,1,1),.014,steel),
 ((1,2,-.5),.011,silver),
 ((2,-.5,-1),.011,cyan),
]
for i,(normal,width,mat) in enumerate(outer):
    sphere_strip('同半径球缘钢带_%02d'%i,normal,width,mat)

def make_obj(name,data):
    o=bpy.data.objects.new(name,data);stage.objects.link(o);return o
cam=make_obj('D32钢球形参考相机',bpy.data.cameras.new('D32球形正面相机'))
cam.data.type='ORTHO';cam.data.ortho_scale=2.38;scene.camera=cam
def camera(pos):
    cam.location=pos;cam.rotation_euler=(-cam.location).to_track_quat('-Z','Y').to_euler()
camera((3.0,-6.2,2.55))
world=bpy.data.worlds.new('D32钢球形中性环境');world.use_nodes=True
bg=next(n for n in world.node_tree.nodes if n.type=='BACKGROUND')
bg.inputs['Color'].default_value=(.055,.070,.085,1);bg.inputs['Strength'].default_value=.45
scene.world=world
for name,pos,energy,size in [('中性主光',(-3,-4,6),650,4.5),('右侧反射',(4,-1,2),450,3),('背侧条反射',(1,4,3),750,2)]:
    l=bpy.data.lights.new(name,'AREA');l.energy=energy;l.shape='DISK';l.size=size
    o=make_obj(name,l);o.location=pos;o.rotation_euler=(-o.location).to_track_quat('-Z','Y').to_euler()

# 应用实体厚度/倒角以保证 FBX 真实几何；保留独立白模场景。
for obj in meshes:
    bpy.context.view_layer.objects.active=obj;obj.select_set(True)
    for mod in list(obj.modifiers): bpy.ops.object.modifier_apply(modifier=mod.name)
    obj.select_set(False)
white_scene=scene.copy();white_scene.name='D32钢_圆形包络独立白模'
white_scene.use_fake_user=True
wc=bpy.data.collections.new('D32钢_球形白模_独立网格')
white_scene.collection.children.link(wc)
white_scene.collection.children.unlink(geometry)
wm=bpy.data.materials.new('D32钢球形白模');wm.use_nodes=True
wn=next(n for n in wm.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
wn.inputs['Base Color'].default_value=(.62,.62,.62,1);wn.inputs['Metallic'].default_value=0;wn.inputs['Roughness'].default_value=.70
wr=bpy.data.objects.new('D32Steel_SphereWhiteRoot',None);wc.objects.link(wr)
for obj in meshes:
    no=obj.copy();no.data=obj.data.copy();wc.objects.link(no);no.parent=wr
    no.data.materials.clear();no.data.materials.append(wm)
    for p in no.data.polygons:p.material_index=0

bpy.context.window.scene=scene
for o in scene.objects:o.select_set(False)
root.select_set(True)
for o in meshes:o.select_set(True)
bpy.context.view_layer.objects.active=root
fbx=OUT/'D32Steel_SphereRoundRevision.fbx'
# 此阶段仅生成源和预览，正式导出须在主代理实际视觉验收后执行。
for o in meshes:o.select_set(False)

views={'ReferenceFront':(3,-6.2,2.55),'Front':(0,-6,0),'Side':(6,0,0),'Back':(0,6,0),'Top':(0,-.001,6),'Oblique':(-3,-4,3)}
for label,pos in views.items():
    camera(pos);scene.render.filepath=str(PREVIEW/('D32Steel_'+label+'.png'))
    bpy.ops.render.render(write_still=True)
bpy.context.window.scene=white_scene
for label,pos in {'WhiteFront':(3,-6.2,2.55),'WhiteSide':(6,0,0),'WhiteTop':(0,-.001,6)}.items():
    camera(pos);white_scene.render.filepath=str(PREVIEW/('D32Steel_'+label+'.png'))
    bpy.ops.render.render(write_still=True)
bpy.context.window.scene=scene
camera((3,-6.2,2.55))
bounds=[o.matrix_world@Vector(v) for o in meshes for v in o.bound_box]
lo=[min(v[i] for v in bounds) for i in range(3)]
hi=[max(v[i] for v in bounds) for i in range(3)]
report={'model':'D32Steel','materials':manifest,'bounds':{'min':lo,'max':hi,'size':[hi[i]-lo[i] for i in range(3)]},
    'mesh_count':len(meshes),'triangles':sum(len(o.data.loop_triangles) for o in meshes),
    'front_camera':{'position':[3,-6.2,2.55],'target':[0,0,0],'ortho_scale':2.38},
    'axes':{'source_up':'Z','source_front':'-Y','fbx_forward':'-Z','fbx_up':'Y'},
    'structure':'6个球心内卷片、12个密集内层宽带、6个各向核心折转尖片、4个疏外层连接带、9个同半径球面大圆窄带；密度随半径降低。',
    'limits':'根据原单视图推定球形内层交叠与背侧；不含稀有度金圈，不声称官方背面。'}
for o in meshes:o.data.calc_loop_triangles()
report['triangles']=sum(len(o.data.loop_triangles) for o in meshes)
(OUT/'sphere_round_revision_info.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'D32Steel_SphereRoundRevision.blend'))
print('SPHERE_ROUND_PREVIEW_COMPLETE',json.dumps({k:report[k] for k in ['bounds','mesh_count','triangles','front_camera']},ensure_ascii=False))
