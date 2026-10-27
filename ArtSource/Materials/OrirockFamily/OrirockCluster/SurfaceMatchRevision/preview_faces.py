from pathlib import Path
import json,numpy as np
from PIL import Image
OUT=Path(__file__).resolve().parent
src=(OUT/'fit_floor_support.py').read_text(encoding='utf-8-sig');src=src[:src.index('lower=np.array')];exec(compile(src,'projection','exec'))
cand=json.loads((OUT/'地板后撑方案.json').read_text(encoding='utf-8'))
allparts=primary+rear(np.array(cand['params']))
z=np.full((183,183),-1.e4);pic=np.zeros((183,183,4),np.uint8)
palette=np.array([(211,195,156),(150,128,80),(53,55,58),(99,96,67),(108,94,64),(112,108,91),(29,34,35),(42,52,58),(192,181,140),(211,195,156)],np.uint8)
for j,v in enumerate(allparts):
 fs=data[j]['faces'] if j<5 else faces
 ms=data[j]['materials'] if j<5 else ([5,7,2,2,3,7] if j==5 else [2,3,4,2,3,2])
 for face,mi in zip(fs,ms):
  f=raster(v,[face]);mask=f>z
  z[mask]=f[mask];pic[mask,:3]=palette[mi];pic[mask,3]=255
Image.fromarray(pic).resize((732,732),Image.Resampling.NEAREST).save(OUT/'块面投影_待检查.png')
print('保存投影候选')
