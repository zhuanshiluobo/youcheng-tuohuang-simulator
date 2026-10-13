"""依据用户指定旧白模的真实可见支撑轮廓恢复完整大方块。"""
from pathlib import Path
import numpy as np
import json
import math

OUT=Path(__file__).resolve().parent
code=(OUT/'place_rear_supports.py').read_text(encoding='utf-8').split('sets=[]')[0]
exec(compile(code,str(OUT/'place_rear_supports.py'),'exec'))
old=json.loads((OUT/'用户旧白模_真实块体.json').read_text(encoding='utf-8'))
old_maps=[depth_map(np.array(p['vertices'])@R.T+T,p['triangles']) for p in old['parts']]
old_top=np.max(old_maps,axis=0)
desired=np.isfinite(old_maps[5])&(old_maps[5]>=old_top-1e-6)
# 左下已经前移，以当前左下遮挡为准。
desired &= ~((main_maps[1]>=main-1e-6)&np.isfinite(main_maps[1]))
desired &= (xx>70)&(xx<118)&(yy>94)&(yy<150)
protected=np.isfinite(main_maps[2])&(main_maps[2]>=main-1e-6)
source=np.array(old['parts'][5]['vertices'])@R.T+T
source-=source.mean(axis=0)
oldside=np.linalg.norm(source[1]-source[0])
matrix_xy=np.array([right[:2],up[:2]])
random=np.random.default_rng(23)
poses=[np.eye(3)]
for _ in range(180):
 q=random.normal(size=4);q/=np.linalg.norm(q);w,x,y,z=q
 poses.append(np.array([[1-2*y*y-2*z*z,2*x*y-2*z*w,2*x*z+2*y*w],
                        [2*x*y+2*z*w,1-2*x*x-2*z*z,2*y*z-2*x*w],
                        [2*x*z-2*y*w,2*y*z+2*x*w,1-2*x*x-2*y*y]]))
records=[]
for size in (.50,.60,.70,.80):
 for pose,rot in enumerate(poses):
  shape=source@rot.T*(size/oldside);cz=-shape[:,2].min()
  for px in (86,92,98):
   for py in (103,109,115,121):
    xy=np.linalg.solve(matrix_xy,[(px-91)*.016+right[2]*(target[2]-cz),
                      (91-py)*.016+up[2]*(target[2]-cz)])
    center=np.r_[xy,cz];verts=shape+center
    dep=depth_map(verts,old['parts'][5]['triangles'])
    visible=dep>main+1e-5
    bad=int(np.count_nonzero(visible&main_mask&~desired))
    if np.count_nonzero(visible&protected)>2 or bad>35:continue
    overlap=int(np.count_nonzero(visible&desired))
    extra=int(np.count_nonzero(visible&~desired))
    missed=int(np.count_nonzero(desired&~visible))
    if overlap<100:continue
    score=missed+extra*2+bad*12
    box=obb(verts);links=[i for i,b in enumerate(main_boxes) if intersect(box,b)]
    if not links:continue
    records.append(dict(index=5,size=size,rotation_matrix=rot.tolist(),pose=pose,
      center_pixels=[px,py],center_world=center.tolist(),vertices=verts.tolist(),
      reference_pixels=int(np.count_nonzero(desired)),reference_overlap=overlap,
      missed_pixels=missed,extra_pixels=extra,unwanted_primary_cover=bad,
      links=links,score=float(score),source_scene=old['scene'],
      source_object=old['parts'][5]['name'],role='用户旧白模中央完整承托方块'))
records.sort(key=lambda r:r['score'])
if not records:raise RuntimeError('未找到完整大方块参考恢复方案。')
(OUT/'旧白模中央承托恢复方案.json').write_text(json.dumps(records[0],ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps([{k:v for k,v in r.items() if k not in ('vertices','rotation_matrix')} for r in records[:3]],ensure_ascii=False,indent=2))
