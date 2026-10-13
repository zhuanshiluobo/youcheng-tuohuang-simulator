import bpy, json
from pathlib import Path
from mathutils import Vector

out=Path(__file__).resolve().parent
bpy.ops.wm.open_mainfile(filepath=str(out.parent/'OrirockConcentration_Finished.blend'))
for scene in bpy.data.scenes:
    if any(o.type=='MESH' for o in scene.objects):
        bpy.context.window.scene=scene
        break
data={'version':bpy.app.version_string,'scene':bpy.context.scene.name,'units':bpy.context.scene.unit_settings.system,'objects':[]}
for o in bpy.context.scene.objects:
    row={'name':o.name,'type':o.type,'location':list(o.location),'scale':list(o.scale),'rotation':list(o.rotation_euler)}
    if o.type=='MESH':
        row.update(vertices=len(o.data.vertices),polygons=len(o.data.polygons),materials=[m.name for m in o.data.materials],bounds=[list(o.matrix_world@Vector(v)) for v in o.bound_box])
    data['objects'].append(row)
(out/'source_inspection.json').write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
