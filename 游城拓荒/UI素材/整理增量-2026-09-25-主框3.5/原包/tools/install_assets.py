"""Install frame resources and, if requested, merge four catalog entries."""
from pathlib import Path
import argparse, datetime, hashlib, json, shutil

def digest(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()

def main():
    parser=argparse.ArgumentParser(description='安装主界面细边框资源，不修改项目源码')
    parser.add_argument('target',type=Path,help='主界面资源根目录')
    parser.add_argument('--assets-only',action='store_true',help='只复制资源，由引擎手动导入')
    args=parser.parse_args()
    root=Path(__file__).resolve().parents[1];target=args.target.expanduser().resolve()
    if not target.is_dir():parser.error('目标目录不存在')
    spec=json.loads((root/'patch-manifest.json').read_text(encoding='utf-8'))
    override=json.loads((root/'integration/catalog-overrides.json').read_text(encoding='utf-8'))['assets']
    catalog_path=target/'asset-manifest.json';catalog=None
    if not args.assets_only:
        if not catalog_path.is_file():parser.error('未找到 asset-manifest.json；手动导入时请使用 --assets-only')
        catalog=json.loads(catalog_path.read_text(encoding='utf-8'))
        if not isinstance(catalog.get('assets'),dict) or any(k not in catalog['assets'] for k in override):parser.error('目标清单结构或素材 ID 不匹配；未写入任何文件')
    operations=[]
    for item in spec['files']:
        src=(root/item['source']).resolve();dst=(target/item['destination']).resolve()
        if not src.is_relative_to(root) or not dst.is_relative_to(target):parser.error('路径越界')
        if not src.is_file() or digest(src)!=item['sha256']:parser.error('补丁内容不完整：'+item['source'])
        operations.append((src,dst))
    files_match=all(dst.is_file() and digest(src)==digest(dst) for src,dst in operations)
    catalog_match=args.assets_only or all(catalog['assets'].get(k)==v for k,v in override.items())
    if files_match and catalog_match:
        print('边框资源已安装，无需重复写入');return
    backup=target/('frame-patch-backup-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f'));backup.mkdir()
    written=[]
    def prepare(dst):
        saved=backup/dst.relative_to(target)
        if dst.exists():saved.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(dst,saved)
        dst.parent.mkdir(parents=True,exist_ok=True);written.append((dst,saved))
    try:
        for src,dst in operations:prepare(dst);shutil.copy2(src,dst)
        if catalog is not None:
            prepare(catalog_path);catalog['assets'].update(override);catalog['framePatchVersion']='3.5'
            catalog_path.write_text(json.dumps(catalog,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        receipt=target/'frame-assets-v3.5/installation.json';prepare(receipt)
        receipt.write_text(json.dumps({'patchId':spec['patchId'],'assetFiles':len(operations),'catalogMerged':catalog is not None,'backup':str(backup)},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    except Exception:
        for dst,saved in reversed(written):
            if saved.exists():shutil.copy2(saved,dst)
            elif dst.exists():dst.unlink()
        raise
    print('已安装边框资源；按 FRAME_PATCH.md 设置九宫格的源切片与目标边距')
    print('备份目录：'+str(backup))

if __name__=='__main__':main()
