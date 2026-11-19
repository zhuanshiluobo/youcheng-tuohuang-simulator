"""从已保留的真实本地终局存档，通过原主页读取并验证新构建的自然退出。"""
import argparse
import base64
import hashlib
import json
import os
import subprocess
import time
from pathlib import Path


project = Path(__file__).resolve().parents[2]
results = (project / 'TestResults').resolve()
builds = (project / 'Builds').resolve()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source', type=Path, required=True)
parser.add_argument('--build-manifest', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
source, manifest_path, root = args.source.resolve(strict=True), args.build_manifest.resolve(strict=True), args.output.resolve()
if any(path == results or not path.is_relative_to(results) for path in (source, manifest_path, root)) or root.exists() or root.is_relative_to(source):
    raise ValueError('来源、构建清单和全新输出必须是 TestResults 内的独立明确路径。')


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(chunk)
    return value.hexdigest()


manifest = read(manifest_path)
build = Path(manifest['root']).resolve(strict=True)
if build == builds or not build.is_relative_to(builds):
    raise ValueError('新构建不在 Builds 的明确子目录内。')
expected = {row['path']: row for row in manifest['files']}
actual = {}
for path in build.rglob('*'):
    if path.is_symlink() or getattr(path.lstat(), 'st_file_attributes', 0) & 0x400:
        raise ValueError('冻结构建包含链接：' + str(path))
    if path.is_file():
        relative = path.relative_to(build).as_posix()
        actual[relative] = {'path': relative, 'bytes': path.stat().st_size, 'sha256': digest(path)}
if len(expected) != manifest['fileCount'] or actual != expected or 'tuohuang.exe' not in actual:
    raise ValueError('新构建文件与冻结清单不一致。')
observation = read(source / '终局自动存档观察.json')
timeout = read(source / '终局退出超时首次观察.json')
prior_start = read(source / '启动记录.json')
prior_identity = read(source / 'game/运行身份.json')
prior_report = read(source / 'game/验证结果.json')
raw = (source / '终局自动存档副本.json').read_bytes()
envelope = json.loads(raw.decode('utf-8-sig'))
checksum = base64.b64encode(hashlib.sha256(envelope['Payload'].encode('utf-8')).digest()).decode('ascii')
saved = json.loads(envelope['Payload'])
snapshot = json.loads(saved['ArchiveJson'])['Snapshot']
state = snapshot['State']
if (envelope['Format'] != 'YC.MatchSave' or envelope['Checksum'] != checksum or
        hashlib.sha256(raw).hexdigest() != observation['sha256'] or saved['Mode'] != 0 or saved['LocalTestNetwork'] or
        saved['GameId'] != observation['gameId'] or saved['GameId'] != state['GameId'] or
        saved['ContentHash'] != observation['contentHash'] or saved['ContentHash'] != state['ContentPackHash'] or
        snapshot['StateRevision'] != observation['revision'] or state['Round'] != 8 or not state['FinalScoring']['IsResolved'] or
        state['RuleConfiguration']['RuleModeId'] != 'standard' or len(saved['Seats']) != 4 or
        any(value['Status'] == 'open' for value in state['EffectRuntime']['InteractionRequests']) or
        not prior_report['Passed'] or not prior_report['FullGameCompleted'] or not timeout['ProcessStillRunning'] or
        timeout['ForcedTermination'] or timeout['ProcessId'] != prior_start['pid'] or timeout['GameId'] != saved['GameId'] or
        timeout['ReportSha256'] != digest(source / 'game/验证结果.json')):
    raise ValueError('真实终局原档、首次退出异常或来源身份不符。')
root.mkdir(parents=True)
game, saves, content, temporary = (root / 'game', root / 'MatchSaves', root / '隔离用户内容', root / '隔离临时目录')
for path in (game, saves, content, temporary):
    path.mkdir()
with (saves / 'auto-1.json').open('xb') as stream:
    stream.write(raw)
arguments = [str(build / 'tuohuang.exe'), '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720',
             '-logFile', str(game / 'Player.log'), '--yc-hotseat-ui-validation-output=' + str(game),
             '--yc-hotseat-ui-validation-case=full-game', '--yc-ui-rule-mode=standard', '--yc-hotseat-player-count=4',
             '--yc-campaign-expansions=', '--yc-ui-defer-content-differences=true', '--yc-ui-campaign-path=' + str(root / 'campaign.json'),
             '--yc-amiya-original-viewer=true', '--yc-performance-save-root=' + str(saves), '--yc-test-content-root=' + str(content),
             '--yc-validation-frame-rate=30', '--yc-local-resume-game-id=' + saved['GameId'], '--yc-local-resume-slot=auto-1',
             '--yc-local-resume-rule-mode=standard', '--yc-local-resume-player-count=4',
             '--yc-local-resume-content-hash=' + saved['ContentHash'], '--yc-local-resume-revision=' + str(snapshot['StateRevision']),
             '--yc-local-resume-source-mvid=' + prior_identity['Assembly']]
environment = os.environ.copy()
environment.update({'TEMP': str(temporary), 'TMP': str(temporary)})
process = subprocess.Popen(arguments, cwd=build, env=environment)
start = {'pid': process.pid, 'source': str(source), 'sourceSaveSha256': observation['sha256'],
         'sourceAssembly': prior_identity['Assembly'], 'newBuildTreeSha256': manifest['treeSha256'], 'arguments': arguments,
         'normalLoadWindow': True, 'stateInjection': False, 'sourceFailureEvidencePreserved': True,
         'scope': '同一原局自然终局存档跨构建读取，单独验证实际退出；不计新局或事件覆盖。'}
(root / '退出复测启动.json').write_text(json.dumps(start, ensure_ascii=False, indent=2), encoding='utf-8')
print('真实终局原档退出复测进程：', process.pid, flush=True)
report_path = game / '验证结果.json'
boundary_path = game / '本地同局正式恢复初始边界.json'
timeout_recorded = False
while process.poll() is None:
    if report_path.exists() and not timeout_recorded:
        elapsed = max(0.0, time.time() - report_path.stat().st_mtime)
        if elapsed >= 120:
            failure = {'processId': process.pid, 'secondsSinceReport': elapsed, 'processStillRunning': True,
                       'forcedTermination': False, 'reportSha256': digest(report_path),
                       'scope': '保留真实终局退出超时现场，继续等待进程自行退出。'}
            (root / '终局退出超时首次观察.json').write_text(json.dumps(failure, ensure_ascii=False, indent=2), encoding='utf-8')
            print('终局退出超过 120 秒，已保留现场并继续等待。', flush=True)
            timeout_recorded = True
    time.sleep(1)
report = read(report_path) if report_path.exists() else None
boundary = read(boundary_path) if boundary_path.exists() else None
seconds_since_report = max(0.0, time.time() - report_path.stat().st_mtime) if report_path.exists() else None
timely = seconds_since_report is not None and seconds_since_report <= 120 and not timeout_recorded
passed = (process.returncode == 0 and bool(report and report['Passed'] and report['FullGameCompleted'] and
          report['NaturalExitRequested'] and report['DeferredContentDifferences'] == 0) and
          bool(boundary and boundary['GameId'] == saved['GameId'] and boundary['ContentPackHash'] == saved['ContentHash'] and
          boundary['Revision'] == snapshot['StateRevision'] and boundary['CommandsSinceOriginalLoad'] == 0 and
          boundary['RestoredThroughOriginalUi'] and boundary['PreviousAssembly'] == prior_identity['Assembly']) and timely)
natural = bool(report and report.get('NaturalExitRequested') and process.returncode == report.get('ExitCode'))
result = {'passed': passed, 'exitCode': process.returncode, 'naturalExit': natural,
          'secondsSinceReport': seconds_since_report, 'exitWithin120Seconds': timely,
          'gameId': saved['GameId'], 'contentHash': saved['ContentHash'], 'revision': snapshot['StateRevision'],
          'sourceAssembly': prior_identity['Assembly'], 'newAssembly': boundary['CurrentAssembly'] if boundary else None,
          'sourceSaveSha256': observation['sha256'], 'sourceExitFailurePreserved': True,
          'scope': '只核对真实终局原档经原主页读取后的自然退出；不宣称真实 Steam 联机通过。'}
(root / '退出复测结果.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print('真实终局原档退出复测：', result, flush=True)
if not passed:
    raise SystemExit(1)
