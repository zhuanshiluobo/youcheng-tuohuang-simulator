"""将已验收材质场景复制为游戏网格，烘焙表面数据；不回写验收源。"""
import bpy, math, json, hashlib
import numpy as np
from pathlib import Path
from mathutils import Vector, Matrix

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parents[3]
SOURCE = HERE.parent / 'MaterialReview' / 'CrystallineElectronicUnit_MaterialReview.blend'
DEST = PROJECT / 'Assets/YC/Presentation/Models/CrystallineElectronicUnit/Approved20261005'
DEST.mkdir(parents=True, exist_ok=True)
source_hash = hashlib.sha256(SOURCE.read_bytes()).hexdigest()
bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
source_scene = bpy.data.scenes['晶体电子单元_材质验收']
bpy.context.window.scene = source_scene
root = source_scene.objects['晶体电子单元_材质模型']
sources = [o for o in root.children_recursive if o.type in {'MESH', 'FONT'}]
assert len([o for o in sources if o.get('白模对象', '').startswith('层片_')]) == 32
dg = bpy.context.evaluated_depsgraph_get()
scene = bpy.data.scenes.new('晶体电子单元_Unity导出')
materials, objects, manifest_materials = {}, [], {}
specs = json.loads((HERE.parent/'MaterialReview/material_review_notes.json').read_text(encoding='utf-8'))['materials']
key_by_name = {v['name']:k for k,v in specs.items()}
key_by_name['散热槽_内壁暗金属'] = 'vent'

def srgb(c):
    return 12.92*c if c <= .0031308 else 1.055*(c**(1/2.4))-.055

for source in sources:
    mesh = bpy.data.meshes.new_from_object(source.evaluated_get(dg), preserve_all_data_layers=True, depsgraph=dg)
    mesh.transform(source.matrix_world)
    name = source.get('白模对象', source.name)
    ob = bpy.data.objects.new(name, mesh)
    scene.collection.objects.link(ob)
    objects.append(ob)
    for slot in ob.material_slots:
        old = slot.material
        if old not in materials:
            mat = old.copy()
            key = key_by_name.get(old.name)
            if not key: raise RuntimeError('未知材质 '+old.name)
            mat.name = 'CEU_Approved_'+key
            materials[old] = mat
            p = next(n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
            linear = list(p.inputs['Base Color'].default_value)
            manifest_materials[mat.name] = dict(key=key, base_color_srgb=[srgb(c) for c in linear[:3]]+[1.0], metallic=p.inputs['Metallic'].default_value, roughness=p.inputs['Roughness'].default_value, transparent=key in {'crystal','edge'})
        slot.material = materials[old]

bpy.context.window.scene = scene
for o in objects: o.select_set(True)
bpy.context.view_layer.objects.active = objects[0]
# 同一图集，所有层片仍保留独立对象以供 Unity 排序。
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=.003, area_weight=.3)
bpy.ops.object.mode_set(mode='OBJECT')
print('UV_COMPLETE', len(objects), sum(len(o.data.polygons) for o in objects), flush=True)

# 只在复制品合并烘焙，游戏网格的 UV 与副本一致。
bpy.ops.object.duplicate()
bpy.ops.object.join()
bake = bpy.context.object
bake.name = '表面数据_烘焙临时网格'
for o in objects: o.hide_render=True; o.hide_set(True)
image = bpy.data.images.new('CEU_Approved_SurfaceBake', width=2048, height=2048, float_buffer=True)
image.colorspace_settings.name='Non-Color'
original_links = []
for mat in materials.values():
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    p = next(n for n in nodes if n.type=='BSDF_PRINCIPLED')
    output = next(n for n in nodes if n.type=='OUTPUT_MATERIAL')
    original_links.append((mat, p, output))
    tex = nodes.new('ShaderNodeTexImage'); tex.image=image; nodes.active=tex
    combine = nodes.new('ShaderNodeCombineColor'); combine.mode='RGB'
    combine.inputs['Red'].default_value=p.inputs['Metallic'].default_value
    if manifest_materials[mat.name]['transparent']:
        combine.inputs['Green'].default_value=1.0
    else:
        ao=nodes.new('ShaderNodeAmbientOcclusion'); ao.inputs['Distance'].default_value=.18; ao.samples=16
        links.new(ao.outputs['AO'],combine.inputs['Green'])
    inv=nodes.new('ShaderNodeMath'); inv.operation='SUBTRACT'; inv.inputs[0].default_value=1
    rough=p.inputs['Roughness']
    if rough.is_linked: links.new(rough.links[0].from_socket, inv.inputs[1])
    else: inv.inputs[1].default_value=rough.default_value
    links.new(inv.outputs[0],combine.inputs['Blue'])
    emission=nodes.new('ShaderNodeEmission')
    links.new(combine.outputs[0],emission.inputs['Color'])
    links.new(emission.outputs[0],output.inputs['Surface'])
scene.render.engine='CYCLES'
scene.cycles.samples=16
scene.render.bake.margin=10
scene.render.bake.use_clear=True
bpy.ops.object.bake(type='EMIT')
pixels=np.empty(2048*2048*4, dtype=np.float32)
image.pixels.foreach_get(pixels)
pixels=pixels.reshape((-1,4))
covered=pixels[:,3]>.5
stats={str(i):[float(pixels[covered,i].min()),float(pixels[covered,i].max()),float(pixels[covered,i].mean())] for i in range(3)}
pixels[:,3]=pixels[:,2]
pixels[:,2]=0
image.pixels.foreach_set(pixels.reshape(-1))
image.file_format='PNG'
scene.render.image_settings.file_format='PNG'
scene.render.image_settings.color_mode='RGBA'
scene.render.image_settings.color_depth='8'
scene.view_settings.view_transform='Raw'
scene.view_settings.look='None'
scene.view_settings.exposure=0; scene.view_settings.gamma=1
image.save_render(str(DEST/'CEU_Approved_Surface.png'),scene=scene)
print('BAKE_COMPLETE',stats,flush=True)
for mat,p,out in original_links: mat.node_tree.links.new(p.outputs[0],out.inputs['Surface'])
bake.hide_render=True; bake.hide_set(True); bake.select_set(False)
for o in objects: o.hide_render=False; o.hide_set(False); o.select_set(False)

# 金属及标识合并为单对象多材质；32 层透明片保留各自排序中心。
opaque=[o for o in objects if not o.name.startswith('层片_')]
layers=[o for o in objects if o.name.startswith('层片_')]
for o in opaque:o.select_set(True)
bpy.context.view_layer.objects.active=opaque[0]
bpy.ops.object.join()
body=bpy.context.object; body.name='CEU_Approved_Housing'
export_objects=[body]+layers
coords=[o.matrix_world@v.co for o in export_objects for v in o.data.vertices]
low=Vector([min(v[i] for v in coords) for i in range(3)])
high=Vector([max(v[i] for v in coords) for i in range(3)])
center=(low+high)*.5
game_root=bpy.data.objects.new('CEU_Approved',None); scene.collection.objects.link(game_root)
for i,o in enumerate(sorted(layers,key=lambda o:o.name)):
    o.name='CEU_Approved_Layer_%02d'%(i+1)
for o in export_objects:
    o.data.transform(Matrix.Translation(-center))
    # 物体原点放在自身中心，透明排序依赖真实层片中心。
    origin=sum((v.co for v in o.data.vertices),Vector())/len(o.data.vertices)
    o.data.transform(Matrix.Translation(-origin)); o.location=origin
    o.parent=game_root
    o.select_set(True)
game_root.select_set(True); bpy.context.view_layer.objects.active=game_root
scene.unit_settings.system='METRIC'; scene.unit_settings.scale_length=1.0
bpy.ops.export_scene.fbx(filepath=str(DEST/'CrystallineElectronicUnit_Approved.fbx'),use_selection=True,object_types={'EMPTY','MESH'},axis_forward='-Z',axis_up='Y',apply_unit_scale=True,use_mesh_modifiers=True,mesh_smooth_type='FACE',add_leaf_bones=False,bake_anim=False,path_mode='STRIP')
manifest=dict(source=str(SOURCE),source_sha256=source_hash,scene=source_scene.name,source_objects=len(sources),export_meshes=len(export_objects),layers=len(layers),triangles=sum(sum(len(p.vertices)-2 for p in o.data.polygons) for o in export_objects),bounds_min=list(low),bounds_max=list(high),source_center=list(center),size=list(high-low),materials=manifest_materials,surface_map='R=metallic,G=AO,B=unused,A=smoothness; linear data',bake_stats=stats)
(HERE/'export_manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
assert hashlib.sha256(SOURCE.read_bytes()).hexdigest()==source_hash
print('EXPORT_COMPLETE',json.dumps(manifest,ensure_ascii=False),flush=True)
