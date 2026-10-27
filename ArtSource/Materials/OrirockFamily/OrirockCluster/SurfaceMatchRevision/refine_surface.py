"""实际预览后修正过强云斑及统一框边；不改变任何模型顶点。"""
import bpy
from pathlib import Path
import importlib,sys,json
from mathutils import Vector

ns=bpy.app.driver_namespace['cluster_square_revision']
OUT=ns['OUT'];scene=ns['scene']
sys.path.insert(0,str(OUT))
import stone_materials
importlib.reload(stone_materials)

def localized_skin(mat,axis,upper,width,rgb,amount=1.,cross_limit=None):
    n,l=mat.node_tree.nodes,mat.node_tree.links
    bs=next(t for t in n if t.type=='BSDF_PRINCIPLED')
    previous=bs.inputs['Base Color'].links[0].from_socket
    uv=n.new('ShaderNodeUVMap');uv.uv_map='StoneFace'
    separate=n.new('ShaderNodeSeparateXYZ');l.new(uv.outputs[0],separate.inputs[0])
    distance=n.new('ShaderNodeMath');distance.operation='SUBTRACT'
    if upper:distance.inputs[0].default_value=1;l.new(separate.outputs[axis],distance.inputs[1])
    else:l.new(separate.outputs[axis],distance.inputs[0]);distance.inputs[1].default_value=0
    noise=n.new('ShaderNodeTexNoise');noise.inputs['Scale'].default_value=8;noise.inputs['Detail'].default_value=4
    l.new(uv.outputs[0],noise.inputs['Vector'])
    factor=n.new('ShaderNodeMath');factor.operation='MULTIPLY';factor.inputs[1].default_value=width
    l.new(noise.outputs['Fac'],factor.inputs[0])
    mask=n.new('ShaderNodeMath');mask.operation='SUBTRACT';l.new(factor.outputs[0],mask.inputs[0]);l.new(distance.outputs[0],mask.inputs[1])
    sharp=n.new('ShaderNodeMath');sharp.operation='MULTIPLY';sharp.use_clamp=True;sharp.inputs[1].default_value=140
    l.new(mask.outputs[0],sharp.inputs[0])
    mask_out=sharp.outputs[0]
    if cross_limit is not None:
        cross=n.new('ShaderNodeMath');cross.operation='SUBTRACT';cross.inputs[0].default_value=cross_limit;l.new(separate.outputs[1-axis],cross.inputs[1])
        gradient=n.new('ShaderNodeMath');gradient.operation='MULTIPLY';gradient.use_clamp=True;gradient.inputs[1].default_value=1/cross_limit;l.new(cross.outputs[0],gradient.inputs[0])
        gate=n.new('ShaderNodeMath');gate.operation='MULTIPLY';l.new(mask_out,gate.inputs[0]);l.new(gradient.outputs[0],gate.inputs[1]);mask_out=gate.outputs[0]
    opacity=n.new('ShaderNodeMath');opacity.operation='MULTIPLY';opacity.inputs[1].default_value=amount;l.new(mask_out,opacity.inputs[0])
    mix=n.new('ShaderNodeMixRGB');mix.blend_type='MIX';mix.inputs[2].default_value=stone_materials._linear_rgba(rgb)
    l.new(opacity.outputs[0],mix.inputs[0]);l.new(previous,mix.inputs[1]);l.new(mix.outputs[0],bs.inputs['Base Color'])
    mix.label='参考图局部风化皮，颜色而非额外几何'

oldmat=list(ns['solid'].data.materials);mapping={};new_materials=[]
for block,part in enumerate(ns['parts']):
    for face,old in enumerate(list(part.data.materials)):
        rgb=ns['block_colors'][block][face]
        light=sum(rgb)/3>145;dark=sum(rgb)/3<80
        # 深色芯面不是带金色相框的板材；只保留细粒和极窄不均匀边缘。
        amount=.026 if light else (.006 if dark else .046)
        weather=(142,116,72) if light else ((86,84,64) if dark else (82,76,52))
        mat=stone_materials.build_face_material(old.name+'_微粒修订',rgb,weather,amount,1.)
        if block==0 and face==0:localized_skin(mat,0,True,.36,(154,128,80),.98,.52)
        if block==0 and face==2:localized_skin(mat,0,False,.09,(124,104,68),.35)
        if block==1 and face==5:localized_skin(mat,0,True,.34,(163,130,77),.72)
        if block==3 and face==2:localized_skin(mat,0,False,.14,(155,128,73),.24)
        if block==4 and face==5:localized_skin(mat,0,True,.23,(150,124,71),.45)
        mapping[old]=mat;part.data.materials[face]=mat;new_materials.append(mat)
for i,mat in enumerate(oldmat):ns['solid'].data.materials[i]=mapping[mat]
ns['materials']=new_materials

# 中性漫射环境让真实向下的赭色面可读；不把照明烘焙进基色，不改游戏公共灯光。
scene.render.engine='CYCLES';scene.cycles.samples=24
scene.world.use_nodes=True
background=next(n for n in scene.world.node_tree.nodes if n.type=='BACKGROUND')
background.inputs['Color'].default_value=(.72,.72,.72,1);background.inputs['Strength'].default_value=.52
ns['render_surface']()
print('细粒石质与局部风化已修订')
