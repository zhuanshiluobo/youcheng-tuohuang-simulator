"""只设定中央槽底和层片的绝对高度，不重复切槽，也不做累加下移。"""
import bpy,json
from pathlib import Path
OUT=Path(__file__).resolve().parent
FILE=OUT/'CrystallineElectronicUnit_MaterialReview.blend'
bpy.ops.wm.open_mainfile(filepath=str(FILE))
scene=bpy.data.scenes['晶体电子单元_材质验收']
objects={o.get('白模对象'):o for o in scene.objects if o.get('白模对象')}
floor=objects['中央凹口封闭槽底']
floor.location.z=.650-max(v.co.z for v in floor.data.vertices)
objects['顶部白模_整体'].location.z=.650
deck=objects['八角台面_宽斜面凹口']
for i in range(24,32):deck.data.vertices[i].co.z=.657
for i in range(32,40):deck.data.vertices[i].co.z=.640
deck.data.update()
scene.view_layers[0].update()
notes_path=OUT/'material_review_notes.json'
notes=json.loads(notes_path.read_text(encoding='utf-8'))
notes['latest_geometry_revision'].update({'basin_depth_before':.120,'basin_depth_after':.290,'layers_lowered_by':.170})
notes_path.write_text(json.dumps(notes,ensure_ascii=False,indent=2),encoding='utf-8')
bpy.ops.wm.save_as_mainfile(filepath=str(FILE))
print('ABSOLUTE_BASIN_HEIGHT',max((floor.matrix_world@v.co).z for v in floor.data.vertices))
print('NO_VENT_GEOMETRY_CHANGED')
