"""核对无随附 DIY 包的安装器；可选核对隔离安装后的全部游戏文件。"""

import argparse
import hashlib
import json
import os
from pathlib import Path


def sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def walk_files(root):
    files = {}
    for directory, subdirectories, names in os.walk(root, followlinks=False):
        for name in subdirectories + names:
            path = Path(directory) / name
            if path.is_symlink() or getattr(path.lstat(), 'st_file_attributes', 0) & 0x400:
                raise ValueError('核验目录内不允许重解析点：' + str(path))
        for name in names:
            path = Path(directory) / name
            files[path.relative_to(root).as_posix()] = path
    return files


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--installer-dir', type=Path, required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--fixture-installed-root', type=Path)
    args = parser.parse_args()

    installer_dir = args.installer_dir.resolve(strict=True)
    receipt = read_json(installer_dir / '安装包构建回执.json')
    manifest = read_json(args.manifest)
    installer = Path(receipt['installer']).resolve(strict=True)
    if installer.parent != installer_dir or sha256(installer) != receipt['sha256']:
        raise ValueError('安装器路径或哈希与构建回执不符。')
    if receipt['diyReleases'] or receipt['diyToolSha256'] is not None or receipt['diySupportsSupplementAndExport']:
        raise ValueError('安装器意外包含随附 DIY 包或工具。')
    if receipt['inputTreeSha256'] != manifest['treeSha256'] or receipt['inputFiles'] != len(manifest['files']):
        raise ValueError('安装器输入与冻结发行清单不一致。')

    entries = {row['path']: row for row in manifest['files']}
    if len(entries) != len(manifest['files']):
        raise ValueError('冻结清单包含重复文件。')
    canonical = ''.join(f"{row['path']}\0{row['bytes']}\0{row['sha256']}\n"
                        for row in sorted(entries.values(), key=lambda row: row['path'].encode('utf-8')))
    if hashlib.sha256(canonical.encode('utf-8')).hexdigest() != manifest['treeSha256']:
        raise ValueError('冻结发行清单的树哈希不符。')
    for path in entries:
        parts = path.casefold().split('/')
        if 'mods' in parts or any(part.startswith('example.') for part in parts) or path.casefold().endswith(('.zip', '.7z', '.rar')):
            raise ValueError('发行文件中发现测试包目录或归档：' + path)

    script = (installer_dir / '游城拓荒安装程序.nsi').read_text(encoding='utf-8-sig')
    file_list = (installer_dir / '安装文件清单.nsh').read_text(encoding='utf-8-sig')
    for forbidden in ('Function OperationPageCreate', 'File /oname=DiyPackTool.exe',
                      '安装游戏时必选；仅补装和导出不会修改游戏文件。', 'example.basic', 'example.cleanup',
                      'example.mixed', 'example.events', 'example.completed'):
        if forbidden.casefold() in (script + file_list).casefold():
            raise ValueError('安装脚本包含不应随本次安装器出现的内容：' + forbidden)
    if '安装游戏所需的全部文件。' not in script:
        raise ValueError('无随附内容的游戏组件说明不正确。')

    installed_count = None
    installed_extras = None
    if args.fixture_installed_root:
        installed = args.fixture_installed_root.resolve(strict=True)
        project = Path(__file__).resolve().parents[2]
        permitted = (project / 'TestResults').resolve(strict=True)
        if not installed.is_relative_to(permitted) or installed == permitted:
            raise ValueError('实际安装核验只接受 TestResults 中的隔离目标。')
        if not receipt['fixtureRegistryWritesDisabled'] or receipt['fixtureUninstallAllowed']:
            raise ValueError('实际安装核验要求禁用真实注册表和卸载的测试安装器。')
        if Path(receipt['fixtureGameTarget']).resolve() != installed:
            raise ValueError('隔离安装路径与回执不符。')
        actual = walk_files(installed)
        missing = sorted(set(entries) - set(actual))
        if missing:
            raise ValueError('隔离安装缺少游戏文件：' + repr(missing[:10]))
        installed_extras = sorted(set(actual) - set(entries))
        allowed_extras = {'卸载游城拓荒.exe', 'fixture-shortcuts/desktop.lnk', 'fixture-shortcuts/start-menu.lnk'}
        if set(installed_extras) - allowed_extras or '卸载游城拓荒.exe' not in installed_extras:
            raise ValueError('隔离安装含有意外附加文件：' + repr(installed_extras))
        for name, row in entries.items():
            path = actual[name]
            if path.stat().st_size != row['bytes'] or sha256(path) != row['sha256']:
                raise ValueError('隔离安装文件与冻结发行清单不符：' + name)
        installed_count = len(entries)

    result = {
        'passed': True,
        'installer': str(installer),
        'sha256': receipt['sha256'],
        'inputTreeSha256': manifest['treeSha256'],
        'gameFiles': len(entries),
        'embeddedDiyPackages': 0,
        'embeddedDiyTool': False,
        'builtInDiyPages': False,
        'fixtureInstalledGameFilesVerified': installed_count,
        'fixtureInstalledExtraFiles': installed_extras,
    }
    with args.output.open('x', encoding='utf-8') as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)
        stream.write('\n')
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
