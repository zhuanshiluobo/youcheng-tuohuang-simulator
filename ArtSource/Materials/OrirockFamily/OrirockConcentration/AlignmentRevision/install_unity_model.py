"""只替换提纯源岩 FBX 和四张贴图，备份被替换文件并保留全部 meta。"""
from pathlib import Path
import hashlib
import json
import shutil

source = Path(__file__).resolve().parent
project = source.parents[4]
target = project/'Assets/YC/Presentation/Models/OrirockFamily/OrirockConcentration'
backup = source/'previous_unity'
backup.mkdir(exist_ok=True)
pairs = [(source/'OrirockConcentration_Aligned.fbx', target/'OrirockConcentration_Finished.fbx')]
for src, dst in [('BaseColor','BaseColor'),('Normal','Normal'),('AO','Occlusion'),('MetallicSmoothness','MetallicSmoothness')]:
    pairs.append((source/'textures'/f'OrirockConcentration_Aligned_{src}.png',
                  target/'Textures'/f'OrirockConcentration_{dst}.png'))
record = []
for src, dst in pairs:
    if not src.is_file() or not dst.is_file():
        raise FileNotFoundError((src,dst))
    previous = backup/dst.name
    if not previous.exists():
        shutil.copy2(dst,previous)
    meta = dst.with_suffix(dst.suffix+'.meta')
    meta_hash = hashlib.sha256(meta.read_bytes()).hexdigest()
    shutil.copy2(src,dst)
    assert hashlib.sha256(meta.read_bytes()).hexdigest() == meta_hash
    record.append(dict(target=str(dst),sha256=hashlib.sha256(dst.read_bytes()).hexdigest(),meta_sha256=meta_hash))
(source/'installed_files.json').write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf-8')
print('已更新提纯源岩 FBX 和四张贴图；全部 meta 保持原样。')



