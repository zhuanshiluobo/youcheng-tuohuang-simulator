"""只渲染侧后方以检查闭合体块，不改动锁定参考相机的源文件。"""
import bpy, json
from pathlib import Path
from mathutils import Vector
OUT=Path('F:/游城拓荒模拟器/ArtSource/Materials/NucleicCrystalSinter/ReferenceViewMatch')
bpy.ops.wm.open_mainfile(filepath=str(OUT/'NucleicCrystalSinter_SameView.blend'))
s=bpy.context.scene;c=s.camera
s.render.resolution_x=600;s.render.resolution_y=700;s.render.resolution_percentage=100
s.cycles.samples=24;s.render.threads_mode='FIXED';s.render.threads=6
c.data.shift_x=0;c.data.shift_y=0;c.data.lens=65
target=Vector((.1,0,1.25))
for name,position in [('SideCheck',(6.8,-.7,3.5)),('BackCheck',(0,6.8,3.5))]:
    c.location=position;c.rotation_euler=(target-c.location).to_track_quat('-Z','Y').to_euler()
    s.render.filepath=str(OUT/(name+'.png'));bpy.ops.render.render(write_still=True)
print('VOLUME_REVIEW_READY',len([o for o in s.objects if o.type=='MESH']))
