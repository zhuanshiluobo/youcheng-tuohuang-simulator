import bpy, json
from pathlib import Path
from mathutils import Vector
folder=Path(__file__).parent/'FixedViewFrameReview'/'AxisLockedFrameReview'
scene=bpy.data.scenes['BipolarNanoflake_FixedViewFrameReview']
bpy.context.window.scene=scene
root=scene.objects['BipolarNanoflake_FixedView_Root']
camera=scene.objects['独立参考相机.001']
def vector(v): return list(v)
materials={}
objects=[]
for obj in root.children_recursive:
    if obj.type!='MESH': continue
    attrs={a.name: {'domain':a.domain,'type':a.data_type} for a in obj.data.attributes}
    objects.append({'name':obj.name,'parent':obj.parent.name,'materials':[s.material.name for s in obj.material_slots],
        'attributes':attrs,'worldVertices':[vector(obj.matrix_world@v.co) for v in obj.data.vertices[:4]],
        'axis':vector(obj.matrix_world.to_3x3()@Vector((1,0,0))),
        'roughnessMaskValues':sorted(set(round(v.value,6) for v in obj.data.attributes['框面粗糙度遮罩'].data)) if '框面粗糙度遮罩' in obj.data.attributes else []})
    for slot in obj.material_slots:
        mat=slot.material
        if mat.name in materials: continue
        shader=next(n for n in mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
        materials[mat.name]={'baseColor':list(shader.inputs['Base Color'].default_value),
            'metallic':shader.inputs['Metallic'].default_value,'roughness':shader.inputs['Roughness'].default_value,
            'emissionColor':list(shader.inputs['Emission Color'].default_value),
            'emissionStrength':shader.inputs['Emission Strength'].default_value,
            'links':[{'from':l.from_node.type,'fromName':l.from_node.name,'fromSocket':l.from_socket.name,'to':l.to_node.type,'toSocket':l.to_socket.name} for l in mat.node_tree.links],
            'attributes':[{'name':n.name,'attribute':n.attribute_name} for n in mat.node_tree.nodes if n.type=='ATTRIBUTE'],
            'mapRanges':[{'name':n.name,'inputs':{i.name:i.default_value for i in n.inputs if i.type=='VALUE'}} for n in mat.node_tree.nodes if n.type=='MAP_RANGE']}
data={'camera':{'right':vector(camera.matrix_world.to_3x3()@Vector((1,0,0))),
    'up':vector(camera.matrix_world.to_3x3()@Vector((0,1,0))),
    'forward':vector(camera.matrix_world.to_3x3()@Vector((0,0,-1)))},'objects':objects,'materials':materials}
(folder/'approved_export_data.json').write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
print('APPROVED_DATA',json.dumps(data,ensure_ascii=False))
