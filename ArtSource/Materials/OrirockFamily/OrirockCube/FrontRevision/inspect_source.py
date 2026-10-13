import bpy, json
from pathlib import Path
out = Path(__file__).resolve().parent
source = out.parent / 'OrirockCube_Finished.blend'
bpy.ops.wm.open_mainfile(filepath=str(source))
info = {'version': bpy.app.version_string, 'file': str(source), 'scenes': []}
for scene in bpy.data.scenes:
    objects = []
    for obj in scene.objects:
        item = {'name': obj.name, 'type': obj.type, 'location': list(obj.location), 'rotation': list(obj.rotation_euler), 'scale': list(obj.scale)}
        if obj.type == 'MESH':
            item.update(vertices=len(obj.data.vertices), polygons=len(obj.data.polygons), materials=[m.name if m else None for m in obj.data.materials])
        objects.append(item)
    info['scenes'].append({'name':scene.name, 'unit_system':scene.unit_settings.system, 'unit_scale':scene.unit_settings.scale_length, 'objects':objects})
(out / 'source_inspection.json').write_text(json.dumps(info, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(info, ensure_ascii=False))
