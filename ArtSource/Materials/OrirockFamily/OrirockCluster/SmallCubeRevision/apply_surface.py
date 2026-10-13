"""从参考的同色区域取岩质表面纹理，不将完整图标投射成虚假的分块。"""
import bpy
from pathlib import Path
ns=bpy.app.driver_namespace['cluster_square_revision']
right,up,front=(ns[k] for k in ('right','up','front'))
reference=Path(__file__).resolve().parents[2]/'reference'/'OrirockCluster_PRTS.png'
picture=bpy.data.images.load(str(reference),check_existing=True)
picture.pack()
for obj in [ns['solid']]+ns['parts']:
    uv=obj.data.uv_layers.get('ReferencePaint') or obj.data.uv_layers.new(name='ReferencePaint')
    for loop in obj.data.loops:
        p=obj.data.vertices[loop.vertex_index].co
        uv.data[loop.index].uv=((91+p.dot(right)/.016)/183,1-(91-p.dot(up)/.016)/183)
# 每种面的采样区域完全在参考中对应颜色内部，不含轮廓、圆环、底座或其他块。
patches=[(113,46,126,69),(123,131,134,135),(60,50,85,70),
         (33,94,47,99),(101,83,113,88),(104,85,111,90),
         (79,122,82,128),(31,123,46,138),(84,98,89,101),(113,46,126,69)]
for i,mat in enumerate(ns['materials']):
    nodes,links=mat.node_tree.nodes,mat.node_tree.links
    bs=next(n for n in nodes if n.type=='BSDF_PRINCIPLED')
    base=bs.inputs['Base Color'].links[0].from_socket
    uv=nodes.new('ShaderNodeUVMap'); uv.uv_map='ReferencePaint'
    scale=nodes.new('ShaderNodeVectorMath'); scale.operation='MULTIPLY'
    x0,y0,x1,y1=patches[i]
    scale.inputs[1].default_value=((x1-x0)/183,(y1-y0)/183,0)
    links.new(uv.outputs[0],scale.inputs[0])
    offset=nodes.new('ShaderNodeVectorMath'); offset.operation='ADD'
    offset.inputs[1].default_value=(x0/183,1-y1/183,0)
    links.new(scale.outputs[0],offset.inputs[0])
    tex=nodes.new('ShaderNodeTexImage'); tex.image=picture
    tex.label='参考同色区域的岩质纹理：不投射图标轮廓'
    links.new(offset.outputs[0],tex.inputs[0])
    mix=nodes.new('ShaderNodeMixRGB'); mix.blend_type='MIX'; mix.inputs[0].default_value=.38
    links.new(base,mix.inputs[1]); links.new(tex.outputs['Color'],mix.inputs[2])
    links.new(mix.outputs[0],bs.inputs['Base Color'])
    mat['纹理依据']='PRTS 对应颜色内部的小块表面纹理 + 程序砂砾；每个面的完整颜色由本面材质决定。'
ns['reference_paint']=str(reference)
ns['scene'].render.filepath=str(Path(__file__).resolve().parent/'正面_小块尺寸纠正.png')
def preview_paint():
    bpy.context.window.scene=ns['scene']
    bpy.ops.render.render(write_still=True)
    return None
bpy.app.timers.register(preview_paint,first_interval=1)
print('已应用同色岩质纹理，不用完整原图掩盖方块结构。')
