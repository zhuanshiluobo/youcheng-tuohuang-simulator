"""把一块完整后撑放到用户圈出的背侧空缺：底面落地，承接上块和左块。"""
from pathlib import Path
import numpy as np
import json
import math

OUT=Path(__file__).resolve().parent
code=(OUT/'place_rear_supports.py').read_text(encoding='utf-8').split('sets=[]')[0]
exec(compile(code,str(OUT/'place_rear_supports.py'),'exec'))
current=json.loads((OUT/'背面缺支撑_修改前.json').read_text(encoding='utf-8'))
fixed_support=current['parts'][5]
fixed_map=depth_map(np.array(fixed_support['vertices']),fixed_support['triangles'])
visible_before=np.maximum(main,fixed_map)
records=[]
for size in (.60,.65,.70,.75,.80):
 for yaw in (-30,-15,0,15,30,45):
  angle=math.radians(yaw)
  rotation=np.array([[math.cos(angle),-math.sin(angle),0],
                     [math.sin(angle),math.cos(angle),0],[0,0,1]])
  shape=(bits-.5)@rotation.T*size
  for x in (-.45,-.35,-.25,-.15):
   for y in (-.45,-.35,-.25,-.15):
    center=np.array([x,y,size/2]);verts=shape+center;box=obb(verts)
    if not intersect(box,main_boxes[0]) or not intersect(box,main_boxes[1]):continue
    dep=depth_map(verts,current['parts'][6]['triangles'])
    visible=dep>visible_before+1e-5
    cover=int(np.count_nonzero(visible&main_mask))
    extension=int(np.count_nonzero(visible&~np.isfinite(visible_before)))
    score=cover*15+extension*10+np.linalg.norm(center[:2]-[-.35,-.4])*5
    records.append(dict(index=6,size=size,yaw_degrees=yaw,center_world=center.tolist(),
      vertices=verts.tolist(),front_pixels_covered=cover,front_new_pixels=extension,score=float(score),
      contact_targets=[0,1],ground_contact='完整底面接地'))
records.sort(key=lambda r:r['score'])
if not records:raise RuntimeError('没有同时承接上块和左块的候选。')
(OUT/'圈出背侧支撑方案.json').write_text(json.dumps(records[0],ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps([{k:v for k,v in r.items() if k!='vertices'} for r in records[:3]],ensure_ascii=False,indent=2))
