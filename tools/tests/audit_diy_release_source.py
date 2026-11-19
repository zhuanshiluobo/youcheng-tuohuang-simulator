"""核对待发发行构建对应的项目输入是否在冻结后发生变化。"""

import argparse
import hashlib
import json
import os
from pathlib import Path


def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(chunk)
    return value.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    project = Path(__file__).resolve().parents[2]
    baseline = json.loads(args.baseline.read_text(encoding='utf-8-sig'))
    original = {item['path']: item for item in baseline['files']}
    if len(original) != len(baseline['files']):
        raise ValueError('发行源码基线包含重复路径。')

    current = {}
    for name in ('Assets', 'Packages', 'ProjectSettings'):
        root = project / name
        if not root.is_dir() or root.is_symlink() or getattr(root.lstat(), 'st_file_attributes', 0) & 0x400:
            raise ValueError('项目输入目录不存在或是重解析点：' + str(root))
        for directory, subdirectories, files in os.walk(root, followlinks=False):
            for child in subdirectories + files:
                path = Path(directory) / child
                if path.is_symlink() or getattr(path.lstat(), 'st_file_attributes', 0) & 0x400:
                    raise ValueError('项目输入含重解析点：' + str(path))
            for child in files:
                path = Path(directory) / child
                relative = path.relative_to(project).as_posix()
                current[relative] = {'bytes': path.stat().st_size, 'sha256': digest(path)}

    added = sorted(set(current) - set(original))
    removed = sorted(set(original) - set(current))
    changed = sorted(name for name in set(original) & set(current)
                     if original[name]['bytes'] != current[name]['bytes']
                     or original[name]['sha256'] != current[name]['sha256'])
    result = {'passed': not (added or removed or changed), 'baseline': str(args.baseline.resolve()),
              'scope': ['Assets', 'Packages', 'ProjectSettings'], 'files': len(current),
              'added': added, 'removed': removed, 'changed': changed}
    with args.output.open('x', encoding='utf-8') as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)
        stream.write('\n')
    print(json.dumps(result, ensure_ascii=False, indent=2))
    if not result['passed']:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
