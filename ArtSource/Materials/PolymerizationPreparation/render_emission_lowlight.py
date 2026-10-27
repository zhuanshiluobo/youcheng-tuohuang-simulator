"""弱光验收图，不保存覆盖正式材质版灯光。"""
import bpy
from pathlib import Path
OUT=Path('F:/游城拓荒模拟器/ArtSource/Materials/PolymerizationPreparation/GlassEmissionReview')
bpy.ops.wm.open_mainfile(filepath=str(OUT/'PolymerizationPreparation_GlassEmission.blend'))
s=bpy.context.scene
for ob in s.objects:
    if ob.type=='LIGHT':ob.data.energy*=.16
for n in s.world.node_tree.nodes:
    if n.type=='BACKGROUND':n.inputs['Strength'].default_value*=.16
s.render.resolution_x=850;s.render.resolution_y=850
s.cycles.samples=48;s.cycles.use_denoising=True
s.render.threads_mode='FIXED';s.render.threads=4
s.render.filepath=str(OUT/'PolymerizationPreparation_EmissionLowLight.png')
bpy.ops.render.render(write_still=True)
print('LOWLIGHT_DONE',flush=True)
