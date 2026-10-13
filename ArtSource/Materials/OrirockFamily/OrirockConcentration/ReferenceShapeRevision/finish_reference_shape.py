import ast, bpy, json
import numpy as np
from pathlib import Path
from mathutils import Quaternion,Vector

OUT=Path(__file__).resolve().parent
KEY='OrirockConcentration'
SIZE=2048
bpy.ops.wm.open_mainfile(filepath=str(OUT/(KEY+'_ReferenceShapeReview.blend')))
bpy.context.preferences.filepaths.save_version=0
scene=bpy.context.scene
meshes=[o for o in scene.objects if o.type=='MESH']
root=next(o for o in scene.objects if o.type=='EMPTY')
stone=meshes[0].data.materials[0]
assert all(len(o.data.materials)==1 and o.data.materials[0]==stone for o in meshes)
camera=scene.camera
# 竖幅相机以较长的画幅尺寸为准，令正面轮廓能直接对应用户图的像素位置。
camera.data.ortho_scale*=241/235
root['尺寸说明']='模型最大尺寸约1.648；各柱分别匹配用户参考六边轮廓，不统一朝向。'
root['最新造型依据']='用户2026-10-11提供的235×241提纯源岩图，去掉圆环、背景和底板。'
source=OUT.parent/'UniformMaterialRevision'/'unify_stone_material.py'
tree=ast.parse(source.read_text(encoding='utf-8'))
selected=[n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name in ('render','progress','finish')]
class Names(ast.NodeTransformer):
    def visit_Constant(self,node):
        if isinstance(node.value,str):node.value=node.value.replace('_UniformStone','_ReferenceShape')
        return node
scope=dict(bpy=bpy,ast=ast,np=np,OUT=OUT,KEY=KEY,SIZE=SIZE,Path=Path)
exec(compile(Names().visit(ast.Module(body=selected,type_ignores=[])),str(source),'exec'),scope)
scope['finish'](scene,meshes,root,stone)

front=camera.matrix_world.copy();ortho=camera.data.ortho_scale
q=Quaternion(json.loads((OUT.parent/'ContactFixRevision'/'contact_fix_info.json').read_text(encoding='utf-8'))['original_view'][0]['rotation'])
camera.rotation_euler=q.to_euler();camera.location=q@Vector((0,0,8));camera.data.ortho_scale=2.25
engine=scene.render.engine
scene.render.engine='BLENDER_WORKBENCH'
scene.display.shading.light='STUDIO';scene.display.shading.color_type='MATERIAL'
scene.display.shading.show_shadows=True;scene.display.shading.show_cavity=True
scene.render.filepath=str(OUT/'底面_结构检查.png')
bpy.ops.render.render(write_still=True)
scene.render.engine=engine;camera.matrix_world=front;camera.data.ortho_scale=ortho
scene.render.filepath=str(OUT/'正面_烘焙材质.png')
for obj in scene.objects:obj.select_set(False)
bpy.context.view_layer.objects.active=None
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_ReferenceShape.blend')))
report=json.loads((OUT/'reference_shape_info.json').read_text(encoding='utf-8'))
report.update(final_blend=bpy.data.filepath,fbx=str(OUT/(KEY+'_ReferenceShape.fbx')),texture_resolution=[SIZE,SIZE],
    export_contents='三个六棱柱与模型根；无背景、圆环、底板、相机及灯光',unity_live_verified=False)
(OUT/'reference_shape_info.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print('参考造型版烘焙及导出完成',flush=True)
