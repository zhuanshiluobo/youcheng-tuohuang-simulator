"""当前独立程序的只读监控；更新CSV，结束时记录最终判定，不操作游戏。"""
import ctypes
import datetime
import json
import pathlib
import subprocess
import sys
from ctypes import wintypes

run = pathlib.Path(sys.argv[1]).resolve()
p95_target = next((float(arg.split('=', 1)[1]) for arg in sys.argv[2:] if arg.startswith('--p95-target=')), None)
identity = json.loads((run / 'process.json').read_text(encoding='utf-8-sig'))
kernel = ctypes.WinDLL('kernel32', use_last_error=True)
kernel.OpenProcess.restype = wintypes.HANDLE
kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
kernel.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
kernel.CloseHandle.argtypes = [wintypes.HANDLE]
handle = kernel.OpenProcess(0x100000 | 0x1000, False, identity['Pid'])
if not handle:
    raise ctypes.WinError(ctypes.get_last_error())
summarizer = pathlib.Path(__file__).with_name('summarize_hotseat_performance.py')
process_exited = False
outcome_path = run / 'game/验证结果.json'
try:
    while True:
        subprocess.run([sys.executable, '-X', 'utf8', str(summarizer), str(run)],
                       stdout=subprocess.DEVNULL, check=True)
        if outcome_path.exists():
            # 夹具结果与进程退出分别记录；请求退出后Player仍可能滞留。
            process_exited = kernel.WaitForSingleObject(handle, 0) == 0
            break
        if kernel.WaitForSingleObject(handle, 30000) == 0:
            process_exited = True
            break
    subprocess.run([sys.executable, '-X', 'utf8', str(summarizer), str(run)],
                   stdout=subprocess.DEVNULL, check=True)
    code = wintypes.DWORD()
    if not kernel.GetExitCodeProcess(handle, ctypes.byref(code)):
        raise ctypes.WinError(ctypes.get_last_error())
    outcome = json.loads(outcome_path.read_text(encoding='utf-8-sig')) if outcome_path.exists() else {}
    summary = json.loads((run / '性能汇总.json').read_text(encoding='utf-8'))
    over = sum(summary.get(kind, {}).get('OverTenSeconds', 0) for kind in ('command', 'ui', 'ui.map'))
    over_five = sum(summary.get(kind, {}).get('OverFiveSeconds', 0) for kind in ('command', 'ui', 'ui.map'))
    measured = sum(summary.get(kind, {}).get('Count', 0) for kind in ('command', 'ui', 'ui.map'))
    final = {'完成时间UTC': datetime.datetime.now(datetime.timezone.utc).isoformat(),
             '进程ID': identity['Pid'], '退出码': code.value,
             '进程已退出': process_exited,
             '夹具已结束但进程仍存活': bool(outcome) and not process_exited,
             '完整对局通过': outcome.get('Passed', False), '停止阶段': outcome.get('Stage'),
             '首次错误': outcome.get('Error'), '耗时小于10秒通过': over == 0,
             '本轮目标秒': 5, '耗时小于5秒通过': measured > 0 and over_five == 0,
             '超过或等于5秒记录数_包含嵌套命令': over_five,
             '超过或等于10秒记录数_包含嵌套命令': over,
             '内存稳定性': '需按逐步及每回合曲线核验，不能由耗时或进程退出推定通过',
             '未强制GC': True, '监控未操作游戏': True}
    if p95_target is not None:
        final['历史单次诊断阈值秒'] = final.pop('本轮目标秒')
        final['本轮目标'] = '八类单次界面操作P95'
        final['本轮P95目标秒'] = p95_target
        subprocess.run([sys.executable, '-X', 'utf8', str(pathlib.Path(__file__).with_name('report_hotseat_p95_target.py')),
                        str(run), '--target', str(p95_target)], check=True, stdout=subprocess.DEVNULL)
        final['正常整局P95目标达成'] = json.loads((run/'P95目标验收.json').read_text(encoding='utf-8'))['正常整局P95目标达成']
    (run / '最终监控结论.json').write_text(json.dumps(final, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(final, ensure_ascii=False, indent=2), flush=True)
finally:
    kernel.CloseHandle(handle)
