"""核对隔离四端局的完成报告、实际退出码和终局后退出延迟。"""
import argparse
from datetime import datetime
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
    raise ValueError('四端对局与输出必须位于 TestResults 的明确子目录。')


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


exits = read(cohort / '实际退出记录.json')
roles = ('host', 'client2', 'client3', 'client4')
if set(exits) != set(roles):
    raise ValueError('实际退出记录不含完整四端。')
rows = []
for role in roles:
    path = cohort / role / '验证结果.json'
    report, exit_record = read(path), exits[role]
    ended = datetime.fromisoformat(exit_record['EndedUtc'].replace('Z', '+00:00')).timestamp()
    latency = ended - path.stat().st_mtime
    valid = bool(report.get('Passed') and report.get('FullGameCompleted') and
                 report.get('NaturalExitRequested') and report.get('DeferredContentDifferences') == 0 and
                 exit_record.get('NaturalExit') and exit_record.get('ExitCode') == 0 and -1 <= latency <= 120)
    rows.append({'role': role, 'exitCode': exit_record.get('ExitCode'), 'naturalExit': exit_record.get('NaturalExit'),
                 'secondsSinceReport': max(0, latency), 'exitWithin120Seconds': -1 <= latency <= 120,
                 'reportPassed': report.get('Passed'), 'fullGameCompleted': report.get('FullGameCompleted'), 'passed': valid})
result = {'passed': all(row['passed'] for row in rows), 'roles': rows,
          'scope': '仅核对本机四端终局报告及实际退出；不能替代真实 Steam 跨机器验证。'}
with output.open('x', encoding='utf-8') as stream:
    json.dump(result, stream, ensure_ascii=False, indent=2)
print('四端终局退出核验：', '通过' if result['passed'] else '失败；保留各端原始退出记录。')
if not result['passed']:
    raise SystemExit(1)
