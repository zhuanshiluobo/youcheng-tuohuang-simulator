import bpy,json,shutil
from pathlib import Path
folder=Path(__file__).parent/'FixedViewFrameReview'/'AxisLockedFrameReview'
output=Path(__file__).parent/'UnityDelivery'
output.mkdir(exist_ok=True)
scene=bpy.data.scenes['BipolarNanoflake_FixedViewFrameReview']
bpy.context.window.scene=scene
root=scene.objects['BipolarNanoflake_FixedView_Root']
manifest=json.loads((folder/'fixed_view_review_manifest.json').read_text(encoding='utf-8'))
original=bpy.data.materials['BipolarNanoflake_等距方框_统一红铜']
matte=original.copy();matte.name='BipolarNanoflake_等距方框_红铜粗糙面'
shader=next(n for n in matte.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
for key,value in [('Roughness',.58),('Coat Weight',0.0)]:
    for link in list(shader.inputs[key].links):matte.node_tree.links.remove(link)
    shader.inputs[key].default_value=value
material_splits=[]
for obj in root.children_recursive:
    if obj.type!='MESH' or original not in list(obj.data.materials):continue
    obj.data=obj.data.copy()
    mask=obj.data.attributes.get('框面粗糙度遮罩')
    if not mask:continue
    source_slot=list(obj.data.materials).index(original)
    new_slot=len(obj.data.materials);obj.data.materials.append(matte)
    count=0
    for face in obj.data.polygons:
        if face.material_index==source_slot and sum(mask.data[i].value for i in face.loop_indices)/face.loop_total>.5:
            face.material_index=new_slot;count+=1
    material_splits.append({'object':obj.name,'roughFaces':count,'roughness':.58,'baseColorChanged':False})
    next(o for o in manifest['frames'] if o.get('name',o.get('object'))==obj.name)['unityRoughFaceMaterial']=matte.name
manifest['materials'][matte.name]=dict(manifest['materials'][original.name],roughness=.58,roughnessFromSurfaceAttribute=False)
manifest['materials'][original.name]['roughnessFromSurfaceAttribute']=False
manifest['frameMaskTransfer']=material_splits
manifest['sourceBlend']=manifest['blend']
manifest['approvedReviewFbx']=manifest['fbx']
manifest['unityExportFbx']=str(output/'BipolarNanoflake_Finished.fbx')
manifest['status']='用户已人工验收并授权替换；Unity 导入中'
bpy.ops.object.select_all(action='DESELECT')
for obj in [root]+list(root.children_recursive):
    if obj.type in {'EMPTY','MESH'}:obj.select_set(True)
bpy.context.view_layer.objects.active=root
bpy.ops.export_scene.fbx(filepath=manifest['unityExportFbx'],use_selection=True,
    object_types={'EMPTY','MESH'},use_mesh_modifiers=True,add_leaf_bones=False,
    axis_forward='-Z',axis_up='Y',apply_unit_scale=True,bake_space_transform=False,
    mesh_smooth_type='FACE',path_mode='STRIP',bake_anim=False)
(output/'material_manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
print('UNITY_EXPORT',json.dumps(material_splits,ensure_ascii=False))
# 只导出当前进程中的材质槽转换；不保存，不改变验收 .blend 的几何或材质。
