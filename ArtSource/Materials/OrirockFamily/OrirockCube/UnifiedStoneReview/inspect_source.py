import bpy, json, numpy as np
from pathlib import Path

out=Path(__file__).resolve().parent
bpy.ops.wm.open_mainfile(filepath=str(out.parent/'LowerNotchReview/OrirockCube_LowerNotchReview.blend'))
scene=bpy.data.scenes['固源岩_底部与低位崩口_待验收']
bpy.context.window.scene=scene
body=scene.objects['固源岩_低位崩口与不规则底部']
report={'version':bpy.app.version_string,'scenes':list(bpy.data.scenes.keys()),'objects':[], 'materials':[]}
for o in scene.objects:
    if not o.hide_render:
        info={'name':o.name,'type':o.type,'location':list(o.location),'rotation':list(o.rotation_euler),'scale':list(o.scale),'parent':o.parent.name if o.parent else None}
        if o.type=='LIGHT': info.update(energy=o.data.energy,color=list(o.data.color),size=o.data.size if o.data.type=='AREA' else None)
        report['objects'].append(info)
for i,mat in enumerate(body.data.materials):
    polys=[p for p in body.data.polygons if p.material_index==i]
    ids={v for p in polys for v in p.vertices}
    verts=np.array([tuple(body.data.vertices[v].co) for v in ids])
    if not len(verts): continue
    mean=verts.mean(axis=0)
    _,s,vh=np.linalg.svd(verts-mean)
    report['materials'].append({'index':i,'name':mat.name,'faces':len(polys),'area':sum(p.area for p in polys),'mean':mean.tolist(),'planeNormal':vh[-1].tolist(),'planeVariance':s.tolist(),'bounds':[verts.min(axis=0).tolist(),verts.max(axis=0).tolist()]})
    if i in [2,3,4,10]:
        report['materials'][-1]['bands']=[{'z':[lo,hi],'mean':v.mean(axis=0).tolist(),'min':v.min(axis=0).tolist(),'max':v.max(axis=0).tolist()} for lo,hi in [(-.62,-.40),(-.3,-.1),(.1,.3),(.4,.62)] if len(v:=verts[(verts[:,2]>=lo)&(verts[:,2]<hi)])]
world=scene.world
report['world']=[{'type':n.type,'name':n.name,'inputs':{s.name:list(s.default_value) if hasattr(s.default_value,'__len__') else s.default_value for s in n.inputs if hasattr(s,'default_value')}} for n in world.node_tree.nodes]
(out/'source_inspection.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(report,ensure_ascii=False),flush=True)
