"""核验正常独立程序的八类单次操作P95；不把命令层或隔离重放当界面验收。"""
import argparse
import json
import pathlib
import subprocess
import sys

CATEGORIES = (
    '建设选牌、付款准备与落位', '收尾与跨回合推进', '其他正式候选与效果分支',
    '条件确认、付款与交易', '快捷或特殊行动入口', '角色选用与角色技能交互',
    '影响力移除、替换与离场响应', '城市移动及其接续',
)
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('run', type=pathlib.Path)
parser.add_argument('--target', type=float, default=6)
parser.add_argument('--classification', type=pathlib.Path)
parser.add_argument('--output', type=pathlib.Path)
args = parser.parse_args()
run = args.run.resolve()
classification = args.classification or run/'流程耗时分类'
if not (classification/'流程分类汇总.json').exists():
    subprocess.run([sys.executable, '-X', 'utf8', str(pathlib.Path(__file__).with_name('classify_hotseat_workflow_performance.py')),
                    run.name, '--output', str(classification)], check=True, stdout=subprocess.DEVNULL)
read = lambda path: json.loads(path.read_text(encoding='utf-8-sig'))
flow_report = read(classification/'流程分类汇总.json')['运行'][run.name]
outcome = read(run/'game/验证结果.json') if (run/'game/验证结果.json').exists() else {}
identity = read(run/'process.json')
build = read(run/'build-result.json')
normal = identity.get('NormalMenuFlow') is True and identity.get('StateInjection') is False and identity.get('ForcedGc') is False
normal = normal and build.get('ExitCode') == 0 and not build.get('UnexpectedInputDrift')
finished = outcome.get('Stage') == '八回合正式终局'
groups = flow_report['分层']['界面完整操作']['流程']
rows = []
for category in CATEGORIES:
    group = groups.get(category)
    rows.append({'流程': category, '样本数': group['次数'] if group else 0,
                 '中位秒': group['中位秒'] if group else None,
                 'P95秒': group['P95秒'] if group else None,
                 '最慢秒': group['最慢秒'] if group else None,
                 '已覆盖且P95达标': bool(group and group['次数'] > 0 and group['P95秒'] <= args.target)})
report = {'目标': '八类单次界面操作P95', '目标秒': args.target, '运行': run.name,
          '统计截止UTC': flow_report['最后记录UTC'], '最后回合': flow_report['最后回合'],
          '最后Revision': flow_report['最后Revision'], '正常独立程序来源核验': normal,
          '到达正式终局': finished, '流程': rows,
          '正常整局P95目标达成': normal and finished and all(row['已覆盖且P95达标'] for row in rows),
          '全功能验收通过': outcome.get('Passed', False), '保留的验收错误': outcome.get('Error'),
          '口径': ['仅已完成的ui/ui.map，命令嵌套不相加；读档排除。',
                   '沿原点击开始至夹具收尾的边界，保留0.65秒固定等待；不是下一合法控件首次可点击时间。',
                   'P95采用向上取整的最近秩；缺少任何一类或尚未终局均不宣称目标完成。',
                   '最大值和功能差异另行报告，不用平均值或总体P95代替每类P95。']}
output = args.output or run/'P95目标验收.json'
assert not output.exists(), '不能覆盖已有目标验收报告'
output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False, indent=2))
