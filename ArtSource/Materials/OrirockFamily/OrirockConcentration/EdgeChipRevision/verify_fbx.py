import bpy
import json
from pathlib import Path
from io_scene_fbx import parse_fbx

out=Path(__file__).resolve().parent
project=out.parents[4]
files=[out/'previous_unity'/'OrirockConcentration_Finished.fbx',
       project/'Assets/YC/Presentation/Models/OrirockFamily/OrirockConcentration/OrirockConcentration_Finished.fbx']

def inspect(path):
    tree,version=parse_fbx.parse(str(path))
    objects=next(e for e in tree.elems if e.id==b'Objects')
    names={kind:[e.props[1].decode('utf-8') for e in objects.elems if e.id==kind.encode()] for kind in ('Geometry','Model')}
    scene=bpy.data.scenes.new(path.parent.name)
    bpy.context.window.scene=scene
    bpy.ops.import_scene.fbx(filepath=str(path))
    meshes=[o for o in scene.objects if o.type=='MESH']
    points=[o.matrix_world@v.co for o in meshes for v in o.data.vertices]
    low=[min(p[i] for p in points) for i in range(3)]
    high=[max(p[i] for p in points) for i in range(3)]
    return dict(file=str(path),names=names,mesh_count=len(meshes),
                triangles=sum(len(o.data.polygons) for o in meshes),
                size=[high[i]-low[i] for i in range(3)],
                objects=[dict(name=o.name,type=o.type) for o in scene.objects])

old,new=[inspect(p) for p in files]
same={k:sorted(old['names'][k])==sorted(new['names'][k]) for k in old['names']}
assert all(same.values()), same
assert new['mesh_count']==3
assert all(o['type'] in ('MESH','EMPTY') for o in new['objects'])
(out/'fbx_import_review.json').write_text(json.dumps(dict(old=old,new=new,names_preserved=same,
    unity_live_verified=False),ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(dict(names_preserved=same,new=new),ensure_ascii=False),flush=True)

