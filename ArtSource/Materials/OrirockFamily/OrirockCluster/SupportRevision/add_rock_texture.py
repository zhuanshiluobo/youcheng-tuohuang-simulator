"""结构确定后加入表面风化、细颗粒和细裂纹；不改变立方体几何。"""
from pathlib import Path
import bpy
ns=bpy.app.driver_namespace['cluster_square_revision']
source=Path(__file__).with_name('apply_surface.py')
exec(compile(source.read_text(encoding='utf-8-sig'),str(source),'exec'),dict(__file__=str(source),__name__='__main__'))
ns['scene'].view_layers[0].material_override=None
for i,mat in enumerate(ns['materials']):
    nodes,links=mat.node_tree.nodes,mat.node_tree.links
    bs=next(n for n in nodes if n.type=='BSDF_PRINCIPLED')
    base=bs.inputs['Base Color'].links[0].from_socket
    coarse=next(n for n in nodes if n.type=='TEX_NOISE' and abs(n.inputs['Scale'].default_value-18)<.01)
    cr=next(l.to_node for l in coarse.outputs['Fac'].links if l.to_node.type=='VALTORGB')
    for stop in cr.color_ramp.elements:
        rgb=stop.color[:3]
        if stop.position<.5: stop.color=tuple(c*.7 for c in rgb)+(1,)
    tc=next(n for n in nodes if n.type=='TEX_COORD')
    stain=nodes.new('ShaderNodeTexNoise');stain.label='局部矿质风化，不增加几何折面'
    stain.inputs['Scale'].default_value=9;stain.inputs['Detail'].default_value=4.5
    stain.inputs['Roughness'].default_value=.72;stain.inputs['Distortion'].default_value=.23
    links.new(tc.outputs['Object'],stain.inputs['Vector'])
    factor=nodes.new('ShaderNodeValToRGB')
    factor.color_ramp.elements[0].position=.47;factor.color_ramp.elements[0].color=(0,0,0,1)
    factor.color_ramp.elements[1].position=.72;factor.color_ramp.elements[1].color=(.42,.42,.42,1)
    links.new(stain.outputs['Fac'],factor.inputs[0])
    mix=nodes.new('ShaderNodeMixRGB');mix.blend_type='MIX'
    rgb=(79,75,62) if i in [2,6,7] else (108,91,55)
    mix.inputs[2].default_value=tuple(ns['lin'](v/255) for v in rgb)+(1,)
    links.new(factor.outputs[0],mix.inputs[0]);links.new(base,mix.inputs[1])
    vein=nodes.new('ShaderNodeTexVoronoi');vein.feature='DISTANCE_TO_EDGE'
    vein.inputs['Scale'].default_value=24;links.new(tc.outputs['Object'],vein.inputs['Vector'])
    vr=nodes.new('ShaderNodeValToRGB');vr.color_ramp.elements[0].position=.0018
    vr.color_ramp.elements[0].color=(.28,.28,.28,1)
    vr.color_ramp.elements[1].position=.012;vr.color_ramp.elements[1].color=(1,1,1,1)
    links.new(vein.outputs['Distance'],vr.inputs[0])
    final=nodes.new('ShaderNodeMixRGB');final.blend_type='MULTIPLY';final.inputs[0].default_value=.36
    links.new(mix.outputs[0],final.inputs[1]);links.new(vr.outputs[0],final.inputs[2])
    links.new(final.outputs[0],bs.inputs['Base Color'])
    mat['表面说明']='风化斑驳、颗粒与细裂纹仅作用于表面；每个完整方块的面颜色独立，未投射整张图标。'
for area in bpy.context.screen.areas:
    if area.type=='VIEW_3D': area.spaces.active.shading.type='MATERIAL'
ns['scene'].render.filepath=str(Path(__file__).with_name('材质_正面草稿.png'))
print('结构之后的岩质纹理已加入；尚未启动烘焙。')
