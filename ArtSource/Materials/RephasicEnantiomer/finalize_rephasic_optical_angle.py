import bpy,json,shutil
from pathlib import Path
from mathutils import Vector
ROOT=Path('F:/游城拓荒模拟器');KEY='RephasicEnantiomer'
ART=ROOT/'ArtSource/Materials'/KEY;GAME=ROOT/'Assets/YC/Presentation/Models'/KEY
bpy.context.preferences.filepaths.save_version=0
scene=bpy.data.scenes.get(KEY+'_Finished');bpy.context.window.scene=scene
cam=scene.camera;cam.location=(2.8,-6,3.8);target=Vector((0,0,.89))
cam.rotation_euler=(target-cam.location).to_track_quat('-Z','Y').to_euler()
mf=json.loads((GAME/'material_manifest.json').read_text(encoding='utf-8'))
mf['reference_camera']={'position':list(cam.location),'target':list(target)}
(GAME/'material_manifest.json').write_text(json.dumps(mf,ensure_ascii=False,indent=2),encoding='utf-8')
preview=ART/(KEY+'_OpticalFront.png');before=ART/(KEY+'_OpticalFront_BeforeAngleCorrection.png')
if preview.exists() and not before.exists():shutil.copy2(preview,before)
scene.render.filepath=str(preview);bpy.ops.render.render(write_still=True)
white=next(m for m in bpy.data.materials if m.name.startswith('RE4_BlockoutWhite'))
scene.view_layers[0].material_override=white
scene.render.filepath=str(ART/(KEY+'_OpticalFront_Blockout.png'));bpy.ops.render.render(write_still=True)
scene.view_layers[0].material_override=None
bpy.ops.wm.save_as_mainfile(filepath=str(ART/(KEY+'_Finished.blend')))
print('FINAL_OPTICAL_FRONT_ANGLE',mf['reference_camera'])
