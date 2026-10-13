import bpy
from pathlib import Path
from mathutils import Vector
KEY='RephasicEnantiomer';ART=Path('F:/游城拓荒模拟器/ArtSource/Materials')/KEY
bpy.context.preferences.filepaths.save_version=0
scene=bpy.data.scenes.get(KEY+'_Finished');bpy.context.window.scene=scene
for obj in scene.objects:
    if obj.type!='LIGHT':continue
    if 'RE5_主反射卡' in obj.name:obj.data.energy=1500;obj.data.size=4;obj.data.size_y=2.88
    if 'RE5_银反射卡' in obj.name:obj.data.energy=1100;obj.data.size=3;obj.data.size_y=2.7
    if 'RE5_背侧反射卡' in obj.name:obj.data.energy=1400;obj.data.size=4;obj.data.size_y=2.8
cam=scene.camera;target=Vector((0,0,.89))
views={'OpticalFront':(2.8,-6,3.8),'OpticalSide':(6.5,0,3.0),'OpticalBack':(0,6.5,3.4),'OpticalQuarter':(-5,-5,3.8)}
for name,loc in views.items():
    cam.location=loc;cam.rotation_euler=(target-cam.location).to_track_quat('-Z','Y').to_euler()
    scene.render.filepath=str(ART/(KEY+'_'+name+'.png'));bpy.ops.render.render(write_still=True)
cam.location=views['OpticalFront'];cam.rotation_euler=(target-cam.location).to_track_quat('-Z','Y').to_euler()
bpy.ops.wm.save_as_mainfile(filepath=str(ART/(KEY+'_Finished.blend')))
print('DENSE_OPTICAL_PREVIEW_READY')
