"""从四端自然完成的隔离短局提取原自动档，准备同版本的正常菜单续局验证。"""
import argparse
import hashlib
import json
import os
from pathlib import Path


project = Path(__file__).resolve().parents[2]
allowed = (project / "TestResults").resolve()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--source", type=Path, required=True)
parser.add_argument("--output", type=Path, required=True)
parser.add_argument("--source-first-failure", type=Path,
                    help="明确指出短局首次失败记录；只从失败前自然生成的原自动档独立续局，不改判原局。")
args = parser.parse_args()
source = args.source.resolve(strict=True)
output = args.output.resolve()
if any(path == allowed or not path.is_relative_to(allowed) for path in (source, output)):
    raise ValueError("来源和输出必须是 TestResults 内的明确子目录。")
if output.exists() or output.is_relative_to(source):
    raise ValueError("续局输出必须是全新的独立目录。")
for path in (source, output.parent):
    for parent in (path, *path.parents):
        if parent == allowed:
            break
        if parent.exists() and (parent.is_symlink() or (hasattr(os.path, "isjunction") and os.path.isjunction(parent))):
            raise ValueError("夹具路径不能经过重解析点。")


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


roles = ("host", "client2", "client3", "client4")
cohort = source / "四端正常联机"
exits = read(cohort / "实际退出记录.json")
if any(not exits[role]["NaturalExit"] for role in roles):
    raise ValueError("短局未由四端自然退出，保留原现场。")
reports = {role: read(cohort / role / "验证结果.json") for role in roles}
source_failure = None
if args.source_first_failure:
    source_failure = args.source_first_failure.resolve(strict=True)
    if source_failure != (cohort / "首个实例失败.json").resolve(strict=True):
        raise ValueError("首败记录必须是该短局的原件。")
    failure = read(source_failure)
    if failure.get("Role") not in roles or not failure.get("Error") or not reports[failure["Role"]].get("DriverContinuationPending"):
        raise ValueError("首败不是已记录的夹具操作中断，不从中推测产品结果。")
else:
    if any(exits[role]["ExitCode"] != 0 or not reports[role].get("PlannedShortRun") or
           not reports[role].get("ShortRunCompleted") for role in roles):
        raise ValueError("短局没有通过计划停止点；如有明确的夹具中断，请指定原首败记录。")

save_path = source / "联机隔离存档" / "host" / "auto-1.json"
if source_failure and save_path.stat().st_mtime > source_failure.stat().st_mtime:
    raise ValueError("自动档晚于首败记录，不能冒充故障前原件。")
save_bytes = save_path.read_bytes()
save = json.loads(json.loads(save_bytes.decode("utf-8-sig"))["Payload"])
snapshot = json.loads(save["ArchiveJson"])["Snapshot"]
state = snapshot["State"]
baseline = json.loads(save["SimulationCheckpointJson"])["Baseline"]
game_id, content_hash = state["GameId"], state["ContentPackHash"]
if (not save["LocalTestNetwork"] or save["GameId"] != game_id or save["ContentHash"] != content_hash or
        baseline["GameId"] != game_id or baseline["ContentPackHash"] != content_hash or
        state["RuleConfiguration"]["RuleModeId"] != "standard" or len(state["Players"]) != 4):
    raise ValueError("原自动档的身份、内容或四人标准模式不符。")
confirmed = {item["Outcome"]["CommandId"] for item in baseline["ConfirmedCommands"]}
launches = {item["Role"]: item for item in read(cohort / "四实例启动记录.json")}
seats = {item["PlayerId"]: item for item in save["Seats"]}
identities, plans, sources = {}, [], []
for role in roles:
    identity = read(cohort / role / "联机运行身份.json")
    if identity["GameId"] != game_id or identity["ContentPackHash"] != content_hash:
        raise ValueError("原四端身份不一致：" + role)
    player = identity["LocalPlayer"]
    arguments = launches[role]["Arguments"]
    names = [s.split("=", 1)[1] for s in arguments if s.startswith("--yc-local-player-name=")]
    operators = [s.split("=", 1)[1] for s in arguments if s.startswith("--yc-test-operator-id=")]
    if len(names) != 1 or len(operators) != 1 or seats[player]["OperatorId"] != operators[0] or seats[player]["PlayerName"] != names[0]:
        raise ValueError("原自动档席位与测试进程身份不一致：" + role)
    log_path = cohort / role / "逐玩家行动计划.jsonl"
    rows = [json.loads(line) for line in log_path.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    rows = [row for row in rows if row["GameId"] == game_id and row["CommandId"] in confirmed and row["Progress"]["PlayerId"] == player]
    if not rows:
        raise ValueError("原自动档确认前缀中没有该玩家的行动记录：" + role)
    last = rows[-1]
    plans.append(last["Progress"])
    identities[role] = {"playerId": player, "name": names[0], "operatorId": operators[0]}
    sources.append({"role": role, "commandId": last["CommandId"], "round": last["Round"], "log": str(log_path)})
if {plan["PlayerId"] for plan in plans} != {1, 2, 3, 4}:
    raise ValueError("原行动记录的玩家集合不完整。")

output.mkdir(parents=True)
copy = output / "原自动存档副本.json"
with copy.open("xb") as stream:
    stream.write(save_bytes)
digest = hashlib.sha256(save_bytes).hexdigest()
if hashlib.sha256(save_path.read_bytes()).hexdigest() != digest:
    raise ValueError("复制期间原自动档发生变化，保留副本并停止。")
policy = output / "原行动策略.json"
with policy.open("x", encoding="utf-8") as stream:
    json.dump({"GameId": game_id, "ContentHash": content_hash, "Players": plans}, stream, ensure_ascii=False, indent=2)
metadata = {"gameId": game_id, "contentHash": content_hash, "ruleMode": "standard", "round": state["Round"],
            "stateRevision": snapshot["StateRevision"], "confirmedSequence": baseline["ConfirmedGlobalSequence"],
            "saveCopy": str(copy), "saveSha256": digest, "policy": str(policy), "policySources": sources,
            "roles": identities, "sourceFailed": source_failure is not None,
            "sourceFailure": str(source_failure) if source_failure else None,
            "scope": "自然短局原自动档独立读档验证；不改判原局，不修改存档，不计作完整局或真实 Steam。"}
with (output / "续局夹具来源.json").open("x", encoding="utf-8") as stream:
    json.dump(metadata, stream, ensure_ascii=False, indent=2)
print("续局原档准备完成：", game_id, "回合", state["Round"], "修订", snapshot["StateRevision"])
