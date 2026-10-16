"""本次显式修改菜单入口及房间顶栏；只编辑已有资产，不在测试/运行时调用。"""
import hashlib
import json
import re
from pathlib import Path

root = Path(__file__).resolve().parents[2]
prefab = root / 'Assets/YC/Presentation/Prefabs/StartMenu/StartMenuRoot.prefab'
scene = root / 'Assets/Scenes/StartScene.unity'
evidence = root / 'Logs/ExpansionValidation/RoomEntry/asset-source'
evidence.mkdir(parents=True, exist_ok=True)
source = prefab.read_text(encoding='utf-8')
scene_source = scene.read_text(encoding='utf-8')
pattern = r'^--- !u!(\d+) &(\d+)\n.*?(?=^--- !u!|\Z)'
blocks = {m[2]: m[0] for m in re.finditer(pattern, source, re.M | re.S)}
types = {m[2]: m[1] for m in re.finditer(pattern, source, re.M | re.S)}
online_id = '948750624690520550'
if online_id not in blocks:
    raise SystemExit('旧中间页已移除；请直接对当前资产增量编辑，不重复执行此工具。')

for path, text in [(prefab, source), (scene, scene_source)]:
    (evidence / (path.name + '.before')).write_text(text, encoding='utf-8')
names = {k: re.search(r'^  m_Name: (.*)$', v, re.M)[1] for k, v in blocks.items() if types[k] == '1'}
rect_go = {k: re.search(r'm_GameObject: \{fileID: (\d+)\}', v)[1] for k, v in blocks.items() if types[k] in ['4', '224']}
go_rect = {v: k for k, v in rect_go.items()}
parents = {k: re.search(r'm_Father: \{fileID: (\d+)\}', blocks[k])[1] for k in rect_go}

def hierarchy(go):
    result, rect = [], go_rect[go]
    while rect != '0':
        result.append(names[rect_go[rect]])
        rect = parents[rect]
    return '/'.join(reversed(result))

prefix = hierarchy(online_id)
removed_go = {k for k in names if hierarchy(k) == prefix or hierarchy(k).startswith(prefix + '/')}
removed = set(removed_go)
for key, block in blocks.items():
    go = re.search(r'm_GameObject: \{fileID: (\d+)\}', block)
    if go and go[1] in removed_go:
        removed.add(key)
targets = [str(prefab.resolve()) + ' :: ' + hierarchy(go) for go in sorted(removed_go)]
assert all('Online Mode Panel' in target and str(root.resolve()) in target for target in targets)
print(json.dumps({'本次移除对象的资产绝对路径及层级': targets}, ensure_ascii=False))
# 仅移除已列明中间页的Unity对象块及父级孩子引用，不删除任何文件或目录。
for key in removed:
    del blocks[key]
for key in blocks:
    blocks[key] = re.sub(r'^  - \{fileID: (\d+)\}\n',
        lambda m: '' if m[1] in removed else m[0], blocks[key], flags=re.M)
    blocks[key] = re.sub(r'^  onlineModePanel:.*\n', '', blocks[key], flags=re.M)

for ident, caption in [('5751324575589917250', '创建房间'), ('5070715762121237602', '加入房间'), ('262607955441658797', '打开联机')]:
    blocks[ident] = re.sub(r'^  m_Text:.*$', lambda _: '  m_Text: ' + json.dumps(caption, ensure_ascii=True), blocks[ident], flags=re.M)
room_view = '536233927053355161'
fields = {'networkingCaption': '{fileID: 262607955441658797}', 'enableNetworkingCaption': '打开联机',
          'disableNetworkingCaption': '关闭联机', 'offlineRoomCaption': '本地房间', 'localHostCaption': '本机房主'}
for key, value in fields.items():
    blocks[room_view] += '  ' + key + ': ' + (value if key == 'networkingCaption' else json.dumps(value, ensure_ascii=True)) + '\n'
join_view = '4703110894835871779'
for key, value in {'steamDescription': '输入房主显示的 Lobby 房间码', 'steamPlaceholder': '请输入 Steam Lobby ID',
                   'localDescription': '输入房主显示的本地地址（IP:端口）', 'localPlaceholder': '例如 127.0.0.1:7780'}.items():
    blocks[join_view] += '  ' + key + ': ' + json.dumps(value, ensure_ascii=True) + '\n'

def update_override(match):
    if match[1] in removed:
        return ''
    captions = {'5751324575589917250': '创建房间', '5070715762121237602': '加入房间'}
    if match[1] in captions and 'propertyPath: m_Text\n' in match[0]:
        return re.sub(r'^      value:.*$', lambda _: '      value: ' + json.dumps(captions[match[1]], ensure_ascii=True), match[0], flags=re.M)
    return match[0]

scene_updated = re.sub(r'^    - target: \{fileID: (\d+), guid: a6c5387646ec56f43babb66b8d1b68bd, type: 3\}\n.*?(?=^    - target:|^    m_Added|^    m_Removed|^  m_SourcePrefab:)', update_override, scene_source, flags=re.M | re.S)
updated = source[:source.index('--- !u!')] + ''.join(blocks.values())
missing = [i for i in re.findall(r'\{fileID: (\d+)\}', updated) if i != '0' and i not in blocks]
assert not missing, missing
prefab.write_text(updated, encoding='utf-8', newline='\n')
scene.write_text(scene_updated, encoding='utf-8', newline='\n')
audit = {'asset': str(prefab.resolve()), 'scene': str(scene.resolve()), 'removedHierarchy': targets,
         'removedBlockIds': sorted(removed), 'danglingLocalReferences': missing,
         'prefabSHA256': hashlib.sha256(prefab.read_bytes()).hexdigest(), 'sceneSHA256': hashlib.sha256(scene.read_bytes()).hexdigest()}
(evidence / '入口资产修改.json').write_text(json.dumps(audit, ensure_ascii=False, indent=2), encoding='utf-8')
print('已更新菜单源Prefab、场景按钮文案覆盖和房间联机开关引用。')
