"""整理经审阅的 UI 白名单；默认只校验新库，不读取受限原档案。"""
from __future__ import annotations

import argparse
import fnmatch
import hashlib
import json
import re
import struct
from collections import Counter
from datetime import datetime, timedelta, timezone
from pathlib import Path, PurePosixPath
from zipfile import ZipFile
import zlib

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "游城拓荒/UI素材"
DEST = ROOT / "游城拓荒/可用UI素材"
IMAGE_EXT = {".png", ".jpg", ".jpeg", ".svg"}
GROUPS = {
    "MainGameUI-v2.2": "通用控件与原画", "MainGameUI-CooperationMarkers-v1": "玩家标记",
    "FrontierUI-v3-Patch": "通用皮肤与顶栏", "MainButtons-v3.2-Patch": "主行动按钮",
    "MainFrame-v3.5-Patch": "主界面四项框体", "EffectRows-v3.4-Patch": "效果条目",
    "CardViewer-v3.6-Patch": "卡牌查看器", "Card-Picker-UI-v1": "多卡选择",
    "Option-Picker-UI-v1": "普通选项与数量", "EnterprisePicker-v1.0": "企业选择",
    "FacilityBuild-UI-v1.0": "设施建设", "CityPattern-UI-v1.0": "城市样式",
    "RhineDepartment-UI-v1.0": "莱茵科室", "Room-UI-v1.1-VerticalMaps": "房间",
    "Settlement-Effects-v1.0": "结算插入按钮", "Settlement-Icons-Extra-v1.1": "结算备用图标",
    "Settlement-Icons-EndPhase-v1.2": "收尾阶段结算图标",
    "Main-Info-Buttons-v1.0": "区控与游戏盒入口", "Region-Squares-v1.0": "区域标记",
    "Area-Control-v1.0": "区控页面", "Player-Select-v1.0": "玩家选择", "Game-Box-v1.0": "游戏盒页面",
}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def save(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def safe_relative(value):
    rel = PurePosixPath(value)
    if rel.is_absolute() or ".." in rel.parts or any(":" in x for x in rel.parts):
        raise ValueError(f"非法相对路径：{value}")
    return rel.as_posix()


def validate_image(data, suffix):
    if suffix == ".png":
        assert data[:8] == b"\x89PNG\r\n\x1a\n"
        width, height = struct.unpack(">II", data[16:24])
        offset, compressed, ended = 8, [], False
        while offset + 12 <= len(data):
            size = struct.unpack(">I", data[offset:offset+4])[0]
            kind, payload = data[offset+4:offset+8], data[offset+8:offset+8+size]
            assert len(payload) == size
            checksum = struct.unpack(">I", data[offset+8+size:offset+12+size])[0]
            assert zlib.crc32(kind + payload) & 0xffffffff == checksum
            if kind == b"IDAT":
                compressed.append(payload)
            offset += size + 12
            if kind == b"IEND":
                ended = True
                break
        assert ended and compressed
        zlib.decompress(b"".join(compressed))
        return {"width": width, "height": height}
    if suffix in (".jpg", ".jpeg"):
        assert data[:2] == b"\xff\xd8" and data[-2:] == b"\xff\xd9"
    return None


def choose(package, rel, rules):
    suffix = PurePosixPath(rel).suffix.lower()
    if suffix not in IMAGE_EXT:
        return False, "文档、参数或生产工具，非贴图", None
    if suffix == ".svg":
        return False, "生产格式不作为导入候选；独立视觉已有 PNG 覆盖，避免外链原料及重复选材", None
    if any(x in PurePosixPath(rel).parts for x in ("preview", "examples", "reference", "verification", "sources")):
        return False, "预览、示例或生产原料不得作为运行时替代图", None
    if PurePosixPath(rel).stem in ("gallery", "sample-card", "main-background"):
        return False, "拼装预览或样例牌，不是正式 UI 素材", None
    # 旧选项整窗无明确当前使用例外；相同字节仍可由 CardPicker 的独立许可收录。
    if package == "Option-Picker-UI-v1.zip" and PurePosixPath(rel).name in ("window.png", "button-primary.png", "button-secondary.png"):
        return False, "旧选项外壳已迁移；CardPicker 专用许可不能外推", None
    if package == "CityPattern-UI-v1.0.zip" and PurePosixPath(rel).stem == "icon-lock":
        return False, "用户已明确删除城市样式锁图标；其他窗口许可另列", None
    if package == "MainGameUI-v2.2.zip" and rel.startswith("06-icons/") and any(x in PurePosixPath(rel).stem for x in ("settings", "collapse", "expand", "undo")):
        return False, "顶栏及撤销对应语义已有指定新版图标", None
    matches = [x for x in rules if fnmatch.fnmatchcase(rel, x["pattern"])]
    matches.sort(key=lambda x: len(x["pattern"].replace("*", "")), reverse=True)
    if not matches:
        return False, "无当前用途许可；不从文件存在推定可用", None
    rule = matches[0]
    decision = rule["decision"]
    allowed = decision.startswith(("保留", "限制使用", "可选兼容方案", "推荐且项目已采用"))
    return allowed, rule["reason"], rule


def parameters_for(member, documents):
    found = []
    def walk(value, location, parent=None):
        if isinstance(value, dict):
            if member in [v for v in value.values() if isinstance(v, str)]:
                found.append({"parameter": location, "spec": value, "parentSpec": parent if parent and any(k in parent for k in ("slices", "sliceLTRB", "role", "size")) else None})
            for k, v in value.items():
                if isinstance(v, (dict, list)):
                    walk(v, location + "/" + str(k), value)
        elif isinstance(value, list):
            for i, v in enumerate(value):
                walk(v, location + "/" + str(i), parent)
    for name, value in documents.items():
        if any(x in name.lower() for x in ("manifest", "assets.json", "component-map", "layout.json", "icons.json", "button-roles")):
            walk(value, name)
    return found


def build(rules_path):
    if DEST.exists():
        raise FileExistsError("新库已存在，禁止静默覆盖；请使用 --verify")
    policy = json.loads(rules_path.read_text(encoding="utf-8-sig"))
    packages = policy["packages"]
    assert set(packages) == {p.name for p in SOURCE.glob("*.zip")}, "源包集合变化，必须重新审阅"
    entries, members, package_index, payloads = {}, [], [], {}
    documents_to_copy, parameters_to_copy = {}, {}
    for package, decision in packages.items():
        stem = Path(package).stem
        archive_data = (SOURCE / package).read_bytes()
        with ZipFile(SOURCE / package) as archive:
            prefix = decision["zipRoot"] + "/"
            all_data = {}
            for info in archive.infolist():
                if info.is_dir():
                    continue
                assert info.filename.startswith(prefix)
                rel = safe_relative(info.filename[len(prefix):])
                assert rel not in all_data
                all_data[rel] = archive.read(info)  # 同时核验 ZIP CRC。
        params = {}
        for rel, data in all_data.items():
            if rel.endswith(".json"):
                value = json.loads(data)
                # 保留布局、用途、切片及示例数据；不复制历史 QA 与哈希表冒充本次验收。
                if not any(x in rel.lower() for x in ("qa", "verification", "checksums", "package-files")) and not rel.startswith(("preview/", "tools/")):
                    params[rel] = value
                    parameters_to_copy[f"参数/{stem}/{rel}"] = data
            if rel.endswith(".md") and stem in GROUPS:
                documents_to_copy[f"文档/原始说明/{stem}/{rel}"] = data
        docs = sorted(p for p in documents_to_copy if p.startswith(f"文档/原始说明/{stem}/"))
        package_index.append({"package": package, "version": decision["adoptedVersion"], "sha256": sha(archive_data), "members": len(all_data), "category": GROUPS.get(stem, "已淘汰"), "documents": docs, "notes": decision.get("notes", [])})
        for rel, data in all_data.items():
            allowed, reason, rule = choose(package, rel, decision["rules"])
            suffix = PurePosixPath(rel).suffix.lower()
            if allowed and suffix == ".svg":
                refs = re.findall(r'(?:xlink:)?href=[\"\']([^\"\']+)', data.decode("utf-8-sig"))
                if any(not r.startswith(("#", "data:")) for r in refs):
                    allowed, reason = False, "SVG 依赖外部生产原料；使用本条目已交付的 PNG，禁止单独导入残缺 SVG"
            digest = sha(data)
            row = {"package": package, "member": rel, "sha256": digest, "bytes": len(data), "decision": "可用" if allowed else "不选用", "reason": reason}
            if rule:
                row["rule"] = rule
            members.append(row)
            if not allowed:
                continue
            scope = rule.get("scope", "")
            use = {"package": package, "member": rel, "purpose": reason, "scope": scope, "evidence": rule.get("evidence", []), "packageNotes": decision.get("notes", []), "documents": docs, "parameters": parameters_for(rel, params)}
            if digest not in entries:
                output = f"资产/{GROUPS[stem]}/{stem}/{rel}"
                entries[digest] = {"path": output, "sha256": digest, "bytes": len(data), "format": suffix, "image": validate_image(data, suffix), "uses": [], "sources": []}
                payloads[output] = data
            entry = entries[digest]
            entry["uses"].append(use)
            entry["sources"].append({"package": package, "member": rel})
            row["catalogPath"] = entry["path"]
    for digest, entry in entries.items():
        match = policy.get("validatedArtwork", {}).get(digest)
        if match:
            entry["formalSources"] = match["matches"]
            for use in entry["uses"]:
                member = use.get("member", "")
                if "atlas" in member:
                    use["scope"] = "只映射到已核对的正式整图与ArtworkSliceCatalog/CardVisualCatalog当前Sprite切片；不得把整图当UI显示或另导逐张正面"
                elif "world-map" in member:
                    use["scope"] = "仅正式四人地图；不得替代三人地图或用作通用UI背景"
                elif "character-back" in member:
                    use["scope"] = "仅对应阵营正式牌背；不得把一种阵营牌背用作其他牌类"
        for use in entry["uses"]:
            if use.get("member") == "assets/option-row.png":
                use["purpose"] += "；用户明确保留建设两个支付按钮原底图"
                use["evidence"].append("docs/设计/建设交互流程需求.md:12")
    for item in policy.get("projectAssets", []):
        source = ROOT / safe_relative(item["source"])
        data = source.read_bytes()
        assert sha(data) == item["sha256"], "已授权项目派生图发生变化，须重新审阅"
        use = {"purpose": item["purpose"], "scope": item.get("scope", "主界面资源与对应建设费用/库存；分数用于区控评分"), "evidence": item["evidence"], "documents": item.get("documents", ["文档/用途与替换规则.md"])}
        project_source = {"package": item.get("sourceKind", "项目已授权派生"), "member": item["source"]}
        entry = entries.get(item["sha256"])
        if entry is None:
            entry = {"path": item["path"], "sha256": item["sha256"], "bytes": len(data), "format": source.suffix, "image": validate_image(data, source.suffix), "sources": [], "uses": []}
            entries[item["sha256"]] = entry
            payloads[entry["path"]] = data
        if project_source not in entry["sources"]:
            entry["sources"].append(project_source)
        if use not in entry["uses"]:
            entry["uses"].append(use)
        if item.get("actualUsage"):
            entry["actualUsage"] = item["actualUsage"]
    for entry in entries.values():
        standards = []
        for standard in policy.get("currentUsageStandards", []):
            if entry["sha256"] in standard.get("assetHashes", []):
                standards.append(standard["id"])
        if standards:
            entry["currentUsageStandards"] = standards
            entry["actualUsage"] = "参数/项目实际用法/实际使用参数.json"
    # 字体是同一现用库的一部分，原文件名与字节不变。
    for p in sorted(SOURCE.iterdir()):
        if p.suffix.lower() in (".ttf", ".otf", ".ttc"):
            data = p.read_bytes()
            output = "资产/字体/" + p.name
            payloads[output] = data
            entries[sha(data)] = {"path": output, "sha256": sha(data), "bytes": len(data), "format": p.suffix, "image": None, "sources": [{"package": "独立字体", "member": p.name}], "uses": [{"purpose": "五类字体角色按 prompt/UI换新/02-单页面与字体补充规范.md；不得因文件存在改变现有字体角色", "documents": ["文档/用途与替换规则.md"]}]}
        elif p.suffix.lower() == ".md" and not p.name.startswith("AGENTS"):
            documents_to_copy["文档/原始说明/独立说明/" + p.name] = p.read_bytes()
        elif p.name == "Rhine-Department-Placement.png":
            # 用户独立交付的叠放坐标说明图：保留用法，不赋予运行时贴图许可。
            documents_to_copy["文档/原始说明/独立说明/" + p.name] = p.read_bytes()
    for mapping in (payloads, documents_to_copy, parameters_to_copy):
        for rel, data in mapping.items():
            target = DEST / safe_relative(rel)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
    save(DEST / "清单/版本选择规则.json", policy)
    save(DEST / "清单/可用素材索引.json", {"date": "2026-10-08", "rule": "仅按 uses 所列用途选用。存在于本库不代表允许跨用途替代。", "assets": list(entries.values())})
    save(DEST / "清单/来源成员与去向.json", members)
    save(DEST / "清单/源包索引.json", package_index)
    save(DEST / "清单/文档与参数哈希.json", [{"path": p, "sha256": sha(b)} for m in (documents_to_copy, parameters_to_copy) for p, b in m.items()])
    render_index(list(entries.values()), package_index)
    return verify()


def render_index(entries, package_index):
    lines = ["# 素材用途索引", "", "先读[当前用途标准与实际参数](文档/当前用途标准与实际参数.md)及[当前用途标准清单](清单/当前用途标准.json)，再按功能找图。已覆写的旧映射不再决定当前期望。以下都是可用文件的唯一副本；同图多包引用只占一份。详细切片与原参数位置见 JSON 索引。", "", "| 功能 | 版本 | 使用说明 |", "| --- | --- | --- |"]
    for p in package_index:
        if p["documents"]:
            lines.append(f'| {p["category"]} | {p["version"]} | [包内说明]({p["documents"][0]}) |')
    for group in list(dict.fromkeys(GROUPS.values())) + ["项目已授权派生", "字体"]:
        rows = [a for a in entries if a["path"].startswith("资产/" + group + "/")]
        if not rows:
            continue
        lines.extend(["", "## " + group, "", "| 文件 | 用途与边界 |", "| --- | --- |"])
        for a in rows:
            uses = list(dict.fromkeys(u["purpose"] + ("；限定：" + u["scope"] if u.get("scope") and u["scope"] != "全部" else "") for u in a["uses"]))
            lines.append(f'| [{Path(a["path"]).name}]({a["path"]}) | {"；".join(uses)} |')
    (DEST / "素材用途索引.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def verify_standard_parameters(standard, library_root, errors):
    parameters = standard.get("parameters")
    standard_id = standard["id"]
    if isinstance(parameters, str):
        try:
            relative = safe_relative(parameters.replace("\\", "/"))
            if not parameters.strip() or relative == ".":
                raise ValueError("参数路径为空")
            path = library_root / relative
            if not path.resolve().is_relative_to(library_root.resolve()):
                raise ValueError("参数路径超出可用库")
        except ValueError:
            errors.append("当前标准使用参数路径非法：" + standard_id + "/" + parameters)
            return
        if not path.is_file():
            errors.append("当前标准缺少使用参数：" + standard_id)
    elif isinstance(parameters, dict):
        asset = parameters.get("asset")
        if not isinstance(asset, str) or not asset.strip():
            errors.append("当前标准内嵌参数缺少有效asset路径：" + standard_id)
            return
        try:
            relative = safe_relative(asset.replace("\\", "/"))
            parts = PurePosixPath(relative).parts
            if len(parts) < 2 or parts[0] != "Assets":
                raise ValueError("内嵌参数asset须为项目Assets相对路径")
        except (ValueError, IndexError):
            errors.append("当前标准内嵌参数asset路径非法：" + standard_id + "/" + asset)
    else:
        errors.append("当前标准使用参数类型错误：" + standard_id
                      + "（应为路径字符串或内嵌对象，实际为" + type(parameters).__name__ + "）")


def verify():
    index = json.loads((DEST / "清单/可用素材索引.json").read_text(encoding="utf-8"))
    errors = []
    expected = set()
    for a in index["assets"]:
        path = DEST / safe_relative(a["path"])
        expected.add(path.resolve())
        if not path.is_file() or sha(path.read_bytes()) != a["sha256"]:
            errors.append("缺失或字节变化：" + a["path"])
        elif path.suffix.lower() in IMAGE_EXT:
            validate_image(path.read_bytes(), path.suffix.lower())
        if not a["uses"]:
            errors.append("用途缺失：" + a["path"])
    actual = {p.resolve() for p in (DEST / "资产").rglob("*") if p.is_file()}
    errors.extend("未登记资产：" + str(p) for p in actual - expected)
    for a in json.loads((DEST / "清单/文档与参数哈希.json").read_text(encoding="utf-8")):
        p = DEST / safe_relative(a["path"])
        if not p.is_file() or sha(p.read_bytes()) != a["sha256"]:
            errors.append("原说明或参数变化：" + a["path"])
    standards_path = DEST / "清单/当前用途标准.json"
    if standards_path.is_file():
        standards = json.loads(standards_path.read_text(encoding="utf-8"))["standards"]
        by_id = {s["id"]: s for s in standards}
        asset_hashes = {a["sha256"] for a in index["assets"]}
        for standard in standards:
            for digest in standard.get("assetHashes", []):
                if digest not in asset_hashes:
                    errors.append("当前标准引用未收录素材：" + standard["id"] + "/" + digest)
            verify_standard_parameters(standard, DEST, errors)
        for a in index["assets"]:
            for standard_id in a.get("currentUsageStandards", []):
                if standard_id not in by_id or a["sha256"] not in by_id[standard_id].get("assetHashes", []):
                    errors.append("素材与当前用途标准映射不一致：" + a["path"] + "/" + standard_id)
            if a.get("actualUsage") and not (DEST / safe_relative(a["actualUsage"])).is_file():
                errors.append("素材缺少实际使用参数：" + a["path"])
    result = {"date": datetime.now(timezone(timedelta(hours=8))).date().isoformat(), "assets": len(expected), "formats": dict(Counter(a["format"] for a in index["assets"])), "bytes": sum(a["bytes"] for a in index["assets"]), "errors": errors, "scope": "只核对可用库清单/哈希/PNG CRC与解压，不访问受限原档案，不代表Unity视觉验收"}
    save(DEST / "清单/校验结果.json", result)
    print(json.dumps(result, ensure_ascii=False))
    if errors:
        raise SystemExit(1)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verify", action="store_true", help="仅校验现用库，默认行为")
    parser.add_argument("--build-source-authorized", type=Path, help="用户本轮明确授权原库访问后，传入审阅完成的规则文件；此开关本身不是授权")
    args = parser.parse_args()
    if args.build_source_authorized:
        build(args.build_source_authorized)
    else:
        verify()
