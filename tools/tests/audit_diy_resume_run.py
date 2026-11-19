"""按原存档确认前缀核验隔离四端续局，不把续局回执当作从 1 开始的新局。"""
import argparse
import collections
import hashlib
import json
from pathlib import Path


project = Path(__file__).resolve().parents[2]
allowed = (project / 'TestResults').resolve()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source', type=Path, required=True)
parser.add_argument('--resume', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
source, resume, output = args.source.resolve(strict=True), args.resume.resolve(strict=True), args.output.resolve()
if any(path == allowed or not path.is_relative_to(allowed) for path in (source, resume, output)):
    raise ValueError('原局、续局及输出必须位于项目 TestResults 的明确子目录。')


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def unique_index(items, key):
    index = {value[key]: value for value in items}
    if len(index) != len(items):
        raise ValueError('正式规则状态存在重复身份：' + key)
    return index


metadata = read(resume / '续局夹具来源.json')
save_path = resume / '原自动存档副本.json'
save_bytes = save_path.read_bytes()
if hashlib.sha256(save_bytes).hexdigest() != metadata['saveSha256']:
    raise ValueError('原自动存档副本哈希发生变化。')
original_path = source / '联机隔离存档/host/auto-1.json'
if hashlib.sha256(original_path.read_bytes()).hexdigest() != metadata['saveSha256']:
    raise ValueError('原自动存档与续局输入不一致。')
save = json.loads(json.loads(save_bytes.decode('utf-8-sig'))['Payload'])
snapshot = json.loads(save['ArchiveJson'])['Snapshot']
before = snapshot['State']
checkpoint = json.loads(save['SimulationCheckpointJson'])['Baseline']
game_id, content_hash = before['GameId'], before['ContentPackHash']
initial = checkpoint['ConfirmedGlobalSequence']
if (game_id != metadata['gameId'] or content_hash != metadata['contentHash'] or
        initial != metadata['confirmedSequence'] or snapshot['StateRevision'] != metadata['stateRevision'] or
        not read(source / '四端正常联机/四端整局验收汇总.json')['本局通过']):
    raise ValueError('原局或存档确认边界不符。')

cohort = resume / '四端正常联机'
exits = read(cohort / '实际退出记录.json')
roles, tails, boundaries, initial_boundaries = {}, {}, {}, {}
for role in ('host', 'client2', 'client3', 'client4'):
    directory = cohort / role
    identity = read(directory / '联机运行身份.json')
    start = read(directory / '正常原档续局初始边界.json')
    report = read(directory / '验证结果.json')
    tail = read(directory / '联机终局原回执清单.json')
    boundary = read(directory / '联机终局确认边界.json')
    if (identity['GameId'] != game_id or identity['ContentPackHash'] != content_hash or
            start['GameId'] != game_id or start['ContentPackHash'] != content_hash or
            start['Revision'] != snapshot['StateRevision'] or start['Boundary']['GlobalFenceSequence'] != initial or
            start['Session']['CheckpointGlobalSequence'] != initial or not start['ExistingCompletionIsNotNewCoverage'] or
            not report['Passed'] or not report['FullGameCompleted'] or report['DeferredContentDifferences'] != 0 or
            exits[role]['ExitCode'] != 0 or not exits[role]['NaturalExit']):
        raise ValueError('续局身份、原确认边界、正常结果或退出不符：' + role)
    if (tail['GameId'] != game_id or tail['ContentHash'] != content_hash or tail['InitialSequence'] != initial or
            not tail['Complete'] or not tail['OriginalPayloadsValid'] or tail['MissingSequences'] or tail['DuplicateSequences'] or
            tail['UnresolvedOriginalCommands'] or tail['UnexpectedRejectedCommands'] or tail['BusinessRejectedCommands'] or
            tail['FinalSequence'] != boundary['GlobalFenceSequence']):
        raise ValueError('续局原始回执不完整：' + role)
    sequences = [item['GlobalCommandSequence'] for item in tail['Receipts']]
    if sequences != list(range(initial + 1, tail['FinalSequence'] + 1)):
        raise ValueError('续局回执没有从原确认前缀连续开始：' + role)
    roles[role] = {'player': identity['LocalPlayer'], 'startRevision': start['Revision'],
                   'initialSequence': initial, 'finalSequence': tail['FinalSequence'],
                   'digest': boundary['RuleStateDigest'], 'naturalExit': True}
    tails[role] = tail['Receipts']
    boundaries[role] = boundary
    initial_boundaries[role] = start['Boundary']
if ({value['player'] for value in roles.values()} != {1, 2, 3, 4} or
        any(tails[role] != tails['host'] or boundaries[role] != boundaries['host'] or
            initial_boundaries[role] != initial_boundaries['host'] for role in roles)):
    raise ValueError('四端玩家、原回执或确认边界不一致。')

after_path = cohort / 'host/联机正式终局规则状态.json'
after = read(after_path)
source_final = read(source / '四端正常联机/host/联机正式终局规则状态.json')
if (after['GameId'] != game_id or after['ContentPackHash'] != content_hash or
        after['RuleConfiguration']['RuleModeId'] != before['RuleConfiguration']['RuleModeId'] or
        after['RuleConfiguration']['LockedContentPacks'] != before['RuleConfiguration']['LockedContentPacks'] or
        not after['FinalScoring']['IsResolved'] or after['FinalScoring'] != source_final['FinalScoring'] or
        [(p['PlayerId'], p['Score']) for p in after['Players']] != [(p['PlayerId'], p['Score']) for p in source_final['Players']]):
    raise ValueError('续局模式、包锁或终局计分与原局不符。')

old_runtime, new_runtime = before['EffectRuntime'], after['EffectRuntime']
for collection, key in (('DispatchReceipts', 'ReceiptId'), ('RuleEvents', 'EventId')):
    old, new = unique_index(old_runtime[collection], key), unique_index(new_runtime[collection], key)
    if any(new.get(identity) != value for identity, value in old.items()):
        raise ValueError('续局改变了原档已有的事件或回执：' + collection)
old_nodes = unique_index(old_runtime['EffectNodes'], 'EffectId')
new_nodes = unique_index(new_runtime['EffectNodes'], 'EffectId')


def children(nodes):
    result = collections.defaultdict(set)
    for node in nodes.values():
        result[node.get('ParentEffectId')].add(node['EffectId'])
    return result


def closure(root, mapping):
    seen, pending = set(), [root]
    while pending:
        identity = pending.pop()
        if identity in seen:
            continue
        seen.add(identity)
        pending.extend(mapping[identity])
    return seen


old_children, new_children = children(old_nodes), children(new_nodes)
event_roots = [node['EffectId'] for node in old_nodes.values()
               if node['EffectTypeId'] == 'effect.event_card.resolve' and node['Status'] == 4]
for root in event_roots:
    old_tree, new_tree = closure(root, old_children), closure(root, new_children)
    if old_tree != new_tree or any(old_nodes[item] != new_nodes.get(item) for item in old_tree):
        raise ValueError('续局改变或重复挂接了原档已完成的事件效果树：' + root)

result = {'passed': True, 'gameId': game_id, 'contentHash': content_hash, 'sourceRevision': snapshot['StateRevision'],
          'initialSequence': initial, 'tailSequences': [r['GlobalCommandSequence'] for r in tails['host']],
          'oldEventTreesPreserved': len(event_roots), 'oldReceiptsPreserved': len(old_runtime['DispatchReceipts']),
          'oldRuleEventsPreserved': len(old_runtime['RuleEvents']), 'sameFinalScoringAsOriginal': True,
          'sourceSaveSha256': metadata['saveSha256'], 'finalStateSha256': hashlib.sha256(after_path.read_bytes()).hexdigest(),
          'roles': roles, 'scope': '同一正常局的第八回合收尾自动存档；旧事件树不计新覆盖，未覆盖事件请求中途恢复。',
          'legacySummary': '原整局汇总器按从 1 开始的全局序号误报不连续，原报告保留；本审计从原确认前缀核对续接。'}
with output.open('x', encoding='utf-8') as stream:
    json.dump(result, stream, ensure_ascii=False, indent=2)
print(f"原档续局通过：确认序号 {initial} → {roles['host']['finalSequence']}，保留 {len(event_roots)} 个已完成事件效果树。")
