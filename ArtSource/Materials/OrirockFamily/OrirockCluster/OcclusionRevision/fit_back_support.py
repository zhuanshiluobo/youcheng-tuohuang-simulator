"""后撑姿态试配：只优化真实立方体的位姿，不改变顶点，不投射图标。"""
from pathlib import Path
import json, math, time
import numpy as np
from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent
SOURCE = OUT / '主要刚性方块坐标.json'
if not SOURCE.exists(): SOURCE = OUT.parent / 'BackPairRevision' / '当前刚性方块坐标.json'
REF = OUT.parents[1] / 'reference' / 'OrirockCluster_PRTS.png'
data = json.loads(SOURCE.read_text(encoding='utf-8'))
faces = [[0,1,2,3],[4,7,6,5],[0,4,5,1],[1,5,6,2],[2,6,7,3],[3,7,4,0]]
grid_y, grid_x = np.mgrid[:183, :183]

def camera_vertices(v):
    v = np.array(v, dtype=float)
    return np.column_stack(((v[:,0]-91)*.016, (91-v[:,1])*.016, v[:,2]))

primary = [camera_vertices(o['verts']) for o in data[:5]]
floor_y = max(91 - a[:,1].min()/.016 for a in primary)

def project(a):
    return np.column_stack((91+a[:,0]/.016, 91-a[:,1]/.016, a[:,2]))

def raster(a, polygons):
    p = project(a)
    field = np.full((183,183), -1.e4)
    for face in polygons:
        for k in range(1, len(face)-1):
            q = p[[face[0], face[k], face[k+1]]]
            x0,y0 = np.maximum(0, np.floor(q[:,:2].min(0))).astype(int)
            x1,y1 = np.minimum(182, np.ceil(q[:,:2].max(0))).astype(int)
            if x1<x0 or y1<y0: continue
            x=grid_x[y0:y1+1,x0:x1+1]+.5; y=grid_y[y0:y1+1,x0:x1+1]+.5
            den=(q[1,1]-q[2,1])*(q[0,0]-q[2,0])+(q[2,0]-q[1,0])*(q[0,1]-q[2,1])
            if abs(den)<1.e-9: continue
            a0=((q[1,1]-q[2,1])*(x-q[2,0])+(q[2,0]-q[1,0])*(y-q[2,1]))/den
            b0=((q[2,1]-q[0,1])*(x-q[2,0])+(q[0,0]-q[2,0])*(y-q[2,1]))/den
            c0=1-a0-b0
            z=a0*q[0,2]+b0*q[1,2]+c0*q[2,2]
            region=field[y0:y1+1,x0:x1+1]
            np.maximum(region, np.where((a0>=0)&(b0>=0)&(c0>=0),z,-1.e4), out=region)
    return field

primary_fields = [raster(a,o['faces']) for a,o in zip(primary,data[:5])]
front = np.maximum.reduce(primary_fields)
rgba=np.array(Image.open(REF).convert('RGBA'),dtype=float)
im=rgba[:,:,:3]
patch = (grid_x>=72)&(grid_x<=119)&(grid_y>=108)&(grid_y<=145)
dark = (im.mean(2)<100)&(im[:,:,2]>im[:,:,0]*.65)
allowed = patch&dark&(rgba[:,:,3]>=250)
# 阴暗的同色轮廓允许相邻一像素，避免按像素齿边拟合三维块。
allowed = np.logical_or.reduce([np.roll(np.roll(allowed,y,0),x,1) for y in [-1,0,1] for x in [-1,0,1]])
needed = allowed & (front < -100)
shape=Image.new('1',(183,183));draw=ImageDraw.Draw(shape)
draw.polygon([(73,24),(102,23),(125,36),(143,64),(169,65),(170,111),(155,136),
 (139,141),(118,143),(105,147),(98,143),(83,146),(77,143),(71,145),(31,146),
 (14,116),(11,111),(19,86),(39,81),(43,71),(46,49),(59,36)],fill=1)
silhouette=np.array(shape,dtype=bool)

def rotation(yaw,pitch,roll):
    y,p,r=np.radians([yaw,pitch,roll])
    ry=np.array([[math.cos(y),0,math.sin(y)],[0,1,0],[-math.sin(y),0,math.cos(y)]])
    rx=np.array([[1,0,0],[0,math.cos(p),-math.sin(p)],[0,math.sin(p),math.cos(p)]])
    rz=np.array([[math.cos(r),-math.sin(r),0],[math.sin(r),math.cos(r),0],[0,0,1]])
    return rz@ry@rx

def cube(params, base=False):
    x,y,d,length,yaw,pitch,roll=params
    rot=rotation(yaw,pitch,roll)
    axes=rot@np.diag([length,-length,-length])
    coords=np.array([[0,0,0],[1,0,0],[1,1,0],[0,1,0],[0,0,1],[1,0,1],[1,1,1],[0,1,1]],float)-.5
    v=coords@axes.T
    if base:
        y=floor_y + v[:,1].min()/.016
    return v+np.array([(x-91)*.016,(91-y)*.016,d])

def overlaps(a,b):
    ae=np.stack([a[i]-a[0] for i in [1,3,4]])
    be=np.stack([b[i]-b[0] for i in [1,3,4]])
    ae/=np.linalg.norm(ae,axis=1)[:,None];be/=np.linalg.norm(be,axis=1)[:,None]
    cross=np.cross(ae[:,None,:],be[None,:,:]).reshape(-1,3)
    cross=cross[np.linalg.norm(cross,axis=1)>1.e-6]
    axes=np.concatenate((ae,be,cross)); ap=a@axes.T; bp=b@axes.T
    return np.all((ap.min(0)<=bp.max(0)+1.e-5)&(bp.min(0)<=ap.max(0)+1.e-5))

def loss(p, details=False):
    current=[a.copy() for a in primary]
    current[2][:,2]+=p[14]
    current[3][:,2]+=p[15]
    fields=list(primary_fields)
    fields[2]=fields[2]+p[14]
    fields[3]=fields[3]+p[15]
    front=np.maximum.reduce(fields)
    back=[cube(p[:7],True),cube(p[7:14])]
    rfield=np.maximum(raster(back[0],faces),raster(back[1],faces))
    visible=rfield>front+1.e-4
    miss=np.count_nonzero(needed&~visible)
    unwanted=np.count_nonzero(visible&~allowed)
    outside=np.count_nonzero(visible&~silhouette)
    covering_primary=np.count_nonzero(visible&(front>-100))
    allparts=current+back
    pairs=[];links=[set() for _ in allparts]
    for j in range(5,7):
        for i in range(j):
            if overlaps(allparts[i],allparts[j]):links[i].add(j);links[j].add(i);pairs.append([i,j])
    for j in range(5):
        for i in range(j):
            if overlaps(current[i],current[j]):links[i].add(j);links[j].add(i)
    reached={0};todo=[0]
    while todo:
        q=todo.pop()
        for k in links[q]-reached:reached.add(k);todo.append(k)
    disconnected=7-len(reached)
    full=np.maximum(front,rfield)
    cap_visible=np.count_nonzero((fields[2]>=full-1.e-5)&(fields[2]>-100))
    front_visible=np.count_nonzero((fields[3]>=full-1.e-5)&(fields[3]>-100))
    score=(miss*2.5+unwanted*4+outside*20+covering_primary*4+disconnected*5000
           +max(0,55-cap_visible)*25+max(0,1000-front_visible)*3)
    # 不将整块大面当作中心碎片，先满足遮挡与连接，再由实图判断。
    if details:return dict(score=float(score),missing=int(miss),unwanted=int(unwanted),outside=int(outside),
                          disconnected=disconnected,contacts=pairs,covering_primary=int(covering_primary),
                          visible_pixels=int(visible.sum()),cap_visible=int(cap_visible),front_visible=int(front_visible))
    return score

lower=np.array([65,100,-.95,.64,15,-12,-16,80,75,-.85,.64,-70,-32,-30,-.25,-.55],float)
upper=np.array([100,125,.1,.96,85,12,16,115,104,.05,.96,-10,32,30,-.02,-.10],float)
start=np.array([82,117,-.45,.80,55,0,0,96,90,-.28,.75,-30,15,18,-.14,-.30],float)
rng=np.random.default_rng(21)
best=start.copy();best_score=loss(best)
scale=np.array([10,3,.18,.08,16,7,9,9,7,.17,.07,15,10,10,.04,.07])
t=time.time(); records=[]
for phase in range(5):
    for step in range(400):
        p=np.clip(best+rng.normal(size=16)*scale*(.65**phase),lower,upper)
        s=loss(p)
        if s<best_score:
            best,best_score=p,s
            records.append(dict(step=phase*400+step,params=p.tolist(),**loss(p,True)))
    print('阶段',phase,'得分',best_score,'耗时',round(time.time()-t,1),flush=True)
result=dict(params=best.tolist(),floor_y=float(floor_y),details=loss(best,True),
            camera_vertices=[project(cube(best[:7],True)).tolist(),project(cube(best[7:14])).tolist()],
            note='数值仅为位姿候选；正面对照、真实连接与背侧实图仍须检查。')
(OUT/'后撑位姿候选.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
(OUT/'试配过程.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(result['details'],ensure_ascii=False),flush=True)
