"""烘焙固源岩修订贴图，保存可编辑源与游戏预览，选择性导出FBX。"""
import bpy
import bmesh
import json
import numpy as np
from pathlib import Path
from mathutils import Vector

OUT=Path(__file__).resolve().parent
SIZE=1024
TEX=OUT/'textures'
TEX.mkdir(exist_ok=True)

def status(stage):
    (OUT/'finish_status.json').write_text(json.dumps({'阶段':stage},ensure_ascii=False,indent=2),encoding='utf-8')
    print('STAGE',stage,flush=True)

def bounds(meshes):
    bpy.context.view_layer.update()
    pts=[o.matrix_world @ v.co for o in meshes for v in o.data.vertices]
    lo=Vector([min(p[i] for p in pts) for i in range(3)])
    hi=Vector([max(p[i] for p in pts) for i in range(3)])
    return {'min':list(lo),'max':list(hi),'size':list(hi-lo),'center':list((hi+lo)*.5)}

status('读取旧成品的尺寸，仅用于比较')
bpy.ops.wm.open_mainfile(filepath=str(OUT.parent/'OrirockCube_Finished.blend'))
old_bounds=bounds([o for o in bpy.context.scene.objects if o.type=='MESH'])
bpy.ops.wm.open_mainfile(filepath=str(OUT/'OrirockCube_FrontDraft_v03.blend'))
scene=bpy.context.scene
meshes=[o for o in scene.objects if o.type=='MESH']
root=next(o for o in scene.objects if o.type=='EMPTY')
draft_bounds=bounds(meshes)
factor=1.35/max(draft_bounds['size'])
for obj in meshes:
    for v in obj.data.vertices:
        v.co*=factor
    obj.data.update()
scene.camera.data.ortho_scale*=factor
materials=list({m for obj in meshes for m in obj.data.materials})
for mat in materials:
    for node in mat.node_tree.nodes:
        if node.type in {'TEX_NOISE','TEX_VORONOI'}:
            node.inputs['Scale'].default_value/=factor
        if node.type=='BUMP':
            node.inputs['Distance'].default_value*=factor
new_bounds=bounds(meshes)
scene['实体尺寸缩放'] = factor
scene['最大实体尺寸_米'] = 1.35
root['展示说明'] = '网格最大边1.35m；根缩放1。收藏室最终缩放由主代理结合实际预制体决定。'
scene.render.engine='CYCLES'
scene.cycles.device='CPU'
scene.cycles.samples=8
scene.render.threads_mode='FIXED'
scene.render.threads=4
scene.render.bake.use_clear=True
scene.render.bake.margin=12
scene.render.bake.use_selected_to_active=False
for obj in scene.objects:
    obj.select_set(obj in meshes)
bpy.context.view_layer.objects.active=meshes[0]
targets={}
for mat in materials:
    node=mat.node_tree.nodes.new('ShaderNodeTexImage')
    node.label='1024游戏图集烘焙目标（原程序材质保留）'
    targets[mat]=node
images={}
texture_paths={}

def bake(name,kind,space):
    status('烘焙 '+name)
    im=bpy.data.images.new('OrirockCube_'+name,width=SIZE,height=SIZE,alpha=True)
    im.colorspace_settings.name=space
    im.generated_color=(0,0,0,1)
    for mat,node in targets.items():
        node.image=im
        mat.node_tree.nodes.active=node
    bpy.ops.object.bake(type=kind,margin=12)
    im.filepath_raw=str(TEX/f'OrirockCube_{name}.png')
    im.file_format='PNG'
    im.save()
    images[name]=im
    texture_paths[name]=im.filepath_raw
    return im

emissions=[]
for mat in materials:
    nodes,links=mat.node_tree.nodes,mat.node_tree.links
    bsdf=next(n for n in nodes if n.type=='BSDF_PRINCIPLED')
    output=next(n for n in nodes if n.type=='OUTPUT_MATERIAL')
    emit=nodes.new('ShaderNodeEmission')
    links.new(bsdf.inputs['Base Color'].links[0].from_socket,emit.inputs['Color'])
    links.new(emit.outputs[0],output.inputs['Surface'])
    emissions.append((mat,emit,bsdf,output))
bake('BaseColor','EMIT','sRGB')
for mat,emit,bsdf,output in emissions:
    mat.node_tree.links.new(bsdf.outputs[0],output.inputs['Surface'])
    mat.node_tree.nodes.remove(emit)
bake('Normal','NORMAL','Non-Color')
bake('AO','AO','Non-Color')
status('烘焙光滑度并打包 R=金属度 A=光滑度')
rough=bpy.data.images.new('OrirockCube_内部临时粗糙度',width=SIZE,height=SIZE,alpha=True)
rough.colorspace_settings.name='Non-Color'
for mat,node in targets.items():
    node.image=rough
    mat.node_tree.nodes.active=node
bpy.ops.object.bake(type='ROUGHNESS',margin=12)
values=np.empty(SIZE*SIZE*4,dtype=np.float32)
rough.pixels.foreach_get(values)
pixels=np.zeros((SIZE*SIZE,4),dtype=np.float32)
pixels[:,3]=1-np.clip(values.reshape((-1,4))[:,0],0,1)
ms=bpy.data.images.new('OrirockCube_MetallicSmoothness',width=SIZE,height=SIZE,alpha=True)
ms.colorspace_settings.name='Non-Color'
ms.pixels.foreach_set(pixels.reshape(-1))
ms.filepath_raw=str(TEX/'OrirockCube_MetallicSmoothness.png')
ms.file_format='PNG'
ms.save()
images['MetallicSmoothness']=ms
texture_paths['MetallicSmoothness']=ms.filepath_raw
for mat,node in targets.items():
    node.image=images['BaseColor']
for im in images.values():
    im.pack()

game=bpy.data.materials.new('OrirockCube_游戏_PBR图集')
game.use_nodes=True
nodes,links=game.node_tree.nodes,game.node_tree.links
bsdf=next(n for n in nodes if n.type=='BSDF_PRINCIPLED')
bsdf.inputs['Specular IOR Level'].default_value=.22
base=nodes.new('ShaderNodeTexImage'); base.image=images['BaseColor']; base.label='BaseColor sRGB'
ao=nodes.new('ShaderNodeTexImage'); ao.image=images['AO']; ao.label='AO 非颜色：Unity Occlusion 独立通道'
# FBX 基础色直接绑定图集。预览使用实际光线遮蔽，AO独立交付给Unity。
links.new(base.outputs['Color'],bsdf.inputs['Base Color'])
normal=nodes.new('ShaderNodeTexImage'); normal.image=images['Normal']; normal.label='Normal 非颜色 Tangent +Y'
nm=nodes.new('ShaderNodeNormalMap'); nm.space='TANGENT'
links.new(normal.outputs['Color'],nm.inputs['Color']); links.new(nm.outputs[0],bsdf.inputs['Normal'])
mask=nodes.new('ShaderNodeTexImage'); mask.image=images['MetallicSmoothness']; mask.label='R 金属度 / A 光滑度'
sep=nodes.new('ShaderNodeSeparateColor'); links.new(mask.outputs['Color'],sep.inputs[0]); links.new(sep.outputs['Red'],bsdf.inputs['Metallic'])
roughness=nodes.new('ShaderNodeMath'); roughness.operation='SUBTRACT'; roughness.inputs[0].default_value=1
links.new(mask.outputs['Alpha'],roughness.inputs[1]); links.new(roughness.outputs[0],bsdf.inputs['Roughness'])
game['通道约定']='BaseColor=sRGB；Normal/AO/MetallicSmoothness=Non-Color；R金属度A光滑度；岩质金属度0。'
game.use_fake_user=True

status('创建独立游戏贴图预览场景并导出两个闭合网格')
root.name='OrirockCube_EditableRoot'
game_scene=bpy.data.scenes.new('固源岩_正面修订_游戏贴图预览')
game_scene.unit_settings.system='METRIC'
game_scene.unit_settings.scale_length=1
game_scene.world=scene.world
game_scene.collection.children.link(bpy.data.collections['预览设备_不导出'])
col=bpy.data.collections.new('固源岩_游戏模型_导出')
game_scene.collection.children.link(col)
game_root=bpy.data.objects.new('OrirockCube_Root',None)
col.objects.link(game_root)
for k in root.keys():
    game_root[k]=root[k]
game_meshes=[]
for index,obj in enumerate(meshes):
    copy=obj.copy()
    copy.data=obj.data.copy()
    copy.name='OrirockCube_Body' if '主体' in obj.name else 'OrirockCube_Chip'
    col.objects.link(copy)
    copy.parent=game_root
    copy.data.materials.clear()
    copy.data.materials.append(game)
    for p in copy.data.polygons:
        p.material_index=0
    game_meshes.append(copy)
game_scene.camera=scene.camera
game_scene.render.engine='CYCLES'
game_scene.cycles.device='CPU'
game_scene.cycles.samples=32
game_scene.cycles.use_denoising=True
game_scene.render.threads_mode='FIXED'
game_scene.render.threads=4
game_scene.render.resolution_x=game_scene.render.resolution_y=1000
game_scene.render.resolution_percentage=100
game_scene.render.film_transparent=True
game_scene.render.image_settings.file_format='PNG'
game_scene.render.image_settings.color_mode='RGBA'
game_scene.view_settings.view_transform='Standard'
game_scene.view_settings.look='None'
game_scene.view_settings.exposure=0
bpy.context.window.scene=game_scene
for obj in game_scene.objects:
    obj.select_set(obj==game_root or obj in game_meshes)
bpy.context.view_layer.objects.active=game_root
fbx=OUT/'OrirockCube_FrontRevision.fbx'
bpy.ops.export_scene.fbx(filepath=str(fbx),use_selection=True,object_types={'EMPTY','MESH'},
    axis_forward='-Z',axis_up='Y',apply_unit_scale=True,use_mesh_modifiers=True,
    mesh_smooth_type='FACE',add_leaf_bones=False,bake_anim=False,path_mode='STRIP')
front=OUT/'OrirockCube_FrontRevision_Front.png'
game_scene.render.filepath=str(front)
status('渲染最终贴图材质正面')
bpy.ops.render.render(write_still=True)
cam=game_scene.camera
front_location=cam.location.copy()
front_rotation=cam.rotation_euler.copy()
cam.location=(3.8,6.2,4.0)
cam.rotation_euler=(-cam.location).to_track_quat('-Z','Y').to_euler()
back=OUT/'OrirockCube_FrontRevision_Back.png'
game_scene.render.filepath=str(back)
status('渲染背面实体体积')
bpy.ops.render.render(write_still=True)
cam.location=front_location
cam.rotation_euler=front_rotation
game_scene.render.filepath=str(front)

metrics=[]
for obj in game_meshes:
    obj.data.calc_loop_triangles()
    bm=bmesh.new(); bm.from_mesh(obj.data)
    metrics.append({'name':obj.name,'vertices':len(obj.data.vertices),'triangles':len(obj.data.loop_triangles),
                    'non_manifold_edges':sum(not e.is_manifold for e in bm.edges)})
    bm.free()
# 保存时程序源场景仍是继续编辑的入口，游戏贴图预览为第二个场景。
bpy.context.window.scene=scene
scene.render.resolution_x=scene.render.resolution_y=1000
scene.cycles.samples=32
scene.render.filepath=str(OUT/'OrirockCube_ProceduralFront.png')
blend=OUT/'OrirockCube_FrontRevision.blend'
bpy.ops.wm.save_as_mainfile(filepath=str(blend))
info={
    'material':'OrirockCube','中文名称':'固源岩','blender_version':bpy.app.version_string,
    'blend':str(blend),'fbx':str(fbx),'textures':texture_paths,
    'renders':{'front':str(front),'back':str(back),'comparison':str(OUT/'comparison_OrirockCube_FrontRevision_Front.png')},
    'reference':str(OUT.parent.parent/'reference/OrirockCube_PRTS.png'),
    'front_camera':{'position':list(front_location),'direction':list(-front_location.normalized()),
        'rotation_euler_radians':list(front_rotation),'target':[0,0,0],'type':'ORTHO','ortho_scale':cam.data.ortho_scale},
    'mesh_count':len(game_meshes),'triangle_count':sum(x['triangles'] for x in metrics),'meshes':metrics,
    'unit_system':'METRIC','unit_scale':1,'root_scale':list(game_root.scale),
    'axis_forward':'-Z','axis_up':'Y','exported_object_types':['EMPTY','MESH'],
    'bounds_blender_meters':new_bounds,'old_source_bounds_blender_meters':old_bounds,
    'scale_applied_to_draft_geometry':factor,
    'maximum_dimension_ratio_to_old':max(new_bounds['size'])/max(old_bounds['size']),
    'texture_size':[SIZE,SIZE],
    'texture_color_spaces':{'BaseColor':'sRGB','Normal':'Non-Color','AO':'Non-Color','MetallicSmoothness':'Non-Color'},
    'metallic_smoothness_channels':{'R':'Metallic = 0','A':'Smoothness = 1 - Roughness'},
    'editable_scene':scene.name,'baked_preview_scene':game_scene.name,
    'visual_review':[
        '已查看v01实际渲染，对照参考发现顶面突兀三角折线、颗粒偏弱、浅黄侧面偏暗。',
        'v02固定参考轮廓后松弛顶面内部几何，增强连续颗粒纹理，提亮右侧。已查看实际渲染。',
        '对照v02贴图预览发现前断面偏平；v03增加指定的斜向连续前断层及褐色下降折面，不添加散块。',
    ],
    'uncertain':['背面轮廓与厚度按单视图推定。','未进入Unity；收藏室最终显示大小仍需主代理结合现用预制体确认。'],
    'scope':'仅OrirockCube/FrontRevision；旧成品与白模未覆盖；无Unity/Excel/UI/其他材料/git提交操作。',
}
(OUT/'revision_info.json').write_text(json.dumps(info,ensure_ascii=False,indent=2),encoding='utf-8')
status('已导出，等待实际贴图渲染视觉检查')
print('DELIVERY_READY',json.dumps(info,ensure_ascii=False),flush=True)
