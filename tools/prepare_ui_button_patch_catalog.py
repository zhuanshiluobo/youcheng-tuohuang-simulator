"""新增主界面按钮 3.2 素材目录，或只读校验已有目录；不安装补丁、不操作 Unity。"""

from __future__ import annotations

import argparse
import json
from pathlib import Path, PurePosixPath
from zipfile import ZipFile

from prepare_ui_asset_catalog import dump, image_info, sha


PROJECT = Path(__file__).resolve().parents[1]
SOURCE = PROJECT / "游城拓荒" / "UI素材"
DEFAULT_OUTPUT = SOURCE / "整理增量-2026-09-25-按钮3.2"
PACKAGE = SOURCE / "MainButtons-v3.2-Patch.zip"
NOTES = SOURCE / "MainButtons-v3.2-Notes.md"
BASELINE = SOURCE / "FrontierUI-v3-Patch.zip"
PREFIX = "MainButtons-v3.2-Patch/"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def relative_path(value: str) -> PurePosixPath:
    path = PurePosixPath(value)
    require(bool(value) and not path.is_absolute() and ".." not in path.parts
            and "\\" not in value and ":" not in value, f"不安全的包内路径：{value}")
    return path


def collect():
    files = {}
    with ZipFile(PACKAGE) as archive:
        for member in archive.infolist():
            if member.is_dir():
                continue
            require(member.filename.startswith(PREFIX), f"不符合包前缀：{member.filename}")
            name = member.filename[len(PREFIX):]
            relative_path(name)
            require(name.casefold() not in {p.casefold() for p in files}, f"重复路径：{name}")
            files[name] = archive.read(member)

    def read_json(name):
        return json.loads(files[name])

    package_files = read_json("package-files.json")["files"]
    require(len({row["path"] for row in package_files}) == len(package_files), "源包清单含重复项")
    require({row["path"] for row in package_files} == set(files) - {"package-files.json"},
            "源包清单与实际文件不一致")
    for row in package_files:
        data = files[row["path"]]
        require(len(data) == row["bytes"] and sha(data) == row["sha256"],
                f"源包文件校验失败：{row['path']}")

    patch = read_json("patch-manifest.json")
    destinations = set()
    for row in patch["files"]:
        relative_path(row["destination"])
        require(row["destination"].casefold() not in destinations, "预览安装目标重复")
        destinations.add(row["destination"].casefold())
        data = files[row["source"]]
        require(len(data) == row["bytes"] and sha(data) == row["sha256"],
                f"预览安装清单源文件校验失败：{row['source']}")

    baseline_checks = []
    with ZipFile(BASELINE) as archive:
        for name, expected in patch["requiredBaselineHashes"].items():
            actual = sha(archive.read("FrontierUI-v3-Patch/" + name))
            require(actual == expected, f"基线文件不匹配：{name}")
            baseline_checks.append({"path": name, "sha256": actual, "matched": True})
    require(NOTES.read_bytes() == files["BUTTON_PATCH.md"], "外部说明与包内说明不同")

    inventory = []
    for name, data in sorted(files.items()):
        row = {"zipMember": PREFIX + name, "copy": "原包/" + name,
               "bytes": len(data), "sha256": sha(data)}
        if name.endswith(".png"):
            row["png"] = image_info(data, ".png")
        inventory.append(row)

    assets = read_json("assets/button-assets.json")["assets"]
    role_source = read_json("button-roles.json")
    bases = {k: v for k, v in assets.items() if v["role"] == "button-background"}
    icons = {k: v for k, v in assets.items() if v["role"] == "button-icon"}
    require(len(bases) == 18 and len(icons) == 33 and len(role_source["roles"]) == 11,
            "按钮补丁规模与 3.2 说明不符")
    for asset_id, asset in assets.items():
        for key in ("path", "path2x", "svg"):
            require(asset[key] in files, f"缺少贴图：{asset_id}/{key}")
        for key, scale in (("path", 1), ("path2x", 2)):
            dimensions = image_info(files[asset[key]], ".png")
            require([dimensions["width"], dimensions["height"]] ==
                    [n * scale for n in asset["viewBox"]], f"贴图尺寸错误：{asset_id}/{key}")

    role_index = []
    for role in role_source["roles"]:
        states = {}
        for base_id, base in bases.items():
            if base["family"] != role["family"]:
                continue
            icon_id = "icon-" + role["glyph"] + "-" + base["recommendedInk"]
            icon = icons[icon_id]
            left, top, right, bottom = base["slices"]
            states[base["state"]] = {
                "baseId": base_id, "iconId": icon_id, "ink": base["recommendedInk"],
                "base": {k: "原包/" + base[k] for k in ("path", "path2x", "svg")},
                "icon": {k: "原包/" + icon[k] for k in ("path", "path2x", "svg")},
                "unityBorderOrder": ["left", "bottom", "right", "top"],
                "unityBorder1x": [left, bottom, right, top],
                "unityBorder2x": [n * 2 for n in (left, bottom, right, top)],
            }
        role_index.append({"sourceRole": role, "states": states})

    snapshots = [{"path": path.relative_to(PROJECT).as_posix(),
                  "bytes": path.stat().st_size, "sha256": sha(path.read_bytes())}
                 for path in (PACKAGE, NOTES, BASELINE)]
    report = {
        "日期": "2026-09-25", "阶段": "源素材整理；尚未导入 Unity",
        "源包文件数": len(files), "源包清单校验通过": len(package_files),
        "安装清单源文件校验通过": len(patch["files"]),
        "安装脚本已执行": False, "浏览器交互已验证": False, "Unity接入已验证": False,
        "基线校验": baseline_checks, "外部说明与包内说明逐字节一致": True,
        "PNG签名CRC与解压通过": sum("png" in row for row in inventory),
        "按钮数": len(role_index), "底板系列数": len({a["family"] for a in bases.values()}),
        "底板状态数": len(bases), "图标配色数": len(icons), "逻辑素材数": len(assets),
        "运行时源素材文件数": len(assets) * 3,
        "说明": "源包 QA.json 保留其原始声明；本报告不将源包自报验收算作项目验收。",
    }
    indices = {
        "源文件快照.json": snapshots, "源包文件索引.json": inventory,
        "按钮贴图索引.json": {"version": "3.2", "canvas": role_source["canvas"],
                         "说明": "坐标为基准构图；label 为素材参考文案，previewAction 非游戏命令。",
                         "roles": role_index},
        "校验报告.json": report,
    }
    return files, indices


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--verify", action="store_true", help="只读校验；不会改写文件")
    args = parser.parse_args()
    output = args.output.resolve()
    files, indices = collect()
    if args.verify:
        for name, data in files.items():
            require((output / "原包" / name).read_bytes() == data, f"整理副本不同：{name}")
        for name, data in indices.items():
            require(json.loads((output / "清单" / name).read_text(encoding="utf-8")) == data,
                    f"整理清单不同：{name}")
        print(f"校验通过：{len(files)} 份原包副本、{len(indices)} 份整理清单；源包及基线校验通过。")
        return
    require(not output.exists(), f"目录已存在，只允许 --verify：{output}")
    output.mkdir(parents=True, exist_ok=False)
    for name, data in files.items():
        target = output / "原包" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("xb") as stream:
            stream.write(data)
    for name, data in indices.items():
        dump(output / "清单" / name, data)
    print(f"已新增：{output}\n源包文件 {len(files)}，整理清单 {len(indices)}；未运行补丁安装脚本。")


if __name__ == "__main__":
    main()
