"""只读既有采样，按实际控件/原命令上下文归类，不跨版本叠加、不双计嵌套命令。"""
import collections
import argparse
import csv
import json
import math
import pathlib
import statistics
import sys

root=pathlib.Path('G:/NoMadCity/Logs/HotseatPerf-20261103')
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('tags',nargs='*')
parser.add_argument('--output',type=pathlib.Path)
args=parser.parse_args()
tags=args.tags or ['R4','R6']
output=args.output or root/'流程耗时分类';output.mkdir(exist_ok=True)

def read(path):
    values=[]
    if path.exists():
        for line in path.read_text(encoding='utf-8-sig').splitlines():
            try:values.append(json.loads(line))
            except json.JSONDecodeError:pass
    return values

def category(row,context):
    name=context.get('ControlName',row['Name']);stage=' '.join(context['Stages']);cmds=context['Commands']
    if row['Before']['Round']==0:return '菜单与读档','菜单/读档阶段'
    if row['After']['Round']!=row['Before']['Round']:return '收尾与跨回合推进','操作前后回合变化'
    if 'EndAction' in cmds or any(s in name for s in ('完成主行动后先结束','收尾首次结束','收尾原来源')):
        return '收尾与跨回合推进','结束行动/正式收尾来源'
    if any(s in name for s in ('页签','当前宣告不可达','入口返回原主要行动','特殊入口缺口')):
        return '页签与非提交导航','导航控件'
    if any(s in name for s in ('实际供应区选择','正常建设','正常供应原确认')) or any(c in cmds for c in ('PreparePaymentDraft','ConfirmPaymentPreparation','BuildFacility','SwitchPreparedPaymentMode')):
        return '建设选牌、付款准备与落位','明确建设控件/建设付款命令'
    if '角色移动' in name or '原角色卡选择' in name or 'character.ability' in stage:
        return '角色选用与角色技能交互','角色技能请求/控件'
    if 'MoveCity' in cmds or 'effect.city.move.target' in stage or any(s in name for s in ('主要移城','进入正常移城','移城余款')):
        return '城市移动及其接续','移城命令/目标/付款接续'
    if any(s in stage for s in ('effect.influence.remove','effect.influence.replace')):
        return '影响力移除、替换与离场响应','原请求为影响力移除/替换'
    if any(s in name for s in ('采集','探索','航道路费','随机事件')) or any(s in stage for s in ('effect.route.choose','collection.task')):
        return '采集、探索、路费与事件','资源/探索/航道控件或请求'
    if any(s in name for s in ('部署','调度')) or any(c in cmds for c in ('DeployInfluence','DispatchInfluence','MoveInfluence')):
        return '部署与调度','正式部署/调度控件或命令'
    if any(s in name for s in ('企业/科室','企业家正式候选','企业家原确认')) or any(s in stage for s in ('enterprise.choose','entrepreneur.choose')):
        return '企业与企业家选择','明确企业/企业家选择控件'
    if any(s in name for s in ('条件与付款','资源付款','交易','出售')) or 'Payment' in ' '.join(cmds):
        return '条件确认、付款与交易','付款/条件/交易控件；不代表只执行扣款'
    if any(s in name for s in ('城市样式','宣告')):
        return '城市样式宣告','正式样式宣告控件'
    if any(s in name for s in ('原快速行动行','特殊行动入口','整局正常主要行动')) or any(c in cmds for c in ('UseSpecialAction','InvokeAbilityAction','UseCharacterAbility')):
        return '快捷或特殊行动入口','行动原入口/行'
    if row['Kind']=='ui.map':return '其他地图预选与目标交互','没有足够上下文细分的地图步骤'
    if 'effect.choice.execute' in stage or any(s in name for s in ('选择一个合法分支','选择牌','牌选择','供应选择真实顶牌')):
        return '其他正式候选与效果分支','正式候选/通用效果分支'
    return '其他原控件操作','未按名称强行推断业务'

def describe(values,total):
    seconds=sorted(v['Seconds'] for v in values)
    slow=max(values,key=lambda v:v['Seconds'])
    return {'次数':len(values),'累计秒':sum(seconds),'占本层已测耗时百分比':100*sum(seconds)/total if total else 0,
            '中位秒':statistics.median(seconds),'P95秒':seconds[math.ceil(.95*len(seconds))-1],
            '最慢秒':seconds[-1],'至少5秒次数':sum(s>=5 for s in seconds),'至少10秒次数':sum(s>=10 for s in seconds),
            '最慢实例':{k:slow[k] for k in ('Name','Kind','Index')},
            '最慢实例BeforeRevision':slow['Before']['Revision'],'最慢实例AfterRevision':slow['After']['Revision'],
            '最慢实例BeforeRound':slow['Before']['Round'],'最慢实例AfterRound':slow['After']['Round'],
            '最慢实例阶段秒_含嵌套不能相加':{k:round(v/slow['Frequency'],6) for k,v in slow['TimingDelta'].items()
                if k.endswith('.ticks') and v and k.split('.ticks')[0] in (
                    'submit.total','submit.handler','handler.clone','handler.copy','runtime.validate','rule.digest',
                    'kernel.attachment','lua.context','lua.context.input','lua.context.fixture','lua.context.frame',
                    'ui.full','input.callback','fixture.append','fixture.write','fixture.capture')}}

report={'口径':['UI/ui.map与command分层统计，命令已嵌套于UI，不相加。',
    '各版本分别统计；累计秒仅指有完整end记录的受测操作，排除菜单后计算局内占比，不等于整个进程运行时间。',
    '分类按原控件、原命令及其请求上下文；跨回合推进优先单列，避免把整条结算误称为单纯付款。',
    '通用分支/地图步骤没有足够上下文时保留其他类别，不猜测为具体业务。',
    'R4覆盖第4～7回合，R6为第5～6回合现有部分记录，不据不同覆盖范围直接计算修复收益。'], '运行':{}}
details=[]
for tag in tags:
    game=root/tag/'game'
    rows=[r for r in read(game/'逐步性能.jsonl') if r.get('Boundary')=='end']
    commands=[r for r in rows if r['Kind']=='command']
    controls=[r for r in rows if r['Kind']!='command']
    receipts={r['Command']['CommandId']:r for r in read(game/'命令与准确回执.jsonl')}
    classified=[]
    for row in rows:
        if row['Kind']=='command':nested=[row]
        else:
            began=row['Before']['Tick'];ended=began+row['Seconds']*row['Frequency']
            nested=[c for c in commands if c['Before']['Tick']>=began and c['Before']['Tick']+c['Seconds']*c['Frequency']<=ended+row['Frequency']*.01]
        context={'Commands':[c['Name'] for c in nested], 'Stages':[receipts[c['CommandId']]['Stage'] for c in nested if c.get('CommandId') in receipts]}
        if row['Kind']=='command':
            began=row['Before']['Tick'];ended=began+row['Seconds']*row['Frequency']
            parents=[u for u in controls if u['Before']['Tick']<=began and u['Before']['Tick']+u['Seconds']*u['Frequency']+u['Frequency']*.01>=ended]
            if parents:context['ControlName']=min(parents,key=lambda u:u['Seconds'])['Name']
        group,reason=category(row,context);classified.append((row,group))
        details.append({'运行':tag,'样本':row['Index'],'层':row['Kind'],'流程':group,'归类依据':reason,
            '控件或命令':row['Name'],'操作前回合':row['Before']['Round'],'操作后回合':row['After']['Round'],
            'BeforeRevision':row['Before']['Revision'],'AfterRevision':row['After']['Revision'],'耗时秒':row['Seconds'],
            '原命令':';'.join(context['Commands']),'原请求上下文':';'.join(context['Stages']),'所属原控件':context.get('ControlName',row['Name'])})
    sections={}
    for layer in ('界面完整操作','游戏命令'):
        subset=[(r,g) for r,g in classified if (r['Kind']=='command')==(layer=='游戏命令')]
        in_game=[(r,g) for r,g in subset if g!='菜单与读档']
        total=sum(r['Seconds'] for r,g in in_game);buckets=collections.defaultdict(list)
        for row,group in in_game:buckets[group].append(row)
        sections[layer]={'局内记录数':len(in_game),'局内已测累计秒':total,
            '流程':dict(sorted(((g,describe(v,total)) for g,v in buckets.items()),key=lambda p:p[1]['累计秒'],reverse=True))}
        menus=[r for r,g in subset if g=='菜单与读档']
        if menus:sections[layer]['菜单与读档单列']=describe(menus,sum(r['Seconds'] for r in menus))
        assert sum(v['次数'] for v in sections[layer]['流程'].values())==len(in_game)
        assert math.isclose(sum(v['累计秒'] for v in sections[layer]['流程'].values()),total,rel_tol=1e-9)
    report['运行'][tag]={'最后记录UTC':rows[-1]['After']['Utc'],'最后回合':rows[-1]['After']['Round'],
        '最后Revision':rows[-1]['After']['Revision'],'分层':sections}
(output/'流程分类汇总.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
with (output/'逐步流程分类.csv').open('w',encoding='utf-8-sig',newline='') as f:
    writer=csv.DictWriter(f,fieldnames=list(details[0]));writer.writeheader();writer.writerows(details)
for tag,data in report['运行'].items():
    print(tag,data['最后回合'],data['最后Revision'],data['最后记录UTC'])
    for layer,section in data['分层'].items():
        print(layer,section['局内记录数'],round(section['局内已测累计秒'],1))
        for name,values in section['流程'].items():
            print(name,values['次数'],round(values['累计秒'],1),round(values['占本层已测耗时百分比'],1),round(values['中位秒'],2),round(values['P95秒'],2),round(values['最慢秒'],2),values['至少10秒次数'])
