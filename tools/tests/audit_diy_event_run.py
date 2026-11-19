"""核对一局隔离四端正常对局的事件选择证据，并生成下一局合法候选优先级。"""
import argparse
import hashlib
import json
from pathlib import Path
import struct

project = Path(__file__).resolve().parents[2]
allowed = (project / 'TestResults').resolve()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--cohort', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--previous', type=Path, action='append', default=[], help='同一程序集和内容哈希的每一已完成局记录须分别提供；不会自动展开其中的累计结果。')
args = parser.parse_args()
cohort = args.cohort.resolve(strict=True)
output = args.output.resolve()
if cohort == allowed or not cohort.is_relative_to(allowed) or output == allowed or not output.is_relative_to(allowed):
    raise ValueError('输入和输出必须位于项目 TestResults 的明确子目录。')

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def lines(path):
    if not path.exists():
        return []
    return [json.loads(line) for line in path.read_text(encoding='utf-8-sig').splitlines() if line.strip()]

def candidate(card, choice):
    parts = ('event-choice', card, choice)
    data = struct.pack('>I', len(parts))
    for value in ('nmc.stable-id.v1', *parts):
        raw = value.encode('utf-8')
        data += b'\x01' + struct.pack('>I', len(raw)) + raw
    return 'event-choice_' + hashlib.sha256(data).hexdigest()

def prop(value, name):
    return next((entry['Value'] for entry in (value or {}).get('Properties', []) if entry.get('Name') == name), {})

roles = ('host', 'client2', 'client3', 'client4')
state_path = cohort / 'host' / '联机正式终局规则状态.json'
state = read(state_path)
if state.get('Round') != 8 or not state.get('FinalScoring', {}).get('IsResolved'):
    raise ValueError('仅核对已正式完成八回合计分的本局。')
game, content = state['GameId'], state['ContentPackHash']
role_data = {}
assembly = None
for role in roles:
    directory = cohort / role
    identity = read(directory / '联机运行身份.json')
    run = read(directory / '运行身份.json')
    report = read(directory / '验证结果.json')
    if identity.get('GameId') != game or identity.get('ContentPackHash') != content or not report.get('Passed'):
        raise ValueError('四端身份或验收结果不一致：' + role)
    assembly = assembly or run['Assembly']
    if run['Assembly'] != assembly:
        raise ValueError('四端程序集不一致。')
    role_data[identity['LocalPlayer']] = {
        'role': role,
        'decisions': lines(directory / '决策变化.jsonl'),
        'controls': lines(directory / '控件事件.jsonl'),
        'receipts': lines(directory / '命令与准确回执.jsonl'),
    }
if set(role_data) != {1, 2, 3, 4}:
    raise ValueError('四端玩家身份不完整。')

cards = {card['CardId']: card for card in state['ContentDefinitions']['Events']}
recipe_ids = {entry['DefinitionId'] for entry in state['SetupRecipe']['Entries']
              if entry.get('InitialZoneRole', '').startswith('event_')}
eligible = {(card_id, choice['ChoiceId']) for card_id, card in cards.items() if card_id in recipe_ids
            for choice in card.get('Choices', [])}
nodes = state['EffectRuntime']['EffectNodes']
children = {}
for node in nodes:
    children.setdefault(node.get('ParentEffectId'), []).append(node)
requests = {request.get('OwnerEffectId'): request for request in state['EffectRuntime']['InteractionRequests']
            if request.get('InteractionTypeId') == 'event_card.option'}

def descendants(parent):
    pending = list(children.get(parent, []))
    result = []
    while pending:
        node = pending.pop()
        result.append(node)
        pending.extend(children.get(node['EffectId'], []))
    return result

covered, observations = set(), []
for node in (item for item in nodes if item.get('EffectTypeId') == 'effect.event_card.resolve'):
    result = node.get('NormalizedResult') or {}
    card, choice = prop(result, 'cardId').get('StringValue', ''), prop(result, 'selectedChoiceId').get('StringValue', '')
    selected = prop(result, 'selectedOptionId').get('StringValue', '')
    if not card or not choice:
        continue
    if selected != candidate(card, choice):
        raise ValueError('正式结果与稳定候选身份不一致：' + card + '/' + choice)
    actor = node['PlayerId']
    data = role_data[actor]
    request = requests.get(node['EffectId'])
    receipts = [r for r in data['receipts'] if request and
                r.get('Command', {}).get('Parameters', {}).get('interactionId') == request.get('InteractionId') and
                selected in r.get('Command', {}).get('OptionIds', []) and
                r.get('Result', {}).get('CommandResult', {}).get('Succeeded') and r.get('Result', {}).get('AppliedLocally')]
    decisions = [d for d in data['decisions'] if d.get('Action') == '事件牌选项' and d.get('Card') == card and
                 d.get('PlayerId') == actor and d.get('ActualControl') == 'event.choice.' + choice]
    card_choices = cards.get(card, {}).get('Choices', [])
    visual_index = next((i + 1 for i, item in enumerate(card_choices) if item.get('ChoiceId') == choice), None)
    controls = [c for c in data['controls'] if any(c.get('Stage') == receipt.get('Stage') and c.get('Frame', 0) <= receipt.get('Frame', -1) for receipt in receipts)
                and c.get('Description') == '正常随机事件首次选择' and c.get('Path', '').endswith('/Choice ' + str(visual_index))]
    subtree = descendants(node['EffectId'])
    failures = [{'effectId': item['EffectId'], 'type': item.get('EffectTypeId'), 'status': item.get('Status'),
                 'reason': item.get('FailureReason')} for item in subtree if item.get('Status') in (5, 6)]
    checks = {'eligible': (card, choice) in eligible, 'requestAnswered': bool(request and request.get('Status') == 'answered' and
              selected in request.get('CandidateIds', []) and any(item.get('ReferenceId') == selected for item in request.get('NormalizedAnswer', {}).get('Items', []))),
              'visibleDecision': bool(decisions), 'originalControl': bool(controls), 'acceptedAppliedReceipt': bool(receipts),
              'effectCompleted': node.get('Status') == 4 and all(item.get('Status') == 4 for item in subtree), 'noFailedDescendants': not failures}
    passed = all(checks.values())
    if passed:
        covered.add((card, choice))
    observations.append({'cardId': card, 'choiceId': choice, 'candidateId': selected, 'playerId': actor,
                         'role': data['role'], 'effectId': node['EffectId'], 'requestId': request.get('InteractionId') if request else None,
                         'receiptIds': [r['Command']['CommandId'] for r in receipts], 'controlPaths': [c['Path'] for c in controls],
                         'checks': checks, 'failedDescendants': failures, 'covered': passed})

missing = sorted(eligible - covered)
cumulative = set(covered)
game_ids = {game}
provenance = [{'gameId': game, 'stateSha256': hashlib.sha256(state_path.read_bytes()).hexdigest(), 'coveredChoices': len(covered)}]
for previous_path in args.previous:
    previous_path = previous_path.resolve(strict=True)
    if previous_path == allowed or not previous_path.is_relative_to(allowed):
        raise ValueError('历史覆盖记录必须位于项目 TestResults 的明确子目录。')
    previous = read(previous_path)
    if previous['gameId'] in game_ids:
        raise ValueError('不能把同一局重复累计：' + previous['gameId'])
    if previous['assembly'] != assembly or previous['contentHash'] != content:
        raise ValueError('不同程序或内容哈希不能合并覆盖：' + str(previous_path))
    previous_covered = {tuple(item) for item in previous['covered']}
    previous_missing = {tuple(item) for item in previous['missing']}
    if previous_covered | previous_missing != eligible or previous_covered & previous_missing:
        raise ValueError('历史局事件候选范围与本局不同：' + str(previous_path))
    cumulative.update(previous_covered)
    game_ids.add(previous['gameId'])
    provenance.append({'gameId': previous['gameId'], 'stateSha256': previous['stateSha256'], 'coveredChoices': len(previous_covered)})
priorities = [candidate(card, choice) for card, choice in sorted(eligible - cumulative)]
other_priorities = [item['Key'] for item in read(cohort / 'host' / '本局覆盖与下一局决策.json').get('NextPriorities', [])]
result = {'gameId': game, 'contentHash': content, 'assembly': assembly, 'terminalRevision': state['EffectRuntime']['StateRevision'],
          'statePath': str(state_path), 'stateSha256': hashlib.sha256(state_path.read_bytes()).hexdigest(),
          'eligibleChoices': len(eligible), 'coveredChoices': len(covered), 'covered': sorted(covered), 'missing': missing,
          'observations': observations, 'cumulativeCovered': sorted(cumulative), 'cumulativeMissing': sorted(eligible - cumulative),
          'cumulativeGames': provenance, 'nextPriorities': priorities + other_priorities,
          'scope': '仅同局本人正式请求、原控件点击、准确成功回执与完整效果树均有证据的选择计覆盖；未出现和未证明项均保留缺口。'}
output.parent.mkdir(parents=True, exist_ok=True)
with output.open('x', encoding='utf-8') as stream:
    json.dump(result, stream, ensure_ascii=False, indent=2)
print('事件选择覆盖：' + str(len(covered)) + '/' + str(len(eligible)) + '；观察节点：' + str(len(observations)))
