"""只读核对企业／科室组件资产、导入来源与本次工作区边界；不启动或重建 Unity UI。"""
from pathlib import Path
import hashlib
import json
import re
import zipfile

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'prompt/UI换新/执行记录/企业与科室'
PRESENTATION = ROOT / 'Assets/YC/Presentation'


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    baseline = json.loads((OUT / '开工资产哈希.json').read_text(encoding='utf-8'))
    allowed = {
        'Assets/YC/Presentation/GameplayDialogRegistry.cs',
        'Assets/YC/Presentation/GameplayHudFrame.cs',
        'Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab',
    }
    changes = [p for p, h in baseline.items() if not (ROOT / p).is_file() or digest(ROOT / p) != h]
    unexpected = [p for p in changes if p not in allowed]
    guid_paths = {}
    for directory in (ROOT / 'Assets', ROOT / 'Library/PackageCache/com.unity.ugui@1.0.0'):
        for p in directory.rglob('*.meta'):
            match = re.search(r'^guid: (\w+)', p.read_text(encoding='utf-8-sig'), re.M)
            if match:
                guid_paths[match[1]] = str(p.relative_to(ROOT))[:-5]
    pages = []
    unresolved = []
    for p in (PRESENTATION / 'Prefabs/Gameplay/Dialogs/Enterprise').glob('*.prefab'):
        text = p.read_text(encoding='utf-8')
        ids = set(re.findall(r'^--- !u!\d+ &(-?\d+)', text, re.M))
        missing_ids = [i for i in re.findall(r'\{fileID: (-?\d+)\}', text) if i != '0' and i not in ids]
        references = sorted(set(re.findall(r'guid: (\w+)', text)))
        missing_guids = [g for g in references if g not in guid_paths]
        unresolved.extend(missing_ids + missing_guids)
        pages.append(dict(asset=str(p.relative_to(ROOT)), missingFileIds=missing_ids, missingGuids=missing_guids,
                          references={g: guid_paths.get(g) for g in references}))
    source_mapping = json.loads((OUT / '新增资产映射.json').read_text(encoding='utf-8'))
    sources = []
    for item in source_mapping['artwork']:
        sources.append(dict(asset=item['asset'], source=item['source'],
                            originalBytesPreserved=digest(Path(item['source'])) == digest(Path(item['asset']))))
    with zipfile.ZipFile(ROOT / '游城拓荒/UI素材/Card-Picker-UI-v1.zip') as archive:
        for kind in ('primary', 'secondary'):
            p = PRESENTATION / f'Enterprise/Artwork/selection-button-{kind}.png'
            entry = f'card-picker-ui-v1/assets/button-{kind}.png'
            sources.append(dict(asset=str(p), source='Card-Picker-UI-v1.zip/' + entry,
                                originalBytesPreserved=digest(p) == hashlib.sha256(archive.read(entry)).hexdigest()))
    report = dict(existingFileChanges=changes, unexpectedChanges=unexpected, assets=pages,
                  unresolvedReferences=unresolved, artwork=sources,
                  scenesUnchanged=all(digest(ROOT / p) == h for p, h in baseline.items() if p.startswith('Assets/Scenes/')),
                  limitation='静态来源和范围检查，不代表画面或正式业务通过。')
    (OUT / '最终资产与范围核验.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(dict(existingChanges=len(changes), unexpectedChanges=len(unexpected),
                          unresolvedReferences=len(unresolved), originalArtwork=len(sources),
                          scenesUnchanged=report['scenesUnchanged']), ensure_ascii=False))
    if unexpected or unresolved or not all(s['originalBytesPreserved'] for s in sources):
        raise SystemExit(1)


if __name__ == '__main__':
    main()
