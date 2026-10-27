"""只读核验规划结构、来源基线与追溯完整性；不执行Unity、不验证游戏实现。

用法：python -X utf8 核验规划.py
输出JSON到标准输出。此脚本不改源文、不写存档、不读取受限UI原档案。
"""
from pathlib import Path
import collections
import hashlib
import json
import re
import sys

BASE = Path(__file__).resolve().parents[1]
PROJECT = BASE.parents[1]
errors = []
checks = []

def check(ok, message):
    checks.append({'检查': message, '通过': bool(ok)})
    if not ok:
        errors.append(message)

def read_json(name):
    return json.loads((BASE / name).read_text(encoding='utf-8'))

def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()

def safe_source(p):
    p = Path(p).resolve()
    forbidden = (PROJECT / '游城拓荒/UI素材').resolve()
    if p == forbidden or forbidden in p.parents:
        raise ValueError('拒绝读取受限UI原档案')
    if PROJECT.resolve() not in p.parents:
        raise ValueError('来源必须在当前项目内')
    return p

manifest = read_json('核验/来源基线.json')
records = read_json('覆盖/全部来源条目.json')
tasks = read_json('任务定义.json')
cards = read_json('覆盖/卡面映射.json')
cross = read_json('覆盖/交叉映射.json')
ids = {t['id'] for t in tasks}
check(len(tasks) == len(ids) == 19, '19个唯一任务')
check(len(records) == len({r['id'] for r in records}), '来源条目ID唯一')
check(all(r['primary_task'] in ids and r['primary_task'] in r['tasks'] and set(r['tasks']) <= ids for r in records), '每条有唯一合法主责和合法关联任务')
check(all(r['implementation_status'] == '未实施' and r['verification_status'] == '未运行验收' for r in records), '规划条目未冒充实施或验收通过')
check(all(t['deps'] and set(t['deps']) <= ids if t['id'] != 'QY-001' else t['deps'] == [] for t in tasks), '前置任务引用有效')

graph = {t['id']: t['deps'] for t in tasks}
visiting, done = set(), set()
def visit(n):
    if n in visiting:
        raise ValueError('依赖存在环：' + n)
    if n in done:
        return
    visiting.add(n)
    for dep in graph[n]:
        visit(dep)
    visiting.remove(n)
    done.add(n)
try:
    for n in graph:
        visit(n)
    check(True, '任务依赖无环')
except ValueError as e:
    check(False, str(e))

per_source = []
for src in manifest['sources']:
    p = safe_source(src['path'])
    snap = BASE / '依据快照' / src['name']
    check(sha(p) == src['sha256'] == sha(snap), src['name'] + '：当前原文／快照／登记基线哈希一致')
    lines = snap.read_text(encoding='utf-8-sig').splitlines()
    rs = [r for r in records if r['source'] == src['name']]
    covered = set(src['structural_lines'])
    body_lines = set()
    for r in rs:
        excerpt = '\n'.join(lines[r['start_line'] - 1:r['end_line']])
        if excerpt != r['excerpt'] or hashlib.sha256(excerpt.encode()).hexdigest() != r['excerpt_sha256']:
            errors.append('原文摘录不一致：' + r['id'])
        ran = set(range(r['start_line'], r['end_line'] + 1))
        if ran & body_lines:
            errors.append('来源行重复切分：' + r['id'])
        body_lines |= ran
        covered |= ran
    meaningful = {i for i,l in enumerate(lines,1) if l.strip()}
    missing = sorted(meaningful - covered)
    check(not missing, src['name'] + '：全部非空原文行均有正文条目或结构记录')
    check(len(rs) == src['record_count'], src['name'] + '：记录数量一致')
    per_source.append({'文档':src['name'],'来源条目':len(rs),'非空行':len(meaningful),'未覆盖行':missing})

declared = dict(re.findall(r'^([ADPMT]\d{2})\s+([0-9a-f]{64})\s*$',
                          (BASE/'依据快照/素材清单.md').read_text(encoding='utf-8-sig'),re.M))
expected = {f'{p}{i:02}' for p,n in [('A',9),('D',9),('P',9),('M',13),('T',9)] for i in range(1,n+1)}
check(len(cards)==49 and {c['id'] for c in cards} == expected, '49张来源号完整且唯一')
check(set(declared) == expected, '素材清单当前49个哈希与来源号对齐')
card_bad=[]
for c in cards:
    path = safe_source(c['path'])
    if sha(path) != c['sha256'] or c['sha256'] != declared.get(c['id']):
        card_bad.append(c['id'])
    if c['task'] not in ids:
        card_bad.append(c['id']+'任务无效')
check(not card_bad, '49卡图实际字节／基线／素材清单哈希一致且有内容主责')
active_images={c['id']:c for c in manifest['images']}
check(all(c['path']==active_images[c['id']]['path'] and c['sha256']==active_images[c['id']]['sha256'] for c in cards),
      '卡面映射与来源基线的活动图片引用一致')
asset_revision=read_json('核验/A05贴图更新记录.json')
updated_image=asset_revision['current']
check(active_images['A05']['path']==updated_image['path'] and
      active_images['A05']['sha256']==updated_image['sha256'] and
      '支付1指令' in updated_image['rule'], 'A05采用已目视核对的1指令更新贴图')
old_catalog=safe_source(asset_revision['catalog_update']['previous_snapshot'])
check(sha(old_catalog)==asset_revision['catalog_update']['previous_sha256'], 'A05更新前素材清单快照可追溯')
new_catalog=safe_source(asset_revision['catalog_update']['path'])
check(sha(new_catalog)==asset_revision['catalog_update']['current_sha256'], 'A05更新后素材清单哈希符合变更记录')
old_lines=old_catalog.read_text(encoding='utf-8-sig').splitlines()
new_lines=new_catalog.read_text(encoding='utf-8-sig').splitlines()
changed_lines=[i for i,(a,b) in enumerate(zip(old_lines,new_lines),1) if a!=b]
check(len(old_lines)==len(new_lines) and changed_lines==asset_revision['catalog_update']['changed_lines']==[3,27,104],
      '素材清单仅更改更新说明、A05文件引用与哈希三行')
rule=manifest['rulebook']
check(sha(safe_source(rule['path']))==rule['sha256'], '规则书哈希保持基线')

round_cards=[c for c in cards if re.match(r'^[1357][AB] ',c['label'])]
abilities=[c for c in cards if c['label'].startswith('能力牌')]
check(len(round_cards)==40 and len(abilities)==5, '40面及5能力清点；其余4图为M01与三色设施')
for prefix in 'ADPMT':
    check({c['label'].split(' ')[0] for c in round_cards if c['id'].startswith(prefix)} == {f'{r}{s}' for r in [1,3,5,7] for s in 'AB'}, prefix+'：四回合A/B面完整')

for family,expected_keys in [
    ('页面',[f'U{i:02}' for i in range(1,23)]),
    ('接口',[f'L{i:02}' for i in range(1,12)]),
    ('技术提案',[f'技术提案{i:02}' for i in range(1,13)]),
    ('J-Q',[f'J-Q{i:02}' for i in range(1,11)]),
    ('差异',[f'RK-{i:02}' for i in range(1,6)])]:
    check(all(k in cross and cross[k] and set(cross[k]) <= ids for k in expected_keys), family+'全部有合法承接任务')

prompt_files=list((BASE/'执行者提示词').glob('*.md'))
check(len(prompt_files)==19, '19份执行者提示词文件')
for t in tasks:
    p=BASE/'执行者提示词'/f'{t["id"]}-{t["title"]}.md'
    body=p.read_text(encoding='utf-8')
    req=BASE/'任务要求'/f'{t["id"]}.md'
    own=[r['id'] for r in records if r['primary_task']==t['id']]
    check(bool(own),t['id']+'有明确主责原文')
    check(req.exists() and all(rid in req.read_text(encoding='utf-8') for rid in own),t['id']+'责任索引包含全部主责条目')
    check('UI不得超出文档规定的页面链路' in body and '必须征询用户意见' in body and
          '游戏C#内核不实现企业家、角色牌卡面效果' in body and 'Lua落实' in body,
          t['id']+'完整包含用户两条硬约束')
    check(all(h in body for h in ['前置：','必读：','## 要完成的工作','## 验收条件','## 交付']),t['id']+'可单独派发结构完整')

# Current human rulings supersede conflicting historical excerpts, which remain
# byte-identical evidence and are deliberately not rewritten to look original.
rulings = read_json('核验/用户裁定.json')
decisions = {x['id']: x for x in rulings['items']}
check(set(decisions) == {f'RK-{i:02}' for i in range(1,6)} and
      all(x['status'] == '已确认' for x in decisions.values()), 'RK-01—05均有当前用户直接确认记录')
check('支付1指令' in decisions['RK-01']['effective_rule'] and
      '快速入口仍支付2指令' in decisions['RK-01']['effective_rule'], 'A05收尾1指令与战术快速入口2指令区分')
check('按卡面解决' in decisions['RK-02']['effective_rule'], 'RK-02保留按卡面解决')
check('U20' in decisions['RK-03']['effective_rule'] and
      '落位' in decisions['RK-03']['effective_rule'] and
      '再执行原入场' in decisions['RK-03']['effective_rule'], 'RK-03采用U20时序')
check('自动取最大格' in decisions['RK-04']['effective_rule'] and
      '不弹手动选格页' in decisions['RK-04']['effective_rule'], 'RK-04自动计分且不增加手选链路')
check('实际效果等价' in decisions['RK-05']['effective_rule'], 'RK-05以实际效果等价核验')
fee_ids = {'S1-0225','S1-0294','S1-0399','S1-0401','S2-0039','S2-0056','S2-0066','S3-0041','S3-0091','S3-0092'}
fee_records = [r for r in records if r['id'] in fee_ids]
check(len(fee_records) == len(fee_ids) and
      all('支付1指令' in r.get('effective_requirement','') and 'RK-01' in r.get('ruling_ids',[]) for r in fee_records),
      '10条A05历史费用要求均标明当前1指令更正，摘录仍保留原字节')
check(all('已确认' in r['planning_status'] for r in records if r.get('ruling_ids')), '已裁定来源条目不再标为待确认')
check(all('这五项不再重复确认' in p.read_text(encoding='utf-8') for p in prompt_files), '19份提示词同步用户裁定并停止重复确认')
task_text = json.dumps(tasks,ensure_ascii=False)
obsolete = ['A05费用为2','5B按卡面收尾一次付2','获准自动才','M13时序有权威依据后再验','执行前处理01中的M13时序门槛']
check(not any(s in task_text for s in obsolete), '任务定义不残留已撤销费用／马丁审批门槛')
check(all(not any(s in p.read_text(encoding='utf-8') for s in obsolete) for p in prompt_files), '提示词不残留已撤销费用／马丁审批门槛')

# Only navigation documents are checked here. Source snapshots and verbatim excerpts
# deliberately preserve original relative links; their hash, not relocated navigation, is the invariant.
nav_files=list(BASE.glob('*.md'))+prompt_files+list((BASE/'任务要求').glob('*.md'))+[
    BASE/'覆盖/49张卡面映射.md', BASE/'覆盖/交叉核账.md']
bad_links=[]; checked_links=0
for p in nav_files:
    body=p.read_text(encoding='utf-8')
    for m in re.finditer(r'\[[^\]]+\]\((?:<([^>]+)>|([^\)]+))\)',body):
        dest=m.group(1) or m.group(2)
        if re.match(r'^[A-Za-z]:/',dest):
            dest=re.sub(r':\d+$','',dest)
            checked_links+=1
            if not Path(dest).exists():
                bad_links.append({'文件':str(p),'链接':dest})
check(not bad_links, '生成的导航文件绝对路径链接全部存在')

result={'性质':'只验证规划覆盖、结构与证据基线；不代表语义自动正确、游戏实现完成或运行验收通过',
        '通过':not errors,'错误':errors,'失效链接':bad_links,'失配卡图':card_bad,
        '当前用户裁定日期':rulings['date'],
        '统计':{'源文件':len(manifest['sources']),'来源条目':len(records),'任务':len(tasks),'已确认RK项':len(decisions),
                '卡图':len(cards),'能力':len(abilities),'回合面':len(round_cards),'双面实体':20,
                '页面':22,'L编号':11,'技术提案':12,'J-Q编号':10,'导航链接':checked_links},
        '逐源结果':per_source,'检查':checks}
print(json.dumps(result,ensure_ascii=False,indent=2))
sys.exit(0 if result['通过'] else 1)
