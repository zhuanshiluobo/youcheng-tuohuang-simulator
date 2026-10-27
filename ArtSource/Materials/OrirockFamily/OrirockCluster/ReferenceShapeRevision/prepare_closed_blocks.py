"""按插画角点校形六面闭合方块，色面仍共用八角点，不拆成前侧薄片。"""
from pathlib import Path
import json
import numpy as np

OUT=Path(__file__).resolve().parent
OUT.mkdir(exist_ok=True)
source=OUT.parent/'SurfaceMatchRevision'
rows=json.loads((source/'当前块体坐标.json').read_text(encoding='utf-8'))
info=json.loads((source/'地板基准.json').read_text(encoding='utf-8'))
basis=np.array(info['basis'])
gravity=np.array(info['gravity_camera']);gravity/=np.linalg.norm(gravity)
floor=info['floor_height']
faces=[[0,1,2,3],[4,7,6,5],[0,4,5,1],[1,5,6,2],[2,6,7,3],[3,7,4,0]]
targets={
 0:[[109,74],[101,25],[45,52],[70,96],[142,80],[126,36],[103,104]],
 1:[[57,109],[13,111],[31,146],[71,145],[67,81],[23,86],[78,119]],
 2:[[86,103],[82,96],[78,105],[81,111],[94,106],[92,99],[89,112]],
 3:[[118,126],[101,102],[91,123],[106,145],[143,126],[122,101],[131,142]]}
visible=[0,1,2,3,4,5,7]

def signed_planes(v):
    planes=[]
    for face in faces:
        a,b,c=v[face[:3]]
        n=np.cross(b-a,c-a);n/=np.linalg.norm(n)
        d=np.dot(n,a)
        if np.dot(n,v.mean(0))-d>0:n,d=-n,-d
        planes.append((n,d))
    return planes

out=[]
for index,row in enumerate(rows):
    old=np.array(row['verts'])
    cam=np.column_stack(((old[:,0]-91)*.016,(91-old[:,1])*.016,old[:,2]))
    if index in targets:
        xy=np.array(targets[index],float)
        v=np.zeros((8,3))
        v[visible,:2]=np.column_stack(((xy[:,0]-91)*.016,(91-xy[:,1])*.016))
        v[0,2]=cam[0,2]
        edges=v[[1,3,4],:2]-v[0,:2]
        products=-edges@edges.T
        # 共角处保持三个互相垂直的主面；相对侧略校形以吻合手绘远棱。
        if min(products[0,1],products[0,2],products[1,2])>0:
            zu=-np.sqrt(products[0,1]*products[0,2]/products[1,2])
            z=np.array([zu,products[0,1]/zu,products[0,2]/zu])
        else:
            z=cam[[1,3,4],2]-cam[0,2]
        v[[1,3,4],2]=v[0,2]+z
        for missing,a,b in [(2,1,3),(5,1,4),(7,3,4)]:
            n=np.cross(v[a]-v[0],v[b]-v[0])
            v[missing,2]=v[0,2]-np.dot(n[:2],v[missing,:2]-v[0,:2])/n[2]
        planes=[]
        for tri in [[1,2,5],[2,3,7],[4,5,7]]:
            a,b,c=v[tri];n=np.cross(b-a,c-a);n/=np.linalg.norm(n)
            planes.append((n,float(n@a)))
        v[6]=np.linalg.solve(np.array([p[0] for p in planes]),np.array([p[1] for p in planes]))
    else:v=cam.copy()
    # 最低主块角点仍在同一真实世界地板，不能把前侧角削平。
    if index in [1,3,4]:v[:,2]+=(floor-np.min(v@gravity))/gravity[2]
    planes=signed_planes(v)
    convex_violation=max(float((v@n-d).max()) for n,d in planes)
    planarity=max(float(np.max(np.abs(v[f]@n-d))) for f,(n,d) in zip(faces,planes))
    if convex_violation>1e-7 or planarity>1e-7:
        raise ValueError(f'块{index}不是闭合凸六面方块，停止：{convex_violation},{planarity}')
    world=v@basis
    screen=np.column_stack((91+v[:,0]/.016,91-v[:,1]/.016,v[:,2]))
    out.append(dict(name=row['name'].split('.')[0],block=index,verts=screen.tolist(),world_vertices=world.tolist(),
        faces=faces,main_edge_lengths=np.linalg.norm(v[[1,3,4]]-v[0],axis=1).tolist(),
        plane_error=planarity,model_zmin=float(np.min(v@gravity)),
        note='完整六面方块；共角主面垂直，远棱按手绘校形。不是数学等边立方体，也不是色面拼片。'))
(OUT/'正面校形坐标.json').write_text(json.dumps(out,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps([dict(block=o['block'],main_edges=o['main_edge_lengths'],zmin=o['model_zmin']) for o in out],ensure_ascii=False))
