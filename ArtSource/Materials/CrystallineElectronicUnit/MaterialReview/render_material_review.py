"""从保存后的独立材质场景生成实际验收渲染。"""
import bpy,math
from pathlib import Path
from mathutils import Vector

OUT=Path(__file__).resolve().parent
bpy.ops.wm.open_mainfile(filepath=str(OUT/'CrystallineElectronicUnit_MaterialReview.blend'))
scene=bpy.data.scenes['晶体电子单元_材质验收']
by_source={ob.get('白模对象'):ob for ob in scene.objects if ob.get('白模对象')}
scene.cycles.samples=48
scene.render.resolution_x=1100;scene.render.resolution_y=1100
for key,name in [('Reference','参考角度'),('Side','侧面观察'),('Back','背面观察_推定'),('Top','顶部观察')]:
    scene.camera=by_source[name]
    if key=='Top':scene.camera.rotation_euler=(0,0,math.pi/8)
    if key=='Back':
        scene.camera.location=(-math.sin(math.pi/8)*8,math.cos(math.pi/8)*8,5.7)
        scene.camera.rotation_euler=(Vector((0,0,1.2))-scene.camera.location).to_track_quat('-Z','Y').to_euler()
    scene.render.filepath=str(OUT/f'Material_{key}.png')
    bpy.ops.render.render(write_still=True,scene=scene.name)

frame=by_source['侧面_00_局部基准']
data=bpy.data.cameras.new('内嵌散热槽验收');data.type='ORTHO';data.ortho_scale=1.52
cam=bpy.data.objects.new(data.name,data);scene.collection.objects.link(cam)
target=frame.matrix_world@Vector((0,0,.395))
cam.location=target+frame.matrix_world.to_3x3()@Vector((.70,-3.0,.65))
cam.rotation_euler=(target-cam.location).to_track_quat('-Z','Y').to_euler()
scene.camera=cam
scene.render.resolution_y=680
scene.render.filepath=str(OUT/'Material_VentDetail.png')
bpy.ops.render.render(write_still=True,scene=scene.name)

root=by_source['左后水平底座与双侧铁皮支撑_整体']
parts={root,*root.children}
for ob in scene.objects:
    if ob.type in {'MESH','FONT'} and ob not in parts:ob.hide_render=True
points=[ob.matrix_world@vert.co for ob in root.children if ob.type=='MESH' for vert in ob.data.vertices]
target=Vector([(min(p[i] for p in points)+max(p[i] for p in points))/2 for i in range(3)])
data=bpy.data.cameras.new('后组件材质特写');data.type='ORTHO';data.ortho_scale=1.55
cam=bpy.data.objects.new(data.name,data);scene.collection.objects.link(cam)
a=math.pi/8
u=Vector((math.cos(a),math.sin(a),0));v=Vector((-math.sin(a),math.cos(a),0))
cam.location=target+u*1.8-v*4.5+Vector((0,0,2.4))
cam.rotation_euler=(target-cam.location).to_track_quat('-Z','Y').to_euler()
scene.camera=cam
scene.render.resolution_y=900
scene.render.filepath=str(OUT/'Material_RearDetail.png')
bpy.ops.render.render(write_still=True,scene=scene.name)
print('MATERIAL_REVIEW_RENDERS_COMPLETE')
