import json,hashlib,re
from pathlib import Path
project=Path('F:/游城拓荒模拟器')
source=Path(__file__).parent
delivery=source/'UnityDelivery'
review=source/'FixedViewFrameReview'/'AxisLockedFrameReview'
models=project/'Assets/YC/Presentation/Models/BipolarNanoflake'
state=json.loads((delivery/'replacement_backup.json').read_text(encoding='utf-8'))
runtime=json.loads((delivery/'unity_runtime_review.json').read_text(encoding='utf-8'))
replacement=json.loads((delivery/'unity_replacement_review.json').read_text(encoding='utf-8'))
hash_file=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
changed_protected=[path for path,value in state['protectedFiles'].items() if hash_file(Path(path))!=value]
guid_changes=[]
for entry in state['savedFiles']:
    path=Path(entry['path'])
    if path.suffix!='.meta':continue
    old=re.search(r'^guid: (\w+)',Path(entry['backup']).read_text(encoding='utf-8'),re.M)
    new=re.search(r'^guid: (\w+)',path.read_text(encoding='utf-8'),re.M)
    if old and new and old.group(1)!=new.group(1):guid_changes.append(str(path))
status='用户已人工验收；Unity 正式模型、材质与展示预制体已替换并完成轻量运行验收'
manifest=json.loads((models/'material_manifest.json').read_text(encoding='utf-8'))
manifest.update(status=status,unityPrefab=str(project/'Assets/YC/Presentation/Collection/Prefabs/BipolarNanoflakeDisplay.prefab'),
    unityReplacementReport=str(delivery/'unity_replacement_review.json'),unityRuntimeReport=str(delivery/'unity_runtime_review.json'),
    sourceBlend=str(source/'BipolarNanoflake_Finished.blend'),sourceScene='BipolarNanoflake_FixedViewFrameReview',
    sourceModelRoot='BipolarNanoflake_FixedView_Root',backup=state['backup'],meshCount=14)
manifest['unityDisplayAdaptation']={key:replacement[key] for key in ['alignmentRotation','alignmentScale','alignmentPosition','poseRotation','poseScale','afterBounds']}
manifest['latestUnityReviewImage']=str(delivery/'双极纳米片_Unity收藏室_实际界面.png')
manifest['validation']={'protectedFilesChanged':changed_protected,'existingGuidsChanged':guid_changes,
    'rotationChangeDegrees':runtime['rotationChangeDegrees'],'resetRotationErrorDegrees':runtime['resetRotationErrorDegrees'],
    'switchAwayFinished':runtime.get('switchAwayFinished',False),'switchBackFinished':runtime.get('switchBackFinished',False),
    'missingMeshes':runtime.get('missingMeshes'),'missingMaterials':runtime.get('missingMaterials'),
    'commonAxis':runtime.get('commonAxis'),'localLightsAndProbePreserved':replacement['protectedComponentsUnchanged']}
for path in [models/'material_manifest.json',delivery/'material_manifest.json']:
    path.write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
old_review=json.loads((review/'fixed_view_review_manifest.json').read_text(encoding='utf-8'))
old_review.update(status=status,unityMaterialManifest=str(models/'material_manifest.json'),unityPrefab=manifest['unityPrefab'],
    latestUnityReviewImage=manifest['latestUnityReviewImage'])
(review/'fixed_view_review_manifest.json').write_text(json.dumps(old_review,ensure_ascii=False,indent=2),encoding='utf-8')
notes=review/'本轮修复说明.md'
text=notes.read_text(encoding='utf-8')
text=text.replace('Unity 正式资产和展示预制体尚未替换。','用户已人工验收，Unity 正式资产和展示预制体已替换。')
text=text.replace('当前待用户人工验收，不代表已与参考图精确一致。','用户已人工验收当前造型；背面及套环截面仍按单图推定。')
text=text.replace('正式 Unity 资产仍等待用户人工验收后替换。','正式 Unity 资产已按本轮人工验收授权替换。')
text+='\n## Unity 正式替换（2026-10-07）\n\n已替换正式 FBX、核心双色贴图和双红轴发光贴图，更新展示预制体的模型子树。既有 .meta 和 GUID 保留，局部两盏灯光、反射探针和展示姿态节点保持原设置。额外的“验收视角对齐”节点完成 Blender 相机到 Unity 的坐标换算与统一缩放，模型分件没有再改尺寸或转角。框面粗糙度遮罩转成同色不同光滑度的材质槽，没有添加白色线条。核心继续使用不透明 Standard；仅红盘关闭直接镜面高光，保留环境反射，以避免收藏室灯光把渐变盘照成灰白。\n\n从已有主菜单进入实际收藏室观察：旋转 25° 后复位误差为 0；缩放到 75% 后复位至 100%；10→09→10 切换完成。五框轴向夹角和中心差均为 0，14 个模型分件网格及材质引用完整，静态 FBX 不包含动画。场景、UI、配置表和公共接口未改动。有效实际界面截图见 UnityDelivery/双极纳米片_Unity收藏室_实际界面.png。\n'
notes.write_text(text,encoding='utf-8')
# 最后一次截图时编辑器已离开收藏室，该黑图不作为验收证据。
black=delivery/'双极纳米片_Unity完成版_收藏室.png'
failed=delivery/'截图未用于验收_编辑器已离开收藏室.png'
if black.exists() and not failed.exists():black.rename(failed)
summary={'status':status,'backup':state['backup'],'protectedFilesChanged':changed_protected,'existingGuidsChanged':guid_changes,
    'modelSha256':hash_file(models/'BipolarNanoflake_Finished.fbx'),
    'prefabSha256':hash_file(project/'Assets/YC/Presentation/Collection/Prefabs/BipolarNanoflakeDisplay.prefab'),
    'actualScreenshot':manifest['latestUnityReviewImage'],'runtime':runtime}
(delivery/'交付验收记录.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
(delivery/'交付说明.md').write_text('# 双极纳米片完成版\n\n'+status+'。\n\n可编辑源：'+manifest['sourceBlend']+'\n\n正式模型：'+manifest['fbx']+'\n\n正式展示预制体：'+manifest['unityPrefab']+'\n\n替换前备份：'+state['backup']+'\n\n保留原模型、白模和辅助场景；没有制作金色外围边框或灰色背景。背面与连接套环截面按单张参考图合理推定。核心、框和红蓝交点保持用户人工验收的几何。Unity 材质及实际运行验收参数详见 material_manifest.json、unity_replacement_review.json、unity_runtime_review.json。实际界面截图为“ 双极纳米片_Unity收藏室_实际界面.png ”。其他诊断或未用于验收截图不代表最终画面。\n',encoding='utf-8')
print(json.dumps({'protectedFilesChanged':changed_protected,'existingGuidsChanged':guid_changes,'runtimeSwitchVerified':runtime.get('switchBackFinished',False),'meshCount':14},ensure_ascii=False))
