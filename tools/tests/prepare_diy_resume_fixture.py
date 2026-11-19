"""从已完成的隔离正常联机局保留原自动存档，准备独立的正常读档续局夹具。"""
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
source = args.source.resolve(strict=True)
output = args.output.resolve()
if any(path == allowed or not path.is_relative_to(allowed) for path in (source, output)):
    raise ValueError('来源和输出必须是项目 TestResults 中的明确子目录。')
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


cohort = source / '四端正常联机'
summary = read(cohort / '四端整局验收汇总.json')
if not summary.get('本局通过') or not summary.get('四端按报告自行退出'):
    raise ValueError('只能使用已正常完成且四端自然退出的原局。')
save_path = source / '联机隔离存档/host/auto-1.json'
save_bytes = save_path.read_bytes()
save = json.loads(json.loads(save_bytes.decode('utf-8-sig'))['Payload'])
snapshot = json.loads(save['ArchiveJson'])['Snapshot']
state = snapshot['State']
baseline = json.loads(save['SimulationCheckpointJson'])['Baseline']
game_id, content_hash = state['GameId'], state['ContentPackHash']
if (not save['LocalTestNetwork'] or save['GameId'] != game_id or save['ContentHash'] != content_hash or
        baseline['GameId'] != game_id or baseline['ContentPackHash'] != content_hash or
        state['RuleConfiguration']['RuleModeId'] != 'example.basic.mode' or state['Round'] != 8):
    raise ValueError('原存档的本地联机身份、模式或活动内容不符。')
confirmed = {item['Outcome']['CommandId'] for item in baseline['ConfirmedCommands']}
launches = {item['Role']: item for item in read(cohort / '四实例启动记录.json')}
seats = {item['PlayerId']: item for item in save['Seats']}
roles, plans, sources = {}, [], []
for role in ('host', 'client2', 'client3', 'client4'):
    identity = read(cohort / role / '联机运行身份.json')
    if identity['GameId'] != game_id or identity['ContentPackHash'] != content_hash:
        raise ValueError('原四端身份不一致：' + role)
    player = identity['LocalPlayer']
    arguments = launches[role]['Arguments']
    names = [s.split('=', 1)[1] for s in arguments if s.startswith('--yc-local-player-name=')]
    operators = [s.split('=', 1)[1] for s in arguments if s.startswith('--yc-test-operator-id=')]
    if len(names) != 1 or len(operators) != 1 or seats[player]['OperatorId'] != operators[0] or seats[player]['PlayerName'] != names[0]:
        raise ValueError('原存档席位与测试进程身份不一致：' + role)
    log_path = cohort / role / '逐玩家行动计划.jsonl'
    rows = [json.loads(line) for line in log_path.read_text(encoding='utf-8-sig').splitlines() if line.strip()]
    rows = [row for row in rows if row['GameId'] == game_id and row['CommandId'] in confirmed and row['Progress']['PlayerId'] == player]
    if not rows:
        raise ValueError('原存档确认前缀中没有该玩家的行动策略原记录：' + role)
    last = rows[-1]
    plans.append(last['Progress'])
    roles[role] = {'playerId': player, 'name': names[0], 'operatorId': operators[0]}
    sources.append({'role': role, 'commandId': last['CommandId'], 'round': last['Round'], 'log': str(log_path)})
if {plan['PlayerId'] for plan in plans} != {1, 2, 3, 4}:
    raise ValueError('原行动策略玩家不完整。')
output.mkdir(parents=True)
copy = output / '原自动存档副本.json'
with copy.open('xb') as stream:
    stream.write(save_bytes)
digest = hashlib.sha256(save_bytes).hexdigest()
if hashlib.sha256(save_path.read_bytes()).hexdigest() != digest:
    raise ValueError('复制期间原自动存档发生变化，保留副本并停止。')
policy = output / '原行动策略.json'
with policy.open('x', encoding='utf-8') as stream:
    json.dump({'GameId': game_id, 'ContentHash': content_hash, 'Players': plans}, stream, ensure_ascii=False, indent=2)
metadata = {'gameId': game_id, 'contentHash': content_hash, 'ruleMode': state['RuleConfiguration']['RuleModeId'],
            'round': state['Round'], 'stateRevision': snapshot['StateRevision'],
            'confirmedSequence': baseline['ConfirmedGlobalSequence'], 'saveCopy': str(copy), 'saveSha256': digest,
            'policy': str(policy), 'policySources': sources, 'roles': roles,
            'scope': '原局正常自动存档的独立读档验证；不修改存档，也不计作新局或补齐随机覆盖。'}
with (output / '续局夹具来源.json').open('x', encoding='utf-8') as stream:
    json.dump(metadata, stream, ensure_ascii=False, indent=2)
print('续局原档准备完成：', game_id, '修订', snapshot['StateRevision'], '确认序号', baseline['ConfirmedGlobalSequence'])
