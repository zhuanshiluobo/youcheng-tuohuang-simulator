"""从已修正并验收接缝的文件烘焙和导出；不再执行任何几何生成。"""
import ast
import bpy
import json
import numpy as np
from pathlib import Path
from mathutils import Quaternion,Vector

OUT=Path(__file__).resolve().parent
KEY='OrirockConcentration'
SIZE=2048
bpy.ops.wm.open_mainfile(filepath=str(OUT/(KEY+'_ContactReview.blend')))
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
        if isinstance(node.value,str): node.value=node.value.replace('_UniformStone','_ContactFixed')
        return node
module=Names().visit(ast.Module(body=selected,type_ignores=[]))
scope=dict(bpy=bpy,ast=ast,np=np,OUT=OUT,KEY=KEY,SIZE=SIZE,Path=Path)
exec(compile(module,str(source),'exec'),scope)
scope['finish'](scene,meshes,root,stone)

report=json.loads((OUT/'contact_fix_info.json').read_text(encoding='utf-8'))
camera=scene.camera
front=camera.matrix_world.copy()
view=report['original_view'][0]
rotation=Quaternion(view['rotation'])
camera.rotation_euler=rotation.to_euler()
camera.location=Vector(view['location'])+rotation@Vector((0,0,8))
camera.data.ortho_scale=2.3
scene.render.filepath=str(OUT/'底面_修复后材质.png')
scene.cycles.samples=32
bpy.ops.render.render(write_still=True)
camera.matrix_world=front
camera.data.ortho_scale=2.25
scene.render.filepath=str(OUT/'正面_烘焙材质.png')
scene.cycles.samples=64
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_ContactFixed.blend')))
report['final_blend']=bpy.data.filepath
report['fbx']=str(OUT/(KEY+'_ContactFixed.fbx'))
report['texture_resolution']=[SIZE,SIZE]
report['total_triangles']=sum(len(o.data.polygons) for o in meshes)
(OUT/'contact_fix_info.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print('底面接缝修复、图集烘焙与导出完成',flush=True)
