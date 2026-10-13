"""现有完整方块及崩口随八角点笼校形；独立观察版本，不覆盖正式游戏资产。"""
from pathlib import Path
import bpy,bmesh,json,math
import numpy as np
from mathutils import Vector,Quaternion

OUT=Path(__file__).resolve().parent
base=bpy.app.driver_namespace['cluster_square_revision'];work=base['scene']
if bpy.context.scene!=work or bpy.app.is_job_running('RENDER'):
    raise RuntimeError('当前场景变化或渲染仍运行，先核对现场。')
old_rows=json.loads((OUT.parent/'SurfaceMatchRevision'/'当前块体坐标.json').read_text(encoding='utf-8'))
rows=json.loads((OUT/'正面校形坐标.json').read_text(encoding='utf-8'))
chip_records=json.loads((OUT.parent/'ChipMatchRevision'/'制作状态.json').read_text(encoding='utf-8'))['chips']
scene=bpy.data.scenes.new('固源岩组_原图六面块体校形')
scene.world=work.world.copy();scene.render.engine=work.render.engine;scene.cycles.samples=24
scene.unit_settings.system=work.unit_settings.system;scene.unit_settings.scale_length=work.unit_settings.scale_length
scene.view_settings.view_transform=work.view_settings.view_transform;scene.view_settings.look=work.view_settings.look
scene.view_settings.exposure=work.view_settings.exposure;scene.view_settings.gamma=work.view_settings.gamma
scene.render.resolution_x=scene.render.resolution_y=1000;scene.render.resolution_percentage=100
scene.render.image_settings.file_format=work.render.image_settings.file_format;scene.render.film_transparent=True
objects={};materials_map={};collections={}
for old in work.collection.children:
    new=bpy.data.collections.new(old.name+'_正面校形');scene.collection.children.link(new)
    new.hide_render=old.hide_render;new.hide_viewport=old.hide_viewport;collections[old]=new
    for o in old.objects:
        if o==base['solid']:continue
        c=o.copy()
        if c.data:c.data=c.data.copy()
        new.objects.link(c);objects[o]=c
        if c.type=='MESH':
            for k,m in enumerate(c.data.materials):
                if m not in materials_map:materials_map[m]=m.copy()
                c.data.materials[k]=materials_map[m]
for old,new in objects.items():
    new.parent=objects.get(old.parent);new.matrix_world=old.matrix_world.copy()
scene.camera=objects[work.camera];bpy.context.window.scene=scene
parts=[objects[o] for o in base['parts']];root=objects[base['root']]
preview=collections[base['preview']]
right,up,front=[base[k].copy() for k in ('right','up','front')]
floor=base['floor'];camera_location=base['camera_location'].copy();camera_quat=base['camera_quat'].copy()
bits=np.array([[0,0,0],[1,0,0],[1,1,0],[0,1,0],[0,0,1],[1,0,1],[1,1,1],[0,1,1]])

def deform(mesh,block):
    old=np.array(old_rows[block]['world_vertices']);new=np.array(rows[block]['world_vertices'])
    axes=np.column_stack([old[i]-old[0] for i in [1,3,4]])
    for v in mesh.vertices:
        p=np.linalg.solve(axes,np.array(v.co)-old[0])
        weights=np.prod(np.where(bits==1,p,1-p),axis=1)
        v.co=weights@new
    mesh.update()
    bm=bmesh.new();bm.from_mesh(mesh);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(mesh);bm.free()

for block,part in enumerate(parts[:5]):
    deform(part.data,block)
    part['结构']='同一完整六面块状岩体；共角主面垂直，远棱按手绘校形，保留真实小缺口。'
for old,new in objects.items():
    if new.type!='MESH' or new in parts:continue
    rec=next((r for r in chip_records if old.name.startswith(r['label']+'_减材刀具')),None)
    if rec:deform(new.data,rec['block'])

game=next(c for c in scene.collection.children if root in list(c.objects))
solid=parts[0].copy();solid.data=parts[0].data.copy();solid.parent=root
solid.name='固源岩组_原图六面方块闭合实体';game.objects.link(solid)
for o in scene.objects:o.select_set(o==solid)
bpy.context.view_layer.objects.active=solid
for p in parts[1:]:
    mod=solid.modifiers.new('闭合方块真实衔接','BOOLEAN')
    mod.operation='UNION';mod.solver='EXACT';mod.material_mode='TRANSFER';mod.object=p
    bpy.ops.object.modifier_apply(modifier=mod.name)
bm=bmesh.new();bm.from_mesh(solid.data)
bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.000001)
bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bmesh.ops.triangulate(bm,faces=list(bm.faces))
seen=set();components=[]
for v in bm.verts:
    if v in seen:continue
    seen.add(v);todo=[v];count=0
    while todo:
        a=todo.pop();count+=1
        for e in a.link_edges:
            b=e.other_vert(a)
            if b not in seen:seen.add(b);todo.append(b)
    components.append(count)
geometry=dict(boundary_edges=sum(e.is_boundary for e in bm.edges),non_manifold_edges=sum(not e.is_manifold for e in bm.edges),
              volume=abs(bm.calc_volume(signed=True)),connected_components=components)
bm.to_mesh(solid.data);bm.free();solid.data.calc_loop_triangles()
geometry.update(triangles=len(solid.data.loop_triangles),model_zmin=min(v.co.z for v in solid.data.vertices),floor_z=floor)
if geometry['model_zmin']<floor-1e-6:raise RuntimeError('校形后的实体进入地板线以下。')
solid.data.uv_layers.active_index=0
for uv in solid.data.uv_layers:uv.active_render=(uv.name=='UVMap')
bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(66),island_margin=.02);bpy.ops.object.mode_set(mode='OBJECT')
materials=list(solid.data.materials);preserved=[]

def set_front_view():
    scene.camera.location=camera_location;scene.camera.rotation_euler=camera_quat.to_euler()
    for area in bpy.context.screen.areas:
        if area.type=='VIEW_3D':
            s=area.spaces.active;s.shading.type='MATERIAL';s.overlay.show_overlays=False
            s.region_3d.view_rotation=camera_quat;s.region_3d.view_perspective='ORTHO'
            region=next(r for r in area.regions if r.type=='WINDOW')
            with bpy.context.temp_override(area=area,region=region):bpy.ops.view3d.view_selected(use_all_regions=False)
            s.region_3d.view_distance*=1.10
    for o in preview.objects:o.hide_set(True)

def render_shape_revision():
    outputs={}
    for label,angle in [('正面',0),('背侧',150)]:
        q=Quaternion(Vector((0,0,1)),math.radians(angle))
        scene.camera.location=q@camera_location;scene.camera.rotation_euler=(q@camera_quat).to_euler()
        scene.render.filepath=str(OUT/('校形_'+label+'.png'));bpy.ops.render.render(write_still=True)
        outputs[label]=scene.render.filepath
    set_front_view()
    bpy.data.libraries.write(str(OUT/'OrirockCluster_ReferenceShape.blend'),{scene},fake_user=True)
    (OUT/'制作状态.json').write_text(json.dumps(dict(stage='完整方块参照手绘远棱校形，待正面实图评估',scene=scene.name,
        geometry=geometry,renders=outputs,posterior_cubes=2,shape_note='六面块体而非前侧薄片，远侧略校形；非严格等边数学立方体',unity_imported=False),ensure_ascii=False,indent=2),encoding='utf-8')
    return None

set_front_view();bpy.app.driver_namespace['cluster_square_revision']=globals().copy()
bpy.app.timers.register(render_shape_revision,first_interval=1)
print(json.dumps(dict(scene=scene.name,geometry=geometry),ensure_ascii=False))
