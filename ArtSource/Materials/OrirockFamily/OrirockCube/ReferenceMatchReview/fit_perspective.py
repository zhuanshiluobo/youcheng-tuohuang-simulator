import math,numpy as np
import reference_shape as ref

def build(az,el,d=3.5,unit=.012):
    toward,right,up=ref.axes(az,el); origin=toward*d
    result={}
    for name,x,y,axis,value in ref.ANCHORS+ref.CHIP:
        if axis=='Y': value+=.18
        ray=-toward+(right*(x-90)+up*(90-y))*unit/d
        a='XYZ'.index(axis)
        t=(value-origin[a])/ray[a]
        result[name]=origin+ray*t
    return result

def score(q):
    az,el,d,unit=q
    if not(15<az<38 and 15<el<42 and 1.8<d<12 and .009<unit<.018): return 1e3
    p=build(*q)
    v=np.array([p[a[0]] for a in ref.ANCHORS]); size=v.max(0)-v.min(0)
    cap=np.array([p[k][2] for k in ['RT','TR','L0','L1','L2','L3','L4','L6','L7','L8']])
    bottom=np.array([p[k][2] for k in ['F2','F3','F4','FB','R3']])
    return np.std(size)/np.mean(size)+np.std(bottom)*2+abs(p['RT'][2]-p['TR'][2])*2+np.maximum(cap-.64,0).sum()*2

if __name__=='__main__':
    rng=np.random.default_rng(119)
    candidates=[]
    for _ in range(5000):
        q=[rng.uniform(18,34),rng.uniform(18,38),rng.uniform(2,7),rng.uniform(.01,.016)]
        candidates.append((score(q),q))
    value,q=min(candidates)
    step=np.array([2.,2.,.4,.0005]); q=np.array(q)
    for iteration in range(160):
        better=False
        for j in range(4):
            for sign in [-1,1]:
                alt=q.copy(); alt[j]+=step[j]*sign
                result=score(alt)
                if result<value: value=result; q=alt; better=True
        if not better: step*=.8
        if max(step[:3])<1e-4: break
    print(value,q.tolist())
    p=build(*q); coords=np.array([p[a[0]] for a in ref.ANCHORS]); print('size',coords.max(0)-coords.min(0))
    for name in ['TL','BL1','B2','RT','TR','L0','L3','L5','L6','L7','L8','F1','F2','F3','F4','FB','R3']:
        print(name,p[name].round(4))
