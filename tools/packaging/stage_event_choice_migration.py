#!/usr/bin/env python3
"""将已核对的旧事件数据转换到独立审查目录；不读取 UI 原档案，也不覆盖源包。"""
import argparse
import hashlib
import json
from pathlib import Path


RESOURCES = {
    "originium": "Originium", "originiumShard": "OriginiumShard", "iron": "Iron",
    "pureOriginium": "PureOriginium", "goldVoucher": "GoldVoucher",
}
RESOURCE_ENUM = list(RESOURCES.values())


def lua_string(value):
    return "'" + value.replace("\\", "\\\\").replace("'", "\\'").replace("\r", "\\r").replace("\n", "\\n") + "'"


def enter(path, relative):
    if not relative or "\\" in relative or any(part in ("", ".", "..") for part in relative.split("/")):
        raise ValueError("非法相对路径：" + relative)
    child = (path / relative).resolve()
    if not child.is_relative_to(path):
        raise ValueError("路径越过边界：" + relative)
    return child


def enterprise_branch(card_id, index):
    if card_id == "enterprise.event.collection_platform" and index == 0:
        return ["    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.LoseScore({ player = ctx.playerId, amount = 1 }) },",
                "        rightEffects = { Flow.UpgradeThenActivateEnterpriseSpecial({ apiVersion = 1, player = ctx.playerId, levels = 1 }) } }),",
                "        { interaction = { prompt = Text.Ref('core:condition.enterprise.event.collection_platform.resolve') } }) }"]
    if card_id == "enterprise.event.cave_ruins" and index == 2:
        return ["    local candidates = {}", "    for _, item in ipairs(Global.Content.FindInstances()) do",
                "        if item.contentType == 'enterprise' then table.insert(candidates, item.id) end", "    end",
                "    local right = { Effect.UpgradeEnterprise({ apiVersion = 1, player = ctx.playerId, enterpriseItemIds = candidates, levels = 1 }) }",
                "    return { Effect.WithPresentation(Effect.Condition({ leftEffects = { Effect.PayResource({ recipient = ctx.playerId, resourceType = 'Iron', amount = 1 }) },",
                "        rightEffects = right }), { interaction = { prompt = Text.Ref('core:condition.enterprise.event.cave_ruins.resolve') } }) }"]
    return None


def preselection(card_id, choice_id, reward, pending):
    if len(pending) != 1 or pending[0]["kind"] != 3 or pending[0]["amount"] != 1 or pending[0]["costAmount"] != 0:
        raise ValueError("影响力预选与基线不一致：" + card_id)
    scope = pending[0]["targetScope"]
    if (card_id, scope) not in {("event_red_01", 3), ("event_yellow_05", 4)}:
        raise ValueError("未核对的影响力预选：" + card_id)
    handler = card_id + "." + choice_id + ".completed"
    body = ["return function(ctx)", "    local origin, slots = ctx.payload.targetLocationId, {}", "    local map = Global.Map.GetMap()"]
    if scope == 3:
        body.extend(["    for _, point in ipairs(map.locations) do", "        if point.locationId == origin then",
                     "            for _, slot in ipairs(point.influenceSlotIds) do table.insert(slots, slot) end", "        end", "    end"])
    body.extend(["    for _, route in ipairs(map.routes) do", "        if not route.hasRoad then", "            local adjacent = false",
                 "            for _, location in ipairs(route.coveredLocationIds) do if location == origin then adjacent = true end end",
                 "            if adjacent then for _, slot in ipairs(route.influenceSlotIds) do table.insert(slots, slot) end end",
                 "        end", "    end", "    table.sort(slots)",
                 "    return { Effect.Choice({ player = ctx.playerId, options = slots, minSelections = 1, maxSelections = 1,",
                 "        interactionType = 'effect.influence.place.target', resultContext = { origin = origin }, completionHandlerId = " + lua_string(handler) + " }) }", "end", ""])
    continuation = ["return function(ctx)", "    if ctx.payload.outcome ~= 'completed' then return {} end",
                    "    local target = ctx.payload.normalizedResult.value", "    local effects = {}"]
    for key, resource in RESOURCES.items():
        amount = reward[key]
        if amount:
            continuation.append("    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = " + lua_string(resource) +
                                ", amount = " + str(amount) + ", reasonId = " + lua_string("event_card.reward:" + card_id) + " }))")
    continuation.extend(["    table.insert(effects, Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = " +
                         lua_string("event_card_pending:" + card_id) + ", ownerSubject = ctx.playerId, targetSlotId = target, causeKind = 'other_intrinsic_flow' }))",
                         "    return effects", "end", ""])
    return "\n".join(body), "\n".join(continuation), handler


def script(card_id, index, reward, pending):
    branch = enterprise_branch(card_id, index)
    if branch is not None:
        if any(reward.values()) or pending:
            raise ValueError("企业特殊分支与核对基线不符：" + card_id)
        return "\n".join(["return function(ctx)"] + branch + ["end", ""])
    lines = ["return function(ctx)", "    local effects = {}"]
    for key, resource in RESOURCES.items():
        amount = reward[key]
        if not isinstance(amount, int) or amount < 0:
            raise ValueError("资源奖励无效：" + card_id)
        if amount:
            reason = "" if card_id.startswith("enterprise.event.") else ", reasonId = " + lua_string("event_card.reward:" + card_id)
            lines.append("    table.insert(effects, Effect.GainResource({ recipient = ctx.playerId, resourceType = " + lua_string(resource) +
                         ", amount = " + str(amount) + reason + " }))")
    for effect in pending:
        kind, amount, target = effect["kind"], effect["amount"], effect["targetScope"]
        if kind == 1 and target == 1:
            lines.append("    table.insert(effects, Effect.GainScore({ recipient = ctx.playerId, amount = " + str(amount) +
                         ", reasonId = " + lua_string("event_card.score:" + card_id) + " }))")
        elif kind == 2 and target in (1, 2):
            resource = RESOURCE_ENUM[effect["resourceType"]]
            reason = "event_card.opponent_reward:" if target == 2 else "event_card.pending_reward:"
            gain = "Effect.GainResource({ recipient = " + ("opponent" if target == 2 else "ctx.playerId") + ", resourceType = " + lua_string(resource) + ", amount = " + str(amount) + ", reasonId = " + lua_string(reason + card_id) + " })"
            if target == 2:
                lines.extend(["    for _, opponent in ipairs(GameData.GetPlayers()) do", "        if opponent.playerId ~= ctx.playerId then table.insert(effects, " + gain + ") end", "    end"])
            else:
                lines.append("    table.insert(effects, " + gain + ")")
        elif kind == 3 and target == 0 and effect["costAmount"] == 4 and effect["costResourceType"] == 4 and amount == 1:
            lines.extend(["    table.insert(effects, Effect.WithPresentation(Effect.Condition({",
                          "        leftEffects = { Effect.PayResource({ player = ctx.playerId, cost = { Originium = 0, OriginiumShard = 0, Iron = 0, PureOriginium = 0, GoldVoucher = 4 } }) },",
                          "        rightEffects = { Effect.PlaceInfluence({ executingPlayer = ctx.playerId, influenceSource = " + lua_string("event_card_pending:" + card_id) + ", ownerSubject = ctx.playerId, causeKind = 'other_intrinsic_flow' }) }",
                          "    }), { interaction = { prompt = Text.Ref('core:condition.event.event_yellow_04.payment') } }))"])
        else:
            raise ValueError("待迁移效果需要人工处理：" + card_id + "/" + str(index))
    return "\n".join(lines + ["    return effects", "end", ""])


def main():
    parser = argparse.ArgumentParser(description="按冻结映射生成事件迁移审查草稿，不改写正式包。")
    parser.add_argument("--input", required=True, help="旧内容包目录")
    parser.add_argument("--mapping", required=True, help="逐牌逐选项核对映射")
    parser.add_argument("--output", required=True, help="独立、尚不存在的审查目录")
    args = parser.parse_args()
    root, target = Path(args.input).resolve(strict=True), Path(args.output).resolve()
    if target.exists() or target == root or target.is_relative_to(root) or root.is_relative_to(target):
        raise ValueError("输出目录必须不存在，且必须与旧包互不包含。")
    manifest = json.loads(Path(args.mapping).read_text(encoding="utf-8"))
    if len(manifest["definitions"]) != 24 or sum(len(d["choices"]) for d in manifest["definitions"]) != 55:
        raise ValueError("映射不符合核对基线。")
    output, choice_count = {}, 0
    for record in manifest["definitions"]:
        card_id = record["definitionId"]
        original = enter(root, record["source"]).read_bytes()
        if hashlib.sha256(original).hexdigest() != record["sourceSha256"]:
            raise ValueError("源定义已变化，请重新核对：" + card_id)
        definition = json.loads(original.decode("utf-8-sig")); data = definition["data"]
        if definition["definitionId"] != card_id or definition.get("artworkSpriteId") != record["artworkSpriteId"]:
            raise ValueError("定义或图片切片身份不一致：" + card_id)
        choices = []
        for choice in record["choices"]:
            index = choice["legacyIndex"]
            if (data["choiceDescriptions"][index] != choice["description"] or data["choiceRewards"][index] != choice["legacyReward"] or
                    data["choicePendingEffects"][index] != choice["legacyPendingEffects"]):
                raise ValueError("旧分支与核对映射不一致：" + card_id + "/" + choice["choiceId"])
            path = choice["script"]
            if not path.startswith("lua/events/choices/") or not path.endswith(".lua") or path in output:
                raise ValueError("脚本路径无效或重复：" + path)
            enter(target, path)
            entry = {"choiceId": choice["choiceId"], "description": choice["description"], "button": choice["button"], "script": path, "version": "1.0.0"}
            pending = choice["legacyPendingEffects"]
            if pending and pending[0]["kind"] == 3 and pending[0]["costAmount"] == 0:
                body, continuation, handler = preselection(card_id, choice["choiceId"], choice["legacyReward"], pending)
                completed_path = path[:-4] + ".completed.lua"
                if completed_path in output: raise ValueError("完成脚本路径重复：" + completed_path)
                output[path] = body; output[completed_path] = continuation
                entry["completionHandlerId"] = handler
                definition.setdefault("abilityHandlers", []).append({"handlerId": handler, "script": completed_path, "version": "1.0.0"})
            else:
                output[path] = script(card_id, index, choice["legacyReward"], pending)
            choices.append(entry); choice_count += 1
        for key in ("cardId", "name", "choiceDescriptions", "choiceRewards", "choicePendingEffects", "effectScript"):
            data.pop(key, None)
        data["eventChoiceSchemaVersion"] = 1; data["choices"] = choices
        definition["version"] = "2.0.0"
        definition["requiredCapabilities"] = sorted(set(definition.get("requiredCapabilities", [])) | {"event.choices.v1", "event.artwork_buttons.v1"})
        if record["legacyEffectScript"]:
            if card_id not in {"enterprise.event.collection_platform", "enterprise.event.cave_ruins"}:
                raise ValueError("未核对的整牌脚本：" + card_id)
            legacy_script = enter(root, record["legacyEffectScript"]).read_bytes()
            if hashlib.sha256(legacy_script).hexdigest() != record["legacyEffectScriptSha256"]:
                raise ValueError("整牌脚本已变化，请重新核对：" + card_id)
            definition["abilities"] = []
        output[record["source"]] = json.dumps(definition, ensure_ascii=False, indent=2) + "\n"
    report = {"scope": "仅供审查，未启用，卡面区域尚未视觉核验", "convertedDefinitions": len(manifest["definitions"]),
              "convertedChoices": choice_count, "skipped": []}
    output["migration-report.json"] = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    for relative, content in output.items():
        path = enter(target, relative); path.parent.mkdir(parents=True, exist_ok=True); path.write_text(content, encoding="utf-8", newline="\n")
    print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
