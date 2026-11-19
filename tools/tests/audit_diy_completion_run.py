"""核对完成事件模式正常四端局中的城市移动与示例得分。"""
import argparse
import hashlib
import json
from pathlib import Path

project = Path(__file__).resolve().parents[2]
allowed = (project / 'TestResults').resolve()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--cohort', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
cohort = args.cohort.resolve(strict=True)
output = args.output.resolve()
if cohort == allowed or not cohort.is_relative_to(allowed) or output == allowed or not output.is_relative_to(allowed):
    raise ValueError('输入和输出必须位于项目 TestResults 的明确子目录。')


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def field(value, name):
    return next((entry['Value'] for entry in (value or {}).get('Properties', []) if entry.get('Name') == name), {})


state_path = cohort / 'host' / '联机正式终局规则状态.json'
state = read(state_path)
if state.get('Round') != 8 or not state.get('FinalScoring', {}).get('IsResolved'):
    raise ValueError('只核对已正式完成八回合计分的本局。')
if state['RuleConfiguration'].get('RuleModeId') != 'example.completed.mode':
    raise ValueError('本局没有使用完成事件模式示例。')
roles = {}
for role in ('host', 'client2', 'client3', 'client4'):
    directory = cohort / role
    identity = read(directory / '联机运行身份.json')
    run = read(directory / '运行身份.json')
    report = read(directory / '验证结果.json')
    boundary = read(directory / '联机终局确认边界.json')
    if (identity.get('GameId') != state['GameId'] or identity.get('ContentPackHash') != state['ContentPackHash'] or
            not report.get('Passed') or report.get('DeferredContentDifferences') != 0):
        raise ValueError('四端身份或正常局结果不一致：' + role)
    roles[role] = {'player': identity['LocalPlayer'], 'assembly': run['Assembly'],
                   'sequence': boundary['GlobalFenceSequence'], 'digest': boundary['RuleStateDigest']}
if ({v['player'] for v in roles.values()} != {1, 2, 3, 4} or
        len({(v['assembly'], v['sequence'], v['digest']) for v in roles.values()}) != 1):
    raise ValueError('四端终局玩家、程序集或确认摘要不一致。')

runtime = state['EffectRuntime']
nodes = {n['EffectId']: n for n in runtime['EffectNodes']}
events = {event['EventId']: event for event in runtime['RuleEvents'] if event.get('EventType') == 'EffectCompleted'}
receipts = [receipt for receipt in runtime['DispatchReceipts'] if receipt.get('AbilityId') == 'example.completed.city_score']
rows = []
nonresponsive = []
for node in nodes.values():
    if node.get('EffectTypeId') != 'effect.city.move' or node.get('Status') != 4:
        continue
    event = events.get(node.get('CompletedEventId'))
    if not event:
        rows.append({'player': node['PlayerId'], 'moveEffectId': node['EffectId'],
                     'eventId': node.get('CompletedEventId'), 'passed': False, 'reason': '缺少完成事件'})
        continue
    if event.get('ResponseKind') == 0:
        nonresponsive.append({'moveEffectId': node['EffectId'], 'eventId': event['EventId'],
                              'reason': '完成事件按正式协议不接受响应'})
        continue
    if event.get('ResponseKind') != 1:
        rows.append({'player': node['PlayerId'], 'moveEffectId': node['EffectId'], 'eventId': event['EventId'],
                     'passed': False, 'reason': '完成事件响应类型无效'})
        continue
    dispatched = [receipt for receipt in receipts if receipt.get('EventId') == event['EventId']]
    receipt = dispatched[0] if len(dispatched) == 1 else None
    attached = receipt.get('AttachedEffectIds', []) if receipt else []
    effect = nodes.get(attached[0]) if len(attached) == 1 else None
    result = effect.get('NormalizedResult') if effect else None
    player = node['PlayerId']
    passed = bool(event.get('SourceEffectId') == node['EffectId'] and receipt and receipt.get('Succeeded') and effect and
                  effect.get('EffectTypeId') == 'effect.score.gain' and effect.get('Status') == 4 and effect.get('PlayerId') == player and
                  field(result, 'playerId').get('IntegerValue') == player and field(result, 'amount').get('IntegerValue') == 1 and
                  field(result, 'operation').get('StringValue') == 'effect.score.gain')
    rows.append({'player': player, 'moveEffectId': node['EffectId'], 'eventId': event['EventId'],
                 'receiptId': receipt.get('ReceiptId') if receipt else None, 'scoreEffectId': effect.get('EffectId') if effect else None,
                 'passed': passed})
known_events = {row['eventId'] for row in rows}
failed_events = {node.get('CompletedEventId') for node in nodes.values()
                 if node.get('EffectTypeId') == 'effect.city.move' and node.get('Status') == 5}
filtered_receipts = [receipt['ReceiptId'] for receipt in receipts if receipt.get('EventId') in failed_events and
                     receipt.get('Succeeded') and not receipt.get('AttachedEffectIds')]
extra_receipts = [receipt['ReceiptId'] for receipt in receipts if receipt.get('EventId') not in known_events and
                  receipt['ReceiptId'] not in filtered_receipts]
summary = {'gameId': state['GameId'], 'contentHash': state['ContentPackHash'], 'roles': roles,
           'statePath': str(state_path), 'stateSha256': hashlib.sha256(state_path.read_bytes()).hexdigest(),
           'observedMoves': len(rows), 'passedMoves': sum(row['passed'] for row in rows),
           'nonresponsiveMoves': nonresponsive, 'filteredFailedEventReceipts': filtered_receipts,
           'extraReceipts': extra_receipts, 'rows': rows}
summary['passed'] = None if not rows and not extra_receipts else bool(rows and all(row['passed'] for row in rows) and not extra_receipts)
summary['scope'] = '只计已成功完成且发出可响应完成事件的城市移动；未出现时记为未覆盖。'
output.parent.mkdir(parents=True, exist_ok=True)
with output.open('x', encoding='utf-8') as stream:
    json.dump(summary, stream, ensure_ascii=False, indent=2)
print(f"完成事件规则：{summary['passedMoves']}/{summary['observedMoves']}；结果：{summary['passed']}")
if summary['passed'] is False:
    raise SystemExit(1)
