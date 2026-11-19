"""只读监听隔离对局的正式自动存档，保留实际存在的事件待答原档。"""
import argparse
import base64
import hashlib
import json
import time
from pathlib import Path


project = Path(__file__).resolve().parents[2]
allowed = (project / 'TestResults').resolve()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--evidence', type=Path, required=True)
parser.add_argument('--max-seconds', type=int, default=3600)
parser.add_argument('--local', action='store_true', help='观察本地热座正常局的自动存档。')
parser.add_argument('--min-round', type=int, default=1, help='仅保留达到此回合的自然自动存档。')
args = parser.parse_args()
root = args.evidence.resolve(strict=True)
if root == allowed or not root.is_relative_to(allowed) or not 1 <= args.max_seconds <= 7200 or not 1 <= args.min_round <= 8:
    raise ValueError('对局证据须是 TestResults 内的明确子目录，观察时间须为 1 至 7200 秒，回合须为 1 至 8。')
save = root / ('MatchSaves/auto-1.json' if args.local else '联机隔离存档/host/auto-1.json')
report = root / ('game/验证结果.json' if args.local else '四端正常联机/host/验证结果.json')
copy, result = root / '事件待答原档.json', root / '事件待答原档观察.json'
if copy.exists() or result.exists():
    raise FileExistsError('观察结果已存在，不覆盖。')
last_identity = None
deadline = time.monotonic() + args.max_seconds
while time.monotonic() < deadline:
    try:
        stat = save.stat()
        identity = (stat.st_mtime_ns, stat.st_size)
        if identity != last_identity:
            raw = save.read_bytes()
            envelope = json.loads(raw.decode('utf-8-sig'))
            digest = base64.b64encode(hashlib.sha256(envelope['Payload'].encode('utf-8')).digest()).decode('ascii')
            if envelope.get('Format') != 'YC.MatchSave' or envelope.get('Checksum') != digest:
                raise ValueError('正在观察的自动存档校验和不匹配。')
            data = json.loads(envelope['Payload'])
            snapshot = json.loads(data['ArchiveJson'])['Snapshot']
            state = snapshot['State']
            requests = [value for value in state['EffectRuntime']['InteractionRequests']
                        if value.get('InteractionTypeId') == 'event_card.option' and value.get('Status') == 'open' and value.get('CandidateIds')]
            if requests and state['Round'] >= args.min_round:
                if data['GameId'] != state['GameId'] or data['ContentHash'] != state['ContentPackHash']:
                    raise ValueError('待答原档的对局或活动内容身份不一致。')
                if args.local:
                    if data['Mode'] != 0 or data['LocalTestNetwork'] or data['SimulationCheckpointJson']:
                        raise ValueError('待答原档不是本地热座对局。')
                    sequence = None
                else:
                    baseline = json.loads(data['SimulationCheckpointJson'])['Baseline']
                    if not data['LocalTestNetwork'] or baseline['GameId'] != state['GameId']:
                        raise ValueError('待答原档不是当前真实本地联机档。')
                    sequence = baseline['ConfirmedGlobalSequence']
                with copy.open('xb') as stream:
                    stream.write(raw)
                info = {'found': True, 'gameId': state['GameId'], 'contentHash': state['ContentPackHash'],
                        'round': state['Round'], 'revision': snapshot['StateRevision'],
                        'confirmedSequence': sequence, 'saveSha256': hashlib.sha256(raw).hexdigest(),
                        'savedFile': str(copy), 'requests': [{key: request.get(key) for key in
                            ('InteractionId', 'OwnerEffectId', 'AnsweringPlayerId', 'CandidateIds', 'CandidateSetVersion')} for request in requests],
                        'scope': '只读复制自然产生的原自动存档，不改变对局、候选或随机。',
                        'mode': '本地热座' if args.local else '本机联机'}
                with result.open('x', encoding='utf-8') as stream:
                    json.dump(info, stream, ensure_ascii=False, indent=2)
                print('已保留真实事件待答自动存档：', state['GameId'], '修订', snapshot['StateRevision'], flush=True)
                break
            last_identity = identity
    except (FileNotFoundError, PermissionError, json.JSONDecodeError, UnicodeDecodeError):
        pass  # 自动替换槽位时只等待下一次完整文件，不改动原件。
    if report.exists():
        with result.open('x', encoding='utf-8') as stream:
            json.dump({'found': False, 'reason': '本局结束前未观察到事件待答自动存档。'}, stream, ensure_ascii=False, indent=2)
        print('本局未观察到事件待答自动存档。', flush=True)
        break
    time.sleep(.2 if args.local else 1)
else:
    with result.open('x', encoding='utf-8') as stream:
        json.dump({'found': False, 'reason': '观察时限内未出现事件待答自动存档。'}, stream, ensure_ascii=False, indent=2)
    print('观察时限内未出现事件待答自动存档。', flush=True)
