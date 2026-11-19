"""核对本地正常局中自然事件待答原档的原界面续局与一次结算。"""
import argparse
import base64
import hashlib
import json
import struct
from pathlib import Path


project = Path(__file__).resolve().parents[2]
allowed = (project / 'TestResults').resolve()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source', type=Path, required=True)
parser.add_argument('--resume', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--ongoing', action='store_true', help='只核对尚在正常推进的续局中已完成的原事件，不宣称整局通过。')
parser.add_argument('--resume-save', type=Path, help='续局自动存档的只读保留副本；进行中核对必须明确提供。')
args = parser.parse_args()
source, resume, output = args.source.resolve(strict=True), args.resume.resolve(strict=True), args.output.resolve()
if any(path == allowed or not path.is_relative_to(allowed) for path in (source, resume, output)):
    raise ValueError('来源、续局和输出必须是 TestResults 内的明确子路径。')
if args.ongoing and not args.resume_save:
    raise ValueError('续局进行中必须提供已保留的自动存档副本，不能依赖会被覆盖的槽位。')
resume_save = args.resume_save.resolve(strict=True) if args.resume_save else resume / 'MatchSaves/auto-1.json'
if not resume_save.is_relative_to(resume):
    raise ValueError('续局存档必须在续局证据目录内。')


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def save(path):
    raw = path.read_bytes()
    envelope = json.loads(raw.decode('utf-8-sig'))
    checksum = base64.b64encode(hashlib.sha256(envelope['Payload'].encode('utf-8')).digest()).decode('ascii')
    if envelope.get('Format') != 'YC.MatchSave' or envelope.get('Checksum') != checksum:
        raise ValueError('自动存档校验失败：' + str(path))
    data = json.loads(envelope['Payload'])
    snapshot = json.loads(data['ArchiveJson'])['Snapshot']
    if data['Mode'] != 0 or data['LocalTestNetwork'] or data['GameId'] != snapshot['State']['GameId']:
        raise ValueError('不是身份一致的本地正常局自动存档。')
    return data, snapshot, hashlib.sha256(raw).hexdigest()


def prop(value, name, field='StringValue'):
    return next((v['Value'].get(field) for v in (value or {}).get('Properties', []) if v.get('Name') == name), None)


def index(items, key):
    result = {item[key]: item for item in items}
    if len(result) != len(items):
        raise ValueError('正式状态存在重复身份：' + key)
    return result


def candidate(card, choice):
    parts = ('event-choice', card, choice)
    data = struct.pack('>I', len(parts))
    for value in ('nmc.stable-id.v1', *parts):
        raw = value.encode('utf-8')
        data += b'\x01' + struct.pack('>I', len(raw)) + raw
    return 'event-choice_' + hashlib.sha256(data).hexdigest()


def tree(root, nodes):
    children = {}
    for node in nodes.values():
        children.setdefault(node.get('ParentEffectId'), []).append(node['EffectId'])
    pending, seen = [root], set()
    while pending:
        value = pending.pop()
        if value in seen:
            raise ValueError('效果树包含重复或环：' + value)
        seen.add(value)
        pending.extend(children.get(value, []))
    return seen


source_exit_path = source / '实际退出记录.json'
source_exit = read(source_exit_path) if source_exit_path.exists() else None
source_timeout = read(source / '终局退出超时首次观察.json') if not source_exit else None
source_report = read(source / 'game/验证结果.json')
resume_exit = read(resume / '实际退出记录.json') if not args.ongoing else None
observed = read(source / '事件待答原档观察.json')
source_after_info = read(source / '事件原局选择后观察.json')
metadata = read(resume / '续局原档身份.json')
boundary = read(resume / 'game/本地同局正式恢复初始边界.json')
report = read(resume / 'game/验证结果.json') if not args.ongoing else None
original, initial, digest = save(source / '事件待答原档.json')
_, source_after, source_after_digest = save(source / '事件原局选择后自动存档.json')
_, final, final_digest = save(resume_save)
if args.resume_save and final_digest != read(resume / '事件续局选择后观察.json')['sha256']:
    raise ValueError('续局存档副本与只读观察的哈希不一致。')
before, original_after, after = initial['State'], source_after['State'], final['State']
game, content = before['GameId'], before['ContentPackHash']
source_ok = bool(source_exit and source_exit['passed'] and source_exit['naturalExit']) or bool(
    source_timeout and source_timeout['Terminal'] and source_timeout['ProcessStillRunning'] and
    not source_timeout['ForcedTermination'] and source_timeout['SecondsSinceReport'] >= 300 and
    source_timeout['ReportSha256'] == hashlib.sha256((source / 'game/验证结果.json').read_bytes()).hexdigest())
resume_ok = args.ongoing or bool(resume_exit and resume_exit['passed'] and resume_exit['naturalExit'] and
                                 report['Passed'] and report['FullGameCompleted'] and report['DeferredContentDifferences'] == 0)
if (not source_ok or not source_report['Passed'] or not source_report['FullGameCompleted'] or
        source_report['DeferredContentDifferences'] != 0 or not resume_ok or
        digest != observed['saveSha256'] or digest != metadata['saveSha256'] or source_after_digest != source_after_info['sha256'] or
        metadata['source'] != str(source) or game != metadata['gameId'] or content != metadata['contentHash'] or
        initial['StateRevision'] != metadata['revision'] or
        boundary['GameId'] != game or boundary['ContentPackHash'] != content or boundary['Revision'] != initial['StateRevision'] or
        boundary['Players'] != 4 or boundary['CommandsSinceOriginalLoad'] != 0 or boundary['PriorCommandsReplayed'] or
        not boundary['RestoredThroughOriginalUi'] or not boundary['ExistingCompletionIsNotNewCoverage'] or
        boundary['CurrentAssembly'] != boundary['PreviousAssembly'] or
        any(value['GameId'] != game or value['ContentPackHash'] != content for value in (original_after, after))):
    raise ValueError('原档、构建、正常读取或整局结果不一致。')
request_id = observed['requests'][0]['InteractionId']
old_requests = index(before['EffectRuntime']['InteractionRequests'], 'InteractionId')
saved_request = old_requests[request_id]
loaded = boundary['Request']
if (saved_request['Status'] != 'open' or saved_request['InteractionTypeId'] != 'event_card.option' or
        any(saved_request.get(key) != loaded.get(key) for key in
            ('InteractionId', 'OwnerEffectId', 'AnsweringPlayerId', 'CandidateIds', 'CandidateSetVersion'))):
    raise ValueError('原界面读取后的待答请求或候选与原档不一致。')

old_nodes = index(before['EffectRuntime']['EffectNodes'], 'EffectId')
new_nodes = index(after['EffectRuntime']['EffectNodes'], 'EffectId')
root_id = saved_request['OwnerEffectId']
old_root, root = old_nodes[root_id], new_nodes[root_id]
resolved_request = index(after['EffectRuntime']['InteractionRequests'], 'InteractionId')[request_id]
source_resolved = index(original_after['EffectRuntime']['InteractionRequests'], 'InteractionId')[request_id]
card = prop(root['NormalizedResult'], 'cardId')
choice = prop(root['NormalizedResult'], 'selectedChoiceId')
selected = prop(root['NormalizedResult'], 'selectedOptionId')
if (old_root['Status'] != 3 or old_root['ChildEffectIds'] or root['Status'] != 4 or
        root['EffectTypeId'] != 'effect.event_card.resolve' or card != 'event_yellow_01' or
        choice not in ('choice_01', 'choice_02') or selected != candidate(card, choice) or
        selected not in saved_request['CandidateIds'] or resolved_request['Status'] != 'answered' or
        source_resolved['Status'] != 'answered' or
        not any(item.get('ReferenceId') == selected for item in resolved_request['NormalizedAnswer']['Items'])):
    raise ValueError('原事件请求没有以合法稳定选择身份完成。')

receipts = [json.loads(line) for line in (resume / 'game/命令与准确回执.jsonl').read_text(encoding='utf-8-sig').splitlines() if line.strip()]
answers = [row for row in receipts if row.get('Command', {}).get('Parameters', {}).get('interactionId') == request_id]
if (len(answers) != 1 or answers[0]['Command']['PlayerId'] != saved_request['AnsweringPlayerId'] or
        answers[0]['Command']['OptionIds'] != [selected] or not answers[0]['Result']['AppliedLocally'] or
        not answers[0]['Result']['CommandResult']['Succeeded']):
    raise ValueError('原请求的实际成功回执不是恰好一次。')
cards = {item['CardId']: item for item in before['ContentDefinitions']['Events']}
visual_index = next(i + 1 for i, item in enumerate(cards[card]['Choices']) if item['ChoiceId'] == choice)
controls = [json.loads(line) for line in (resume / 'game/控件事件.jsonl').read_text(encoding='utf-8-sig').splitlines() if line.strip()]
if not any(item.get('Stage') == answers[0]['Stage'] and item.get('Frame', 0) <= answers[0]['Frame'] and
           item.get('Description') == '正常随机事件首次选择' and item.get('Path', '').endswith('/Choice ' + str(visual_index))
           for item in controls):
    raise ValueError('原请求没有对应的真实卡面控件点击。')

child_ids = root['ChildEffectIds']
expected = [('effect.resource.gain', 'Originium', 5)] if choice == 'choice_01' else [
    ('effect.resource.gain', 'GoldVoucher', 4), ('effect.score.gain', None, 1)]
if len(child_ids) != len(expected) or len(set(child_ids)) != len(child_ids):
    raise ValueError('事件奖励子效果数量不符或重复。')
for child_id, (kind, resource, amount) in zip(child_ids, expected):
    child = new_nodes[child_id]
    result = child['NormalizedResult']
    if (child['ParentEffectId'] != root_id or child['EffectTypeId'] != kind or child['Status'] != 4 or
            prop(result, 'amount', 'IntegerValue') != amount or
            prop(result, 'playerId', 'IntegerValue') != saved_request['AnsweringPlayerId'] or
            resource is not None and prop(result, 'resourceType') != resource):
        raise ValueError('实际奖励效果的类型、数量或玩家不符：' + child_id)
if tree(root_id, new_nodes) != {root_id, *child_ids}:
    raise ValueError('事件奖励树存在额外或重复的后代。')

completed = [node['EffectId'] for node in old_nodes.values()
             if node['EffectTypeId'] == 'effect.event_card.resolve' and node['Status'] == 4]
for identity in completed:
    old_tree, new_tree = tree(identity, old_nodes), tree(identity, new_nodes)
    if old_tree != new_tree or any(old_nodes[item] != new_nodes.get(item) for item in old_tree):
        raise ValueError('续局改变了原档已完成的事件树：' + identity)
for collection, key in (('DispatchReceipts', 'ReceiptId'), ('RuleEvents', 'EventId')):
    old = index(before['EffectRuntime'][collection], key)
    new = index(after['EffectRuntime'][collection], key)
    if any(new.get(identity) != value for identity, value in old.items()):
        raise ValueError('续局改变了原档已有回执或事件：' + collection)

source_latency = ((source_exit_path.stat().st_mtime - (source / 'game/验证结果.json').stat().st_mtime)
                  if source_exit else None)
resume_latency = (((resume / '实际退出记录.json').stat().st_mtime - (resume / 'game/验证结果.json').stat().st_mtime)
                  if resume_exit else None)
source_timely = source_latency is not None and 0 <= source_latency <= 120
resume_timely = resume_latency is not None and 0 <= resume_latency <= 120
result = {'passed': not args.ongoing and source_timely and resume_timely,
          'eventRecoveryPassed': True, 'fullGameCompleted': not args.ongoing,
          'sourceExitVerified': bool(source_exit and source_exit['naturalExit']),
          'sourceExitLatencySeconds': source_latency, 'sourceExitWithin120Seconds': source_timely,
          'resumeExitLatencySeconds': resume_latency, 'resumeExitWithin120Seconds': resume_timely,
          'gameId': game, 'contentHash': content, 'sourceRevision': initial['StateRevision'],
          'sourceSaveSha256': digest, 'sourceAfterSaveSha256': source_after_digest,
          'resumeSavePath': str(resume_save), 'resumeSaveSha256': final_digest,
          'requestId': request_id, 'effectId': root_id, 'playerId': saved_request['AnsweringPlayerId'],
          'cardId': card, 'choiceId': choice, 'candidateId': selected, 'answerCommandId': answers[0]['Command']['CommandId'],
          'rewardChildEffectIds': child_ids, 'preservedCompletedEventTrees': len(completed),
          'preservedDispatchReceipts': len(before['EffectRuntime']['DispatchReceipts']),
          'preservedRuleEvents': len(before['EffectRuntime']['RuleEvents']),
          'scope': ('从正常本地局的原事件待答存档，经原主页读取并以本人可见卡面控件选择；本次只核对已完成的事件，续局仍在进行。'
                    if args.ongoing else '从正常本地局的原事件待答存档，经原主页读取、本人可见卡面控件选择并继续整局；整局通过还要求及时退出，仅核对本地续局。')}
with output.open('x', encoding='utf-8') as stream:
    json.dump(result, stream, ensure_ascii=False, indent=2)
print('本地事件待答原档恢复核验通过：', card, choice, '；原请求准确作答一次。',
      '整局仍在进行。' if args.ongoing else ('整局及及时退出通过。' if result['passed'] else '整局已结束，但退出超时，整体不计通过。'))
if not args.ongoing and not result['passed']:
    raise SystemExit(1)
