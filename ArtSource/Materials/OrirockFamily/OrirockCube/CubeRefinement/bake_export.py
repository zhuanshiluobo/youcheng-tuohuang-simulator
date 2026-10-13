"""为固源岩独立精修场景烘焙 2K 游戏图集，保持原导出节点名。"""
import bpy, bmesh, json, numpy as np
from pathlib import Path

OUT=Path(__file__).resolve().parent
TEX=OUT/'textures'; TEX.mkdir(exist_ok=True)
SIZE=2048
bpy.ops.wm.open_mainfile(filepath=str(OUT/'OrirockCube_Refined.blend'))
scene=bpy.data.scenes['固源岩_方体精修_程序源']; bpy.context.window.scene=scene
meshes=[o for o in scene.objects if o.type=='MESH']
mats=list({m for o in meshes for m in o.data.materials})
scene.cycles.samples=8; scene.render.bake.margin=16
scene.render.bake.use_clear=True; scene.render.bake.use_selected_to_active=False
for o in scene.objects: o.select_set(o in meshes)
bpy.context.view_layer.objects.active=meshes[0]
targets={}
for m in mats:
    targets[m]=m.node_tree.nodes.new('ShaderNodeTexImage')
    targets[m].label='2048 游戏图集；程序材质保留'
images={}
def bake(name,kind,space):
    print('BAKE_START',name,flush=True)
    im=bpy.data.images.new('Refined_'+name,width=SIZE,height=SIZE,alpha=True)
    im.colorspace_settings.name=space; im.generated_color=(0,0,0,1)
    for m,t in targets.items(): t.image=im; m.node_tree.nodes.active=t
    bpy.ops.object.bake(type=kind,margin=16)
    im.filepath_raw=str(TEX/('OrirockCube_'+name+'.png')); im.file_format='PNG'; im.save()
    images[name]=im
    print('BAKE_SAVED',im.filepath_raw,flush=True)
    return im

for m in mats:
    n,l=m.node_tree.nodes,m.node_tree.links
    bs=next(n0 for n0 in n if n0.type=='BSDF_PRINCIPLED')
    output=next(n0 for n0 in n if n0.type=='OUTPUT_MATERIAL')
    em=n.new('ShaderNodeEmission'); em.label='烘焙基础色'
    l.new(bs.inputs['Base Color'].links[0].from_socket,em.inputs['Color']); l.new(em.outputs[0],output.inputs['Surface'])
bake('BaseColor','EMIT','sRGB')
for m in mats:
    n,l=m.node_tree.nodes,m.node_tree.links
    l.new(next(n0 for n0 in n if n0.type=='BSDF_PRINCIPLED').outputs[0],next(n0 for n0 in n if n0.type=='OUTPUT_MATERIAL').inputs['Surface'])
    for no in list(n):
        if no.type=='EMISSION': n.remove(no)
bake('Normal','NORMAL','Non-Color')
scene.cycles.samples=16
bake('Occlusion','AO','Non-Color')
rough=bake('Roughness','ROUGHNESS','Non-Color')
values=np.empty(SIZE*SIZE*4,dtype=np.float32); rough.pixels.foreach_get(values)
pixels=np.zeros((SIZE*SIZE,4),dtype=np.float32); pixels[:,3]=1-np.clip(values.reshape((-1,4))[:,0],0,1)
im=bpy.data.images.new('Refined_MetallicSmoothness',width=SIZE,height=SIZE,alpha=True)
im.colorspace_settings.name='Non-Color'; im.pixels.foreach_set(pixels.ravel())
im.filepath_raw=str(TEX/'OrirockCube_MetallicSmoothness.png'); im.file_format='PNG'; im.save()
images['MetallicSmoothness']=im
for im in images.values(): im.pack()
for m,t in targets.items(): t.image=images['BaseColor']

game=bpy.data.materials.new('OrirockCube_RefinedBaked'); game.use_nodes=True
n,l=game.node_tree.nodes,game.node_tree.links
bs=next(a for a in n if a.type=='BSDF_PRINCIPLED'); bs.inputs['Specular IOR Level'].default_value=.18
texnodes={}
for key,im in images.items():
    no=n.new('ShaderNodeTexImage'); no.image=im; no.label=key; texnodes[key]=no
l.new(texnodes['BaseColor'].outputs['Color'],bs.inputs['Base Color'])
normal=n.new('ShaderNodeNormalMap'); l.new(texnodes['Normal'].outputs['Color'],normal.inputs['Color'])
l.new(normal.outputs[0],bs.inputs['Normal']); l.new(texnodes['Roughness'].outputs['Color'],bs.inputs['Roughness'])
game['说明']='基础色为sRGB；法线/遮蔽/金属光滑度为非颜色数据；MS的R=0、A=1-粗糙度。'

# 保留历史场景，同时保证新 FBX 的对象名与 Unity 已有 Mesh 引用一致。
for name in ['OrirockCube_Root','OrirockCube_Body','OrirockCube_Chip']:
    if name in bpy.data.objects: bpy.data.objects[name].name=name+'_Previous'
preview=bpy.data.scenes.new('固源岩_方体精修_游戏贴图')
preview.unit_settings.system='METRIC'; preview.unit_settings.scale_length=1; preview.world=scene.world
preview.collection.children.link(bpy.data.collections['固源岩_精修预览设备_不导出'])
col=bpy.data.collections.new('固源岩_精修导出'); preview.collection.children.link(col)
root=bpy.data.objects.new('OrirockCube_Root',None); col.objects.link(root)
exports=[]
for o in meshes:
    copy=o.copy(); copy.data=o.data.copy(); col.objects.link(copy); copy.parent=root
    copy.name='OrirockCube_Body' if '主体' in o.name else 'OrirockCube_Chip'
    legacy_name='OrirockCube_连续崩裂主体_Mesh.001' if copy.name=='OrirockCube_Body' else 'OrirockCube_左下原位断角_Mesh.001'
    if legacy_name in bpy.data.meshes: bpy.data.meshes[legacy_name].name=legacy_name+'_Previous'
    copy.data.name=legacy_name; copy.data.materials.clear(); copy.data.materials.append(game)
    for p in copy.data.polygons: p.material_index=0
    exports.append(copy)
preview.camera=scene.camera; preview.render.engine=scene.render.engine
preview.cycles.device='CPU'; preview.cycles.samples=40; preview.cycles.use_denoising=True
preview.render.threads_mode='FIXED'; preview.render.threads=10
preview.render.resolution_x=preview.render.resolution_y=1000; preview.render.resolution_percentage=100
preview.render.film_transparent=True; preview.render.image_settings.file_format='PNG'; preview.render.image_settings.color_mode='RGBA'
preview.view_settings.view_transform=scene.view_settings.view_transform; preview.view_settings.look=scene.view_settings.look
bpy.context.window.scene=preview
for o in preview.objects: o.select_set(o==root or o in exports)
bpy.context.view_layer.objects.active=root
bpy.ops.export_scene.fbx(filepath=str(OUT/'OrirockCube_Refined.fbx'),use_selection=True,object_types={'EMPTY','MESH'},
    axis_forward='-Z',axis_up='Y',apply_unit_scale=True,use_mesh_modifiers=True,
    mesh_smooth_type='FACE',add_leaf_bones=False,bake_anim=False,path_mode='STRIP')
for name,position in [('front',(3.8,-6.2,3.7)),('back',(-3.8,6.2,3.7)),('side',(6,1,2.0))]:
    preview.camera.location=position; preview.camera.rotation_euler=(-preview.camera.location).to_track_quat('-Z','Y').to_euler()
    preview.render.filepath=str(OUT/('preview_'+name+'.png'))
    print('RENDER',name,flush=True); bpy.ops.render.render(write_still=True)
preview.camera.location=(3.8,-6.2,3.7); preview.camera.rotation_euler=(-preview.camera.location).to_track_quat('-Z','Y').to_euler()
bpy.context.window.scene=scene; scene.cycles.samples=40
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'OrirockCube_Refined.blend'))
report={'可编辑源':str(OUT/'OrirockCube_Refined.blend'),'游戏FBX':str(OUT/'OrirockCube_Refined.fbx'),
    '主体三轴尺寸':list(next(o for o in exports if o.name=='OrirockCube_Body').dimensions),'贴图尺寸':[SIZE,SIZE],
    '导出对象':[o.name for o in [root]+exports],'预览':['preview_front.png','preview_back.png','preview_side.png'],
    '备注':'旧源及旧场景保留；背面为推定；不包含背景和底板。'}
(OUT/'delivery.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print('DELIVERY_READY',flush=True)
