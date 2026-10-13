"""在用户圈出的背侧下方补一块底面接地的完整承托。"""
from pathlib import Path
import numpy as np
import json
import math

OUT=Path(__file__).resolve().parent
exec(compile((OUT/'place_rear_supports.py').read_text(encoding='utf-8').split('sets=[]')[0],
             str(OUT/'place_rear_supports.py'),'exec'))
current=json.loads((OUT/'新增背部承托_现场.json').read_text(encoding='utf-8'))
all_maps=[depth_map(np.array(p['vertices']),p['triangles']) for p in current['parts']]
before=np.max(all_maps,axis=0)
support_boxes=[obb(np.array(p['vertices'])) for p in current['parts'][5:]]
records=[]
for size in (.55,.60,.65,.70):
 for yaw in (-25,-10,5,20):
  angle=math.radians(yaw)
  rotation=np.array([[math.cos(angle),-math.sin(angle),0],
                     [math.sin(angle),math.cos(angle),0],[0,0,1]])
  shape=(bits-.5)@rotation.T*size
  for x in (-.65,-.55,-.45,-.35):
   for y in (.30,.45,.60,.75):
    center=np.array([x,y,size/2]);verts=shape+center;box=obb(verts)
    if not intersect(box,main_boxes[0]):continue
    if not any(intersect(box,b) for b in support_boxes):continue
    dep=depth_map(verts,current['parts'][6]['triangles']);visible=dep>before+1e-5
    front_cover=int(np.count_nonzero(visible&np.isfinite(before)))
    front_extension=int(np.count_nonzero(visible&~np.isfinite(before)))
    proximity=float(np.linalg.norm(center[:2]-[-.5,.55]))
    score=front_cover*10+front_extension*20+proximity*50
    records.append(dict(index=7,size=size,yaw_degrees=yaw,center_world=center.tolist(),
        vertices=verts.tolist(),front_cover_pixels=front_cover,front_extension_pixels=front_extension,
        score=score,role='按用户红线新增的背面完整承托',ground_contact='底面接地'))
records.sort(key=lambda r:r['score'])
if not records:raise RuntimeError('未找到接触上块的落地承托。')
(OUT/'用户红线新增承托方案.json').write_text(json.dumps(records[0],ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps([{k:v for k,v in r.items() if k!='vertices'} for r in records[:3]],ensure_ascii=False,indent=2))
