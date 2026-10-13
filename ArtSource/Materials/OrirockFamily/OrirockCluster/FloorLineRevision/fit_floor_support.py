"""以正面主块最低角点定义真实世界地板，只摆放后方两个完整方块。"""
from pathlib import Path
import json, math, time
import numpy as np
from PIL import Image

OUT = Path(__file__).resolve().parent
helper = (OUT.parent/'OcclusionRevision'/'fit_back_support.py').read_text(encoding='utf-8')
helper = helper[:helper.index('def loss(')]
helper = helper.replace("SOURCE = OUT / '主要刚性方块坐标.json'", "SOURCE = OUT / '当前块体坐标.json'")
exec(compile(helper, 'existing_cube_projection_helpers', 'exec'))
info = json.loads((OUT/'地板基准.json').read_text(encoding='utf-8'))
g = np.array(info['gravity_camera']); g /= np.linalg.norm(g)
r = np.array([1.,0.,0.]); r -= g*np.dot(r,g); r /= np.linalg.norm(r)
h = np.cross(r,g); h /= np.linalg.norm(h)
floor = min(float(np.min(a@g)) for a in primary)
coords = np.array([[0,0,0],[1,0,0],[1,1,0],[0,1,0],
                   [0,0,1],[1,0,1],[1,1,1],[0,1,1]],float)-.5

def rear(p):
    x,hd,L,yaw = p[:4]
    theta=math.radians(yaw)
    a=r*math.cos(theta)+h*math.sin(theta)
    b=-r*math.sin(theta)+h*math.cos(theta)
    base=coords@np.stack([a*L,g*L,b*L])
    base+=r*((x-91)*.016)+h*hd+g*(floor+L/2)
    upper=cube(p[4:11])
    upper+=g*max(0.,floor-float(np.min(upper@g)))
    return [base,upper]

def contact_gap(a,b):
    ae=np.stack([a[i]-a[0] for i in [1,3,4]])
    be=np.stack([b[i]-b[0] for i in [1,3,4]])
    ae/=np.linalg.norm(ae,axis=1)[:,None];be/=np.linalg.norm(be,axis=1)[:,None]
    cross=np.cross(ae[:,None,:],be[None,:,:]).reshape(-1,3)
    cross=cross[np.linalg.norm(cross,axis=1)>1.e-6]
    cross/=np.linalg.norm(cross,axis=1)[:,None]
    axes=np.concatenate((ae,be,cross));ap=a@axes.T;bp=b@axes.T
    return max(0.,float(np.maximum(ap.min(0)-bp.max(0),bp.min(0)-ap.max(0)).max()))

def evaluate(p,details=False):
    backs=rear(p);allparts=primary+backs
    rf=np.maximum(raster(backs[0],faces),raster(backs[1],faces))
    visible=rf>front+1.e-4
    miss=np.count_nonzero(needed&~visible)
    unwanted=np.count_nonzero(visible&~allowed)
    outside=np.count_nonzero(visible&~silhouette)
    covering=np.count_nonzero(visible&(front>-100))
    pairs=[];links=[set() for _ in allparts];nearest=[]
    for j in range(7):
        gaps=[]
        for i in range(j):
            gap=contact_gap(allparts[i],allparts[j]);gaps.append(gap)
            if gap<.0001:links[i].add(j);links[j].add(i);pairs.append([i,j])
        if j>=5:nearest.append(min(gaps))
    reached={0};todo=[0]
    while todo:
        q=todo.pop()
        for k in links[q]-reached:reached.add(k);todo.append(k)
    disconnected=7-len(reached)
    # 后方落地面的四角提供支撑范围；前侧角点不截平。
    lengths=np.array([np.linalg.norm(v[1]-v[0]) for v in allparts])
    com=np.average([v.mean(0) for v in allparts],axis=0,weights=lengths**3)
    base=backs[0];axes=np.array([base[1]-base[0],base[4]-base[0]])
    axes/=np.linalg.norm(axes,axis=1)[:,None]
    over=np.maximum(0.,np.abs(axes@(com-base.mean(0)))-p[2]/2+.025)
    support_penalty=float(np.sum(over**2))*90000
    score=miss*3+unwanted*6+outside*24+covering*5+disconnected*9000+sum(nearest)*4000+support_penalty
    if details:return dict(score=float(score),missing=int(miss),unwanted=int(unwanted),outside=int(outside),
                           covering_primary=int(covering),disconnected=disconnected,contacts=pairs,
                           approximate_center_overhang=over.tolist(),floor_height=float(floor),
                           rear_min_heights=[float(np.min(v@g)) for v in backs])
    return score

lower=np.array([65,-.85,.70,-65,65,60,-1.35,.60,-85,-36,-34])
upper=np.array([112,-.05,1.04,65,118,106,-.05,.98,-5,36,34])
start=np.array([90,-.36,.88,22,94,89,-.75,.79,-30,19,8])
rng=np.random.default_rng(45)
scale=np.array([8,.09,.07,15,8,7,.13,.05,14,8,8])
best=start.copy();score=evaluate(best);records=[];t=time.time()
for phase in range(5):
    for step in range(550):
        p=np.clip(best+rng.normal(size=11)*scale*(.66**phase),lower,upper)
        s=evaluate(p)
        if s<score:best,score=p,s;records.append(dict(params=p.tolist(),**evaluate(p,True)))
    print('后撑摆放阶段',phase,round(score,2),'耗时',round(time.time()-t,1),flush=True)
result=dict(params=best.tolist(),camera_vertices=[project(a).tolist() for a in rear(best)],details=evaluate(best,True),
            note='只调整背后两块，前侧五块保持原位；世界Z地板来自前侧最低角点。数值不是正面视觉验收。')
(OUT/'地板后撑方案.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
(OUT/'摆放记录.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(result['details'],ensure_ascii=False),flush=True)
