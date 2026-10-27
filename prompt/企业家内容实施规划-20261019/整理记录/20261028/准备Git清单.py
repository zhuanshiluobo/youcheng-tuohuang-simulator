"""只读准备精确提交候选；不暂存、提交、推送或读取受限原档案。"""
from pathlib import Path
import subprocess, json, hashlib, re, collections
ROOT=Path('G:/NoMadCity'); OUT=Path(__file__).resolve().parent
EXCLUDES=['游城拓荒/UI素材','游城拓荒/UI素材/**','**/UI素材/**','Builds/**','Build/**','Logs/**','TestResults/**','Artifacts/**','Library/**','Temp/**','obj/**','Obj/**','.codex-tmp/**','tmp/**','.git/**','**/实施记录/**','prompt/UI换新/执行记录/**','prompt/卡牌Sprite切片替换/**','**/__pycache__/**']
def git(*args, data=None):
    p=subprocess.run(['git','-c','core.longpaths=true','-c','core.quotepath=false','--no-optional-locks','-C',str(ROOT),*args],capture_output=True,input=data)
    if p.returncode: raise RuntimeError(f'Git只读命令失败: {args[:2]}，退出码{p.returncode}')
    return p.stdout
spec=['.']+[':(exclude,glob)'+p for p in EXCLUDES]
parts=git('status','--porcelain=v1','-z','--untracked-files=all','--',*spec).decode('utf-8').split('\0')
rows=[];i=0
while i<len(parts):
    value=parts[i];i+=1
    if not value: continue
    status,path=value[:2],value[3:];old=None
    if any(x in status for x in 'RC'):
        old=parts[i];i+=1
    if 'UI素材' in Path(path).parts: raise RuntimeError('受限路径不应进入清单')
    p=ROOT/path
    exists=p.exists();size=p.stat().st_size if exists and p.is_file() else None
    if path.startswith(('Assets/','Packages/','ProjectSettings/')): group='生产代码资源及关联验证'
    elif path.startswith('tools/'):group='工具'
    elif path.startswith('游城拓荒/可用UI素材/'):group='可用素材和许可清单'
    elif path.startswith('游城拓荒/'):group='规则及内容参考文档'
    elif path.startswith('ArtSource/'):group='美术制作源待确认必要性'
    elif path.startswith('prompt/'):group='工作文档'
    elif path in ('.gitignore','.gitattributes','.rgignore','AGENTS.md','README.md','steam_appid.txt'):group='仓库配置说明'
    else:group='待逐项确认'
    rows.append(dict(status=status,path=path,oldPath=old,bytes=size,group=group))
attrs=git('check-attr','-z','--stdin','filter',data=('\0'.join(r['path'] for r in rows)+'\0').encode()) if rows else b''
attr=attrs.decode().split('\0');filters={attr[n]:attr[n+2] for n in range(0,len(attr)-2,3)}
secret=[]
patterns=[('私钥头',re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----')),('GitHub令牌',re.compile(r'\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})\b')),('云访问密钥',re.compile(r'\bAKIA[A-Z0-9]{16}\b')),('OpenAI形式密钥',re.compile(r'\bsk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{40,}\b'))]
text_ext={'.cs','.lua','.json','.md','.txt','.py','.ps1','.yml','.yaml','.toml','.ini','.config','.xml','.env','.pem','.key','.sh','.bat','.cmd','.iss'}
for r in rows:
    r['lfs']=filters.get(r['path'])=='lfs'
    p=ROOT/r['path']
    if r['bytes'] is None or r['bytes']>2*1024**2 or (p.suffix.lower() not in text_ext and not p.name.startswith('.')):continue
    try:content=p.read_text(encoding='utf-8-sig')
    except (UnicodeError, OSError):continue
    for name,pattern in patterns:
        for m in pattern.finditer(content):secret.append(dict(path=r['path'],line=content.count('\n',0,m.start())+1,kind=name))
    if p.suffix.lower() in {'.pem','.key','.env'}:secret.append(dict(path=r['path'],line=None,kind='凭证常用文件名需人工复核'))
summary=dict(branch=git('branch','--show-current').decode().strip(),upstream=git('rev-parse','--abbrev-ref','--symbolic-full-name','@{upstream}').decode().strip(),head=git('rev-parse','HEAD').decode().strip(),remoteTxym=git('rev-parse','origin/txym').decode().strip(),aheadBehind=git('rev-list','--left-right','--count','HEAD...origin/txym').decode().strip(),excluded=EXCLUDES,counts=dict(collections.Counter(r['group'] for r in rows)),large50MiB=[r for r in rows if (r['bytes'] or 0)>=50*1024**2],secretLocations=secret,rows=len(rows),trackedChanged=sum(r['status']!='??' for r in rows),untracked=sum(r['status']=='??' for r in rows),note='不读取Builds或在途实施记录，不扫描受限原档案；候选不是已暂存清单。敏感项只记录位置，未输出内容。')
for name,obj in [('Git工作区候选.json',rows),('Git范围摘要.json',summary)]:
    (OUT/name).write_text(json.dumps(obj,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({k:v for k,v in summary.items() if k not in ('excluded','large50MiB','secretLocations')},ensure_ascii=False,indent=2))
print(json.dumps({'large50MiB':summary['large50MiB'],'secretLocations':secret},ensure_ascii=False,indent=2))

