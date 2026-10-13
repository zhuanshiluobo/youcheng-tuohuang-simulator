"""按锁定参考视角修订体块和材质；仅 ArtSource，不导出/导入 Unity。"""
import bpy, bmesh, math, json, random, re
from pathlib import Path
from mathutils import Vector
from bpy_extras.object_utils import world_to_camera_view

ART=Path('F:/游城拓荒模拟器/ArtSource/Materials/NucleicCrystalSinter')
OUT=ART/'ReferenceViewMatch'
bpy.ops.wm.open_mainfile(filepath=str(OUT/'NucleicCrystalSinter_ViewMatch.blend'))
bpy.context.preferences.filepaths.save_version=0
old=bpy.context.scene
scene=bpy.data.scenes.new('同视角结构材质修订')
scene.render.engine='CYCLES';scene.cycles.samples=48;scene.cycles.use_denoising=True
scene.cycles.max_bounces=14;scene.cycles.transmission_bounces=12
scene.render.resolution_x=760;scene.render.resolution_y=836;scene.render.resolution_percentage=100
scene.render.threads_mode='FIXED';scene.render.threads=6
scene.render.image_settings.file_format='PNG';scene.render.image_settings.color_mode='RGBA';scene.render.film_transparent=True
scene.view_settings.view_transform='AgX'
scene.world=old.world.copy()
mapping={}
for src in old.objects:
    o=src.copy()
    if src.data:o.data=src.data.copy()
    scene.collection.objects.link(o);mapping[src]=o
for src,o in mapping.items():o.parent=mapping.get(src.parent)
scene.camera=mapping[old.camera]
bpy.context.window.scene=scene
cam=scene.camera
root=next(o for o in scene.objects if o.type=='EMPTY' and o.parent is None)
matmap={}
for o in scene.objects:
    if o.type=='MESH':
        for i,m in enumerate(o.data.materials):
            if m not in matmap:matmap[m]=m.copy()
            o.data.materials[i]=matmap[m]
def mat_named(name):
    return next(m for oldmat,m in matmap.items() if oldmat.name==name)
pearl=mat_named('NCS_CrystalPearl');ice=mat_named('NCS_CrystalIce')
lavender=mat_named('NCS_CrystalLavender');shadow=mat_named('NCS_CrystalShadow')
core=mat_named('NCS_CrystalCore');lid=mat_named('NCS_LavenderShell')
silver=mat_named('NCS_SilverEdge');black=mat_named('NCS_BlackHousing');steel=mat_named('NCS_DarkSteel');violet=mat_named('NCS_DeepViolet')
def linear(c):return c/12.92 if c<=.04045 else ((c+.055)/1.055)**2.4
def settings(m,rgb,rough,trans=0,metal=0):
    p=next(n for n in m.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    p.inputs['Base Color'].default_value=tuple(linear(v) for v in rgb)+(1,)
    p.inputs['Roughness'].default_value=rough;p.inputs['Transmission Weight'].default_value=trans
    p.inputs['Metallic'].default_value=metal;p.inputs['IOR'].default_value=1.48
    return p
settings(pearl,(.965,.94,1),.038,.86)
settings(ice,(.97,.97,1),.025,1)
settings(lavender,(.84,.72,.98),.038,.98)
settings(shadow,(.54,.32,.78),.045,.86)
settings(lid,(.62,.47,.79),.19,0,.42)
settings(violet,(.24,.13,.38),.16,0,.42)
settings(silver,(.78,.73,.85),.16,0,.65)
settings(black,(.055,.06,.075),.40,0,.85)
settings(steel,(.12,.13,.15),.32,0,.72)
p=settings(core,(.99,.98,1),.08,0)
p.inputs['Emission Color'].default_value=(.96,.92,1,1);p.inputs['Emission Strength'].default_value=5
ink=core.copy();ink.name='铭牌象牙白印字';p=next(n for n in ink.node_tree.nodes if n.type=='BSDF_PRINCIPLED');p.inputs['Transmission Weight'].default_value=0;p.inputs['Emission Strength'].default_value=0
plate_mat=violet.copy();plate_mat.name='型号面板哑光紫灰';settings(plate_mat,(.40,.34,.46),.36,0,.25)
# 顶盖保留细腻金属表面，粗糙度与法线的变化不影响已对齐的轮廓。
p=next(n for n in lid.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
noise=lid.node_tree.nodes.new('ShaderNodeTexNoise');noise.inputs['Scale'].default_value=135;noise.inputs['Detail'].default_value=2
bump=lid.node_tree.nodes.new('ShaderNodeBump');bump.inputs['Strength'].default_value=.16;bump.inputs['Distance'].default_value=.0012
lid.node_tree.links.new(noise.outputs['Fac'],bump.inputs['Height']);lid.node_tree.links.new(bump.outputs['Normal'],p.inputs['Normal'])
warm=silver.copy();warm.name='顶盖凹阶浅暖金属';settings(warm,(.60,.56,.42),.20,0,.68)
for o in scene.objects:
    if o.type=='MESH' and o.name.startswith('五层内收方形凹阶'):
        slot=len(o.data.materials);o.data.materials.append(warm)
        for k in (3,5,7):o.data.polygons[k*4+2].material_index=slot

# 保留旧场景，当前修订中替换偏长的方块墙及装饰性底座附件。
remove_prefix=('右侧宽大','右上外伸','右下块状','中央','前下方横晶','后侧上凝晶','后侧下凝晶','左上短方晶','左外横晶','左中连接晶','左下碎晶','左前竖晶','左下伸出晶','左侧附生晶','前侧短透明横晶',
 '前侧方形锁扣','锁扣顶部','锁扣下部','底座面板散热','底座紫灰铭牌','铭牌浅色编码','凝晶灰紫托盘','底座紫灰斜顶板','装置顶面紫色编号牌','型号04','型号TYPE','铭牌点阵','左侧银色接点')
for o in list(scene.objects):
    if o.type=='MESH' and o.name.startswith(remove_prefix):scene.collection.objects.unlink(o)

# 射线与深度平面求交，定位真实立体分件；相机始终保持锁定。
def at_pixel(x,y,depth=-.14):
    bpy.context.view_layer.update()
    projection=cam.calc_matrix_camera(bpy.context.evaluated_depsgraph_get(),x=760,y=836,scale_x=1,scale_y=1)
    inv=projection.inverted()
    v=inv@Vector((x/380*2-1,1-y/418*2,-1,1));v/=v.w
    point=cam.matrix_world@Vector(v[:3]);origin=cam.matrix_world.translation
    direction=(point-origin).normalized()
    return origin+direction*((depth-origin.y)/direction.y)

def register(o,name,m=None):
    o.name=name;o.data.name=name+'_网格';o.parent=root
    if m:o.data.materials.append(m)
    return o
def box(name,loc,dims,m,bevel=.003,rot=(0,0,0)):
    bpy.ops.mesh.primitive_cube_add(size=1,location=loc,rotation=rot)
    o=bpy.context.object;o.dimensions=dims;bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
    register(o,name,m)
    if bevel:
        mod=o.modifiers.new('实体棱边','BEVEL');mod.width=bevel;mod.segments=1
        n=o.modifiers.new('硬面法线','WEIGHTED_NORMAL');n.keep_sharp=True
    return o
def crystal(name,xy,dims,rot=(0,0,0),depth=-.14,tint=0):
    o=box(name,at_pixel(*xy,depth),dims,None,min(dims)*.026,rot)
    for m in (pearl,ice,lavender,shadow):o.data.materials.append(m)
    for f in o.data.polygons:
        f.material_index=3 if f.normal.x>.5 else (2 if f.normal.y>.5 else (tint if f.normal.z>.5 else 0))
    return o

central=crystal('中央透明主核',(199,205),(.46,.40,.59),(.12,-.10,-.12),-.13)
crystal('中央上方透亮块',(183,168),(.29,.28,.30),(.27,-.12,.2),-.12,1)
crystal('顶盖下方结合晶',(166,143),(.31,.27,.17),(.1,-.12,.08),.03)
crystal('中央下方横晶',(192,246),(.53,.34,.22),(.12,.10,-.12),-.28)
crystal('右下宽大斜切晶',(267,245),(.48,.42,.62),(.20,-.16,-.06),.10,1)
crystal('右侧中部晶面',(266,204),(.43,.37,.30),(.13,.19,-.09),.11)
crystal('前侧短透明横晶',(235,260),(.28,.22,.19),(.10,.05,-.10),-.28,1)
crystal('后侧连接凝晶',(201,203),(.44,.36,.66),(.15,.03,.11),.29,2)

# 右上部是斜向外伸的楔状晶，使用闭合凸多面体，而非长方体尖角。
verts=[at_pixel(x,y,d) for x,y,d in [(214,166,-.12),(279,163,-.12),(261,217,-.12),(218,211,-.12),
                                    (213,116,.27),(313,154,.27),(294,190,.27),(247,188,.27)]]
bm=bmesh.new()
for v in verts:bm.verts.new(v)
bm.verts.ensure_lookup_table();bmesh.ops.convex_hull(bm,input=list(bm.verts),use_existing_faces=False)
bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
mesh=bpy.data.meshes.new('右上楔晶_封闭体');bm.to_mesh(mesh);bm.free()
o=bpy.data.objects.new('右上斜向楔状晶',mesh);scene.collection.objects.link(o);register(o,o.name)
wedge_dark=shadow.copy();wedge_dark.name='右上晶体深紫折射面';settings(wedge_dark,(.40,.20,.66),.035,.68)
for m in (pearl,ice,lavender,wedge_dark):mesh.materials.append(m)
for face in mesh.polygons:
    center=sum((mesh.vertices[i].co for i in face.vertices),Vector())/len(face.vertices)
    v=world_to_camera_view(scene,cam,center)
    face.material_index=3 if v.x*380>282 else (2 if face.normal.z<-.2 else 1)
mod=o.modifiers.new('楔晶窄锐边','BEVEL');mod.width=.002;mod.segments=1

left_specs=[((107,161),(.21,.19,.16),(.22,.30,.6)),((133,155),(.19,.21,.20),(.12,-.28,-.16)),
 ((94,179),(.19,.17,.16),(.18,.20,-.43)),((118,181),(.20,.20,.21),(.3,-.13,.25)),
 ((108,203),(.14,.16,.19),(.1,.27,.18)),((133,207),(.19,.16,.21),(.11,-.25,.25)),
 ((150,191),(.22,.20,.23),(.27,.15,-.20)),((155,222),(.21,.20,.17),(.20,-.12,.10)),
 ((137,229),(.13,.14,.17),(-.15,.24,.18)),((117,222),(.12,.14,.18),(.25,-.16,-.27))]
for i,(xy,dims,rot) in enumerate(left_specs):crystal('左侧分叉方晶_%02d'%i,xy,dims,rot,-.34,i%3)
random.seed(181)
for i in range(10):
    x,y=((120,169),(142,180),(109,193),(140,219),(96,190),(160,203),(123,233),(170,239),(150,152),(183,230))[i]
    s=random.uniform(.06,.09)
    crystal('附着小晶片_%02d'%i,(x,y),(s,s*.8,s*1.2),tuple(random.uniform(-.5,.5) for _ in range(3)),-.46,i%3)

# 小体积亮核位于透明晶内部；不以大块白色内胆遮住折射细节。
for i,(xy,dims) in enumerate([((197,188),(.24,.22,.34)),((167,205),(.09,.11,.13)),((207,238),(.12,.13,.08))]):
    o=box('晶内亮核_%d'%i,at_pixel(*xy,-.02),dims,core,.01,(.15,.13,.2))

# 移回参考图中的大号顶部铭牌，以及左侧黑底银色接点。
plate=box('黑色装置上的紫灰型号面板',(.10,-.035,.39),(1.02,.46,.02),plate_mat,.003)
for i,y in enumerate((.32,.07,-.18)):
    box('左侧接点黑色插座_%d'%i,(-.595,y,.382),(.19,.18,.035),black)
    box('左侧接点银灰触片_%d'%i,(-.595,y,.408),(.125,.10,.020),silver,.002)
def text(name,body,loc,size):
    bpy.ops.object.select_all(action='DESELECT')
    cu=bpy.data.curves.new(name,'FONT');cu.body=body;cu.size=size;cu.extrude=.00025
    o=bpy.data.objects.new(name,cu);scene.collection.objects.link(o);o.location=loc
    bpy.context.view_layer.objects.active=o;o.select_set(True);bpy.ops.object.convert(target='MESH')
    register(bpy.context.object,name,ink)
text('参考字样_04','04',(.245,-.19,.403),.23)
text('参考字样_TYPE','TYPE',(-.16,-.18,.403),.082)
for i in range(7):
    for j in range(6):
        if (i*7+j*3)%5:
            box('面板点阵_%d_%d'%(i,j),(-.30+i*.014,-.183+j*.022,.403),(.009,.014,.001),ink,0)
gold=silver.copy();gold.name='装置下沿金色标记';settings(gold,(.72,.55,.09),.34,0,.35)
box('装置前沿金色识别块',(-.38,-.58,.14),(.27,.006,.055),gold,.001)
for j in range(6):box('识别块细纹_%d'%j,(-.48+j*.035,-.584,.14),(.008,.002,.045),black,0)
# 正面下端小连接晶避免主体悬空，接入后方承托。
crystal('底部短连接晶',(193,286),(.13,.20,.17),(.05,.2,.07),.12,1)
for x in (-.2,.375):
    box('后侧顶盖连接座_'+str(x),(x,.40,2.135),(.095,.10,.13),lid,.005)
# 右下紫色连接鳍片有真实厚度；背面为单视图无法确认的装配推定。
tail_front=[Vector(v) for v in [(.86,-.05,.56),(1.02,-.03,.42),(.73,-.47,.30),(.65,-.40,.36)]]
tail_verts=tail_front+[v+Vector((0,.065,0)) for v in tail_front]
tail_faces=[(3,2,1,0),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)]
data=bpy.data.meshes.new('右下连接鳍片_实体');data.from_pydata(tail_verts,[],tail_faces);data.update()
bm=bmesh.new();bm.from_mesh(data);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(data);bm.free()
o=bpy.data.objects.new('右下紫色连接鳍片',data);scene.collection.objects.link(o);register(o,o.name,lavender)

# 用窄柔光在晶体中形成明暗折射与锐利亮边，背景仍为透明。
for o in list(scene.objects):
    if o.type=='LIGHT':scene.collection.objects.unlink(o)
bg=next(n for n in scene.world.node_tree.nodes if n.type=='BACKGROUND');bg.inputs['Color'].default_value=(.45,.45,.48,1);bg.inputs['Strength'].default_value=.8
wn=scene.world.node_tree.nodes;wl=scene.world.node_tree.links
darkbg=wn.new('ShaderNodeBackground');darkbg.inputs['Color'].default_value=(.05,.05,.07,1);darkbg.inputs['Strength'].default_value=.5
lightpath=wn.new('ShaderNodeLightPath');mix=wn.new('ShaderNodeMixShader')
wl.new(lightpath.outputs['Is Transmission Ray'],mix.inputs[0]);wl.new(darkbg.outputs[0],mix.inputs[1]);wl.new(bg.outputs[0],mix.inputs[2])
wl.new(mix.outputs[0],next(n for n in wn if n.type=='OUTPUT_WORLD').inputs['Surface'])
target=Vector((0,0,1.25))
for name,loc,energy,sx,sy in [('左前细长反射光',(-3,-4,5),700,1.1,3.8),('右侧窄亮边',(3,-1,3),650,.35,3),('后方晶体透射光',(-1,3,3.5),950,2,2.8),('前方柔补光',(-.5,-4,2),100,2,2)]:
    d=bpy.data.lights.new(name,'AREA');d.energy=energy;d.shape='RECTANGLE';d.size=sx;d.size_y=sy
    o=bpy.data.objects.new(name,d);scene.collection.objects.link(o);o.location=loc;o.rotation_euler=(target-o.location).to_track_quat('-Z','Y').to_euler()
scene['制作状态']='锁定参考相机后的结构材质修订；仅 Blender，未导入 Unity。'
scene.render.filepath=str(OUT/'SameView_Refined.png')
bpy.ops.object.select_all(action='DESELECT');root.select_set(True);bpy.context.view_layer.objects.active=root
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='VIEW_3D':
            area.spaces.active.region_3d.view_perspective='CAMERA'
            area.spaces.active.shading.type='MATERIAL'
bpy.context.view_layer.update()
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'NucleicCrystalSinter_SameView.blend'),compress=True)
bpy.ops.render.render(write_still=True)
print('SAME_VIEW_REFINED',len([o for o in scene.objects if o.type=='MESH']))
