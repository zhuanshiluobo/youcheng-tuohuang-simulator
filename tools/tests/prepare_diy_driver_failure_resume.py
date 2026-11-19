"""从驱动误判而中断的四端局，保留自然自动档供独立正常读档验证。"""
import argparse
import hashlib
import json
import os
from pathlib import Path

project = Path(__file__).resolve().parents[2]
allowed = (project / 'TestResults').resolve()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
source, output = args.source.resolve(strict=True), args.output.resolve()
if any(path == allowed or not path.is_relative_to(allowed) for path in (source, output)):
    raise ValueError('来源和输出必须是 TestResults 的明确子目录。')
if output.exists() or output.is_relative_to(source):
    raise ValueError('输出必须是全新的独立目录。')
for path in (source, output.parent):
    for parent in (path, *path.parents):
        if parent == allowed:
            break
        if parent.exists() and (parent.is_symlink() or (hasattr(os.path, 'isjunction') and os.path.isjunction(parent))):
            raise ValueError('夹具路径不能经过重解析点。')


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def lines(path):
    return [json.loads(line) for line in path.read_text(encoding='utf-8-sig').splitlines() if line.strip()]


cohort = source / '四端正常联机'
failure = read(cohort / '首个实例失败.json')
if failure['Role'] != 'client4' or 'DriverContinuationPendingException' not in failure['Error']:
    raise ValueError('首败不是已核验的驱动待接，不使用此专用夹具。')
first = read(cohort / 'client4/延后差异首次-0001.json')['Current']
request = first['CurrentRequest']
if (request['InteractionTypeId'] != 'character.ability.choice.move' or request['AllowDecline'] or
        not request['VisibleToViewer'] or request['Status'] != 'open' or request['MinSelections'] < 1):
    raise ValueError('首差异不属于本人可见的必答移动请求。')
candidate_targets = {value.split('|')[2] for value in request['CandidateIds'] if len(value.split('|')) == 3}
visible = {item['TargetId'] for item in first['ActualHighlights'] if item['Kind'] == 'InfluenceSlot' and
           item['IsInteractive'] and item['Visible'] and item['Active']}
if len(candidate_targets & visible) != 8:
    raise ValueError('首差异的八个正式可交互目标不一致。')

save_path = source / '联机隔离存档/host/auto-1.json'
save_bytes = save_path.read_bytes()
save = json.loads(json.loads(save_bytes.decode('utf-8-sig'))['Payload'])
snapshot = json.loads(save['ArchiveJson'])['Snapshot']
state = snapshot['State']
baseline = json.loads(save['SimulationCheckpointJson'])['Baseline']
game_id, content_hash = state['GameId'], state['ContentPackHash']
initial = baseline['ConfirmedGlobalSequence']
if (not save['LocalTestNetwork'] or save['GameId'] != game_id or save['ContentHash'] != content_hash or
        baseline['GameId'] != game_id or baseline['ContentPackHash'] != content_hash or
        first['GameId'] != game_id or state['RuleConfiguration']['RuleModeId'] != 'standard' or
        state['Round'] != 6 or snapshot['StateRevision'] >= first['Revision'] or initial <= 0):
    raise ValueError('自然自动档的身份、模式或首败前边界不符。')
confirmed = baseline['ConfirmedCommands']
if len(confirmed) != initial:
    raise ValueError('存档确认前缀数量不符。')
launches = {item['Role']: item for item in read(cohort / '四实例启动记录.json')}
seats = {item['PlayerId']: item for item in save['Seats']}
roles, plans, sources, prefix = {}, [], [], None
for role in ('host', 'client2', 'client3', 'client4'):
    folder = cohort / role
    identity = read(folder / '联机运行身份.json')
    if identity['GameId'] != game_id or identity['ContentPackHash'] != content_hash:
        raise ValueError('原四端身份不一致：' + role)
    applied = [row['Applied'] for row in lines(folder / '四端全局应用回执.jsonl')][:initial]
    if (len(applied) != initial or [row['GlobalCommandSequence'] for row in applied] != list(range(1, initial + 1)) or
            any(not row['RuleResult']['Succeeded'] or row['Command']['CommandId'] != original['Outcome']['CommandId']
                for row, original in zip(applied, confirmed))):
        raise ValueError('自然存档确认前缀与原回执不一致：' + role)
    if prefix is None:
        prefix = applied
    elif applied != prefix:
        raise ValueError('自然存档确认前缀不是四端一致：' + role)
    player = identity['LocalPlayer']
    arguments = launches[role]['Arguments']
    names = [value.split('=', 1)[1] for value in arguments if value.startswith('--yc-local-player-name=')]
    operators = [value.split('=', 1)[1] for value in arguments if value.startswith('--yc-test-operator-id=')]
    if len(names) != 1 or len(operators) != 1 or seats[player]['OperatorId'] != operators[0] or seats[player]['PlayerName'] != names[0]:
        raise ValueError('原存档席位与原进程身份不一致：' + role)
    confirmed_ids = {row['Outcome']['CommandId'] for row in confirmed}
    path = folder / '逐玩家行动计划.jsonl'
    candidates = [row for row in lines(path) if row['GameId'] == game_id and row['CommandId'] in confirmed_ids and
                  row['Progress']['PlayerId'] == player]
    if not candidates:
        raise ValueError('确认前缀缺少原行动策略：' + role)
    last = candidates[-1]
    plans.append(last['Progress'])
    roles[role] = {'playerId': player, 'name': names[0], 'operatorId': operators[0]}
    sources.append({'role': role, 'commandId': last['CommandId'], 'round': last['Round'], 'log': str(path)})
if {plan['PlayerId'] for plan in plans} != {1, 2, 3, 4}:
    raise ValueError('原行动策略玩家不完整。')
output.mkdir(parents=True)
copy = output / '原自动存档副本.json'
with copy.open('xb') as stream:
    stream.write(save_bytes)
digest = hashlib.sha256(save_bytes).hexdigest()
if hashlib.sha256(save_path.read_bytes()).hexdigest() != digest:
    raise ValueError('复制期间原存档发生变化。')
policy = output / '原行动策略.json'
with policy.open('x', encoding='utf-8') as stream:
    json.dump({'GameId': game_id, 'ContentHash': content_hash, 'Players': plans}, stream, ensure_ascii=False, indent=2)
metadata = {'gameId': game_id, 'contentHash': content_hash, 'ruleMode': state['RuleConfiguration']['RuleModeId'],
            'round': state['Round'], 'stateRevision': snapshot['StateRevision'], 'confirmedSequence': initial,
            'saveCopy': str(copy), 'saveSha256': digest, 'policy': str(policy), 'policySources': sources, 'roles': roles,
            'sourceFailed': True, 'sourceFailure': str(cohort / '首个实例失败.json'),
            'scope': '驱动误判的未完成原局；从更早自然自动档经正常菜单独立续局，不改判原局、不注入状态。'}
with (output / '续局夹具来源.json').open('x', encoding='utf-8') as stream:
    json.dump(metadata, stream, ensure_ascii=False, indent=2)
print('已核验四端原回执并复制自然自动档：', game_id, '修订', snapshot['StateRevision'], '确认序号', initial)
