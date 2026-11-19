"""将已冻结的Unity Windows完整构建打包为中文NSIS安装向导。"""
from pathlib import Path, PurePosixPath
import argparse
import hashlib
import json
import subprocess
import re
import stat
from datetime import datetime, timezone
import os
import math


def sha(path):
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def quote(value):
    return '"' + str(value).replace('$', '$$').replace('"', '$\\"') + '"'


def unique_json(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('JSON 字段重复：' + key)
        result[key] = value
    return result


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'), object_pairs_hook=unique_json)


def checked_path(path, restricted, *, reject_ancestor=False):
    """先检查路径本身及已有祖先，避免通过链接进入受限原档案。"""
    absolute = Path(os.path.abspath(path))
    if (absolute == restricted or absolute.is_relative_to(restricted)
            or reject_ancestor and restricted.is_relative_to(absolute)):
        raise ValueError('路径不得进入或包含受限原档案目录：' + str(absolute))
    for current in (absolute, *absolute.parents):
        try:
            info = current.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
            raise ValueError('打包输入和输出不能通过符号链接或目录联接：' + str(current))
    return absolute


def snapshot_tree(root, *, exclude_top=()):
    """只读取真实文件，记录可复核的完整源码快照，不跟随重解析点。"""
    info = root.lstat()
    if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
        raise ValueError('源码目录不能是链接：' + str(root))
    if not root.is_dir():
        raise ValueError('源码目录不存在：' + str(root))
    files, folded = {}, set()
    for parent, dirs, names in os.walk(root, followlinks=False):
        parent_path = Path(parent)
        if parent_path == root:
            dirs[:] = [name for name in dirs if name not in exclude_top]
        for name in dirs + names:
            path = parent_path / name
            info = path.lstat()
            if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
                raise ValueError('源码不能包含符号链接或目录联接：' + str(path))
        for name in names:
            path = parent_path / name
            if not path.is_file():
                raise ValueError('源码只能包含普通文件：' + str(path))
            relative = path.relative_to(root).as_posix()
            key = relative.casefold()
            if key in folded:
                raise ValueError('源码文件名大小写冲突：' + relative)
            folded.add(key)
            files[relative] = {'bytes': path.stat().st_size, 'sha256': sha(path)}
    return files


def discover_builtin_sources(root, restricted):
    root = checked_path(root, restricted, reject_ancestor=True)
    if not root.is_dir():
        raise ValueError('内置 DIY 包目录不存在：' + str(root))
    packages, names = [], set()
    for item in sorted(root.iterdir(), key=lambda entry: entry.name.encode('utf-8')):
        info = item.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
            raise ValueError('内置 DIY 包目录不能包含链接：' + str(item))
        if item.is_file() and item.name == '.gitkeep':
            continue
        if not item.is_dir():
            raise ValueError('请将每个内置 DIY 包放在独立文件夹中：' + str(item))
        if item.name.casefold() in names:
            raise ValueError('内置 DIY 文件夹名称大小写冲突：' + item.name)
        names.add(item.name.casefold())
        snapshot = snapshot_tree(item)
        if 'pack.json' not in snapshot:
            raise ValueError('内置 DIY 包根目录缺少 pack.json：' + str(item))
        packages.append({'source': item, 'files': snapshot})
    return root, packages


def build_diy_tool(project, output):
    """仅在需要内置内容且未指定工具时，从项目源码构建自包含工具。"""
    restricted = (project / '游城拓荒' / 'UI素材').absolute()
    source = checked_path(project / 'tools' / 'packaging' / 'DiyPackTool', restricted, reject_ancestor=True)
    source_files = snapshot_tree(source, exclude_top=('bin', 'obj'))
    if 'DiyPackTool.csproj' not in source_files:
        raise ValueError('缺少 DIY 发布工具项目：' + str(source))
    copied = output / '内置DIY工具源码'
    copied.mkdir()
    for relative, expected in source_files.items():
        destination = copied.joinpath(*PurePosixPath(relative).parts)
        destination.parent.mkdir(parents=True, exist_ok=True)
        with (source / relative).open('rb') as original, destination.open('xb') as target:
            while block := original.read(1024 * 1024):
                target.write(block)
        if destination.stat().st_size != expected['bytes'] or sha(destination) != expected['sha256']:
            raise RuntimeError('复制 DIY 发布工具源码时发生变化：' + relative)
    tool_dir = output / '内置DIY工具'
    command = ['dotnet', 'publish', str(copied / 'DiyPackTool.csproj'), '-c', 'Release', '-r', 'win-x64',
               '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
               '-o', str(tool_dir), '--nologo']
    with (output / '内置DIY工具构建日志.txt').open('x', encoding='utf-8') as log:
        result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT)
    tool = tool_dir / 'DiyPackTool.exe'
    if result.returncode or not tool.is_file():
        raise RuntimeError('DIY 发布工具构建失败，查看内置DIY工具构建日志.txt。')
    if snapshot_tree(source, exclude_top=('bin', 'obj')) != source_files:
        raise RuntimeError('构建期间 DIY 发布工具源码发生变化。')
    return tool, source_files


def publish_builtin_source(package, game, tool, output, index):
    destination = output / '内置DIY发布' / f'{index:04d}'
    env = os.environ.copy()
    env['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = str(output / 'DIY校验运行时')
    result = subprocess.run([str(tool), 'publish', '--game', str(game), '--source', str(package['source']),
                             '--output', str(destination)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env)
    outputs = list(tool_output_candidates(result.stdout))
    if result.returncode:
        details = outputs[0] if outputs else result.stdout.decode('utf-8', errors='replace')
        raise ValueError('内置 DIY 发布失败：' + str(package['source']) + '：' + details.strip())
    for output_text in outputs:
        try:
            manifest = json.loads(output_text, object_pairs_hook=unique_json)
        except (ValueError, TypeError):
            continue
        if not isinstance(manifest, dict):
            continue
        pack_id, version = manifest.get('packId', ''), manifest.get('version', '')
        if (not re.fullmatch(r'[a-z0-9]+(?:[._-][a-z0-9]+)*', pack_id)
                or not re.fullmatch(r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)', version)):
            raise ValueError('内置 DIY 发布结果身份无效：' + str(package['source']))
        path = destination / (pack_id + '-' + version + '.release.json')
        if read_json(path) != manifest:
            raise ValueError('内置 DIY 发布结果与清单不一致：' + str(path))
        if snapshot_tree(package['source']) != package['files']:
            raise RuntimeError('发布期间内置 DIY 源码发生变化：' + str(package['source']))
        published_files = {row['path']: {'bytes': row['bytes'], 'sha256': row['sha256']}
                           for row in manifest.get('files', [])}
        if published_files != package['files']:
            raise ValueError('内置 DIY 发布文件与源码快照不一致：' + str(package['source']))
        return path
    raise ValueError('内置 DIY 发布工具未返回有效 JSON：' + str(package['source']))


def tool_output_candidates(data):
    # 自包含 .NET 工具在有控制台和无控制台时可能分别使用 UTF-8 或系统代码页。
    # 不使用替换解码参与身份校验；稍后仍要核对全部发布清单字段。
    encodings = ['utf-8'] + (['mbcs', 'oem'] if os.name == 'nt' else [])
    seen = set()
    for encoding in encodings:
        try:
            value = data.decode(encoding)
        except (UnicodeDecodeError, LookupError):
            continue
        if value not in seen:
            seen.add(value)
            yield value


def read_diy_release(path, game, tool, output):
    restricted = (Path(__file__).resolve().parents[2] / '游城拓荒' / 'UI素材').absolute()
    path = checked_path(path, restricted)
    item = read_json(path)
    expected = {'schemaVersion', 'packId', 'version', 'displayName', 'author', 'description', 'contentHash',
                'zipFile', 'zipBytes', 'zipSha256', 'coreVersion', 'requiredCapabilities', 'dependencies', 'files'}
    if set(item) != expected or item['schemaVersion'] != 1:
        raise ValueError('DIY 发布清单字段或版本无效：' + str(path))
    if not re.fullmatch(r'[a-z0-9]+(?:[._-][a-z0-9]+)*', item['packId']) or item['packId'] == 'core':
        raise ValueError('DIY 发布清单的包 ID 无效：' + str(path))
    if not re.fullmatch(r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)', item['version']):
        raise ValueError('DIY 发布清单的版本无效：' + str(path))
    if item['zipFile'] != item['packId'] + '-' + item['version'] + '.zip':
        raise ValueError('DIY ZIP 名称与包身份不一致：' + str(path))
    for key in ('displayName', 'author', 'description'):
        if not isinstance(item[key], str) or not item[key].strip():
            raise ValueError('DIY 展示信息无效：' + key)
    for key in ('zipSha256', 'contentHash'):
        if not isinstance(item[key], str) or not re.fullmatch(r'[0-9a-f]{64}', item[key]):
            raise ValueError('DIY 哈希无效：' + key)
    archive = checked_path(path.parent / item['zipFile'], restricted)
    if not archive.is_file() or archive.stat().st_size != item['zipBytes'] or sha(archive) != item['zipSha256']:
        raise ValueError('DIY ZIP 与发布清单不一致：' + str(archive))
    verify_dir = output / 'DIY包校验' / (item['packId'] + '-' + item['version'])
    env = os.environ.copy()
    env['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = str(output / 'DIY校验运行时')
    command = [str(tool), 'verify', '--game', str(game), '--zip', str(archive), '--sha256', item['zipSha256'],
               '--pack-id', item['packId'], '--version', item['version'], '--content-hash', item['contentHash'], '--output', str(verify_dir)]
    result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env)
    outputs = list(tool_output_candidates(result.stdout))
    if result.returncode:
        details = outputs[0] if outputs else result.stdout.decode('utf-8', errors='replace')
        raise ValueError('DIY 正式解析器校验失败：' + details.strip())
    fields = [('packId', 'packId'), ('version', 'version'), ('displayName', 'name'), ('author', 'author'),
              ('description', 'description'), ('contentHash', 'contentHash'), ('requiredCapabilities', 'requiredCapabilities'),
              ('dependencies', 'dependencies'), ('files', 'files')]
    decoded = []
    for output_text in outputs:
        try:
            verified = json.loads(output_text, object_pairs_hook=unique_json)
        except (ValueError, TypeError):
            continue
        if not isinstance(verified, dict):
            continue
        mismatches = [source for source, actual in fields if item[source] != verified.get(actual)]
        if not mismatches:
            # 玩法级能力来自刚经正式解析器校验的 ZIP，而非可被单独篡改的发布说明。
            package = read_json(verify_dir / 'pack.json')
            scopes = []
            binding_capabilities = {
                'PlayerCleanupStarted': 'diy.rule_bindings.cleanup.v1',
                'CandidateSetBuilding': 'diy.rule_bindings.candidate.v1',
                'EffectCompleted': 'diy.rule_bindings.completed.v1',
                **{event: 'diy.rule_bindings.events.v1' for event in ('RoundStarted', 'RoundEnded', 'CharacterCardCovered',
                    'CharacterCoverCompleted', 'CharacterUseFinished', 'CollectionStarted', 'CityMoveCompleted', 'FacilityPlaced', 'FinalScoringStarted')},
            }
            for kind, collection, identity in (('模式', 'ruleModes', 'modeId'), ('扩展', 'expansions', 'expansionId')):
                for scope in package.get(collection, []):
                    capabilities = list(scope.get('requiredCapabilities', []))
                    if kind == '模式':
                        if 'preparation' in scope and 'diy.preparation_flow.v1' not in capabilities:
                            capabilities.append('diy.preparation_flow.v1')
                        if ('preparationOrder' in scope or scope.get('conflicts')) and 'diy.multi_rule_modes.v1' not in capabilities:
                            capabilities.append('diy.multi_rule_modes.v1')
                    for binding in scope.get('ruleBindings', []):
                        capability = binding_capabilities.get(binding['eventType'])
                        if capability is None:
                            raise ValueError('安装器无法核对规则绑定入口：' + binding['eventType'])
                        if capability not in capabilities:
                            capabilities.append(capability)
                    if capabilities:
                        scopes.append({'kind': kind, 'id': scope[identity], 'capabilities': capabilities})
            return {'manifest': item, 'path': path, 'zip': archive, 'manifestSha256': sha(path), 'scopeRequirements': scopes}
        decoded.append(mismatches)
    if decoded:
        raise ValueError('DIY 发布清单与实际 ZIP 内容不一致：' + min(decoded, key=len)[0])
    raise ValueError('DIY 正式解析器的输出不是受支持编码的有效 JSON。')


def make_diy_sections(releases, tool, product, fixture_data_root=None):
    sections, descriptions, count_selected = [], [], []
    def visible(value, limit):
        text = ''.join(' ' if ord(character) < 32 else character for character in value)
        return text if len(text) <= limit else text[:limit - 1] + '…'
    for index, entry in enumerate(releases):
        item = entry['manifest']
        section = 'SecDiy' + str(index)
        size = (str(max(1, math.ceil(item['zipBytes'] / 1024))) + ' KiB' if item['zipBytes'] < 1024 * 1024 else
                format(item['zipBytes'] / (1024 * 1024), '.2f') + ' MiB')
        name = visible(item['displayName'], 120) + ' ' + item['version'] + '（' + size + '）'
        requirements = visible('、'.join(item['requiredCapabilities']) or '无额外能力', 260)
        core_ranges = visible('、'.join(dep['range'] for dep in item['dependencies'] if dep['id'] == 'core') or '未声明', 100)
        scope_requirements = '；'.join(scope['kind'] + ' ' + scope['id'] + '：' + '、'.join(scope['capabilities'])
                                      for scope in entry.get('scopeRequirements', []))
        scope_details = '；玩法能力：' + visible(scope_requirements, 260) if scope_requirements else ''
        description = visible(item['description'], 400) + '；核心版本：' + core_ranges + '；能力：' + requirements + scope_details + '。不同版本并存，不改变已有默认绑定。'
        descriptions.append('  !insertmacro MUI_DESCRIPTION_TEXT ${' + section + '} ' + quote(description))
        count_selected.extend([
            '  SectionGetFlags ${' + section + '} $0',
            '  IntOp $0 $0 & ${SF_SELECTED}',
            '  ${If} $0 != 0',
            '    IntOp $DiyCount $DiyCount + 1',
            '  ${EndIf}'
        ])
        zip_name = item['zipFile']
        result_file = 'diy-result-' + str(index) + '.txt'
        tool_path = '$PLUGINSDIR\\DiyPackTool.exe'
        zip_path = '$PLUGINSDIR\\' + zip_name
        result_path = '$PLUGINSDIR\\' + result_file
        export = ('"' + tool_path + '" export --zip "' + zip_path + '" --sha256 ' + item['zipSha256'] +
                  ' --output "$INSTDIR" --result-file "' + result_path + '"')
        install = ('"' + tool_path + '" install --game "$INSTDIR" --product "' + product + '" --zip "' + zip_path +
                   '" --sha256 ' + item['zipSha256'] + ' --pack-id ' + item['packId'] + ' --version ' + item['version'] +
                   ' --content-hash ' + item['contentHash'] + ' --result-file "' + result_path + '"')
        if fixture_data_root is not None:
            install += ' --fixture-data-root "' + str(fixture_data_root) + '" --keep-stage true'
        sections.extend([
            'Section /o ' + quote(name) + ' ' + section,
            '  Call EnsureDiyTool',
            '  SetOutPath "$PLUGINSDIR"',
            '  File ' + quote('/oname=' + zip_name) + ' ' + quote(entry['zip']),
            '  ${If} $Operation == "export"',
            "    nsExec::ExecToStack '" + export + "'",
            '  ${Else}',
            "    nsExec::ExecToStack '" + install + "'",
            '  ${EndIf}',
            '  Pop $0',
            '  Pop $1',
            '  ClearErrors',
            '  FileOpen $2 "' + result_path + '" r',
            '  ${IfNot} ${Errors}',
            '    FileReadUTF16LE $2 $1',
            '    FileClose $2',
            '  ${EndIf}',
            '  ${If} $0 != 0',
            '    MessageBox MB_OK|MB_ICONSTOP "DIY 内容操作失败：$1" /SD IDOK',
            '    Abort',
            '  ${EndIf}',
            '  DetailPrint "$1"',
            '  StrCpy $DiyReport "$DiyReport$\\r$\\n$1"',
            'SectionEnd', ''
        ])
    functions = r'''
Function OperationPageCreate
  !insertmacro MUI_HEADER_TEXT "安装方式" "请选择本次操作。"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateFirstRadioButton} 0 2u 100% 18u "安装或更新游戏，可选装 DIY 内容"
  Pop $InstallRadio
  ${NSD_CreateAdditionalRadioButton} 0 35u 100% 18u "仅安装 DIY 内容到已安装游戏"
  Pop $SupplementRadio
  ${NSD_CreateAdditionalRadioButton} 0 68u 100% 18u "导出 DIY 内容包，无需安装游戏"
  Pop $ExportRadio
  ${NSD_CreateLabel} 0 103u 100% 44u "仅补装不会修改游戏、快捷方式或卸载信息。导出只保存内置 ZIP，不表示内容已安装。"
  Pop $0
  ${If} $Operation == "supplement"
    ${NSD_Check} $SupplementRadio
  ${ElseIf} $Operation == "export"
    ${NSD_Check} $ExportRadio
  ${Else}
    ${NSD_Check} $InstallRadio
  ${EndIf}
  nsDialogs::Show
FunctionEnd

Function OperationPageLeave
  ${If} $Operation != "export"
    StrCpy $GameTargetPath $INSTDIR
  ${EndIf}
  ${NSD_GetState} $SupplementRadio $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $Operation "supplement"
  ${Else}
    ${NSD_GetState} $ExportRadio $0
    ${If} $0 == ${BST_CHECKED}
      StrCpy $Operation "export"
    ${Else}
      StrCpy $Operation "game"
    ${EndIf}
  ${EndIf}
  ${If} $Operation == "game"
    StrCpy $INSTDIR $GameTargetPath
    IntOp $0 ${SF_SELECTED} | ${SF_RO}
    SectionSetFlags ${SecGame} $0
    SectionSetFlags ${SecDesktop} ${SF_SELECTED}
    SectionSetFlags ${SecStartMenu} 0
  ${Else}
    SectionSetFlags ${SecGame} ${SF_RO}
    SectionSetFlags ${SecDesktop} ${SF_RO}
    SectionSetFlags ${SecStartMenu} ${SF_RO}
    ${If} $Operation == "export"
      StrCpy $INSTDIR "$DOCUMENTS\游城拓荒DIY导出"
    ${Else}
      StrCpy $INSTDIR $GameTargetPath
    ${EndIf}
  ${EndIf}
FunctionEnd

Function ValidateDiySelection
  StrCpy $DiyCount 0
@COUNT_SELECTED@
  ${If} $Operation != "game"
  ${AndIf} $DiyCount == 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "请选择至少一个 DIY 内容包。"
    Abort
  ${EndIf}
FunctionEnd

Function ValidateTargetDirectory
  ${If} $Operation != "supplement"
    Return
  ${EndIf}
  IfFileExists "$INSTDIR\tuohuang.exe" target_found
    MessageBox MB_OK|MB_ICONEXCLAMATION "没有找到游戏主程序。请选择已安装游戏目录，或返回上一步选择导出。"
    Abort
  target_found:
  Call EnsureDiyTool
  nsExec::ExecToStack '"$PLUGINSDIR\DiyPackTool.exe" inspect --game "$INSTDIR" --product "@PRODUCT@"'
  Pop $0
  Pop $1
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "无法确认目标游戏的身份和 DIY 能力。请选择兼容的游戏目录，或返回上一步选择导出。"
    Abort
  ${EndIf}
FunctionEnd

Function EnsureDiyTool
  ${If} $DiyToolReady == 1
    Return
  ${EndIf}
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=DiyPackTool.exe @TOOL@
  System::Call 'kernel32::SetEnvironmentVariableW(w "DOTNET_BUNDLE_EXTRACT_BASE_DIR", w "$PLUGINSDIR\运行时") i .r0'
  StrCpy $DiyToolReady 1
FunctionEnd

Function DiyFinishPageShow
  ${If} $Operation == "export"
    ShowWindow $mui.FinishPage.Run ${SW_HIDE}
  ${EndIf}
  ${If} $DiyReport != ""
    MessageBox MB_OK|MB_ICONINFORMATION "DIY 内容结果：$DiyReport" /SD IDOK
  ${EndIf}
FunctionEnd
'''.replace('@COUNT_SELECTED@', '\n'.join(count_selected)).replace('@TOOL@', quote(tool)).replace('@PRODUCT@', product)
    return '\n'.join(sections), '\n'.join(descriptions), functions


def main():
    project = Path(__file__).resolve().parents[2]
    restricted = (project / '游城拓荒' / 'UI素材').absolute()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', required=True, type=Path)
    parser.add_argument('--compiler', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--build-label', default='R33')
    parser.add_argument('--icon', type=Path, default=Path(__file__).resolve().parent / 'ApplicationIcon.ico')
    parser.add_argument('--diy-manifest', action='append', type=Path, default=[], help='DIY 发布清单，可重复提供')
    parser.add_argument('--diy-tool', type=Path, help='自包含的 DIY 包校验与安装工具；未提供时按需构建')
    parser.add_argument('--builtin-diy-root', type=Path, default=project / '内置DIY' / '包',
                        help='自动发布并内置的 DIY 源码包目录')
    parser.add_argument('--no-builtin-diy', action='store_true', help='本次不自动内置该目录中的包')
    parser.add_argument('--fixture-data-root', type=Path, help=argparse.SUPPRESS)
    parser.add_argument('--fixture-allow-uninstall', action='store_true', help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.fixture_allow_uninstall and args.fixture_data_root is None:
        raise ValueError('测试卸载只能用于指定隔离内容根的测试安装器。')
    if not re.fullmatch(r'R[0-9]+', args.build_label):
        raise ValueError('构建标签必须为R及数字')
    product_version = '0.5.0.' + args.build_label[1:]
    display_version = '0.5.0-alpha (' + args.build_label + ')'
    manifest_path = checked_path(args.manifest, restricted)
    manifest = read_json(manifest_path)
    source = checked_path(manifest['root'], restricted, reject_ancestor=True)
    output = checked_path(args.output, restricted, reject_ancestor=True)
    builtin_root, builtin_sources = (None, []) if args.no_builtin_diy else discover_builtin_sources(args.builtin_diy_root, restricted)
    if builtin_root is not None and (output == builtin_root or output.is_relative_to(builtin_root)
                                     or builtin_root.is_relative_to(output)):
        raise ValueError('安装器输出目录不能与内置 DIY 源码目录重叠。')
    fixture_data_root = None
    if args.fixture_data_root is not None:
        permitted = checked_path(project / 'TestResults', restricted, reject_ancestor=True)
        if not args.fixture_data_root.is_absolute():
            raise ValueError('测试内容根必须是绝对路径。')
        fixture_data_root = checked_path(args.fixture_data_root, restricted, reject_ancestor=True)
        if not source.is_relative_to(permitted) and not source.is_relative_to(checked_path(project / 'Builds', restricted, reject_ancestor=True)):
            raise ValueError('测试安装器的游戏输入必须位于项目 Builds 或 TestResults。')
        for path in (output, fixture_data_root):
            if path == permitted or not path.is_relative_to(permitted):
                raise ValueError('测试安装器的输出和内容根必须在 TestResults 的独立子目录。')
            current = path
            while current != permitted:
                if current.exists() and (current.is_symlink() or getattr(current.lstat(), 'st_file_attributes', 0) & 0x400):
                    raise ValueError('测试安装器路径不能包含重解析点：' + str(current))
                current = current.parent
        if any(character in str(fixture_data_root) for character in "'$\"\r\n"):
            raise ValueError('测试内容路径包含不可用于安装器命令的字符。')
        if bool(args.diy_manifest or builtin_sources) != bool(args.diy_tool):
            raise ValueError('隔离测试中的 DIY 包与测试工具必须成对提供。')
    output.mkdir(parents=True, exist_ok=True)
    icon = checked_path(args.icon, restricted)
    if not icon.is_file() or icon.read_bytes()[:4] != b'\x00\x00\x01\x00':
        raise ValueError('安装器图标不存在或不是 ICO 文件：' + str(icon))
    exe = output / ('游城拓荒-0.5.0-alpha-' + args.build_label + '-Windows64-安装包.exe')
    if exe.exists():
        raise RuntimeError('目标安装包已经存在，不覆盖')
    rows = manifest['files']
    actual_files = snapshot_tree(source)
    actual = set(actual_files)
    assert actual == {r['path'] for r in rows}
    for row in rows:
        assert actual_files[row['path']] == {'bytes': row['bytes'], 'sha256': row['sha256']}, row['path']
    canonical = ''.join(f"{r['path']}\0{r['bytes']}\0{r['sha256']}\n" for r in sorted(rows, key=lambda r: r['path'].encode('utf-8')))
    assert hashlib.sha256(canonical.encode('utf-8')).hexdigest() == manifest['treeSha256']
    assert 'tuohuang.exe' in actual and 'UnityPlayer.dll' in actual
    # Unity 2022.3 构建器可能把空公司名回退为 DefaultCompany。
    # 卸载路径必须以本次构建的真实身份为准，不能仅从项目 YAML 猜测。
    identity = (source / 'tuohuang_Data/app.info').read_text(encoding='utf-8-sig').splitlines()
    if len(identity) != 2 or identity[0] not in ('zhuanshiluobo', '', 'DefaultCompany') or identity[1] != 'NoMadCity':
        raise ValueError('构建产品身份不符：预期公司名 zhuanshiluobo（兼容此前空公司名构建），产品为 NoMadCity')
    app_data_subdirectory = '\\'.join(part for part in identity if part)
    diy_tool = None
    releases, builtin_releases = [], []
    tool_sources = None
    if args.diy_manifest or builtin_sources:
        if args.diy_tool is None:
            diy_tool, tool_sources = build_diy_tool(project, output)
        else:
            diy_tool = checked_path(args.diy_tool, restricted)
        if not diy_tool.is_file() or diy_tool.suffix.lower() != '.exe':
            raise ValueError('DIY 工具必须是自包含 Windows 程序。')
        generated = []
        for index, package in enumerate(builtin_sources):
            release_path = publish_builtin_source(package, source, diy_tool, output, index)
            generated.append(release_path)
            builtin_releases.append((package, release_path))
        seen = set()
        for release in generated + [checked_path(path, restricted) for path in args.diy_manifest]:
            preliminary = read_json(release)
            preliminary_id = preliminary.get('packId') if isinstance(preliminary, dict) else None
            if isinstance(preliminary_id, str) and preliminary_id in seen:
                raise ValueError('同一安装器不能包含同一包的多个默认版本：' + preliminary_id)
            item = read_diy_release(release, source, diy_tool, output)
            key = item['manifest']['packId']
            if key in seen:
                raise ValueError('同一安装器不能包含同一包的多个默认版本：' + key)
            seen.add(key)
            releases.append(item)
    install = []
    uninstall = []
    directories = set()
    previous = None
    for row in sorted(rows, key=lambda r: r['path'].encode('utf-8')):
        rel = PurePosixPath(row['path'])
        parent = rel.parent.as_posix()
        if parent != previous:
            destination = '$INSTDIR' if parent == '.' else '$INSTDIR\\' + parent.replace('/', '\\')
            install.append('SetOutPath "' + destination + '"')
            previous = parent
        install.append('File ' + quote('/oname=' + rel.name) + ' ' + quote(source / row['path']))
        uninstall.append('Delete "$INSTDIR\\' + row['path'].replace('/', '\\') + '"')
        for directory in rel.parents:
            if directory.as_posix() != '.':
                directories.add(directory.as_posix())
    for directory in sorted(directories, key=lambda d: (-d.count('/'), d)):
        uninstall.append('RMDir "$INSTDIR\\' + directory.replace('/', '\\') + '"')
    for filename, lines in [('安装文件清单.nsh', install), ('卸载文件清单.nsh', uninstall)]:
        with (output / filename).open('x', encoding='utf-8-sig') as stream:
            stream.write('\n'.join(lines) + '\n')
    script = r'''
Unicode true
!include "MUI2.nsh"
!include "x64.nsh"
!include "nsDialogs.nsh"
!include "FileFunc.nsh"
!include "Sections.nsh"
!define MUI_ICON @ICON@
!define MUI_UNICON @ICON@

Var RemoveAppData
Var RemoveAppDataCheckbox
Var LocalLowPath
Var AppDataRemovalFailed
Var Operation
Var InstallRadio
Var SupplementRadio
Var ExportRadio
Var GameTargetPath
Var DiyToolReady
Var DiyCount
Var DiyReport

Name "游城拓荒"
OutFile @OUTPUT@
InstallDir "$LOCALAPPDATA\Programs\游城拓荒"
InstallDirRegKey HKCU "Software\YouChengTuoHuang" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 8
BrandingText "游城拓荒"
ShowInstDetails nevershow
ShowUninstDetails nevershow
VIProductVersion "@PRODUCT_VERSION@"
VIAddVersionKey /LANG=2052 "ProductName" "游城拓荒"
VIAddVersionKey /LANG=2052 "FileDescription" "游城拓荒安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "@PRODUCT_VERSION@"
VIAddVersionKey /LANG=2052 "ProductVersion" "@DISPLAY_VERSION@"
VIAddVersionKey /LANG=2052 "LegalCopyright" "zhuanshiluobo"

!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "欢迎安装游城拓荒"
!define MUI_WELCOMEPAGE_TEXT "本向导将帮助您安装游城拓荒，并可选装、补装或导出随附的 DIY 内容。"
!define MUI_DIRECTORYPAGE_TEXT_TOP "请选择目标文件夹：安装或补装时选择游戏目录；导出时选择 ZIP 的保存目录。"
!define MUI_COMPONENTSPAGE_TEXT_TOP "请选择本次需要的游戏文件、DIY 内容或快捷方式。"
!define MUI_COMPONENTSPAGE_TEXT_COMPLIST "DIY 内容可单独选择；新旧版本会并存。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\tuohuang.exe"
!define MUI_FINISHPAGE_RUN_TEXT "启动游城拓荒"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!insertmacro MUI_PAGE_WELCOME
@MODE_PAGE@
@DIRECTORY_PAGE_CALLBACK@
!insertmacro MUI_PAGE_DIRECTORY
@COMPONENT_PAGE_CALLBACK@
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
@FINISH_PAGE_CALLBACK@
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
UninstPage custom un.AppDataPageCreate un.AppDataPageLeave
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "SimpChinese"

Section "游戏文件（安装游戏时必选）" SecGame
  SectionIn RO
@GAME_SECTION_BEGIN@
  SetShellVarContext current
  SetRegView 64
  !include @INSTALL_LIST@
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\卸载游城拓荒.exe"
  WriteRegStr HKCU "Software\YouChengTuoHuang" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\YouChengTuoHuang" "DisplayName" "游城拓荒"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\YouChengTuoHuang" "DisplayVersion" "@DISPLAY_VERSION@"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\YouChengTuoHuang" "Publisher" "zhuanshiluobo"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\YouChengTuoHuang" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\YouChengTuoHuang" "DisplayIcon" '"$INSTDIR\tuohuang.exe"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\YouChengTuoHuang" "UninstallString" '"$INSTDIR\卸载游城拓荒.exe"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\YouChengTuoHuang" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\YouChengTuoHuang" "NoRepair" 1
@GAME_SECTION_END@
SectionEnd

Section "创建桌面快捷方式" SecDesktop
@GAME_SECTION_BEGIN@
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  CreateShortCut "$DESKTOP\游城拓荒.lnk" "$INSTDIR\tuohuang.exe" "" "$INSTDIR\tuohuang.exe" 0
@GAME_SECTION_END@
SectionEnd

Section /o "创建开始菜单快捷方式" SecStartMenu
@GAME_SECTION_BEGIN@
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  CreateShortCut "$SMPROGRAMS\游城拓荒.lnk" "$INSTDIR\tuohuang.exe" "" "$INSTDIR\tuohuang.exe" 0
@GAME_SECTION_END@
SectionEnd

@DIY_SECTIONS@

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecGame} "安装游戏时必选；仅补装和导出不会修改游戏文件。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "在当前用户的桌面创建名为“游城拓荒”的快捷方式。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecStartMenu} "在当前用户的开始菜单创建名为“游城拓荒”的快捷方式。"
@DIY_DESCRIPTIONS@
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "游城拓荒需要64位Windows系统。"
    Quit
  ${EndIf}
  SetRegView 64
  StrCpy $Operation "game"
  StrCpy $GameTargetPath $INSTDIR
  StrCpy $DiyToolReady 0
  StrCpy $DiyReport ""
FunctionEnd

@DIY_FUNCTIONS@

Function un.onInit
  SetRegView 64
  SetShellVarContext current
  StrCpy $RemoveAppData ${BST_UNCHECKED}
  StrCpy $LocalLowPath ""
  ; 使用系统已知文件夹，兼容用户资料目录重定向；失败时保留数据。
  System::Call 'shell32::SHGetKnownFolderPath(g "{A520A1A4-1780-4FF6-BD18-167343C5AF16}", i 0, p 0, *p .r0) i .r1'
  ${If} $1 == 0
    System::Call '*$0(&w${NSIS_MAX_STRLEN} .r2)'
    StrCpy $LocalLowPath $2
  ${EndIf}
  System::Call 'ole32::CoTaskMemFree(p r0)'
FunctionEnd

Function un.AppDataPageCreate
  !insertmacro MUI_HEADER_TEXT "应用数据" "选择是否同时删除当前用户的存档、DIY、修改、备份、配置和日志。"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 26u "默认保留应用数据，方便重新安装后继续使用。勾选后将永久删除下列目录中的数据（包含旧版数据）。"
  Pop $0
  ${NSD_CreateCheckbox} 0 34u 100% 14u "删除此应用数据（含存档、DIY、作者修改与备份）"
  Pop $RemoveAppDataCheckbox
  ${NSD_SetState} $RemoveAppDataCheckbox $RemoveAppData
  ${If} $LocalLowPath == ""
    EnableWindow $RemoveAppDataCheckbox 0
    ${NSD_CreateLabel} 0 58u 100% 40u "无法确定应用数据路径，本次卸载将保留应用数据。"
  ${Else}
    ${NSD_CreateLabel} 0 58u 100% 80u "$LocalLowPath\@APP_DATA_SUBDIRECTORY@$\r$\n$\r$\n$LocalLowPath\zhuanshiluobo\游城拓荒模拟器"
  ${EndIf}
  Pop $0
  nsDialogs::Show
FunctionEnd

Function un.AppDataPageLeave
  ${NSD_GetState} $RemoveAppDataCheckbox $RemoveAppData
FunctionEnd

; 不使用 RMDir /r：它会沿目录联接删除目标目录中的文件。
; 逐项处理精确路径，遇到联接或符号链接只移除链接本身。
Function un.RemoveDataTree
  Exch $0
  Push $1
  Push $2
  Push $3
  System::Call 'kernel32::GetFileAttributesW(w r0) i .r1'
  ${If} $1 == -1
    System::Call 'kernel32::GetLastError() i .r2'
    ${If} $2 != 2
    ${AndIf} $2 != 3
      StrCpy $AppDataRemovalFailed 1
    ${EndIf}
    Goto tree_done
  ${EndIf}
  IntOp $2 $1 & 0x10
  IntOp $3 $1 & 0x400
  ${If} $2 != 0
    ${If} $3 == 0
      FindFirst $1 $2 "$0\*"
      tree_next:
        ${If} $2 == ""
          Goto tree_close
        ${EndIf}
        ${If} $2 != "."
        ${AndIf} $2 != ".."
          Push "$0\$2"
          Call un.RemoveDataTree
        ${EndIf}
        FindNext $1 $2
        Goto tree_next
      tree_close:
      FindClose $1
    ${EndIf}
    ClearErrors
    RMDir "$0"
  ${Else}
    ClearErrors
    Delete "$0"
  ${EndIf}
  ${If} ${Errors}
    StrCpy $AppDataRemovalFailed 1
  ${EndIf}
  tree_done:
  Pop $3
  Pop $2
  Pop $1
  Pop $0
FunctionEnd

Function un.RemoveAppDataChild
  Exch $0
  Push $1
  Push $2
  ; 公司目录不能是联接，避免通过父目录跳出本应用的数据范围。
  ${GetParent} "$0" $1
  System::Call 'kernel32::GetFileAttributesW(w r1) i .r2'
  ${If} $2 != -1
    IntOp $2 $2 & 0x400
    ${If} $2 != 0
      StrCpy $AppDataRemovalFailed 1
      Goto child_done
    ${EndIf}
  ${EndIf}
  DetailPrint "删除应用数据：$0"
  Push $0
  Call un.RemoveDataTree
  child_done:
  Pop $2
  Pop $1
  Pop $0
FunctionEnd

Function un.RemoveSelectedAppData
  ${If} $RemoveAppData != ${BST_CHECKED}
  ${OrIf} $LocalLowPath == ""
    Return
  ${EndIf}
  ; 仅允许系统返回的绝对路径，永远不删除 LocalLow 或公司目录本身。
  System::Call 'shlwapi::PathIsRelativeW(w "$LocalLowPath") i .r0'
  ${If} $0 != 0
    Return
  ${EndIf}
  StrCpy $AppDataRemovalFailed 0
  Push "$LocalLowPath\@APP_DATA_SUBDIRECTORY@"
  Call un.RemoveAppDataChild
  Push "$LocalLowPath\zhuanshiluobo\游城拓荒模拟器"
  Call un.RemoveAppDataChild
  ${If} $AppDataRemovalFailed != 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "部分应用数据未能删除：文件可能被占用、没有访问权限，或父目录为链接。请检查上面列出的目录。" /SD IDOK
  ${EndIf}
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
  SetRegView 64
  !include @UNINSTALL_LIST@
  Delete "$DESKTOP\游城拓荒.lnk"
  Delete "$SMPROGRAMS\游城拓荒.lnk"
  Delete "$INSTDIR\卸载游城拓荒.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\YouChengTuoHuang"
  DeleteRegKey HKCU "Software\YouChengTuoHuang"
  Call un.RemoveSelectedAppData
SectionEnd
'''
    if releases:
        diy_sections, diy_descriptions, diy_functions = make_diy_sections(releases, diy_tool, identity[1], fixture_data_root)
        script = script.replace('@MODE_PAGE@', 'Page custom OperationPageCreate OperationPageLeave')
        script = script.replace('@DIRECTORY_PAGE_CALLBACK@', '!define MUI_PAGE_CUSTOMFUNCTION_LEAVE ValidateTargetDirectory')
        script = script.replace('@COMPONENT_PAGE_CALLBACK@', '!define MUI_PAGE_CUSTOMFUNCTION_LEAVE ValidateDiySelection')
        script = script.replace('@FINISH_PAGE_CALLBACK@', '!define MUI_PAGE_CUSTOMFUNCTION_SHOW DiyFinishPageShow')
        script = script.replace('@GAME_SECTION_BEGIN@', '  ${If} $Operation == "game"').replace('@GAME_SECTION_END@', '  ${EndIf}')
        script = script.replace('@DIY_SECTIONS@', diy_sections).replace('@DIY_DESCRIPTIONS@', diy_descriptions).replace('@DIY_FUNCTIONS@', diy_functions)
    else:
        for placeholder in ('@MODE_PAGE@', '@DIRECTORY_PAGE_CALLBACK@', '@COMPONENT_PAGE_CALLBACK@', '@FINISH_PAGE_CALLBACK@',
                            '@GAME_SECTION_BEGIN@', '@GAME_SECTION_END@', '@DIY_SECTIONS@', '@DIY_DESCRIPTIONS@', '@DIY_FUNCTIONS@'):
            script = script.replace(placeholder, '')
        script = script.replace('本向导将帮助您安装游城拓荒，并可选装、补装或导出随附的 DIY 内容。',
                                '本向导将帮助您安装游城拓荒，并可选择安装位置和快捷方式。')
        script = script.replace('请选择目标文件夹：安装或补装时选择游戏目录；导出时选择 ZIP 的保存目录。',
                                '请选择游城拓荒的安装位置，点击“浏览”可以更改文件夹。')
        script = script.replace('请选择本次需要的游戏文件、DIY 内容或快捷方式。', '请选择本次需要的游戏文件或快捷方式。')
        script = script.replace('DIY 内容可单独选择；新旧版本会并存。', '游戏文件为必选组件。')
        script = script.replace('安装游戏时必选；仅补装和导出不会修改游戏文件。', '安装游戏所需的全部文件。')
    fixture_game_target = None
    if fixture_data_root is not None:
        # 此分支仅生成 TestResults 内使用的测试安装器，不允许写真实注册表或快捷方式。
        fixture_game_target = fixture_data_root.parent / 'installed-game'
        fixture_target_guard = ('  ${If} $INSTDIR != ' + quote(fixture_game_target) + '\n'
            '    MessageBox MB_OK|MB_ICONSTOP "隔离测试安装器只能写入指定的测试游戏目录。" /SD IDOK\n'
            '    Abort\n  ${EndIf}\n')
        fixture_game_section = 'Section "游戏文件（安装游戏时必选）" SecGame\n  SectionIn RO\n'
        if releases:
            fixture_game_section += '  ${If} $Operation == "game"\n'
        if script.count(fixture_game_section) != 1:
            raise RuntimeError('无法为隔离安装器确定唯一的游戏组件。')
        # 页面校验之外还需检查实际组件，避免静默安装或跳过页面时写入其他目录。
        script = script.replace(fixture_game_section, fixture_game_section + fixture_target_guard, 1)
        if not releases:
            script = script.replace('!insertmacro MUI_PAGE_DIRECTORY',
                '!define MUI_PAGE_CUSTOMFUNCTION_LEAVE ValidateFixtureGameDirectory\n!insertmacro MUI_PAGE_DIRECTORY', 1)
            script += '\nFunction ValidateFixtureGameDirectory\n' + fixture_target_guard + 'FunctionEnd\n'
        script = script.replace('InstallDir "$LOCALAPPDATA\\Programs\\游城拓荒"', 'InstallDir ' + quote(fixture_game_target))
        script = '\n'.join(('; 隔离测试禁用注册表：' + line if line.lstrip().startswith(
            ('InstallDirRegKey ', 'WriteRegStr HKCU ', 'WriteRegDWORD HKCU ', 'DeleteRegKey HKCU ')) else line)
            for line in script.split('\n'))
        script = script.replace('CreateShortCut "$DESKTOP\\游城拓荒.lnk"',
                                'CreateDirectory "$INSTDIR\\fixture-shortcuts"\n  CreateShortCut "$INSTDIR\\fixture-shortcuts\\desktop.lnk"')
        script = script.replace('CreateShortCut "$SMPROGRAMS\\游城拓荒.lnk"',
                                'CreateDirectory "$INSTDIR\\fixture-shortcuts"\n  CreateShortCut "$INSTDIR\\fixture-shortcuts\\start-menu.lnk"')
        script = script.replace('Delete "$DESKTOP\\游城拓荒.lnk"', 'Delete "$INSTDIR\\fixture-shortcuts\\desktop.lnk"')
        script = script.replace('Delete "$SMPROGRAMS\\游城拓荒.lnk"', 'Delete "$INSTDIR\\fixture-shortcuts\\start-menu.lnk"')
        script = script.replace('Function ValidateTargetDirectory\n', 'Function ValidateTargetDirectory\n'
            '  ${If} $Operation == "game"\n  ${AndIf} $INSTDIR != ' + quote(fixture_game_target) + '\n'
            '    MessageBox MB_OK|MB_ICONSTOP "隔离测试安装器只能写入指定的测试游戏目录。" /SD IDOK\n    Abort\n  ${EndIf}\n')
        if args.fixture_allow_uninstall:
            # 仅供明确目标与范围已经核验、并获得删除授权的隔离卸载夹具。
            # 测试安装器不能查询或删除真实 LocalLow；两个数据目标均是 TestResults 中的固定路径。
            legacy_target = fixture_data_root.parent / 'legacy-data'
            on_init_start = script.index('Function un.onInit\n')
            on_init_end = script.index('\nFunctionEnd', on_init_start) + len('\nFunctionEnd')
            on_init = ('Function un.onInit\n  SetRegView 64\n  SetShellVarContext current\n'
                '  StrCpy $RemoveAppData ${BST_UNCHECKED}\n  StrCpy $LocalLowPath ' + quote(fixture_data_root.parent) + '\n'
                '  ${If} $INSTDIR != ' + quote(fixture_game_target) + '\n'
                '    MessageBox MB_OK|MB_ICONSTOP "隔离卸载器拒绝非测试游戏目录。"\n    Abort\n  ${EndIf}\nFunctionEnd')
            script = script[:on_init_start] + on_init + script[on_init_end:]
            script = script.replace('$LocalLowPath\\@APP_DATA_SUBDIRECTORY@', str(fixture_data_root))
            script = script.replace('$LocalLowPath\\zhuanshiluobo\\游城拓荒模拟器', str(legacy_target))
        else:
            script = script.replace('Function un.onInit\n', 'Function un.onInit\n'
                '  MessageBox MB_OK|MB_ICONSTOP "此隔离测试安装器不执行卸载。"\n  Abort\n')
            script = script.replace('Function un.RemoveSelectedAppData\n', 'Function un.RemoveSelectedAppData\n  Return\n')
    script = script.replace('@OUTPUT@', quote(exe)).replace('@INSTALL_LIST@', quote(output / '安装文件清单.nsh')).replace('@UNINSTALL_LIST@', quote(output / '卸载文件清单.nsh'))
    script = script.replace('@PRODUCT_VERSION@', product_version).replace('@DISPLAY_VERSION@', display_version)
    script = script.replace('@APP_DATA_SUBDIRECTORY@', app_data_subdirectory)
    script = script.replace('@ICON@', quote(icon))
    compiler = checked_path(args.compiler, restricted)
    nsi = output / '游城拓荒安装程序.nsi'
    with nsi.open('x', encoding='utf-8-sig') as stream:
        stream.write(script)
    with (output / '编译原始日志.txt').open('x', encoding='utf-8') as log:
        result = subprocess.run([str(compiler), '/V3', str(nsi)], stdout=log, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError(f'安装包编译失败：{result.returncode}，查看编译原始日志.txt')
    # 编译后再次核验完整文件集，并拒绝期间出现的链接。
    if snapshot_tree(source) != actual_files:
        raise RuntimeError('编译期间游戏构建文件发生变化。')
    for entry in releases:
        release_zip = checked_path(entry['zip'], restricted)
        release_manifest = checked_path(entry['path'], restricted)
        if sha(release_zip) != entry['manifest']['zipSha256'] or sha(release_manifest) != entry['manifestSha256']:
            raise RuntimeError('编译期间 DIY 发布产物发生变化：' + entry['manifest']['packId'])
    if builtin_root is not None:
        _, after_builtin_sources = discover_builtin_sources(builtin_root, restricted)
        if after_builtin_sources != builtin_sources:
            raise RuntimeError('编译期间内置 DIY 源码目录或文件发生变化。')
    if tool_sources is not None and snapshot_tree(project / 'tools' / 'packaging' / 'DiyPackTool', exclude_top=('bin', 'obj')) != tool_sources:
        raise RuntimeError('编译期间 DIY 发布工具源码发生变化。')
    receipt = {'builtUtc': datetime.now(timezone.utc).isoformat(), 'installer': str(exe), 'bytes': exe.stat().st_size, 'sha256': sha(exe), 'compiler': str(compiler), 'compilerSha256': sha(compiler), 'inputRoot': str(source), 'inputManifest': str(manifest_path), 'inputManifestSha256': sha(manifest_path), 'inputFiles': len(rows), 'inputBytes': sum(r['bytes'] for r in rows), 'inputTreeSha256': manifest['treeSha256'], 'sourceBuildUnchanged': True, 'language': '简体中文', 'chooseInstallDirectory': True, 'optionalDesktopShortcut': True, 'optionalStartMenuShortcut': True, 'startMenuShortcutDefaultChecked': False, 'shortcutName': '游城拓荒', 'gameStarted': False, 'uninstallerGameFileDeletionUsesExactManifestAndEmptyDirectories': True, 'gameSavesIncluded': False, 'optionalAppDataRemoval': True, 'appDataRemovalDefaultChecked': False, 'silentUninstallPreservesAppData': True, 'appDataRemovalLocalLowSubdirectories': ['NoMadCity', 'zhuanshiluobo/游城拓荒模拟器']}
    receipt['appDataRemovalLocalLowSubdirectories'] = [app_data_subdirectory, 'zhuanshiluobo\\游城拓荒模拟器']
    receipt['builtPlayerCompanyName'] = identity[0]
    receipt['builtPlayerProductName'] = identity[1]
    receipt['applicationIcon'] = str(icon)
    receipt['applicationIconSha256'] = sha(icon)
    receipt['diyToolSha256'] = sha(diy_tool) if diy_tool else None
    receipt['diyReleases'] = [{'packId': e['manifest']['packId'], 'version': e['manifest']['version'],
                               'contentHash': e['manifest']['contentHash'], 'zipSha256': e['manifest']['zipSha256'],
                               'zipBytes': e['manifest']['zipBytes'], 'manifestSha256': e['manifestSha256']} for e in releases]
    receipt['diySupportsSupplementAndExport'] = bool(releases)
    receipt['builtinDiyRoot'] = str(builtin_root) if builtin_root else None
    releases_by_path = {entry['path']: entry['manifest'] for entry in releases}
    receipt['builtinDiySources'] = [{'source': str(package['source']), 'fileCount': len(package['files']),
                                      'packId': releases_by_path[path]['packId'], 'version': releases_by_path[path]['version'],
                                      'contentHash': releases_by_path[path]['contentHash'], 'releaseManifest': str(path)}
                                     for package, path in builtin_releases]
    receipt['diyToolBuiltFromSource'] = tool_sources is not None
    receipt['fixtureDataRoot'] = str(fixture_data_root) if fixture_data_root else None
    receipt['fixtureGameTarget'] = str(fixture_game_target) if fixture_game_target else None
    receipt['fixtureRegistryWritesDisabled'] = fixture_data_root is not None
    receipt['fixtureUninstallAllowed'] = bool(fixture_data_root and args.fixture_allow_uninstall)
    receipt['fixtureLegacyDataTarget'] = str(fixture_data_root.parent / 'legacy-data') if fixture_data_root and args.fixture_allow_uninstall else None
    with (output / '安装包构建回执.json').open('x', encoding='utf8') as stream:
        json.dump(receipt, stream, ensure_ascii=False, indent=2)
    print(json.dumps(receipt, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
