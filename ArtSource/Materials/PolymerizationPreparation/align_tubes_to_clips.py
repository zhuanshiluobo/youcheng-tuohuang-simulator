"""三管等长、俯视平行；后两管仅绕右夹持点向上旋转。仅输出验收源。"""
import bpy, math, json
from pathlib import Path
from mathutils import Vector
ART=Path('F:/游城拓荒模拟器/ArtSource/Materials/PolymerizationPreparation')
OUT=ART/'AlignedPivotReview'
bpy.ops.wm.open_mainfile(filepath=str(OUT/'BeforeAlignment.blend'))
scene=bpy.context.scene;root=scene.objects['PolymerizationPreparation_Root']
factor=scene.objects['前黄绿长标签'].dimensions.x/1.10
# 源坐标原点从未移动过的内衬平面恢复。
lining=scene.objects['托盘青灰内衬薄板']
offset=lining.location-Vector((0,0,-.053))*factor
silver=bpy.data.materials['PolymerizationPreparation_金属银边']
dark=bpy.data.materials['PolymerizationPreparation_凹槽黑']
def world(p):return Vector(p)*factor+offset
names=['前侧横置管','中部斜置管','后部斜立管']
radii=[.063,.065,.070];rows=[-.37,-.04,.32];angles=[0,30,65]
left_x=-.47;right_x=.47;floor=-.042
# 直管段在夹子外还保留 .025，再接圆头，而非用圆头卡在夹子上。
straight_overhang=.025
target=(right_x-left_x+2*straight_overhang+radii[0]*1.88)*factor
def measure(name):
    head=scene.objects[name+'_圆弧封头']
    h=[head.matrix_world@v.co for v in head.data.vertices]
    c=sum(h[:48],Vector())/48;c1=sum(h[48:96],Vector())/48
    d=(c-c1).normalized()
    objects=[o for o in scene.objects if o.type=='MESH' and o.name.startswith(name+'_')]
    ps=[(o.matrix_world@v.co).dot(d) for o in objects for v in o.data.vertices]
    return objects,d,min(ps),max(ps),c,(h[0]-c).length
review=[]
for name,rad,y,degrees in zip(names,radii,rows,angles):
    objects,axis,lo,hi,headcenter,r=measure(name)
    theta=math.radians(degrees);newaxis=Vector((math.cos(theta),0,-math.sin(theta)))
    rot=axis.rotation_difference(newaxis)
    # 固定的夹持支点位于右侧夹子中心，改变角度不改变支点。
    grip=world((right_x,y,floor+rad+.022))
    newbottom=grip+newaxis*(straight_overhang*factor+r)
    oldbottom=headcenter+axis*(hi-headcenter.dot(axis))
    head_seam=headcenter.dot(axis);bottom_seam=hi-r
    delta=(hi-lo)-target
    stretch=(bottom_seam-head_seam-delta)/(bottom_seam-head_seam)
    for ob in objects:
        mat=ob.matrix_world.copy();inv=mat.inverted();ob.data=ob.data.copy()
        for v in ob.data.vertices:
            p=mat@v.co;t=p.dot(axis)
            mapped=t if t>=bottom_seam else t+delta if t<=head_seam else bottom_seam+(t-bottom_seam)*stretch
            v.co=inv@(newbottom+rot@(p-oldbottom+axis*(mapped-t)))
        ob.data.update()
    review.append({'name':name,'angleDegrees':degrees,'grip':list(grip),'axis':list(newaxis),'targetLength':target})
# 重做六个独立夹子；每对夹子的 X 轴同向，前管不再带平面内偏转。
for o in list(scene.objects):
    if o.name.startswith('盒底独立夹子_'):
        for c in list(o.users_collection):c.objects.unlink(o)
def finish(o,name,mat,bevel=0):
    o.name=name;o.data.name=name+'_Mesh';o.data.materials.append(mat);o.parent=root
    if bevel:
        m=o.modifiers.new('倒角','BEVEL');m.width=bevel*factor;m.segments=3
        m=o.modifiers.new('加权法线','WEIGHTED_NORMAL');m.keep_sharp=True
    return o
def cube(name,p,size,mat):
    bpy.ops.mesh.primitive_cube_add(size=1,location=world(p));o=bpy.context.object;o.dimensions=Vector(size)*factor
    bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
    return finish(o,name,mat,.001)
for i,(rad,y,degrees) in enumerate(zip(radii,rows,angles)):
    for j,x in enumerate([left_x,right_x]):
        index=i*2+j+1;parts=[]
        theta=math.radians(degrees if j==1 else 0)
        axis=Vector((math.cos(theta),0,-math.sin(theta)));up=Vector((math.sin(theta),0,math.cos(theta)));side=Vector((0,1,0))
        center=Vector((x,y,floor+rad+.022));radius=rad+.001
        parts.append(cube('夹子固定脚',Vector((x,y,floor+.009)),(.066,.17,.018),silver))
        N=40;K=N+1;vs=[];fs=[];width=.032
        for zz,rr in [(-width/2,radius),(width/2,radius),(-width/2,radius+.008),(width/2,radius+.008)]:
            for k in range(K):
                t=math.radians(48+264*k/N)
                vs.append(world(center+axis*zz+(up*math.cos(t)+side*math.sin(t))*rr))
        for k in range(N):
            fs.extend([(k,k+1,K+k+1,K+k),(2*K+k,3*K+k,3*K+k+1,2*K+k+1),
                       (k,2*K+k,2*K+k+1,k+1),(K+k,K+k+1,3*K+k+1,3*K+k)])
        fs.extend([(0,K,3*K,2*K),(N,2*K+N,3*K+N,K+N)])
        me=bpy.data.meshes.new('U夹');me.from_pydata(vs,[],fs);me.update()
        ob=bpy.data.objects.new('U夹',me);scene.collection.objects.link(ob);finish(ob,'U夹',silver,.001);parts.append(ob)
        for f in me.polygons:f.use_smooth=True
        # 抬起端的卡口随管轴转动，底脚仍贴盒底，短连接片连接两者。
        bottom=center-up*(radius+.004)
        stemheight=max(.007,bottom.z-floor-.009)
        parts.append(cube('夹子连接片',(bottom.x,y,floor+.009+stemheight/2),(.025,.036,stemheight+.006),silver))
        # 连接片脚必须落在局部固定脚上，随角度只延伸本夹子底片。
        if j==1 and degrees:
            p=(Vector((x,y,floor+.009))+Vector((bottom.x,y,floor+.009)))/2
            parts.append(cube('夹子短底脚',p,(abs(x-bottom.x)+.035,.070,.018),silver))
        bpy.ops.object.select_all(action='DESELECT')
        for p in parts:
            p.select_set(True);bpy.context.view_layer.objects.active=p
            for mod in list(p.modifiers):bpy.ops.object.modifier_apply(modifier=mod.name)
        bpy.context.view_layer.objects.active=ob;bpy.ops.object.join()
        ob.name='盒底独立夹子_%02d'%index;ob.data.name=ob.name+'_Mesh'
bpy.context.view_layer.update()
for row,name in zip(review,names):
    objects,axis,lo,hi,headcenter,r=measure(name)
    row['measuredLength']=hi-lo;row['measuredAxis']=list(axis)
    row['planViewAxisError']=abs(axis.y)
scene['本次细化']='三对夹子俯视水平对齐；三根管等长加长，直管穿至卡口外后才进入圆头；以右卡口为支点，第一根0度，第二根30度，第三根65度。六个独立夹子，无连接框，管内原色保持。仅供验收，不导入Unity。'
scene['三管长度']=json.dumps({r['name']:r['measuredLength'] for r in review},ensure_ascii=False)
scene.render.threads_mode='FIXED';scene.render.threads=6;scene.cycles.samples=32
scene.render.resolution_x=1100;scene.render.resolution_y=1100
scene.camera.data.ortho_scale=3.05
targetpoint=Vector((0,0,-.08));scene.camera.location=(2,-4.2,4)
scene.camera.rotation_euler=(targetpoint-scene.camera.location).to_track_quat('-Z','Y').to_euler()
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='VIEW_3D':
            area.spaces.active.shading.type='MATERIAL';area.spaces.active.overlay.show_overlays=False
            area.spaces.active.region_3d.view_perspective='CAMERA'
meshes=[o for o in scene.objects if o.type=='MESH' and o.parent==root]
bpy.ops.object.select_all(action='DESELECT')
for o in [root]+meshes:o.select_set(True)
bpy.context.view_layer.objects.active=root
bpy.ops.export_scene.fbx(filepath=str(OUT/'PolymerizationPreparation_Review.fbx'),use_selection=True,object_types={'EMPTY','MESH'},use_mesh_modifiers=True,add_leaf_bones=False,axis_forward='-Z',axis_up='Y',apply_unit_scale=True,bake_space_transform=False,mesh_smooth_type='FACE',path_mode='AUTO')
scene.render.filepath=str(OUT/'PolymerizationPreparation_Aligned.png')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'PolymerizationPreparation_Aligned.blend'),compress=True)
(OUT/'alignment_review.json').write_text(json.dumps(review,ensure_ascii=False,indent=2),encoding='utf8')
print('ALIGNMENT',json.dumps(review,ensure_ascii=False),flush=True)
bpy.ops.render.render(write_still=True)
scene.camera.location=(0,0,6);scene.camera.rotation_euler=(0,0,0)
scene.camera.rotation_euler=(Vector((0,0,0))-scene.camera.location).to_track_quat('-Z','Y').to_euler()
scene.render.filepath=str(OUT/'PolymerizationPreparation_TrueTop.png');scene.cycles.samples=24
bpy.ops.render.render(write_still=True)
scene.camera.location=(5,0,.1);scene.camera.rotation_euler=(Vector((0,0,0))-scene.camera.location).to_track_quat('-Z','Y').to_euler()
scene.render.filepath=str(OUT/'PolymerizationPreparation_TrueEnd.png')
bpy.ops.render.render(write_still=True)
print('ALIGNED_REVIEW_DONE',flush=True)
