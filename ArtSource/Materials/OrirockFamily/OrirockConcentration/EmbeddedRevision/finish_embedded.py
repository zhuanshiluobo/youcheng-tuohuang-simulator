import ast,bpy,json
import numpy as np
from pathlib import Path

OUT=Path(__file__).resolve().parent
KEY='OrirockConcentration'
SIZE=2048
bpy.ops.wm.open_mainfile(filepath=str(OUT/(KEY+'_EmbeddedReview.blend')))
bpy.context.preferences.filepaths.save_version=0
scene=bpy.context.scene
meshes=[o for o in scene.objects if o.type=='MESH']
root=next(o for o in scene.objects if o.type=='EMPTY')
stone=meshes[0].data.materials[0]
assert all(len(o.data.materials)==1 and o.data.materials[0]==stone for o in meshes)
source=OUT.parent/'UniformMaterialRevision'/'unify_stone_material.py'
tree=ast.parse(source.read_text(encoding='utf-8'))
selected=[n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name in ('render','progress','finish')]
class Names(ast.NodeTransformer):
    def visit_Constant(self,node):
        if isinstance(node.value,str):node.value=node.value.replace('_UniformStone','_Embedded')
        return node
scope=dict(bpy=bpy,ast=ast,np=np,OUT=OUT,KEY=KEY,SIZE=SIZE,Path=Path)
exec(compile(Names().visit(ast.Module(body=selected,type_ignores=[])),str(source),'exec'),scope)
scope['finish'](scene,meshes,root,stone)
for obj in scene.objects:obj.select_set(False)
bpy.context.view_layer.objects.active=None
root['结构说明']='左后石柱向下连续嵌入整体岩体；整体只有一个闭合连通外壳，内部交叠面已移除。'
whole=bpy.data.meshes.get('提纯源岩_完整闭合嵌合外壳')
if whole:
    whole.materials.clear();whole.materials.append(stone)
    for p in whole.polygons:p.material_index=0
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_Embedded.blend')))
report=json.loads((OUT/'embedded_info.json').read_text(encoding='utf-8'))
report.update(final_blend=bpy.data.filepath,fbx=str(OUT/(KEY+'_Embedded.fbx')),texture_resolution=[SIZE,SIZE],
    camera_matrix=[list(r) for r in scene.camera.matrix_world],
    parts=[dict(name=o.name,dimensions=list(o.dimensions),material=o.data.materials[0].name,
       material_slots=len(o.data.materials),bottom=min(v.co.z for v in o.data.vertices),top=max(v.co.z for v in o.data.vertices)) for o in meshes],
    export_contents='三个岩柱与模型根；无背景、底板、相机和灯光',unity_live_verified=False)
(OUT/'embedded_info.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print('当前视角比例修正版烘焙与导出完成',flush=True)
