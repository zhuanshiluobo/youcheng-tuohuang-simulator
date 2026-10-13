"""六边形保持正形、方向一致；不额外把三个角强行收拢到同一点。"""
import json,math
from pathlib import Path
import numpy as np

OUT=Path(__file__).resolve().parent
state=json.loads((OUT.parent/'ViewSizeRevision/current_user_view.json').read_text(encoding='utf-8'))
M=np.array(state['camera']['matrix'])
R,U=M[:3,0],M[:3,1]
units=state['camera']['ortho_scale']/241
pixels=np.array([
 [[26,58],[54,21],[111,16],[146,48],[125,86],[61,92]],
 [[91,92],[119,54],[169,50],[210,81],[196,124],[130,131]],
 [[30,145],[53,112],[105,110],[145,145],[130,181],[65,185]],
],float)
target=np.empty_like(pixels)
target[:,:,0]=(pixels[:,:,0]-117.5)*units+R@M[:3,3]
target[:,:,1]=(120.5-pixels[:,:,1])*units+U@M[:3,3]
tops=[.5132332444,.197935656,.012399554]
best=None
for yaw in np.linspace(0,30,601):
    angles=np.radians(yaw+180-np.arange(6)*60)
    ring=np.column_stack((np.cos(angles),np.sin(angles)))
    A=[];b=[]
    for part in range(3):
        for k in range(6):
            for axis,proj in enumerate([R,U]):
                row=np.zeros(9);row[part*3:part*3+2]=proj[:2]
                row[part*3+2]=proj[:2]@ring[k]
                value=target[part,k,axis]-proj[2]*tops[part]
                A.append(row);b.append(value)
    A=np.array(A);b=np.array(b)
    fit=np.linalg.lstsq(A,b,rcond=None)[0]
    err=np.mean((A@fit-b)**2)
    if best is None or err<best[0]:best=(err,yaw,fit,ring)
err,yaw,fit,ring=best
bottom=-.4585767984
parts=[]
for i in range(3):
    radius=fit[i*3+2];center=fit[i*3:i*3+2]
    parts.append(dict(label=['左后','右后','前方'][i],center=center.tolist(),radius=float(radius),top=float(tops[i]),bottom=float(bottom)))
report=dict(yaw_degrees=float(yaw),parts=parts,reference_fit_error_pixels=float(math.sqrt(err)/units),camera=state['camera'],view=state['views'][0],note='主轮廓为正六边形；三柱同向，六组主侧面分别平行，上下顶点竖直对应。实体向下嵌合，不额外强制顶点重合。参考中被高柱挡住的右后左角为推定。')
(OUT/'layout.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in report.items() if k not in ['camera','view']},ensure_ascii=False,indent=2))
