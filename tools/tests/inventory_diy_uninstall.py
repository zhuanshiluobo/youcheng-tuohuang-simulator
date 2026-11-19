"""只读核验隔离卸载夹具并逐项列出测试将涉及的绝对路径。"""

import argparse
import hashlib
import json
import os
from pathlib import Path


def reject_links(path: Path, stop: Path) -> None:
    current = path
    while True:
        info = current.lstat()
        if current.is_symlink() or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError(f"禁止重解析点：{current}")
        if current == stop:
            return
        if current.parent == current or not current.is_relative_to(stop):
            raise ValueError(f"路径越界：{current}")
        current = current.parent


def digest(path: Path) -> str:
    sha = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            sha.update(block)
    return sha.hexdigest()


def inventory(root: Path, allowed: Path) -> list:
    if not root.is_absolute() or root == allowed or not root.is_relative_to(allowed):
        raise ValueError(f"根目录不在独立测试范围：{root}")
    reject_links(root, allowed)
    rows = []
    for directory, dirs, files in os.walk(root, topdown=True, followlinks=False):
        base = Path(directory)
        for path in [base] + [base / name for name in dirs + files]:
            reject_links(path, allowed)
        rows.append({"path": str(base), "kind": "directory"})
        for name in files:
            path = base / name
            if not path.is_file():
                raise ValueError(f"不是普通文件：{path}")
            rows.append({"path": str(path), "kind": "file", "bytes": path.stat().st_size, "sha256": digest(path)})
    return sorted(rows, key=lambda row: row["path"].casefold())


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--verify-case", choices=("default", "selected"))
    parser.add_argument("--verify-preserved", action="store_true")
    args = parser.parse_args()
    project = Path(__file__).resolve().parents[2]
    allowed = project / "TestResults"
    base = args.base
    if not base.is_absolute() or base == allowed or not base.is_relative_to(allowed):
        raise ValueError("夹具根必须是 TestResults 内的绝对路径。")
    reject_links(base, project)
    if not args.output.is_absolute() or args.output.parent != base:
        raise ValueError("清单必须位于夹具根中的准确文件。")
    if args.verify_case or args.verify_preserved:
        reject_links(args.output, project)
        saved = json.loads(args.output.read_text(encoding="utf-8"))
        name = "default" if args.verify_preserved else args.verify_case
        case = saved["cases"][name]
        installer = Path(case["installer"])
        reject_links(installer, allowed)
        if saved.get("fixture") != str(base) or digest(installer) != case["installerSha256"]:
            raise ValueError("夹具路径或安装器身份与原清单不一致。")
        for key, entries in case["roots"].items():
            if args.verify_preserved and key == "installed-game":
                continue
            if inventory(base / name / key, allowed) != entries:
                raise ValueError(f"卸载前内容与原逐项清单不一致：{base / name / key}")
        if not args.verify_preserved:
            for target in case["absentShortcutTargets"]:
                if Path(target).exists():
                    raise ValueError(f"清单中不存在的快捷方式已出现：{target}")
        print(f"已逐项复核 {name} 的绝对路径、重解析点、文件长度和 SHA256。")
        return
    if args.output.exists():
        raise ValueError("禁止覆盖已有逐项清单。")
    expected = json.loads((project / "TestResults/DIY-installer-tool-c1-r1-20261111/game-manifest.json").read_text(encoding="utf-8-sig"))
    data = {"fixture": str(base), "cases": {}}
    for name in ("default", "selected"):
        case = base / name
        receipt = json.loads((case / "installer/安装包构建回执.json").read_text(encoding="utf-8-sig"))
        installer = Path(receipt["installer"])
        reject_links(installer, allowed)
        if (not receipt.get("fixtureUninstallAllowed") or not receipt.get("fixtureRegistryWritesDisabled") or
                receipt.get("fixtureGameTarget") != str(case / "installed-game") or
                receipt.get("fixtureDataRoot") != str(case / "data") or
                receipt.get("fixtureLegacyDataTarget") != str(case / "legacy-data") or
                digest(installer).lower() != receipt["sha256"].lower()):
            raise ValueError(f"测试安装器身份不符：{name}")
        roots = {key: inventory(case / key, allowed) for key in ("installed-game", "data", "legacy-data")}
        game = {Path(row["path"]).relative_to(case / "installed-game").as_posix(): row for row in roots["installed-game"] if row["kind"] == "file"}
        expected_files = {row["path"]: row for row in expected["files"]}
        uninstaller = "卸载游城拓荒.exe"
        if set(game) != set(expected_files) | {uninstaller}:
            raise ValueError(f"安装文件与冻结清单不一致：{name}")
        for relative, original in expected_files.items():
            actual = game[relative]
            if actual["bytes"] != original["bytes"] or actual["sha256"] != original["sha256"]:
                raise ValueError(f"安装文件被修改：{actual['path']}")
        data["cases"][name] = {
            "installer": str(installer), "installerSha256": digest(installer),
            "uninstaller": str(case / "installed-game" / uninstaller),
            "deleteRoots": [str(case / root) for root in (("installed-game",) if name == "default" else ("installed-game", "data", "legacy-data"))],
            "preserveRoots": [str(case / root) for root in (("data", "legacy-data") if name == "default" else ())],
            "roots": roots,
            "absentShortcutTargets": [str(case / "installed-game/fixture-shortcuts/desktop.lnk"), str(case / "installed-game/fixture-shortcuts/start-menu.lnk")],
        }
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(data, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    for name, case in data["cases"].items():
        print(name, "删除范围", case["deleteRoots"], "保留范围", case["preserveRoots"],
              "已核验条目", sum(len(rows) for rows in case["roots"].values()))
    print("逐项清单", args.output)


if __name__ == "__main__":
    main()
