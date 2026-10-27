"""04正面块面与全侧石质材质修订；保留旧白模与真实地板基准。"""
from pathlib import Path
import json,math,sys,traceback
import bpy,bmesh,numpy as np
from mathutils import Vector,Quaternion

OUT=Path(__file__).resolve().parent
sys.path.insert(0,str(OUT))
from stone_materials import build_face_material
info=json.loads((OUT/'地板基准.json').read_text(encoding='utf-8'))
rows=json.loads((OUT/'当前块体坐标.json').read_text(encoding='utf-8'))
candidate=json.loads((OUT/'地板后撑方案.json').read_text(encoding='utf-8'))
old=bpy.data.scenes.get('固源岩组_角点地板线后撑修订') or bpy.data.scenes[info['scene']]
scene=bpy.data.scenes.new('固源岩组_正面比例与石质全侧修订')
bpy.context.window.scene=scene
scene.world=old.world.copy();scene.render.engine=old.render.engine
scene.unit_settings.system=old.unit_settings.system;scene.unit_settings.scale_length=old.unit_settings.scale_length
scene.view_settings.view_transform='Standard';scene.view_settings.look='None'
scene.view_settings.exposure=0;scene.view_settings.gamma=1
scene.render.resolution_x=scene.render.resolution_y=1000
scene.render.resolution_percentage=100;scene.render.image_settings.file_format='PNG';scene.render.film_transparent=True
author=bpy.data.collections.new('04_七个原始完整立方体_不导出')
game=bpy.data.collections.new('04_全侧实体_导出')
preview=bpy.data.collections.new('04_预览设备地板_不导出')
for col in (author,game,preview):scene.collection.children.link(col)
for obj in old.objects:
    if obj.type in {'LIGHT','CAMERA'}:
        c=obj.copy();c.data=obj.data.copy();preview.objects.link(c)
        if c.type=='CAMERA':scene.camera=c;c.data.ortho_scale=183*.016
        else:c.data.energy*=.42
camera_quat=Quaternion(info['camera_quat']);camera_location=Vector(info['camera_position'])
right,up,front=[Vector(v) for v in info['basis']];floor=info['floor_height']
BOX_FACES=[[0,1,2,3],[4,7,6,5],[0,4,5,1],[1,5,6,2],[2,6,7,3],[3,7,4,0]]
palette=[(208,194,164),(135,115,73),(61,62,63),(99,97,69),(101,87,58),
         (111,103,82),(24,29,30),(32,46,53),(191,178,143),(209,195,163)]
block_colors=[
    [(61,62,63),(85,85,65),(210,197,169),(111,97,64),(84,83,62),(112,100,69)],
    [(31,43,49),(102,90,61),(105,103,72),(41,47,49),(96,91,65),(208,195,166)],
    [(133,126,106),(48,52,52),(193,180,144),(43,49,50),(83,85,67),(200,187,153)],
    [(35,47,53),(47,48,46),(210,198,169),(56,58,53),(92,91,67),(137,120,79)],
    [(80,75,73),(139,117,70),(150,127,76),(131,109,64),(124,106,69),(206,193,157)],
    [(73,74,62),(29,41,48),(37,42,41),(41,50,53),(83,85,62),(34,45,49)],
    [(39,46,49),(68,72,58),(98,89,62),(36,44,46),(82,80,58),(36,46,49)]]
parts=[];materials=[]

def point(p):return right*((p[0]-91)*.016)+up*((91-p[1])*.016)+front*p[2]

def make_cube(name,vertices,block):
    verts=[Vector(v) for v in vertices]
    mesh=bpy.data.meshes.new(name+'_六面网格');mesh.from_pydata(verts,[],BOX_FACES);mesh.update()
    # UVMap为导出/烘焙第0通道，StoneFace保留每个真实平面的连续0..1坐标。
    uv=mesh.uv_layers.new(name='UVMap');face_uv=mesh.uv_layers.new(name='StoneFace')
    axes=np.column_stack([np.array(verts[i]-verts[0]) for i in [1,3,4]])
    cube_coord=np.array([np.linalg.solve(axes,np.array(v-verts[0])) for v in verts])
    varying=[(0,1),(0,1),(0,2),(1,2),(0,2),(1,2)]
    for k,poly in enumerate(mesh.polygons):
        rgb=block_colors[block][k]
        light=np.mean(rgb)>145;dark=np.mean(rgb)<80
        weather=(135,112,66) if light else ((113,99,64) if dark else (76,73,52))
        amount=.13 if light else (.07 if dark else .15)
        mat=build_face_material('04_'+str(block)+'_'+str(k)+'_石质面',rgb,weather,amount,1)
        mesh.materials.append(mat);materials.append(mat);poly.material_index=k
        a,b=varying[k]
        for li in poly.loop_indices:
            index=mesh.loops[li].vertex_index
            value=tuple(float(np.clip(cube_coord[index,c],0,1)) for c in (a,b))
            uv.data[li].uv=value;face_uv.data[li].uv=value
    # 顶块风化帽与浅侧面的下缘风化属于材质，不拆出三角片或额外棱角。
    obj=bpy.data.objects.new(name,mesh);author.objects.link(obj);parts.append(obj)
    bm=bmesh.new();bm.from_mesh(mesh);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(mesh);bm.free()
    obj['结构']='完整正常立方体：正交等长三轴，共用八角点；不同颜色只在六面材质上。'
    obj['边长']=(verts[1]-verts[0]).length
    return obj

for j,row in enumerate(rows):make_cube(row['name'].split('.')[0],row['world_vertices'],j)
for j,name in enumerate(['后方_落地完整源岩_承托','后方_不同朝向完整源岩_斜靠']):
    verts=[point(p) for p in candidate['camera_vertices'][j]]
    zmin=min(v.z for v in verts)
    if zmin<floor:verts=[v+Vector((0,0,floor-zmin)) for v in verts]
    make_cube(name,verts,5+j)

root=bpy.data.objects.new('OrirockClusterMatch_Root',None);game.objects.link(root)
solid=parts[0].copy();solid.data=parts[0].data.copy();game.objects.link(solid);solid.parent=root
solid.name='固源岩组_完整七方块石质实体'
for obj in scene.objects:obj.select_set(obj==solid)
bpy.context.view_layer.objects.active=solid
for part in parts[1:]:
    mod=solid.modifiers.new('完整方块衔接','BOOLEAN');mod.operation='UNION';mod.solver='EXACT';mod.material_mode='TRANSFER';mod.object=part
    bpy.ops.object.modifier_apply(modifier=mod.name)
bm=bmesh.new();bm.from_mesh(solid.data)
bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.000001)
bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bmesh.ops.triangulate(bm,faces=list(bm.faces))
geometry=dict(boundary_edges=sum(e.is_boundary for e in bm.edges),volume=abs(bm.calc_volume(signed=True)))
bm.to_mesh(solid.data);bm.free();solid.data.calc_loop_triangles()
geometry['triangles']=len(solid.data.loop_triangles);geometry['floor_z']=floor
geometry['model_zmin']=min(v.co.z for v in solid.data.vertices)
author.hide_render=True;author.hide_viewport=True
solid.data.uv_layers.active_index=0
for uv in solid.data.uv_layers:uv.active_render=(uv.name=='UVMap')
bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(66),island_margin=.02)
bpy.ops.object.mode_set(mode='OBJECT')
preserved=[]

def set_front_view():
    scene.camera.location=camera_location;scene.camera.rotation_euler=camera_quat.to_euler()
    for area in bpy.context.screen.areas:
        if area.type=='VIEW_3D':
            area.spaces.active.shading.type='MATERIAL';area.spaces.active.overlay.show_overlays=False
            area.spaces.active.region_3d.view_rotation=camera_quat
            area.spaces.active.region_3d.view_location=Vector((0,0,0));area.spaces.active.region_3d.view_distance=4.7
            area.spaces.active.region_3d.view_perspective='ORTHO'
    for obj in preview.objects:obj.hide_set(True)

def status(stage,**extra):
    (OUT/'制作状态.json').write_text(json.dumps(dict(stage=stage,scene=scene.name,geometry=geometry,**extra),ensure_ascii=False,indent=2),encoding='utf-8')

def render_surface():
    try:
        files={}
        for name,angle in [('正面',0),('背面',180),('左侧',90),('右侧',-90)]:
            q=Quaternion(Vector((0,0,1)),math.radians(angle))
            scene.camera.location=q@camera_location;scene.camera.rotation_euler=(q@camera_quat).to_euler()
            scene.render.filepath=str(OUT/('石质_'+name+'.png'));bpy.ops.render.render(write_still=True);files[name]=scene.render.filepath
        set_front_view()
        bpy.data.libraries.write(str(OUT/'OrirockCluster_SurfaceMatch.blend'),{scene},fake_user=True)
        status('块面比例与全侧石质已修订，待正面对照',renders=files,unity_imported=False)
    except Exception:status('渲染失败',error=traceback.format_exc())
    return None

set_front_view()
bpy.app.driver_namespace['cluster_square_revision']=globals().copy()
bpy.app.timers.register(render_surface,first_interval=1)
print(json.dumps(dict(scene=scene.name,geometry=geometry,materials=len(materials)),ensure_ascii=False))
