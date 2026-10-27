"""复用用户提供旧白模中的两个真实后撑；不沿用被否定的重叠方案。"""
import bpy,json
from pathlib import Path
from mathutils import Vector
OUT=Path(__file__).resolve().parent
base=bpy.app.driver_namespace['cluster_face_revision'];oldscene=bpy.data.scenes['固源岩组_三处真实平面截角'];work=base['scene'];floor_base=base['base']
scene=bpy.data.scenes.new('固源岩组_复用旧后部_白模调整')
scene.world=work.world.copy();scene.render.engine=work.render.engine
for key in ('view_transform','look','exposure','gamma'):setattr(scene.view_settings,key,getattr(work.view_settings,key))
scene.cycles.samples=32;scene.cycles.use_denoising=True
scene.render.resolution_x=scene.render.resolution_y=900;scene.render.resolution_percentage=100
scene.render.image_settings.file_format='PNG';scene.render.image_settings.color_mode='RGBA';scene.render.film_transparent=True
model=bpy.data.collections.new('旧后部复用_七块白模');scene.collection.children.link(model)
preview=bpy.data.collections.new('旧后部复用_观察设备不导出');scene.collection.children.link(preview)
white=bpy.data.materials.new('旧后部复用_检查白模');white.use_nodes=True
bsdf=next(n for n in white.node_tree.nodes if n.type=='BSDF_PRINCIPLED');bsdf.inputs['Base Color'].default_value=(.065,.065,.065,1);bsdf.inputs['Roughness'].default_value=.9
parts=[];records=[]
for o in base['parts'][:5]:
 n=o.copy();n.data=o.data.copy();n.parent=None;n.matrix_world=o.matrix_world.copy();model.objects.link(n);n.hide_render=False;n.hide_viewport=False
 n.data.materials.clear();n.data.materials.append(white)
 for f in n.data.polygons:f.material_index=0
 parts.append(n)
for o in oldscene.objects:
 if o.type!='MESH' or not o.name.startswith('后方_'):continue
 n=o.copy();n.data=o.data.copy();n.parent=None;n.matrix_world.identity();n.name='旧版复用_'+o.name;model.objects.link(n);n.hide_render=False;n.hide_viewport=False
 verts=[floor_base['R']@(o.matrix_world@v.co)+floor_base['T'] for v in o.data.vertices];rise=-min(v.z for v in verts)
 for v,co in zip(n.data.vertices,verts):v.co=co+Vector((0,0,rise))
 n.data.update();n.data.materials.clear();n.data.materials.append(white)
 for f in n.data.polygons:f.material_index=0
 records.append(dict(source=o.name,object=n.name,ground_shift=rise,original_size_preserved=True,vertices=[list(v.co) for v in n.data.vertices]));parts.append(n)
for o in base['preview'].objects:
 n=o.copy();n.data=o.data.copy() if o.data else None;n.parent=None;n.matrix_world=o.matrix_world.copy();preview.objects.link(n)
 if o==work.camera:scene.camera=n
 if o==base['floor_object']:floor_object=n
bpy.context.window.scene=scene
renders={}
def set_front_view():
 scene.camera.location=floor_base['camera_location'];scene.camera.rotation_euler=floor_base['camera_quat'].to_euler()
 scene.camera.name='固源岩组_旧后部复用_正面对照_小键盘0'
 floor_object.hide_render=True
 for a in bpy.context.screen.areas:
  if a.type=='VIEW_3D':
   sp=a.spaces.active;sp.shading.type='MATERIAL';sp.region_3d.view_rotation=floor_base['camera_quat'];sp.region_3d.view_location=floor_base['T'];sp.region_3d.view_distance=4.1;sp.region_3d.view_perspective='CAMERA';sp.region_3d.view_camera_zoom=4
 for obj in preview.objects:obj.hide_set(obj!=floor_object)
def save_stage():
 bpy.data.libraries.write(str(OUT/'OrirockCluster_OldRearReuse.blend'),{scene,bpy.context.workspace,bpy.context.screen},fake_user=True)
 (OUT/'制作状态.json').write_text(json.dumps(dict(scene=scene.name,source_scene=oldscene.name,supports=records,renders=renders,front_five_unchanged=True,full_reference_match_accepted=False,unity_imported=False),ensure_ascii=False,indent=2),encoding='utf-8')
def render_checks():
 cam=scene.camera;loc=cam.location.copy();rot=cam.rotation_euler.copy();scale=cam.data.ortho_scale
 try:
  off=Vector((-6,1.4,2));cam.location=Vector((0,0,.85))+off;cam.rotation_euler=(-off).to_track_quat('-Z','Y').to_euler();cam.data.ortho_scale=3.4;floor_object.hide_render=False
  scene.render.filepath=str(OUT/'旧后部直接复用_背面.png');bpy.ops.render.render(write_still=True);renders['back']=scene.render.filepath
  cam.location=loc;cam.rotation_euler=rot;cam.data.ortho_scale=scale;set_front_view()
  scene.render.filepath=str(OUT/'旧后部直接复用_正面.png');bpy.ops.render.render(write_still=True);renders['front']=scene.render.filepath
 finally:
  cam.location=loc;cam.rotation_euler=rot;cam.data.ortho_scale=scale;set_front_view();save_stage()
 return None
set_front_view();save_stage();bpy.app.driver_namespace['cluster_old_rear_reuse']=globals().copy()
bpy.app.timers.register(render_checks,first_interval=.4)
print('旧版两块后撑已直接复制，保持原大小和相对地板适配后的倾向；未重复添加新块，等待白模观察。')
