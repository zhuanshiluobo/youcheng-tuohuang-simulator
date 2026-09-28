"""核对 Effects 采用的 1x PNG 与 Unity GUID，写入交付映射。"""
import hashlib
import json
import re
import struct
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "游城拓荒" / "UI素材"
TARGET = ROOT / "Assets" / "YC" / "Presentation" / "Effects" / "Sprites"
OUTPUT = ROOT / "prompt" / "UI换新" / "执行记录" / "2026-09-25_Effects_资产映射.json"

SOURCES = [
    (
        SOURCE / "整理增量-2026-09-25-主框3.5" / "原包" / "assets",
        TARGET / "Frames",
        ["outer-frame.png", "bottom-frame.png", "chain-slot-frame.png", "bottom-bar-clean.png"],
    ),
]
effect_source = SOURCE / "整理增量-2026-09-25-效果条目3.4" / "原包" / "assets"
effect_target = TARGET / "EffectRows"
for folder in ("rows", "status", "icons", "toggle", "markers"):
    names = sorted(p.name for p in (effect_source / folder).glob("*.png") if "@2x" not in p.name)
    SOURCES.append((effect_source / folder, effect_target / folder, names))


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def dimensions(path: Path) -> list[int]:
    data = path.read_bytes()[:24]
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"不是 PNG：{path}")
    return list(struct.unpack(">II", data[16:24]))


items = []
for source_dir, target_dir, names in SOURCES:
    for name in names:
        source = source_dir / name
        target = target_dir / name
        if sha(source) != sha(target):
            raise ValueError(f"源图与 Unity 贴图不同：{target}")
        meta = Path(str(target) + ".meta").read_text(encoding="utf-8")
        guid = re.search(r"^guid: ([a-f0-9]{32})$", meta, re.M)
        border = re.search(r"^  spriteBorder: \{x: (\d+), y: (\d+), z: (\d+), w: (\d+)\}$", meta, re.M)
        ppu = re.search(r"^  spritePixelsToUnits: (\d+)$", meta, re.M)
        if guid is None or border is None or ppu is None:
            raise ValueError(f"Sprite 导入配置不完整：{target}")
        items.append(
            {
                "source": source.relative_to(ROOT).as_posix(),
                "unityPath": target.relative_to(ROOT).as_posix(),
                "guid": guid.group(1),
                "sha256": sha(source),
                "pixels": dimensions(target),
                "ppu": int(ppu.group(1)),
                "borderLeftBottomRightTop": [int(value) for value in border.groups()],
            }
        )

OUTPUT.write_text(json.dumps({"scale": "1x", "assets": items}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"已核对 {len(items)} 张 Unity Sprite，映射写入 {OUTPUT}")
