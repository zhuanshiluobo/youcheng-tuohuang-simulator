"""在已保存的缺口版继续局部减材；不改主块位姿、最低角和后撑。"""
from pathlib import Path
import bpy,bmesh,json,math,re
import numpy as np
from mathutils import Vector,Quaternion

OUT=Path(__file__).resolve().parent
OUT.mkdir(exist_ok=True)
base=bpy.app.driver_namespace['cluster_square_revision']
work=base['scene']
if bpy.context.scene!=work or bpy.app.is_job_running('RENDER'):
    raise RuntimeError('场景已变化或仍在渲染，先读取实际状态。')
records=json.loads((OUT.parent/'ChipMatchRevision'/'制作状态.json').read_text(encoding='utf-8'))['chips']
rows=json.loads((OUT.parent/'SurfaceMatchRevision'/'当前块体坐标.json').read_text(encoding='utf-8'))
scene=bpy.data.scenes.new('固源岩组_小崩口局部精修')
scene.world=work.world.copy();scene.render.engine=work.render.engine
scene.unit_settings.system=work.unit_settings.system
scene.unit_settings.scale_length=work.unit_settings.scale_length
scene.view_settings.view_transform=work.view_settings.view_transform
scene.view_settings.look=work.view_settings.look
scene.view_settings.exposure=work.view_settings.exposure
scene.view_settings.gamma=work.view_settings.gamma
scene.cycles.samples=24
scene.render.resolution_x=scene.render.resolution_y=1000
scene.render.resolution_percentage=100
scene.render.image_settings.file_format=work.render.image_settings.file_format
scene.render.film_transparent=True
material_map={};objects={};collections={}
for old in work.collection.children:
    new=bpy.data.collections.new(old.name+'_崩口精修')
    scene.collection.children.link(new)
    new.hide_render=old.hide_render;new.hide_viewport=old.hide_viewport
    collections[old]=new
    for o in old.objects:
        # 重新合并可编辑块体，旧实体仍完整保留在旧阶段。
        if o==base['solid']:continue
        c=o.copy()
        if o.data:c.data=o.data.copy()
        new.objects.link(c);objects[o]=c
        if c.type=='MESH':
            for i,m in enumerate(c.data.materials):
                if m not in material_map:material_map[m]=m.copy()
                c.data.materials[i]=material_map[m]
for old,new in objects.items():
    new.parent=objects.get(old.parent);new.matrix_world=old.matrix_world.copy()
scene.camera=objects[work.camera]
bpy.context.window.scene=scene
parts=[objects[o] for o in base['parts']]
root=objects[base['root']]
preview=collections[base['preview']]
right,up,front=[base[k].copy() for k in ('right','up','front')]
camera_location=base['camera_location'].copy();camera_quat=base['camera_quat'].copy()
floor=base['floor']

# 只校正当前模型基色：上一版浅色面过曝。游戏公共光与背景保持原状。
adjustments=json.loads((OUT/'色面微调.json').read_text(encoding='utf-8'))
factors={(r['block'],r['face']):r['linear_multiplier'] for r in adjustments}
tone_records=[]
for old,m in material_map.items():
    match=re.match(r'04_(\d+)_(\d+)_',old.name)
    if not match:continue
    key=tuple(map(int,match.groups()))
    if key not in factors:continue
    factor=np.array(factors[key])
    tone=next(n for n in m.node_tree.nodes if n.label=='各真实色面的矿物底色校准')
    tone.inputs[2].default_value=tuple(np.array(tone.inputs[2].default_value[:3])*factor)+(1,)
    m.diffuse_color=tuple(np.array(m.diffuse_color[:3])*factor)+(1,)
    tone_records.append(dict(block=key[0],face=key[1],linear_multiplier=factor.tolist()))

# 三处最清楚的小崩口只增大约一成；主棱端点与接地角均不动。
details=[('上灰面左棱上段小缺口',1.13,1.16,5),
         ('上块浅面外侧小缺口',1.10,1.12,2),
         ('右后褐浅交界下段凹口',1.12,1.15,2)]
chip_changes=[]
for label,width_factor,depth_factor,face_material in details:
    rec=next(r for r in records if r['label']==label)
    cutter=next(c for old,c in objects.items() if old.name.startswith(label+'_减材刀具'))
    block=rec['block'];part=parts[block]
    center=Vector(rec['world_center'])
    original=[Vector(v) for v in rows[block]['world_vertices']]
    i,j=rec['edge'];edge=(original[j]-original[i]).normalized()
    for v in cutter.data.vertices:
        d=v.co-center;along=edge*d.dot(edge)
        v.co=center+along*width_factor+(d-along)*depth_factor
    # 暴露的断口是石质色面，不涂成黑洞。仍为该完整方块的一部分。
    cutter.data.materials[0]=part.data.materials[face_material]
    axes=np.column_stack([np.array(original[k]-original[0]) for k in (1,3,4)])
    coordinates=np.array([np.linalg.solve(axes,np.array(v.co-original[0])) for v in cutter.data.vertices])
    for name in ('UVMap','StoneFace'):
        uv=cutter.data.uv_layers.get(name) or cutter.data.uv_layers.new(name=name)
        for poly in cutter.data.polygons:
            for li in poly.loop_indices:
                vi=cutter.data.loops[li].vertex_index
                uv.data[li].uv=tuple(np.clip(coordinates[vi,:2],0,1))
    bpy.context.view_layer.objects.active=part
    for o in scene.objects:o.select_set(o==part)
    mod=part.modifiers.new(label+'_局部修整','BOOLEAN')
    mod.operation='DIFFERENCE';mod.solver='EXACT';mod.material_mode='TRANSFER';mod.object=cutter
    bpy.ops.object.modifier_apply(modifier=mod.name)
    chip_changes.append(dict(label=label,block=block,width_factor=width_factor,depth_factor=depth_factor))

game=next(c for c in scene.collection.children if root in list(c.objects))
solid=parts[0].copy();solid.data=parts[0].data.copy();solid.parent=root
solid.name='固源岩组_完整实体_局部真实断口'
game.objects.link(solid)
bpy.context.view_layer.objects.active=solid
for o in scene.objects:o.select_set(o==solid)
for p in parts[1:]:
    mod=solid.modifiers.new('方块实体衔接','BOOLEAN')
    mod.operation='UNION';mod.solver='EXACT';mod.material_mode='TRANSFER';mod.object=p
    bpy.ops.object.modifier_apply(modifier=mod.name)
bm=bmesh.new();bm.from_mesh(solid.data)
bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.000001)
bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
bmesh.ops.triangulate(bm,faces=list(bm.faces))
geometry=dict(boundary_edges=sum(e.is_boundary for e in bm.edges),non_manifold_edges=sum(not e.is_manifold for e in bm.edges),volume=abs(bm.calc_volume(signed=True)))
bm.to_mesh(solid.data);bm.free();solid.data.calc_loop_triangles()
geometry.update(triangles=len(solid.data.loop_triangles),model_zmin=min(v.co.z for v in solid.data.vertices),floor_z=floor)
if geometry['model_zmin']<floor-1e-6:raise RuntimeError('出现地板线以下几何，不保存为有效阶段。')
solid.data.uv_layers.active_index=0
for uv in solid.data.uv_layers:uv.active_render=(uv.name=='UVMap')
bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(66),island_margin=.02)
bpy.ops.object.mode_set(mode='OBJECT')
materials=list(solid.data.materials);preserved=[]

def set_front_view():
    scene.camera.location=camera_location;scene.camera.rotation_euler=camera_quat.to_euler()
    for area in bpy.context.screen.areas:
        if area.type=='VIEW_3D':
            space=area.spaces.active;space.shading.type='MATERIAL';space.overlay.show_overlays=False
            space.region_3d.view_rotation=camera_quat;space.region_3d.view_location=Vector((0,0,0))
            space.region_3d.view_distance=4.7;space.region_3d.view_perspective='ORTHO'
    for o in preview.objects:o.hide_set(True)

def render_chip_details():
    outputs={}
    for label,angle in [('正面',0),('侧背',-65)]:
        q=Quaternion(Vector((0,0,1)),math.radians(angle))
        scene.camera.location=q@camera_location;scene.camera.rotation_euler=(q@camera_quat).to_euler()
        scene.render.filepath=str(OUT/('精修_'+label+'.png'))
        bpy.ops.render.render(write_still=True);outputs[label]=scene.render.filepath
    set_front_view()
    bpy.data.libraries.write(str(OUT/'OrirockCluster_ChipDetail.blend'),{scene},fake_user=True)
    (OUT/'制作状态.json').write_text(json.dumps(dict(stage='局部小缺口与断口材质精修，待整体正面对照',scene=scene.name,geometry=geometry,
        chip_changes=chip_changes,tone_changes=tone_records,renders=outputs,main_cube_pose_unchanged=True,
        floor_contact_unchanged=True,posterior_cubes=2,unity_imported=False),ensure_ascii=False,indent=2),encoding='utf-8')
    return None

set_front_view()
bpy.app.driver_namespace['cluster_square_revision']=globals().copy()
bpy.app.timers.register(render_chip_details,first_interval=1)
print(json.dumps(dict(scene=scene.name,geometry=geometry,details=chip_changes),ensure_ascii=False))
