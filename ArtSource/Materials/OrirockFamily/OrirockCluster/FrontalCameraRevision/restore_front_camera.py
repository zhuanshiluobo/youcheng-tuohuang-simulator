import bpy,math,json
from pathlib import Path
from mathutils import Vector
OUT=Path(__file__).resolve().parent
scene=bpy.context.scene
camera=scene.camera
if camera is None:raise RuntimeError('白模中没有已有对照相机；停止，不额外创建设备。')
before=dict(location=list(camera.location),rotation=list(camera.rotation_euler),type=camera.data.type)
target=Vector((0,0,.90));angle=math.radians(12);distance=8
camera.location=target+Vector((0,-distance*math.cos(angle),distance*math.sin(angle)))
camera.rotation_euler=(target-camera.location).to_track_quat('-Z','Y').to_euler()
camera.name='固源岩组_正前方轻俯视_固定基准'
# 保留原投影类型与画幅，不通过侧向相机凑造型。
for window in bpy.context.window_manager.windows:
 for area in window.screen.areas:
  if area.type=='VIEW_3D':
   sp=area.spaces.active;sp.region_3d.view_perspective='CAMERA';sp.region_3d.view_camera_zoom=4;sp.region_3d.view_camera_offset=(0,0);sp.lock_camera=False
scene.name='固源岩组_正前方轻俯视_重做白模'
parts=[o for o in scene.objects if o.type=='MESH' and o.name!='04_地板Z0_仅观察不导出.002' and ('单个倾斜立方体' in o.name or '圈内小立方体' in o.name or '旧版复用_' in o.name)]
state=dict(scene=scene.name,camera_before=before,camera_location=list(camera.location),camera_rotation=list(camera.rotation_euler),elevation_degrees=12,azimuth_degrees=0,roll_degrees=0,parts=[o.name for o in parts],model_rebuilt=False,materials_changed=False,lights_changed=False,unity_imported=False)
(OUT/'正面视角基准.json').write_text(json.dumps(state,ensure_ascii=False,indent=2),encoding='utf-8')
bpy.app.driver_namespace['cluster_frontal_revision']=dict(scene=scene,camera=camera,parts=parts,OUT=OUT,target=target)
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'OrirockCluster_FrontView.blend'))
print('已建立正前方轻俯视基准；几何尚未重做。',flush=True)
