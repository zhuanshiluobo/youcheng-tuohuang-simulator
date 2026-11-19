"""核对纯模式与收尾示例的正常四端终局及每位玩家的收尾效果。"""
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
config = state['RuleConfiguration']
if config.get('RuleModeId') != 'example.basic.mode' or 'example.cleanup.rules' not in config.get('EnabledExpansionIds', []):
    raise ValueError('本局没有使用纯模式和收尾示例扩展。')
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
window_nodes = [n for n in runtime['MainNodes']
                if n.get('NodeTypeId') == 'player_cleanup_window' and 1 <= n.get('RoundNumber', 0) <= 8]
windows = {(n['RoundNumber'], n['PlayerId']): n for n in window_nodes}
events = [e for e in runtime['RuleEvents'] if e.get('EventType') == 'PlayerCleanupStarted']
receipts = [r for r in runtime['DispatchReceipts'] if r.get('AbilityId') == 'example.cleanup.score']
nodes = {n['EffectId']: n for n in runtime['EffectNodes']}
rows = []
for round_number in range(1, 9):
    for player in range(1, 5):
        window = windows.get((round_number, player))
        matching = [e for e in events if window and e.get('PlayerId') == player and
                    field(e.get('Payload'), 'nodeId').get('StringValue') == window['NodeId']]
        event = matching[0] if len(matching) == 1 else None
        dispatched = [r for r in receipts if event and r.get('EventId') == event['EventId']]
        receipt = dispatched[0] if len(dispatched) == 1 else None
        attached = receipt.get('AttachedEffectIds', []) if receipt else []
        effect = nodes.get(attached[0]) if len(attached) == 1 else None
        result = effect.get('NormalizedResult') if effect else None
        passed = bool(window and event and receipt and receipt.get('Succeeded') and effect and
                      effect.get('EffectTypeId') == 'effect.score.gain' and effect.get('Status') == 4 and
                      effect.get('PlayerId') == player and field(result, 'playerId').get('IntegerValue') == player and
                      field(result, 'amount').get('IntegerValue') == 1 and
                      field(result, 'operation').get('StringValue') == 'effect.score.gain')
        rows.append({'round': round_number, 'player': player, 'windowId': window.get('NodeId') if window else None,
                     'eventId': event.get('EventId') if event else None, 'receiptId': receipt.get('ReceiptId') if receipt else None,
                     'effectId': effect.get('EffectId') if effect else None, 'passed': passed})
known_events = {row['eventId'] for row in rows if row['eventId']}
extra_receipts = [r['ReceiptId'] for r in receipts if r.get('EventId') not in known_events]
summary = {'gameId': state['GameId'], 'contentHash': state['ContentPackHash'], 'roles': roles,
           'statePath': str(state_path), 'stateSha256': hashlib.sha256(state_path.read_bytes()).hexdigest(),
           'expectedWindows': 32, 'passedWindows': sum(row['passed'] for row in rows),
           'extraReceipts': extra_receipts, 'rows': rows}
summary['passed'] = (summary['passedWindows'] == 32 and not extra_receipts and len(windows) == 32 and
                     len(window_nodes) == 32 and len(events) == 32 and len(receipts) == 32)
output.parent.mkdir(parents=True, exist_ok=True)
with output.open('x', encoding='utf-8') as stream:
    json.dump(summary, stream, ensure_ascii=False, indent=2)
print(f"收尾规则覆盖：{summary['passedWindows']}/32；通过：{summary['passed']}")
if not summary['passed']:
    raise SystemExit(1)
