import bpy
import json
import shutil
from pathlib import Path
from datetime import datetime

review = Path(__file__).parent / 'FixedViewFrameReview' / 'AxisLockedFrameReview'
manifest_path = review / 'fixed_view_review_manifest.json'
report_path = review / 'red_blue_intersection_review.json'
manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
report = json.loads(report_path.read_text(encoding='utf-8'))
scene = bpy.data.scenes['BipolarNanoflake_FixedViewFrameReview']
bpy.context.window.scene = scene
root = bpy.data.objects['BipolarNanoflake_FixedView_Root']
meshes = [obj for obj in root.children_recursive if obj.type == 'MESH']
selected = list(bpy.context.selected_objects)
active = bpy.context.view_layer.objects.active
fbx_path = Path(manifest['fbx'])
backup_path = review / ('BipolarNanoflake_红蓝交点修复前导出_' + datetime.now().strftime('%Y%m%d_%H%M%S') + '.fbx')
if fbx_path.exists():
    shutil.copy2(fbx_path, backup_path)
bpy.ops.object.select_all(action='DESELECT')
for obj in [root] + list(root.children_recursive):
    if obj.type in {'EMPTY', 'MESH'}:
        obj.select_set(True)
bpy.context.view_layer.objects.active = root
bpy.ops.export_scene.fbx(filepath=str(fbx_path), use_selection=True,
    object_types={'EMPTY', 'MESH'}, use_mesh_modifiers=True,
    add_leaf_bones=False, axis_forward='-Z', axis_up='Y',
    apply_unit_scale=True, bake_space_transform=False,
    mesh_smooth_type='FACE', path_mode='STRIP')
bpy.ops.object.select_all(action='DESELECT')
for obj in selected:
    obj.select_set(True)
bpy.context.view_layer.objects.active = active
report['status'] = '红蓝交点已补齐并实际渲染复核；待用户人工验收；未替换Unity'
report['renderReviewed'] = True
report['fbx'] = str(fbx_path)
report['fbxBackup'] = str(backup_path)
manifest['status'] = report['status']
manifest['redBlueIntersectionReport'] = str(report_path)
manifest['redBlueIntersectionNodes'] = report['nodes']
manifest['latestReviewImages'] = report['images']
manifest['meshCount'] = len(meshes)
manifest['intersections'] = report['intersections']
material = bpy.data.materials['BipolarNanoflake_红蓝交点_金属套环']
shader = next(node for node in material.node_tree.nodes if node.type == 'BSDF_PRINCIPLED')
manifest['materials'][material.name] = {
    'baseColor': list(shader.inputs['Base Color'].default_value),
    'metallic': shader.inputs['Metallic'].default_value,
    'roughness': shader.inputs['Roughness'].default_value,
    'specular': shader.inputs['Specular IOR Level'].default_value,
    'emissionStrength': shader.inputs['Emission Strength'].default_value,
    'unityShader': 'Standard', 'opaque': True, 'coreTextureAtlas': False}
for path, data in [(report_path, report), (manifest_path, manifest)]:
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
notes = review / '本轮修复说明.md'
text = notes.read_text(encoding='utf-8')
text = text.replace('最新截图为“完整红导线”系列：展示视角、横向近景、外侧节点。较早的“固定验收视角”“核心双红轴”“红轴细化”图片属于对应历史阶段。参数与检查结果见 complete_red_axis_review.json。',
    '最新截图为“红蓝交点”系列：展示视角、左侧近景、右侧近景。此前“完整红导线”等图片保留为历史阶段记录。最新交点参数与检查结果见 red_blue_intersection_review.json。')
text += '\n## 红蓝交点补充（2026-10-07）\n\n左右外节点已补上贴合青蓝环曲率的中空金属套环，并与原双红导线接线座合并为连续实体。青蓝环穿过套环内孔，内半径 0.00855，环线实体半径 0.008；套环外半径 0.012、弧长 0.05375。原先节点旁的短触头已由套环替代。套环截面按单张参考图合理推定。\n\n本轮保留方框、核心、两条环线和展示相机的变换与几何；实际近景已检查左右连接。14 个模型分件之间的三角面交叉检查未发现交叉。审阅 FBX 与材质清单已同步，正式 Unity 资产仍等待用户人工验收后替换。\n'
notes.write_text(text, encoding='utf-8')
bpy.ops.wm.save_as_mainfile(filepath=manifest['blend'], compress=True)
print('FINALIZED', json.dumps({'meshCount': len(meshes), 'fbx': str(fbx_path), 'status': report['status']}, ensure_ascii=False))
