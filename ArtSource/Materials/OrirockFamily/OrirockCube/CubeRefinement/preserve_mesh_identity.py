import bpy
from pathlib import Path
OUT=Path(__file__).resolve().parent
bpy.ops.wm.open_mainfile(filepath=str(OUT/'OrirockCube_Refined.blend'))
scene=bpy.data.scenes['固源岩_方体精修_游戏贴图']; bpy.context.window.scene=scene
names={'OrirockCube_Body':'OrirockCube_连续崩裂主体_Mesh.001','OrirockCube_Chip':'OrirockCube_左下原位断角_Mesh.001'}
for obj_name,mesh_name in names.items():
    obj=scene.objects[obj_name]
    if obj.data.name!=mesh_name:
        if mesh_name in bpy.data.meshes: bpy.data.meshes[mesh_name].name=mesh_name+'_Previous'
        obj.data.name=mesh_name
for o in scene.objects: o.select_set(o.name in names or o.name=='OrirockCube_Root')
bpy.context.view_layer.objects.active=scene.objects['OrirockCube_Root']
bpy.ops.export_scene.fbx(filepath=str(OUT/'OrirockCube_Refined.fbx'),use_selection=True,object_types={'EMPTY','MESH'},axis_forward='-Z',axis_up='Y',apply_unit_scale=True,use_mesh_modifiers=True,mesh_smooth_type='FACE',add_leaf_bones=False,bake_anim=False,path_mode='STRIP')
bpy.context.window.scene=bpy.data.scenes['固源岩_方体精修_程序源']
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'OrirockCube_Refined.blend'))
print('保留旧版网格数据名与对象名，导出完成。',flush=True)
