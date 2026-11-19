"""核对正常四端整局中罗德岛一级特效的真实选择、回执及升级结果。"""
import argparse
import hashlib
import json
from pathlib import Path

project = Path(__file__).resolve().parents[2]
allowed = (project / 'TestResults').resolve()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--cohort', type=Path, required=True)
parser.add_argument('--terminal-audit', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
cohort = args.cohort.resolve(strict=True)
audit_path = args.terminal_audit.resolve(strict=True)
output = args.output.resolve()
if any(path == allowed or not path.is_relative_to(allowed) for path in (cohort, audit_path, output)):
    raise ValueError('对局、终局审计及输出必须位于 TestResults 的明确子目录。')


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def lines(path):
    return [json.loads(line) for line in path.read_text(encoding='utf-8-sig').splitlines() if line.strip()]


def field(value, name):
    return next((entry['Value'] for entry in (value or {}).get('Properties', []) if entry.get('Name') == name), {})


roles = ('host', 'client2', 'client3', 'client4')
terminal = read(audit_path)
state_path = cohort / 'host' / '联机正式终局规则状态.json'
state = read(state_path)
if (not terminal.get('passed') or terminal.get('gameId') != state['GameId'] or
        terminal.get('contentHash') != state['ContentPackHash'] or state['Round'] != 8 or
        not state['FinalScoring']['IsResolved']):
    raise ValueError('四端终局审计、正式终局和游戏身份不一致。')
special = 'infected.enterprise.rhodes_island.level_1.special'
target_key = 'enterprise:infected.enterprise.rhodes_island:level:1'
observations = [row for row in lines(cohort / 'host' / '实际完成覆盖.jsonl') if row.get('Key') == target_key]
index = {node['EffectId']: node for node in state['EffectRuntime']['EffectNodes']}
if len(index) != len(state['EffectRuntime']['EffectNodes']):
    raise ValueError('终局效果节点身份重复。')
results = []
for observation in observations:
    evidence = observation['Evidence']
    event = evidence['Event']
    command_id = event['OperationContext']['SourceCommandId']
    actor = event['PlayerId']
    if (event['EventType'] != 'EnterpriseSpecialEffectCompleted' or
            field(event['Payload'], 'specialId').get('StringValue') != special):
        raise ValueError('记录的企业完成事件身份不符。')
    node = index[event['SourceEffectId']]
    if (node['Status'] != 4 or node['PlayerId'] != actor or node['EffectTypeId'] != 'effect.enterprise.activate_special' or
            field(node['NormalizedResult'], 'selectedSpecialId').get('StringValue') != special or
            field(node['NormalizedResult'], 'selectedEnterpriseItemId').get('StringValue') != event['TargetEntityId']):
        raise ValueError('企业特效未按原玩家和原企业完成。')
    children = [index[identity] for identity in node['ChildEffectIds']]
    upgrades = [child for child in children if child['EffectTypeId'] == 'effect.enterprise.upgrade']
    if len(upgrades) != 1 or any(child['Status'] != 4 for child in children):
        raise ValueError('罗德岛一级特效的升级子效果未完成。')
    upgrade = upgrades[0]
    upgrade_result = upgrade['NormalizedResult']
    scope = [item['StringValue'] for item in field(upgrade['NormalizedArguments'], 'enterpriseItemIds').get('Items', [])]
    target = field(upgrade_result, 'selectedEnterpriseItemId').get('StringValue')
    old_level = field(upgrade_result, 'oldLevel').get('IntegerValue')
    new_level = field(upgrade_result, 'newLevel').get('IntegerValue')
    if (event['TargetEntityId'] in scope or target not in scope or len(scope) != len(set(scope)) or
            not field(upgrade_result, 'changed').get('BooleanValue') or old_level is None or new_level != old_level + 1):
        raise ValueError('升级目标、排除自身或实际等级变化不符。')
    matches = []
    for role in roles:
        folder = cohort / role
        for receipt in lines(folder / '命令与准确回执.jsonl'):
            command = receipt.get('Command', {})
            result = receipt.get('Result') or {}
            if command.get('CommandId') != command_id:
                continue
            if command.get('PlayerId') != actor or not result.get('AppliedLocally') or not (result.get('CommandResult') or {}).get('Succeeded'):
                raise ValueError('原选择没有准确的本机成功回执。')
            chosen = command.get('OptionIds') or []
            if len(chosen) != 1:
                raise ValueError('原选择候选数量不符。')
            decisions = [row for row in lines(folder / '企业覆盖决策.jsonl') if row.get('Request') == 'enterprise.special' and
                         row.get('LocalPlayer') == actor and chosen[0] in row.get('Chosen', []) and any(
                             candidate.get('Id') == chosen[0] and candidate.get('EffectId') == special and
                             candidate.get('CompanyId') == event['TargetEntityId'] for candidate in row.get('Candidates', []))]
            controls = [row for row in lines(folder / '控件事件.jsonl') if row.get('Stage') == receipt.get('Stage') and
                        row.get('Description') == '正常企业/科室候选' + chosen[0] and row.get('Frame', 0) <= receipt['Frame']]
            confirms = [row for row in lines(folder / '控件事件.jsonl') if row.get('Stage') == receipt.get('Stage') and
                        row.get('Description') == '企业/科室首次确认' and row.get('Frame', 0) <= receipt['Frame']]
            if not decisions or not controls or not confirms:
                raise ValueError('企业特效缺少本人可见候选、原控件点击或确认。')
            matches.append((role, chosen[0], receipt, controls[0]))
    if len(matches) != 1:
        raise ValueError('企业选择命令的本机成功回执不是唯一的。')
    role, candidate, receipt, control = matches[0]
    global_receipts = []
    for view in roles:
        rows = [row['Applied'] for row in lines(cohort / view / '四端全局应用回执.jsonl')
                if row['Applied']['Command']['CommandId'] == command_id]
        if len(rows) != 1 or not rows[0]['RuleResult']['Succeeded']:
            raise ValueError('四端缺少企业选择的同一成功回执：' + view)
        global_receipts.append(rows[0])
    if any(item != global_receipts[0] for item in global_receipts[1:]):
        raise ValueError('四端企业选择的全局回执不一致。')
    results.append({'player': actor, 'role': role, 'candidateId': candidate, 'commandId': command_id,
                    'globalSequence': global_receipts[0]['GlobalCommandSequence'], 'controlPath': control['Path'],
                    'specialEffectId': node['EffectId'], 'upgradeEffectId': upgrade['EffectId'],
                    'specialEnterpriseItemId': event['TargetEntityId'], 'upgradeCandidateItemIds': scope,
                    'upgradedEnterpriseItemId': target, 'oldLevel': old_level, 'newLevel': new_level})
if not results:
    raise ValueError('本局未完成感染者罗德岛一级特效，不能视为覆盖。')
report = {'passed': True, 'gameId': state['GameId'], 'contentHash': state['ContentPackHash'],
          'terminalStateSha256': hashlib.sha256(state_path.read_bytes()).hexdigest(), 'observations': results,
          'scope': '本人可见候选及原控件、四端同一成功回执、企业特效与一级升级效果树；随机未出现的其他路径不计覆盖。'}
with output.open('x', encoding='utf-8') as stream:
    json.dump(report, stream, ensure_ascii=False, indent=2)
print('罗德岛一级特效正常对局审计通过：', len(results), '次')
