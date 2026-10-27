from pathlib import Path
import numpy as np
from PIL import Image
p=Path(__file__).resolve().parent
ref=np.array(Image.open(p.parents[1]/'reference'/'OrirockCluster_PRTS.png').convert('RGB'),float)
rend=np.array(Image.open(p/'石质_正面.png').convert('RGB'),float)
for label,(x,y) in {'顶灰':(80,57),'顶浅':(122,56),'顶褐':(109,90),'左灰':(43,126),'左浅':(67,112),'左上':(38,96),'小浅':(89,104),'右前浅':(119,113),'右前灰':(106,132),'右前褐':(128,133),'右后灰':(150,120),'右后上':(158,80),'右后浅':(164,107)}.items():
 a=ref[y-1:y+2,x-1:x+2].mean((0,1))
 xx,yy=[round((c-91)*1000/183+500) for c in (x,y)]
 b=rend[yy-8:yy+9,xx-8:xx+9].mean((0,1))
 print(label, a.round(1).tolist(),b.round(1).tolist())
