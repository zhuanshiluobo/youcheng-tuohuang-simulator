"""只读四端正常流程原件，生成本局联机验收汇总。"""
import hashlib
import json
import pathlib
import sys

root = pathlib.Path(sys.argv[1]).resolve()
roles = ['host', 'client2', 'client3', 'client4']

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def lines(path):
    if not path.exists():
        return []
    return [json.loads(line) for line in path.read_text(encoding='utf-8-sig').splitlines() if line.strip()]

def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(',', ':'))

def command_payload(command):
    # 仅统一GameCommand字典与传输DTO参数序列；全部事件载荷另作原样严格比较。
    value = dict(command)
    parameters = value.get('Parameters') or {}
    value['Parameters'] = parameters if isinstance(parameters, dict) else {item['Key']: item['Value'] for item in parameters}
    return canonical(value)

def recovery_summary(folder, report):
    interruptions = lines(folder / '联机原始中断.jsonl')
    events = lines(folder / '联机正式恢复事件.jsonl')
    completed = lines(folder / '联机正式恢复完成.jsonl')
    key = lambda identity: (identity.get('GameId'), identity.get('SimulationEpoch'), identity.get('MembershipVersion'))
    expected = {key(row['Notice']) for row in events if row.get('Event') == 'Started' and row.get('Notice')}
    expected.update((row['Fault']['GameId'], row['Fault']['SimulationEpoch'] + 1, row['Fault']['MembershipVersion']) for row in interruptions)
    installed = {key(row['Installed']) for row in completed if row.get('Installed')}
    pending = sorted(expected - installed, key=lambda value: json.dumps(value, ensure_ascii=False))
    observation = read(folder / '恢复验证等待未完成.json') if (folder / '恢复验证等待未完成.json').exists() else None
    return dict(
        原始协议故障记录数=len(lines(folder / '联机真实协议故障.jsonl')),
        正式接纳中断数=len(interruptions), 恢复开始通知数=sum(row.get('Event') == 'Started' for row in events),
        安装完成通知数=sum(row.get('Event') == 'Installed' for row in events), 正式前缀与消费者就绪数=len(completed),
        未完成的正式恢复=[dict(GameId=value[0], SimulationEpoch=value[1], MembershipVersion=value[2]) for value in pending],
        验证等待未完成=observation, 最终验证失败=not report['Passed'],
        最终致命失败=not report['Passed'] and observation is None, 最终错误=report.get('Error'),
        状态='仍未完成恢复' if pending else '正式恢复完成' if completed else '无正式恢复完成记录',
    )

def confirmed_recovery_chains(data):
    """仅依四端真实安装边界裁定旧代次尾段，保留原始行号和完整原件。"""
    issues, cycles_by_role = [], {}
    for role, value in data.items():
        cycles = []
        for row in value['completed']:
            source, installed, notice = row.get('Source') or {}, row.get('Installed') or {}, row.get('Notice') or {}
            boundary = row.get('CheckpointBoundary') or {}
            checkpoint = installed.get('CheckpointGlobalSequence')
            identity = (source.get('GameId'), source.get('SimulationEpoch'), installed.get('SimulationEpoch'), source.get('MembershipVersion'))
            valid = (all(x is not None for x in identity) and bool(identity[0]) and bool(source.get('ContentPackHash')) and bool(installed.get('CheckpointPrefixDigest')) and isinstance(checkpoint, int) and
                identity[2] == identity[1] + 1 and installed.get('GameId') == identity[0] and
                installed.get('ContentPackHash') == source.get('ContentPackHash') and installed.get('MembershipVersion') == identity[3] and
                installed.get('LocalPlayerId') == source.get('LocalPlayerId') and
                notice.get('GameId') == identity[0] and notice.get('SourceSimulationEpoch') == identity[1] and
                notice.get('SimulationEpoch') == identity[2] and notice.get('MembershipVersion') == identity[3] and
                boundary.get('ProtocolVersion') == 1 and boundary.get('GameId') == identity[0] and
                boundary.get('SimulationEpoch') == identity[2] and boundary.get('MembershipVersion') == identity[3] and
                boundary.get('GlobalFenceSequence') == checkpoint and bool(boundary.get('FactDigest')) and
                boundary.get('CommandPrefixDigest') == installed.get('CheckpointPrefixDigest') and
                any(e.get('Event') == 'Started' and (e.get('Notice') or {}).get('SimulationEpoch') == identity[2] and
                    (e.get('Notice') or {}).get('SourceSimulationEpoch') == identity[1] and (e.get('Notice') or {}).get('GameId') == identity[0]
                    for e in value['events']) and
                any(e.get('Event') == 'Installed' and (e.get('Installed') or {}).get('SimulationEpoch') == identity[2] and
                    (e.get('Installed') or {}).get('GameId') == identity[0] and e.get('Stopped') is False and e.get('RequiresRecovery') is False
                    for e in value['events']))
            prefix_proven = (source.get('CheckpointGlobalSequence') == checkpoint and
                source.get('CheckpointPrefixDigest') == installed.get('CheckpointPrefixDigest')) or any(
                (p.get('Prefix') or {}).get('GameId') == identity[0] and (p.get('Prefix') or {}).get('SimulationEpoch') == identity[1] and
                (p.get('Prefix') or {}).get('MembershipVersion') == identity[3] and
                (p.get('Prefix') or {}).get('ValidatedThroughSequence') == checkpoint and
                (p.get('Prefix') or {}).get('PrefixDigest') == installed.get('CheckpointPrefixDigest') for p in value['prefixes'])
            if not valid or not prefix_proven:
                issues.append(role + ':恢复边界或原确认前缀证明不完整')
                continue
            cycles.append(dict(GameId=identity[0], SourceEpoch=identity[1], TargetEpoch=identity[2], MembershipVersion=identity[3],
                CheckpointSequence=checkpoint, PrefixDigest=installed.get('CheckpointPrefixDigest'), ContentHash=installed.get('ContentPackHash'),
                CheckpointBoundary=boundary))
        cycles_by_role[role] = cycles
    reference = cycles_by_role['host']
    used = any(value['completed'] for value in data.values())
    if used and not all(canonical(cycles) == canonical(reference) for cycles in cycles_by_role.values()):
        issues.append('四端恢复安装边界不一致或缺失')
    originals = {}
    if used:
        for value in data.values():
            for proposal in value['proposals']:
                command = proposal['Command']
                if command['CommandId'] in originals:
                    issues.append('原始提案存在重复CommandId，不能按退役折叠')
                originals[command['CommandId']] = command
    for index, cycle in enumerate(reference):
        if index and (cycle['SourceEpoch'] != reference[index-1]['TargetEpoch'] or
                cycle['CheckpointSequence'] < reference[index-1]['CheckpointSequence']):
            issues.append('恢复代次或确认前缀不连续')
    effective, retired = {}, {}
    for role, value in data.items():
        effective[role], retired[role] = [], []
        for index, receipt in enumerate(value['applied']):
            if used and (receipt['Command']['CommandId'] not in originals or
                command_payload(receipt['Command']) != command_payload(originals.get(receipt['Command']['CommandId'], {}))):
                issues.append(role + ':完整原回执没有匹配原始提案，不能通过恢复裁尾掩盖')
            cycle = next((c for c in reference if c['SourceEpoch'] == receipt.get('SimulationEpoch')), None)
            if used and (not reference or receipt.get('GameId') != reference[0]['GameId'] or
                receipt.get('MembershipVersion') != reference[0]['MembershipVersion'] or
                receipt.get('SimulationEpoch') not in {c['SourceEpoch'] for c in reference} | {reference[-1]['TargetEpoch']}):
                issues.append(role + ':原回执身份不属于正式恢复链')
            if cycle and receipt['GlobalCommandSequence'] > cycle['CheckpointSequence']:
                if not receipt['RuleResult']['Succeeded']:
                    issues.append(role + ':真实失败回执不能按恢复尾段吞掉')
                retired[role].append(dict(RawLine=index+1, CommandId=receipt['Command']['CommandId'],
                    SimulationEpoch=receipt['SimulationEpoch'], Sequence=receipt['GlobalCommandSequence'],
                    FullPayloadSHA256=hashlib.sha256(canonical(receipt).encode()).hexdigest(), RecoveryBoundary=cycle))
            else:
                effective[role].append(receipt)
    same = all(canonical(rows) == canonical(effective['host']) for rows in effective.values())
    sequence = [row['GlobalCommandSequence'] for row in effective['host']]
    contiguous = bool(sequence) and sequence == list(range(1, len(sequence)+1))
    if used and len({row['Command']['CommandId'] for row in effective['host']}) != len(effective['host']):
        issues.append('最终有效链重复应用同一CommandId')
    return dict(Used=used, Issues=issues, Cycles=reference, Effective=effective, RetiredRaw=retired,
        FullEffectivePayloadsEqual=same, EffectiveSequenceContiguous=contiguous)

def local_recovery_attempts(value, chain, role):
    issues, retired_ids = [], set()
    proposals = {row['Command']['CommandId']: row for row in value['proposals']}
    observed = {canonical(row['Callback']) for row in value['observations']}
    effective_ids = {row['Command']['CommandId'] for row in chain['Effective'][role] if row['RuleResult']['Succeeded']}
    for retirement in value['retirements']:
        callback, original = retirement.get('Callback') or {}, retirement.get('Original') or {}
        proposal = proposals.get(original.get('CommandId'))
        source, installed, notice = retirement.get('Source') or {}, retirement.get('Installed') or {}, retirement.get('Notice') or {}
        cycle = next((c for c in chain['Cycles'] if c['GameId'] == callback.get('GameId') and c['SourceEpoch'] == source.get('SimulationEpoch') and
            c['TargetEpoch'] == installed.get('SimulationEpoch') and c['MembershipVersion'] == installed.get('MembershipVersion')), None)
        step = retirement.get('StepId')
        valid = (cycle is not None and proposal is not None and canonical(proposal['Command']) == canonical(original) and
            proposal.get('StepId') == step and bool(step) and proposal.get('SimulationEpoch') == cycle['SourceEpoch'] and
            callback.get('CommandId') == original.get('CommandId') and callback.get('Accepted') is False and
            callback.get('ErrorCode') == 13 and callback.get('ErrorName') == 'StateRevisionMismatch' and callback.get('RequiresRefresh') is True and canonical(callback) in observed and
            source.get('GameId') == installed.get('GameId') == cycle['GameId'] and source.get('MembershipVersion') == cycle['MembershipVersion'] and
            source.get('ContentPackHash') == installed.get('ContentPackHash') == cycle['ContentHash'] and retirement.get('SimulationEpoch') == cycle['SourceEpoch'] and
            original.get('PlayerId') == source.get('LocalPlayerId') == installed.get('LocalPlayerId') and
            notice.get('GameId') == cycle['GameId'] and notice.get('MembershipVersion') == cycle['MembershipVersion'] and
            notice.get('SourceSimulationEpoch') == cycle['SourceEpoch'] and notice.get('SimulationEpoch') == cycle['TargetEpoch'] and
            retirement.get('AppliedLocally') is False and retirement.get('RuleResult') is None and
            any(row.get('StepId') == step and original['CommandId'] in row.get('OriginalCommandIds', []) and row.get('OldContinuationExecuted') is False for row in value['steps']) and
            any(row.get('RetiredStepId') == step and row.get('NewStepId') and row['NewStepId'] != step and
                row.get('OriginalChoiceReused') is False and (row.get('Current') or {}).get('GameId') == cycle['GameId'] for row in value['reentries']) and
            not any(row.get('SimulationEpoch') == cycle['TargetEpoch'] and row.get('StepId') == step for row in value['proposals']))
        if not valid or original.get('CommandId') in effective_ids:
            issues.append('缺少准确恢复拒绝/整步退役/新页面独立决策证明，或原命令仍在有效链')
        else:
            retired_ids.add(original['CommandId'])
    applied_local = {row['Command']['CommandId'] for row in value['local'] if (row.get('Result') or {}).get('AppliedLocally') and
        ((row.get('Result') or {}).get('CommandResult') or {}).get('Succeeded')}
    unresolved = [key for key in proposals if key not in retired_ids and (key not in effective_ids or key not in applied_local)]
    if any(not ((row.get('Result') or {}).get('CommandResult') or {}).get('Succeeded') for row in value['local']):
        issues.append('原准确回执存在真实拒绝')
    return dict(Valid=not issues and not unresolved, Issues=issues, RetiredCommandIds=sorted(retired_ids), Unresolved=unresolved)

results, global_rows, states, boundaries, recovery_data = {}, {}, {}, {}, {}
for role in roles:
    folder = root / role
    report = read(folder / '验证结果.json')
    identity = read(folder / '联机运行身份.json') if (folder / '联机运行身份.json').exists() else None
    applied = [row['Applied'] for row in lines(folder / '四端全局应用回执.jsonl')]
    local = lines(folder / '命令与准确回执.jsonl')
    proposals = lines(folder / '本机原控件提案.jsonl')
    global_rows[role] = applied
    recovery_data[role] = dict(applied=applied, proposals=proposals, local=local,
        completed=lines(folder / '联机正式恢复完成.jsonl'), prefixes=lines(folder / '四端确认前缀.jsonl'),
        events=lines(folder / '联机正式恢复事件.jsonl'), retirements=lines(folder / '恢复退役原命令.jsonl'),
        observations=lines(folder / '原命令准确回执观察.jsonl'), steps=lines(folder / '驱动步骤恢复退役.jsonl'),
        reentries=lines(folder / '恢复后独立决策.jsonl'))
    if (folder / '联机正式终局规则状态.json').exists():
        states[role] = read(folder / '联机正式终局规则状态.json')
    if (folder / '联机终局确认边界.json').exists():
        boundaries[role] = read(folder / '联机终局确认边界.json')
    completed = (report.get('Campaign') or {}).get('Completed', [])
    applied_local = [row for row in local if (row.get('Result') or {}).get('AppliedLocally') and ((row.get('Result') or {}).get('CommandResult') or {}).get('Succeeded')]
    confirmed_ids = {row['Command']['CommandId'] for row in applied_local}
    unresolved = [row['Command']['CommandId'] for row in proposals if row['Command']['CommandId'] not in confirmed_ids]
    results[role] = dict(
        通过=report['Passed'], 整局完成=report.get('FullGameCompleted', False), 阶段=report['Stage'], 错误=report.get('Error'),
        身份=identity, 本机提案数=len(proposals), 准确回执数=len(local), 全局回执数=len(applied),
        本机命令全部真实应用=bool(local) and len(local) == len(proposals) and len(applied_local) == len(local) and not unresolved,
        本机成功应用回执数=len(applied_local), 未收到本机成功应用回执的提案=unresolved,
        本机命令无重复=len({row['Command']['CommandId'] for row in local}) == len(local),
        Unity错误数=len(report.get('RuntimeErrors') or []), 预期退出码=report['ExitCode'], 非阻断UI失败=report.get('NonfatalUiFailures', []), 截图数=len(list(folder.glob('*.png'))), 实际覆盖=completed,
        影响力决策数=len(lines(folder / '影响力决策.jsonl')),
        特殊行动提案=[row['Command'] for row in proposals if row['Command']['Kind'] == 11],
        中断与恢复=recovery_summary(folder, report),
    )

reference = global_rows['host']
same_receipts = all(canonical(global_rows[role]) == canonical(reference) for role in roles)
common_count = min(map(len, global_rows.values()))
common_receipts_same = bool(common_count) and all(canonical(global_rows[role][:common_count]) == canonical(reference[:common_count]) for role in roles)
sequences = [row['GlobalCommandSequence'] for row in reference]
contiguous = bool(sequences) and sequences == list(range(1, len(sequences) + 1))
same_state = len(states) == 4 and len({canonical(value) for value in states.values()}) == 1
same_boundary = len(boundaries) == 4 and len({canonical(value) for value in boundaries.values()}) == 1
exits = read(root / '实际退出记录.json') if (root / '实际退出记录.json').exists() else {}
normal_exits = len(exits) == 4 and all(value['ExitCode'] == results[role]['预期退出码'] and value.get('NaturalExit', True) for role, value in exits.items())
passed = all(value['通过'] and value['整局完成'] and value['本机命令全部真实应用'] and value['本机命令无重复'] and value['Unity错误数'] == 0 and not value['中断与恢复']['未完成的正式恢复'] for value in results.values())
passed = passed and same_receipts and contiguous and same_state and same_boundary and normal_exits
chain = confirmed_recovery_chains(recovery_data)
if chain['Used']:
    for role in roles:
        results[role]['原始尝试与正式恢复退役'] = local_recovery_attempts(recovery_data[role], chain, role)
        results[role]['本机最终有效尝试全部真实应用'] = results[role]['原始尝试与正式恢复退役']['Valid']
    passed = (not chain['Issues'] and chain['FullEffectivePayloadsEqual'] and chain['EffectiveSequenceContiguous'] and
        same_state and same_boundary and normal_exits and all(value['通过'] and value['整局完成'] and value['本机命令无重复'] and value['Unity错误数'] == 0 and
            not value['中断与恢复']['未完成的正式恢复'] and value['原始尝试与正式恢复退役']['Valid'] for value in results.values()))
summary = dict(
    本局通过=passed, 四端=results, 四端全局回执一致=same_receipts, 全局序列连续=contiguous,
    共同完整回执数=common_count, 共同前缀含EventId严格一致=common_receipts_same,
    正式恢复确认链=dict(Used=chain['Used'], Issues=chain['Issues'], Cycles=chain['Cycles'],
        FullEffectivePayloadsEqualIncludingEventId=chain['FullEffectivePayloadsEqual'], EffectiveSequenceContiguous=chain['EffectiveSequenceContiguous'],
        EffectiveCommandCount=len(chain['Effective']['host']), RetiredRawReferences=chain['RetiredRaw']),
    最终有效完整回执SHA256={role: hashlib.sha256(canonical(value).encode()).hexdigest() for role, value in chain['Effective'].items()},
    全局命令数=len(reference), 四端正式终局状态一致=same_state, 四端终局确认边界一致=same_boundary,
    终局状态SHA256={role: hashlib.sha256(canonical(state).encode()).hexdigest() for role, state in states.items()},
    四端按报告自行退出=normal_exits, 实际退出=exits,
    正常流程边界='四个可见独立程序，正常菜单建房和加入、原控件指针事件及真实Raycast；无预设对局、无牌或资源注入、无随机替换、无直接命令提交',
    采集策略='维持不主动支付路费',
    恢复结论边界='原始中断、正式恢复安装通知、确认前缀及消费者就绪、最终致命失败分别记录；物理回连或正式恢复完成不等于整局通过。保留全部原回执比较，不删除退役尾段以制造一致。',
)
(root / '四端整局验收汇总.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({key: value for key, value in summary.items() if key not in ['四端', '实际退出', '终局状态SHA256']}, ensure_ascii=False, indent=2))
