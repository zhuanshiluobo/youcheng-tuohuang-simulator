"""以主要块面的交界、外轮廓试配完整立方体，纹理与几何分开处理。"""
from pathlib import Path
import json, math
import numpy as np

OUT=Path(__file__).resolve().parent
parts=json.loads((OUT.parent/'BackPairRevision'/'当前刚性方块坐标.json').read_text(encoding='utf-8'))
# 模型角点0/1/2/3/4/5/7对应同一物体的可见棱，非将各色切成独立部件。
observed={
 'top': (0,[[109,74],[101,25],[45,52],[70,96],[142,80],[126,36],[103,104]], [4,2,2,3,4,3,1]),
 'left': (1,[[57,109],[13,111],[31,146],[71,145],[67,81],[23,86],[78,119]], [3,3,2,3,4,2,3]),
 'middle': (2,[[86,103],[82,96],[78,105],[81,111],[94,106],[92,99],[89,112]], [2,1,1,2,2,2,2]),
 'right_front': (3,[[118,126],[101,102],[91,123],[106,145],[143,126],[122,101],[131,142]], [4,3,2,3,4,3,2]),
}
indices=np.array([0,1,2,3,4,5,7])
bits=np.array([[0,0,0],[1,0,0],[1,1,0],[0,1,0],[0,0,1],[1,0,1],[1,1,1],[0,1,1]],float)

def rotation(v):
    a=np.linalg.norm(v)
    if a<1.e-12:return np.eye(3)
    x,y,z=v/a;k=np.array([[0,-z,y],[z,0,-x],[-y,x,0]])
    return np.eye(3)+math.sin(a)*k+(1-math.cos(a))*(k@k)

rng=np.random.default_rng(52)
result={}
for key,(index,target,weights) in observed.items():
    old=np.array(parts[index]['verts']);camera=np.column_stack(((old[:,0]-91)*.016,(91-old[:,1])*.016,old[:,2]))
    axes=np.column_stack([camera[i]-camera[0] for i in [1,3,4]])
    length=np.linalg.norm(axes[:,0]);basis=axes/length
    target=np.array(target,float);weights=np.sqrt(np.array(weights,float))[:,None]
    def model(p):
        a=(rotation(p[3:])@basis)*math.exp(p[2])
        edge=np.column_stack((a[0]/.016,-a[1]/.016))
        return bits@edge+np.array(p[:2]),a
    def residual(p):
        xy,a=model(p)
        geometry=((xy[indices]-target)*weights).ravel()
        # 三个可见面的共同角点必须面向相机，保持原先分面关系。
        return np.r_[geometry,np.maximum(a[2],0)*500]
    start=np.r_[old[0,:2],math.log(length),[0,0,0]]
    best=None
    for trial in range(32):
        p=start.copy()
        if trial:p[3:]+=rng.normal(0,.22,3)
        lam=.05
        for iteration in range(90):
            error=residual(p)
            jac=np.column_stack([(residual(p+np.eye(6)[j]*1.e-5)-error)/1.e-5 for j in range(6)])
            step=np.linalg.solve(jac.T@jac+lam*np.eye(6),-jac.T@error)
            if np.linalg.norm(step)<1.e-7:break
            candidate=p+step
            if np.linalg.norm(residual(candidate))<np.linalg.norm(error):p=candidate;lam=max(lam*.6,1.e-6)
            else:lam=min(lam*4,1.e6)
        score=float(np.linalg.norm(residual(p)))
        if best is None or score<best[0]:best=(score,p)
    score,p=best;xy,a=model(p)
    corner_depth=float(old[0,2]);z=bits@a[2]+corner_depth
    verts=np.column_stack((xy,z)).tolist()
    result[key]=dict(camera_vertices=verts,corner=verts[0],
       screen_edges=[[float(a[0,i]/.016),float(-a[1,i]/.016)] for i in range(3)],
       edge_length=float(math.exp(p[2])),rms_pixels=float(np.sqrt(np.mean((xy[indices]-target)**2))),
       target_points=target.tolist(),predicted_points=xy[indices].tolist(),
       note='低分辨率插画观测有不确定性；数值试配只改完整立方体位姿，实图尚未验收。')
(OUT/'主要块面位姿候选.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:{'rms':v['rms_pixels'],'length':v['edge_length'],'corner':v['corner']} for k,v in result.items()},ensure_ascii=False))
