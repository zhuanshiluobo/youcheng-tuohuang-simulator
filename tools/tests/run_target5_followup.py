"""等当前R7自然退出后，串行执行下一版原档隔离回归；不与正常Player抢占CPU。"""
import ctypes
import json
import pathlib
import subprocess
from ctypes import wintypes

root = pathlib.Path('G:/NoMadCity')
identity = json.loads((root/'Logs/HotseatPerf-20261103/R7/process.json').read_text(encoding='utf-8'))
kernel = ctypes.WinDLL('kernel32', use_last_error=True)
kernel.OpenProcess.restype = wintypes.HANDLE
kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
kernel.CloseHandle.argtypes = [wintypes.HANDLE]
handle = kernel.OpenProcess(0x100000|0x1000, False, identity['Pid'])
if handle:
    try:
        path = ctypes.create_unicode_buffer(32768)
        length = wintypes.DWORD(len(path))
        if not kernel.QueryFullProcessImageNameW(handle, 0, path, ctypes.byref(length)):
            raise ctypes.WinError(ctypes.get_last_error())
        assert pathlib.Path(path.value).resolve() == pathlib.Path(identity['Arguments'][0]).resolve(), '进程身份不符，停止等待和测试'
        print('已核验R7进程；等待自然退出后执行隔离回归。', flush=True)
        while kernel.WaitForSingleObject(handle, 30000) == 258:
            print('R7仍在运行，未启动Unity隔离测试。', flush=True)
    finally:
        kernel.CloseHandle(handle)

tag = 'target5-r8'
output = root/'Logs/HotseatPerf-20261103'/tag
assert not output.exists(), '报告目录已存在，不能覆盖'
filters = ';'.join('YC.Tests.EditMode.'+name for name in [
    'ExpansionRules.HotseatRecordedPerformanceReplayTests', 'ExpansionRules.RecordedBuildQuotePerformanceTests',
    'ExpansionRules.RuleStateSha256CompatibilityTests', 'ExpansionRules.RuleDigestStreamingCompatibilityTests',
    'ExpansionRules.NormalizedFieldLookupPerformanceTests', 'ExpansionRules.CanonicalTransferDigestPerformanceTests',
    'ExpansionRules.QY008CloneDiagnosticPathR5Tests', 'ReadOnlyStateQueryScopeTests', 'LocalSimulationProjectionTests',
    'ExpansionRules.EnterpriseSpecialCandidateScopeR17Tests', 'ExpansionRules.EnterpriseEffectTests'])
args = ['pwsh', '-NoProfile', '-File', str(root/'tools/tests/Run-EditModeTests.ps1'),
    '-UnityPath', 'D:/2022.3.62f1c1/Editor/Unity.exe', '-ProjectPath', str(root), '-TestFilter', filters,
    '-OutputDirectory', str(output), '-LogFile', str(output/'tests.log'), '-ResultsFile', str(output/'tests.xml'),
    '-NoGraphics', '-ExtraUnityArgs',
    '--yc-replay-source='+str(root/'Logs/HotseatPerf-20261103/target5-baseline/replay.json.before-5736.json'),
    '--yc-replay-commands='+str(root/'Logs/HotseatPerf-20261103/R6/game/命令与准确回执.jsonl'),
    '--yc-replay-report='+str(output/'replay.json'), '--yc-replay-last-before=5736',
    '--yc-quote-source='+str(root/'Logs/HotseatPerf-20261103/target5-r4/replay.json.before-6969.json'),
    '--yc-quote-report='+str(output/'quotes.json'), '--yc-quote-player=4']
print('R7已退出，开始下一版兼容性、移城和付款后投影隔离回归。', flush=True)
# -File不会把多个外部参数绑定进string[]；在PowerShell内构造真实数组。
# 所有字符串使用单引号转义，路径中的$、反引号等不能执行。
def ps_quote(value):
    return "'"+value.replace("'", "''")+"'"
extra_at = args.index('-ExtraUnityArgs')
command = '& '+ps_quote(args[3])
for index in range(4, extra_at-1, 2):
    command += ' '+args[index]+' '+ps_quote(args[index+1])
command += ' -NoGraphics -ExtraUnityArgs @('+','.join(ps_quote(value) for value in args[extra_at+1:])+')'
raise SystemExit(subprocess.run(['pwsh','-NoProfile','-Command',command], cwd=root, creationflags=subprocess.CREATE_NO_WINDOW).returncode)
