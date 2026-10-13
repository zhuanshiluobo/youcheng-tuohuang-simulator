"""恢复正面小块下方的暗色完整方块，保留刚修好的两处背撑。"""
from pathlib import Path
import numpy as np
import json
import math

OUT=Path(__file__).resolve().parent
code=(OUT/'place_rear_supports.py').read_text(encoding='utf-8').split('sets=[]')[0]
exec(compile(code,str(OUT/'place_rear_supports.py'),'exec'))
current=json.loads((OUT/'背面缺支撑_修改前.json').read_text(encoding='utf-8'))
existing=current['parts'][5]
existing_vertices=np.array(existing['vertices'])
existing_map=depth_map(existing_vertices,existing['triangles'])
baseline=np.maximum(main,existing_map)
source=np.array(base['parts'][5]['vertices'])@R.T+T
source-=source.mean(axis=0)
oldside=np.linalg.norm(source[1]-source[0])
target_gap=(xx>=78)&(xx<=108)&(yy>=114)&(yy<=148)&~np.isfinite(baseline)
matrix_xy=np.array([right[:2],up[:2]])
extra_box=obb(existing_vertices)

def rotation(axis,degrees):
 a=axis/np.linalg.norm(axis);x,y,z=a;angle=math.radians(degrees)
 K=np.array([[0,-z,y],[z,0,-x],[-y,x,0]])
 return np.eye(3)+math.sin(angle)*K+(1-math.cos(angle))*(K@K)

records=[]
random=np.random.default_rng(19)
poses=[(yaw,pitch,rotation(np.array([0,0,1]),yaw)@rotation(right,pitch))
       for yaw in (-20,0,20) for pitch in (-20,0,20)]
for _ in range(70):
 axis=random.normal(size=3);angle=random.uniform(-180,180)
 poses.append((None,None,rotation(axis,angle)))
for size in (.25,.30,.35,.40):
 for yaw,pitch,rot in poses:
  if True:
   shape=source@rot.T*(size/oldside);cz=-shape[:,2].min()
   for px in (84,88,92):
    for py in (120,124,128,132):
     xy=np.linalg.solve(matrix_xy,[(px-91)*.016+right[2]*(target[2]-cz),
                       (91-py)*.016+up[2]*(target[2]-cz)])
     center=np.r_[xy,cz];verts=shape+center
     dep=depth_map(verts,current['parts'][5]['triangles'])
     visible=dep>baseline+1e-5
     occlusion=int(np.count_nonzero(visible&main_mask))
     filled=int(np.count_nonzero(visible&target_gap))
     outside=int(np.count_nonzero(visible&~np.isfinite(baseline)&~target_gap))
     if occlusion>6 or outside>8 or filled<50:continue
     box=obb(verts)
     links=[i for i,b in enumerate(main_boxes+[extra_box]) if intersect(box,b)]
     if not links:continue
     score=occlusion*50+outside*5-filled
     records.append(dict(index=7,size=size,yaw_degrees=yaw,pitch_degrees=pitch,
       center_pixels=[px,py],center_world=center.tolist(),vertices=verts.tolist(),
       rotation_matrix=rot.tolist(),main_occlusion=occlusion,gap_pixels_filled=filled,unwanted_extension=outside,
       links=links,score=float(score),source='恢复此前中央暗色支撑方块的完整立方体基础'))
records.sort(key=lambda r:r['score'])
if not records:raise RuntimeError('未找到接地并补回中央暗部的完整方块。')
(OUT/'中央暗色支撑恢复方案.json').write_text(json.dumps(records[0],ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps([{k:v for k,v in r.items() if k!='vertices'} for r in records[:3]],ensure_ascii=False,indent=2))
