"""核对驱动中断后的自然自动档续局；不把未完成原局改判为通过。"""
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
    raise ValueError('原局、续局和输出必须位于 TestResults 的明确子目录。')


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def lines(path):
    if not path.exists():
        return []
    return [json.loads(line) for line in path.read_text(encoding='utf-8-sig').splitlines() if line.strip()]


def unique(items, key):
    indexed = {value[key]: value for value in items}
    if len(indexed) != len(items):
        raise ValueError('规则状态存在重复身份：' + key)
    return indexed


metadata = read(resume / '续局夹具来源.json')
save_path = resume / '原自动存档副本.json'
save_bytes = save_path.read_bytes()
original = source / '联机隔离存档/host/auto-1.json'
if (not metadata.get('sourceFailed') or hashlib.sha256(save_bytes).hexdigest() != metadata['saveSha256'] or
        hashlib.sha256(original.read_bytes()).hexdigest() != metadata['saveSha256']):
    raise ValueError('未完成原局标记或自然存档原件哈希不符。')
failure = read(source / '四端正常联机/首个实例失败.json')
if 'DriverContinuationPendingException' not in failure['Error']:
    raise ValueError('原局首败不是预期的驱动待接。')
save = json.loads(json.loads(save_bytes.decode('utf-8-sig'))['Payload'])
snapshot = json.loads(save['ArchiveJson'])['Snapshot']
before = snapshot['State']
baseline = json.loads(save['SimulationCheckpointJson'])['Baseline']
game, content, initial = before['GameId'], before['ContentPackHash'], baseline['ConfirmedGlobalSequence']
if (game != metadata['gameId'] or content != metadata['contentHash'] or initial != metadata['confirmedSequence'] or
        snapshot['StateRevision'] != metadata['stateRevision'] or before['Round'] != metadata['round']):
    raise ValueError('原自动档身份或确认边界不符。')
cohort = resume / '四端正常联机'
exits = read(cohort / '实际退出记录.json')
roles, tails, boundaries, starts, assemblies = {}, {}, {}, {}, set()
for role in ('host', 'client2', 'client3', 'client4'):
    folder = cohort / role
    identity = read(folder / '联机运行身份.json')
    run = read(folder / '运行身份.json')
    start = read(folder / '正常原档续局初始边界.json')
    report = read(folder / '验证结果.json')
    tail = read(folder / '联机终局原回执清单.json')
    boundary = read(folder / '联机终局确认边界.json')
    assemblies.add(run['Assembly'])
    if (identity['GameId'] != game or identity['ContentPackHash'] != content or
            start['GameId'] != game or start['ContentPackHash'] != content or
            start['Revision'] != snapshot['StateRevision'] or start['Boundary']['GlobalFenceSequence'] != initial or
            start['Session']['CheckpointGlobalSequence'] != initial or not start['ExistingCompletionIsNotNewCoverage'] or
            not report['Passed'] or not report['FullGameCompleted'] or report['DeferredContentDifferences'] != 0 or
            report['RuntimeErrors'] or exits[role]['ExitCode'] != 0 or not exits[role]['NaturalExit']):
        raise ValueError('续局身份、首边界、正常结果或退出不符：' + role)
    if (tail['GameId'] != game or tail['ContentHash'] != content or tail['InitialSequence'] != initial or
            not tail['Complete'] or not tail['OriginalPayloadsValid'] or not tail['CompetitionContinuationsComplete'] or
            tail['MissingSequences'] or tail['DuplicateSequences'] or tail['UnresolvedOriginalCommands'] or
            tail['UnexpectedRejectedCommands'] or tail['BusinessRejectedCommands'] or tail['FinalSequence'] != boundary['GlobalFenceSequence']):
        raise ValueError('续局原回执清单不完整：' + role)
    applied = [row['Applied'] for row in lines(folder / '四端全局应用回执.jsonl')]
    if applied != tail['Receipts'] or [row['GlobalCommandSequence'] for row in applied] != list(range(initial + 1, tail['FinalSequence'] + 1)):
        raise ValueError('续局全局原回执未从原确认前缀连续开始：' + role)
    roles[role] = {'player': identity['LocalPlayer'], 'startRevision': start['Revision'], 'initialSequence': initial,
                   'finalSequence': tail['FinalSequence'], 'digest': boundary['RuleStateDigest'], 'naturalExit': True}
    tails[role], boundaries[role], starts[role] = applied, boundary, start['Boundary']
if ({row['player'] for row in roles.values()} != {1, 2, 3, 4} or len(assemblies) != 1 or
        any(tails[role] != tails['host'] or boundaries[role] != boundaries['host'] or starts[role] != starts['host'] for role in roles)):
    raise ValueError('四端身份、程序集、原回执或边界不一致。')
after_path = cohort / 'host/联机正式终局规则状态.json'
after = read(after_path)
if (after['GameId'] != game or after['ContentPackHash'] != content or after['Round'] != 8 or
        not after['FinalScoring']['IsResolved'] or
        after['RuleConfiguration']['RuleModeId'] != before['RuleConfiguration']['RuleModeId'] or
        after['RuleConfiguration']['LockedContentPacks'] != before['RuleConfiguration']['LockedContentPacks']):
    raise ValueError('续局模式、包锁或正式终局不符。')
old_runtime, new_runtime = before['EffectRuntime'], after['EffectRuntime']
for collection, key in (('DispatchReceipts', 'ReceiptId'), ('RuleEvents', 'EventId')):
    old, new = unique(old_runtime[collection], key), unique(new_runtime[collection], key)
    if any(new.get(identity) != value for identity, value in old.items()):
        raise ValueError('续局改变了原档已有的事件或回执：' + collection)
old_nodes = unique(old_runtime['EffectNodes'], 'EffectId')
new_nodes = unique(new_runtime['EffectNodes'], 'EffectId')


def children(nodes):
    result = collections.defaultdict(set)
    for node in nodes.values():
        result[node.get('ParentEffectId')].add(node['EffectId'])
    return result


def closure(root, mapping):
    seen, pending = set(), [root]
    while pending:
        identity = pending.pop()
        if identity not in seen:
            seen.add(identity)
            pending.extend(mapping[identity])
    return seen


old_children, new_children = children(old_nodes), children(new_nodes)
event_roots = [node['EffectId'] for node in old_nodes.values()
               if node['EffectTypeId'] == 'effect.event_card.resolve' and node['Status'] == 4]
for root in event_roots:
    old_tree, new_tree = closure(root, old_children), closure(root, new_children)
    if old_tree != new_tree or any(old_nodes[item] != new_nodes.get(item) for item in old_tree):
        raise ValueError('续局改变或重复挂接原档已完成的事件效果树：' + root)

verified_fallbacks = []
for role in roles:
    folder = cohort / role
    decisions = lines(folder / '决策变化.jsonl')
    controls = lines(folder / '控件事件.jsonl')
    receipts = lines(folder / '命令与准确回执.jsonl')
    for row in lines(folder / '强制影响力例外.jsonl'):
        if row.get('Reason') != '必答移动的常规策略无可见目标；从原请求及实际高亮的合法目标继续。':
            continue
        request = row['OriginalRequest']
        candidates = [value for value in request['CandidateIds'] if value == 'move|' + row['SourceId'] + '|' + row['SelectedSlot']]
        if (len(candidates) != 1 or row['SelectedSlot'] not in row['VisibleLegalSlots'] or
                not request['VisibleToViewer'] or request['AllowDecline'] or request['MinSelections'] < 1):
            raise ValueError('强制移动不属于本人原请求与可见合法高亮。')
        matches = [item for item in decisions if item.get('Action') == '角色移动原来源与目标两阶段完成' and
                   item.get('Round') == row['Round'] and item.get('PlayerId') == row['PlayerId'] and
                   item.get('SourceId') == row['SourceId'] and item.get('TargetSlot') == row['SelectedSlot'] and
                   item.get('Candidate') == candidates[0]]
        if len(matches) != 1:
            raise ValueError('强制移动没有唯一实际完成决策。')
        command_id = matches[0]['CommandId']
        local = [item for item in receipts if item.get('Command', {}).get('CommandId') == command_id]
        if (len(local) != 1 or not local[0].get('Result', {}).get('AppliedLocally') or
                not local[0].get('Result', {}).get('CommandResult', {}).get('Succeeded') or
                local[0]['Command']['OptionIds'] != candidates):
            raise ValueError('强制移动缺少准确成功回执。')
        clicks = [item for item in controls if item.get('Stage') == local[0]['Stage'] and item.get('Frame', 0) <= local[0]['Frame']]
        if not any(item.get('Description') == '角色移动目标原首击' for item in clicks) or not any(
                item.get('Description') == '角色移动目标原二击确认' for item in clicks):
            raise ValueError('强制移动缺少原地图目标的两次点击。')
        applied = [item for item in tails['host'] if item['Command']['CommandId'] == command_id]
        if len(applied) != 1 or not applied[0]['RuleResult']['Succeeded']:
            raise ValueError('强制移动缺少四端共同成功回执。')
        verified_fallbacks.append({'role': role, 'player': row['PlayerId'], 'sourceItemId': row['SourceId'],
                                   'targetSlot': row['SelectedSlot'], 'commandId': command_id,
                                   'globalSequence': applied[0]['GlobalCommandSequence']})
result = {'passed': True, 'gameId': game, 'contentHash': content, 'resumeAssembly': next(iter(assemblies)),
          'sourceRevision': snapshot['StateRevision'], 'initialSequence': initial, 'finalSequence': boundaries['host']['GlobalFenceSequence'],
          'oldEventTreesPreserved': len(event_roots), 'oldReceiptsPreserved': len(old_runtime['DispatchReceipts']),
          'oldRuleEventsPreserved': len(old_runtime['RuleEvents']), 'sourceSaveSha256': metadata['saveSha256'],
          'finalStateSha256': hashlib.sha256(after_path.read_bytes()).hexdigest(), 'verifiedMandatoryMoveFallbacks': verified_fallbacks,
          'roles': roles, 'scope': '从驱动误判中断的旧局自然自动档独立续局；原失败不改判，旧程序集覆盖不与新程序集混算。'}
with output.open('x', encoding='utf-8') as stream:
    json.dump(result, stream, ensure_ascii=False, indent=2)
print('自然档续局核验通过：', initial, '→', result['finalSequence'], '；原事件树', len(event_roots), '；必答移动', len(verified_fallbacks))
