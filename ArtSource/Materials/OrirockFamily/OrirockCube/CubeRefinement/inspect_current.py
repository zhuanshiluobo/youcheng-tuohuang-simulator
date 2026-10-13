import bpy, bmesh, json
from pathlib import Path
from mathutils import Vector

OUT = Path(__file__).resolve().parent
SRC = OUT.parent / 'FrontRevision' / 'OrirockCube_FrontRevision.blend'
bpy.ops.wm.open_mainfile(filepath=str(SRC))
result = {'version': bpy.app.version_string, 'file': bpy.data.filepath, 'scenes': []}
for scene in bpy.data.scenes:
    row = {'name': scene.name, 'engine': scene.render.engine, 'objects': []}
    for obj in scene.objects:
        info = {'name': obj.name, 'type': obj.type, 'size': list(obj.dimensions), 'location': list(obj.location)}
        if obj.type == 'MESH':
            info['materials'] = [m.name for m in obj.data.materials]
            info['bounds'] = [[min(v.co[i] for v in obj.data.vertices), max(v.co[i] for v in obj.data.vertices)] for i in range(3)]
        row['objects'].append(info)
    result['scenes'].append(row)
print(json.dumps(result, ensure_ascii=False), flush=True)
(OUT/'current_inspection.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
scene = next(s for s in bpy.data.scenes if '游戏' in s.name)
bpy.context.window.scene = scene
scene.cycles.samples = 16
scene.render.threads = 8
scene.render.resolution_x = scene.render.resolution_y = 700
for name, pos in [('before_front',(3.8,-6.2,4)),('before_back',(-3.8,6.2,3.2))]:
    scene.camera.location = pos
    scene.camera.rotation_euler = (-scene.camera.location).to_track_quat('-Z','Y').to_euler()
    scene.render.filepath = str(OUT/(name+'.png'))
    bpy.ops.render.render(write_still=True)
