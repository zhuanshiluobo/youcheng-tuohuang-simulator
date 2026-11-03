"""API-01/API-33：仅扫描明确生产/测试目录，输出升级消费者快照。"""
import argparse
import hashlib
import json
import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
HOST = ROOT / "Assets/YC/Infrastructure/Lua/MoonSharpLuaRuntimeHost.cs"


def availability_report(source, effects, flows):
    """只检查表格首列的可用入口；退役说明、示例正文和历史材料不算推荐项。"""
    entries = []
    for number, line in enumerate(source.splitlines(), 1):
        if not line.startswith("|"):
            continue
        for namespace, name in re.findall(r"\b(Effect|Flow)\.(\w+)", line.split("|", 2)[1]):
            entries.append({"line": number, "api": namespace + "." + name,
                            "exposed": name in (effects if namespace == "Effect" else flows)})
    return {"entries": entries, "count": len(entries), "unexposedRecommendations": [row for row in entries if not row["exposed"]]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True)
    parser.add_argument("--verify-docs-only", action="store_true", help="仅核对 DIY 可用 API 表与当前构造目录")
    parser.add_argument("--verify-reviewed", action="store_true", help="核验API-01逐项分类、完整消费者与固定审阅指纹")
    args = parser.parse_args()
    out = (ROOT / args.output).resolve()
    if not out.is_relative_to(ROOT) or out == ROOT or out.is_relative_to(ROOT / "游城拓荒/UI素材"):
        raise ValueError("输出必须位于项目内且不能进入受限原档案")
    out.mkdir(parents=True, exist_ok=True)
    if args.verify_reviewed:
        if args.verify_docs_only:
            parser.error("--verify-reviewed与--verify-docs-only须分别运行")
        from lua_api_upgrade_inventory import run
        return 0 if run(out)[3]["passed"] else 1
    host = HOST.read_text(encoding="utf-8-sig")
    catalog = host.split("private static Dictionary<string, string> CreateRegisteredEffectConstructors()", 1)[1].split("return registered;", 1)[0]
    names = re.findall(r'\{ "(\w+)", ', catalog)
    for relative in ["Domain/Cards/CharacterContentEffectExecutor.cs", "Domain/Effects/ContentTransactionEffectExecutor.cs"]:
        source = (ROOT / "Assets/YC" / relative).read_text(encoding="utf-8-sig")
        dictionary = source.split("Constructors =", 1)[1].split("};", 1)[0]
        names += re.findall(r'\{ "(\w+)", ', dictionary)
    names = sorted(set(names))
    wrappers = host.split("public static IReadOnlyCollection<string> SupportedEffectConstructors =>", 1)[1].split(";", 1)[0]
    flow_catalog = host.split("public static IReadOnlyCollection<string> SupportedFlowConstructors =>", 1)[1].split(";", 1)[0]
    flows = re.findall(r'"(\w+)"', flow_catalog)
    document = ROOT / "docs/设计/DIY内容开发-Lua API.md"
    availability = availability_report(document.read_text(encoding="utf-8-sig"),
                                       set(names) | set(re.findall(r'"(\w+)"', wrappers)), set(flows))
    availability.update(document=document.relative_to(ROOT).as_posix(),
                        documentSha256=hashlib.sha256(document.read_bytes()).hexdigest(),
                        scope="校验可用表中的入口是否实际暴露；不代表参数、规则执行或正常玩法已验收")
    (out / "可用API表核验.json").write_text(json.dumps(availability, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.verify_docs_only:
        print(json.dumps({"checked": availability["count"], "unexposedRecommendations": availability["unexposedRecommendations"]}, ensure_ascii=False))
        return 1 if availability["unexposedRecommendations"] else 0
    files = sorted(p for root in [ROOT / "Assets/YC", ROOT / "Assets/StreamingAssets/Content"]
                   for p in root.rglob("*") if p.is_file() and p.suffix in {".cs", ".lua", ".json"})
    texts = {p: p.read_text(encoding="utf-8-sig") for p in files}
    targets = {"Repeat", "LoadCardMarkers", "SelectInfluence", "ChooseBuild", "ChooseExplore",
               "TransferCharacterCard", "RetainCharacter", "MoveCharacterCardToZone", "StorePlayerMarker",
               "ReturnPlayerMarker", "UpgradeEnterpriseForPlayer", "ReleaseFacilityBinding", "DepartureBatch"}
    rows = []
    for name in sorted(set(names) | targets):
        pattern = re.compile(r"\b" + name + r"\b")
        references = []
        for path, content in texts.items():
            for number, line in enumerate(content.splitlines(), 1):
                if not pattern.search(line):
                    continue
                relative = path.relative_to(ROOT).as_posix()
                kind = "测试（须逐项区分正向/拒绝）" if "/Tests/" in relative else "生产源码/注册（须追踪工厂/恢复）"
                if relative.startswith("Assets/StreamingAssets/"):
                    kind = "内容脚本（绑定另见定义）" if path.suffix == ".lua" else "内容定义/登记"
                references.append({"path": relative, "line": number, "category": kind, "source": line.strip()})
        rows.append({"name": name, "currentlyExposed": name in names, "references": references})
    identities = [{"path": p.relative_to(ROOT).as_posix(), "sha256": hashlib.sha256(p.read_bytes()).hexdigest()} for p in files]
    result = {"head": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
              "effectConstructors": names, "effectCount": len(names), "flowConstructors": flows, "flowCount": len(flows),
              "availabilityTable": availability,
              "luaFileCount": sum(p.suffix == ".lua" for p in files), "files": identities, "entries": rows,
              "scope": "仅 Assets/YC 与 Assets/StreamingAssets/Content；不读取受限原档案。静态引用不代表启用能力数或运行覆盖。",
              "limits": ["须结合正式绑定、生成 Lua、内部工厂及恢复逐项判读", "未检查外部 DIY 或用户存档", "不计作正常玩法/联机验收"]}
    (out / "消费者快照.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({key: result[key] for key in ["head", "effectCount", "flowCount", "luaFileCount", "scope"]}, ensure_ascii=False))
    return 1 if availability["unexposedRecommendations"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
