"""在新的项目内测试目录验证 NSIS 数据清理逻辑，不访问真实用户数据。"""
import argparse
import json
from pathlib import Path
import re
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--script', type=Path, required=True)
    parser.add_argument('--compiler', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    project = Path(__file__).resolve().parents[2]
    output = args.output.resolve()
    if not output.is_relative_to(project / 'Builds') or output.exists():
        raise ValueError('测试输出必须是项目 Builds 内尚不存在的子目录')
    output.mkdir(parents=True)
    script = args.script.read_text(encoding='utf-8-sig')
    init = re.search(r'Function un.onInit\n(.*?)\nFunctionEnd', script, re.S).group(1)
    remove = re.search(r'Function un.RemoveSelectedAppData\n(.*?)\nFunctionEnd', script, re.S).group(1)
    tree = re.search(r'Function un.RemoveDataTree\n(.*?)\nFunctionEnd', script, re.S).group(1)
    child = re.search(r'Function un.RemoveAppDataChild\n(.*?)\nFunctionEnd', script, re.S).group(1)
    targets = re.findall(r'Push "\$LocalLowPath\\([^"\n]+)"', remove)
    assert len(targets) == 2
    assert targets[0] in ('DefaultCompany\\NoMadCity', 'zhuanshiluobo\\NoMadCity')
    assert targets[1] == 'zhuanshiluobo\\游城拓荒模拟器'
    company_name = Path(targets[0]).parts[0]
    assert 'Section /o "创建开始菜单快捷方式" SecStartMenu' in script
    assert '${NSD_SetState} $RemoveAppDataCheckbox $RemoveAppData' in script
    results = []
    for scenario in ('保留', '删除', '空路径', '相对路径', '目录联接', '父目录联接'):
        case = output / scenario
        case.mkdir()
        appdata = case / '模拟LocalLow'
        appdata.mkdir()
        if scenario == '父目录联接':
            destination = case / '联接目标'
            destination.mkdir()
            command = f"New-Item -ItemType Junction -Path '{appdata / company_name}' -Target '{destination}' | Out-Null"
            subprocess.run(['powershell', '-NoProfile', '-Command', command], check=True)
        explicit_targets = [(appdata / t).resolve() for t in targets]
        for target in explicit_targets:
            assert target.is_relative_to(output) and target not in (project, output, appdata)
            target.mkdir(parents=True)
            (target / '测试存档.txt').write_text('仅用于安装器测试', encoding='utf-8')
            (target / '子目录').mkdir()
            (target / '子目录/嵌套存档.txt').write_text('嵌套测试文件', encoding='utf-8')
        sibling = appdata / company_name / '其他应用'
        sibling.mkdir()
        sentinel = sibling / '必须保留.txt'
        sentinel.write_text('其他应用的数据', encoding='utf-8')
        if scenario == '目录联接':
            # 联接和它的目标均在本次新建的测试目录内。
            junction = explicit_targets[0] / '联接'
            command = f"New-Item -ItemType Junction -Path '{junction}' -Target '{sibling}' | Out-Null"
            subprocess.run(['powershell', '-NoProfile', '-Command', command], check=True)
        print(json.dumps({'scenario': scenario, 'verifiedAbsoluteTargets': list(map(str, explicit_targets))}, ensure_ascii=False), flush=True)
        # 只复用正式卸载器的函数；把根目录设为上面已经核验的模拟目录。
        root_value = '' if scenario == '空路径' else 'relative-path' if scenario == '相对路径' else str(appdata)
        selected = scenario != '保留'
        source = f'''Unicode true
!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "FileFunc.nsh"
Name "安装器数据清理隔离验证"
OutFile "{case / '验证.exe'}"
RequestExecutionLevel user
SilentInstall silent
Var RemoveAppData
Var LocalLowPath
Var AppDataRemovalFailed
Function .onInit
{init}
  FileOpen $3 "{case / '系统路径.txt'}" w
  FileWriteUTF16LE $3 "$LocalLowPath"
  FileClose $3
  ${{If}} $RemoveAppData != ${{BST_UNCHECKED}}
    SetErrorLevel 10
    Quit
  ${{EndIf}}
FunctionEnd
Function RemoveDataTree
{tree.replace('Call un.', 'Call ')}
FunctionEnd
Function RemoveAppDataChild
{child.replace('Call un.', 'Call ')}
FunctionEnd
Function RemoveSelectedAppData
{remove.replace('Call un.', 'Call ')}
FunctionEnd
Section
  StrCpy $LocalLowPath "{root_value}"
  {'StrCpy $RemoveAppData ${BST_CHECKED}' if selected else '; 保持正式初始化默认值'}
  Call RemoveSelectedAppData
SectionEnd
'''
        nsi = case / '验证.nsi'
        nsi.write_text(source, encoding='utf-8-sig')
        compile_result = subprocess.run([str(args.compiler.resolve()), '/V2', str(nsi)], capture_output=True)
        (case / '编译日志.txt').write_bytes(compile_result.stdout + compile_result.stderr)
        compile_result.check_returncode()
        subprocess.run([str(case / '验证.exe'), '/S'], check=True, timeout=30)
        resolved = (case / '系统路径.txt').read_text(encoding='utf-16-le').lstrip('\ufeff')
        assert Path(resolved).is_absolute(), resolved
        deleted = scenario in ('删除', '目录联接')
        for index, target in enumerate(explicit_targets):
            should_delete = deleted or (scenario == '父目录联接' and Path(targets[index]).parts[0] != company_name)
            assert target.exists() != should_delete, (scenario, target)
        assert sentinel.read_text(encoding='utf-8') == '其他应用的数据', scenario
        assert appdata.exists() and sibling.parent.exists()
        results.append({'scenario': scenario, 'passed': True, 'systemLocalLow': resolved, 'targets': list(map(str, explicit_targets))})
    (output / '验证结果.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
    print('全部隔离验证通过；真实 AppData 未修改。')


if __name__ == '__main__':
    main()
