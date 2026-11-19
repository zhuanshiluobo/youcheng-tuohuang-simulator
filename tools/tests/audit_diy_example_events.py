"""核对正常整局中试验事件的本人选择和实际奖励；不合并不同版本的覆盖。"""
import argparse
import hashlib
import json
from pathlib import Path


project = Path(__file__).resolve().parents[2]
results = (project / 'TestResults').resolve()
builds = (project / 'Builds').resolve()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--cohort', type=Path, required=True)
parser.add_argument('--coverage', type=Path, required=True)
parser.add_argument('--release', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--require', action='append', default=[])
args = parser.parse_args()
cohort = args.cohort.resolve(strict=True)
coverage_path = args.coverage.resolve(strict=True)
release_path = args.release.resolve(strict=True)
output = args.output.resolve()
if any(path == results or not path.is_relative_to(results) for path in (cohort, coverage_path, output)):
    raise ValueError('对局、覆盖记录和输出必须位于 TestResults 的明确子目录。')
if release_path == builds or not release_path.is_relative_to(builds):
    raise ValueError('发布清单必须位于 Builds 的明确子目录。')


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def field(value, name, representation):
    return next((item['Value'].get(representation) for item in (value or {}).get('Properties', [])
                 if item.get('Name') == name), None)


# 规则与本次用户许可的试验包对应；脚本哈希不匹配时不能沿用这些预期。
expectations = {
    'example.events.dream/first': ('249562c14a09d538862e62234633a38f8779569255c058f9ebf187d1842b5545',
                                   [('effect.resource.gain', 'Iron', 4)]),
    'example.events.dream/second': ('142c39fe351b85f05abeb9af0ef6d951e756e94285eb855039ba62d8a7321952',
                                    [('effect.resource.gain', 'GoldVoucher', 4), ('effect.score.gain', None, 1)]),
    'example.events.support/first': ('b05019c4a9284f95a103dd93016c9b82be742a56bbf9a0099fb73c002a42b1d8',
                                     [('effect.resource.gain', 'Iron', 3), ('effect.resource.gain', 'GoldVoucher', 3)]),
    'example.events.support/second': ('fc74d2fb503b4dc928af234d0f832bafb2ea37508ad2ccb9b1258e2c0a530e0f',
                                      [('effect.resource.gain', 'Originium', 3), ('effect.resource.gain', 'Iron', 1)]),
}

release = read(release_path)
archive = release_path.parent / release['zipFile']
if (release['packId'] != 'example.events' or release['version'] != '1.0.0' or
        archive.name != 'example.events-1.0.0.zip' or sha(archive) != release['zipSha256']):
    raise ValueError('试验包发布身份或 ZIP 哈希不符。')
files = {row['path']: row for row in release['files']}
for key, (digest, _) in expectations.items():
    card, choice = key.split('/')
    script = 'lua/' + card.rsplit('.', 1)[1] + '.' + choice + '.lua'
    if files.get(script, {}).get('sha256') != digest:
        raise ValueError('试验脚本版本变化，不能沿用奖励预期：' + script)

coverage = read(coverage_path)
state_path = cohort / 'host/联机正式终局规则状态.json'
state = read(state_path)
if (str(state_path) != coverage['statePath'] or sha(state_path) != coverage['stateSha256'] or
        state['GameId'] != coverage['gameId'] or state['ContentPackHash'] != coverage['contentHash'] or
        state['Round'] != 8 or not state['FinalScoring']['IsResolved']):
    raise ValueError('覆盖记录与正式终局状态不一致。')
locked = [item for item in state['RuleConfiguration']['LockedContentPacks'] if item['PackId'] == release['packId']]
if len(locked) != 1 or locked[0]['Version'] != release['version'] or locked[0]['ContentHash'] != release['contentHash']:
    raise ValueError('本局没有锁定该试验包的精确版本和内容。')

nodes = state['EffectRuntime']['EffectNodes']
index = {item['EffectId']: item for item in nodes}
if len(index) != len(nodes):
    raise ValueError('正式效果身份重复。')
cards = {item['CardId']: item for item in state['ContentDefinitions']['Events']}
rows = []
for observation in coverage['observations']:
    key = observation['cardId'] + '/' + observation['choiceId']
    if key not in expectations or not observation['covered']:
        continue
    card, choice = key.split('/')
    expected = expectations[key][1]
    definition = cards[card]
    choices = [item for item in definition['Choices'] if item['ChoiceId'] == choice]
    expected_script = 'lua/.diy/example.events/1.0.0/' + card.rsplit('.', 1)[1] + '.' + choice + '.lua'
    if len(choices) != 1 or choices[0]['Script'] != expected_script or choices[0]['Version'] != '1.0.0':
        raise ValueError('活动定义的脚本来源不符：' + key)
    root = index[observation['effectId']]
    children = [index[identity] for identity in root['ChildEffectIds']]
    reward = [item for item in children if item['EffectTypeId'] in ('effect.resource.gain', 'effect.score.gain')]
    actual = []
    for item in reward:
        value = item['NormalizedResult']
        resource = field(value, 'resourceType', 'StringValue') if item['EffectTypeId'] == 'effect.resource.gain' else None
        if (item['Status'] != 4 or item['ParentEffectId'] != root['EffectId'] or
                field(value, 'playerId', 'IntegerValue') != observation['playerId'] or
                field(value, 'operation', 'StringValue') != item['EffectTypeId']):
            raise ValueError('奖励效果身份或执行结果不符：' + key)
        actual.append((item['EffectTypeId'], resource, field(value, 'amount', 'IntegerValue')))
    if root['Status'] != 4 or root['PlayerId'] != observation['playerId'] or actual != expected:
        raise ValueError('实际奖励与试验规则不符：' + key + ' / ' + str(actual))
    rows.append({'choice': key, 'playerId': observation['playerId'], 'effectId': root['EffectId'],
                 'rewardEffectIds': [item['EffectId'] for item in reward],
                 'rewards': [{'effectType': kind, 'resource': resource, 'amount': amount}
                             for kind, resource, amount in actual]})
covered = {row['choice'] for row in rows}
if not covered or not set(args.require) <= covered:
    raise ValueError('要求核对的选项未在本局形成完整覆盖：' + ', '.join(sorted(set(args.require) - covered)))
result = {'passed': True, 'gameId': state['GameId'], 'contentHash': state['ContentPackHash'],
          'assembly': coverage['assembly'], 'stateSha256': coverage['stateSha256'], 'packHash': release['contentHash'],
          'choices': rows, 'scope': '仅核对本局已完成的试验事件，卡面与奖励均为用户许可的临时示例。'}
with output.open('x', encoding='utf-8') as stream:
    json.dump(result, stream, ensure_ascii=False, indent=2)
print('试验事件实际奖励核验通过：', ', '.join(sorted(covered)))
