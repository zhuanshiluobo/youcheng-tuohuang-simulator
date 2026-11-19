"""核对正常四端局中旧前缀被拒绝后的真实刷新、重新选择和完整回执。"""
import argparse
import hashlib
import json
import re
from pathlib import Path


project = Path(__file__).resolve().parents[2]
allowed = (project / 'TestResults').resolve()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--cohort', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
cohort, output = args.cohort.resolve(strict=True), args.output.resolve()
if any(path == allowed or not path.is_relative_to(allowed) for path in (cohort, output)):
    raise ValueError('输入及输出必须位于项目 TestResults 的明确子目录。')


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def lines(path):
    return [json.loads(line) for line in path.read_text(encoding='utf-8-sig').splitlines() if line.strip()]


def parameters(command):
    value = command.get('Parameters') or {}
    return value if isinstance(value, dict) else {entry['Key']: entry['Value'] for entry in value}


def normalized_command(command):
    result = dict(command)
    result['Parameters'] = parameters(command)
    return result


def matches_applied(original, actual):
    left, right = normalized_command(original), normalized_command(actual)
    original_params, actual_params = left.pop('Parameters'), right.pop('Parameters')
    if left != right or any(actual_params.get(key) != value for key, value in original_params.items()):
        return False
    added = set(actual_params) - set(original_params)
    # Host 在受协商的交互提交中附加请求合同标记；其他额外字段一律拒绝。
    return not added or (left.get('Kind') == 15 and added == {'requestContractVersion', 'expectedRequestContractDigest'} and
                         actual_params['requestContractVersion'] == '1' and
                         re.fullmatch(r'[0-9a-f]{64}', actual_params['expectedRequestContractDigest']) is not None)


roles = ('host', 'client2', 'client3', 'client4')
exits = read(cohort / '实际退出记录.json')
states, boundaries, global_rows, role_results, exceptions = {}, {}, {}, {}, []
game_id = content_hash = None
for role in roles:
    directory = cohort / role
    identity = read(directory / '联机运行身份.json')
    report = read(directory / '验证结果.json')
    run = read(directory / '运行身份.json')
    tail = read(directory / '联机终局原回执清单.json')
    boundary = read(directory / '联机终局确认边界.json')
    state_path = directory / '联机正式终局规则状态.json'
    state_bytes = state_path.read_bytes()
    state = json.loads(state_bytes.decode('utf-8-sig'))
    game_id = game_id or state['GameId']
    content_hash = content_hash or state['ContentPackHash']
    if (state['GameId'] != game_id or state['ContentPackHash'] != content_hash or state['Round'] != 8 or
            not state['FinalScoring']['IsResolved'] or identity['GameId'] != game_id or
            identity['ContentPackHash'] != content_hash or not report['Passed'] or not report['FullGameCompleted'] or
            report['DeferredContentDifferences'] != 0 or report['RuntimeErrors'] or
            exits[role]['ExitCode'] != 0 or not exits[role]['NaturalExit']):
        raise ValueError('终局、身份、运行报告或正常退出不符：' + role)
    if (tail['GameId'] != game_id or tail['ContentHash'] != content_hash or tail['InitialSequence'] != 0 or
            not tail['Complete'] or not tail['OriginalPayloadsValid'] or not tail['CompetitionContinuationsComplete'] or
            tail['MissingSequences'] or tail['DuplicateSequences'] or tail['UnresolvedOriginalCommands'] or
            tail['BusinessRejectedCommands'] or tail['UnexpectedRejectedCommands'] or
            tail['FinalSequence'] != boundary['GlobalFenceSequence']):
        raise ValueError('终局原回执清单不完整：' + role)
    applied = [row['Applied'] for row in lines(directory / '四端全局应用回执.jsonl')]
    if applied != tail['Receipts'] or [row['GlobalCommandSequence'] for row in applied] != list(range(1, tail['FinalSequence'] + 1)):
        raise ValueError('全局原回执缺失或不连续：' + role)
    proposals = lines(directory / '本机原控件提案.jsonl')
    local = lines(directory / '命令与准确回执.jsonl')
    proposal_ids = [row['Command']['CommandId'] for row in proposals]
    local_ids = [row['Command']['CommandId'] for row in local]
    if len(set(proposal_ids)) != len(proposal_ids) or len(set(local_ids)) != len(local_ids) or set(proposal_ids) != set(local_ids):
        raise ValueError('本机原提案与准确回执不一一对应：' + role)
    global_by_id = {row['Command']['CommandId']: row for row in applied}
    if len(global_by_id) != len(applied):
        raise ValueError('全局原回执包含重复命令：' + role)
    seen_rejections = []
    for row in local:
        command = row['Command']
        command_id = command['CommandId']
        outcome = row.get('Result') or {}
        result = outcome.get('CommandResult') or {}
        if outcome.get('AppliedLocally') and result.get('Succeeded'):
            actual = global_by_id.get(command_id)
            if not actual or not actual['RuleResult']['Succeeded'] or not matches_applied(command, actual['Command']):
                raise ValueError('本机成功命令没有对应完整全局回执：' + command_id)
            continue
        rejection = row.get('CompetitionRejection') or {}
        proposal = rejection.get('Proposal') or {}
        host_seq = rejection.get('HostSequencedThrough')
        old_params = parameters(command)
        if (not row.get('RejectedByCompetition') or not row.get('CompetitionResolutionObserved') or row.get('ContentRejected') or
                result.get('Succeeded') is not False or (result.get('Validation') or {}).get('ErrorCode') != 13 or
                rejection.get('Status') != 3 or rejection.get('ErrorCode') != 13 or not rejection.get('RequiresRefresh') or
                rejection.get('OrderedCommand') is not None or rejection.get('GameId') != game_id or
                normalized_command(proposal.get('Command') or {}) != normalized_command(command) or
                not isinstance(host_seq, int) or host_seq <= row['ObservedValidatedSequence'] or command_id in global_by_id):
            raise ValueError('被拒绝的旧提案缺少精确竞争与前缀证据：' + command_id)
        previous = next((v for v in applied if v['GlobalCommandSequence'] == host_seq), None)
        retry = [v for v in local if v.get('ReconsideredFromCommandId') == command_id]
        if len(retry) != 1 or not previous or previous['Command']['PlayerId'] == command['PlayerId']:
            raise ValueError('缺少其他玩家先行提交或唯一重新选择：' + command_id)
        follow = retry[0]
        newer = follow['Command']
        new_params = parameters(newer)
        actual = global_by_id.get(newer['CommandId'])
        if (newer['CommandId'] == command_id or newer['PlayerId'] != command['PlayerId'] or
                newer['OptionIds'] != command['OptionIds'] or new_params.get('interactionId') != old_params.get('interactionId') or
                int(new_params.get('expectedRevision', -1)) <= int(old_params.get('expectedRevision', -1)) or
                follow['ObservedValidatedSequence'] < host_seq or follow['SubmittedRequestContract'] != row['SubmittedRequestContract']):
            raise ValueError('刷新后的真实候选或重新选择不完整：' + command_id)
        follow_result = follow.get('Result') or {}
        follow_command_result = follow_result.get('CommandResult') or {}
        if follow.get('RejectedByCompetition'):
            # 再次竞争必须是未应用的真实拒绝；该行本身在当前循环中仍需通过完整拒绝核验。
            if (follow_result.get('AppliedLocally') or follow_command_result.get('Succeeded') is not False or
                    (follow_command_result.get('Validation') or {}).get('ErrorCode') != 13 or actual is not None):
                raise ValueError('再次竞争的拒绝证据不完整：' + newer['CommandId'])
            retry_status = 'rejected_again'
        else:
            if (not follow_result.get('AppliedLocally') or not follow_command_result.get('Succeeded') or
                    not actual or not actual['RuleResult']['Succeeded'] or actual['GlobalCommandSequence'] <= host_seq or
                    not matches_applied(newer, actual['Command'])):
                raise ValueError('刷新后的成功回执不完整：' + command_id)
            retry_status = 'accepted'
        seen_rejections.append(command_id)
        exceptions.append({'role': role, 'player': command['PlayerId'], 'rejectedCommandId': command_id,
                           'oldRevision': int(old_params['expectedRevision']), 'confirmedThrough': host_seq,
                           'interveningPlayer': previous['Command']['PlayerId'], 'retryCommandId': newer['CommandId'],
                           'newRevision': int(new_params['expectedRevision']), 'retryStatus': retry_status,
                           'retrySequence': actual['GlobalCommandSequence'] if actual else None,
                           'sameVisibleRequestAndCandidate': True})
    if set(seen_rejections) != set(tail['NormalCompetitionRejectedCommands']):
        raise ValueError('竞争拒绝清单与逐项核验不一致：' + role)
    # 每条再次竞争链必须最终落到准确成功回执；不可凭“将来会重试”视为完成。
    by_id = {item['Command']['CommandId']: item for item in local}
    for rejected in seen_rejections:
        current, visited = rejected, set()
        while current in seen_rejections:
            if current in visited:
                raise ValueError('竞争重选链存在循环：' + rejected)
            visited.add(current)
            next_rows = [item for item in local if item.get('ReconsideredFromCommandId') == current]
            if len(next_rows) != 1:
                raise ValueError('竞争重选链没有唯一后继：' + current)
            current = next_rows[0]['Command']['CommandId']
        final = by_id.get(current)
        if (not final or not (final.get('Result') or {}).get('AppliedLocally') or not
                ((final.get('Result') or {}).get('CommandResult') or {}).get('Succeeded')):
            raise ValueError('竞争重选链没有最终成功：' + rejected)
    states[role] = hashlib.sha256(state_bytes).hexdigest()
    boundaries[role] = boundary
    global_rows[role] = applied
    role_results[role] = {'player': identity['LocalPlayer'], 'assembly': run['Assembly'],
                          'finalSequence': tail['FinalSequence'], 'digest': boundary['RuleStateDigest'],
                          'proposals': len(proposals), 'reconsidered': len(seen_rejections)}
if ({v['player'] for v in role_results.values()} != {1, 2, 3, 4} or
        len({v['assembly'] for v in role_results.values()}) != 1 or len(set(states.values())) != 1 or
        any(global_rows[role] != global_rows['host'] or boundaries[role] != boundaries['host'] for role in roles)):
    raise ValueError('四端程序集、状态、回执或终局边界不一致。')
result = {'passed': True, 'gameId': game_id, 'contentHash': content_hash, 'roles': role_results,
          'globalReceipts': len(global_rows['host']), 'terminalStateSha256': states['host'],
          'reconsideredStaleProposals': exceptions,
          'scope': '严格保留并核对旧提案的真实拒绝、前缀变化、当前原候选重新选择及成功回执；不将拒绝伪装成成功。',
          'legacySummary': ('旧汇总器不支持竞争拒绝；已有汇总文件保留。' if (cohort / '四端整局验收汇总.json').exists()
                            else '旧汇总器不支持竞争拒绝；此目录没有旧汇总文件。')}
with output.open('x', encoding='utf-8') as stream:
    json.dump(result, stream, ensure_ascii=False, indent=2)
print(f"四端竞争及终局核验通过：{len(global_rows['host'])} 条全局回执，{len(exceptions)} 次旧前缀拒绝后重新选择。")
