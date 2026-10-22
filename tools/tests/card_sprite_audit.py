"""卡牌切片迁移的只读审计与审阅图输出。切片坐标只读取运行时清单。

用法：python tools/tests/card_sprite_audit.py prepare|verify
此工具不删除文件，也不生成运行时单张卡图。
"""
from pathlib import Path
import csv
import hashlib
import json
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
CORE = ROOT / 'Assets/StreamingAssets/Content/core'
OUT = ROOT / 'prompt/卡牌Sprite切片替换/执行记录'
REGISTRY = json.loads((CORE / 'artwork_slices.json').read_text(encoding='utf-8-sig'))
SLICES = REGISTRY['slices']
BY_ID = {s['id']: s for s in SLICES}
BY_DEF = {}
for entry in SLICES:
    for definition in entry['definitionIds']:
        BY_DEF.setdefault(definition, []).append(entry['id'])


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(name, value):
    (OUT / name).write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def files():
    raw = subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], cwd=ROOT)
    return sorted(set(p.decode('utf-8') for p in raw.split(b'\0') if p))


def prepare():
    from PIL import Image, ImageDraw, ImageFont
    OUT.mkdir(parents=True, exist_ok=True)
    previous = OUT / '旧图删除清单.json'
    if previous.exists():
        records = json.loads(previous.read_text(encoding='utf-8'))
        if any(not Path(row['path']).exists() for row in records):
            raise SystemExit('已执行旧图删除；为保留首次清单，请改用 verify。')
    font = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 18)
    font_small = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 14)
    for sheet in REGISTRY['sheets']:
        source = ROOT / sheet['sourcePath']
        copied = CORE / sheet['artwork']
        assert digest(source) == digest(copied) == sheet['sourceSha256']
        img = Image.open(source)
        assert img.size == (sheet['width'], sheet['height'])
        # 审阅图保持原图格子顺序，跳过格也可核对；设施分两页避免文字太小。
        per_page = 27 if sheet['id'] == 'facility' else sheet['rows'] * sheet['columns']
        for start in range(0, sheet['rows'] * sheet['columns'], per_page):
            cells = list(range(start + 1, min(start + per_page, sheet['rows'] * sheet['columns']) + 1))
            cols = 3 if sheet['id'] == 'facility' else sheet['columns']
            width = 360 if sheet['cellWidth'] > sheet['cellHeight'] else 250
            height = round(width * sheet['cellHeight'] / sheet['cellWidth'])
            page = Image.new('RGB', (cols * (width + 16), ((len(cells) + cols - 1) // cols) * (height + 74)), '#192230')
            draw = ImageDraw.Draw(page)
            for i, cell in enumerate(cells):
                row, col = divmod(cell - 1, sheet['columns'])
                box = (col * sheet['cellWidth'], row * sheet['cellHeight'], (col + 1) * sheet['cellWidth'], (row + 1) * sheet['cellHeight'])
                tile = img.crop(box).resize((width, height), Image.Resampling.LANCZOS)
                x, y = (i % cols) * (width + 16) + 8, (i // cols) * (height + 74) + 6
                page.paste(tile, (x, y))
                entry = next((s for s in SLICES if s['sheetId'] == sheet['id'] and s['cell'] == cell), None)
                label = f'{cell:02d} 跳过／空格' if entry is None else f'{cell:02d} {entry["name"]}'
                draw.text((x, y + height + 3), label, font=font, fill='white')
                if entry:
                    draw.text((x, y + height + 29), ('基础' if entry['expansionId'] == 'core' else '企业扩／规则待实现') + ' · ' + entry['id'], font=font_small, fill='#91d3f5')
            page.save(OUT / f'切片总览-{sheet["id"]}-{start // per_page + 1}.jpg', quality=95)
    with (OUT / '切片映射.csv').open('w', encoding='utf-8-sig', newline='') as handle:
        writer = csv.writer(handle)
        writer.writerow(['Sprite标识', '原图', 'SHA256', '格子', '行', '列', '矩形左下角', '名称', '规则ID', '归属', '规则状态', '替换目标', '份数说明'])
        for entry in SLICES:
            sheet = next(s for s in REGISTRY['sheets'] if s['id'] == entry['sheetId'])
            writer.writerow([entry['id'], sheet['sourcePath'], sheet['sourceSha256'], entry['cell'], entry['row'], entry['column'], json.dumps(entry['rect']), entry['name'], ';'.join(entry['definitionIds']), entry['expansionId'], entry['ruleStatus'], json.dumps(entry.get('replaces', {}), ensure_ascii=False), entry.get('note', '')])
    old = {}
    def add(path, ids, reason):
        path = path.resolve()
        assert path.is_relative_to(ROOT) and path.is_file()
        assert ids and all(s in BY_ID for s in ids), str(path)
        meta = Path(str(path) + '.meta')
        guid = re.search(r'^guid: (\w+)', meta.read_text(encoding='utf-8'), re.M) if meta.exists() else None
        old[path] = {'path': str(path), 'relativePath': path.relative_to(ROOT).as_posix(), 'sha256': digest(path), 'metaPath': str(meta) if meta.exists() else None,
                     'metaSha256': digest(meta) if meta.exists() else None, 'guid': guid[1] if guid else None, 'newSprites': ids,
                     'definitionIds': sorted({d for s in ids for d in BY_ID[s]['definitionIds']}), 'reason': reason}
    for kind in ('character', 'city_style', 'event', 'facility'):
        for path in (CORE / 'artwork' / kind).glob('*.jpg'):
            add(path, BY_DEF[path.stem], '运行时旧正面；定义已关联整图 Sprite')
    # Resources 别名先按明确稳定 ID，再按与旧正式正面完全相同的字节核对。
    known_hashes = {}
    for row in old.values():
        known_hashes.setdefault(row['sha256'], set()).update(row['newSprites'])
    for path in (ROOT / 'Assets/YC/Presentation/Resources/CardImages').rglob('*'):
        if path.suffix.lower() not in ('.jpg', '.png') or path.name.startswith('back-') or path.parent.name == 'Boards':
            continue
        ids = BY_DEF.get(path.stem) or BY_DEF.get('city_style_' + path.stem) or sorted(known_hashes.get(digest(path), []))
        if path.stem == 'enterprise_office':
            ids = BY_DEF['facility_enterprise_office']
        add(path, ids, '旧 Resources 正面／重复别名；不再有持久化 Catalog 正面引用')
    source = ROOT / '游城拓荒/素材'
    city_ids = ['city_style_1_001', 'city_style_1_002', 'city_style_1_003', 'city_style_1_009', 'city_style_2_001', 'city_style_2_002']
    for path in (source / '城市样式牌拆分').glob('*.jpg'):
        add(path, [city_ids[int(path.stem.split('_')[2]) - 1]], '旧城市样式拆图')
    for path in (source / '角色卡拆分').glob('*.jpg'):
        add(path, ['character_' + path.stem.split('_')[1].zfill(3)], '旧角色拆图')
    for path in (source / '事件牌拆分').glob('*.jpg'):
        match = re.search(r'event_(green|yellow|red)_.*_r(\d+)c(\d+)$', path.stem)
        cell = (int(match[2]) - 1) * 2 + int(match[3])
        add(path, [f'event_{match[1]}_{cell:03d}'], '旧事件拆图；按原图行列核对，未使用累计编号')
    for path in (source / '建设牌输出/建筑卡片').glob('*.jpg'):
        add(path, BY_DEF['_'.join(path.stem.split('_')[:2])], '普通设施旧ID到新格子保持稳定')
    reserve = {'core_command_tower': 'reserve_001', 'extension_hub_blue': 'reserve_002', 'extension_hub_yellow': 'reserve_003', 'extension_hub_red': 'reserve_004'}
    for path in (source / '建设牌输出/备用卡片').glob('*.jpg'):
        key = next(k for k in reserve if path.stem.startswith(k))
        add(path, BY_DEF[reserve[key]], '备用设施拆图；核心塔共用规则定义并登记四格')
    for path in (source / '建设牌拆分').glob('grid_*.jpg'):
        match = re.fullmatch(r'grid_r(\d+)_c(\d+)', path.stem)
        cell = (int(match[1]) - 1) * 9 + int(match[2])
        if cell <= 51:
            add(path, [f'facility_{cell:03d}'], '旧建设牌逐格正面')
    hashes = {}
    for row in old.values():
        hashes.setdefault(row['sha256'], set()).update(row['newSprites'])
    for relative in files():
        path = ROOT / relative
        if path.suffix.lower() in ('.png', '.jpg', '.jpeg') and path.exists() and path.resolve() not in old:
            ids = hashes.get(digest(path))
            if ids:
                add(path, sorted(ids), '项目中旧正面的字节完全相同副本')
    rows = sorted(old.values(), key=lambda row: row['relativePath'])
    write('旧图删除清单.json', rows)
    scan(rows, '删除前引用扫描.json')
    print(json.dumps({'images': len(rows), 'metas': sum(bool(r['metaPath']) for r in rows), 'slices': len(SLICES)}, ensure_ascii=False))


def scan(rows, output):
    targets = {row['relativePath'] for row in rows}
    guids = {row['guid']: row['relativePath'] for row in rows if row['guid']}
    pattern = re.compile('|'.join(re.escape(g) for g in guids))
    paths = {p: p for p in targets}
    paths.update({p[p.index('CardImages/'):].rsplit('.', 1)[0]: p for p in targets if 'CardImages/' in p})
    path_pattern = re.compile('|'.join(re.escape(p) for p in sorted(paths, key=len, reverse=True)))
    matches, historical = [], []
    for relative in files():
        path = ROOT / relative
        if not path.exists() or relative in targets or relative.removesuffix('.meta') in targets or '卡牌Sprite切片替换/执行记录/' in relative:
            continue
        if path.suffix.lower() not in ('.cs', '.asset', '.prefab', '.unity', '.json', '.py', '.ps1', '.md', '.txt',
                                       '.meta', '.mat', '.controller', '.overridecontroller', '.anim', '.spriteatlas',
                                       '.rendertexture', '.terrainlayer', '.playable', '.shader', '.yaml', '.yml'):
            continue
        text = path.read_text(encoding='utf-8-sig', errors='replace')
        for number, line in enumerate(text.splitlines(), 1):
            guid_hits = [guids[g] for g in pattern.findall(line)]
            path_hits = [paths[p] for p in path_pattern.findall(line)]
            if guid_hits or path_hits:
                result = {'file': relative, 'line': number, 'targets': sorted(set(guid_hits + path_hits))}
                # 该只读规则校验器不解析 image 字段；保留 JSON 避免改变旧规则资产源哈希。
                historical_manifest = relative == 'Assets/YC/Editor/Data/building_cards_manifest.json' and not guid_hits
                (matches if relative.startswith('Assets/') and not historical_manifest else historical).append(result)
    write(output, {'liveAssetReferences': matches, 'historicalOrToolReferences': historical})
    print('live references:', len(matches), 'historical/tools:', len(historical))


def verify():
    rows = json.loads((OUT / '旧图删除清单.json').read_text(encoding='utf-8'))
    remaining = [p for row in rows for p in (row['path'], row['metaPath']) if p and Path(p).exists()]
    scan(rows, '删除后引用扫描.json')
    changes = subprocess.check_output(['git', '-c', 'core.quotePath=false', 'diff', '--name-only', '--diff-filter=D', '-z'], cwd=ROOT).decode('utf-8').split('\0')
    allowed = {str(Path(p).relative_to(ROOT)).replace('\\', '/') for row in rows for p in (row['path'], row['metaPath']) if p}
    deleted = [p for p in changes if p]
    write('删除后核验.json', {'remainingTargets': remaining, 'gitDeletedCount': len(deleted), 'unexpectedDeletions': sorted(set(deleted) - allowed),
                             'deletedImages': len(rows), 'deletedMetas': sum(bool(r['metaPath']) for r in rows),
                             'sourceHashesUnchanged': all(digest(ROOT / s['sourcePath']) == s['sourceSha256'] == digest(CORE / s['artwork']) for s in REGISTRY['sheets'])})
    print('remaining:', len(remaining), 'git deletions:', len(deleted), 'unexpected:', len(set(deleted) - allowed))


if __name__ == '__main__':
    {'prepare': prepare, 'verify': verify}[sys.argv[1]]()
