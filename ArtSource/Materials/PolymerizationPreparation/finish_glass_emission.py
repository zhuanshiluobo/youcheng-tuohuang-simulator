"""聚合剂玻璃与内部发光材质验收；不改已确认几何，不写 Unity 资产。"""
import bpy,json,hashlib,struct
from pathlib import Path
OUT=Path('F:/游城拓荒模拟器/ArtSource/Materials/PolymerizationPreparation/GlassEmissionReview')
bpy.ops.wm.open_mainfile(filepath=str(OUT/'BeforeGlassEmission.blend'))
s=bpy.context.scene
def geometry_digest():
    h=hashlib.sha256()
    for o in sorted((o for o in s.objects if o.type=='MESH'),key=lambda o:o.name):
        h.update(o.name.encode('utf8'))
        for row in o.matrix_world:
            h.update(struct.pack('4f',*row))
        for v in o.data.vertices:h.update(struct.pack('3f',*v.co))
    return h.hexdigest()
before=geometry_digest()
names=['PolymerizationPreparation_青蓝透光盖板','PolymerizationPreparation_青色聚合层板','PolymerizationPreparation_层板亮边']
new={}
for name in names:
    old=bpy.data.materials[name];mat=old.copy();mat.name=name+'_玻璃发光验收'
    for ob in s.objects:
        if ob.type=='MESH':
            for slot in ob.material_slots:
                if slot.material==old:slot.material=mat
    new[name]=mat
def principled(mat):return next(n for n in mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
glass=new[names[0]];p=principled(glass)
p.inputs['Base Color'].default_value=(.82,.955,.98,1)
p.inputs['Alpha'].default_value=1
p.inputs['Metallic'].default_value=0
p.inputs['Roughness'].default_value=.065
p.inputs['Transmission Weight'].default_value=1
p.inputs['IOR'].default_value=1.46
p.inputs['Emission Strength'].default_value=0
glass.diffuse_color=(.70,.91,.95,.24)
# 空心双层玻璃有四次界面穿越，用菲涅耳控制通透层，避免空腔因多次折射发黑。
nodes=glass.node_tree.nodes;links=glass.node_tree.links
output=next(n for n in nodes if n.type=='OUTPUT_MATERIAL')
clear=nodes.new('ShaderNodeBsdfTransparent');clear.label='空腔清晰透视';clear.inputs[0].default_value=(.96,.99,1,1)
fresnel=nodes.new('ShaderNodeFresnel');fresnel.label='玻璃边缘反射';fresnel.inputs['IOR'].default_value=1.46
weight=nodes.new('ShaderNodeMath');weight.operation='MULTIPLY_ADD';weight.inputs[1].default_value=.84;weight.inputs[2].default_value=.075
mix=nodes.new('ShaderNodeMixShader');mix.label='中央通透、边缘保留折射与反光'
links.new(fresnel.outputs[0],weight.inputs[0]);links.new(weight.outputs[0],mix.inputs[0])
links.new(clear.outputs[0],mix.inputs[1]);links.new(p.outputs['BSDF'],mix.inputs[2]);links.new(mix.outputs[0],output.inputs['Surface'])
glass['材质说明']='空心玻璃与圆头共用；IOR1.46的折射/反射与菲涅耳通透层混合，避免多层玻璃空腔发黑；外壳不发光。'
fluid=new[names[1]];p=principled(fluid)
# 保留已确认液体基色，青色内发光只提高亮度，不换色系。
base=list(p.inputs['Base Color'].default_value)
p.inputs['Metallic'].default_value=0
p.inputs['Roughness'].default_value=.18
p.inputs['Transmission Weight'].default_value=.18
p.inputs['IOR'].default_value=1.333
p.inputs['Emission Color'].default_value=(.002,.46,.55,1)
p.inputs['Emission Strength'].default_value=1.15
fluid['材质说明']='保留原青色基色；内部聚合液自发光1.15，轻度透光，液体IOR1.333。'
meniscus=new[names[2]];p=principled(meniscus)
p.inputs['Metallic'].default_value=0
p.inputs['Roughness'].default_value=.12
p.inputs['Emission Color'].default_value=(.008,.60,.69,1)
p.inputs['Emission Strength'].default_value=1.0
s.cycles.max_bounces=10;s.cycles.transmission_bounces=8;s.cycles.transparent_max_bounces=8
s.cycles.film_transparent_glass=True;s.cycles.film_transparent_roughness=.12
s.render.film_transparent=True
s.render.threads_mode='FIXED';s.render.threads=4
s.render.resolution_x=950;s.render.resolution_y=950
s.cycles.samples=64;s.cycles.use_denoising=True
s['本次材质']='青色聚合液内部自发光；玻璃真实折射与高光；管体网格、夹子、盒体和姿态均未修改。仅Blender验收。'
# 保存相同参考角度，开启渲染视图才能正确显示折射和液体投光。
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='VIEW_3D':
            sp=area.spaces.active;sp.shading.type='RENDERED';sp.overlay.show_overlays=False
            sp.region_3d.view_perspective='CAMERA';sp.region_3d.view_camera_zoom=0
s.cycles.preview_samples=24;s.cycles.use_preview_denoising=True
s.render.filepath=str(OUT/'PolymerizationPreparation_GlassEmission.png')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'PolymerizationPreparation_GlassEmission.blend'),compress=True)
report={'geometryUnchanged':before==geometry_digest(),'geometryHash':before,'liquidOriginalBaseColor':base,
        'glass':{'ior':1.46,'transmission':1,'roughness':.065,'emission':0},
        'fluid':{'ior':1.333,'transmission':.18,'emissionColorLinear':[.002,.46,.55],'emissionStrength':1.15},
        'glassClearMix':'Fresnel * 0.84 + 0.075','meniscusEmissionStrength':1.0,'unityImported':False}
(OUT/'material_review.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
print('MATERIAL_REPORT',json.dumps(report,ensure_ascii=False),flush=True)
bpy.ops.render.render(write_still=True)
print('GLASS_EMISSION_DONE',flush=True)
