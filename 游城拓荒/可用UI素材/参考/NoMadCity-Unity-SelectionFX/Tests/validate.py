"""Offline structural and geometry checks. Does NOT compile C# or ShaderLab."""
import math
from pathlib import Path
root=Path(__file__).resolve().parents[1]
shader=(root/'Assets/NoMadCitySelectionFX/Resources/NoMadCitySelection.shader').read_text()
controller=(root/'Assets/NoMadCitySelectionFX/Runtime/SelectionEffectGraphic.cs').read_text()

def d(x,y,w,h,c):
    qx,qy=abs(x)-w,abs(y)-h
    return max(qx,qy,(qx+qy+c)/math.sqrt(2))

checks=0
for width,height in [(190,541),(280,542.635659),(380,255),(240,340),(90,42)]:
    w,h=width/2,height/2;c=min(7,w,h)
    assert d(0,0,w,h,c)<0
    assert abs(d(w,0,w,h,c))<1e-7
    assert abs(d(w-c/2,h-c/2,w,h,c))<1e-7
    assert d(w,h,w,h,c)>0
    assert d(w+20,0,w,h,c)>=20
    checks+=5
for elapsed,expected in [(0,1),(.175,.5),(.35,0),(1,0)]:
    flash=max(0,min(1,1-elapsed/.35))
    assert abs(flash-expected)<1e-6; checks+=1
for t in range(100):
    pulse=.88+.12*math.sin(t*2.0943951)
    assert .7599<=pulse<=1.0001;checks+=1
for token in ['Stencil {','UNITY_UI_CLIP_RECT','UnityGet2DClipping','UNITY_UI_ALPHACLIP','ZWrite Off','Blend SrcAlpha OneMinusSrcAlpha']:
    assert token in shader;checks+=1
for token in ['raycastTarget = false','Time.unscaledTime','state == value','Destroy(ownedMaterial)','TexCoord1','TexCoord2','TexCoord3']:
    assert token in controller;checks+=1
for path in (root/'Assets').rglob('*'):
    if path.suffix in ('.cs','.shader'):
        content=path.read_text();assert content.count('{')==content.count('}'),path;checks+=1
assert 'Texture2D' not in controller and 'sprite =' not in controller
checks+=1
print(f'PASS: {checks} offline checks. Unity compilation / rendering / Play Mode NOT RUN.')
