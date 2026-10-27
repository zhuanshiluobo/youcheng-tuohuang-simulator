from pathlib import Path
import json,hashlib
base=Path('G:/NoMadCity/prompt/企业家内容实施规划-20261019');hist=base/'历史/20261028整理前';out=base/'整理记录/20261028'
p=hist/'文档/35-SelectInfluence受限候选合同说明.md'
before=p.read_text(encoding='utf-8')
old='[007证据](<../../../实施记录/QY-007-ARC-CONTRACT-REVIEW-R1/证据.json>)'
new='[007证据恢复索引](<../../../实施记录/清理旧日志旧构建-20261027-R5/第七批压缩原件清单.json>)'
if before.count(old)!=1:raise RuntimeError('预期的单一旧链接不存在，不覆盖')
note='\n> 2026-10-28链接整理：上述证据原件已于R5第七批无损封存，原路径为`实施记录/QY-007-ARC-CONTRACT-REVIEW-R1/证据.json`；SHA256 `b4445a3529261e7e15430493ddb33fee36f3865ccf212cd35ae9e8612a18a239`。请通过R5恢复脚本Batch 7恢复后阅读。本次只修正导航，没有改动或重新生成证据。\n'
p.write_text(before.replace(old,new)+note,encoding='utf-8')
mapping=json.loads((hist/'原路径映射.json').read_text(encoding='utf-8'))
for r in mapping:
    if Path(r['destination'])==p:
        r['navigableSha256']=hashlib.sha256(p.read_bytes()).hexdigest();r['linkAdjustments']+=1;r['archivedEvidenceLinkCorrection']=True
(hist/'原路径映射.json').write_text(json.dumps(mapping,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
result=dict(path=str(p),originalLink=old,replacement=new,reason='原证据已在R5封存，非本次文档移动丢失',evidenceBytes=138850,evidenceSha256='b4445a3529261e7e15430493ddb33fee36f3865ccf212cd35ae9e8612a18a239',externalPromptOriginal=dict(path=str(hist/'外部prompt入口原文.md'),sha256=hashlib.sha256((hist/'外部prompt入口原文.md').read_bytes()).hexdigest()))
(out/'历史失效引用修复.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('已修复1处已封存证据导航；原字节仍在快照中。')
