"""正常菜单四人企业家完整局；冻结构建、逐步采样、外部进程监控。不注入状态。"""
import datetime, hashlib, json, pathlib, re, shutil, subprocess, sys, time

ROOT = pathlib.Path('G:/NoMadCity')
TAG = sys.argv[1] if len(sys.argv)>1 else 'R1'
assert TAG.isalnum()
OUT = ROOT/'Logs/HotseatPerf-20261103'/TAG
BUILD = ROOT/f'Builds/HotseatPerf-20261103-{TAG}'
REUSE = sys.argv[2] if len(sys.argv)>2 and sys.argv[2]!='-' else None
RESUME = sys.argv[3] if len(sys.argv)>3 else None
OPTIMIZE_MANAGED = '--optimize-managed-code' in sys.argv[4:]
if REUSE:
    assert REUSE.isalnum()
    BUILD = ROOT/f'Builds/HotseatPerf-20261103-{REUSE}'
OUT.mkdir(parents=True, exist_ok=False)
def write(name,value):
    (OUT/name).write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8')
def digest(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def inputs():
    return {str(p.relative_to(ROOT)):digest(p) for base in ('Assets/YC','Assets/Scenes','Assets/StreamingAssets','ProjectSettings')
        for p in (ROOT/base).rglob('*') if p.is_file() and p.suffix in ('.cs','.meta','.asmdef','.lua','.json','.prefab','.unity','.asset','.txt')}
before=inputs(); write('build-inputs.json',before)
compiler_settings = ROOT/'ProjectSettings/ProjectSettings.asset'
original_compiler_settings = compiler_settings.read_bytes() if OPTIMIZE_MANAGED and not REUSE else None
if original_compiler_settings is not None:
    (OUT/'ProjectSettings.before.asset').write_bytes(original_compiler_settings)
args=['D:/2022.3.62f1c1/Editor/Unity.exe','-batchmode','-nographics','-projectPath',str(ROOT),
    '-ycBuildLocalhostDevelopment','-ycDevelopmentOutput',str(BUILD),'-logFile',str(OUT/'build.log')]
if OPTIMIZE_MANAGED:
    args.append('-ycOptimizeDevelopmentCode')
if not REUSE:
    assert not BUILD.exists()
    build=subprocess.Popen(args,cwd=ROOT,creationflags=subprocess.CREATE_NO_WINDOW)
    print('构建进程',build.pid,flush=True)
    while build.poll() is None:
        time.sleep(10)
        print('构建中',flush=True)
else:
    prior=json.loads((ROOT/'Logs/HotseatPerf-20261103'/REUSE/'build-inputs.json').read_text(encoding='utf-8'))
    assert all(before.get(p)==h for p,h in prior.items()), '复用构建源码漂移'
    manifest = BUILD/'ManagedCompilerOptions.json'
    actual_optimized = manifest.exists() and json.loads(manifest.read_text(encoding='utf-8-sig')).get('OptimizationRequested', False)
    assert bool(actual_optimized) == OPTIMIZE_MANAGED, '复用构建的托管编译配置不一致'
if original_compiler_settings is not None and build.returncode == 0:
    manifest = json.loads((BUILD/'ManagedCompilerOptions.json').read_text(encoding='utf-8-sig'))
    assert manifest['BuildSucceeded'] and manifest['DevelopmentBuild'] and manifest['OptimizationRequested']
    assert manifest['PreviousArguments'] == manifest['RestoredArguments'], '构建后编译参数未恢复'
    current_settings = compiler_settings.read_bytes()
    if current_settings != original_compiler_settings:
        # Unity可能保留新增的空目标键。仅在差异完全属于已恢复的编译参数段时还原该段；其他改动须停下。
        pattern = rb'(?ms)^  additionalCompilerArguments:.*?(?=^  [A-Za-z])'
        prior_section = re.search(pattern, original_compiler_settings)
        current_section = re.search(pattern, current_settings)
        assert prior_section and current_section, '不能定位编译参数恢复范围'
        restored_settings = current_settings[:current_section.start()] + prior_section.group() + current_settings[current_section.end():]
        restored_settings = re.sub(rb'(?m)^(  - m_BuildTarget:)[ \t]+(\r?$)', rb'\1\2', restored_settings)
        assert restored_settings == original_compiler_settings, '构建期间另有项目设置改动，不能自动恢复'
        compiler_settings.write_bytes(restored_settings)
        write('temporary-compiler-settings-restoration.json', {'ApiArgumentsRestored':True,
            'OnlyCompilerArgumentsAndEmptyYamlPaddingChanged':True,'OriginalSha256':hashlib.sha256(original_compiler_settings).hexdigest(),
            'RestoredSha256':digest(compiler_settings)})
after=inputs()
drift=[p for p in before.keys()|after.keys() if before.get(p)!=after.get(p)]
# Unity首次导入新增探针生成的meta不改变已交付源码；其余输入漂移必须停下。
unexpected=[p for p in drift if not(p.endswith('.cs.meta') and p not in before)]
code=0 if REUSE else build.returncode
write('build-result.json',{'ExitCode':code,'InputDrift':drift,'UnexpectedInputDrift':unexpected,'Build':str(BUILD),'Reused':REUSE,
    'ManagedCodeOptimizationRequested':OPTIMIZE_MANAGED,'DevelopmentInstrumentationPreserved':True})
assert code==0 and not unexpected
if OPTIMIZE_MANAGED:
    pwsh = 'C:/Users/wzy/.cache/codex-runtimes/codex-primary-runtime/dependencies/native/powershell/pwsh.exe'
    subprocess.run([pwsh,'-NoProfile','-File',str(ROOT/'tools/tests/Read-ManagedCompilerOptions.ps1'),
        '-AssemblyPath',str(BUILD/'tuohuang_Data/Managed/YC.Domain.dll'),'-OutputPath',str(OUT/'规则程序集编译标记.json')],check=True)
    marker = json.loads((OUT/'规则程序集编译标记.json').read_text(encoding='utf-8-sig'))
    assert marker['JitOptimizationDisabled'] is False, '托管优化未生效，保留构建证据并停止测试'
if '--build-only' in sys.argv[2:]:
    print('仅构建完成，未启动新程序；保留现有运行包。',flush=True)
    raise SystemExit(0)
case=OUT/'game'; case.mkdir()
resume_args=[]
if RESUME:
    assert RESUME.isalnum()
    source=ROOT/'Logs/HotseatPerf-20261103'/RESUME
    saves=OUT/'MatchSaves'; saves.mkdir()
    source_save=source/'MatchSaves/auto-1.json'
    data=json.loads(json.loads(source_save.read_text(encoding='utf-8-sig'))['Payload'])
    revision=json.loads(data['ArchiveJson'])['Snapshot']['StateRevision']
    original=digest(source_save)
    shutil.copy2(source_save,saves/'auto-1.json')
    shutil.copy2(source/'campaign.json',OUT/'campaign.json')
    identity=json.loads((source/'game/运行身份.json').read_text(encoding='utf-8-sig'))
    resume_args=[f'--yc-local-resume-game-id={data["GameId"]}','--yc-local-resume-slot=auto-1',
        '--yc-local-resume-rule-mode=entrepreneur','--yc-local-resume-player-count=4',
        f'--yc-local-resume-content-hash={data["ContentHash"]}',f'--yc-local-resume-revision={revision}',
        f'--yc-local-resume-source-mvid={identity["Assembly"]}']
    write('resume-source.json',{'Path':str(source_save),'Sha256':original,'GameId':data['GameId'],
        'ContentHash':data['ContentHash'],'Revision':revision,'OriginalUnchanged':digest(source_save)==original,
        'NormalLoadWindow':True,'StateInjection':False})
args=[str(BUILD/'tuohuang.exe'),'-screen-fullscreen','0','-screen-width','1280','-screen-height','720',
    '-logFile',str(case/'Player.log'),f'--yc-hotseat-ui-validation-output={case}',
    '--yc-hotseat-ui-validation-case=full-game','--yc-ui-rule-mode=entrepreneur','--yc-hotseat-player-count=4',
    '--yc-campaign-expansions=enterprise,infected','--yc-full-game-performance=true',
    f'--yc-ui-campaign-path={OUT / "campaign.json"}','--yc-amiya-original-viewer=true',
    '--yc-ui-defer-content-differences=true',f'--yc-performance-save-root={OUT / "MatchSaves"}']+resume_args
player=subprocess.Popen(args,cwd=BUILD)
write('process.json',{'Pid':player.pid,'Arguments':args,'Utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'NormalMenuFlow':True,'StateInjection':False,'ForcedGc':False,'ComputerUse':False})
print('可见独立程序',player.pid,'输出',case,flush=True)
import ctypes
from ctypes import wintypes
class Counters(ctypes.Structure):
    _fields_=[('cb',wintypes.DWORD),('PageFaultCount',wintypes.DWORD)]+[(n,ctypes.c_size_t) for n in (
        'PeakWorkingSetSize','WorkingSetSize','QuotaPeakPagedPoolUsage','QuotaPagedPoolUsage',
        'QuotaPeakNonPagedPoolUsage','QuotaNonPagedPoolUsage','PagefileUsage','PeakPagefileUsage','PrivateUsage')]
kernel=ctypes.WinDLL('kernel32',use_last_error=True); psapi=ctypes.WinDLL('psapi',use_last_error=True)
kernel.OpenProcess.restype=wintypes.HANDLE
handle=kernel.OpenProcess(0x410,False,player.pid)
psapi.GetProcessMemoryInfo.argtypes=[wintypes.HANDLE,ctypes.POINTER(Counters),wintypes.DWORD]
started=time.monotonic()
with (OUT/'os-memory.jsonl').open('w',encoding='utf-8',buffering=1) as f:
    while player.poll() is None:
        c=Counters(); c.cb=ctypes.sizeof(c)
        if psapi.GetProcessMemoryInfo(handle,ctypes.byref(c),c.cb):
            sample={'Utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'Seconds':time.monotonic()-started,
                'WorkingSet':c.WorkingSetSize,'Private':c.PrivateUsage,'PeakWorkingSet':c.PeakWorkingSetSize}
            f.write(json.dumps(sample)+'\n')
            # 连续样本以文件为准；不把采样循环绑在可能被填满的宿主stdout管道上。
        time.sleep(1)
kernel.CloseHandle.argtypes=[wintypes.HANDLE]; kernel.CloseHandle(handle)
write('exit.json',{'ExitCode':player.returncode,'Seconds':time.monotonic()-started,'ForcedTermination':False})
print('程序退出',player.returncode,flush=True)
