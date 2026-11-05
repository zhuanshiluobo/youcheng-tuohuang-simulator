"""按已有边界拆分耗时；不把未分配的帧时间当成夹具耗时。"""
import csv
import datetime as dt
import json
import pathlib
import sys

run=pathlib.Path(sys.argv[1]); game=run/'game'
def read(name):
    result=[]; path=game/name
    if path.exists():
        for line in path.read_text(encoding='utf-8-sig').splitlines():
            try: result.append(json.loads(line))
            except json.JSONDecodeError: pass
    return result
def time(value): return dt.datetime.fromisoformat(value)
rows=[r for r in read('逐步性能.jsonl') if r.get('Boundary')=='end']
groups={}
for r in read('控件回调边界.jsonl'):
    groups.setdefault(r['Index'],[]).append(r)
starts={}
for index,group in groups.items():
    begin=next((r for r in group if r['Callback']=='PointerDown' and not r['Returned']),None)
    if begin: starts.setdefault(begin['Description'],[]).append((time(begin['AtUtc']),index))
flat=[]
for r in rows:
    if r['Kind']!='ui' or r['Name'] not in starts: continue
    stamp,index=min(starts[r['Name']],key=lambda pair:abs((pair[0]-time(r['Before']['Utc'])).total_seconds()))
    if abs((stamp-time(r['Before']['Utc'])).total_seconds())>0.2: continue
    group=groups[index]
    def duration(callback):
        pair={v['Returned']:time(v['AtUtc']) for v in group if v['Callback']==callback}
        return (pair[True]-pair[False]).total_seconds() if len(pair)==2 else None
    click,observation=duration('PointerClick'),duration('ObserveAndSave')
    flat.append({'样本':r['Index'],'回合':r['After']['Round'],'操作':r['Name'],'操作总秒':r['Seconds'],
        '原PointerClick回调秒':click,'夹具事后取证秒':observation,
        '其余秒_包含帧刷新等待及其他开销':r['Seconds']-(click or 0)-(observation or 0),
        '游戏Submit秒':r['TimingDelta'].get('submit.total.ticks',0)/r['Frequency'],
        '游戏规则Handler秒':r['TimingDelta'].get('submit.handler.ticks',0)/r['Frequency']})
if flat:
    with (run/'控件耗时归因.csv').open('w',encoding='utf-8-sig',newline='') as f:
        writer=csv.DictWriter(f,fieldnames=list(flat[0]));writer.writeheader();writer.writerows(flat)
commands=[r for r in rows if r['Kind']=='command']
largest=max(commands,key=lambda r:r['Seconds']) if commands else None
callers=read('Lua查询调用来源.jsonl')
fixture_examples=[r for r in callers if 'BuildDraftViewModel' in r.get('Stack','')
    and 'HotseatUiValidationDriver' in r.get('Stack','') and 'ExecuteEvents.Execute' not in r.get('Stack','')]
report={'原命令最慢实例':None if largest is None else {'操作':largest['Name'],'回合':largest['After']['Round'],
    'BeforeRevision':largest['Before']['Revision'],'AfterRevision':largest['After']['Revision'],
    'Submit秒':largest['Seconds'],'规则Handler秒':largest['TimingDelta'].get('submit.handler.ticks',0)/largest['Frequency']},
    '控件实例':sorted(flat,key=lambda r:r['操作总秒'],reverse=True)[:5],
    '夹具主动查询实例':fixture_examples[:3],
    '限制':['原PointerClick回调包含产品代码及夹具命令观察器，不能把整个回调标成纯游戏CPU。',
        '其余时间包含Unity帧刷新、夹具固定0.65秒等待、日志和系统调度，不能全部计为夹具开销。',
        'Lua调用栈为有上限的首次实例采样，不能按条数推算耗时占比。',
        '工作集和托管保留堆没有按根对象归因；不能据这些计时给出游戏/夹具的内存百分比。']}
if rows:
    final=rows[-1]['After'].get('Timings') or {}; frequency=rows[-1]['Frequency']
    phases=sorted(set(('lua.context.input','lua.context.fixture','lua.context.frame','fixture.append','fixture.write','fixture.capture')) |
        {key[:-6] for key in final if key.endswith('.ticks') and key.startswith(('query.input.','query.fixture.','query.frame.','input.','save.'))})
    if any(final.get(phase+'.calls',0) for phase in phases):
        report['会话绑定后来源阶段累计_非整操作比例']={phase:{'Calls':final.get(phase+'.calls',0),'Seconds':final.get(phase+'.ticks',0)/frequency} for phase in phases}
        report['限制'].append('query.input/query.frame是产品输入/帧查询，query.fixture是夹具主动查询；input、Submit与子阶段有包含关系。后台save可与前台并行，以上阶段均不能直接相加。')
(run/'耗时归因.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'控件实例数':len(flat),'夹具主动查询实例数_非占比':len(fixture_examples),
                  '原命令最慢实例':report['原命令最慢实例']},ensure_ascii=False))
