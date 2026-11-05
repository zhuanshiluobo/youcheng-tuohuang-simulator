import csv,json,pathlib,sys,statistics,os,time
p=pathlib.Path(sys.argv[1]); lines=(p/'game/逐步性能.jsonl').read_text(encoding='utf-8-sig').splitlines()
rows=[]
for line in lines:
    try: r=json.loads(line)
    except json.JSONDecodeError: continue
    if r.get('Boundary')=='end': rows.append(r)
flat=[]
for r in rows:
    flat.append(dict(Index=r['Index'],Kind=r['Kind'],Name=r['Name'],Round=r['After']['Round'],InGame=r['Before']['Round']>0,
        Revision=r['After']['Revision'],Seconds=r['Seconds'],Utc=r['Before']['Utc'],CommandId=r.get('CommandId'),WorkingSetMiB=r['After']['WorkingSet']/2**20,
        PrivateMiB=r['After']['Private']/2**20,ManagedMiB=r['After']['Managed']/2**20,MonoHeapMiB=r['After']['MonoHeap']/2**20,
        DeltaMonoHeapMiB=(r['After']['MonoHeap']-r['Before']['MonoHeap'])/2**20,
        FrameDelta=r['After'].get('Frame',0)-r['Before'].get('Frame',0) if 'Frame' in r['Before'] else None,
        UnityAllocatedMiB=r['After']['UnityAllocated']/2**20 if 'UnityAllocated' in r['After'] else None,
        UnityReservedMiB=r['After']['UnityReserved']/2**20 if 'UnityReserved' in r['After'] else None,
        ArtworkKnownMiB=r['After']['Artwork']['KnownTextureBytes']/2**20 if r['After'].get('Artwork',{}).get('CurrentValuesAvailable') else None,
        ArtworkUnknownEntries=r['After']['Artwork']['UnknownTextureEntries'] if r['After'].get('Artwork',{}).get('CurrentValuesAvailable') else None,
        IconKnownMiB=r['After']['Icons']['KnownTextureBytes']/2**20 if r['After'].get('Icons',{}).get('CurrentValuesAvailable') else None,
        IconUnknownEntries=r['After']['Icons']['UnknownTextureEntries'] if r['After'].get('Icons',{}).get('CurrentValuesAvailable') else None,
        DeltaWorkingSetMiB=r['DeltaWorkingSet']/2**20,DeltaPrivateMiB=r['DeltaPrivate']/2**20,DeltaManagedMiB=r['DeltaManaged']/2**20,
        **{k: v/r['Frequency'] for k,v in r['TimingDelta'].items() if k.endswith('.ticks')}))
if flat:
    keys=list(dict.fromkeys(k for r in flat for k in r))
    with (p/'逐步性能.csv').open('w',encoding='utf-8-sig',newline='') as f:
        w=csv.DictWriter(f,fieldnames=keys); w.writeheader(); w.writerows(flat)
summary={}
for kind in ('command','ui','ui.map'):
    selected=[r for r in flat if r['Kind']==kind and r['InGame']]
    if not selected: continue
    summary[kind]={'Count':len(selected),'MaxSeconds':max(r['Seconds'] for r in selected),
        'TargetSeconds':5,'OverFiveSeconds':sum(r['Seconds']>=5 for r in selected),
        'OverTenSeconds':sum(r['Seconds']>=10 for r in selected),'LastRound':selected[-1]['Round'],
        'LastWorkingSetMiB':selected[-1]['WorkingSetMiB'],'LastPrivateMiB':selected[-1]['PrivateMiB'],
        'Slowest':sorted(selected,key=lambda r:r['Seconds'],reverse=True)[:3]}
rounds=[]
for number in sorted({r['Round'] for r in flat}):
    selected=[r for r in flat if r['Round']==number]
    commands=[r for r in selected if r['Kind']=='command']
    rounds.append(dict(Round=number,Samples=len(selected),Commands=len(commands),
        MaxSeconds=max(r['Seconds'] for r in selected),MaxCommandSeconds=max([r['Seconds'] for r in commands] or [0]),
        OverFiveSeconds=sum(r['InGame'] and r['Seconds']>=5 for r in selected),OverTenSeconds=sum(r['InGame'] and r['Seconds']>=10 for r in selected),
        FirstWorkingSetMiB=selected[0]['WorkingSetMiB'],LastWorkingSetMiB=selected[-1]['WorkingSetMiB'],
        PeakWorkingSetMiB=max(r['WorkingSetMiB'] for r in selected),
        LastPrivateMiB=selected[-1]['PrivateMiB'],LastManagedMiB=selected[-1]['ManagedMiB'],LastMonoHeapMiB=selected[-1]['MonoHeapMiB']))
summary['Rounds']=rounds
menus=[r for r in flat if not r['InGame']]
summary['MenusAndLoad']={'Count':len(menus),'MaxSeconds':max([r['Seconds'] for r in menus] or [0]),'OutsideTargetScope':True}
summary['Notes']=['command嵌套于UI，不能相加当成总操作时间；UI包含正常同步和0.65秒夹具等待。',
    '按用户2026-11-04指示，菜单与读档单列保留原始数据，不纳入本轮5秒目标；局内操作按Before.Round>0识别。',
    'DeltaManaged为托管使用净变化，不是累计分配量；工作集、私有提交量和托管使用量分别报告。',
    'MonoHeap为Unity托管保留堆，不能当作仍存活的业务对象总量；FrameDelta仅新版本探针提供。',
    'UnityAllocated/UnityReserved仅新版探针提供，旧记录为null；这些引擎统计不能与进程私有提交或MonoHeap直接相加。',
    'Artwork/Icon为原资源缓存诊断的已知纹理估算及未知条目数；旧包或未启用时为null，不代表完整OS/GPU内存归属。',
    'Round=0为菜单/读档；未强制GC；原始jsonl保留begin/end边界。']
summary_pending=p/f'性能汇总.{os.getpid()}.tmp'
summary_pending.write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
for attempt in range(20):
    try:
        summary_pending.replace(p/'性能汇总.json')
        break
    except PermissionError:
        if attempt==19: raise
        time.sleep(0.05)  # Windows读者可能短暂持有不允许替换的共享句柄。
print(json.dumps(summary,ensure_ascii=False,indent=2))
