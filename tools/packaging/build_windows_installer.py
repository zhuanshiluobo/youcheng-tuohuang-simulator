"""将已冻结的Unity Windows完整构建打包为中文NSIS安装向导。"""
from pathlib import Path, PurePosixPath
import argparse
import hashlib
import json
import subprocess
import re
from datetime import datetime, timezone


def sha(path):
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def quote(value):
    return '"' + str(value).replace('$', '$$').replace('"', '$\\"') + '"'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', required=True, type=Path)
    parser.add_argument('--compiler', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--build-label', default='R33')
    parser.add_argument('--icon', type=Path, default=Path(__file__).resolve().parent / 'ApplicationIcon.ico')
    args = parser.parse_args()
    if not re.fullmatch(r'R[0-9]+', args.build_label):
        raise ValueError('构建标签必须为R及数字')
    product_version = '0.5.0.' + args.build_label[1:]
    display_version = '0.5.0-alpha (' + args.build_label + ')'
    manifest = json.loads(args.manifest.read_text(encoding='utf-8-sig'))
    source = Path(manifest['root']).resolve()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    icon = args.icon.resolve()
    if not icon.is_file() or icon.read_bytes()[:4] != b'\x00\x00\x01\x00':
        raise ValueError('安装器图标不存在或不是 ICO 文件：' + str(icon))
    exe = output / ('游城拓荒-0.5.0-alpha-' + args.build_label + '-Windows64-安装包.exe')
    if exe.exists():
        raise RuntimeError('目标安装包已经存在，不覆盖')
    rows = manifest['files']
    actual = {p.relative_to(source).as_posix() for p in source.rglob('*') if p.is_file()}
    assert actual == {r['path'] for r in rows}
    for row in rows:
        path = (source / row['path']).resolve()
        assert path.is_relative_to(source) and path.is_file()
        assert path.stat().st_size == row['bytes'] and sha(path) == row['sha256'], row['path']
    canonical = ''.join(f"{r['path']}\0{r['bytes']}\0{r['sha256']}\n" for r in sorted(rows, key=lambda r: r['path'].encode('utf-8')))
    assert hashlib.sha256(canonical.encode('utf-8')).hexdigest() == manifest['treeSha256']
    assert 'tuohuang.exe' in actual and 'UnityPlayer.dll' in actual
    # Unity 2022.3 构建器可能把空公司名回退为 DefaultCompany。
    # 卸载路径必须以本次构建的真实身份为准，不能仅从项目 YAML 猜测。
    identity = (source / 'tuohuang_Data/app.info').read_text(encoding='utf-8-sig').splitlines()
    if len(identity) != 2 or identity[0] not in ('zhuanshiluobo', '', 'DefaultCompany') or identity[1] != 'NoMadCity':
        raise ValueError('构建产品身份不符：预期公司名 zhuanshiluobo（兼容此前空公司名构建），产品为 NoMadCity')
    app_data_subdirectory = '\\'.join(part for part in identity if part)
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
!define MUI_ICON @ICON@
!define MUI_UNICON @ICON@

Var RemoveAppData
Var RemoveAppDataCheckbox
Var LocalLowPath
Var AppDataRemovalFailed

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
!define MUI_WELCOMEPAGE_TEXT "本向导将帮助您安装游城拓荒。$\r$\n$\r$\n您可以选择安装位置，并决定是否创建桌面和开始菜单快捷方式。"
!define MUI_DIRECTORYPAGE_TEXT_TOP "请选择游城拓荒的安装位置，点击“浏览”可以更改文件夹。"
!define MUI_COMPONENTSPAGE_TEXT_TOP "请选择需要创建的快捷方式；取消勾选则不创建。"
!define MUI_COMPONENTSPAGE_TEXT_COMPLIST "快捷方式名称均为“游城拓荒”。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\tuohuang.exe"
!define MUI_FINISHPAGE_RUN_TEXT "启动游城拓荒"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
UninstPage custom un.AppDataPageCreate un.AppDataPageLeave
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "SimpChinese"

Section "游戏文件（必选）" SecGame
  SectionIn RO
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
SectionEnd

Section "创建桌面快捷方式" SecDesktop
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  CreateShortCut "$DESKTOP\游城拓荒.lnk" "$INSTDIR\tuohuang.exe" "" "$INSTDIR\tuohuang.exe" 0
SectionEnd

Section /o "创建开始菜单快捷方式" SecStartMenu
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  CreateShortCut "$SMPROGRAMS\游城拓荒.lnk" "$INSTDIR\tuohuang.exe" "" "$INSTDIR\tuohuang.exe" 0
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecGame} "安装运行游城拓荒所需的完整游戏文件。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "在当前用户的桌面创建名为“游城拓荒”的快捷方式。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecStartMenu} "在当前用户的开始菜单创建名为“游城拓荒”的快捷方式。"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "游城拓荒需要64位Windows系统。"
    Quit
  ${EndIf}
  SetRegView 64
FunctionEnd

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
  !insertmacro MUI_HEADER_TEXT "应用数据" "选择是否同时删除当前用户的存档、配置和日志。"
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 26u "默认保留应用数据，方便重新安装后继续使用。勾选后将永久删除下列目录中的数据（包含旧版数据）。"
  Pop $0
  ${NSD_CreateCheckbox} 0 34u 100% 14u "删除此应用的 AppData（含存档、配置和日志）"
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
    script = script.replace('@OUTPUT@', quote(exe)).replace('@INSTALL_LIST@', quote(output / '安装文件清单.nsh')).replace('@UNINSTALL_LIST@', quote(output / '卸载文件清单.nsh'))
    script = script.replace('@PRODUCT_VERSION@', product_version).replace('@DISPLAY_VERSION@', display_version)
    script = script.replace('@APP_DATA_SUBDIRECTORY@', app_data_subdirectory)
    script = script.replace('@ICON@', quote(icon))
    nsi = output / '游城拓荒安装程序.nsi'
    with nsi.open('x', encoding='utf-8-sig') as stream:
        stream.write(script)
    with (output / '编译原始日志.txt').open('x', encoding='utf-8') as log:
        result = subprocess.run([str(args.compiler.resolve()), '/V3', str(nsi)], stdout=log, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError(f'安装包编译失败：{result.returncode}，查看编译原始日志.txt')
    # 编译后再次确认冻结包未被编译器改写。
    for row in rows:
        assert sha(source / row['path']) == row['sha256'], row['path']
    receipt = {'builtUtc': datetime.now(timezone.utc).isoformat(), 'installer': str(exe), 'bytes': exe.stat().st_size, 'sha256': sha(exe), 'compiler': str(args.compiler.resolve()), 'compilerSha256': sha(args.compiler), 'inputRoot': str(source), 'inputManifest': str(args.manifest.resolve()), 'inputManifestSha256': sha(args.manifest), 'inputFiles': len(rows), 'inputBytes': sum(r['bytes'] for r in rows), 'inputTreeSha256': manifest['treeSha256'], 'sourceBuildUnchanged': True, 'language': '简体中文', 'chooseInstallDirectory': True, 'optionalDesktopShortcut': True, 'optionalStartMenuShortcut': True, 'startMenuShortcutDefaultChecked': False, 'shortcutName': '游城拓荒', 'gameStarted': False, 'uninstallerGameFileDeletionUsesExactManifestAndEmptyDirectories': True, 'gameSavesIncluded': False, 'optionalAppDataRemoval': True, 'appDataRemovalDefaultChecked': False, 'silentUninstallPreservesAppData': True, 'appDataRemovalLocalLowSubdirectories': ['NoMadCity', 'zhuanshiluobo/游城拓荒模拟器']}
    receipt['appDataRemovalLocalLowSubdirectories'] = [app_data_subdirectory, 'zhuanshiluobo\\游城拓荒模拟器']
    receipt['builtPlayerCompanyName'] = identity[0]
    receipt['builtPlayerProductName'] = identity[1]
    receipt['applicationIcon'] = str(icon)
    receipt['applicationIconSha256'] = sha(icon)
    with (output / '安装包构建回执.json').open('x', encoding='utf8') as stream:
        json.dump(receipt, stream, ensure_ascii=False, indent=2)
    print(json.dumps(receipt, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
