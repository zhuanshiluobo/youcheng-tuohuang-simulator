"""继续当前 Blender 场景；保留源阶段，同心共轴完善双极纳米片。"""
import bpy, bmesh, json, math
from pathlib import Path
from mathutils import Matrix, Vector

OUT = Path('F:/游城拓荒模拟器/ArtSource/Materials/BipolarNanoflake/RedCoreRegionReview')
source_scene = bpy.context.scene
assert source_scene.name == 'BipolarNanoflake_Finished', source_scene.name
backup = OUT / 'BipolarNanoflake_BeforeFinish_Live.blend'
if not backup.exists():
    bpy.ops.wm.save_as_mainfile(filepath=str(backup), copy=True, compress=True)
bpy.ops.scene.new(type='FULL_COPY')
scene = bpy.context.scene
scene.name = 'BipolarNanoflake_ReferenceFinished'
root = next(o for o in scene.objects if o.name.startswith('BipolarNanoflake_Root'))
root.name = 'BipolarNanoflake_Finished_Root'
meshes = list(o for o in root.children_recursive if o.type == 'MESH')
mat_copies = {}
for obj in meshes:
    obj.data = obj.data.copy()
    for slot in obj.material_slots:
        if slot.material:
            original = slot.material
            if original not in mat_copies:
                m = original.copy()
                m.name = original.name.split('.00')[0] + '_完成版'
                mat_copies[original] = m
            slot.material = mat_copies[original]

frames = sorted([o for o in meshes if o.name.startswith('完整铜红长方形外框')], key=lambda o:o.name)
extra = next(o for o in meshes if o.name.startswith('最大框_附属内层'))
core = next(o for o in meshes if o.name.startswith('核心_一体圆盘'))
horizontal = next(o for o in meshes if o.name.startswith('青色环绕细线'))
vertical = next(o for o in meshes if o.name.startswith('外围纵向大环线'))
basis = frames[1].matrix_world.to_3x3().normalized()
axis = (basis @ Vector((1,0,0))).normalized()
center = core.matrix_world.translation.copy()

def linear(v):
    return v/12.92 if v<=.04045 else ((v+.055)/1.055)**2.4

def material(name, rgb, metal, rough, emission=0):
    m = bpy.data.materials.new('BipolarNanoflake_' + name)
    m.use_nodes = True
    p = next(n for n in m.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    color = tuple(linear(v) for v in rgb)
    p.inputs['Base Color'].default_value = (*color,1)
    p.inputs['Metallic'].default_value = metal
    p.inputs['Roughness'].default_value = rough
    p.inputs['Emission Color'].default_value = (*color,1)
    p.inputs['Emission Strength'].default_value = emission
    p.inputs['Transmission Weight'].default_value = 0
    m.diffuse_color = (*color,1)
    return m

copper = material('完成_红铜侧面',(.74,.135,.055),.78,.28)
dark = material('完成_深铜内壁',(.20,.055,.037),.68,.32)
bright = material('完成_浅铜银窄亮面',(.91,.75,.63),.80,.24)
graphite = material('完成_铜框暗面',(.19,.10,.073),.68,.29)
teal = material('完成_青色横环',(.15,.66,.67),.72,.24)
teal_dark = material('完成_青灰纵环',(.105,.29,.30),.80,.28)
rim_mat = material('完成_银灰盘沿',(.67,.75,.73),.84,.22)
foil_mat = material('完成_琥珀铜薄片边',(.87,.45,.15),.62,.26,.22)
body_mat = material('完成_暖白纳米片',(.98,.90,.69),.15,.30,.95)
light_mat = material('完成_纳米片暖白亮面',(1,.965,.82),.10,.27,1.8)
muted_mat = material('完成_纳米片浅琥珀断面',(.83,.61,.30),.24,.31,.55)
trace_mat = material('完成_铜红细导路',(.87,.29,.06),.62,.29,.13)
silver_trace = material('完成_银色细端接',(.60,.75,.73),.82,.25)

def frame(obj, name, width, height, rail, depth, angle, pale=True):
    # 单一实体框；亮面按框本体表面分槽，空窗保持留空。
    outer = [(-width/2,-height/2),(width/2,-height/2),(width/2,height/2),(-width/2,height/2)]
    inner = [(-width/2+rail,-height/2+rail),(width/2-rail,-height/2+rail),
             (width/2-rail,height/2-rail),(-width/2+rail,height/2-rail)]
    verts = [(x,y,z) for z in (-depth/2,depth/2) for ring in (outer,inner) for x,y in ring]
    faces=[]; ids=[]
    for i in range(4):
        j=(i+1)%4
        faces.extend([(i,j,8+j,8+i),(4+i,12+i,12+j,4+j),
                      (8+i,8+j,12+j,12+i),(i,4+i,4+j,j)])
        ids.extend([0,1,2 if pale else 3,3])
    mesh=bpy.data.meshes.new(name+'_实体框')
    mesh.from_pydata(verts,[],faces);mesh.update()
    for m in (copper,dark,bright,graphite):mesh.materials.append(m)
    for p,mid in zip(mesh.polygons,ids):p.material_index=mid
    bm=bmesh.new();bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(mesh);bm.free()
    obj.data=mesh;obj.name=name
    obj.location=center;obj.rotation_euler=(basis@Matrix.Rotation(math.radians(angle),3,'X')).to_euler()
    for mod in obj.modifiers:
        if mod.type=='BEVEL':
            mod.width=.0022;mod.segments=2
    obj['共同轴方向']=list(axis);obj['绕共同轴角度']=angle
    obj['完成尺寸']=[width,height,rail,depth]

specs=[('BNF_铜框_01_主外框',1.92,1.22,.043,.055,-33,True),
       ('BNF_铜框_02_后立框',1.64,.82,.044,.057,-3,True),
       ('BNF_铜框_03_前横框',1.36,.86,.043,.064,-52,False),
       ('BNF_铜框_04_交错框',1.50,.84,.039,.061,53,True)]
for obj,spec in zip(frames,specs):frame(obj,*spec)
frame(extra,'BNF_铜框_05_最大框内层',1.78,1.08,.033,.047,-33,True)
for obj,name,angle,mat in [(horizontal,'BNF_横向青色细环',-82,teal),
                          (vertical,'BNF_纵向青灰细环',-28,teal_dark)]:
    obj.name=name;obj.location=center
    obj.rotation_euler=(basis@Matrix.Rotation(math.radians(angle),3,'X')).to_euler()
    obj.data.materials.clear();obj.data.materials.append(mat)
    for p in obj.data.polygons:p.material_index=0
    obj['共同轴方向']=list(axis);obj['绕共同轴角度']=angle

# 保留已完成的连续盘面、真实细盘沿；重做中央薄片与定向细导路。
old=core.data;nv=int(core['外壳连续顶点数']);nf=int(core['外壳面数'])
verts=[tuple(v.co) for v in old.vertices[:nv]]
faces=[tuple(p.vertices) for p in old.polygons[:nf]]
mids=[p.material_index for p in old.polygons[:nf]]
smooth=[p.use_smooth for p in old.polygons[:nf]]
red=material('完成_连续红橙核心',(.68,.10,.025),.10,.34,.7)
rp=next(n for n in red.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
vc=red.node_tree.nodes.new('ShaderNodeVertexColor');vc.layer_name='核心红橙渐变_完成'
red.node_tree.links.new(vc.outputs['Color'],rp.inputs['Base Color'])
red.node_tree.links.new(vc.outputs['Color'],rp.inputs['Emission Color'])
mats=[red,rim_mat,foil_mat,body_mat,light_mat,muted_mat,trace_mat,silver_trace]

def poly(points,mid):
    start=len(verts);verts.extend(points);faces.append(tuple(range(start,len(verts))))
    mids.append(mid);smooth.append(False)

def prism(w,h,z,d,top,side,cut=.004):
    corners=[(-w/2+cut,-h/2),(w/2-cut,-h/2),(w/2,-h/2+cut),(w/2,h/2-cut),
             (w/2-cut,h/2),(-w/2+cut,h/2),(-w/2,h/2-cut),(-w/2,-h/2+cut)]
    start=len(verts)
    for dz in (0,d):verts.extend((x,y,z+dz) for x,y in corners)
    faces.extend([tuple(start+i for i in reversed(range(8))),tuple(start+8+i for i in range(8))])
    mids.extend([side,top]);smooth.extend([False,False])
    for i in range(8):
        j=(i+1)%8;faces.append((start+i,start+j,start+8+j,start+8+i));mids.append(side);smooth.append(False)

prism(.228,.190,.004,.003,2,2)
prism(.211,.178,.007,.009,3,2)
prism(.202,.167,.016,.0015,3,2)
# 少量较大嵌入式片层断面，形成连续暖白核心，不散布随机碎屑。
for pts,mid in [([(-.085,-.060),(-.012,-.067),(.013,-.027),(-.068,-.012)],4),
                ([(-.062,-.008),(.006,-.022),(.032,.024),(-.047,.048)],4),
                ([(.018,-.053),(.078,-.039),(.066,.003),(.032,-.004)],3),
                ([(.035,.010),(.085,.025),(.066,.065),(.004,.060)],4),
                ([(-.085,.025),(-.045,.038),(-.034,.069),(-.081,.060)],5),
                ([(-.024,-.063),(.039,-.065),(.059,-.050),(.006,-.038)],5)]:
    poly([(x,y,.01765) for x,y in pts],mid)
# 背面保持实心薄层，可辨片体厚度。
prism(.211,.178,-.010,.007,2,2)

def tube(points,radius,mid,segments=6):
    # 曲线实际网格附着于盘面，从核心引向外围，无粒子和悬空火花。
    starts=[]
    for i,pt in enumerate(points):
        p=Vector(pt);t=Vector(points[min(i+1,len(points)-1)])-Vector(points[max(i-1,0)])
        t.normalize();a=t.cross(Vector((0,0,1))).normalized();b=t.cross(a).normalized()
        st=len(verts);starts.append(st)
        for k in range(segments):
            q=p+radius*(math.cos(k*2*math.pi/segments)*a+math.sin(k*2*math.pi/segments)*b)
            verts.append(tuple(q))
    for i in range(len(starts)-1):
        for k in range(segments):
            j=(k+1)%segments;faces.append((starts[i]+k,starts[i]+j,starts[i+1]+j,starts[i+1]+k));mids.append(mid);smooth.append(True)
    for st,rev in [(starts[0],True),(starts[-1],False)]:
        faces.append(tuple(st+k for k in (reversed(range(segments)) if rev else range(segments))));mids.append(mid);smooth.append(False)

for points,mid in [([(-.115,.025,.004),(-.20,.052,.004),(-.267,.127,.004),(-.294,.143,.004)],6),
                   ([(-.110,-.049,.004),(-.176,-.093,.004),(-.255,-.142,.004),(-.274,-.178,.004)],6),
                   ([(.111,.031,.004),(.173,.064,.004),(.244,.050,.004),(.302,.094,.004)],7),
                   ([(.113,-.030,.004),(.165,-.086,.004),(.236,-.102,.004),(.274,-.151,.004)],7)]:
    tube(points,.0018,mid)

mesh=bpy.data.meshes.new('BNF_连续薄盘与片层核心')
mesh.from_pydata(verts,[],faces);mesh.update()
for m in mats:mesh.materials.append(m)
for p,mid,sm in zip(mesh.polygons,mids,smooth):p.material_index=mid;p.use_smooth=sm
bm=bmesh.new();bm.from_mesh(mesh);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(mesh);bm.free()
attr=mesh.color_attributes.new(name='核心红橙渐变_完成',type='FLOAT_COLOR',domain='CORNER')
for p in mesh.polygons:
    for li in p.loop_indices:
        v=mesh.vertices[mesh.loops[li].vertex_index].co
        if p.material_index==0:
            r=math.hypot(v.x,v.y);hot=math.exp(-((r/.17)**2))
            veins=.96+.04*math.sin(v.x*64+math.sin(v.y*41)*2)
            c=(linear(.34+.62*hot)*veins,linear(.030+.22*hot)*veins,linear(.012+.034*hot),1)
        else:c=(1,1,1,1)
        attr.data[li].color=c
core.data=mesh;core.name='BNF_一体红盘与暖白薄片'
core.rotation_euler=(basis@Matrix.Rotation(math.radians(-33),3,'X')).to_euler();core.location=center
core['共同轴方向']=list(axis);core['绕共同轴角度']=-33
core['结构说明']='连续深红薄盘、银灰细盘沿、紧凑暖白片层、少量贴面导路；无背景圆盘、粒子或电路板。'

scene.camera.name='BNF_仅预览参考相机'
scene.camera.location=(3.6,-5.3,2.8)
scene.camera.rotation_euler=(-scene.camera.location).to_track_quat('-Z','Y').to_euler()
scene.camera.data.ortho_scale=2.62
scene.render.engine='CYCLES';scene.cycles.samples=48;scene.cycles.use_denoising=True
scene.render.resolution_x=1000;scene.render.resolution_y=1000;scene.render.resolution_percentage=100
scene.render.film_transparent=True
scene.view_settings.view_transform='AgX';scene.view_settings.exposure=-.35
scene['制作说明']='用户确认整体按图修形；所有主体保持同心共轴。灰色背景与外金色稀有度圈不建模。'
root['共同中心']=list(center);root['共同轴方向']=list(axis)
root['背面说明']='单张参考图无法核实背面；按薄片结构合理补齐。'
bpy.ops.object.select_all(action='DESELECT')
root.select_set(True);bpy.context.view_layer.objects.active=root
scene['完成阶段']='几何与源材质完成，待贴图烘焙及实际验收'
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'BipolarNanoflake_Finished.blend'),compress=True)
print('FINISH_GEOMETRY_READY',json.dumps({'scene':scene.name,'root':root.name,'axis':list(axis),'center':list(center),'meshes':[(o.name,len(o.data.polygons)) for o in root.children_recursive if o.type=='MESH']},ensure_ascii=False),flush=True)
