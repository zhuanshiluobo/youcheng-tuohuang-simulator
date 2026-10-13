import bpy
from pathlib import Path
from mathutils import Vector
OUT=Path('F:/游城拓荒模拟器/ArtSource/Materials/PolymerizationPreparation/AlignedPivotReview')
bpy.ops.wm.open_mainfile(filepath=str(OUT/'PolymerizationPreparation_Aligned.blend'))
s=bpy.context.scene
s.render.threads_mode='FIXED';s.render.threads=4
s.render.resolution_x=850;s.render.resolution_y=850
s.cycles.samples=48;s.cycles.use_denoising=False
s.camera.location=(0,0,6)
s.camera.rotation_euler=(Vector((0,0,0))-s.camera.location).to_track_quat('-Z','Y').to_euler()
s.render.filepath=str(OUT/'PolymerizationPreparation_TrueTop.png')
bpy.ops.render.render(write_still=True)
print('TOP_DONE',flush=True)
