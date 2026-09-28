"""只读核对本次局内修正的身份、删除结果、基准和受保护资产，写入新的核验报告。"""
from pathlib import Path
import hashlib
import json
import re
from audit_gameplay_ui import documents, field

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'prompt/UI换新/执行记录/局内UI基础修正'
EVIDENCE = OUT.parent / '证据'


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def save(name, data):
    (OUT / name).write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')


def main():
    mapping = read(OUT / '命名文件映射.json')
    failures = []
    for item in mapping:
        target = Path(item['new'])
        if not target.exists():
            failures.append({'path': str(target), 'reason': '目标不存在'})
            continue
        if item.get('guid'):
            meta = target if target.suffix == '.meta' else Path(str(target) + '.meta')
            match = re.search(r'^guid: (\w+)', meta.read_text(encoding='utf-8-sig'), re.M)
            if not match or match.group(1) != item['guid']:
                failures.append({'path': str(target), 'reason': 'GUID 不一致'})
    object_failures = []
    object_map = read(OUT / '命名对象映射.json')
    file_map = {item['old']: item['new'] for item in mapping}
    for item in object_map:
        asset = Path(file_map.get(item['asset'], item['asset']))
        obj = documents(asset).get(int(item['fileID']))
        if obj is None or field(obj[1], 'm_Name') != item['new']:
            object_failures.append(item)
    deleted = read(OUT / '精确文件删除清单.json')
    asset_texts = [(p, p.read_text(encoding='utf-8-sig')) for p in (ROOT / 'Assets').rglob('*')
                  if p.suffix in ('.prefab', '.unity', '.asset', '.cs', '.asmdef')]
    for item in deleted:
        item['remainingGuidReferences'] = [str(p) for p, t in asset_texts if item['guid'] in t]
        item['status'] = '已删除，引用为零' if not Path(item['path']).exists() and not Path(item['meta']).exists() and not item['remainingGuidReferences'] else '核验失败'
    save('精确文件删除清单.json', deleted)
    extras = read(ROOT / 'Logs/retired-phase-two.json') + read(OUT / '附加废弃文件.json')
    save('其他精确删除最终状态.json', [{'path': p, 'status': '已删除' if not Path(p).exists() else '仍存在'} for p in extras])
    baseline = read(OUT / '开工资产清单.json')
    protected = []
    for path, digest in baseline.items():
        if path.startswith(('Assets/YC/Presentation/Prefabs/StartMenu/', 'Assets/YC/Presentation/Prefabs/GameSettings/', 'Assets/YC/Presentation/Prefabs/Viewers/')) or (path.startswith('Assets/Scenes/') and not any(s in path for s in ('SampleScene', 'ThreePlayerScene'))):
            p = ROOT / path
            protected.append({'path': path, 'unchanged': p.exists() and hashlib.sha256(p.read_bytes()).hexdigest() == digest})
    save('局外保护源核验.json', protected)
    comparisons = []
    targets = ('Main Regions', 'Player City Column', 'Map Hand Column', 'Actions Scroll Content', 'City Region', 'Map Region')
    for scene in ('SampleScene', 'ThreePlayerScene'):
        before = read(EVIDENCE / f'GameplayFoundation-Before-{scene}-1920x1080-actual-1920x1080.json')
        after = read(EVIDENCE / f'GameplayFoundation-RootFixed-{scene}-1920x1080-actual-1920x1080.json')
        for name in targets:
            def find(data):
                return next((e for e in data['entries'] if re.sub(r'^UI\d+ ', '', e['path'].split('/')[-1]) == name), None)
            a, b = find(before), find(after)
            if a and b:
                delta = max(abs(a['screenRect'][v] - b['screenRect'][v]) for v in ('x', 'y', 'width', 'height'))
                comparisons.append({'scene': scene, 'object': name, 'before': a['screenRect'], 'after': b['screenRect'], 'maximumDifferencePixels': delta})
    save('正常基准矩形对照.json', comparisons)
    save('最终身份与范围核验.json', {'fileMappingEntries': len(mapping), 'guidFailures': failures, 'objectMappingEntries': len(object_map), 'objectIdentityFailures': object_failures, 'protectedSources': len(protected), 'protectedChanges': [p for p in protected if not p['unchanged']], 'preciseDeletionFailures': [x for x in deleted if x['status'] != '已删除，引用为零']})
    print(json.dumps({'fileMappings': len(mapping), 'guidFailures': len(failures), 'objectFailures': len(object_failures), 'protectedChanges': sum(not x['unchanged'] for x in protected), 'baselineComparisons': len(comparisons)}, ensure_ascii=False))


if __name__ == '__main__':
    main()
