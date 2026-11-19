"""API-01：当前工作区的可追踪静态审计；不执行Lua、Unity或历史工具。"""
from __future__ import annotations

import bisect
import collections
import hashlib
import json
import os
import re
import subprocess
from functools import lru_cache
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CORE = ROOT / "Assets/StreamingAssets/Content/core"
REVIEW = Path(__file__).with_name("lua_api_upgrade_review.json")
CATEGORIES = ("有效执行入口", "仅拒绝", "负例", "失效正向夹具", "历史材料")


def digest(value):
    if isinstance(value, str):
        value = value.encode("utf-8")
    return hashlib.sha256(value).hexdigest()


def read(path):
    path = path.resolve()
    if not path.is_relative_to(ROOT) or path.is_relative_to(ROOT / "游城拓荒/UI素材"):
        raise ValueError("审计路径越界")
    return path.read_text(encoding="utf-8-sig")


def masked_cs(text):
    """保留偏移和换行，屏蔽注释/字符/普通及逐字字符串中的括号。"""
    pattern = r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\''
    return re.sub(pattern, lambda m: "".join("\n" if c == "\n" else " " for c in m[0]), text)


def closing(text, start, left="{", right="}"):
    depth = 0
    for i in range(start, len(text)):
        if text[i] == left:
            depth += 1
        elif text[i] == right:
            depth -= 1
            if depth == 0:
                return i + 1
    raise ValueError("未闭合的源码结构")


def scopes(text):
    masked = masked_cs(text)
    # 支持仓库中[Test] public void ...同一行，以及多行TestCase属性。
    masked = re.sub(r"\[(?:[^\[\]]|\[[^\]]*\])*\]", lambda m: "".join("\n" if c == "\n" else " " for c in m[0]), masked)
    result = []
    pattern = r"(?m)^[ \t]*(?:(?:public|private|protected|internal|static|async|virtual|override|sealed|partial|new|unsafe)\s+)+[^\n;={}]*?\b(\w+)(?:<[^>\n]+>)?\s*\("
    for match in re.finditer(pattern, masked):
        end_args = closing(masked, masked.find("(", match.start()), "(", ")")
        after = re.match(r"\s*(?:where[^{;=>]+)?\s*(=>|\{)", masked[end_args:])
        if not after:
            continue
        body = end_args + after.end() - len(after[1])
        end = closing(masked, body) if after[1] == "{" else masked.find(";", body) + 1
        start = match.start()
        # TestCase属性中的Lua属于随后的用例，而不是前一方法。
        prefix = text[:start]
        attributes = re.search(r"(?s)(?:\s*\[(?:[^\[\]]|\[[^\]]*\])*\])+\s*$", prefix)
        if attributes:
            start = attributes.start()
        result.append({"name": match[1], "start": start, "end": end, "text": text[start:end]})
    return result


@lru_cache(maxsize=None)
def class_ranges(text):
    masked = masked_cs(text)
    found = []
    for match in re.finditer(r"\b(?:class|struct)\s+(\w+)[^{;]*\{", masked):
        start = masked.find("{", match.start())
        found.append((start, closing(masked, start), match[1]))
    return found


def class_at(text, offset):
    found = [name for start, end, name in class_ranges(text) if start <= offset < end]
    return found[-1] if found else ""


def constants(texts):
    expressions = {}
    locations = {}
    for path, text in texts.items():
        if not path.endswith(".cs") or "/Tests/" in path:
            continue
        for match in re.finditer(r"\bconst\s+string\s+([^;]+);", text):
            owner = class_at(text, match.start())
            for part in re.split(r',(?=(?:[^"]*"[^"]*")*[^"]*$)', match[1]):
                value = re.fullmatch(r"\s*(\w+)\s*=\s*(.+)\s*", part)
                if value:
                    key = owner + "." + value[1]
                    expressions[key] = (owner, value[2].strip())
                    locations[key] = {"path": path, "line": text.count("\n", 0, match.start()) + 1}
    resolved = {}
    for _ in range(12):
        before = len(resolved)
        for key, (owner, expression) in expressions.items():
            parts = re.split(r"\s*\+\s*", expression)
            values = []
            for part in parts:
                if re.fullmatch(r'"[^"\\]*"', part):
                    values.append(part[1:-1])
                else:
                    symbol = part.removeprefix("YC.Domain.Effects.").removeprefix("YC.Domain.Cards.")
                    symbol = symbol if "." in symbol else owner + "." + symbol
                    if symbol not in resolved:
                        break
                    values.append(resolved[symbol])
            else:
                resolved[key] = "".join(values)
        if len(resolved) == before:
            break
    return resolved, locations


def public_catalog(texts, values, locations):
    path = "Assets/YC/Infrastructure/Lua/MoonSharpLuaRuntimeHost.cs"
    host = texts[path]
    section = host.split("private static Dictionary<string, string> CreateRegisteredEffectConstructors()", 1)[1].split("return registered;", 1)[0]
    entries = []
    for match in re.finditer(r'\{\s*"(\w+)",\s*([\w.]+)\s*\}', section):
        symbol = match[2]
        if "." not in symbol:
            symbol = "MoonSharpLuaRuntimeHost." + symbol
        symbol = re.sub(r"^YC\.Domain\.\w+\.", "", symbol)
        entries.append({"namespace": "Effect", "name": match[1], "symbol": symbol, "typeId": values.get(symbol),
                        "exposure": {"path": path, "line": host.count("\n", 0, host.index(match[0], host.index("CreateRegisteredEffectConstructors()\n"))) + 1}})
    for relative, owner in [("Domain/Cards/CharacterContentEffectExecutor.cs", "CharacterContentEffectExecutor"),
                            ("Domain/Effects/ContentTransactionEffectExecutor.cs", "ContentTransactionEffectExecutor")]:
        source_path = "Assets/YC/" + relative
        source = texts[source_path]
        table = source.split("Constructors =", 1)[1].split("};", 1)[0]
        for match in re.finditer(r'\{\s*"(\w+)",\s*"(\w+)"\s*\}', table):
            entries.append({"namespace": "Effect", "name": match[1], "symbol": owner + ".Constructors",
                            "typeId": values[owner + ".Prefix"] + match[2],
                            "exposure": {"path": source_path, "line": source.count("\n", 0, source.index(match[0])) + 1}})
    for name in ["WithPresentation", "WithContext"]:
        entries.append({"namespace": "Effect", "name": name, "typeId": None, "kind": "元数据包装，非独立节点",
                        "exposure": {"path": path, "line": host.count("\n", 0, host.index("public static IReadOnlyCollection<string> SupportedEffectConstructors")) + 1}})
    # Flow回调为单行或多行，后两项由明确的字典循环安装。
    for match in re.finditer(r'flow\.Set\("(\w+)"[\s\S]*?return CreateEffectSpec\(script,\s*([\w.]+),\s*args\)', host):
        symbol = re.sub(r"^YC\.Domain\.\w+\.", "", match[2])
        entries.append({"namespace": "Flow", "name": match[1], "symbol": symbol, "typeId": values.get(symbol),
                        "exposure": {"path": path, "line": host.count("\n", 0, match.start()) + 1}})
    for name, symbol in [("ClearRouteAndPlaceRoad", "ContentPrimitiveEffectExecutor.ClearRouteRoad"),
                         ("AcquireReward", "RewardAcquisitionFlow.TypeId")]:
        entries.append({"namespace": "Flow", "name": name, "symbol": symbol, "typeId": values.get(symbol),
                        "exposure": {"path": path, "line": host.count("\n", 0, host.index('"'+name+'"', host.index('flow.Set'))) + 1}})
    for entry in entries:
        if entry.get("symbol") in locations:
            entry["typeDeclaration"] = locations[entry["symbol"]]
    return entries


def content_bindings(texts):
    manifest = json.loads(texts["Assets/StreamingAssets/Content/core/pack.json"])
    direct = collections.defaultdict(list)
    module_map = {m["moduleId"]: m for m in manifest.get("luaModules", [])}
    def visit(value, pointer, definition, path):
        if isinstance(value, dict):
            for key, child in value.items():
                visit(child, pointer + "/" + key, definition, path)
        elif isinstance(value, list):
            for i, child in enumerate(value):
                visit(child, pointer + "/" + str(i), definition, path)
        elif isinstance(value, str) and value.endswith(".lua"):
            direct[value].append({"definition": definition.get("definitionId", "pack"),
                "enabled": definition.get("enabled", True), "expansionId": definition.get("expansionId", "core"),
                "modes": definition.get("applicableModes", []), "path": path, "jsonPointer": pointer})
    definitions = []
    for relative in manifest["definitions"]:
        path = "Assets/StreamingAssets/Content/core/" + relative
        definition = json.loads(texts[path])
        definitions.append((path, definition))
    # 对齐ExternalContentPack.ReadDefinitionDocuments，包含独立设施和清单展开实例。
    inventory_path = "Assets/StreamingAssets/Content/core/" + manifest["facilityInventory"]
    inventory = json.loads(texts[inventory_path])
    for relative in inventory.get("definitionFiles", []):
        path = "Assets/StreamingAssets/Content/core/" + relative
        definitions.append((path, json.loads(texts[path])))
    for group_index, group in enumerate(inventory["groups"]):
        for variant in group["variants"]:
            for i in range(variant["count"]):
                definition = json.loads(json.dumps(group["definition"]))
                definition["definitionId"] = variant.get("instances", [])[i]["id"] if i < len(variant.get("instances", [])) else group["idPrefix"] + "_" + variant["color"] + "_" + str(i + 1).zfill(3)
                definitions.append((inventory_path + "#/groups/" + str(group_index) + ":" + definition["definitionId"], definition))
    for path, definition in definitions:
        visit(definition, "", definition, path)
        if definition.get("dataTemplate"):
            template_path = "Assets/StreamingAssets/Content/core/" + definition["dataTemplate"]
            template = json.loads(texts[template_path])
            data = merge_data(template["data"], definition.get("data", {}))
            visit(data, "/dataTemplate/data", definition, template_path)
    visit({k: v for k, v in manifest.items() if k not in {"luaModules", "scriptDependencies", "definitions"}}, "", {}, "Assets/StreamingAssets/Content/core/pack.json")
    defaults_path = "Assets/StreamingAssets/Content/core/" + manifest["presentation"]["defaults"]
    visit(json.loads(texts[defaults_path]), "", {}, defaults_path)
    for module_id, module in module_map.items():
        for dependency in manifest.get("scriptDependencies", []):
            if any(d["moduleId"] == module_id for d in dependency["modules"]):
                for binding in direct[dependency["script"]]:
                    direct[module["script"]].append({**binding, "via": dependency["script"],
                        "moduleId": module_id, "moduleVersion": module["version"], "moduleSha256": module["sha256"]})
    return direct, manifest


def merge_data(basis, override):
    """与模板装配一致：递归合并对象，数组整体替换。"""
    result = json.loads(json.dumps(basis))
    for key, value in override.items():
        result[key] = merge_data(result[key], value) if isinstance(result.get(key), dict) and isinstance(value, dict) else value
    return result


def resolve_symbol(expression, owner, values):
    expression = expression.strip()
    if re.fullmatch(r'"[^"\\]*"', expression):
        return expression[1:-1]
    expression = re.sub(r"^YC\.(?:Domain|Infrastructure)\.\w+\.", "", expression)
    return values.get(expression if "." in expression else owner + "." + expression)


def type_inventory(texts, values, catalog):
    """编译白名单和静态注册证据；注册存在不等同执行成功。"""
    path = "Assets/YC/Infrastructure/Lua/LuaEffectSpecCompiler.cs"
    source = texts[path]
    constructor = next(s for s in scopes(source) if s["name"] == "LuaEffectTypeCatalog")
    compiled = collections.defaultdict(list)
    def add(expression, offset):
        value = resolve_symbol(expression, "LuaEffectTypeCatalog", values)
        if value is None:
            raise ValueError("未解析的编译类型：" + expression)
        compiled[value].append({"path": path, "line": source.count("\n", 0, offset) + 1, "expression": expression.strip()})
    for m in re.finditer(r"new LuaEffectTypeRegistration\(\s*([^,\n]+)", constructor["text"]):
        if m[1].strip() != "typeId":
            add(m[1], constructor["start"] + m.start())
    array = re.search(r"string\[\] genericTypes\s*=\s*\{([\s\S]*?)\};", source)
    for m in re.finditer(r"[^,\s]+", array[1]):
        add(m[0], array.start(1) + m.start())
    for entry in catalog:
        if entry.get("symbol", "").endswith(".Constructors"):
            compiled[entry["typeId"]].append({**entry["exposure"], "expression": entry["symbol"] + "前缀循环"})
    registrations = collections.defaultdict(list)
    families = []
    for p, t in texts.items():
        if not p.endswith(".cs") or "/Tests/" in p:
            continue
        for m in re.finditer(r"(?:new EffectRegistration\(|\b(?:registry|effectRegistry)\.Register\((?!\s*new\b))\s*([^,\n]+)", t):
            expression = m[1].strip()
            value = resolve_symbol(expression, class_at(t, m.start()), values)
            if value is not None:
                registrations[value].append({"path": p, "line": t.count("\n", 0, m.start()) + 1, "expression": expression})
        # 类型数组/构造器后缀循环的注册不可只靠第一实参字面量检索。
        for m in re.finditer(r"string\[\]\s+(effectTypeIds)\s*=\s*\{([\s\S]*?)\};", t):
            for expression in m[2].split(","):
                value = resolve_symbol(expression, class_at(t, m.start()), values)
                if value:
                    registrations[value].append({"path": p, "line": t.count("\n", 0, m.start()) + 1, "expression": "注册循环：" + expression.strip()})
        for s in scopes(t):
            if s["name"].startswith("Register") or s["name"] == "EnsureMainlineRegistration":
                for m in re.finditer(r"\bRegister(?:Once|IfMissing)?\(registry,\s*(?:executor,\s*)?([\w.]+)", s["text"]):
                    value = resolve_symbol(m[1], class_at(t, s["start"]), values)
                    if value:
                        registrations[value].append({"path": p, "line": t.count("\n", 0, s["start"] + m.start()) + 1, "expression": "注册辅助函数：" + m[1]})
                for m in re.finditer(r"foreach\s*\(string\s+(?:type|typeId)\s+in\s+new\[\]\s*\{([^}]+)\}", s["text"]):
                    for expression in m[1].split(","):
                        value = resolve_symbol(expression, class_at(t, s["start"]), values)
                        if value:
                            registrations[value].append({"path": p, "line": t.count("\n", 0, s["start"] + m.start()) + 1, "expression": "注册foreach：" + expression.strip()})
                calls = re.findall(r"\b([A-Z]\w+)\.Register\w*\(", s["text"])
                if calls:
                    families.append({"path": p, "line": t.count("\n", 0, s["start"]) + 1, "method": s["name"], "calls": sorted(set(calls)), "sha256": digest(s["text"])})
    for entry in catalog:
        symbol = entry.get("symbol", "")
        if symbol.endswith(".Constructors"):
            registrations[entry["typeId"]].append({**entry["exposure"], "expression": symbol + "：Register后缀循环"})
    for symbol in ["ContentPrimitiveEffectExecutor.StoreRemoved", "ContentPrimitiveEffectExecutor.ClearRouteRoad", "RewardAcquisitionFlow.TypeId"]:
        registrations[values[symbol]].append({"path": "Assets/YC/Domain/Effects/ContentPrimitiveEffectExecutor.cs", "expression": "ContentFlowTypeIds注册循环"})
    for symbol in ["ContentLifecycleEffectExecutor.RetireFacilityTypeId", "ContentLifecycleEffectExecutor.ReturnMarkerTypeId"]:
        registrations[values[symbol]].append({"path": "Assets/YC/Domain/Effects/ContentLifecycleEffectExecutor.cs", "expression": "PublicTypeIds注册循环"})
    entries = []
    public_types = {e.get("typeId") for e in catalog}
    for type_id, evidence in sorted(compiled.items()):
        entries.append({"typeId": type_id, "publicNames": [e["namespace"] + "." + e["name"] for e in catalog if e.get("typeId") == type_id],
                        "compiler": evidence, "registration": registrations.get(type_id, []), "compilerOnly": type_id not in public_types,
                        "note": "编译/注册是静态可达证据；通用执行器未实现分支会明确故障，不算已实现玩法"})
    return entries, families


def lua_calls(text, names):
    # 在当前受控内容中，先完整记录函数别名赋值，再记录其调用；不把Effect构造结果当函数别名。
    aliases = {m[1]: m[2] for m in re.finditer(r"\b(\w+)\s*=\s*Effect\.(\w+)\s*(?=;|--|\n|$)", text)}
    rows = []
    for match in re.finditer(r"\b(Effect|Flow)\.(\w+)\s*(?=[({])", text):
        start = match.end()
        if text[start] == "(":
            start += 1
            while start < len(text) and text[start].isspace():
                start += 1
        body = ""
        if start < len(text) and text[start] == "{":
            # Lua中的注释及引号内括号不参与配平。
            masked = re.sub(r"--[^\n]*|'(?:\\.|[^'\\])*'|\"(?:\\.|[^\"\\])*\"", lambda m: " " * len(m[0]), text)
            end = closing(masked, start)
            body = text[start:end]
        rows.append({"namespace": match[1], "name": match[2], "line": text.count("\n", 0, match.start()) + 1,
                     "arguments": body, "apiVersion": (re.search(r"\bapiVersion\s*=\s*(\d+)", body) or [None, None])[1],
                     "mode": (re.search(r"\bexecutionMode\s*=\s*['\"]([^'\"]+)", body) or [None, None])[1]})
    for alias, name in aliases.items():
        for match in re.finditer(r"\b" + re.escape(alias) + r"\s*[(]", text):
            rows.append({"namespace": "Effect", "name": name, "line": text.count("\n", 0, match.start()) + 1,
                         "arguments": "", "alias": alias})
    return rows, aliases


def inputs():
    result = {}
    for directory in ["Assets", "docs", "内置DIY/文档", "tools/validation"]:
        for p in sorted((ROOT / directory).rglob("*")):
            if p.is_file() and p.suffix in {".cs", ".lua", ".json", ".md", ".py"} and p.name not in {Path(__file__).name, REVIEW.name, "audit_lua_api_upgrade.py"}:
                result[p.relative_to(ROOT).as_posix()] = read(p)
    # prompt下的冻结包仅按路径登记历史材料，不读取重复的历史源码或运行输入。
    historical = []
    for folder, dirs, files in os.walk(ROOT / "prompt"):
        dirs[:] = sorted(d for d in dirs if d != "UI素材")
        for name in sorted(files):
            p = Path(folder) / name
            if p.suffix in {".cs", ".lua", ".json", ".md", ".py", ".ps1"}:
                relative = p.relative_to(ROOT).as_posix()
                historical.append({"path": relative, "category": "历史材料",
                                   "reason": "不在当前Assets/正式pack装配根内；冻结输入和实施材料不计当前生产消费者"})
    return result, historical


def collect(review):
    texts, history = inputs()
    values, locations = constants(texts)
    catalog = public_catalog(texts, values, locations)
    bindings, manifest = content_bindings(texts)
    scopes_by_path = {p: scopes(t) for p, t in texts.items() if p.endswith(".cs")}
    rows = []
    for target in review["targets"]:
        identities = set(target.get("typeIds", []))
        symbols = {key for key, value in values.items() if value in identities}
        needles = set(target.get("tokens", [])) | symbols | identities
        for name in target.get("names", []):
            needles.add(name)
        patterns = [re.compile(r"(?<![\w])" + re.escape(n) + r"(?![\w])") for n in sorted(needles)]
        file_pattern = re.compile(r"(?<![\w])(?:" + "|".join(re.escape(n) for n in sorted(needles)) + r")(?![\w])")
        common_guards = {"RetiredMarkerContractReason", "TryValidateRetiredCardMarkerRawState", "TryValidateContentContinuations"}
        direct_pattern = re.compile(r"(?<![\w])(?:" + "|".join(re.escape(n) for n in sorted(needles - common_guards)) + r")(?![\w])")
        for path, source in texts.items():
            if path not in target.get("owners", []) and not file_pattern.search(source):
                continue
            spans = scopes_by_path.get(path, [])
            local_tokens = []
            if path.endswith(".cs"):
                local_tokens.extend(symbol.split(".")[-1] for symbol in symbols if locations.get(symbol, {}).get("path") == path)
                # 测试类中的RetiredRelease等常量不能因未进生产符号表而漏掉手写规格/恢复反例。
                literal_values = identities | set(target.get("tokens", []))
                for match in re.finditer(r'\bconst\s+string\s+(\w+)\s*=\s*"([^"]+)"', source):
                    if match[2] in literal_values:
                        local_tokens.append(match[1])
            offset = 0
            for number, line in enumerate(source.splitlines(keepends=True), 1):
                matched = [p.pattern for p in patterns if p.search(line)]
                matched += [token for token in local_tokens if re.search(r"\b" + re.escape(token) + r"\b", line)]
                owner = next((s for s in spans if s["start"] <= offset < s["end"]), None)
                # 同类常量的无前缀引用以及版本/模式接受分支。
                if not matched and path in target.get("owners", []):
                    if any(re.search(r"\b" + re.escape(symbol.split(".")[-1]) + r"\b", line) for symbol in symbols if symbol.startswith(class_at_cached(path, texts, review) + ".")):
                        matched = ["同类常量引用"]
                    if re.search(target.get("ownerPattern", r"(?!)"), line):
                        matched.append("版本/模式/字段分支")
                if matched:
                    method = owner["name"] if owner else "<声明或文件级>"
                    rows.append({"target": target["id"], "path": path, "line": number, "method": method,
                                 "source": line.rstrip(), "scopeSha256": digest(owner["text"] if owner else source),
                                 "scope": owner["text"] if owner else "", "matches": matched,
                                 "relation": "共享门禁旁证" if any(n in line for n in common_guards) and not direct_pattern.search(line) else "直接引用或版本/模式分支"})
                offset += len(line)
        target["resolvedSymbols"] = sorted(symbols)
    calls = []
    aliases = []
    for path, text in texts.items():
        if not path.startswith("Assets/StreamingAssets/Content/") or not path.endswith(".lua"):
            continue
        found, alias = lua_calls(text, catalog)
        for row in found:
            relative = path.removeprefix("Assets/StreamingAssets/Content/core/")
            calls.append({**row, "path": path, "bindings": bindings.get(relative, []),
                          "bound": bool(bindings.get(relative)), "declarationKind": "正式构造调用" if bindings.get(relative) else "未绑定脚本"})
        aliases.extend({"path": path, "alias": alias_name, "constructor": name} for alias_name, name in alias.items())
    compiled, families = type_inventory(texts, values, catalog)
    generated = []
    for path, source in texts.items():
        if path.endswith(".cs") and "/Tests/" not in path:
            for match in re.finditer(r"\b(Effect|Flow)\.(\w+)\s*[(\{]", source):
                owner = next((s for s in scopes_by_path[path] if s["start"] <= match.start() < s["end"]), None)
                generated.append({"path": path, "line": source.count("\n", 0, match.start()) + 1,
                                  "method": owner["name"] if owner else "<声明或文件级>",
                                  "namespace": match[1], "name": match[2], "evidence": source.splitlines()[source.count("\n", 0, match.start())]})
    lua_files = []
    dynamic = []
    for path, source in texts.items():
        if path.startswith("Assets/StreamingAssets/Content/") and path.endswith(".lua"):
            relative = path.removeprefix("Assets/StreamingAssets/Content/core/")
            lua_files.append({"path": path, "bindings": bindings.get(relative, []), "bound": bool(bindings.get(relative))})
            for m in re.finditer(r"\b(?:Effect|Flow)\s*\[|\brequire\s*\(", source):
                dynamic.append({"path": path, "line": source.count("\n", 0, m.start()) + 1, "source": m[0]})
    return {"catalog": catalog, "compilerTypes": compiled, "registrationFamilies": families,
            "references": rows, "contentCalls": calls, "generatedLuaCalls": generated,
            "luaAliases": aliases, "luaFiles": lua_files, "dynamicLuaReferences": dynamic,
            "historicalFiles": history, "bindings": dict(bindings), "manifestVersion": manifest["version"],
            "inputs": [{"path": p, "sha256": digest(t)} for p, t in texts.items()],
            "targets": review["targets"]}, texts


def class_at_cached(path, texts, review):
    # owners由人工指定其执行器类；只用于该类无前缀常量的引用，避免把别处的TypeId串进来。
    return Path(path).stem


def grouped_references(data):
    groups = collections.defaultdict(list)
    for row in data["references"]:
        groups[(row["target"], row["path"], row["method"])].append(row)
    return [{"target": target, "path": path, "method": method, "key": "|".join((target, path, method)),
             "lines": [r["line"] for r in references], "source": "\n".join(r["source"] for r in references),
             "relations": sorted(set(r["relation"] for r in references)),
             "scope": "\n\n".join(dict.fromkeys(r["scope"] for r in references)),
             "scopeSha256": digest("\n".join(sorted(set(r["scopeSha256"] for r in references))))}
            for (target, path, method), references in sorted(groups.items())]


def verify_review(data, groups, review):
    decisions = {d["key"]: d for d in review.get("decisions", [])}
    errors = []
    if len(decisions) != len(review.get("decisions", [])):
        errors.append("重复审阅键")
    for group in groups:
        decision = decisions.get(group["key"])
        if decision is None:
            errors.append("未分类：" + group["key"])
        elif decision.get("scopeSha256") != group["scopeSha256"]:
            errors.append("源码已改变：" + group["key"])
        elif bool(decision.get("categories")) == bool(decision.get("excluded")) or any(c not in CATEGORIES for c in decision.get("categories", [])) or not decision.get("reason"):
            errors.append("分类缺少理由或无效：" + group["key"])
    errors.extend("已消失的审阅项：" + k for k in sorted(set(decisions) - {g["key"] for g in groups}))
    fingerprint = digest(json.dumps(data["inputs"], ensure_ascii=False, sort_keys=True))
    if review.get("inputFingerprint") != fingerprint:
        errors.append("审计输入指纹变化；须复核新增消费者、目录及正式绑定")
    if data.get("toolFingerprint") is not None and review.get("toolFingerprint") != data["toolFingerprint"]:
        errors.append("采集/核验工具变化；须复核审计方法")
    if any(not e.get("typeId") and e.get("name") not in {"WithContext", "WithPresentation"} for e in data["catalog"]):
        errors.append("公开类型未解析")
    compiled = {e["typeId"] for e in data["compilerTypes"]}
    if any(e.get("typeId") and e["typeId"] not in compiled for e in data["catalog"]):
        errors.append("公开类型无编译证据")
    if any(not e["registration"] for e in data["compilerTypes"]):
        errors.append("编译类型缺注册证据")
    for target in review["targets"]:
        if target.get("expectedRetired"):
            if any(e.get("typeId") in target["typeIds"] or e["name"] in target["names"] for e in data["catalog"]) or compiled.intersection(target["typeIds"]):
                errors.append("已退役入口回流公开/编译目录：" + target["id"])
    if data["luaAliases"] or data["dynamicLuaReferences"]:
        errors.append("正式内容出现函数别名或动态引用，须补充专项追踪")
    if any(not c["bound"] for c in data["contentCalls"]):
        errors.append("出现含构造调用的未绑定脚本，须逐项判读")
    return errors, decisions, fingerprint


def run(output, draft=False):
    review = json.loads(read(REVIEW))
    data, texts = collect(review)
    data["toolFingerprint"] = digest("\n".join(read(p) for p in [Path(__file__), Path(__file__).with_name("audit_lua_api_upgrade.py"), ROOT / "tools/tests/test_lua_api_upgrade_inventory.py"]))
    group_rows = grouped_references(data)
    output.mkdir(parents=True, exist_ok=True)
    (output / "逐方法待审清单.json").write_text(json.dumps(group_rows, ensure_ascii=False, indent=2), encoding="utf-8")
    (output / "原始引用.json").write_text(json.dumps({k: v for k, v in data.items() if k != "references"}, ensure_ascii=False, indent=2), encoding="utf-8")
    errors, decisions, fingerprint = verify_review(data, group_rows, review)
    if not draft:
        classified = [{**g, **decisions.get(g["key"], {})} for g in group_rows]
        (output / "逐项分类.json").write_text(json.dumps(classified, ensure_ascii=False, indent=2), encoding="utf-8")
        lines = ["# API-01 逐项分类清单", "", "由当前源码和固定审阅决定生成。同一方法兼有旧正向准备与拒绝断言时保留两种分类；失效指不符合升级目标，不代表现有用例运行失败。", "",
                 "| 对象 | 分类 | 文件／方法／引用行 | 判读依据 |", "| --- | --- | --- | --- |"]
        for g in classified:
            category = "、".join(g.get("categories", [])) or "排除：非本API"
            path = g["path"]
            link = os.path.relpath(ROOT / path, output).replace("\\", "/")
            lines.append("| " + " | ".join([g["target"], category, f'[{path}](<{link}>) · {g["method"]} · ' + ",".join(map(str, g["lines"])), g.get("reason", "未审阅").replace("|", "／")]) + " |")
        (output / "逐项分类.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    summary = {"head": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
               "scope": review["scope"], "draft": draft, "inputFingerprint": fingerprint, "toolFingerprint": data["toolFingerprint"],
               "groups": len(group_rows), "references": len(data["references"]), "catalog": len(data["catalog"]),
               "compilerTypes": len(data["compilerTypes"]), "compilerOnly": sum(e["compilerOnly"] for e in data["compilerTypes"]),
               "luaFiles": len(data["luaFiles"]), "unboundLuaFiles": [f["path"] for f in data["luaFiles"] if not f["bound"]],
               "calls": len(data["contentCalls"]), "historyFiles": len(data["historicalFiles"]),
               "classifiedGroups": sum(bool(d.get("categories")) for d in decisions.values()),
               "excludedGroups": sum(bool(d.get("excluded")) for d in decisions.values()),
               "categoryCounts": dict(collections.Counter(c for d in decisions.values() for c in d.get("categories", []))),
               "errors": errors, "passed": not errors and not draft}
    (output / "审计核验.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({**summary, "errors": errors[:5], "errorCount": len(errors)}, ensure_ascii=False))
    return data, group_rows, texts, summary


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    parser.add_argument("--draft", action="store_true", help="只采集待审材料；草稿永不标记通过")
    args = parser.parse_args()
    destination = (ROOT / args.output).resolve()
    if destination == ROOT or not destination.is_relative_to(ROOT) or destination.is_relative_to(ROOT / "游城拓荒/UI素材"):
        raise ValueError("输出目录越界")
    _, _, _, result = run(destination, draft=args.draft)
    raise SystemExit(0 if args.draft or result["passed"] else 1)
