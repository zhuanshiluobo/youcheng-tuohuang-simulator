"""仅供验收：盒体加高、左右边框加厚，三管头部改为圆弧玻璃顶。"""
import bpy, math, json
from pathlib import Path
from mathutils import Vector
ART=Path('F:/游城拓荒模拟器/ArtSource/Materials/PolymerizationPreparation')
SRC=ART/'FloorHolderRevision'
OUT=ART/'BoxRoundHeadReview'
OUT.mkdir(exist_ok=True)
bpy.ops.wm.open_mainfile(filepath=str(SRC/'PolymerizationPreparation_SixClips.blend'))
scene=bpy.context.scene;root=scene.objects['PolymerizationPreparation_Root']
factor=scene.objects['前黄绿长标签'].dimensions.x/1.10
offset=scene.objects['前侧横置管_银灰封帽'].location-Vector((-.49,-.39,.046))*factor
glass=bpy.data.materials['PolymerizationPreparation_青蓝透光盖板']
def unlink(ob):
    for c in list(ob.users_collection):c.objects.unlink(ob)
def change_vertices(ob,fn):
    matrix=ob.matrix_world.copy();inv=matrix.inverted()
    ob.data=ob.data.copy()
    for v in ob.data.vertices:
        p=(matrix@v.co-offset)/factor
        v.co=inv@(fn(p)*factor+offset)
    ob.data.update()
def raise_wall(p):
    p=p.copy();p.z=.095+(p.z-.06)*(.455/.30);return p
def widen(ob,delta):
    center=ob.location.x
    for v in ob.data.vertices:
        v.co.x*=1+delta*factor/ob.dimensions.x
    ob.data.update()
# 盒顶上移少量、盒底向下延伸，增加外壁高度，内衬和六个夹子位置不动。
wall_prefix=['浅灰侧壁_','前后灰薄板_','前护角_','后护角_']
rim_prefix=['左右银框_','前后银框_','左右内沿暗缝_','前后内沿暗缝_',
            '右框密集压合齿_','侧框高光窄沿_']
for ob in list(scene.objects):
    if ob.type!='MESH':continue
    name=ob.name
    if any(name.startswith(s) for s in wall_prefix):
        change_vertices(ob,raise_wall)
    elif any(name.startswith(s) for s in rim_prefix):
        ob.location.z+=.035*factor
    elif name=='浅容器封闭底板':
        ob.location.z-=.120*factor
    elif name.startswith('背侧中心接缝') or name.startswith('背侧固定片'):
        ob.location.z-=.070*factor
    # 以侧壁内缘为固定线，向外增厚 0.072，保持管体的内部空间。
    if name.startswith(('浅灰侧壁_','左右银框_','前护角_','后护角_')):
        sign=1 if ob.location.x>0 else -1
        width=ob.dimensions.x
        for v in ob.data.vertices:v.co.x*=1+.072*factor/width
        ob.location.x+=sign*.036*factor;ob.data.update()
    elif name.startswith(('前后灰薄板_','前后银框_')) or name=='浅容器封闭底板':
        width=ob.dimensions.x
        for v in ob.data.vertices:v.co.x*=1+.144*factor/width
        ob.data.update()
    elif name.startswith('侧框高光窄沿_'):
        ob.location.x+=(1 if ob.location.x>0 else -1)*.072*factor
    # 铭牌整体适应较高的前侧板，字形不拉伸。
    if name.startswith('标签') or name in ['前黄绿长标签','前标签左侧黑底印章','左侧标识']:
        ob.location.z-=.045*factor
        if name in ['前黄绿长标签','前标签左侧黑底印章']:
            for v in ob.data.vertices:v.co.z*=1.23
            ob.data.update()
    if name in ['前右独立绿色标记','右侧小标识']:
        ob.location.z-=.10*factor
bpy.context.view_layer.update()
# 只改变封口几何，直接沿用原有玻璃材质；液芯和管内颜色不动。
for name,radius in [('前侧横置管',.063),('中部斜置管',.065),('后部斜立管',.070)]:
    cap=scene.objects[name+'_银灰封帽'];a=cap.location.copy()
    d=(scene.objects[name+'_封帽亮沿'].location-a).normalized()
    q=d.to_track_quat('Z','Y');r=radius*factor;N=48
    # 外弧和内弧连接成有真实厚度的封闭圆顶，圆顶接到现有管口。
    profile=[(.022*factor,r)]
    for k in range(1,17):
        t=k*math.pi/32;profile.append((.022*factor-r*.88*math.sin(t),max(r*math.cos(t),.00001)))
    ri=r*.86
    profile.append((.022*factor-ri*.88,.00001))
    for k in range(15,-1,-1):
        t=k*math.pi/32;profile.append((.022*factor-ri*.88*math.sin(t),max(ri*math.cos(t),.00001)))
    profile.append(profile[0]);vs=[];fs=[]
    for z,rr in profile:
        vs.extend([a+q@Vector((rr*math.cos(j*math.tau/N),rr*math.sin(j*math.tau/N),z)) for j in range(N)])
    for k in range(len(profile)-1):
        for j in range(N):
            h=(j+1)%N;fs.append((k*N+j,k*N+h,(k+1)*N+h,(k+1)*N+j))
    mesh=bpy.data.meshes.new(name+'_圆弧封头_Mesh');mesh.from_pydata(vs,[],[tuple(reversed(f)) for f in fs]);mesh.update()
    ob=bpy.data.objects.new(name+'_圆弧封头',mesh);scene.collection.objects.link(ob);ob.parent=root;mesh.materials.append(glass)
    for face in mesh.polygons:face.use_smooth=True
    for old in list(scene.objects):
        if old.name.startswith(name+'_') and any(s in old.name for s in ['银灰封帽','帽顶内凹','帽顶同心压环','封帽环槽']):unlink(old)
    # 保留很薄的封口沿，去掉原来的高平顶金属帽。
    lip=scene.objects[name+'_封帽亮沿']
    lip.data=lip.data.copy()
    # 沿轴方向压薄至原厚度的一半。
    transform=lip.matrix_world.copy();inverse=transform.inverted();center=lip.location.copy()
    for v in lip.data.vertices:
        p=transform@v.co;v.co=inverse@(p-d*((p-center).dot(d)*.5))
    lip.data.update()
scene['本次细化']='验收版：盒体外壁加高约52%，左右银框宽度从0.060增至0.132；三根管头改圆弧玻璃顶。六个独立夹子、管体姿态及管内颜色不变。未导入Unity。'
scene.render.threads_mode='FIXED';scene.render.threads=6;scene.cycles.samples=36
scene.render.resolution_x=1100;scene.render.resolution_y=1100
scene.camera.data.ortho_scale=2.95
target=Vector((0,0,-.10));scene.camera.location=(2,-4.2,4)
scene.camera.rotation_euler=(target-scene.camera.location).to_track_quat('-Z','Y').to_euler()
meshes=[o for o in scene.objects if o.type=='MESH' and o.parent==root]
bpy.ops.object.select_all(action='DESELECT')
for o in [root]+meshes:o.select_set(True)
bpy.context.view_layer.objects.active=root
bpy.ops.export_scene.fbx(filepath=str(OUT/'PolymerizationPreparation_Review.fbx'),use_selection=True,object_types={'EMPTY','MESH'},use_mesh_modifiers=True,add_leaf_bones=False,axis_forward='-Z',axis_up='Y',apply_unit_scale=True,bake_space_transform=False,mesh_smooth_type='FACE',path_mode='AUTO')
manifest=json.loads((SRC/'material_manifest.json').read_text(encoding='utf-8-sig'))
manifest['referenceRefinement']=scene['本次细化'];manifest['meshCount']=len(meshes)
manifest['editableSource']=str(OUT/'PolymerizationPreparation_BoxRoundHead.blend')
points=[o.matrix_world@Vector(v) for o in meshes for v in o.bound_box]
lo=[min(v[i] for v in points) for i in range(3)];hi=[max(v[i] for v in points) for i in range(3)]
manifest['bounds']={'min':lo,'max':hi,'size':[hi[i]-lo[i] for i in range(3)]}
(OUT/'material_manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf8')
scene.render.filepath=str(OUT/'PolymerizationPreparation_BoxRoundHead.png')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'PolymerizationPreparation_BoxRoundHead.blend'),compress=True)
bpy.ops.render.render(write_still=True)
scene.camera.location=(3.8,-4.5,1.7);scene.camera.rotation_euler=(target-scene.camera.location).to_track_quat('-Z','Y').to_euler()
scene.render.filepath=str(OUT/'PolymerizationPreparation_BoxSide.png');scene.cycles.samples=24
bpy.ops.render.render(write_still=True)
print('BOX_ROUND_HEAD_REVIEW_DONE',len(meshes),flush=True)
