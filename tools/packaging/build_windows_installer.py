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
    args = parser.parse_args()
    if not re.fullmatch(r'R[0-9]+', args.build_label):
        raise ValueError('构建标签必须为R及数字')
    product_version = '0.5.0.' + args.build_label[1:]
    display_version = '0.5.0-alpha (' + args.build_label + ')'
    manifest = json.loads(args.manifest.read_text(encoding='utf-8-sig'))
    source = Path(manifest['root']).resolve()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
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

Section "创建开始菜单快捷方式" SecStartMenu
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
SectionEnd
'''
    script = script.replace('@OUTPUT@', quote(exe)).replace('@INSTALL_LIST@', quote(output / '安装文件清单.nsh')).replace('@UNINSTALL_LIST@', quote(output / '卸载文件清单.nsh'))
    script = script.replace('@PRODUCT_VERSION@', product_version).replace('@DISPLAY_VERSION@', display_version)
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
    receipt = {'builtUtc': datetime.now(timezone.utc).isoformat(), 'installer': str(exe), 'bytes': exe.stat().st_size, 'sha256': sha(exe), 'compiler': str(args.compiler.resolve()), 'compilerSha256': sha(args.compiler), 'inputRoot': str(source), 'inputManifest': str(args.manifest.resolve()), 'inputManifestSha256': sha(args.manifest), 'inputFiles': len(rows), 'inputBytes': sum(r['bytes'] for r in rows), 'inputTreeSha256': manifest['treeSha256'], 'sourceBuildUnchanged': True, 'language': '简体中文', 'chooseInstallDirectory': True, 'optionalDesktopShortcut': True, 'optionalStartMenuShortcut': True, 'shortcutName': '游城拓荒', 'gameStarted': False, 'uninstallerGameFileDeletionUsesExactManifestAndEmptyDirectories': True, 'gameSavesIncludedOrDeleted': False}
    with (output / '安装包构建回执.json').open('x', encoding='utf8') as stream:
        json.dump(receipt, stream, ensure_ascii=False, indent=2)
    print(json.dumps(receipt, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
