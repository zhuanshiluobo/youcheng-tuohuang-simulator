"""只研究两个后撑的刚体倾斜；保留正面和中央暗部。"""
from pathlib import Path
import json,math,numpy as np
OUT=Path(__file__).resolve().parent
exec(compile((OUT/'place_rear_supports.py').read_text(encoding='utf-8').split('sets=[]')[0],str(OUT/'place_rear_supports.py'),'exec'))
current=json.loads((OUT/'背撑错落_修改前.json').read_text(encoding='utf-8'))
fixedmap=np.max([depth_map(np.array(p['vertices']),p['triangles']) for p in current['parts'][:6]],axis=0)
box5=obb(np.array(current['parts'][5]['vertices']))
def rot(x,y,z):
 x,y,z=np.radians([x,y,z]);cx,sx,cy,sy,cz,sz=np.cos(x),np.sin(x),np.cos(y),np.sin(y),np.cos(z),np.sin(z)
 return np.array([[cz,-sz,0],[sz,cz,0],[0,0,1]])@np.array([[cy,0,sy],[0,1,0],[-sy,0,cy]])@np.array([[1,0,0],[0,cx,-sx],[0,sx,cx]])
choices=[]
for index,poses,sizes,basepos in [(6,[(22,-18,30),(30,-12,20),(15,-30,35),(-22,20,30),(28,0,35)],(.56,.60,.64),(-.45,-.35)),(7,[(-28,18,-25),(-18,30,-20),(-30,-15,-10),(20,28,-35),(-25,0,-25)],(.50,.55,.60),(-.45,.60))]:
 records=[]
 for angles in poses:
  rotation=rot(*angles)
  for size in sizes:
   shape=(bits-.5)@rotation.T*size
   for dx in (-.12,0,.12):
    for dy in (-.12,0,.12):
     center=np.array([basepos[0]+dx,basepos[1]+dy,-shape[:,2].min()]);verts=shape+center;box=obb(verts)
     if not intersect(box,main_boxes[0]):continue
     if not intersect(box,main_boxes[1] if index==6 else box5):continue
     dep=depth_map(verts,current['parts'][index]['triangles']);vis=dep>fixedmap+1e-5
     cover=int(np.count_nonzero(vis&np.isfinite(fixedmap)));extension=int(np.count_nonzero(vis&~np.isfinite(fixedmap)))
     score=cover*15+extension*25+(dx*dx+dy*dy)*20+abs(size-sizes[1])*5
     records.append(dict(index=index,size=size,euler_xyz_degrees=angles,vertices=verts.tolist(),center_world=center.tolist(),front_cover=cover,front_extension=extension,score=score,ground_contact='倾斜方块角点接地'))
 records.sort(key=lambda x:x['score'])
 if not records:raise RuntimeError('没有合理相接的倾斜后撑')
 choices.append(records[0]);print(index,json.dumps([{k:v for k,v in r.items() if k!='vertices'} for r in records[:4]],ensure_ascii=False))
(OUT/'背撑错落摆放方案.json').write_text(json.dumps(dict(supports=choices),ensure_ascii=False,indent=2),encoding='utf-8')
