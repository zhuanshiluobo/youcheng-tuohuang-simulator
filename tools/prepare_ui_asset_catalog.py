"""只读扫描 UI 源包，新增可追溯的素材整理目录；已有目录只能校验。"""

from __future__ import annotations

import argparse
import hashlib
import json
import struct
from collections import defaultdict
from pathlib import Path, PurePosixPath
from zipfile import ZipFile
import zlib


PROJECT = Path(__file__).resolve().parents[1]
SOURCE = PROJECT / "游城拓荒" / "UI素材"
DEFAULT_OUTPUT = SOURCE / "整理版-2026-09-24"
PACKAGES = {
    "FrontierUI-v3-Patch": ("3.1", "主界面新版组件、布局补丁和底栏；其他窗口适配依据"),
    "MainGameUI-v2.2": ("2.2.0", "主界面内容基线、原图、玩家标记和未替换控件"),
    "EnterprisePicker-v1.0": ("1.0.1", "企业／企业特效窗口；包内实际版本为 1.0.1"),
    "FacilityBuild-UI-v1.0": ("1.0.0", "设施建设窗口；效果区须按新版底栏合同迁移"),
    "CityPattern-UI-v1.0": ("1.0.0", "城市样式详情与宣告窗口"),
    "RhineDepartment-UI-v1.0": ("1.0.0", "莱茵生命公共科室选择窗口"),
    "Card-Picker-UI-v1": ("1.0.0", "通用卡牌选择窗口"),
    "Option-Picker-UI-v1": ("1", "单选与重复出售窗口；沿用包名版本标识"),
    "MainGameUI-CooperationMarkers-v1": ("1", "标记补充包，与主包重复部分按字节去重"),
    "MainGameUI-Topbar-v2.2": ("2.2", "历史顶栏补充包；新版主界面不再采用"),
}
PATCH = "FrontierUI-v3-Patch"
BASE = "MainGameUI-v2.2"


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def dump(path: Path, value) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.write("\n")


def image_info(data: bytes, suffix: str):
    if suffix == ".png":
        if not data.startswith(b"\x89PNG\r\n\x1a\n"):
            raise ValueError("PNG 签名错误")
        width, height = struct.unpack(">II", data[16:24])
        offset, compressed, ended = 8, [], False
        while offset + 12 <= len(data):
            length = struct.unpack(">I", data[offset:offset + 4])[0]
            kind = data[offset + 4:offset + 8]
            payload = data[offset + 8:offset + 8 + length]
            if offset + length + 12 > len(data):
                raise ValueError("PNG 数据截断")
            expected = struct.unpack(">I", data[offset + 8 + length:offset + 12 + length])[0]
            if zlib.crc32(kind + payload) & 0xFFFFFFFF != expected:
                raise ValueError("PNG 块 CRC 错误")
            if kind == b"IDAT":
                compressed.append(payload)
            offset += length + 12
            if kind == b"IEND":
                ended = True
                break
        if not ended or not compressed:
            raise ValueError("PNG 缺少 IEND 或 IDAT")
        zlib.decompress(b"".join(compressed))
        return {"width": width, "height": height, "pngColorType": data[25]}
    if suffix in (".jpg", ".jpeg"):
        if data[:2] != b"\xff\xd8":
            raise ValueError("JPEG 签名错误")
        pos = 2
        while pos < len(data):
            if data[pos] != 255:
                pos += 1
                continue
            while pos < len(data) and data[pos] == 255:
                pos += 1
            marker = data[pos]
            pos += 1
            if marker in (0xD8, 0xD9) or 0xD0 <= marker <= 0xD7:
                continue
            size = int.from_bytes(data[pos:pos + 2], "big")
            if marker in (0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7,
                          0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF):
                return {"width": int.from_bytes(data[pos + 5:pos + 7], "big"),
                        "height": int.from_bytes(data[pos + 3:pos + 5], "big")}
            if size < 2:
                break
            pos += size
        raise ValueError("JPEG 未找到尺寸")
    return None


def destination(package: str, rel: str, patch_assets: dict):
    p = PurePosixPath(rel)
    if p.suffix.lower() not in (".png", ".jpg", ".jpeg"):
        return None
    if package == PATCH and rel in patch_assets:
        asset_id = patch_assets[rel]
        group = "兼容组件" if asset_id in ("turn-tag", "redzone-paper") else "新版组件"
        return f"资产/{group}/Frontier-3.1/{rel}", (
            "3.0 兼容素材，3.1 主预览未采用" if group == "兼容组件" else "新版通用组件")
    if len(p.parts) == 2 and p.parts[0] in ("preview", "examples"):
        return f"预览/{package}/{p.name}", (
            "新版主界面实际素材拼装" if package == PATCH else
            "视觉样例，非实现截图" if package == "Option-Picker-UI-v1" else "原包历史外观或窗口拼装预览")
    if package == BASE and rel.startswith("04-markers/") and p.stem != "gallery":
        return f"资产/沿用标记/{p.name}", "沿用玩家颜色、组合与状态叠层"
    if package == BASE and rel.startswith("06-icons/"):
        return f"资产/沿用图标/{p.name}", "沿用图标；同用途已有新版时优先新版"
    if package == BASE and rel.startswith("03-controls/") and not p.name.startswith("button-"):
        return f"资产/沿用控件/{p.name}", "旧版页签、步进器或滚动条；未交付对应新版专用状态图"
    if package == "EnterprisePicker-v1.0" and rel.startswith("assets/"):
        return f"资产/窗口专用/选择状态/{p.name}", "沿用可选、选中、不可选状态叠层"
    if package in ("Card-Picker-UI-v1", "Option-Picker-UI-v1") and rel.startswith("assets/"):
        if p.name in ("window.png", "card-slot.png", "button-primary.png", "button-secondary.png"):
            return None
        if p.name == "sample-card.jpg":
            return f"参考/示例内容/{p.name}", "卡牌排布校准样本，不是运行时候选"
        if p.name.startswith("resource-"):
            return f"资产/原始内容/资源/{p.name}", "原始资源图标"
        return f"资产/窗口专用/卡牌与选项/{p.name}", "沿用窗口专用素材；仍需原包透明区域参数"
    if p.parts[0] in ("07-originals", "originals"):
        if p.name == "main-background.png":
            return f"参考/示例内容/{p.name}", "历史界面背景示意图，不能作为游戏 HUD 底图"
        return f"资产/原始内容/{package}/{p.name}", "原始卡面、图集、城市、地图或图标；实际内容版本由项目确定"
    return None


def safe_path(root: Path, relative: str) -> Path:
    p = PurePosixPath(relative)
    if p.is_absolute() or any(x in ("..", "") or ":" in x for x in p.parts):
        raise ValueError(f"不安全路径：{relative}")
    target = (root / Path(*p.parts)).resolve()
    if not target.is_relative_to(root.resolve()) or target == root.resolve():
        raise ValueError(f"路径越界：{relative}")
    return target


def build(output: Path):
    if output.exists():
        raise FileExistsError(f"整理目录已存在，不覆盖：{output}；请使用 --verify")
    if not output.is_relative_to(SOURCE.resolve()) or output == SOURCE.resolve():
        raise ValueError("新增整理目录必须位于 UI素材 内，且不能为素材根目录")
    actual = {p.stem for p in SOURCE.glob("*.zip")}
    if actual != set(PACKAGES):
        raise ValueError(f"素材包发生变化，请先更新版本决策：{actual ^ set(PACKAGES)}")
    preserved = [p for p in SOURCE.iterdir() if p.is_file()]
    preserved += list((PROJECT / "docs").rglob("*.md"))
    preserved += list((PROJECT / "prompt").rglob("*.md"))
    preserved += [PROJECT / "AGENTS.md", PROJECT / "README.md"]
    baseline = {p.relative_to(PROJECT).as_posix(): sha(p.read_bytes()) for p in preserved}
    inventories, package_rows, members = [], [], {}
    by_hash = defaultdict(list)
    claim_checks, errors, images_checked = 0, [], 0
    image_cache = {}
    for package, (version, purpose) in PACKAGES.items():
        archive = SOURCE / f"{package}.zip"
        payload = archive.read_bytes()
        rows = {}
        with ZipFile(archive) as z:
            names = [i for i in z.infolist() if not i.is_dir()]
            roots = {PurePosixPath(i.filename).parts[0] for i in names}
            if len(roots) != 1:
                raise ValueError(f"压缩包根目录不唯一：{package}")
            prefix = next(iter(roots)) + "/"
            seen = set()
            for info in names:
                rel = info.filename[len(prefix):]
                safe_path(output, rel)
                if rel.casefold() in seen:
                    raise ValueError(f"压缩包内重名：{package}/{rel}")
                seen.add(rel.casefold())
                data = z.read(info)
                digest = sha(data)
                item = {"package": package, "member": info.filename, "relativePath": rel,
                        "bytes": len(data), "sha256": digest}
                suffix = PurePosixPath(rel).suffix.lower()
                if suffix in (".png", ".jpg", ".jpeg"):
                    if digest not in image_cache:
                        image_cache[digest] = image_info(data, suffix)
                    item["image"] = image_cache[digest]
                    images_checked += 1
                if suffix == ".json":
                    json.loads(data)
                rows[rel] = data
                inventories.append(item)
                by_hash[digest].append(item)
        members[package] = rows
        package_rows.append({"archive": archive.name, "package": package,
                             "archiveSha256": sha(payload), "archiveBytes": len(payload),
                             "actualVersion": version, "purpose": purpose,
                             "fileCount": len(rows), "uncompressedBytes": sum(map(len, rows.values()))})
        if "asset-manifest.json" in rows:
            manifest = json.loads(rows["asset-manifest.json"])
            claims = manifest.get("files", list(manifest.get("assets", {}).values()))
            for claim in claims:
                rel, digest = claim.get("path"), claim.get("sha256")
                if not rel or not digest:
                    continue
                claim_checks += 1
                if rel not in rows or sha(rows[rel]) != digest.lower():
                    errors.append(f"{package}/{rel} 的包内 SHA-256 声明不符")
    if errors:
        raise ValueError("\n".join(errors))

    manifest = json.loads(members[PATCH]["asset-manifest.json"])
    patch_assets = {v["path"]: k for k, v in manifest["assets"].items()}
    output.mkdir(parents=True)
    catalog, copied_hashes, generated = [], {}, []

    def copy(relative, data, kind):
        target = safe_path(output, relative)
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("xb") as stream:
            stream.write(data)
        generated.append({"path": relative, "sha256": sha(data), "bytes": len(data), "kind": kind})

    for package, rows in members.items():
        for rel, data in rows.items():
            choice = destination(package, rel, patch_assets)
            if choice:
                target, usage = choice
                digest = sha(data)
                if digest in copied_hashes:
                    copied_hashes[digest]["uses"].append({"package": package, "path": rel, "usage": usage})
                    continue
                # 同名异内容永不静默覆盖。
                if (output / target).exists():
                    p = PurePosixPath(target)
                    target = str(p.with_name(f"{p.stem}-{digest[:8]}{p.suffix}"))
                copy(target, data, "整理图像")
                item = {"path": target, "sha256": digest, "bytes": len(data),
                        "image": image_cache[digest], "usage": usage,
                        "uses": [{"package": package, "path": rel, "usage": usage}],
                        "sources": [{"package": x["package"], "member": x["member"]}
                                    for x in by_hash[digest]]}
                catalog.append(item)
                copied_hashes[digest] = item
            if rel.endswith(".json"):
                copy(f"原包参数/{package}/{rel}", data, "原包 JSON 原样副本")

    for item in inventories:
        found = copied_hashes.get(item["sha256"])
        if found:
            item["catalogPath"] = found["path"]
        else:
            item["storage"] = "保留于源 ZIP；未选入整理图像库"
    v3_index = {}
    for asset_id, spec in manifest["assets"].items():
        data = members[PATCH][spec["path"]]
        actual_size = image_cache[sha(data)]
        if [actual_size["width"], actual_size["height"]] != spec["viewBox"]:
            raise ValueError(f"新版组件尺寸不符：{asset_id}")
        left, top, right, bottom = spec["slices"]
        v3_index[asset_id] = {"path": copied_hashes[sha(data)]["path"],
                              "sourcePackage": PATCH, "sourceSpec": spec,
                              "unityBorderLeftBottomRightTop": [left, bottom, right, top]}
    skin = json.loads(members[PATCH]["skin-patch.json"])
    for group in ("oldRoleToNewAssetIds", "windowRoleToNewAssetIds", "newRoles"):
        for ids in skin[group].values():
            if any(i not in v3_index for i in ids):
                raise ValueError("补丁引用不存在的组件 ID")
    duplicates = [{"sha256": digest, "bytes": group[0]["bytes"],
                   "sources": [{"package": x["package"], "member": x["member"]} for x in group]}
                  for digest, group in by_hash.items() if len(group) > 1]
    all_png_count = sum(1 for x in inventories if x["relativePath"].endswith(".png"))
    issues = [
        {"id": "version-frontier", "finding": "补丁包名为 v3，组件清单和顶栏为 3.1；layout-patch.json 仍标 3.0.0。", "decision": "采用 3.1 组件及顶栏；主界面区域采用现存布局补丁，不改写源版本字段。"},
        {"id": "version-enterprise", "finding": "企业包文件名 v1.0，包内 asset-manifest.json 与说明实际为 1.0.1。", "decision": "目录保留源包名，版本清单标记 1.0.1。"},
        {"id": "frontier-count", "finding": f"补丁 verification.json 声明 assetCount=51，实际 asset-manifest.json 为 {len(v3_index)} 项。", "decision": "整理数量以实际成员及组件清单为准，原 QA 文件保留。"},
        {"id": "modal-bottom-overlap", "finding": "历史设施、企业、城市样式、科室窗口主体延伸到 y=1012，新底栏从 y=986 开始。", "decision": "列为适配事项；未制作新弹窗矩形或新版弹窗预览，不宣称已经完成。"},
        {"id": "preview-coverage", "finding": "补丁仅交付新版主界面预览；其他窗口仍是旧主题。包内浏览器验证均未完成。", "decision": "预览按版本分类，素材整理不代表 Unity 接入、浏览器交互或规则验收通过。"},
    ]
    dump(output / "清单/源包清单.json", package_rows)
    dump(output / "清单/源包文件索引.json", inventories)
    dump(output / "清单/资产索引.json", catalog)
    dump(output / "清单/重复文件来源.json", duplicates)
    dump(output / "清单/新版组件索引.json", {"version": "3.1", "assets": v3_index})
    dump(output / "清单/版本决策.json", {"patch": skin, "issues": issues,
         "scope": "资产整理与接入参考；未修改 Unity 场景、Prefab 或原有文档"})
    dump(output / "清单/源文件保全.json", baseline)
    dump(output / "清单/整理文件校验值.json", generated)
    report = {"archiveCount": len(package_rows), "archiveMemberCount": len(inventories),
              "copiedUniqueImages": len(catalog), "copiedJsonFiles": sum(x["kind"] != "整理图像" for x in generated),
              "v3Components": len(v3_index), "archiveHashClaimsChecked": claim_checks,
              "rasterMembersChecked": images_checked, "uniqueRasterFilesChecked": len(image_cache),
              "pngMembersChecked": all_png_count, "duplicateHashGroups": len(duplicates),
              "copiedImageBytes": sum(x["bytes"] for x in catalog),
              "sourceDocumentsAndArchivesPreserved": True,
              "browserTestsRun": False, "unityAssetsChanged": False,
              "errors": [], "notes": "PNG 检查签名、尺寸、全部块 CRC 及 IDAT 解压；JPEG 检查签名和尺寸；ZIP 读取执行 CRC 校验。"}
    dump(output / "清单/整理校验报告.json", report)
    verify(output)
    print(json.dumps(report, ensure_ascii=False, indent=2))


def verify(output: Path):
    baseline = json.loads((output / "清单/源文件保全.json").read_text(encoding="utf-8"))
    # 提示词分类是显式路径迁移；保留整理时的原始快照及 SHA-256，不能重建基线。
    relocation_file = PROJECT / "prompt/文档分类迁移记录-2026-09-24.json"
    relocations = {}
    if relocation_file.exists():
        record = json.loads(relocation_file.read_text(encoding="utf-8"))
        if record["version"] != 1 or record["count"] != len(record["files"]):
            raise ValueError("提示词迁移记录格式错误")
        for item in record["files"]:
            source, destination = item["source"], item["destination"]
            if (source in relocations or not source.startswith("prompt/")
                    or not destination.startswith("prompt/Lua架构迁移/")
                    or PurePosixPath(source).name != PurePosixPath(destination).name):
                raise ValueError(f"提示词迁移记录路径错误：{source}")
            data = safe_path(PROJECT, destination).read_bytes()
            if len(data) != item["bytes"] or sha(data) != item["sha256"]:
                raise ValueError(f"分类迁移后的原文发生变化：{destination}")
            relocations[source] = item
    relocated_count = 0
    for rel, digest in baseline.items():
        original = safe_path(PROJECT, rel)
        if original.is_file() and sha(original.read_bytes()) == digest:
            continue
        relocated = relocations.get(rel)
        if relocated and relocated["sha256"] == digest:
            relocated_count += 1
        else:
            raise ValueError(f"原文件发生变化：{rel}")
    files = json.loads((output / "清单/整理文件校验值.json").read_text(encoding="utf-8"))
    for item in files:
        if sha(safe_path(output, item["path"]).read_bytes()) != item["sha256"]:
            raise ValueError(f"整理文件变化：{item['path']}")
    catalog = json.loads((output / "清单/资产索引.json").read_text(encoding="utf-8"))
    if len({x["sha256"] for x in catalog}) != len(catalog):
        raise ValueError("整理图像库仍有重复字节")
    print(f"校验通过：{len(baseline)} 个原文件内容未变（其中 {relocated_count} 个已分类迁移），"
          f"{len(files)} 个整理副本与来源一致。")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--verify", action="store_true", help="只校验已有整理目录，不写文件")
    args = parser.parse_args()
    target = args.output.resolve()
    if args.verify:
        verify(target)
    else:
        build(target)
