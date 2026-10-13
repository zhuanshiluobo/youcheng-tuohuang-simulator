"""独立编辑场景分开展示三块原尺寸岩柱，主场景装配位置不动。"""
import bpy,json
from pathlib import Path
from mathutils import Vector

OUT=Path(__file__).resolve().parent
path=OUT/'OrirockConcentration_PerRockChips.blend'
bpy.ops.wm.open_mainfile(filepath=str(path))
bpy.context.preferences.filepaths.save_version=0
main=bpy.context.scene
edit=bpy.data.scenes['逐块编辑_完整岩柱']
layout=json.loads((OUT.parent/'AlignedHexRevision/layout.json').read_text(encoding='utf-8'))
parts=sorted([o for o in edit.objects if o.type=='MESH'],key=lambda o:0 if '左后' in o.name else 1 if '右后' in o.name else 2)
for i,(obj,spec) in enumerate(zip(parts,layout['parts'])):
    obj.location=Vector(((i-1)*1.35-spec['center'][0],-spec['center'][1],0))
    obj['查看说明']='此场景分开展示便于单块编辑；正式装配位置保留在主场景。'
camera=main.camera.copy();camera.data=main.camera.data.copy();camera.name='逐块验收相机'
edit.collection.objects.link(camera);edit.camera=camera
camera.location=(1.4,-7,5.3);camera.rotation_euler=(-camera.location).to_track_quat('-Z','Y').to_euler();camera.data.ortho_scale=4.6
edit.render.engine='BLENDER_WORKBENCH';edit.render.resolution_x=1280;edit.render.resolution_y=650;edit.render.resolution_percentage=100
edit.render.film_transparent=True;edit.display.shading.color_type='MATERIAL';edit.display.shading.light='STUDIO';edit.display.shading.show_cavity=True
edit.render.filepath=str(OUT/'三块独立缺口验收.png')
bpy.ops.render.render(write_still=True,scene=edit.name)
bpy.ops.wm.save_as_mainfile(filepath=str(path))
print('独立岩块验收场景已准备；主场景装配未改变',flush=True)
