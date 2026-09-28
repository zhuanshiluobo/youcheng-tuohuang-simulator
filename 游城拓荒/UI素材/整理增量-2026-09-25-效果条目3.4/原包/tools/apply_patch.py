"""Install EffectRows v3.4 over the supplied v3.2 or v3.3 presentation baseline."""
from pathlib import Path
import argparse, datetime, hashlib, json, shutil

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def main():
    parser = argparse.ArgumentParser(description='安装暖棕通用条目补丁，不执行游戏规则喵')
    parser.add_argument('baseline', type=Path, help='完整主界面根目录（v3.2 或 v3.3）')
    args = parser.parse_args()
    package = Path(__file__).resolve().parents[1]
    target = args.baseline.expanduser().resolve()
    contract = json.loads((package/'patch-manifest.json').read_text(encoding='utf-8'))
    if not target.is_dir():
        parser.error('目标目录不存在喵')
    receipt = target/'effect-assets-v3.4'/'installation.json'
    if receipt.exists():
        old = json.loads(receipt.read_text(encoding='utf-8'))
        if old.get('patchId') == contract['patchId'] and all((target/x['destination']).is_file() and sha(target/x['destination']) == x['sha256'] for x in contract['files']):
            print('暖棕通用条目补丁已经安装，无需重复覆盖喵')
            return
        parser.error('目标中的 v3.4 文件已有其他改动，请保留这些改动并在干净基线副本上安装喵')
    baseline = next((p['version'] for p in contract['acceptedBaselines'] if all((target/rel).is_file() and sha(target/rel) == expected for rel,expected in p['hashes'].items())), None)
    if baseline is None:
        parser.error('关键文件与提供的 v3.2、v3.3 基线均不一致，尚未写入任何文件喵')
    operations = []
    for item in contract['files']:
        src=(package/item['source']).resolve(); dst=(target/item['destination']).resolve()
        if not src.is_relative_to(package) or not dst.is_relative_to(target):
            parser.error('补丁路径越界，已停止喵')
        if not src.is_file() or sha(src) != item['sha256']:
            parser.error('补丁文件不完整：'+item['source']+'喵')
        operations.append((src,dst))
    backup = target/('effect-row-v34-backup-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f'))
    backup.mkdir()
    written=[]
    try:
        for src,dst in operations:
            saved=backup/dst.relative_to(target)
            if dst.exists():
                saved.parent.mkdir(parents=True,exist_ok=True); shutil.copy2(dst,saved)
            dst.parent.mkdir(parents=True,exist_ok=True)
            written.append((dst,saved)); shutil.copy2(src,dst)
        receipt.parent.mkdir(parents=True,exist_ok=True)
        receipt.write_text(json.dumps({'patchId':contract['patchId'],'version':'3.4','baseline':baseline,'backup':str(backup),'changedFiles':len(operations)},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    except Exception:
        for dst,saved in reversed(written):
            if saved.exists(): shutil.copy2(saved,dst)
            elif dst.exists(): dst.unlink()
        raise
    print('已从 '+baseline+' 安装暖棕通用条目，打开 preview/index.html 查看喵')
    print('旧文件备份：'+str(backup)+'喵')

if __name__=='__main__':
    main()
