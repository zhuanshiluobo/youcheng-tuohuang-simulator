"""新增效果条目 3.4／主框 3.5 素材目录，或只读校验；不执行安装脚本。"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from zipfile import ZipFile

from prepare_ui_asset_catalog import dump, image_info, sha
from prepare_ui_button_patch_catalog import relative_path, require


PROJECT = Path(__file__).resolve().parents[1]
SOURCE = PROJECT / "游城拓荒" / "UI素材"
PACKAGES = {
    "effects": ("EffectRows-v3.4-Patch", "EffectRows-v3.4-Patch", "EffectRows-v3.4-Notes.md",
                "EFFECT_ROW_PATCH.md", "effect-assets.json", "整理增量-2026-09-25-效果条目3.4"),
    "frames": ("MainFrame-v3.5-Patch", "main-frame-patch-v3.5", "MainFrame-v3.5-Notes.md",
               "FRAME_PATCH.md", "frame-assets.json", "整理增量-2026-09-25-主框3.5"),
}


def read_zip(path, prefix):
    files, seen = {}, set()
    with ZipFile(path) as archive:
        for member in archive.infolist():
            if member.is_dir():
                continue
            require(member.filename.startswith(prefix + "/"), f"包前缀不符：{member.filename}")
            name = member.filename[len(prefix) + 1:]
            relative_path(name)
            require(name.casefold() not in seen, f"重复包内路径：{name}")
            seen.add(name.casefold())
            files[name] = archive.read(member)
    return files


def check_rows(files, rows, path_key):
    seen = set()
    for row in rows:
        name = row[path_key]
        relative_path(name)
        require(name not in seen, f"清单源路径重复：{name}")
        seen.add(name)
        data = files[name]
        require(sha(data) == row["sha256"], f"哈希不符：{name}")
        if "bytes" in row:
            require(len(data) == row["bytes"], f"文件大小不符：{name}")


def collect(kind):
    package, prefix, note, internal_note, asset_file, folder = PACKAGES[kind]
    files = read_zip(SOURCE / (package + ".zip"), prefix)
    read = lambda name: json.loads(files[name])
    require((SOURCE / note).read_bytes() == files[internal_note], "包外说明与包内说明不一致")
    patch = read("patch-manifest.json")
    check_rows(files, patch["files"], "source")
    destinations = [row["destination"].casefold() for row in patch["files"]]
    require(len(destinations) == len(set(destinations)), "安装清单目标重复")
    for row in patch["files"]:
        relative_path(row["destination"])

    baseline_checks, additional_sources = [], []
    if kind == "effects":
        package_rows = read("package-files.json")["files"]
        check_rows(files, package_rows, "path")
        require({r["path"] for r in package_rows} == set(files) - {"package-files.json"},
                "源包文件清单与 ZIP 不一致")
        baseline = read_zip(SOURCE / "FrontierUI-v3-Patch.zip", "FrontierUI-v3-Patch")
        button = read_zip(SOURCE / "MainButtons-v3.2-Patch.zip", "MainButtons-v3.2-Patch")
        button_patch = json.loads(button["patch-manifest.json"])
        check_rows(button, button_patch["files"], "source")
        for name, expected in button_patch["requiredBaselineHashes"].items():
            require(sha(baseline[name]) == expected, f"3.1 基线不符：{name}")
        # 仅在内存按清单查询合成后的字节，不安装或执行原包脚本。
        for row in button_patch["files"]:
            baseline[row["destination"]] = button[row["source"]]
        accepted = next(b for b in patch["acceptedBaselines"] if b["version"] == "3.2")
        for name, expected in accepted["hashes"].items():
            actual = sha(baseline[name])
            require(actual == expected, f"3.2 基线不符：{name}")
            baseline_checks.append({"path": name, "sha256": actual, "matched": True})
        additional_sources = ["FrontierUI-v3-Patch.zip", "MainButtons-v3.2-Patch.zip"]
    else:
        package_rows = []
        for line in files["SHA256SUMS.txt"].decode("utf-8").splitlines():
            digest, name = line.split("  ", 1)
            package_rows.append({"path": name, "sha256": digest})
        check_rows(files, package_rows, "path")
        require({r["path"] for r in package_rows} == set(files) - {"SHA256SUMS.txt"},
                "SHA256SUMS 与 ZIP 不一致")

    inventory = []
    for name, data in sorted(files.items()):
        row = {"zipMember": prefix + "/" + name, "copy": "原包/" + name,
               "bytes": len(data), "sha256": sha(data)}
        if name.endswith(".png"):
            row["png"] = image_info(data, ".png")
        inventory.append(row)

    catalog = read("assets/" + asset_file)
    assets = catalog["assets"]
    index = []

    def asset_entry(asset_id, asset, part_of=None):
        second_key = "path2x" if kind == "effects" else "png2x"
        paths = {key: "原包/" + asset[key] for key in ("path", second_key, "svg")}
        for key in paths:
            require(asset[key] in files, f"素材缺失：{asset_id}/{key}")
        logical_size = asset.get("logicalSize", asset.get("viewBox"))
        for key, scale in (("path", 1), (second_key, 2)):
            info = image_info(files[asset[key]], ".png")
            require([info["width"], info["height"]] == [n * scale for n in logical_size],
                    f"素材尺寸不符：{asset_id}/{key}")
        source = {k: v for k, v in asset.items() if k != "parts"}
        row = {"id": asset_id, "partOf": part_of, "source": source, "paths": paths}
        if "slices" in asset:
            left, top, right, bottom = asset["slices"]
            row.update({"unityBorderOrder": ["left", "bottom", "right", "top"],
                        "unityBorder1x": [left, bottom, right, top],
                        "unityBorder2x": [n * 2 for n in (left, bottom, right, top)]})
        index.append(row)

    for asset_id, asset in assets.items():
        asset_entry(asset_id, asset)
        for part_id, part in asset.get("parts", {}).items():
            asset_entry(asset_id + "/" + part_id, part, asset_id)

    marker_count = 0
    if kind == "effects":
        markers = read("assets/marker-combinations.json")["combinations"]
        require({m["mask"] for m in markers} == set(range(1, 16)), "标记组合不完整")
        for marker in markers:
            path = marker["path"]
            info = image_info(files[path], ".png")
            require([info["width"], info["height"]] == [64, 64], "标记尺寸不符")
            index.append({"id": "marker-" + str(marker["mask"]), "source": marker,
                          "paths": {"path": "原包/" + path}})
        marker_count = len(markers)
    else:
        overrides = read("integration/catalog-overrides.json")["assets"]
        require(set(overrides) == set(assets), "3.5 覆盖范围不符")
        for asset_id, override in overrides.items():
            original = assets[asset_id]
            require(override["viewBox"] == original["pixelDimensions"]["2x"] and
                    override["slices"] == original["borders2x"] and
                    override["targetSlices"] == original["targetSlices"],
                    f"安装用 2x 映射不符：{asset_id}")

    snapshots = [{"path": (SOURCE / name).relative_to(PROJECT).as_posix(),
                  "bytes": (SOURCE / name).stat().st_size,
                  "sha256": sha((SOURCE / name).read_bytes())}
                 for name in [package + ".zip", note] + additional_sources]
    report = {
        "日期": "2026-09-25", "版本": catalog["version"],
        "阶段": "源素材整理；Unity 接入未验证", "源包文件数": len(files),
        "源包清单哈希通过": len(package_rows), "安装清单源哈希通过": len(patch["files"]),
        "PNG签名CRC及解压通过": sum("png" in r for r in inventory),
        "包外包内说明逐字节一致": True, "顶层逻辑素材数": len(assets),
        "八片拆分逻辑素材数": sum(len(a.get("parts", {})) for a in assets.values()),
        "标记PNG数": marker_count,
        "运行时源素材文件数": sum(len(r["paths"]) for r in index),
        "3.2基线内存核对": baseline_checks,
        "安装脚本已执行": False, "浏览器交互已验证": False, "Unity接入已验证": False,
        "说明": "源包 QA 保留原声明；PNG 校验是结构与压缩数据检查，不等于实际引擎画面验证。",
    }
    indices = {"源文件快照.json": snapshots, "源包文件索引.json": inventory,
               "素材索引.json": {"version": catalog["version"], "assets": index},
               "校验报告.json": report}
    return SOURCE / folder, files, indices


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", choices=(*PACKAGES, "all"), default="all")
    parser.add_argument("--verify", action="store_true", help="只读校验已有整理目录")
    args = parser.parse_args()
    kinds = list(PACKAGES) if args.package == "all" else [args.package]
    collected = [collect(kind) for kind in kinds]
    if not args.verify:
        for output, _, _ in collected:
            require(not output.exists(), f"目录已存在，只允许 --verify：{output}")
    for output, files, indices in collected:
        if args.verify:
            for name, data in files.items():
                require((output / "原包" / name).read_bytes() == data, f"副本不一致：{name}")
            for name, data in indices.items():
                require(json.loads((output / "清单" / name).read_text(encoding="utf-8")) == data,
                        f"清单不一致：{name}")
            print(f"校验通过：{output.name}，{len(files)} 份副本、{len(indices)} 份清单。")
            continue
        output.mkdir(parents=True, exist_ok=False)
        for name, data in files.items():
            target = output / "原包" / name
            target.parent.mkdir(parents=True, exist_ok=True)
            with target.open("xb") as stream:
                stream.write(data)
        for name, data in indices.items():
            dump(output / "清单" / name, data)
        print(f"新增：{output.name}，{len(files)} 份原包副本、{len(indices)} 份清单。")


if __name__ == "__main__":
    main()
