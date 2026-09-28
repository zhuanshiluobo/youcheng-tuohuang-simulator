"""Copy only selected UI-002 runtime inputs; never touches existing Unity assets."""
from __future__ import annotations

import hashlib
import json
import shutil
from pathlib import Path

from fontTools.ttLib import TTCollection


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "游城拓荒/UI素材"
CATALOG = SOURCE / "整理版-2026-09-24"
DEST = ROOT / "Assets/YC/Presentation/Ui002"


def copy_new(source: Path, target: Path) -> None:
    if not source.is_file():
        raise FileNotFoundError(source)
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists():
        if source.read_bytes() != target.read_bytes():
            raise RuntimeError(f"Existing Unity asset differs: {target}")
        return
    shutil.copyfile(source, target)
    print(f"新增 {target.relative_to(ROOT).as_posix()}")


def main() -> None:
    index = json.loads((CATALOG / "清单/新版组件索引.json").read_text(encoding="utf-8"))
    manifest = []
    for asset_id, entry in index["assets"].items():
        source = CATALOG / entry["path"]
        category = "Frontier31" if "新版组件" in entry["path"] else "Compatible"
        target = DEST / "Sprites" / category / source.name
        copy_new(source, target)
        manifest.append({
            "id": asset_id,
            "path": target.relative_to(ROOT).as_posix(),
            "source": entry["path"],
            "sha256": entry["sourceSpec"]["sha256"],
            "border": entry["unityBorderLeftBottomRightTop"],
            "size": entry["sourceSpec"]["viewBox"],
        })

    for category in ("沿用控件", "窗口专用/选择状态"):
        for source in sorted((CATALOG / "资产" / category).glob("*.png")):
            copy_new(source, DEST / "Sprites" / ("LegacyControls" if category == "沿用控件" else "SelectionStates") / source.name)

    names = {
        "FangZhengHeiTiJianTi-1.ttf": "FangZhengHeiTiJianTi-1.ttf",
        "HanYiCuHeiJian-1.ttf": "HanYiCuHeiJian-1.ttf",
        "Novecento NarrowBold.otf": "Novecento NarrowBold.otf",
        # Source bytes are TrueType; only the misleading suffix is removed.
        "Novecento wide Normal Regular.woff2.ttf": "Novecento wide Normal Regular.ttf",
    }
    for source_name, target_name in names.items():
        copy_new(SOURCE / source_name, DEST / "Fonts" / target_name)

    ttc = SOURCE / "msyh.ttc"
    output = DEST / "Fonts/MicrosoftYaHei-Regular-face0.ttf"
    collection = TTCollection(str(ttc))
    if len(collection.fonts) < 2:
        raise RuntimeError("msyh.ttc member list changed")
    font = collection.fonts[0]
    font.flavor = None
    output.parent.mkdir(parents=True, exist_ok=True)
    if not output.exists():
        font.save(str(output))
        print(f"提取成员 0 {output.relative_to(ROOT).as_posix()}")
    collection.close()
    print("TTC SHA-256", hashlib.sha256(ttc.read_bytes()).hexdigest())
    manifest_path = ROOT / "tools/ui002_sprite_manifest.json"
    manifest_path.write_text(json.dumps({"sprites": manifest}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
