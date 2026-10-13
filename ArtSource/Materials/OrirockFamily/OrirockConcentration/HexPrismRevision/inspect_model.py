import bpy, json
from pathlib import Path

base = Path(__file__).resolve().parent
source = base.parent / 'FrontRevision' / 'OrirockConcentration_FrontRevision.blend'
bpy.ops.wm.open_mainfile(filepath=str(source))
scene = bpy.context.scene
info = {'version': bpy.app.version_string, 'file': bpy.data.filepath, 'scene': scene.name,
        'engine': scene.render.engine, 'camera': scene.camera.name if scene.camera else None,
        'objects': []}
for obj in scene.objects:
    item = {'name': obj.name, 'type': obj.type, 'location': list(obj.location),
            'rotation': list(obj.rotation_euler), 'scale': list(obj.scale)}
    if obj.type == 'MESH':
        item.update(mesh=obj.data.name, vertices=len(obj.data.vertices),
                    materials=[m.name for m in obj.data.materials])
    info['objects'].append(item)
(base / 'source_inspection.json').write_text(json.dumps(info, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(info, ensure_ascii=False), flush=True)
scene.render.resolution_x = scene.render.resolution_y = 600
scene.cycles.samples = 20
scene.render.filepath = str(base / '原模型.png')
bpy.ops.render.render(write_still=True)
