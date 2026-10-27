"""采用已核对的可见棱拟合，保留真实立方体，并让低侧角点不越过原地板。"""
from pathlib import Path
import json
import numpy as np

OUT=Path(__file__).resolve().parent
OUT.mkdir(exist_ok=True)
info=json.loads((OUT.parent/'FloorLineRevision'/'地板基准.json').read_text(encoding='utf-8'))
old=json.loads((OUT.parent/'FloorLineRevision'/'当前块体坐标.json').read_text(encoding='utf-8'))
poses=json.loads((OUT.parent/'OcclusionRevision'/'主要块面位姿候选.json').read_text(encoding='utf-8'))
back=json.loads((OUT.parent/'OcclusionRevision'/'主要刚性方块坐标.json').read_text(encoding='utf-8'))[4]
g=np.array(info['gravity_camera']);g/=np.linalg.norm(g)
basis=np.array(info['basis']);floor=info['floor_height']
rows=[]
for index,key in enumerate(['top','left','middle','right_front','right_back']):
    pose=back if index==4 else poses[key]
    values=np.array(pose['verts'] if index==4 else pose['camera_vertices'])
    if key=='middle':values[:,2]+=.17-values[0,2]
    camera=np.column_stack(((values[:,0]-91)*.016,(91-values[:,1])*.016,values[:,2]))
    low=float((camera@g).min())
    # 低侧块的整体沿视线平移：保持正面的投影不变，最低角点接地。
    if key in ['left','right_front','right_back']:
        delta=(floor-low)/g[2]
        camera[:,2]+=delta;values[:,2]+=delta
    elif low<floor:
        delta=(floor-low)/g[2]
        camera[:,2]+=delta;values[:,2]+=delta
    row=dict(old[index])
    row.update(verts=values.tolist(),world_vertices=(camera@basis).tolist(),zmin=float((camera@g).min()),
               edge_length=float(np.linalg.norm(camera[1]-camera[0])))
    if index==4:
        row['faces']=back['faces'];row['materials']=back['materials']
    rows.append(row)
(OUT/'当前块体坐标.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf-8')
(OUT/'地板基准.json').write_text(json.dumps(info,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps([dict(name=x['name'],zmin=x['zmin'],corner=x['verts'][0]) for x in rows],ensure_ascii=False))
