"""核对正常整局中企业替换事件的实际资源、支付与企业升级结果。"""
import argparse
import hashlib
import json
from pathlib import Path

project = Path(__file__).resolve().parents[2]
allowed = (project / 'TestResults').resolve()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--cohort', type=Path, required=True)
parser.add_argument('--coverage', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
cohort, coverage_path, output = args.cohort.resolve(strict=True), args.coverage.resolve(strict=True), args.output.resolve()
if any(path == allowed or not path.is_relative_to(allowed) for path in (cohort, coverage_path, output)):
    raise ValueError('对局、覆盖记录和输出必须位于 TestResults 的明确子目录。')


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def field(value, name, representation):
    return next((item['Value'].get(representation) for item in (value or {}).get('Properties', [])
                 if item.get('Name') == name), None)


coverage = read(coverage_path)
state_path = cohort / 'host/联机正式终局规则状态.json'
state = read(state_path)
if (str(state_path) != coverage['statePath'] or hashlib.sha256(state_path.read_bytes()).hexdigest() != coverage['stateSha256'] or
        state['GameId'] != coverage['gameId'] or state['ContentPackHash'] != coverage['contentHash'] or
        state['Round'] != 8 or not state['FinalScoring']['IsResolved'] or
        'enterprise' not in state['RuleConfiguration']['EnabledExpansionIds']):
    raise ValueError('覆盖记录、企业扩展或正式终局身份不一致。')
nodes = state['EffectRuntime']['EffectNodes']
index = {node['EffectId']: node for node in nodes}
if len(index) != len(nodes):
    raise ValueError('正式效果身份重复。')

simple = {
    'enterprise.event.cave_ruins/choice_01': [('Originium', 5)],
    'enterprise.event.cave_ruins/choice_02': [('GoldVoucher', 10)],
    'enterprise.event.collection_platform/choice_02': [('Originium', 4), ('OriginiumShard', 3)],
    'enterprise.event.collection_platform/choice_03': [('Iron', 4), ('GoldVoucher', 5)],
}
complex_choices = {'enterprise.event.cave_ruins/choice_03', 'enterprise.event.collection_platform/choice_01'}
rows, failures = [], []
for observation in coverage['observations']:
    key = observation['cardId'] + '/' + observation['choiceId']
    if not observation['covered'] or key not in simple and key not in complex_choices:
        continue
    root = index[observation['effectId']]
    player = observation['playerId']
    if root['Status'] != 4 or root['PlayerId'] != player:
        raise ValueError('事件根节点或玩家身份不符：' + key)
    children = [index[identity] for identity in root['ChildEffectIds']]
    if key in simple:
        rewards = [item for item in children if item['EffectTypeId'] == 'effect.resource.gain']
        actual = []
        for item in rewards:
            result = item['NormalizedResult']
            if item['Status'] != 4 or field(result, 'playerId', 'IntegerValue') != player:
                raise ValueError('资源收益玩家或完成状态不符：' + key)
            actual.append((field(result, 'resourceType', 'StringValue'), field(result, 'amount', 'IntegerValue')))
        if actual != simple[key]:
            raise ValueError('资源收益与迁移规则不符：' + key + ' / ' + str(actual))
        rows.append({'choice': key, 'playerId': player, 'effectId': root['EffectId'], 'fullRewardVerified': True,
                     'rewards': [{'resource': name, 'amount': amount} for name, amount in actual]})
        continue
    conditions = [item for item in children if item['EffectTypeId'] == 'effect.flow.condition']
    if len(conditions) != 1:
        raise ValueError('企业选项缺少唯一支付条件：' + key)
    condition = conditions[0]
    direct = [index[identity] for identity in condition['ChildEffectIds']]
    cost_kind = 'effect.resource.pay' if 'cave_ruins' in key else 'effect.score.lose'
    target_kind = 'effect.enterprise.upgrade' if 'cave_ruins' in key else 'effect.enterprise.upgrade_then_activate'
    costs = [item for item in direct if item['EffectTypeId'] == cost_kind]
    actions = [item for item in direct if item['EffectTypeId'] == target_kind]
    if not costs and not actions:
        rows.append({'choice': key, 'playerId': player, 'effectId': root['EffectId'],
                     'fullRewardVerified': False, 'reason': '未形成支付与企业升级的完整分支，保留为缺口。'})
        continue
    if len(costs) != 1 or len(actions) != 1:
        failures.append(key + ':cost_or_action_count')
        rows.append({'choice': key, 'fullRewardVerified': False, 'reason': '支付与升级节点数量不符。'})
        continue
    cost, action = costs[0], actions[0]
    cost_result = cost['NormalizedResult']
    valid_cost = (cost['Status'] == 4 and field(cost_result, 'playerId', 'IntegerValue') == player and
                  field(cost_result, 'amount', 'IntegerValue') == 1)
    if cost_kind == 'effect.resource.pay':
        valid_cost = valid_cost and field(cost_result, 'resourceType', 'StringValue') == 'Iron'
    else:
        before = field(cost_result, 'previousScore', 'IntegerValue')
        after = field(cost_result, 'score', 'IntegerValue')
        valid_cost = valid_cost and before is not None and after is not None and before - after == 1
    upgrades = [action] if target_kind == 'effect.enterprise.upgrade' else [
        index[identity] for identity in action['ChildEffectIds'] if index[identity]['EffectTypeId'] == 'effect.enterprise.upgrade']
    valid_upgrade = len(upgrades) == 1 and action['Status'] == 4 and upgrades[0]['Status'] == 4
    if valid_upgrade:
        upgrade_result = upgrades[0]['NormalizedResult']
        before = field(upgrade_result, 'oldLevel', 'IntegerValue')
        after = field(upgrade_result, 'newLevel', 'IntegerValue')
        valid_upgrade = (upgrades[0]['PlayerId'] == player and field(upgrade_result, 'changed', 'BooleanValue') is True and
                         before is not None and after is not None and after - before == 1)
    verified = bool(valid_cost and valid_upgrade)
    if not verified:
        failures.append(key + ':cost_or_upgrade_invalid')
    rows.append({'choice': key, 'playerId': player, 'effectId': root['EffectId'], 'costEffectId': cost['EffectId'],
                 'actionEffectId': action['EffectId'], 'upgradeEffectId': upgrades[0]['EffectId'] if len(upgrades) == 1 else None,
                 'costVerified': bool(valid_cost), 'upgradeVerified': bool(valid_upgrade), 'fullRewardVerified': verified})

verified_count = sum(bool(row['fullRewardVerified']) for row in rows)
result = {'passed': False if failures else (True if verified_count else None),
          'gameId': state['GameId'], 'contentHash': state['ContentPackHash'],
          'assembly': coverage['assembly'], 'verifiedChoiceCount': verified_count, 'verifiedChoices': rows, 'failures': failures,
          'scope': '只核对本局有本人可见选择和完整效果树证据的企业替换事件；未支付的分支不计完整收益。'}
with output.open('x', encoding='utf-8') as stream:
    json.dump(result, stream, ensure_ascii=False, indent=2)
print('企业替换事件奖励核验：', '失败' if failures else ('通过' if verified_count else '本局未形成完整收益证据'),
      '；本局完整核对', verified_count, '项。')
if failures:
    raise SystemExit(1)
