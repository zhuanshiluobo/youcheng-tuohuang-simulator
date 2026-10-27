"""有限范围研究后撑：保持标准立方体与接地，不遮挡前方五块。"""
from pathlib import Path
import json
import numpy as np

OUT = Path(__file__).resolve().parent
data = json.loads((OUT / '新地板分件观察.json').read_text(encoding='utf-8'))
base = json.loads((OUT / '旧摆放只读基准.json').read_text(encoding='utf-8'))
plan = json.loads((OUT / '新地板与后撑方案.json').read_text(encoding='utf-8'))
right, up, front, target = [np.array(data[k]) for k in ('right','up','front','target')]
bits = np.array([[0,0,0],[1,0,0],[1,1,0],[0,1,0],[0,0,1],[1,0,1],[1,1,1],[0,1,1]])
yy, xx = np.mgrid[:183,:183]

def depth_map(vs, tris):
    relative = vs - target
    pts = np.column_stack((91 + relative @ right / .016,
                           91 - relative @ up / .016, relative @ front))
    out = np.full((183,183), -np.inf)
    for tri in tris:
        a,b,c = pts[tri]
        x0,y0 = np.maximum(0, np.floor(np.minimum(np.minimum(a[:2],b[:2]),c[:2])).astype(int))
        x1,y1 = np.minimum(182,np.ceil(np.maximum(np.maximum(a[:2],b[:2]),c[:2])).astype(int))
        if x1<x0 or y1<y0: continue
        dx,dy = xx[y0:y1+1,x0:x1+1]+.5-a[0], yy[y0:y1+1,x0:x1+1]+.5-a[1]
        e1,e2 = b[:2]-a[:2],c[:2]-a[:2]
        det = e1[0]*e2[1]-e1[1]*e2[0]
        if abs(det)<1e-8: continue
        s = (dx*e2[1]-dy*e2[0])/det
        t = (e1[0]*dy-e1[1]*dx)/det
        inside = (s>=-1e-5)&(t>=-1e-5)&(s+t<=1.00001)
        z = a[2]+s*(b[2]-a[2])+t*(c[2]-a[2])
        sl = out[y0:y1+1,x0:x1+1]
        np.maximum(sl, np.where(inside,z,-np.inf),out=sl)
    return out

def obb(vs):
    axes = np.column_stack([vs[k]-vs[0] for k in (1,3,4)])
    lengths = np.linalg.norm(axes,axis=0)
    return vs.mean(axis=0),axes/lengths,lengths/2

def intersect(a,b):
    ca,aa,ha = a; cb,ab,hb=b
    axes=[aa[:,i] for i in range(3)]+[ab[:,i] for i in range(3)]
    axes += [np.cross(aa[:,i],ab[:,j]) for i in range(3) for j in range(3)]
    for ax in axes:
        if np.linalg.norm(ax)<1e-7: continue
        if abs((cb-ca)@ax) > np.abs(aa.T@ax)@ha+np.abs(ab.T@ax)@hb-.006:
            return False
    return True

main_maps=[depth_map(np.array(p['vertices']),p['triangles']) for p in data['parts'][:5]]
main=np.max(main_maps,axis=0)
main_mask=np.isfinite(main)
# 暗部容许后撑露出；其余轮廓不允许由后撑扩大。
allowed = (xx>=77)&(xx<=119)&(yy>=111)&(yy<=148)
main_boxes=[]
R,T=np.array(plan['alignment_matrix']),np.array(plan['world_translation'])
for i,row in enumerate(base['rows']):
    v=np.array(row['world_vertices'])
    if i==1: v += np.array(base['front'])*.76
    main_boxes.append(obb(v@R.T+T))
fixed=np.array([[intersect(a,b) for b in main_boxes] for a in main_boxes])
back_h=-front.copy();back_h[2]=0
side_h=right.copy();side_h[2]=0
sets=[]
for index in (5,6):
    part=data['parts'][index]
    vs=np.array(part['vertices']); center=vs.mean(axis=0)
    shape=vs-center; oldside=np.linalg.norm(vs[1]-vs[0])
    records=[]
    for size in (oldside, .80,.90,1.0,1.10):
        local=shape*(size/oldside)
        grounded=center.copy();grounded[2]=-local[:,2].min()
        for backward in np.arange(0,1.61,.1):
            for sideways in (-.3,-.15,0,.15,.3):
                c=grounded+back_h*backward+side_h*sideways
                verts=local+c
                dep=depth_map(verts,part['triangles'])
                visible=dep>main+1e-5
                clipped=visible & main_mask
                bad_extension=visible & ~main_mask & ~allowed
                reveal=visible & ~main_mask & allowed
                cost=clipped.sum()*16+bad_extension.sum()*20 - min(250,reveal.sum())*.5
                box=obb(verts)
                links=[i for i,b in enumerate(main_boxes) if intersect(box,b)]
                if not links:continue
                records.append(dict(index=index,size=float(size),backward=float(backward),
                    sideways=sideways,center_world=c.tolist(),vertices=verts.tolist(),
                    main_occlusion=int(clipped.sum()),unwanted_extension=int(bad_extension.sum()),
                    dark_gap_fill=int(reveal.sum()),cost=float(cost),links=links,
                    box=box,depth=dep))
    records.sort(key=lambda x:x['cost'])
    sets.append(records)

best=None
for a in sets[0]:
    for b in sets[1]:
        cost=a['cost']+b['cost']
        if best is not None and cost>=best[0]:continue
        graph=np.eye(7,dtype=bool);graph[:5,:5]=fixed
        for j,r in enumerate((a,b),5):
            graph[j,r['links']]=True;graph[r['links'],j]=True
        graph[5,6]=graph[6,5]=intersect(a['box'],b['box'])
        seen={0};stack=[0]
        while stack:
            i=stack.pop()
            for j in np.nonzero(graph[i])[0]:
                if j not in seen:seen.add(j);stack.append(j)
        if len(seen)==7:best=(cost,a,b)
if best is None:raise RuntimeError('本研究范围没有连接全部主块的组合。')
records=[]
for r in best[1:]:
    records.append({k:v for k,v in r.items() if k not in ('box','depth')})
(OUT/'后撑退后方案.json').write_text(json.dumps(dict(supports=records,
    note='有限范围搜索结果，需实际渲染与实体连通检查，不作为视觉验收。'),ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps([{k:v for k,v in r.items() if k!='vertices'} for r in records],ensure_ascii=False,indent=2))
