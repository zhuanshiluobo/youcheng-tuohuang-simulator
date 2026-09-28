"""Install the v3.2 presentation patch over the supplied v3.1 preview package."""
from pathlib import Path
import argparse, datetime, hashlib, json, shutil, sys

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def main():
    parser=argparse.ArgumentParser(description='安装按钮外观补丁，不执行游戏规则喵')
    parser.add_argument('baseline',type=Path,help='已解压的 FrontierUI-v3-Patch 目录')
    args=parser.parse_args()
    package=Path(__file__).resolve().parents[1]
    target=args.baseline.expanduser().resolve()
    contract=json.loads((package/'patch-manifest.json').read_text(encoding='utf-8'))
    if not target.is_dir():
        parser.error('目标目录不存在喵')
    receipt=target/'button-assets-v3.2'/'installation.json'
    if receipt.exists():
        old=json.loads(receipt.read_text(encoding='utf-8'))
        if old.get('patchId')==contract['patchId'] and all((target/x['destination']).is_file() and sha(target/x['destination'])==x['sha256'] for x in contract['files']):
            print('此按钮补丁已经安装，无需重复覆盖喵')
            return
        parser.error('目标已存在不同内容的 v3.2 按钮目录，请保留该目录并将本补丁应用到干净的 v3.1 副本喵')
    for rel,expected in contract['requiredBaselineHashes'].items():
        p=target/rel
        if not p.is_file() or sha(p)!=expected:
            parser.error('基线文件与 v3.1 不一致，尚未写入任何文件：'+rel+'喵')
    operations=[]
    for item in contract['files']:
        src=(package/item['source']).resolve();dst=(target/item['destination']).resolve()
        if not src.is_relative_to(package) or not dst.is_relative_to(target):
            parser.error('补丁路径越界，已停止喵')
        if not src.is_file() or sha(src)!=item['sha256']:
            parser.error('补丁文件不完整：'+item['source']+'喵')
        operations.append((src,dst))
    stamp=datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f')
    backup=target/('button-patch-backup-'+stamp)
    backup.mkdir()
    written=[]
    try:
        for src,dst in operations:
            relative=dst.relative_to(target)
            if dst.exists():
                saved=backup/relative;saved.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(dst,saved)
            dst.parent.mkdir(parents=True,exist_ok=True)
            written.append((dst,backup/relative))
            shutil.copy2(src,dst)
        receipt.parent.mkdir(parents=True,exist_ok=True)
        receipt.write_text(json.dumps({'patchId':contract['patchId'],'version':'3.2','backup':str(backup),'changedFiles':len(operations)},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    except Exception:
        for dst,saved in reversed(written):
            if saved.exists():shutil.copy2(saved,dst)
            elif dst.exists():dst.unlink()
        raise
    print('按钮补丁已安装，打开目标目录的 preview/index.html 查看主界面喵')
    print('旧文件备份：'+str(backup)+'喵')

if __name__=='__main__':
    main()
