"""替换明确的固源岩 FBX/贴图，保留 GUID 和旧资产备份。"""
from pathlib import Path
import shutil,json,hashlib
from PIL import Image

OUT=Path(__file__).resolve().parent
PROJECT=OUT.parents[4]
ASSETS=PROJECT/'Assets/YC/Presentation/Models/OrirockFamily/OrirockCube'
PREVIOUS=OUT/'PreviousAssets'; PREVIOUS.mkdir(exist_ok=True)
names=['BaseColor','Normal','Occlusion','MetallicSmoothness']
rows=[]
for name in names:
    source=OUT/'textures'/f'OrirockCube_{name}.png'
    with Image.open(source) as im:
        assert im.size==(2048,2048),str(source)
        if name=='MetallicSmoothness': assert im.mode=='RGBA'
        rows.append({'贴图':source.name,'尺寸':list(im.size),'通道':im.mode,'范围':im.getextrema()})
    target=ASSETS/'Textures'/source.name
    for path in [target,Path(str(target)+'.meta')]:
        backup=PREVIOUS/path.name
        if not backup.exists(): shutil.copy2(path,backup)
    shutil.copy2(source,target)
    meta=Path(str(target)+'.meta')
    value=meta.read_text(encoding='utf-8')
    value=value.replace('maxTextureSize: 1024','maxTextureSize: 2048')
    meta.write_text(value,encoding='utf-8',newline='\n')
fbx=ASSETS/'OrirockCube_Finished.fbx'
if not (PREVIOUS/fbx.name).exists(): shutil.copy2(fbx,PREVIOUS/fbx.name)
shutil.copy2(OUT/'OrirockCube_Refined.fbx',fbx)
result={'更新目录':str(ASSETS),'FBX_SHA256':hashlib.sha256(fbx.read_bytes()).hexdigest(),'贴图检查':rows,
        '说明':'保留FBX、材质与纹理GUID、预制体及场景；默认纹理导入上限调整为2048。'}
(OUT/'installed_assets.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(result,ensure_ascii=False),flush=True)
