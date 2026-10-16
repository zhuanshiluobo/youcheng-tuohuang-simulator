"""只读比较 Unity 贴图引用与已批准 UI 素材清单，不访问历史原素材目录。

扫描结果是证据，不自动认定未引用素材缺失，不修改资产或人工审计报告。
用法：python tools/ui/audit_ui_library_usage.py --output Artifacts/ui-usage-audit
"""
from __future__ import annotations

import argparse
import collections
import csv
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[2]
IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".tga", ".psd", ".svg", ".bmp", ".webp"}
GUID = re.compile(r"guid: ([0-9a-f]{32})")
DOCUMENT = re.compile(r"^--- !u!(\d+) &(-?\d+)(?: stripped)?\n([\s\S]*?)(?=^--- !u!|\Z)", re.M)


def relative(path: Path) -> str:
    return path.relative_to(ROOT).as_posix()


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig", errors="replace")


def save(path: Path, value) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def decoded_name(value: str) -> str:
    if value.startswith('"'):
        try:
            return json.loads(value)
        except json.JSONDecodeError:
            pass
    return value


def parse_references(path: Path, text: str, guid_paths: dict[str, str]):
    documents = {}
    for match in DOCUMENT.finditer(text):
        kind, object_id, body = match.group(1, 2, 3)

        def field(name):
            found = re.search(r"^  " + re.escape(name) + r": (.*)$", body, re.M)
            return found[1] if found else ""

        def file_id(name):
            found = re.search(r"fileID: (-?\d+)", field(name))
            return found[1] if found else ""

        documents[object_id] = {
            "kind": kind, "name": decoded_name(field("m_Name")), "game_object": file_id("m_GameObject"),
            "parent": file_id("m_Father"), "body": body, "line": text[:match.start(3)].count("\n") + 1,
        }
    transforms = {value["game_object"]: key for key, value in documents.items() if value["kind"] in ("4", "224")}

    def object_path(object_id, seen=None):
        seen = set() if seen is None else seen
        if object_id in seen or object_id not in documents:
            return ""
        seen.add(object_id)
        value = documents[object_id]
        if value["kind"] == "1":
            transform = documents.get(transforms.get(object_id, ""), {})
            parent = object_path(transform.get("parent", ""), seen)
            return (parent + "/" if parent else "") + value["name"]
        return object_path(value["game_object"], seen)

    references, overrides = [], []
    for object_id, value in documents.items():
        for offset, line in enumerate(value["body"].splitlines()):
            for guid in GUID.findall(line):
                texture = guid_paths.get(guid, "")
                if Path(texture).suffix.lower() in IMAGE_EXTENSIONS:
                    references.append({
                        "file": relative(path), "line": value["line"] + offset,
                        "object_id": object_id, "object_path": object_path(object_id), "type": value["kind"],
                        "property": line.strip().split(":")[0], "texture": texture, "guid": guid,
                        "text": line.strip(),
                    })
        if value["kind"] != "1001":
            continue
        pattern = (r"- target: \{fileID: (-?\d+), guid: (\w+), type: 3\}\n"
                   r"\s+propertyPath: ([^\n]+)\n\s+value:([^\n]*)\n\s+objectReference: ([^\n]+)")
        for match in re.finditer(pattern, value["body"]):
            if match[3] in ("m_Sprite", "m_OverrideSprite", "m_Texture"):
                overrides.append({
                    "file": relative(path), "line": value["line"] + value["body"][:match.start()].count("\n"),
                    "prefab": guid_paths.get(match[2], match[2]), "object_id": match[1],
                    "property": match[3], "reference": match[5],
                })
    return references, overrides


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", help="输出目录；省略时在 Artifacts 下建立带时间的新目录")
    args = parser.parse_args()
    output = Path(args.output) if args.output else Path("Artifacts") / ("ui-usage-audit-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
    output = (ROOT / output).resolve() if not output.is_absolute() else output.resolve()
    artifacts = (ROOT / "Artifacts").resolve()
    if not output.is_relative_to(artifacts) or output == artifacts:
        parser.error("输出必须是当前项目 Artifacts 内的明确子目录，避免覆盖项目输入或人工审计文档。")

    # 只读已批准清单中的哈希；来源包名/成员只是字符串，不据此打开历史档案。
    library = ROOT / "游城拓荒/可用UI素材"
    index = json.loads(read(library / "清单/可用素材索引.json"))
    members = json.loads(read(library / "清单/来源成员与去向.json"))
    by_hash = {entry["sha256"]: entry for entry in index["assets"]}
    source_by_hash = collections.defaultdict(list)
    for member in members:
        if member.get("sha256"):
            source_by_hash[member["sha256"]].append(member)

    guid_paths = {}
    for path in (ROOT / "Assets").rglob("*.meta"):
        match = re.search(r"^guid: (\w+)", read(path), re.M)
        if match:
            guid_paths[match[1]] = relative(path)[:-5]
    references, overrides, graph = [], [], {}
    serialized_files = 0
    for path in (ROOT / "Assets").rglob("*"):
        if path.suffix not in (".prefab", ".unity", ".asset", ".mat", ".anim", ".controller"):
            continue
        content = read(path)
        serialized_files += 1
        rows, changes = parse_references(path, content, guid_paths)
        references.extend(rows)
        overrides.extend(changes)
        graph[relative(path)] = sorted({guid_paths[guid] for guid in GUID.findall(content) if guid in guid_paths})

    grouped_refs = collections.defaultdict(list)
    for row in references:
        grouped_refs[row["guid"]].append(row)
    textures = []
    for guid, name in guid_paths.items():
        path = ROOT / name
        if path.suffix.lower() not in IMAGE_EXTENSIONS or not path.is_file():
            continue
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        approved = by_hash.get(digest)
        texture_refs = grouped_refs[guid]
        textures.append({
            "texture": name, "guid": guid, "sha256": digest,
            "reference_count": len(texture_refs), "reference_files": sorted({row["file"] for row in texture_refs}),
            "library_match": approved,
            "source_decisions": source_by_hash.get(digest, []),
            "classification": "匹配可用库，仍须核对用途" if approved else
                "仅匹配未选来源，须按用途复核" if source_by_hash.get(digest) else "未匹配本库，不能据此判错",
        })

    settings = ROOT / "ProjectSettings/EditorBuildSettings.asset"
    scenes = re.findall(r"- enabled: 1\s+path: ([^\n]+)", read(settings)) if settings.exists() else []
    reachability = {}
    for scene in scenes:
        routes = {scene: [scene]}
        queue = collections.deque([scene])
        while queue:
            current = queue.popleft()
            for target in graph.get(current, []):
                if target not in routes:
                    routes[target] = routes[current] + [target]
                    queue.append(target)
        reachability[scene] = routes

    referenced_hashes = {row["sha256"] for row in textures if row["reference_count"]}
    unreferenced = [{"path": entry["path"], "sha256": digest, "uses": entry.get("uses", []),
                     "status": "未发现序列化引用；未判定为缺项或必须接入"}
                    for digest, entry in by_hash.items() if digest not in referenced_hashes]
    summary = {
        "generated_at": datetime.now().astimezone().isoformat(), "project": str(ROOT),
        "inputs": ["Assets", "ProjectSettings/EditorBuildSettings.asset", "游城拓荒/可用UI素材/清单/可用素材索引.json", "游城拓荒/可用UI素材/清单/来源成员与去向.json"],
        "historical_archive_accessed": False, "serialized_files": serialized_files,
        "textures": len(textures), "texture_references": len(references), "sprite_texture_overrides": len(overrides),
        "classification_counts": dict(collections.Counter(row["classification"] for row in textures)),
        "limitations": ["引用包括模板、目录和状态数组，不等于实际可见实例。", "未引用不等于缺项；备用尺寸、备用状态和动态加载须单独判断。",
                        "嵌套实例覆盖单独输出；可达链不自动代表当前屏幕可见。", "不运行Unity、不读取历史素材、不更改界面资产、不覆盖人工审计结论。"],
    }
    output.mkdir(parents=True, exist_ok=True)
    save(output / "usage-summary.json", summary)
    save(output / "texture-inventory.json", textures)
    save(output / "texture-references.json", references)
    save(output / "sprite-texture-overrides.json", overrides)
    save(output / "scene-reachability.json", reachability)
    save(output / "library-without-serialized-reference.json", unreferenced)
    with (output / "texture-references.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        fields = ["file", "line", "object_id", "object_path", "type", "property", "texture", "guid", "text"]
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(references)
    print(json.dumps({"output": str(output), **summary}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
