"""保留正面五块，按角点地板线独立制作后方两块与观察地板。"""
from pathlib import Path
import json, math
import bpy, bmesh
from mathutils import Vector, Quaternion

OUT=Path(__file__).resolve().parent
info=json.loads((OUT/'地板基准.json').read_text(encoding='utf-8'))
rows=json.loads((OUT/'当前块体坐标.json').read_text(encoding='utf-8'))
candidate=json.loads((OUT/'地板后撑方案.json').read_text(encoding='utf-8'))
old=bpy.data.scenes[info['scene']]
scene=bpy.data.scenes.new('固源岩组_角点地板线后撑修订')
bpy.context.window.scene=scene
scene.world=old.world.copy();scene.render.engine=old.render.engine
scene.view_settings.view_transform=old.view_settings.view_transform
scene.unit_settings.system=old.unit_settings.system
scene.unit_settings.scale_length=old.unit_settings.scale_length
scene.render.resolution_x=scene.render.resolution_y=900
scene.render.resolution_percentage=100;scene.render.image_settings.file_format='PNG'
scene.render.film_transparent=False
scene.cycles.samples=32
author=bpy.data.collections.new('04_七个可编辑立方体_不导出')
game=bpy.data.collections.new('04_模型实体_导出')
preview=bpy.data.collections.new('04_地板相机灯光_不导出')
for col in (author,game,preview):scene.collection.children.link(col)
for obj in old.objects:
    if obj.type in {'CAMERA','LIGHT'}:
        c=obj.copy();c.data=obj.data.copy();preview.objects.link(c)
        if obj.type=='CAMERA':scene.camera=c
camera_quat=Quaternion(info['camera_quat']);camera_location=Vector(info['camera_position'])
right,up,front=[Vector(v) for v in info['basis']]
materials=list(old.objects[info['primary'][0]].data.materials)
floor=info['floor_height']
parts=[]

def point(p):
    return right*((p[0]-91)*.016)+up*((91-p[1])*.016)+front*p[2]

def mesh_obj(name,verts,faces,indices):
    mesh=bpy.data.meshes.new(name+'_网格');mesh.from_pydata(verts,[],faces);mesh.update()
    for mat in materials:mesh.materials.append(mat)
    for p,i in zip(mesh.polygons,indices):p.material_index=i
    bm=bmesh.new();bm.from_mesh(mesh);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bm.to_mesh(mesh);bm.free()
    obj=bpy.data.objects.new(name,mesh);author.objects.link(obj);parts.append(obj)
    obj['结构']='完整等边立方体；整体旋转与移动，不拆开颜色面。'
    return obj

for row in rows[:5]:
    obj=mesh_obj(row['name'].split('.')[0],row['world_vertices'],row['faces'],row['materials'])
    obj['前侧保留']='本次所有顶点保持不变，前侧倾斜角点不截平。'

faces=[[0,1,2,3],[4,7,6,5],[0,4,5,1],[1,5,6,2],[2,6,7,3],[3,7,4,0]]
for j,name in enumerate(['后方_落地大方块_承托','后方_不同朝向大方块_斜靠']):
    verts=[point(p) for p in candidate['camera_vertices'][j]]
    # 仅修正浮点误差；不能将越界块体截成异形块。
    zmin=min(v.z for v in verts)
    if zmin<floor:verts=[v+Vector((0,0,floor-zmin)) for v in verts]
    obj=mesh_obj(name,verts,faces,[5,7,2,2,3,7] if j==0 else [2,3,4,2,3,2])
    obj['边长']=(verts[1]-verts[0]).length
    obj['地板基准']=floor;obj['背侧推定']='正面参考之外的支撑为背侧推定，只有两块。'

root=bpy.data.objects.new('OrirockClusterFloor_Root',None);game.objects.link(root)
solid=parts[0].copy();solid.data=parts[0].data.copy();game.objects.link(solid);solid.parent=root
solid.name='固源岩组_角点地板线完整实体'
for obj in scene.objects:obj.select_set(obj==solid)
bpy.context.view_layer.objects.active=solid
for part in parts[1:]:
    mod=solid.modifiers.new('方块衔接','BOOLEAN');mod.operation='UNION';mod.solver='EXACT'
    mod.material_mode='TRANSFER';mod.object=part;bpy.ops.object.modifier_apply(modifier=mod.name)
bm=bmesh.new();bm.from_mesh(solid.data)
bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.000001)
bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bmesh.ops.triangulate(bm,faces=list(bm.faces))
geometry=dict(boundary_edges=sum(e.is_boundary for e in bm.edges),volume=abs(bm.calc_volume(signed=True)))
bm.to_mesh(solid.data);bm.free();solid.data.calc_loop_triangles()
geometry['triangles']=len(solid.data.loop_triangles)
geometry['model_zmin']=min((solid.matrix_world@v.co).z for v in solid.data.vertices)
geometry['floor_z']=floor
geometry['primary_max_vertex_change']=0
author.hide_render=True;author.hide_viewport=True

# 观察地板不是模型，导出只选game对象。
mesh=bpy.data.meshes.new('观察地板_不进入游戏')
mesh.from_pydata([(-6,-6,floor), (6,-6,floor), (6,6,floor), (-6,6,floor)],[],[(0,1,2,3)])
ground=bpy.data.objects.new('地板线_前侧最低角点所在平面_不导出',mesh);preview.objects.link(ground)
ground['仅观察']=True
floor_mat=bpy.data.materials.new('地板线观察_浅灰');floor_mat.diffuse_color=(.22,.24,.26,1)
floor_mat.use_nodes=True
bs=next(n for n in floor_mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
bs.inputs['Base Color'].default_value=(.22,.24,.26,1);bs.inputs['Roughness'].default_value=.95
mesh.materials.append(floor_mat)
white=bpy.data.materials.new('角点地板线_白模观察');white.use_nodes=True
bs=next(n for n in white.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
bs.inputs['Base Color'].default_value=(.5,.5,.5,1);bs.inputs['Roughness'].default_value=.95
original_mats=list(solid.data.materials)
for k in range(len(solid.data.materials)):solid.data.materials[k]=white

def set_front_view():
    scene.camera.location=camera_location;scene.camera.rotation_euler=camera_quat.to_euler()
    for area in bpy.context.screen.areas:
        if area.type=='VIEW_3D':
            space=area.spaces.active;space.shading.type='SOLID';space.overlay.show_overlays=True
            space.overlay.show_floor=False
            space.region_3d.view_rotation=camera_quat
            space.region_3d.view_location=Vector((0,0,.08))
            space.region_3d.view_distance=4.6;space.region_3d.view_perspective='ORTHO'

def render_structure():
    files={}
    target=Vector((0,0,.08))
    for label,angle in [('正面',0),('右侧',-90),('背面',180),('左侧',90)]:
        q=Quaternion(Vector((0,0,1)),math.radians(angle))
        scene.camera.location=target+q@(camera_location-target)
        scene.camera.rotation_euler=(q@camera_quat).to_euler()
        scene.render.filepath=str(OUT/('地板线_'+label+'.png'))
        bpy.ops.render.render(write_still=True);files[label]=scene.render.filepath
    set_front_view()
    bpy.data.libraries.write(str(OUT/'OrirockCluster_FloorLine_Blockout.blend'),{scene},fake_user=True)
    (OUT/'制作状态.json').write_text(json.dumps(dict(stage='地板线后撑已修订，白模待正面对照，不添加纹理，不覆盖Unity',scene=scene.name,
        geometry=geometry,posterior_count=2,renders=files,limitations='背侧推定；正面视觉尚未验收。'),ensure_ascii=False,indent=2),encoding='utf-8')
    return None

set_front_view()
bpy.app.driver_namespace['cluster_square_revision']=globals().copy()
bpy.app.timers.register(render_structure,first_interval=1)
print(json.dumps(dict(scene=scene.name,geometry=geometry),ensure_ascii=False))
