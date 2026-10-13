import ast,bpy,json,math
import numpy as np
from pathlib import Path

OUT=Path(__file__).resolve().parent
KEY='OrirockConcentration';SIZE=2048
bpy.ops.wm.open_mainfile(filepath=str(OUT/(KEY+'_RemainingEdgesReview.blend')))
bpy.context.preferences.filepaths.save_version=0
scene=bpy.context.scene
meshes=[o for o in scene.objects if o.type=='MESH']
root=next(o for o in scene.objects if o.type=='EMPTY')
stone=meshes[0].data.materials[0]
report=json.loads((OUT/'chip_review.json').read_text(encoding='utf-8'))
errors=[]
for obj in meshes:
    assert len(obj.data.materials)==1 and obj.data.materials[0]==stone
    for p in obj.data.polygons:
        if abs(p.normal.z)<1e-5 and p.area>.015:
            angle=math.degrees(math.atan2(p.normal.y,p.normal.x))
            errors.append(abs((angle-13.5)%60-30))
report['remaining_main_side_alignment_error_degrees']=max(errors)
assert max(errors)<.001,max(errors)
source=OUT.parent/'UniformMaterialRevision'/'unify_stone_material.py'
tree=ast.parse(source.read_text(encoding='utf-8'))
selected=[n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name in ('render','progress','finish')]
class Names(ast.NodeTransformer):
    def visit_Constant(self,node):
        if isinstance(node.value,str):node.value=node.value.replace('_UniformStone','_RemainingEdges')
        return node
scope=dict(bpy=bpy,ast=ast,np=np,OUT=OUT,KEY=KEY,SIZE=SIZE,Path=Path)
exec(compile(Names().visit(ast.Module(body=selected,type_ignores=[])),str(source),'exec'),scope)
scope['finish'](scene,meshes,root,stone)
for obj in scene.objects:obj.select_set(False)
bpy.context.view_layer.objects.active=None
scene.render.filepath=str(OUT/'正面_完整边缘缺口.png')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_RemainingEdges.blend')))
report.update(final_blend=bpy.data.filepath,fbx=str(OUT/(KEY+'_RemainingEdges.fbx')),texture_resolution=[SIZE,SIZE],unity_live_verified=False)
(OUT/'chip_review.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print('各边角缺口源文件、贴图与 FBX 完成',flush=True)
