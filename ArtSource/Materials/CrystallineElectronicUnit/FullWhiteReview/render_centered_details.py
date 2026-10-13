"""实际白模细节和沿底座轴线的俯视预览，不覆盖模型源文件。"""
import bpy, math
from pathlib import Path
from mathutils import Vector, Matrix

OUT=Path(__file__).resolve().parent
bpy.ops.wm.open_mainfile(filepath=str(OUT/'CrystallineElectronicUnit_FullWhite.blend'))
scene=bpy.context.scene
scene.cycles.samples=32
camdata=bpy.data.cameras.new('本轮细节验收')
camdata.type='ORTHO'
cam=bpy.data.objects.new('本轮细节验收',camdata)
scene.collection.objects.link(cam)
scene.camera=cam
a=math.radians(22.5)
u=Vector((math.cos(a),math.sin(a),0));v=Vector((-math.sin(a),math.cos(a),0))

# 图案和后组件的组合中心在俯视图上纵向对齐。
cam.location=(0,0,8)
cam.rotation_euler=(0,0,a)
camdata.ortho_scale=3.65
scene.render.resolution_x=1000;scene.render.resolution_y=1000
scene.render.filepath=str(OUT/'FullWhite_CenteredTop.png')
bpy.ops.render.render(write_still=True)

# 用真实光照显示浅浮雕边框，不添加猜测文字。
labels=bpy.data.objects['台面标签_居中组合']
target=labels.matrix_world.translation+Vector((0,0,.008))
cam.location=target-v*.55+Vector((0,0,1.5))
forward=(target-cam.location).normalized()
right=u
up=right.cross(forward).normalized()
cam.rotation_euler=Matrix((right,up,-forward)).transposed().to_euler()
camdata.ortho_scale=.77
scene.render.resolution_x=1100;scene.render.resolution_y=570
scene.render.filepath=str(OUT/'FullWhite_LabelDetail.png')
bpy.ops.render.render(write_still=True)
print('CENTERED_DETAIL_RENDERS_COMPLETE')
