import json,shutil,hashlib
from pathlib import Path
from datetime import datetime
project=Path('F:/游城拓荒模拟器')
source=Path(__file__).parent
review=source/'FixedViewFrameReview'/'AxisLockedFrameReview'
delivery=source/'UnityDelivery'
models=project/'Assets/YC/Presentation/Models/BipolarNanoflake'
prefab=project/'Assets/YC/Presentation/Collection/Prefabs/BipolarNanoflakeDisplay.prefab'
backup=source/'UnityReplacementBackups'/datetime.now().strftime('%Y%m%d_%H%M%S')
backup.mkdir(parents=True,exist_ok=False)
hash_file=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
saved=[]
for original in list(models.rglob('*'))+[prefab,Path(str(prefab)+'.meta'),source/'BipolarNanoflake_Finished.blend',source/'BipolarNanoflake_Finished.fbx']:
    if not original.is_file():continue
    relative=original.relative_to(project)
    destination=backup/relative
    destination.parent.mkdir(parents=True,exist_ok=True)
    shutil.copy2(original,destination)
    saved.append({'path':str(original.resolve()),'backup':str(destination),'sha256':hash_file(original)})
protected=[project/'Assets/Scenes/CollectionRoom.unity',project/'Assets/Scenes/StartScene.unity',
    project/'Assets/YC/Presentation/Collection/Items/10.asset',project/'Assets/YC/Presentation/Collection/CollectionCatalog.asset',
    project/'ConfigTables/收藏室配置.xlsx',project/'Assets/YC/Presentation/CollectionRoomController.cs',
    project/'Assets/YC/Presentation/CollectionModelViewController.cs']
state={'backup':str(backup),'savedFiles':saved,'protectedFiles':{str(p):hash_file(p) for p in protected if p.is_file()},
    'approvedBlend':str(review/'BipolarNanoflake_FixedView_Review.blend'),'approvedFbx':str(review/'BipolarNanoflake_FixedView_Review.fbx')}
(delivery/'replacement_backup.json').write_text(json.dumps(state,ensure_ascii=False,indent=2),encoding='utf-8')
for src,dst in [(delivery/'BipolarNanoflake_Finished.fbx',models/'BipolarNanoflake_Finished.fbx'),
    (review/'Textures/BipolarNanoflake_FixedView_Core_BaseColor.png',models/'Textures/BipolarNanoflake_Core_BaseColor.png'),
    (review/'Textures/BipolarNanoflake_FixedView_Core_Emission.png',models/'Textures/BipolarNanoflake_Core_Emission.png'),
    (review/'Textures/BipolarNanoflake_RedAxes_Emission.png',models/'Textures/BipolarNanoflake_RedAxes_Emission.png'),
    (review/'BipolarNanoflake_FixedView_Review.blend',source/'BipolarNanoflake_Finished.blend'),
    (delivery/'BipolarNanoflake_Finished.fbx',source/'BipolarNanoflake_Finished.fbx')]:
    shutil.copy2(src,dst)
manifest=json.loads((delivery/'material_manifest.json').read_text(encoding='utf-8'))
manifest['backup']=str(backup)
manifest['sourceBlend']=str(source/'BipolarNanoflake_Finished.blend')
manifest['fbx']=str(models/'BipolarNanoflake_Finished.fbx')
manifest['textures']['BaseColor']=str(models/'Textures/BipolarNanoflake_Core_BaseColor.png')
manifest['textures']['Emission']=str(models/'Textures/BipolarNanoflake_Core_Emission.png')
manifest['materials']['BipolarNanoflake_双红轴_由核心向外渐暗']['emissionTexture']=str(models/'Textures/BipolarNanoflake_RedAxes_Emission.png')
for path in [delivery/'material_manifest.json',models/'material_manifest.json']:
    path.write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'backup':str(backup),'backedUpFiles':len(saved),'modelHash':hash_file(models/'BipolarNanoflake_Finished.fbx')},ensure_ascii=False))
