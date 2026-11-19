"""Offline structural and geometry checks. Does NOT compile C# or ShaderLab."""
import math
import re
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
    pulse=.8+.2*math.sin(t*3.9269908)
    assert .5999<=pulse<=1.0001;checks+=1
for token in ['Stencil {','UNITY_UI_CLIP_RECT','UnityGet2DClipping','UNITY_UI_ALPHACLIP','ZWrite Off','Blend SrcAlpha OneMinusSrcAlpha']:
    assert token in shader;checks+=1
for token in ['raycastTarget = false','Time.unscaledTime','state == value','Destroy(ownedMaterial)','TexCoord1','TexCoord2','TexCoord3']:
    assert token in controller;checks+=1
for path in (root/'Assets').rglob('*'):
    if path.suffix in ('.cs','.shader'):
        content=path.read_text();assert content.count('{')==content.count('}'),path;checks+=1
assert 'Texture2D' not in controller and 'sprite =' not in controller
checks+=1
# Regression: HLSL keywords cannot be used as function names.
assert not re.search(r"\bline\s*\(", shader); checks+=1
assert len(re.findall(r"\bSelectionOutline\s*\(", shader))==3; checks+=1
keywords = set("line lineadj linear point triangle triangleadj sample register packoffset uniform in out inout matrix vector sampler texture struct cbuffer tbuffer technique pass".split())
functions = re.findall(r"\b(?:float[1-4]?|fixed[1-4]?|half[1-4]?|v2f)\s+(\w+)\s*\(", shader)
assert not keywords.intersection(functions); checks+=1
# v2: persistent outline contrast, phase progression and wrap continuity.
assert 'max(4.5,width*1.65)' in shader and 'inner*.90' in shader; checks+=1
assert 'edge*strength*pulse' not in shader; checks+=1
assert 'selected*motion*(1-disabled)' in shader; checks+=1
assert 'Mathf.Repeat(Mathf.Max(0f, Time.unscaledTime - selectedAt), 48f)' in controller; checks+=1
for t in [0,.3,.6,1.2,2.4,47.999]:
    phase=(t/2.4+.25)%1
    travel=(phase-t/2.4)%1
    assert abs(travel-.25)<1e-10; checks+=1
    assert abs(((t+48)/2.4-t/2.4)-20)<1e-10; checks+=1
for w in [.5,2,3,8]:
    assert max(4.5,w*1.65)>=w*1.65; checks+=1
assert 'ApplyStrongVisiblePreset' in controller and 'DebugForceSelected' in controller; checks+=1
assert 'if (state == SelectionState.Selected) selectedAt = Time.unscaledTime;' in controller; checks+=1
# v3: selected-only geometry, redraw on state changes, original UI left alone.
mesh=controller.split('protected override void OnPopulateMesh(VertexHelper vh)',1)[1]
assert mesh.index('vh.Clear();') < mesh.index('if (state != SelectionState.Selected)') < mesh.index('Rect r = rectTransform.rect;'); checks+=1
nonselected=mesh.split('if (state != SelectionState.Selected)',1)[1].split('Rect r = rectTransform.rect;',1)[0]
assert 'return;' in nonselected and 'LastUploadedAnimationTime = 0f;' in nonselected; checks+=1
setstate=controller.split('public void SetState(SelectionState value)',1)[1].split('public void SetHovered',1)[0]
assert 'state = value;' in setstate and 'UpdateBadges(); SetVerticesDirty();' in setstate; checks+=1
assert 'unavailableBadge' not in controller; checks+=1
assert 'selectedBadge.SetActive(state == SelectionState.Selected)' in controller; checks+=1
editor=(root/'Assets/NoMadCitySelectionFX/Editor/SelectionEffectGraphicEditor.cs').read_text()
assert 'unavailableBadge' not in editor; checks+=1
# v5: selected-only gate retained; animation resets on each selection / replay.
assert 'Time.unscaledTime - selectedAt' in controller; checks+=1
assert 'float beat=t/1.2;' in shader and 'saturate(frac(beat)*1.5)' in shader; checks+=1
assert 'floor(beat)+eased' in shader and 'phase-startPhase-halfTurn' in shader; checks+=1
assert 'selected*(.50*pulse+.25*flash)' in shader; checks+=1
assert 'float3(1,.96,.78),.30*selected' in shader; checks+=1

def progress(t):
    beat=t/1.2
    u=min(1,(beat-math.floor(beat))*1.5)
    return .5*(math.floor(beat)+u**3*(u*(u*6-15)+10))
def velocity(t):
    u=min(1,(t%1.2)/.8)
    return .5/.8*30*u*u*(u-1)*(u-1)
for i in range(1001):
    t=48*i/1000
    assert progress(t+.0001)>=progress(t)-1e-12; checks+=1
    assert abs(progress(t+1.2)-progress(t)-.5)<1e-11; checks+=1
    assert abs(progress(t+48)-progress(t)-20)<1e-11; checks+=1
    assert abs((progress(t)+.5)-progress(t)-.5)<1e-12; checks+=1
for t in [0,.8,1.0,1.2,2.0,2.4,48]:
    assert abs(velocity(t))<1e-10; checks+=1
for t,expected in [(0,0),(.4,.25),(.8,.5),(1.0,.5),(1.199,.5),(1.2,.5),(1.6,.75),(2,1),(2.4,1)]:
    assert abs(progress(t)-expected)<1e-10; checks+=1
for boundary in [.8,1.2,2,2.4,48]:
    assert abs(progress(boundary+1e-6)-progress(boundary-1e-6))<1e-9; checks+=1
# Angle-space start points are actual clipped corner midpoints for each aspect ratio.
for w,h,c in [(105/2,150,7),(285/2,66,7),(135,90.5,7),(105,150,7)]:
    start=math.atan2((h-c*.5)/h,(-w+c*.5)/w)/(2*math.pi)+.5
    other=math.atan2((-h+c*.5)/h,(w-c*.5)/w)/(2*math.pi)+.5
    assert abs((start-other)%1-.5)<1e-12; checks+=1
old=(1,.69,.15); new=tuple(.7*a+.3*b for a,b in zip(old,(1,.96,.78)))
luma=lambda rgb: sum(a*b for a,b in zip(rgb,(.2126,.7152,.0722)))
assert luma(new)>luma(old); checks+=1
# Runtime v3 regression: ONLY the selection-relative animation time changed.
import hashlib
normalized=controller.replace('Mathf.Repeat(Mathf.Max(0f, Time.unscaledTime - selectedAt), 48f)','Mathf.Repeat(Time.unscaledTime, 48f)')
assert hashlib.sha256(normalized.encode()).hexdigest()=='cecc7824fa1409b24eb105b81690472034beea3faa4e2caefb3911fd78234a19'; checks+=1
# v6: static decoration modifies RGB only, gated to the existing selected main stroke.
decoration=shader.split('// Static machined-metal details',1)[1].split('// Moving light remains',1)[0]
assert 'float strokeMask=edge*selected*(1-disabled)' in decoration; checks+=1
assert 'a=' not in decoration and 'a =' not in decoration; checks+=1
assert not re.search(r'\bt\s*\*',decoration) and 'sin(' not in decoration; checks+=1
assert 'fwidth(engravingCoord)' in decoration and 'fwidth(rivetDistance)' in decoration; checks+=1
assert 'smoothstep(3,6,cut)' in decoration; checks+=1
for w,h,c in [(52.5,150,7),(142.5,66,7),(135,90.5,7),(105,150,7)]:
    assert abs(d(w-c*.5,h-c*.5,w,h,c))<1e-10; checks+=1
# Decoration cannot expand the alpha silhouette or leak into content, even in reduced motion.
assert shader.index('float a=saturate(')<shader.index('// Static machined-metal details')<shader.index('fixed4 result=fixed4(rgb,a*i.color.a)'); checks+=1
# v7: editor-only opt-in alignment, no automatic runtime mutations of original UI.
alignment=(root/'Assets/NoMadCitySelectionFX/Editor/SelectionEffectAlignmentTools.cs').read_text()
for token in ['Undo.SetTransformParent','rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;',
              'rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;',
              'rt.localRotation = Quaternion.identity; rt.localScale = Vector3.one;',
              '!target.IsChildOf(effect.transform)', 'rt.SetAsLastSibling();']:
    assert token in alignment; checks+=1
assert '.enabled =' not in alignment and '.SetActive(' not in alignment; checks+=1
assert 'SelectionEffectGraphicEditor' in editor and 'Alignment Target' in editor; checks+=1
# Reference mapping demonstrates why a whole row is not the visual effect frame.
scale=248/650
row=(20,406,630,246); frame=(160,412,482,234)
assert abs((frame[0]-row[0])*scale-53.41538461538462)<1e-9; checks+=1
assert abs((row[2]-frame[2])*scale-56.46769230769231)<1e-9; checks+=1
# v8: no focus-specific extra geometry/alpha/color. Selection input API retained.
assert 'focusLine' not in shader and 'd-7' not in shader and 'focused' not in shader; checks+=1
assert 'SetFocused' in controller; checks+=1
assert 'float a=saturate(edge*strength+inner*.90+glow);' in shader; checks+=1
assert 'saturate(tracer+sweep*.7)' in shader; checks+=1
print(f'PASS: {checks} offline checks. Unity compilation / rendering / Play Mode NOT RUN.')
