import bpy, json, math
from pathlib import Path
from mathutils import Vector
from bpy_extras.object_utils import world_to_camera_view

out=Path(__file__).resolve().parent
source=out.parent/'HexPrismRevision'/'OrirockConcentration_HexPrisms.blend'
bpy.ops.wm.open_mainfile(filepath=str(source))
scene=bpy.context.scene
result=[]
for obj in [o for o in scene.objects if o.type=='MESH']:
    z=max(v.co.z for v in obj.data.vertices)
    ring=[v.co.copy() for v in obj.data.vertices if abs(v.co.z-z)<1e-5]
    center=sum(ring,Vector())/len(ring)
    ring.sort(key=lambda p:math.atan2(p.y-center.y,p.x-center.x))
    edges=[]
    for a,b in zip(ring,ring[1:]+ring[:1]):
        if (b-a).length < .1: continue
        projection=[]
        for p in [a,b]:
            v=world_to_camera_view(scene,scene.camera,obj.matrix_world@p)
            projection.append([round(v.x*900,1),round((1-v.y)*900,1)])
        edges.append(dict(index=len(edges),a=list(a),b=list(b),screen=projection))
    result.append(dict(name=obj.name,z=z,center=list(center),edges=edges))
(out/'source_edges.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(result,ensure_ascii=False),flush=True)
