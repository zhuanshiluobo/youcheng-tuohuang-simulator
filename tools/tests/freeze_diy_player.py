"""冻结项目内独立测试程序的文件清单；只读取明确构建目录，不覆盖证据。"""
import argparse
import hashlib
import json
from pathlib import Path

project = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--build', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
options = parser.parse_args()
root = options.build.resolve(strict=True)
destination = options.output.resolve()
builds = (project / 'Builds').resolve()
results = (project / 'TestResults').resolve()
if (root == builds or root == results or not (root.is_relative_to(builds) or root.is_relative_to(results)) or
        destination == results or not destination.is_relative_to(results)):
    raise ValueError('构建必须位于 Builds 或 TestResults 的明确子目录，证据必须位于 TestResults。')
rows = []
for path in root.rglob('*'):
    if path.is_symlink() or getattr(path.lstat(), 'st_file_attributes', 0) & 0x400:
        raise ValueError('构建产物不能包含链接：' + str(path))
    if path.is_file():
        digest = hashlib.sha256()
        with path.open('rb') as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b''):
                digest.update(chunk)
        rows.append({'path': path.relative_to(root).as_posix(), 'bytes': path.stat().st_size, 'sha256': digest.hexdigest()})
rows.sort(key=lambda row: row['path'].encode('utf-8'))
if not any(row['path'] == 'tuohuang.exe' for row in rows):
    raise ValueError('构建目录没有游戏主程序。')
canonical = ''.join(f"{row['path']}\0{row['bytes']}\0{row['sha256']}\n" for row in rows)
manifest = {'root': str(root), 'fileCount': len(rows), 'treeSha256': hashlib.sha256(canonical.encode('utf-8')).hexdigest(), 'files': rows}
destination.parent.mkdir(parents=True, exist_ok=True)
with destination.open('x', encoding='utf-8') as stream:
    json.dump(manifest, stream, ensure_ascii=False, indent=2)
print(f"冻结 {len(rows)} 个文件：{manifest['treeSha256']}")
