"""只读审查 Unity 文本资产；不启动生成器，不保存 Unity 资产。"""
from pathlib import Path
import re, json, hashlib, subprocess, zipfile

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'prompt/UI换新/执行记录/局内UI基础修正'

def documents(path):
    text = path.read_text(encoding='utf-8-sig')
    return {int(m.group(2)): (int(m.group(1)), m.group(0)) for m in
            re.finditer(r'^--- !u!(\d+) &(-?\d+).*?(?=^--- !u!|\Z)', text, re.M | re.S)}

def field(text, name, default=''):
    m = re.search(r'^  '+re.escape(name)+r': (.*)$', text, re.M)
    return m.group(1) if m else default

def ref(text, name):
    return int(re.search(r'fileID: (-?\d+)', field(text, name, '{fileID: 0}')).group(1))

def inventory(path, scripts):
    docs = documents(path)
    objects = {i: field(t, 'm_Name') for i, (k,t) in docs.items() if k == 1}
    rects = {i: (ref(t, 'm_GameObject'), ref(t, 'm_Father')) for i,(k,t) in docs.items() if k in (4,224)}
    def tree(i, seen=()):
        if i not in rects or i in seen: return ''
        go, parent = rects[i]
        return (tree(parent, seen+(i,))+'/' if parent else '')+objects.get(go, str(go))
    paths = {go: tree(i) for i,(go,parent) in rects.items()}
    result = []
    for i,(k,t) in docs.items():
        if k != 114: continue
        m = re.search(r'm_Script: .*guid: ([a-f0-9]+)', t)
        script = scripts.get(m.group(1), m.group(1)) if m else ''
        if any(x in script for x in ('Layout','HudFrame','MainModules','SizeFitter','ScrollRect')):
            result.append(dict(source=str(path.relative_to(ROOT)).replace('\\','/'), fileID=i,
                               object=paths.get(ref(t,'m_GameObject'),''), script=script, configuration=t))
    return result

def main():
    OUT.mkdir(parents=True, exist_ok=True)
    target = OUT/'开工资产清单.json'
    if target.exists(): raise SystemExit('开工记录已存在，禁止覆盖。')
    scripts = {}
    for base in (ROOT/'Assets', ROOT/'Library/PackageCache'):
        for p in base.rglob('*.cs.meta'):
            m = re.search(r'^guid: (\w+)',p.read_text(encoding='utf-8-sig'),re.M)
            if m: scripts[m.group(1)] = p.name[:-5]
    files = [p for base in ('Assets/YC','Assets/Scenes','tools') for p in (ROOT/base).rglob('*')
             if p.is_file() and p.suffix in ('.cs','.prefab','.unity','.asset','.meta','.py','.ps1','.asmdef')]
    hashes = {str(p.relative_to(ROOT)).replace('\\','/'): hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
    target.write_text(json.dumps(hashes,ensure_ascii=False,indent=2),encoding='utf-8')
    (OUT/'开工Git状态.txt').write_bytes(subprocess.check_output(['git','status','--short','-uall'],cwd=ROOT))
    backup = ROOT/'Logs/GameplayUiFoundationBefore.zip'
    if backup.exists(): raise SystemExit('备份已存在，禁止覆盖。')
    backup.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(backup,'w',zipfile.ZIP_DEFLATED) as z:
        for p in files: z.write(p,str(p.relative_to(ROOT)))
    layouts=[]
    for p in files:
        if p.suffix in ('.prefab','.unity'): layouts.extend(inventory(p,scripts))
    (OUT/'开工布局明细.json').write_text(json.dumps(layouts,ensure_ascii=False,indent=2),encoding='utf-8')
    print(f'已记录 {len(hashes)} 个文件哈希、{len(layouts)} 个布局相关组件；开工备份：{backup}')

if __name__ == '__main__': main()
