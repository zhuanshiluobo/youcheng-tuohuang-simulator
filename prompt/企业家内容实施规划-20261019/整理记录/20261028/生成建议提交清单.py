from pathlib import Path
import json, hashlib, datetime
OUT=Path(__file__).resolve().parent;ROOT=Path('G:/NoMadCity')
rows=json.loads((OUT/'Git工作区候选.json').read_text(encoding='utf-8-sig'))
included=[];excluded=[]
for row in rows:
    entry=dict(row)
    if row['path']=='-.png' or row['group'] in ('待逐项确认','美术制作源待确认必要性'):
        entry['reason']='已有改动保留现场；本次未核定该删除/制作源与本轮提交关系，不自动暂存'
        excluded.append(entry);continue
    if '整理记录/20261028/' not in row['path']:
        p=ROOT/row['path']
        if p.is_file():entry['sha256AtPreparation']=hashlib.sha256(p.read_bytes()).hexdigest()
    entry['decision']='建议纳入；后继核017完成后的新增变化，按精确路径暂存'
    included.append(entry)
for name in ('建议提交文件.json','不纳入提交文件.json','提交范围校验.json','整理完整性与链接核验.json','最终交接.json'):
    path=(OUT/name).relative_to(ROOT).as_posix()
    if not any(r['path']==path for r in included):included.append(dict(path=path,group='工作文档',status='待写入/本轮清单',decision='整理记录；实际存在时纳入，无需扫描实施记录'))
policy=['Builds/','Build/','Artifacts/','Logs/','TestResults/','Library/','Temp/','.codex-tmp/','tmp/','存档/','**/实施记录/大型原件或完整输入树','游城拓荒/UI素材/**','旧UI原档案整理副本']
excluded.append(dict(policyOnly=True,excludedScopes=policy,reason='仅列范围，不枚举受限原档案或在途构建；实施记录原件只本地保留'))
for name,obj in [('建议提交文件.json',included),('不纳入提交文件.json',excluded)]:
    (OUT/name).write_text(json.dumps(obj,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
summary=dict(preparedAt=datetime.datetime.now(datetime.timezone(datetime.timedelta(hours=8))).isoformat(),included=len(included),excludedExplicit=sum('path' in r for r in excluded),trackedChangesIncluded=sum(r.get('status') not in ('??','待写入/本轮清单') for r in included),sensitiveOutputContainsValues=False,staged=False,committed=False,pushed=False,large50MiB=sum((r.get('bytes') or 0)>=50*1024**2 for r in included),lfsCount=sum(r.get('lfs',False) for r in included),includedBytes=sum(r.get('bytes') or 0 for r in included),note='本清单只读准备，017完成后需重新核源身份与工作区新增变化；整理输出自身不锁固定SHA，避免自引用。')
(OUT/'提交范围校验.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(summary,ensure_ascii=False,indent=2))
