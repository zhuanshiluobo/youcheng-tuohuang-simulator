import ast,bpy,json,math
import numpy as np
from pathlib import Path

OUT=Path(__file__).resolve().parent
KEY='OrirockConcentration';SIZE=2048
bpy.ops.wm.open_mainfile(filepath=str(OUT/(KEY+'_AlignedHexReview.blend')))
bpy.context.preferences.filepaths.save_version=0
scene=bpy.context.scene
meshes=[o for o in scene.objects if o.type=='MESH']
root=next(o for o in scene.objects if o.type=='EMPTY')
stone=meshes[0].data.materials[0]
layout=json.loads((OUT/'layout.json').read_text(encoding='utf-8'))
report=json.loads((OUT/'geometry_review.json').read_text(encoding='utf-8'))
normal_errors=[]
for obj in meshes:
    assert len(obj.data.materials)==1 and obj.data.materials[0]==stone
    for p in obj.data.polygons:
        if abs(p.normal.z)<1e-5 and p.area>.01:
            a=math.degrees(math.atan2(p.normal.y,p.normal.x))
            normal_errors.append(abs((a-layout['yaw_degrees']-30+30)%60-30))
report['main_side_normal_alignment_error_degrees']=max(normal_errors)
assert max(normal_errors)<.001,max(normal_errors)
source=OUT.parent/'UniformMaterialRevision'/'unify_stone_material.py'
tree=ast.parse(source.read_text(encoding='utf-8'))
selected=[n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name in ('render','progress','finish')]
class Names(ast.NodeTransformer):
    def visit_Constant(self,node):
        if isinstance(node.value,str):node.value=node.value.replace('_UniformStone','_AlignedHex')
        return node
scope=dict(bpy=bpy,ast=ast,np=np,OUT=OUT,KEY=KEY,SIZE=SIZE,Path=Path)
exec(compile(Names().visit(ast.Module(body=selected,type_ignores=[])),str(source),'exec'),scope)
scope['finish'](scene,meshes,root,stone)
for obj in scene.objects:obj.select_set(False)
bpy.context.view_layer.objects.active=None
root['结构说明']='三个同向正六棱柱，上下对应、主侧棱竖直，向下嵌合成连续岩体。'
scene.render.filepath=str(OUT/'正面_同向六棱柱.png')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_AlignedHex.blend')))
report.update(final_blend=bpy.data.filepath,fbx=str(OUT/(KEY+'_AlignedHex.fbx')),texture_resolution=[SIZE,SIZE],unity_live_verified=False)
(OUT/'geometry_review.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print('六棱柱正式源、贴图及 FBX 已保存',flush=True)
