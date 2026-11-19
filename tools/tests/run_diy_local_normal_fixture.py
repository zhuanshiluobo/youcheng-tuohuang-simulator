"""运行隔离本地正常整局，或从该局自然产生的事件待答原档正常续局。"""
import argparse
import base64
import hashlib
import json
import os
import subprocess
import sys
import time
from pathlib import Path


project = Path(__file__).resolve().parents[2]
results = (project / 'TestResults').resolve()
builds = (project / 'Builds').resolve()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--build-manifest', type=Path, required=True)
parser.add_argument('--resume-source', type=Path, help='已完整结束且保留事件待答原档的本地夹具目录。')
parser.add_argument('--source-exit-timeout', action='store_true', help='允许从已到终局但退出超时且保留现场的原局继续独立读档验证。')
args = parser.parse_args()
root = args.output.resolve()
manifest_path = args.build_manifest.resolve(strict=True)
if root == results or not root.is_relative_to(results) or root.exists():
    raise ValueError('测试输出必须是 TestResults 内尚不存在的明确子目录。')
if args.source_exit_timeout and not args.resume_source:
    raise ValueError('退出超时选项仅能用于明确的原局续档。')
if manifest_path == results or not manifest_path.is_relative_to(results):
    raise ValueError('构建清单必须位于 TestResults。')
manifest = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
build = Path(manifest['root']).resolve(strict=True)
if build == builds or not build.is_relative_to(builds):
    raise ValueError('程序构建必须位于 Builds 的明确子目录。')


def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(chunk)
    return value.hexdigest()


expected = {row['path']: row for row in manifest['files']}
actual = {}
for path in build.rglob('*'):
    if path.is_symlink() or getattr(path.lstat(), 'st_file_attributes', 0) & 0x400:
        raise ValueError('冻结构建包含链接：' + str(path))
    if path.is_file():
        relative = path.relative_to(build).as_posix()
        actual[relative] = {'path': relative, 'bytes': path.stat().st_size, 'sha256': digest(path)}
if len(expected) != manifest['fileCount'] or actual != expected or 'tuohuang.exe' not in actual:
    raise ValueError('冻结构建与清单不一致。')
resume = None
if args.resume_source:
    source = args.resume_source.resolve(strict=True)
    if source == results or not source.is_relative_to(results) or root.is_relative_to(source):
        raise ValueError('续局来源必须是 TestResults 内另一明确子目录。')
    read = lambda path: json.loads(path.read_text(encoding='utf-8-sig'))
    observed = read(source / '事件待答原档观察.json')
    source_start = read(source / '启动记录.json')
    source_identity = read(source / 'game/运行身份.json')
    source_report_path = source / 'game/验证结果.json'
    source_report = read(source_report_path)
    exit_path = source / '实际退出记录.json'
    timeout = None
    if exit_path.exists():
        finished = read(exit_path)
        if not finished['passed'] or not finished['naturalExit']:
            raise ValueError('原局未完成或未自然退出。')
        source_exit = '原局已自然退出。'
    else:
        if not args.source_exit_timeout:
            raise ValueError('原局尚未自然退出；不能将终局报告冒充退出通过。')
        timeout = read(source / '终局退出超时首次观察.json')
        if (not timeout['Terminal'] or not timeout['ProcessStillRunning'] or timeout['ForcedTermination'] or
                timeout['SecondsSinceReport'] < 300 or timeout['ProcessId'] != source_start['pid'] or
                timeout['ReportSha256'] != digest(source_report_path) or not timeout['ReportPassed'] or
                not timeout['FullGameCompleted']):
            raise ValueError('原局退出异常的首次现场不完整。')
        source_exit = '原局已到终局但退出超时；原进程和首次现场保留，不计自然退出通过。'
    original = (source / '事件待答原档.json').read_bytes()
    envelope = json.loads(original.decode('utf-8-sig'))
    checksum = base64.b64encode(hashlib.sha256(envelope['Payload'].encode('utf-8')).digest()).decode('ascii')
    saved = json.loads(envelope['Payload'])
    snapshot = json.loads(saved['ArchiveJson'])['Snapshot']
    state = snapshot['State']
    requests = [item for item in state['EffectRuntime']['InteractionRequests']
                if item.get('InteractionTypeId') == 'event_card.option' and item.get('Status') == 'open' and item.get('CandidateIds')]
    if (not source_report['Passed'] or not source_report['FullGameCompleted'] or source_report['DeferredContentDifferences'] != 0 or
            not observed['found'] or
            observed.get('mode') != '本地热座' or source_start['buildTreeSha256'] != manifest['treeSha256'] or
            envelope.get('Format') != 'YC.MatchSave' or envelope.get('Checksum') != checksum or
            hashlib.sha256(original).hexdigest() != observed['saveSha256'] or saved['Mode'] != 0 or saved['LocalTestNetwork'] or
            saved['GameId'] != state['GameId'] or saved['ContentHash'] != state['ContentPackHash'] or
            observed['gameId'] != state['GameId'] or observed['contentHash'] != state['ContentPackHash'] or
            timeout is not None and timeout['GameId'] != state['GameId'] or
            observed['revision'] != snapshot['StateRevision'] or state['RuleConfiguration']['RuleModeId'] != 'standard' or
            len(saved['Seats']) != 4 or len(requests) != 1 or
            len(observed['requests']) != 1 or any(requests[0].get(key) != observed['requests'][0].get(key) for key in
                ('InteractionId', 'OwnerEffectId', 'AnsweringPlayerId', 'CandidateIds', 'CandidateSetVersion'))):
        raise ValueError('原局、构建身份或自然产生的单个事件待答存档不符。')
    resume = {'source': str(source), 'saveSha256': observed['saveSha256'], 'gameId': saved['GameId'],
              'contentHash': saved['ContentHash'], 'revision': snapshot['StateRevision'],
              'assembly': source_identity['Assembly'], 'request': requests[0], 'sourceExit': source_exit, 'bytes': original}
root.mkdir(parents=True)
game = root / 'game'
saves = root / 'MatchSaves'
content = root / '隔离用户内容'
temporary = root / '隔离临时目录'
for path in (game, saves, content, temporary):
    path.mkdir()
executable = build / 'tuohuang.exe'
arguments = [str(executable), '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720',
             '-logFile', str(game / 'Player.log'), '--yc-hotseat-ui-validation-output=' + str(game),
             '--yc-hotseat-ui-validation-case=full-game', '--yc-ui-rule-mode=standard',
             '--yc-hotseat-player-count=4', '--yc-campaign-expansions=', '--yc-ui-defer-content-differences=true',
             '--yc-ui-campaign-path=' + str(root / 'campaign.json'), '--yc-amiya-original-viewer=true',
             '--yc-performance-save-root=' + str(saves), '--yc-test-content-root=' + str(content),
             '--yc-validation-frame-rate=30']
if resume:
    with (saves / 'auto-1.json').open('xb') as stream:
        stream.write(resume.pop('bytes'))
    arguments.extend(['--yc-local-resume-game-id=' + resume['gameId'], '--yc-local-resume-slot=auto-1',
                      '--yc-local-resume-rule-mode=standard', '--yc-local-resume-player-count=4',
                      '--yc-local-resume-content-hash=' + resume['contentHash'],
                      '--yc-local-resume-revision=' + str(resume['revision']),
                      '--yc-local-resume-source-mvid=' + resume['assembly']])
    resume['policy'] = '续局测试驱动重新记录行动偏好，不沿用原局累计策略；只选择可见合法候选，不把原局覆盖计作新覆盖。'
    with (root / '续局原档身份.json').open('x', encoding='utf-8') as stream:
        json.dump(resume, stream, ensure_ascii=False, indent=2)
observer = None if resume else subprocess.Popen(
    [sys.executable, '-X', 'utf8', str(project / 'tools/tests/capture_diy_pending_event.py'),
     '--evidence', str(root), '--local', '--min-round', '2'], cwd=project,
    creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
try:
    environment = os.environ.copy()
    environment.update({'TEMP': str(temporary), 'TMP': str(temporary)})
    player = subprocess.Popen(arguments, cwd=build, env=environment)
except Exception:
    if observer:
        observer.terminate()
        observer.wait()
    raise
start = {'pid': player.pid, 'observerPid': observer.pid if observer else None, 'executable': str(executable),
         'executableSha256': actual['tuohuang.exe']['sha256'], 'buildTreeSha256': manifest['treeSha256'],
         'arguments': arguments, 'temporaryRoot': str(temporary), 'normalMenuFlow': True, 'visiblePlayer': True,
         'stateReplacement': False, 'randomSeedOverride': False, 'computerUse': False}
with (root / '启动记录.json').open('x', encoding='utf-8') as stream:
    json.dump(start, stream, ensure_ascii=False, indent=2)
print('已启动隔离本地正常整局：', player.pid, flush=True)
while player.poll() is None:
    time.sleep(2)
if observer:
    try:
        observer.wait(timeout=30)
    except subprocess.TimeoutExpired:
        # 只有没有形成结束报告的异常退出才会留下观察器；停止这一只读助手，不触碰游戏或存档。
        observer.terminate()
        observer.wait()
report_path = game / '验证结果.json'
report = json.loads(report_path.read_text(encoding='utf-8-sig')) if report_path.exists() else None
result = {'exitCode': player.returncode, 'observerExitCode': observer.returncode if observer else None,
          'naturalExit': player.returncode == 0 and bool(report and report.get('NaturalExitRequested')),
          'passed': player.returncode == 0 and bool(report and report.get('Passed') and report.get('FullGameCompleted')
                                                   and report.get('DeferredContentDifferences') == 0),
          'report': str(report_path), 'observation': str(root / '事件待答原档观察.json') if observer else None,
          'resumeSource': resume['source'] if resume else None}
with (root / '实际退出记录.json').open('x', encoding='utf-8') as stream:
    json.dump(result, stream, ensure_ascii=False, indent=2)
print('隔离本地正常整局退出：', result, flush=True)
if not result['passed']:
    raise SystemExit(1)
