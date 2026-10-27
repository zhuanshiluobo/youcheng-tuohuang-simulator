"""六个独立夹子固定于盒内衬；夹子之间没有长杆或连接框。"""
import bpy, math, json
from pathlib import Path
from mathutils import Vector
ART=Path('F:/游城拓荒模拟器/ArtSource/Materials/PolymerizationPreparation')
SRC=ART/'ReferenceRefinement'
OUT=ART/'FloorHolderRevision'
OUT.mkdir(exist_ok=True)
bpy.ops.wm.open_mainfile(filepath=str(SRC/'PolymerizationPreparation_Refined.blend'))
scene=bpy.context.scene
root=scene.objects['PolymerizationPreparation_Root']
# 用户要求保持管内原配色：从修改前源文件恢复管液、玻璃和液面材质。
restore_names=['PolymerizationPreparation_青色聚合层板','PolymerizationPreparation_青蓝透光盖板','PolymerizationPreparation_层板亮边']
with bpy.data.libraries.load(str(ART/'PolymerizationPreparation_Finished.blend'),link=False) as (source,target):
    target.materials=list(restore_names)
for name,restored in zip(restore_names,target.materials):
    current=bpy.data.materials.get(name)
    if current:
        current.name=name+'_上一版材质留存'
        for o in scene.objects:
            if o.type=='MESH':
                for slot in o.material_slots:
                    if slot.material==current:slot.material=restored
    restored.name=name
silver=bpy.data.materials['PolymerizationPreparation_金属银边']
dark=bpy.data.materials['PolymerizationPreparation_凹槽黑']
factor=scene.objects['前黄绿长标签'].dimensions.x/1.10
offset=scene.objects['前侧横置管_银灰封帽'].location-Vector((-.49,-.39,.046))*factor
def world(p):return Vector(p)*factor+offset
# 仅移除上一版悬空夹架，保留三个管体、盒体和材质。
old_parts=['端部卡箍','夹爪短耳','细长夹架侧梁','夹架短支脚','另一端短支脚','后框斜撑','下端固定短撑','后框固定细撑']
for o in list(scene.objects):
    if any(s in o.name for s in old_parts):
        for c in list(o.users_collection):c.objects.unlink(o)
def finish(o,name,mat,bevel=0):
    o.name=name;o.data.name=name+'_Mesh';o.data.materials.append(mat);o.parent=root
    if bevel:
        m=o.modifiers.new('细倒角','BEVEL');m.width=bevel*factor;m.segments=3
        n=o.modifiers.new('加权法线','WEIGHTED_NORMAL');n.keep_sharp=True
    return o
def cube(name,p,size,mat,bevel=.001):
    bpy.ops.mesh.primitive_cube_add(size=1,location=world(p));o=bpy.context.object;o.dimensions=Vector(size)*factor
    bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
    return finish(o,name,mat,bevel)
floor=-.042
for i,(left,right) in enumerate([((-.48,-.390),(.43,-.351)),((-.48,-.040),(.46,-.040)),((-.48,.320),(.46,.320))],1):
    a=Vector((left[0],left[1],floor));b=Vector((right[0],right[1],floor))
    axis=(b-a).normalized();side=Vector((-axis.y,axis.x,0));up=Vector((0,0,1))
    length=(b-a).length
    # 每根管仅对应两个独立夹子，不制作任何连接长杆或框架。
    for j,pt in enumerate([a,b],1):
        foot=cube('底部夹架_%s_端部固定片_%s'%(i,j),pt+up*.005,(.075,.175,.010),silver)
        foot.rotation_euler.z=math.atan2(axis.y,axis.x)
        # 向上敞开的弹性 U 卡位固定在底片上，不跟随翘起管子旋转。
        radius=.080 if i<3 else .084
        center=pt+up*(radius+.011)
        width=.033;N=40;K=N+1;vs=[];fs=[]
        for x,r in [(-width/2,radius),(width/2,radius),(-width/2,radius+.009),(width/2,radius+.009)]:
            for k in range(K):
                ang=math.radians(48+264*k/N)
                vs.append(world(center+axis*x+(up*math.cos(ang)+side*math.sin(ang))*r))
        for k in range(N):
            fs.extend([(k,k+1,K+k+1,K+k),(2*K+k,3*K+k,3*K+k+1,2*K+k+1),
                       (k,2*K+k,2*K+k+1,k+1),(K+k,K+k+1,3*K+k+1,3*K+k)])
        fs.extend([(0,K,3*K,2*K),(N,2*K+N,3*K+N,K+N)])
        mesh=bpy.data.meshes.new('固定U夹');mesh.from_pydata(vs,[],fs);mesh.update()
        o=bpy.data.objects.new('底部夹架_%s_固定U夹_%s'%(i,j),mesh);scene.collection.objects.link(o);finish(o,o.name,silver,.001)
        for f in mesh.polygons:f.use_smooth=True
        for sign in [-1,1]:
            # 低矮铆钉说明夹具确实装在盒底，无外部展示底板。
            p=world(pt+side*(sign*.066)+up*.012)
            bpy.ops.mesh.primitive_uv_sphere_add(segments=12,ring_count=6,radius=.010*factor,location=p)
            rivet=bpy.context.object;rivet.scale=(1,1,.30);finish(rivet,'底部夹架_%s_铆钉_%s_%s'%(i,j,sign),dark)
for i in range(1,4):
    for j in range(1,3):
        names=['底部夹架_%s_端部固定片_%s'%(i,j),'底部夹架_%s_固定U夹_%s'%(i,j),
               '底部夹架_%s_铆钉_%s_-1'%(i,j),'底部夹架_%s_铆钉_%s_1'%(i,j)]
        bpy.ops.object.select_all(action='DESELECT')
        for name in names:scene.objects[name].select_set(True)
        clip=scene.objects[names[1]]
        bpy.context.view_layer.objects.active=clip
        # 应用各夹子局部倒角后合并，独立夹子可整体选择编辑。
        for name in names:
            ob=scene.objects[name];bpy.context.view_layer.objects.active=ob
            for mod in list(ob.modifiers):bpy.ops.object.modifier_apply(modifier=mod.name)
        bpy.context.view_layer.objects.active=clip;bpy.ops.object.join()
        clip.name='盒底独立夹子_%02d'%((i-1)*2+j);clip.data.name=clip.name+'_Mesh'
scene['本次细化']='仅六个独立夹子固定在盒内底部，每根管两个；没有连接长杆、轨道或框架。管体姿态及恢复后的原始配色保持不变。'
scene.render.threads_mode='FIXED';scene.render.threads=6
scene.cycles.samples=40
scene.render.resolution_x=1100;scene.render.resolution_y=1100
scene.camera.location=(2,-4.2,4)
target=Vector((0,0,-.06));scene.camera.rotation_euler=(target-scene.camera.location).to_track_quat('-Z','Y').to_euler()
meshes=[o for o in scene.objects if o.type=='MESH' and o.parent==root]
bpy.ops.object.select_all(action='DESELECT')
for o in [root]+meshes:o.select_set(True)
bpy.context.view_layer.objects.active=root
bpy.ops.export_scene.fbx(filepath=str(OUT/'PolymerizationPreparation_Finished.fbx'),use_selection=True,object_types={'EMPTY','MESH'},use_mesh_modifiers=True,add_leaf_bones=False,axis_forward='-Z',axis_up='Y',apply_unit_scale=True,bake_space_transform=False,mesh_smooth_type='FACE',path_mode='AUTO')
GAME=Path('F:/游城拓荒模拟器/Assets/YC/Presentation/Models/PolymerizationPreparation')
manifest=json.loads((GAME/'material_manifest.json').read_text(encoding='utf-8-sig'))
original=json.loads((SRC/'BeforeRefinement_material_manifest.json').read_text(encoding='utf-8-sig'))
for name in restore_names:manifest['materials'][name]=original['materials'][name]
manifest['meshCount']=len(meshes);manifest['referenceRefinement']=scene['本次细化'];manifest['editableSource']=str(OUT/'PolymerizationPreparation_SixClips.blend')
manifest.pop('unityVerification',None)
(OUT/'material_manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf8')
scene.render.filepath=str(OUT/'PolymerizationPreparation_SixClips.png')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'PolymerizationPreparation_SixClips.blend'),compress=True)
bpy.ops.render.render(write_still=True)
scene.camera.location=(0,-4.6,5.5);scene.camera.rotation_euler=(target-scene.camera.location).to_track_quat('-Z','Y').to_euler()
scene.render.filepath=str(OUT/'PolymerizationPreparation_SixClipsTop.png');scene.cycles.samples=24
bpy.ops.render.render(write_still=True)
print('FLOOR_HOLDERS_DONE',len(meshes),flush=True)
