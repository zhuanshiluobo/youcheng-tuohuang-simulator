"""仅在根代理看过多向预览后使用，导出圆形包络版并追加正式源场景。"""
import bpy
import json
import shutil
from pathlib import Path

revision = Path(__file__).resolve().parent
source = revision.parent
asset = source.parents[2] / 'Assets' / 'YC' / 'Presentation' / 'Models' / 'D32Steel'
preview_source = revision / 'D32Steel_SphereRoundRevision.blend'
formal = source / 'D32Steel_Finished.blend'
backup = revision / 'BeforeRoundRevision_FormalStages.blend'
if not backup.exists():
    shutil.copy2(formal, backup)

finished = bpy.data.scenes.get('D32钢_圆形包络返工')
if finished is None:
    raise RuntimeError('当前不是已审阅的新球形源')
bpy.context.window.scene = finished
meshes = [obj for obj in finished.objects if obj.type == 'MESH']
model_root = next(obj for obj in finished.objects if obj.type == 'EMPTY' and obj.name.startswith('D32Steel_SphereRoot'))
for obj in finished.objects:
    obj.select_set(obj in meshes or obj == model_root)
bpy.context.view_layer.objects.active = model_root
fbx = revision / 'D32Steel_SphereRoundRevision.fbx'
bpy.ops.export_scene.fbx(
    filepath=str(fbx), use_selection=True, object_types={'MESH', 'EMPTY'},
    apply_unit_scale=True, axis_forward='-Z', axis_up='Y', bake_anim=False,
    use_mesh_modifiers=True, use_triangles=True, add_leaf_bones=False,
    mesh_smooth_type='FACE',
)

bpy.ops.wm.open_mainfile(filepath=str(formal))
previous_scenes = [sc.name for sc in bpy.data.scenes]
required = ['D32钢_圆形包络返工', 'D32钢_圆形包络独立白模']
missing = [name for name in required if bpy.data.scenes.get(name) is None]
if missing:
    with bpy.data.libraries.load(str(preview_source), link=False) as (available, imported):
        if any(name not in available.scenes for name in missing):
            raise RuntimeError('已审阅源场景缺失')
        imported.scenes = missing
finished = bpy.data.scenes.get(required[0])
white = bpy.data.scenes.get(required[1])
if finished is None or white is None:
    raise RuntimeError('正式源场景追加失败')
finished.use_fake_user = True
white.use_fake_user = True
bpy.context.window.scene = finished
finished.camera.location = (3, -6.2, 2.55)
finished.camera.rotation_euler = (-finished.camera.location).to_track_quat('-Z', 'Y').to_euler()
if any(name not in bpy.data.scenes for name in previous_scenes):
    raise RuntimeError('旧源场景没有全部保留')
bpy.ops.wm.save_as_mainfile(filepath=str(formal))

shutil.copy2(fbx, asset / 'D32Steel_Finished.fbx')
shutil.copy2(fbx, source / 'D32Steel_Finished.fbx')
shutil.copy2(revision / 'previews' / 'D32Steel_ReferenceFront.png', source / 'D32Steel_Finished_Preview.png')
report = json.loads((revision / 'sphere_round_revision_info.json').read_text(encoding='utf-8'))
report['acceptance'] = '根代理已实际查看源模型的参考正面、三个主轴和背面；正式收藏室旋转验收待完成。'
(asset / 'material_manifest.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
status = {
    'stage': '圆形包络源已审阅，等待收藏室实际验收',
    'mesh_parts': report['mesh_count'],
    'triangles': report['triangles'],
    'blend': str(formal),
    'fbx': str(source / 'D32Steel_Finished.fbx'),
    'shape': '同半径球面大圆钢带形成球形包络，各轴中心密、外缘疏',
    'retained_scenes': len(bpy.data.scenes),
    'active_scene': bpy.context.scene.name,
}
(source / 'D32Steel_FinishStatus.json').write_text(json.dumps(status, ensure_ascii=False, indent=2), encoding='utf-8')
delivery = {
    'active_scene': bpy.context.scene.name,
    'previous_scenes_retained': len(previous_scenes),
    'total_scenes': len(bpy.data.scenes),
    'new_model_meshes': len([obj for obj in finished.objects if obj.type == 'MESH']),
    'new_white_meshes': len([obj for obj in white.objects if obj.type == 'MESH']),
    'backup': str(backup),
    'formal_fbx': str(asset / 'D32Steel_Finished.fbx'),
    'bounds': report['bounds'],
    'front_camera': report['front_camera'],
}
(revision / 'delivery_info.json').write_text(json.dumps(delivery, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(delivery, ensure_ascii=False))
print('D32_ROUND_DELIVERY_COMPLETE')
