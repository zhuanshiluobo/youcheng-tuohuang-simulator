"""核对本机正常四端对局中企业特效放弃的实际来源与效果链。"""
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
cohort, output = args.cohort.resolve(strict=True), args.output.resolve()
if any(path == allowed or not path.is_relative_to(allowed) for path in (cohort, output)):
    raise ValueError('输入及输出必须位于项目 TestResults 的明确子目录。')


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def lines(path):
    return [json.loads(line) for line in path.read_text(encoding='utf-8-sig').splitlines() if line.strip()]


roles = ('host', 'client2', 'client3', 'client4')
state_path = cohort / 'host' / '联机正式终局规则状态.json'
state_bytes = state_path.read_bytes()
state = json.loads(state_bytes.decode('utf-8-sig'))
if state['Round'] != 8 or not state['FinalScoring']['IsResolved']:
    raise ValueError('没有已完成的正常终局。')
nodes = {node['EffectId']: node for node in state['EffectRuntime']['EffectNodes']}
if len(nodes) != len(state['EffectRuntime']['EffectNodes']):
    raise ValueError('效果身份重复。')
global_rows = {role: {row['Applied']['Command']['CommandId']: row['Applied']
                      for row in lines(cohort / role / '四端全局应用回执.jsonl')} for role in roles}
entries, unapplied, other = [], [], 0
for role in roles:
    for row in lines(cohort / role / '原UI命令提交前.jsonl'):
        command = row['OriginalUiCommand']
        request = (row.get('Ui') or {}).get('Request') or {}
        if request.get('InteractionTypeId') != 'action.decline_effect' or command.get('OptionIds') != ['decline']:
            continue
        command_id = command['CommandId']
        receipts = [global_rows[name].get(command_id) for name in roles]
        if not all(receipts):
            unapplied.append(command_id)
            continue
        if (request.get('AnsweringPlayerId') != command['PlayerId'] or
                any(receipt != receipts[0] or not receipt['RuleResult']['Succeeded'] or
                    receipt['Command']['PlayerId'] != command['PlayerId'] or
                    receipt['Command']['OptionIds'] != command['OptionIds'] for receipt in receipts)):
            raise ValueError('本人原控件与四端成功回执不一致：' + command_id)
        applied_parameters = {item['Key']: item['Value'] for item in receipts[0]['Command']['Parameters']}
        if any(applied_parameters.get(key) != value for key, value in command['Parameters'].items()):
            raise ValueError('回执中的原命令参数不一致：' + command_id)
        current = nodes.get(request.get('OwnerEffectId'))
        chain, visited = [], set()
        while current:
            if current['EffectId'] in visited:
                raise ValueError('效果链循环：' + command_id)
            visited.add(current['EffectId'])
            chain.append(current)
            current = nodes.get(current.get('ParentEffectId'))
        special = next((node for node in chain if node['EffectTypeId'] == 'effect.enterprise.activate_special'), None)
        if not special:
            other += 1
            continue
        entry = next((node for node in chain if node['EffectTypeId'] == 'effect.facility.entry'), None)
        build = next((node for node in chain if node['EffectTypeId'] == 'effect.facility.build'), None)
        if entry and special['ParentEffectId'] == entry['EffectId']:
            if not build or entry['ParentEffectId'] != build['EffectId'] or entry['Status'] != 4 or build['Status'] != 4:
                raise ValueError('设施入场后的建设效果未完成：' + command_id)
            source = 'facility_entry'
        else:
            source = 'other_source'
        if special['Status'] != 5 or special['FailureReason'] != 'player_declined':
            raise ValueError('企业特效未保留实际放弃结果：' + command_id)
        entries.append({'role': role, 'playerId': command['PlayerId'], 'commandId': command_id,
                        'globalSequence': receipts[0]['GlobalCommandSequence'], 'source': source,
                        'specialEffectId': special['EffectId'], 'entryEffectId': entry['EffectId'] if entry else None,
                        'buildEffectId': build['EffectId'] if build else None,
                        'chain': [{'type': node['EffectTypeId'], 'status': node['Status'], 'reason': node['FailureReason']}
                                  for node in chain]})
report = {'gameId': state['GameId'], 'contentHash': state['ContentPackHash'],
          'stateSha256': hashlib.sha256(state_bytes).hexdigest(),
          'scope': '仅审计本人原控件实际放弃、四端成功回执及终局效果链；未出现的来源不计覆盖',
          'facilityEntryCovered': any(item['source'] == 'facility_entry' for item in entries),
          'enterpriseSpecialDeclines': entries, 'otherDeclines': other, 'notAppliedOriginalCommands': unapplied}
with output.open('x', encoding='utf-8') as stream:
    json.dump(report, stream, ensure_ascii=False, indent=2)
print('企业特效放弃', len(entries), '次；设施入场来源覆盖：', report['facilityEntryCovered'])
