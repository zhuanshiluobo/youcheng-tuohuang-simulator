"""调整后撑朝向，填补原图底部暗色方块区域，不新增碎块。"""
from pathlib import Path
import numpy as np
import json
import math

OUT=Path(__file__).resolve().parent
# 复用只读投影/盒接触函数，不执行上一轮搜索。
code=(OUT/'place_rear_supports.py').read_text(encoding='utf-8').split('sets=[]')[0]
exec(compile(code,str(OUT/'place_rear_supports.py'),'exec'))
placements=json.loads((OUT/'后撑退后方案.json').read_text(encoding='utf-8'))['supports']
other=placements[1]
other_box=obb(np.array(other['vertices']))
other_depth=depth_map(np.array(other['vertices']),data['parts'][6]['triangles'])
source=np.array(data['parts'][6]['vertices']);source-=source.mean(axis=0)
oldside=np.linalg.norm(source[1]-source[0])
matrix_xy=np.array([right[:2],up[:2]])
dark_target=(xx>=79)&(xx<=112)&(yy>=118)&(yy<=147)&~main_mask

def rotation(axis,angle):
    a=axis/np.linalg.norm(axis);x,y,z=a
    K=np.array([[0,-z,y],[z,0,-x],[-y,x,0]])
    return np.eye(3)+math.sin(angle)*K+(1-math.cos(angle))*(K@K)

best=None
random=np.random.default_rng(41)
poses=[]
for _ in range(180):
 q=random.normal(size=4);q/=np.linalg.norm(q)
 w,x,y,z=q
 rot=np.array([[1-2*y*y-2*z*z,2*x*y-2*z*w,2*x*z+2*y*w],
               [2*x*y+2*z*w,1-2*x*x-2*z*z,2*y*z-2*x*w],
               [2*x*z-2*y*w,2*y*z+2*x*w,1-2*x*x-2*y*y]])
 poses.append(rot)
for size in (.45,.60,.75,.90):
 for pose_index,rot in enumerate(poses):
  if True:
   shape=source@rot.T*(size/oldside)
   cz=-shape[:,2].min()
   for px in (90,96,102):
    for py in (101,108,115):
     xy=np.linalg.solve(matrix_xy,np.array([(px-91)*.016+right[2]*(target[2]-cz),
                        (91-py)*.016+up[2]*(target[2]-cz)]))
     center=np.r_[xy,cz];verts=shape+center
     dep=depth_map(verts,data['parts'][6]['triangles'])
     combined=np.maximum(dep,other_depth)
     visible=combined>main+1e-5
     occluded=int(np.count_nonzero(visible&main_mask))
     outside=int(np.count_nonzero(visible&~main_mask&~allowed))
     filled=int(np.count_nonzero(visible&dark_target))
     if occluded>12 or outside>5 or filled<30:continue
     cost=occluded*20+outside*30-filled
     if best is not None and cost>=best[0]:continue
     box=obb(verts);links=[i for i,b in enumerate(main_boxes) if intersect(box,b)]
     graph=np.eye(7,dtype=bool);graph[:5,:5]=fixed
     for j,link in ((5,links),(6,other['links'])):
      graph[j,link]=True;graph[link,j]=True
     graph[5,6]=graph[6,5]=intersect(box,other_box)
     seen={0};stack=[0]
     while stack:
      for j in np.nonzero(graph[stack.pop()])[0]:
       if j not in seen:seen.add(j);stack.append(j)
     if len(seen)<7:continue
     best=(cost,dict(index=5,size=size,rotation_matrix=rot.tolist(),pose_index=pose_index,
       center_pixels=[px,py],center_world=center.tolist(),vertices=verts.tolist(),
       links=links,main_occlusion=occluded,unwanted_extension=outside,dark_gap_fill=filled))
if best is None:
 print('有限范围内未找到填暗部且保持遮挡的姿态。')
else:
 result=dict(supports=[best[1],other],note='朝向不同的两块完整后撑，待实际渲染。',
             triangles=data['parts'][6]['triangles'])
 (OUT/'后撑暗部方案.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
 print(json.dumps({k:v for k,v in best[1].items() if k!='vertices'},ensure_ascii=False,indent=2))
