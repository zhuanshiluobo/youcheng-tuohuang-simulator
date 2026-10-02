"""定点导入地图按钮 v1.2；不启动 Unity，不调用界面 Rebuild。

运行前应审阅 ROUTES 与静态规则定义、原图航道字母和端点的对应。
只修改两份地图布局及对应源预制体，保留场景覆盖。
"""
import csv
import hashlib
import io
import json
import re
import uuid
import zipfile
from pathlib import Path

from PIL import Image, ImageDraw, ImageChops, ImageStat

ROOT = Path(__file__).resolve().parents[1]
ROUTES = {
    'four': 'G1 R1 G2 F1 F3 R2 A1 D1 D2 F2 R3 R4 R5 R6 A2 R7 B1 E1 E2 C1 C2 B2'.split(),
    'three': 'R1 F1 D1 F3 A1 D2 F2 R2 A2 R4 R6 R7 E1 B1 R5 E2 R3 C2 B2 C1'.split(),
}
PREFABS = {
    'four': 'Assets/YC/Presentation/Prefabs/Map/MapView.prefab',
    'three': 'Assets/Resources/MapViews/map-three-players.prefab',
}
DEST = ROOT / 'Assets/YC/Presentation/Maps/Highlights'
REPORT = ROOT / 'prompt/地图按钮与选择交互/实施记录'


def guid(path):
    meta = Path(str(path) + '.meta')
    if meta.exists():
        return re.search(r'guid: (\w+)', meta.read_text(encoding='utf-8'))[1]
    return uuid.uuid5(uuid.NAMESPACE_URL, 'nomadcity-map-highlights/' + str(path.relative_to(ROOT))).hex


def meta(path, importer='DefaultImporter', fields=''):
    dest = Path(str(path) + '.meta')
    if not dest.exists():
        folder = 'folderAsset: yes\n' if path.is_dir() else ''
        dest.write_text(f'fileFormatVersion: 2\nguid: {guid(path)}\n{folder}{importer}:\n'
                        f'  externalObjects: {{}}\n{fields}  userData: \n'
                        '  assetBundleName: \n  assetBundleVariant: \n', encoding='utf-8')


def vec(x, y, z=None):
    return '{x: %.10g, y: %.10g%s}' % (x, y, '' if z is None else ', z: %.10g' % z)


def reference(block, name):
    return re.search(r'\b' + name + r': \{fileID: (\d+)', block)[1]


def put(block, name, value):
    return re.sub(r'(?m)^(\s*' + name + r': ).*$', lambda m: m[1] + str(value), block)


def parse_prefab(text):
    return {i: [-int(kind) if stripped else int(kind), body] for kind, i, stripped, body in re.findall(
        r'^--- !u!(\d+) &(\d+)( stripped)?\n(.*?)(?=^--- !u!|\Z)', text, re.M | re.S)}


def find_script(blocks, name):
    script_guid = guid(ROOT / name)
    return next((i, b[1]) for i, b in blocks.items() if 'guid: ' + script_guid in b[1])


def transform_of(blocks, component):
    go = blocks[reference(blocks[component][1], 'm_GameObject')][1]
    return next(i for i in re.findall(r'component: \{fileID: (\d+)', go) if blocks[i][0] == 4)


COMMON = '  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n'


def game_object(name, components):
    return 'GameObject:\n' + COMMON + '  serializedVersion: 6\n  m_Component:\n' + ''.join(
        f'  - component: {{fileID: {c}}}\n' for c in components) + (
        f'  m_Layer: 0\n  m_Name: {name}\n  m_TagString: Untagged\n'
        '  m_Icon: {fileID: 0}\n  m_NavMeshLayer: 0\n  m_StaticEditorFlags: 0\n  m_IsActive: 1\n')


def transform(go, parent, children, pos=(0, 0, 0), scale=(1, 1, 1)):
    return ('Transform:\n' + COMMON + f'  m_GameObject: {{fileID: {go}}}\n  serializedVersion: 2\n'
            '  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}\n'
            f'  m_LocalPosition: {vec(*pos)}\n  m_LocalScale: {vec(*scale)}\n'
            '  m_ConstrainProportionsScale: 0\n  m_Children:' + ('\n' + ''.join(
                f'  - {{fileID: {c}}}\n' for c in children) if children else ' []\n') +
            f'  m_Father: {{fileID: {parent}}}\n  m_LocalEulerAnglesHint: {{x: 0, y: 0, z: 0}}\n')


def main():
    package = ROOT / '游城拓荒/地图按钮/Map-Highlights-v1.zip'
    z = zipfile.ZipFile(package)
    manifest = json.loads(z.read('Map-Highlights-v1/data/assets.json'))
    assert manifest['version'] == '1.2'
    for prefab in PREFABS.values():
        assert '  buttonCatalog:' not in (ROOT / prefab).read_text(encoding='utf-8'), '已导入；不要覆盖现有按钮资产'
    DEST.mkdir(parents=True, exist_ok=True)
    REPORT.mkdir(parents=True, exist_ok=True)
    for name in ['assets.json', 'four-player-map.json', 'three-player-map.json']:
        p = DEST / name
        p.write_bytes(z.read('Map-Highlights-v1/data/' + name))
        meta(p, 'TextScriptImporter')
    for a in manifest['assets']:
        p = DEST / Path(a['files']['4x']).name
        p.write_bytes(z.read('Map-Highlights-v1/' + a['files']['4x']))
        template = (ROOT / 'Assets/YC/Data/Maps/Textures/Map_4Players.jpg.meta').read_text(encoding='utf-8')
        template = re.sub(r'guid: \w+', 'guid: ' + guid(p), template, count=1)
        for field, value in [('spriteMeshType', 0), ('spriteGenerateFallbackPhysicsShape', 0),
                             ('alphaIsTransparency', 1), ('textureCompression', 0)]:
            template = put(template, field, value)
        Path(str(p) + '.meta').write_text(template, encoding='utf-8')
    for script in ['Assets/YC/Presentation/Maps/MapHighlightCatalog.cs',
                   'Assets/YC/Presentation/MapButtonView.cs']:
        meta(ROOT / script, 'MonoImporter', '  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n')
    catalog = DEST / 'MapHighlightCatalog.asset'
    catalog_text = '%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!114 &11400000\nMonoBehaviour:\n' + COMMON
    catalog_text += ('  m_GameObject: {fileID: 0}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n'
                     f'  m_Script: {{fileID: 11500000, guid: {guid(ROOT / "Assets/YC/Presentation/Maps/MapHighlightCatalog.cs")}, type: 3}}\n'
                     '  m_Name: MapHighlightCatalog\n  m_EditorClassIdentifier: \n'
                     f'  SourceManifest: {{fileID: 4900000, guid: {guid(DEST / "assets.json")}, type: 3}}\n'
                     f'  Version: {manifest["version"]}\n  SourceMapSize: {vec(*manifest["source_map_size"])}\n  Shapes:\n')
    shapes = {}
    for a in manifest['assets']:
        if a['state'] != 'available':
            continue
        selected = next(v for v in manifest['assets'] if v['id'] == a['kind'] + '-selected')
        assert a['polygon_top_left'] == selected['polygon_top_left']
        shapes[a['kind']] = a
        catalog_text += (f'  - Id: {a["kind"]}\n    OutlineSize: {vec(a["outline_width"], a["outline_height"])}\n'
                         f'    CanvasSize: {vec(a["logical_width"], a["logical_height"])}\n'
                         f'    Pivot: {vec(*a["pivot"])}\n    PolygonTopLeft:\n' + ''.join(
                             f'    - {vec(*v)}\n' for v in a['polygon_top_left']))
        for key, state in [('Available', a), ('Selected', selected)]:
            catalog_text += f'    {key}: {{fileID: 21300000, guid: {guid(DEST / Path(state["files"]["4x"]).name)}, type: 3}}\n'
    catalog.write_text(catalog_text, encoding='utf-8')
    meta(catalog, 'NativeFormatImporter', '  mainObjectFileID: 11400000\n')
    slot_rows, target_rows, registration = [], [], []
    domain = (ROOT / 'Assets/YC/Domain/Maps/StaticMapDefinitions.cs').read_text(encoding='utf-8')
    for map_name, mapped in ROUTES.items():
        map_id = f'map-{map_name}-players'
        data = json.loads((DEST / f'{map_name}-player-map.json').read_text(encoding='utf-8'))
        rule_text = domain.split('public static GameMapDefinition Create' + map_name.title() + 'PlayerMap()')[1]
        rule_text = rule_text.split('private static MapLocationDefinition')[0].split('public static GameMapDefinition Create')[0]
        rules = {rid: (region, int(count), re.findall(r'"([A-G]-\d+)"', locations)) for rid, region, count, locations in re.findall(
            r'Route\("(\w+)", "(\w+)", (\d+), ([^\n]+)\)', rule_text)}
        assert len(mapped) == len(set(mapped)) == len(data['routes']) == len(rules)
        assert set(mapped) == set(rules)
        mapping = {f'L{i+1:02}': rid for i, rid in enumerate(mapped)}
        layout_path = ROOT / f'Assets/Resources/MapLayouts/{map_id}.asset'
        layout = layout_path.read_text(encoding='utf-8')
        targets = []
        for kind, entries in [('resource', data['resources']), ('route', data['routes'])]:
            for entry in entries:
                target = entry['id'] if kind == 'resource' else mapping[entry['id']]
                slots = [s for s in data['influence_slots'] if s['parent_kind'] == kind and s['parent_id'] == entry['id']]
                assert sorted(s['local_index'] for s in slots) == list(range(1, len(slots) + 1))
                if kind == 'route':
                    assert len(slots) == rules[target][1]
                    assert entry['outline_width'] == (118 if len(slots) == 1 else 137)
                key = 'LocationId' if kind == 'resource' else 'RouteId'
                pattern = r'(  - ' + key + ': ' + re.escape(target) + r'\n)(.*?)(?=  - (?:LocationId|RouteId):|  Routes:|  spatialLayout|\Z)'
                match = re.search(pattern, layout, re.S)
                assert match, target
                old = match[2]
                ax, ay = map(float, re.search(r'NormalizedPosition: \{x: ([^,]+), y: ([^}]+)', old).groups())
                offsets = re.findall(r'Offset: \{x: ([^,]+), y: ([^}]+)\}', old.split('InfluenceSlots:')[1])
                n = 0
                def offset_replace(m):
                    nonlocal n
                    slot = slots[n]
                    ox, oy = map(float, offsets[n])
                    nx, ny = slot['x'] / 2048, 1 - slot['y'] / 2048
                    slot_id = ('location:' if kind == 'resource' else 'route:') + target + ':' + str(n)
                    slot_rows.append([map_id, slot['id'], slot_id, PREFABS[map_name], '', (ax+ox)*2048, (1-ay-oy)*2048,
                                      slot['x'], slot['y'], nx, ny, nx-ax, ny-ay])
                    targets.append((2, slot_id, 'influence', nx, ny, slot['id']))
                    n += 1
                    return 'Offset: ' + vec(nx-ax, ny-ay)
                prefix, influence = old.split('InfluenceSlots:', 1)
                influence = re.sub(r'Offset: \{x: [^,]+, y: [^}]+\}', offset_replace, influence)
                assert n == len(slots)
                updated = f'    ButtonPosition: {vec(entry["x"]/2048, 1-entry["y"]/2048)}\n'
                if kind == 'route':
                    updated += f'    SourceButtonId: {entry["id"]}\n'
                updated += prefix + 'InfluenceSlots:' + influence
                layout = layout[:match.start(2)] + updated + layout[match.end(2):]
                shape = 'resource' if kind == 'resource' else ('route-single' if len(slots) == 1 else 'route-double')
                targets.append((0 if kind == 'resource' else 1, target, shape, entry['x']/2048, 1-entry['y']/2048, entry['id']))
                target_rows.append([map_id, kind, entry['id'], target, len(slots),
                                    '' if kind == 'resource' else rules[target][0],
                                    '' if kind == 'resource' else '|'.join(rules[target][2]), entry['x'], entry['y'], shape])
        layout_path.write_text(layout, encoding='utf-8')
        prefab_path = ROOT / PREFABS[map_name]
        blocks = parse_prefab(prefab_path.read_text(encoding='utf-8'))
        _, coord = find_script(blocks, 'Assets/YC/Presentation/Maps/MapCoordinateSpace.cs')
        view_id, view = find_script(blocks, 'Assets/YC/Presentation/MapView.cs')
        map_transform = transform_of(blocks, reference(coord, 'mapRenderer'))
        # 地图原图 5000 px / PPU 100；运行时再用真实 Sprite.bounds 计算，测试核对。
        size = 50.0
        slots_section = view.split('  influenceSlots:\n')[1].split('  cityPool:')[0]
        for slot_id, binding in re.findall(r'  - slotId: (\S+)\n(.*?)(?=  - slotId:|\Z)', slots_section, re.S):
            row = next(r for r in slot_rows if r[0] == map_id and r[2] == slot_id)
            tid = transform_of(blocks, reference(binding, 'renderer'))
            row[4] = tid
            blocks[tid][1] = put(blocks[tid][1], 'm_LocalPosition', vec((row[9]-.5)*size, (.5-row[10])*size, 0))
            cid = reference(binding, 'collider')
            blocks[cid][1] = put(blocks[cid][1], 'm_Enabled', 0)
        # 停用旧热点命中和显示；保留已有放置反馈与模型对象。
        hotspot_guid = guid(ROOT / 'Assets/YC/Presentation/MapHotspot.cs')
        for _, block in list(blocks.values()):
            if 'guid: ' + hotspot_guid not in block:
                continue
            go = blocks[reference(block, 'm_GameObject')][1]
            for cid in re.findall(r'component: \{fileID: (\d+)', go):
                if blocks[cid][0] in (58, 61, 197, 212):
                    blocks[cid][1] = put(blocks[cid][1], 'm_Enabled', 0)
            tid = transform_of(blocks, reference(block, 'spriteRenderer'))
            pos = re.search(r'm_LocalPosition: \{x: ([^,]+), y: ([^,]+), z: [^}]+\}', blocks[tid][1])
            blocks[tid][1] = put(blocks[tid][1], 'm_LocalPosition', vec(float(pos[1]), float(pos[2]), 0))
        base = 900100000000000000
        root_go, root_tr = base, base+1
        children, button_refs = [], []
        for index, (kind, target, shape_id, nx, ny, source_id) in enumerate(targets):
            start = base + 10 + index*10
            go, tr, script, collider, image_go, image_tr, renderer = range(start, start+7)
            children.append(tr)
            button_refs.append(script)
            a = shapes[shape_id]
            sprite_guid = guid(DEST / Path(a['files']['4x']).name)
            blocks[str(go)] = [1, game_object('按钮 ' + target, [tr, collider, script])]
            blocks[str(tr)] = [4, transform(go, root_tr, [image_tr], ((nx-.5)*size, (.5-ny)*size, 0))]
            blocks[str(image_go)] = [1, game_object('轮廓', [image_tr, renderer])]
            blocks[str(image_tr)] = [4, transform(image_go, tr, [], scale=(size/2048*100/4, size/2048*100/4, 1))]
            blocks[str(renderer)] = [212, 'SpriteRenderer:\n' + COMMON + f'  m_GameObject: {{fileID: {image_go}}}\n'
                '  m_Enabled: 0\n  m_CastShadows: 0\n  m_ReceiveShadows: 0\n  m_DynamicOccludee: 1\n'
                '  m_Materials:\n  - {fileID: 10754, guid: 0000000000000000f000000000000000, type: 0}\n'
                '  m_SortingLayerID: 0\n  m_SortingLayer: 0\n  m_SortingOrder: 25\n'
                f'  m_Sprite: {{fileID: 21300000, guid: {sprite_guid}, type: 3}}\n'
                '  m_Color: {r: 1, g: 1, b: 1, a: 1}\n  m_FlipX: 0\n  m_FlipY: 0\n  m_DrawMode: 0\n'
                '  m_Size: {x: 1, y: 1}\n  m_MaskInteraction: 0\n  m_SpriteSortPoint: 0\n']
            polygon = [(x-a['outline_width']/2, a['outline_height']/2-y) for x,y in a['polygon_top_left']]
            blocks[str(collider)] = [60, 'PolygonCollider2D:\n' + COMMON + f'  m_GameObject: {{fileID: {go}}}\n'
                '  m_Enabled: 0\n  m_Density: 1\n  m_Material: {fileID: 0}\n  m_IsTrigger: 0\n'
                '  m_UsedByEffector: 0\n  m_UsedByComposite: 0\n  m_Offset: {x: 0, y: 0}\n'
                '  m_Points:\n    m_Paths:\n    - ' + '\n      '.join('- '+vec(x*size/2048,y*size/2048) for x,y in polygon) + '\n']
            blocks[str(script)] = [114, 'MonoBehaviour:\n' + COMMON + f'  m_GameObject: {{fileID: {go}}}\n'
                '  m_Enabled: 1\n  m_EditorHideFlags: 0\n'
                f'  m_Script: {{fileID: 11500000, guid: {guid(ROOT / "Assets/YC/Presentation/MapButtonView.cs")}, type: 3}}\n'
                '  m_Name: \n  m_EditorClassIdentifier: \n'
                f'  targetKind: {kind}\n  targetId: {target}\n  shapeId: {shape_id}\n'
                f'  image: {{fileID: {renderer}}}\n  hitArea: {{fileID: {collider}}}\n']
        blocks[str(root_go)] = [1, game_object('地图轮廓按钮', [root_tr])]
        blocks[str(root_tr)] = [4, transform(root_go, map_transform, children)]
        blocks[map_transform][1] = blocks[map_transform][1].replace('  m_Children:\n', f'  m_Children:\n  - {{fileID: {root_tr}}}\n')
        blocks[view_id][1] += f'  buttonCatalog: {{fileID: 11400000, guid: {guid(catalog)}, type: 2}}\n  buttons:\n' + ''.join(f'  - {{fileID: {b}}}\n' for b in button_refs)
        prefab_path.write_text('%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n' + ''.join(
            f'--- !u!{abs(kind)} &{fid}{" stripped" if kind < 0 else ""}\n{body}' for fid, (kind, body) in blocks.items()), encoding='utf-8')
        texture = ROOT / f'Assets/YC/Data/Maps/Textures/Map_{4 if map_name == "four" else 3}Players.jpg'
        original = Image.open(texture).convert('RGBA').resize((2048,2048), Image.Resampling.LANCZOS)
        preview = Image.open(io.BytesIO(z.read(f'Map-Highlights-v1/previews/{map_name}-player-routes.jpg'))).convert('RGB')
        patches = [(5,5,65,65),(1960,5,2020,65),(5,1970,65,2030),(1960,1970,2020,2030),(990,990,1020,1020)]
        registration.append(dict(map_id=map_id, project_sha256=hashlib.sha256(texture.read_bytes()).hexdigest(),
            delivery_source=data['source'], project_size=Image.open(texture).size,
            comparison='交付航道叠图与工程完整底图等比例缩放，对比四角和中心；原文件哈希不同，不宣称同一文件',
            patches=[dict(box=b, mean_absolute_rgb=ImageStat.Stat(ImageChops.difference(original.convert('RGB').crop(b),preview.crop(b))).mean) for b in patches]))
        for state in ['available','selected']:
            composite = original.copy()
            for kind,target,shape_id,nx,ny,sid in targets:
                a = next(a for a in manifest['assets'] if a['id'] == shape_id+'-'+state)
                png = Image.open(DEST / Path(a['files']['4x']).name).convert('RGBA').resize((a['logical_width'],a['logical_height']),Image.Resampling.LANCZOS)
                composite.alpha_composite(png,(round(nx*2048-a['logical_width']/2),round(ny*2048-a['logical_height']/2)))
            draw=ImageDraw.Draw(composite)
            for kind,target,shape_id,nx,ny,sid in targets:
                x,y=nx*2048,ny*2048
                draw.line((x-3,y,x+3,y),fill='yellow');draw.line((x,y-3,x,y+3),fill='yellow')
                if kind != 2:draw.text((x,y-12),sid+' / '+target,fill='yellow',stroke_width=1,stroke_fill='black')
            composite.convert('RGB').save(REPORT / f'{map_name}-{state}-overlay.jpg',quality=95)
    assert len(slot_rows) == 142
    for name, headers, rows in [
        ('槽位变更.csv',['地图ID','素材槽ID','规则槽ID','源预制体','槽根Transform文件ID','旧X2048','旧Y2048','新X2048','新Y2048','归一化X','归一化Y左上','相对偏移X','相对偏移Y'],slot_rows),
        ('目标对应.csv',['地图ID','类别','素材ID','规则ID','槽数','区域','连接地点','X2048','Y2048','形状'],target_rows)]:
        with (REPORT/name).open('w',newline='',encoding='utf-8-sig') as f:
            w=csv.writer(f);w.writerow(headers);w.writerows(rows)
    (REPORT/'底图配准.json').write_text(json.dumps(registration,ensure_ascii=False,indent=2),encoding='utf-8')
    meta(DEST, 'DefaultImporter')
    print('已导入 8 张 4× 贴图、82 个资源/航道按钮、142 个槽位按钮；修改两份源预制体与两份布局。')


if __name__ == '__main__':
    main()
