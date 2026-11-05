"""只读比较同一原命令的旧独立程序记录与隔离重放，不能计作正常UI验收。"""
import argparse
import csv
import json
import pathlib

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('replay', type=pathlib.Path)
parser.add_argument('--reference', type=pathlib.Path, required=True)
parser.add_argument('--classification', type=pathlib.Path, required=True)
parser.add_argument('--output', type=pathlib.Path, required=True)
args = parser.parse_args()
assert not args.output.exists(), '不能覆盖已有报告目录'
replay = json.loads(args.replay.read_text(encoding='utf-8-sig'))
with args.classification.open(encoding='utf-8-sig', newline='') as stream:
    categories = {int(row['样本']): row['流程'] for row in csv.DictReader(stream)
                  if row['运行'] == args.reference.name and row['层'] == 'command'}
originals = {}
with (args.reference/'game/逐步性能.jsonl').open(encoding='utf-8-sig') as stream:
    for line in stream:
        value = json.loads(line)
        if value.get('Boundary') == 'end' and value['Kind'] == 'command':
            assert value['GameId'] == replay['GameId'], '不能跨GameId比较'
            assert value['CommandId'] not in originals, '原命令ID重复'
            originals[value['CommandId']] = value
groups, details = {}, []
for row in replay['Rows']:
    original = originals[row['CommandId']]
    category = categories[original['Index']]
    assert original['Before']['Revision'] == row['BeforeRevision']
    detail = {'流程': category, '原命令ID': row['CommandId'], '原命令': original['Name'],
              'BeforeRevision': row['BeforeRevision'], 'AfterRevision': row['Revision'],
              '旧独立程序命令秒': original['Seconds'], '新隔离命令秒': row['Seconds'],
              '隔离重放成功': row['Succeeded'], '隔离工作集MiB': row['WorkingSet']/2**20 if row['WorkingSet']>0 else None,
              '隔离私有提交MiB': row['PrivateBytes']/2**20 if row['PrivateBytes']>0 else None,
              '隔离工作集增量MiB': row['DeltaWorkingSet']/2**20 if row['WorkingSet']>0 else None,
              '隔离私有提交增量MiB': row['DeltaPrivateBytes']/2**20 if row['PrivateBytes']>0 else None,
              '隔离托管净增量MiB': row['ManagedDelta']/2**20}
    details.append(detail)
    groups.setdefault(category, []).append(detail)
summary = {'范围': '同档同命令隔离定位；旧数据为独立Player命令层，不是完整点击层。',
           'GameId': replay['GameId'], '条数': len(details), '流程': {},
           '限制': ['原XML仍决定回执/revision/完整摘要回归是否通过，计时报告不替代规则验证。',
                    '原生Editor隔离与独立Player、冷热、GC不同，不能把该比较当作正常UI已达标。',
                    '部分Unity Mono的Process内存API恒返回0；对应隔离进程指标标为null，不把0当作实测零占用。',
                    '工作集、私有提交、托管净变化不同；负增量是GC净结果，不能当作零分配或无泄漏。']}
for category, rows in groups.items():
    slowest = max(rows, key=lambda row: row['新隔离命令秒'])
    summary['流程'][category] = {'条数': len(rows),
        '旧独立程序同组原命令最慢秒': max(row['旧独立程序命令秒'] for row in rows),
        '新隔离同组原命令最慢秒': slowest['新隔离命令秒'],
        '新隔离至少5秒条数': sum(row['新隔离命令秒'] >= 5 for row in rows),
        '新隔离最慢原命令ID': slowest['原命令ID'], '最慢BeforeRevision': slowest['BeforeRevision']}
args.output.mkdir(parents=True)
(args.output/'同命令分组比较.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')
with (args.output/'同命令逐步比较.csv').open('w', encoding='utf-8-sig', newline='') as stream:
    writer = csv.DictWriter(stream, fieldnames=list(details[0])); writer.writeheader(); writer.writerows(details)
print(json.dumps(summary, ensure_ascii=False, indent=2))
