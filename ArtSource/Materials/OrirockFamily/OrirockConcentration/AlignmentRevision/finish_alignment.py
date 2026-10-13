import ast, bpy, json
import numpy as np
from pathlib import Path
from mathutils import Vector

OUT=Path(__file__).resolve().parent
KEY='OrirockConcentration'
SIZE=2048

def bounds(meshes):
    points=[o.matrix_world@v.co for o in meshes for v in o.data.vertices]
    low=Vector(tuple(min(p[i] for p in points) for i in range(3)))
    high=Vector(tuple(max(p[i] for p in points) for i in range(3)))
    return (low+high)/2,max(high-low)

# 保持正式资产的整体中心和最大尺寸，展示预制体无需改动。
bpy.ops.wm.open_mainfile(filepath=str(OUT.parent/'ContactFixRevision'/(KEY+'_ContactFixed.blend')))
old_center,old_size=bounds([o for o in bpy.context.scene.objects if o.type=='MESH'])
bpy.ops.wm.open_mainfile(filepath=str(OUT/(KEY+'_AlignedReview.blend')))
bpy.context.preferences.filepaths.save_version=0
scene=bpy.context.scene
meshes=[o for o in scene.objects if o.type=='MESH']
root=next(o for o in scene.objects if o.type=='EMPTY')
stone=meshes[0].data.materials[0]
center,size=bounds(meshes)
scale=old_size/size
for obj in meshes:
    for v in obj.data.vertices:v.co=(v.co-center)*scale+old_center
    obj.data.update()
root['尺寸说明']='保持上阶段模型整体中心和最大尺寸；棱柱改为同向侧边对齐。'
source=OUT.parent/'UniformMaterialRevision'/'unify_stone_material.py'
tree=ast.parse(source.read_text(encoding='utf-8'))
selected=[n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name in ('render','progress','finish')]
class Names(ast.NodeTransformer):
    def visit_Constant(self,node):
        if isinstance(node.value,str):node.value=node.value.replace('_UniformStone','_Aligned')
        return node
scope=dict(bpy=bpy,ast=ast,np=np,OUT=OUT,KEY=KEY,SIZE=SIZE,Path=Path)
exec(compile(Names().visit(ast.Module(body=selected,type_ignores=[])),str(source),'exec'),scope)
scope['finish'](scene,meshes,root,stone)
for obj in scene.objects:obj.select_set(False)
bpy.context.view_layer.objects.active=None
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_Aligned.blend')))
report=json.loads((OUT/'alignment_info.json').read_text(encoding='utf-8'))
report.update(final_blend=bpy.data.filepath,fbx=str(OUT/(KEY+'_Aligned.fbx')),texture_resolution=[SIZE,SIZE],
    fitted_scale=scale,old_center=list(old_center),new_center=list(bounds(meshes)[0]),max_dimension=bounds(meshes)[1])
(OUT/'alignment_info.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print('对齐版贴图烘焙与导出完成',flush=True)
