"""Audit UI-002 file copies, Unity GUIDs, borders and serialized references."""
from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "游城拓荒/UI素材"
CATALOG = SOURCE / "整理版-2026-09-24"
DEST = ROOT / "Assets/YC/Presentation/Ui002"
REPORT = ROOT / "prompt/UI换新/执行记录/2026-09-24_UI-002_资产映射.json"


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def guid(path: Path) -> str:
    meta = path.with_name(path.name + ".meta")
    text = meta.read_text(encoding="utf-8")
    match = re.search(r"(?m)^guid: ([0-9a-f]{32})$", text)
    if not match:
        raise ValueError(f"Missing GUID: {meta}")
    return match.group(1)


def border(path: Path) -> list[int]:
    text = path.with_name(path.name + ".meta").read_text(encoding="utf-8")
    match = re.search(r"(?m)^  spriteBorder: \{x: ([\d.]+), y: ([\d.]+), z: ([\d.]+), w: ([\d.]+)\}$", text)
    if not match:
        raise ValueError(f"Missing Sprite border: {path}")
    if "  textureType: 8" not in text or "  alphaIsTransparency: 1" not in text:
        raise ValueError(f"Not an alpha Sprite: {path}")
    return [int(float(value)) for value in match.groups()]


def item(kind: str, path: Path, source: str = "", expected_border: list[int] | None = None) -> dict:
    result = {"kind": kind, "asset": path.relative_to(ROOT).as_posix(), "guid": guid(path)}
    if source:
        result["source"] = source
    if expected_border is not None:
        actual = border(path)
        if actual != expected_border:
            raise ValueError(f"Border mismatch: {path}: {actual} != {expected_border}")
        result["unityBorderLeftBottomRightTop"] = actual
    result["sha256"] = digest(path)
    return result


def main() -> None:
    rows = []
    manifest = json.loads((ROOT / "tools/ui002_sprite_manifest.json").read_text(encoding="utf-8"))["sprites"]
    for row in manifest:
        dest = ROOT / row["path"]
        source = CATALOG / row["source"]
        if digest(source) != row["sha256"] or digest(dest) != row["sha256"]:
            raise ValueError(f"Source copy changed: {row['id']}")
        rows.append(item("Frontier 3.1" if "Frontier31" in row["path"] else "兼容组件",
                         dest, row["source"], row["border"]))

    for folder, target in (("沿用控件", "LegacyControls"), ("窗口专用/选择状态", "SelectionStates")):
        for source in sorted((CATALOG / "资产" / folder).glob("*.png")):
            dest = DEST / "Sprites" / target / source.name
            if digest(source) != digest(dest):
                raise ValueError(f"Legacy copy changed: {source}")
            name = source.stem
            expected = ([0, 8, 0, 8] if name.endswith("-v") else
                        [8, 0, 8, 0] if name.endswith("-h") else
                        [18, 18, 18, 18] if name.startswith("stepper-") else
                        [20, 20, 20, 20] if name.startswith(("tab-", "frame-")) else
                        [0, 0, 0, 0])
            rows.append(item("沿用控件" if target == "LegacyControls" else "专用选择状态",
                             dest, source.relative_to(CATALOG).as_posix(), expected))

    font_sources = {
        "FangZhengHeiTiJianTi-1.ttf": "FangZhengHeiTiJianTi-1.ttf",
        "HanYiCuHeiJian-1.ttf": "HanYiCuHeiJian-1.ttf",
        "Novecento NarrowBold.otf": "Novecento NarrowBold.otf",
        "Novecento wide Normal Regular.ttf": "Novecento wide Normal Regular.woff2.ttf",
    }
    for target, source in font_sources.items():
        path = DEST / "Fonts" / target
        if digest(path) != digest(SOURCE / source):
            raise ValueError(f"Font copy changed: {target}")
        rows.append(item("字体", path, "游城拓荒/UI素材/" + source))
    rows.append(item("字体成员 0", DEST / "Fonts/MicrosoftYaHei-Regular-face0.ttf",
                     "游城拓荒/UI素材/msyh.ttc#0 Microsoft YaHei Regular"))

    for folder, names, kind in (("Content", ["UiFontRoles.asset", "UiSharedComponentTheme.asset"], "配置"),
                                ("Prefabs", ["SharedFrame.prefab", "SharedButton.prefab", "CardSlot.prefab",
                                             "ScrollView.prefab", "MixedLine.prefab", "StatusRow.prefab"], "共用预制体"),
                                ("Verification", ["Ui002Preview.unity"], "验证场景")):
        for name in names:
            rows.append(item(kind, DEST / folder / name))

    guids = [row["guid"] for row in rows]
    if len(guids) != len(set(guids)):
        raise ValueError("Duplicate UI-002 GUID")
    font_roles = (DEST / "Content/UiFontRoles.asset").read_text(encoding="utf-8")
    for row in rows:
        if row["kind"].startswith("字体") and row["guid"] not in font_roles:
            raise ValueError(f"Font role reference missing: {row['asset']}")
    roles_guid = guid(DEST / "Content/UiFontRoles.asset")
    settings = (ROOT / "Assets/YC/Presentation/Prefabs/GameSettings/GameSettingsMenu.prefab").read_text(encoding="utf-8")
    if "roleFonts: {fileID: 11400000, guid: " + roles_guid not in settings:
        raise ValueError("Existing font driver does not reference five roles")
    theme = (DEST / "Content/UiSharedComponentTheme.asset").read_text(encoding="utf-8")
    for required in ("outer-frame", "card-frame", "button-primary", "button-disabled"):
        found = next(row for row in rows if row["asset"].endswith("/" + required + ".png"))
        if found["guid"] not in theme:
            raise ValueError(f"Theme sprite reference missing: {required}")
    REPORT.write_text(json.dumps({"date": "2026-09-24", "count": len(rows), "assets": rows},
                                 ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"UI-002 映射核对通过：{len(rows)} 项，GUID 唯一、来源哈希与 Border 一致。")


if __name__ == "__main__":
    main()
