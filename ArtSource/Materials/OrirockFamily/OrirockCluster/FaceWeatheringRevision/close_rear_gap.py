from pathlib import Path
import numpy as np,json
OUT=Path(__file__).resolve().parent
OLD=OUT.parent/'CorrectedFloorRevision'
# 复用投影与方块相交计算；不调用旧阶段的生成或保存代码。
namespace={'__file__':str(OLD/'place_rear_supports.py')}
exec(compile((OLD/'place_rear_supports.py').read_text(encoding='utf-8').split('sets=[]')[0],str(OLD/'place_rear_supports.py'),'exec'),namespace)
depth_map,obb,intersect=[namespace[k] for k in ('depth_map','obb','intersect')]
main_boxes=namespace['main_boxes']
data=json.loads((OUT/'背部透空_修改前.json').read_text(encoding='utf-8'))
parts=data['parts'];fixed=np.max([depth_map(np.array(p['vertices']),p['triangles']) for p in parts[:6]],axis=0)
box5=obb(np.array(parts[5]['vertices']));sets=[]
for index in (6,7):
 vs=np.array(parts[index]['vertices']);center=vs.mean(axis=0);side=np.linalg.norm(vs[1]-vs[0]);records=[]
 for size in (side,side+.06,side+.12):
  shape=(vs-center)*(size/side)
  for dx in (-.24,-.12,0,.08,.16):
   for dy in ((0,.06,.12,.20,.28) if index==6 else (-.12,-.20,-.28,-.36,-.44,-.52)):
    c=center+np.array([dx,dy,0]);c[2]=-shape[:,2].min();v=shape+c;b=obb(v)
    if not intersect(b,main_boxes[0]):continue
    if not intersect(b,main_boxes[1] if index==6 else box5):continue
    dm=depth_map(v,parts[index]['triangles']);visible=dm>fixed+1e-5
    cover=int(np.count_nonzero(visible&np.isfinite(fixed)));extend=int(np.count_nonzero(visible&~np.isfinite(fixed)))
    records.append(dict(index=index,size=size,center_world=c.tolist(),vertices=v.tolist(),cover=cover,extend=extend,cost=cover*20+extend*30+(dx*dx+dy*dy)*8+(size-side)*3,box=b))
 sets.append(records)
best=None
for a in sets[0]:
 for b in sets[1]:
  if not intersect(a['box'],b['box']):continue
  cost=a['cost']+b['cost']
  if best is None or cost<best[0]:best=(cost,a,b)
if best is None:raise RuntimeError('未找到保持联系的方案')
records=[{k:v for k,v in x.items() if k!='box'} for x in best[1:]]
(OUT/'背部透空修正方案.json').write_text(json.dumps(dict(supports=records),ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps([{k:v for k,v in r.items() if k!='vertices'} for r in records],ensure_ascii=False))
