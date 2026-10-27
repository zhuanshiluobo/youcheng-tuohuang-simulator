"""在独立材质场景校准正面，不更改方块和背撑的几何。"""
from pathlib import Path
import bpy,json,bmesh,math
OUT=Path(__file__).resolve().parent
base=bpy.app.driver_namespace['cluster_square_revision'];work=base['scene']
if bpy.app.is_job_running('RENDER'):raise RuntimeError('已有渲染运行中')
scene=bpy.data.scenes.new('固源岩组_正面色面与风化修订')
scene.world=work.world.copy();scene.render.engine=work.render.engine
for key in ('view_transform','look','exposure','gamma'):setattr(scene.view_settings,key,getattr(work.view_settings,key))
scene.cycles.samples=32;scene.cycles.use_denoising=True
scene.render.resolution_x=scene.render.resolution_y=1000;scene.render.resolution_percentage=100
scene.render.image_settings.file_format='PNG';scene.render.image_settings.color_mode='RGBA';scene.render.film_transparent=True
objects={};collections={};materials={}
for old in work.collection.children:
 new=bpy.data.collections.new(old.name+'_色面');scene.collection.children.link(new)
 new.hide_render,new.hide_viewport=old.hide_render,old.hide_viewport;collections[old]=new
 for obj in old.objects:
  if obj not in objects:
   copy=obj.copy()
   if obj.data:copy.data=obj.data.copy()
   objects[obj]=copy
  new.objects.link(objects[obj])
for old,new in objects.items():
 new.parent=objects.get(old.parent);new.matrix_world=old.matrix_world.copy()
 if new.type=='MESH':
  for j,m in enumerate(new.data.materials):
   if not m:continue
   if m not in materials:
    materials[m]=m.copy();materials[m].name=m.name+'_色面校准'
   new.data.materials[j]=materials[m]
scene.camera=objects[work.camera];bpy.context.window.scene=scene
parts=[objects[p] for p in base['parts']];solid=objects[base['solid']];floor_object=objects[base['floor_object']]
root=objects[base['root']];preview=collections[base['preview']]
# 延续用户指出的背部透空修正，只改后撑6、7；不重置回有洞版本。
gap_plan_path=OUT/'背部透空修正方案.json'
geometry=base['geometry'].copy()
if gap_plan_path.exists():
 for spec in json.loads(gap_plan_path.read_text(encoding='utf-8'))['supports']:
  for vertex,co in zip(parts[spec['index']].data.vertices,spec['vertices']):vertex.co=co
  parts[spec['index']].data.update()
 game=collections[base['game']]
 code=(base['OUT']/'apply_corrected_floor.py').read_text(encoding='utf-8-sig')
 start=code.index('def rebuild_solid():');end=code.index('\nrebuild_solid()',start)
 exec(compile(code[start:end],str(OUT/'rebuild_gap_mesh'),'exec'),globals())
 rebuild_solid()

# 实际同角度渲染与原图的面内中位数，不使用稀有度背景色。
samples=[(0,2,[216,191,142],[218,193,151]),(0,5,[107,97,65],[114,102,68]),(4,2,[168,135,84],[135,115,76]),(4,0,[79,70,62],[87,81,80]),(1,0,[43,51,59],[45,53,61]),(1,2,[106,98,73],[99,86,65]),(1,5,[181,164,127],[216,201,164]),(3,0,[43,51,59],[38,44,51]),(3,2,[200,183,150],[205,186,145]),(3,5,[107,90,57],[93,75,47]),(0,6,[108,83,44],[128,99,63])]
def linear(c):
 v=c/255.;return v/12.92 if v<=.04045 else ((v+.055)/1.055)**2.4
changes=[]
for i,j,ref,old in samples:
 mat=parts[i].data.materials[j];nt=mat.node_tree;bsdf=next(n for n in nt.nodes if n.type=='BSDF_PRINCIPLED')
 source=bsdf.inputs['Base Color'].links[0].from_socket
 node=nt.nodes.new('ShaderNodeMixRGB');node.blend_type='MULTIPLY';node.inputs[0].default_value=1
 node.label='同角度正面面内校色';node.inputs[2].default_value=(*[linear(a)/linear(b) for a,b in zip(ref,old)],1)
 nt.links.new(source,node.inputs[1]);nt.links.new(node.outputs[0],bsdf.inputs['Base Color'])
 changes.append(dict(part=i,material_slot=j,reference=ref,before=old,factor=list(node.inputs[2].default_value)))
renders={}
def set_front_view():
 scene.camera.location=base['camera_location'];scene.camera.rotation_euler=base['camera_quat'].to_euler()
 for area in bpy.context.screen.areas:
  if area.type=='VIEW_3D':
   space=area.spaces.active;space.shading.type='MATERIAL';space.lock_camera=False
   space.region_3d.view_rotation=base['camera_quat'];space.region_3d.view_location=base['T'];space.region_3d.view_distance=4.1
   space.region_3d.view_perspective='CAMERA';space.region_3d.view_camera_zoom=4;space.region_3d.view_camera_offset=(0,0)
 for obj in preview.objects:obj.hide_set(obj!=floor_object)
 floor_object.hide_render=True
 scene.camera.name='固源岩组_原图正面对照_小键盘0返回'
def save_stage():
 bpy.data.libraries.write(str(OUT/'OrirockCluster_FaceWeathering.blend'),{scene,bpy.context.workspace,bpy.context.screen},fake_user=True)
 (OUT/'制作状态.json').write_text(json.dumps(dict(scene=scene.name,source_scene=work.name,front_six_parts_unchanged=True,geometry=geometry,rear_gap_plan=str(OUT/'背部透空修正方案.json'),materials=changes,renders=renders,full_reference_match_accepted=False,unity_imported=False),ensure_ascii=False,indent=2),encoding='utf-8')
def render_front():
 set_front_view();scene.render.filepath=str(OUT/'色面校准_正面.png');bpy.ops.render.render(write_still=True)
 renders['front']=scene.render.filepath;save_stage();return None
scene['制作说明']='独立材质阶段；保留前五块、中央暗部和倾斜背撑；默认正面对照相机。'
scene['已导入Unity']=False
set_front_view();save_stage()
bpy.app.driver_namespace['cluster_face_revision']=globals().copy()
bpy.app.timers.register(render_front,first_interval=.4)
print(json.dumps(dict(scene=scene.name,adjusted_faces=len(changes),render_queued=True),ensure_ascii=False))
