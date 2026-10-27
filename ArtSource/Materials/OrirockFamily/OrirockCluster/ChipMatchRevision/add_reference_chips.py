"""参考图的小缺口是真实的局部减材；不拆色面，不改变主方块位姿和接地点。"""
from pathlib import Path
import bpy,bmesh,json,math,sys
from mathutils import Vector,Quaternion

OUT=Path(__file__).resolve().parent;OUT.mkdir(exist_ok=True)
current=bpy.app.driver_namespace['cluster_square_revision']
base=globals().get('SOURCE_BASE',current)
work=base['scene']
if bpy.context.scene!=work and not (current.get('base') is base and bpy.context.scene==current.get('scene')):
    raise RuntimeError('活动场景已被用户切换；先检查当前目标，不能盲改旧阶段。')
original=base['parts']
if len(original)!=7 or any(len(o.data.vertices)!=8 for o in original):
    raise RuntimeError('本阶段要求当前七个可编辑方块仍是未雕刻的八角点方块。')
floor=base['floor']
scene=bpy.data.scenes.new('固源岩组_参考小崩口全侧修订')
bpy.context.window.scene=scene
scene.world=work.world.copy();scene.render.engine=work.render.engine
scene.unit_settings.system=work.unit_settings.system;scene.unit_settings.scale_length=work.unit_settings.scale_length
scene.view_settings.view_transform=work.view_settings.view_transform;scene.view_settings.look=work.view_settings.look
scene.view_settings.exposure=work.view_settings.exposure;scene.view_settings.gamma=work.view_settings.gamma
scene.cycles.samples=20
scene.render.resolution_x=scene.render.resolution_y=1000;scene.render.resolution_percentage=100
scene.render.image_settings.file_format='PNG';scene.render.film_transparent=True
author=bpy.data.collections.new('04_保留完整方块主体_可编辑崩口')
game=bpy.data.collections.new('04_实体模型_导出')
cutters=bpy.data.collections.new('04_崩口减材刀具_不导出')
preview=bpy.data.collections.new('04_预览设备_不导出')
for c in (author,game,cutters,preview):scene.collection.children.link(c)
for o in work.objects:
    if o.type in {'LIGHT','CAMERA'}:
        c=o.copy();c.data=o.data.copy();preview.objects.link(c)
        if c.type=='CAMERA':scene.camera=c
parts=[]
for o in original:
    c=o.copy();c.data=o.data.copy();c.parent=None;c.matrix_world=o.matrix_world.copy();author.objects.link(c);parts.append(c)
camera_quat=base['camera_quat'];camera_location=base['camera_location'].copy()
right,up,front=[base[k].copy() for k in ('right','up','front')]
materials=list(base['materials'])

# edge=(既有角点编号)，t为该棱上的比例，depth为切入深度，width为沿棱宽度。
# 仅在原图可见的崩口/色面接缝附近制作，深度约0.6–2.4原图像素。
specs=[
    (0,2,3,.12,.024,.055,'上灰面左棱上段小缺口'),
    (0,2,3,.36,.019,.049,'上灰面左棱断续崩口'),
    (0,2,3,.63,.031,.066,'上灰面左棱下段缺口'),
    (0,0,1,.31,.014,.035,'上块灰浅交界细崩口'),
    (0,0,1,.65,.012,.033,'上块灰浅交界上段细崩口'),
    (0,4,5,.40,.022,.048,'上块浅面外侧小缺口'),
    (0,0,3,.48,.015,.038,'上块灰褐交界小缺口'),
    (1,0,4,.43,.014,.032,'左块上面与灰面的局部剥落'),
    (1,0,3,.49,.021,.044,'左块灰浅交界小缺口'),
    (1,4,7,.67,.018,.046,'左块浅面外棱缺口'),
    (1,1,2,.34,.016,.041,'左块灰面左棱小缺口'),
    (1,2,3,.61,.012,.031,'左块灰面下棱细崩口，保留接地角'),
    (2,0,4,.52,.0035,.009,'圈内小块保留尺寸，仅细微边缘缺口'),
    (3,4,5,.60,.020,.039,'右前浅面外侧局部崩口'),
    (3,0,4,.74,.012,.029,'右前浅褐交界细缺口'),
    (3,0,3,.56,.020,.039,'右前灰面下侧小缺口'),
    (3,3,7,.59,.010,.025,'右前下棱细崩口，保留最低接地角'),
    (4,0,4,.33,.028,.055,'右后褐浅交界下段凹口'),
    (4,0,4,.59,.021,.045,'右后褐浅交界上段缺口'),
    (4,4,7,.37,.015,.033,'右后浅面外轮廓小缺口'),
    (4,0,3,.61,.013,.030,'右后灰浅交界细缺口'),
]
records=[]
corners=[[v.co.copy() for v in o.data.vertices] for o in parts]
for index,i,j,t,depth,width,label in specs:
    # 第一版实际渲染中的缺口偏弱；仍保持局部尺寸，不动原来的角点和主体边长。
    depth*=1.55 if index!=2 else 1.20
    width*=1.15 if index!=2 else 1.00
    obj=parts[index];v=corners[index]
    axis=[(v[k]-v[0]).normalized() for k in (1,3,4)]
    edge=(v[j]-v[i]).normalized();center=v[i].lerp(v[j],t)
    normals=[]
    for a in axis:
        if abs(a.dot(edge))<.01:
            position=(center-v[0]).dot(a)
            normals.append(a if position>.4*(v[1]-v[0]).length else -a)
    a,b=normals
    # 原图崩口有短平底台阶和浅楔形，不能把全部缺口做成同样的锯齿V槽。
    step_chip=(index==0 and i==2 and t!=.36) or (index==1 and i==0 and j==3) or (index==4 and i==0 and j==4 and t==.33)
    if step_chip:
        profile=[(-.50,1.4),(-.48,-.48),(-.18,-.52),(.22,-.76),(.45,-.47),(.50,1.4)]
        if index==0 and t==.12:
            profile=[(-.5,1.4),(-.43,-.61),(.09,-.63),(.34,-.40),(.50,1.4)]
        n=len(profile)
        vertices=[center+edge*(e*width)+a*(d*depth)+b*offset*depth for offset in [-.72,1.6] for e,d in profile]
        cutter_faces=[list(reversed(range(n))),list(range(n,2*n))]
        cutter_faces += [[k,(k+1)%n,(k+1)%n+n,k+n] for k in range(n)]
        shape='不规则短平底台阶崩口'
    else:
        vertices=[center-a*depth*.72-b*depth*.68,
                  center+a*depth*1.5-b*depth*.25+edge*width*.5,
                  center-a*depth*.25+b*depth*1.5+edge*width*.42,
                  center+a*depth*1.5+b*depth*1.5-edge*width*.58]
        cutter_faces=[(0,1,2),(0,3,1),(0,2,3),(1,3,2)]
        shape='局部非对称浅楔形崩口'
    mesh=bpy.data.meshes.new(label+'_刀具网格')
    mesh.from_pydata(vertices,[],cutter_faces);mesh.update()
    # 凹面沿用矿芯材质，不用纯黑洞或单色贴片伪装缺口。
    mesh.materials.append(obj.data.materials[0])
    bm=bmesh.new();bm.from_mesh(mesh);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(mesh);bm.free()
    cutter=bpy.data.objects.new(label+'_减材刀具',mesh);cutters.objects.link(cutter)
    bpy.context.view_layer.objects.active=obj;obj.select_set(True)
    before=len(obj.data.vertices)
    mod=obj.modifiers.new(label,'BOOLEAN');mod.operation='DIFFERENCE';mod.solver='EXACT';mod.material_mode='TRANSFER';mod.object=cutter
    bpy.ops.object.modifier_apply(modifier=mod.name)
    records.append(dict(block=index,edge=[i,j],t=t,depth=depth,width=width,label=label,shape=shape,
                        changed=len(obj.data.vertices)!=before,world_center=list(center),
                        projected_center=[91+center.dot(right)/.016,91-center.dot(up)/.016]))

root=bpy.data.objects.new('OrirockClusterChip_Root',None);game.objects.link(root)
solid=parts[0].copy();solid.data=parts[0].data.copy();solid.name='固源岩组_完整方块与真实小崩口';game.objects.link(solid);solid.parent=root
bpy.context.view_layer.objects.active=solid
for o in scene.objects:o.select_set(o==solid)
for part in parts[1:]:
    mod=solid.modifiers.new('完整方块实体衔接','BOOLEAN');mod.operation='UNION';mod.solver='EXACT';mod.material_mode='TRANSFER';mod.object=part
    bpy.ops.object.modifier_apply(modifier=mod.name)
bm=bmesh.new();bm.from_mesh(solid.data);bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.000001)
bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bmesh.ops.triangulate(bm,faces=list(bm.faces))
geometry=dict(boundary_edges=sum(e.is_boundary for e in bm.edges),non_manifold_edges=sum(not e.is_manifold for e in bm.edges),volume=abs(bm.calc_volume(signed=True)))
bm.to_mesh(solid.data);bm.free();solid.data.calc_loop_triangles()
geometry.update(triangles=len(solid.data.loop_triangles),floor_z=floor,model_zmin=min(v.co.z for v in solid.data.vertices))
author.hide_render=True;author.hide_viewport=True;cutters.hide_render=True;cutters.hide_viewport=True
solid.data.uv_layers.active_index=0
for uv in solid.data.uv_layers:uv.active_render=(uv.name=='UVMap')
bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(66),island_margin=.02);bpy.ops.object.mode_set(mode='OBJECT')
preserved=[]

def set_front_view():
    scene.camera.location=camera_location;scene.camera.rotation_euler=camera_quat.to_euler()
    for area in bpy.context.screen.areas:
        if area.type=='VIEW_3D':
            space=area.spaces.active;space.shading.type='MATERIAL';space.overlay.show_overlays=False
            space.region_3d.view_rotation=camera_quat;space.region_3d.view_location=Vector((0,0,0))
            space.region_3d.view_distance=4.7;space.region_3d.view_perspective='ORTHO'
    for o in preview.objects:o.hide_set(True)

def status(stage,**extra):
    (OUT/'制作状态.json').write_text(json.dumps(dict(stage=stage,scene=scene.name,geometry=geometry,**extra),ensure_ascii=False,indent=2),encoding='utf-8')

def render_chips():
    set_front_view();scene.render.filepath=str(OUT/'崩口_正面.png');bpy.ops.render.render(write_still=True)
    q=Quaternion(Vector((0,0,1)),math.radians(-48))
    scene.camera.location=q@camera_location;scene.camera.rotation_euler=(q@camera_quat).to_euler()
    scene.render.filepath=str(OUT/'崩口_侧面.png');bpy.ops.render.render(write_still=True)
    set_front_view()
    bpy.data.libraries.write(str(OUT/'OrirockCluster_ReferenceChips.blend'),{scene},fake_user=True)
    status('原图小缺口已制作，待正面与侧面实图检查，尚未整体验收',chips=records,unity_imported=False)
    return None

set_front_view()
bpy.app.driver_namespace['cluster_square_revision']=globals().copy()
bpy.app.timers.register(render_chips,first_interval=1)
print(json.dumps(dict(scene=scene.name,geometry=geometry,chips_changed=sum(r['changed'] for r in records),chips=len(records)),ensure_ascii=False))
