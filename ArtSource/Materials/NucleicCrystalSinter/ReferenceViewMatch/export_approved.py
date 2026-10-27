"""导出用户已验收的活动场景；不改动验收 Blender 文件。"""
import bpy, json, re
from pathlib import Path
from mathutils import Vector
OUT=Path('F:/游城拓荒模拟器/ArtSource/Materials/NucleicCrystalSinter/ReferenceViewMatch')
bpy.ops.wm.open_mainfile(filepath=str(OUT/'NucleicCrystalSinter_SameView.blend'))
s=bpy.context.scene
meshes=[o for o in s.objects if o.type=='MESH' and o.parent is not None]
root=next(o for o in s.objects if o.type=='EMPTY' and o.parent is None)
materials=set(m for o in meshes for m in o.data.materials if m)
aliases={'铭牌象牙白印字':'NCS_LabelInk','型号面板哑光紫灰':'NCS_LabelPlate','顶盖凹阶浅暖金属':'NCS_WarmStep','右上晶体深紫折射面':'NCS_WedgeViolet','装置下沿金色标记':'NCS_GoldMark'}
manifest={}
def srgb(c):return 12.92*c if c<=.0031308 else 1.055*c**(1/2.4)-.055
for m in sorted(materials,key=lambda m:m.name):
    name=re.sub(r'\.\d{3}$','',m.name)
    name=aliases.get(name,name)
    existing=bpy.data.materials.get(name)
    if existing and existing!=m:existing.name=name+'_Archive'
    m.name=name
    p=next(n for n in m.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    transmission=p.inputs['Transmission Weight'].default_value
    emission=p.inputs['Emission Color'].default_value
    strength=p.inputs['Emission Strength'].default_value
    manifest[name]={'linear_color':list(p.inputs['Base Color'].default_value),'metallic':p.inputs['Metallic'].default_value,'smoothness':1-p.inputs['Roughness'].default_value,'transmission':transmission,'ior':p.inputs['IOR'].default_value,'emission':[v*strength for v in emission[:3]]}
    manifest[name]['color']=[srgb(v) for v in p.inputs['Base Color'].default_value[:3]]+[1]
    manifest[name]['transparent']=transmission>.1
cam=s.camera
q=cam.matrix_world.to_quaternion()
def unity(v):return [-v.x,v.z,-v.y]
orientation={'forward':unity(q@Vector((0,0,-1))),'up':unity(q@Vector((0,1,0))),'blender_position':list(cam.location),'blender_lens':cam.data.lens,'blender_shift':[cam.data.shift_x,cam.data.shift_y]}
bpy.ops.object.select_all(action='DESELECT');root.select_set(True)
for o in meshes:o.select_set(True)
bpy.context.view_layer.objects.active=root
bpy.ops.export_scene.fbx(filepath=str(OUT/'NucleicCrystalSinter_Approved.fbx'),use_selection=True,object_types={'EMPTY','MESH'},apply_unit_scale=True,apply_scale_options='FBX_SCALE_UNITS',axis_forward='-Z',axis_up='Y',use_mesh_modifiers=True,mesh_smooth_type='FACE',add_leaf_bones=False,bake_anim=False)
data={'model':'NucleicCrystalSinter','source':'ArtSource/Materials/NucleicCrystalSinter/ReferenceViewMatch/NucleicCrystalSinter_SameView.blend','revision':'用户验收通过的同视角版','materials':manifest,'reference_orientation':orientation,'mesh_count':len(meshes)}
(OUT/'approved_manifest.json').write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
print('APPROVED_EXPORT',len(meshes),list(manifest))
