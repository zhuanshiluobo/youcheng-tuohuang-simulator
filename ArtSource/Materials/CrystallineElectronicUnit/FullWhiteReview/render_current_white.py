"""只渲染已保存的当前白模，保留用户在实时文件中的编辑。"""
import bpy
from pathlib import Path
from mathutils import Vector
OUT=Path(__file__).resolve().parent
bpy.ops.wm.open_mainfile(filepath=str(OUT/'CrystallineElectronicUnit_FullWhite.blend'))
scene=bpy.context.scene
scene.cycles.samples=24
scene.render.resolution_x=900;scene.render.resolution_y=900
for key,name in [('Reference','参考角度'),('Side','侧面观察'),('Back','背面观察_推定'),('Top','顶部观察')]:
    scene.camera=bpy.data.objects[name]
    scene.render.filepath=str(OUT/f'FullWhite_{key}.png')
    bpy.ops.render.render(write_still=True,scene=scene.name)

camera=bpy.data.cameras.new('支架局部验收')
camera.type='ORTHO';camera.ortho_scale=1.63
ob=bpy.data.objects.new('支架局部验收',camera);scene.collection.objects.link(ob)
module=bpy.data.collections['新增_左后水平底座与铁皮支撑']
points=[obj.matrix_world@vertex.co for obj in module.objects if obj.type=='MESH' for vertex in obj.data.vertices]
target=Vector([(min(p[i] for p in points)+max(p[i] for p in points))/2 for i in range(3)])
ob.location=target+Vector((2.3,-5.5,2.51))
ob.rotation_euler=(target-ob.location).to_track_quat('-Z','Y').to_euler()
scene.camera=ob
# 近看时单独显示底座、两侧铁皮与绕组，避免顶部遮挡支架内部。
module=bpy.data.collections['新增_左后水平底座与铁皮支撑']
visible=set(module.objects)
for item in scene.objects:
    if item.type in {'MESH','FONT'} and item not in visible:item.hide_render=True
scene.render.resolution_x=1000;scene.render.resolution_y=850
scene.render.filepath=str(OUT/'FullWhite_BracketDetail.png')
bpy.ops.render.render(write_still=True,scene=scene.name)
print('CURRENT_WHITE_RENDERS_COMPLETE')
