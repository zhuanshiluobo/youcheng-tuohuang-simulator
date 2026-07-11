from __future__ import annotations

import json
from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
SOURCE_DIR = ROOT / "游城拓荒" / "素材"
OUTPUT_DIR = SOURCE_DIR / "事件牌拆分"
CARD_WIDTH = 850
CARD_HEIGHT = 600


# 仅列出有实际牌面的格子；黄色 r06c01 是“占位符”空白牌，故不导出。
CARDS = [
    ("green", "绿色.jpg", 1, 1, "中立采石场", "event_green_01"),
    ("green", "绿色.jpg", 1, 2, "清理虫巢", "event_green_02"),
    ("green", "绿色.jpg", 2, 1, "外露矿床", "event_green_03"),
    ("green", "绿色.jpg", 2, 2, "富饶岩层", "event_green_04"),
    ("green", "绿色.jpg", 3, 1, "矿业聚落", "event_green_05"),
    ("green", "绿色.jpg", 3, 2, "废弃矿坑（4）", "event_green_06"),
    ("red", "红色.jpg", 1, 1, "险中净土", "event_red_01"),
    ("red", "红色.jpg", 1, 2, "裂谷矿脉", "event_red_02"),
    ("red", "红色.jpg", 2, 1, "采集平台残骸", "event_red_03"),
    ("red", "红色.jpg", 2, 2, "高污染环境", "event_red_04"),
    ("red", "红色.jpg", 3, 1, "深层矿床（4）", "event_red_05"),
    ("red", "红色.jpg", 3, 2, "地质瑰宝", "event_red_06"),
    ("red", "红色.jpg", 4, 1, "采集平台残骸（企业选项版）", None),
    ("yellow", "黄色.jpg", 1, 1, "大型源岩场", "event_yellow_01"),
    ("yellow", "黄色.jpg", 1, 2, "洞穴遗迹", "event_yellow_02"),
    ("yellow", "黄色.jpg", 2, 1, "荒地人村落", "event_yellow_03"),
    ("yellow", "黄色.jpg", 2, 2, "锈锤领地", "event_yellow_04"),
    ("yellow", "黄色.jpg", 3, 1, "情报交换（4）", "event_yellow_05"),
    ("yellow", "黄色.jpg", 3, 2, "风险任务", "event_yellow_06"),
    ("yellow", "黄色.jpg", 4, 1, "异铁开采权", "event_yellow_07"),
    ("yellow", "黄色.jpg", 4, 2, "敌对生态圈（4）", "event_yellow_08"),
    ("yellow", "黄色.jpg", 5, 1, "富异铁区", "event_yellow_09"),
    ("yellow", "黄色.jpg", 5, 2, "遭弃矿场", "event_yellow_10"),
    ("yellow", "黄色.jpg", 6, 2, "洞穴遗迹（企业选项版）", None),
]


TERM_GLOSSARY = {
    "企业升级": "选择一家本局参与的合作企业，将自己在该企业板上的影响力标记提升 1 级。抵达普通奖励格时立即结算；抵达企业特效格时只解锁该特效，不立即发动。",
    "企业特效": "从自己已解锁的企业特效中选择一项发动。企业特效的发动独立于企业升级，不能选择尚未解锁的特效。",
}


def crop_card(source: Image.Image, row: int, column: int) -> Image.Image:
    left = (column - 1) * CARD_WIDTH
    top = (row - 1) * CARD_HEIGHT
    return source.crop((left, top, left + CARD_WIDTH, top + CARD_HEIGHT))


def main() -> None:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    source_cache: dict[str, Image.Image] = {}
    entries = []
    try:
        for index, (color, source_name, row, column, name, database_id) in enumerate(CARDS, start=1):
            source = source_cache.setdefault(source_name, Image.open(SOURCE_DIR / source_name))
            expected_size = (CARD_WIDTH * 2, CARD_HEIGHT * (3 if color == "green" else 4 if color == "red" else 6))
            if source.size != expected_size:
                raise ValueError(f"{source_name} 尺寸异常：{source.size}，预期 {expected_size}")

            file_name = f"event_{color}_{index:02d}_{name}_r{row:02d}c{column:02d}.jpg"
            output_path = OUTPUT_DIR / file_name
            crop_card(source, row, column).save(output_path, quality=95)
            entries.append(
                {
                    "name": name,
                    "color": color,
                    "sourceImage": f"游城拓荒/素材/{source_name}",
                    "row": row,
                    "column": column,
                    "image": str(output_path.relative_to(ROOT)).replace("\\", "/"),
                    "eventCardDatabaseId": database_id,
                    "recordedInEventCardDatabase": database_id is not None,
                }
            )
    finally:
        for source in source_cache.values():
            source.close()

    manifest = {
        "grid": {"columns": 2, "cardWidth": CARD_WIDTH, "cardHeight": CARD_HEIGHT},
        "cards": entries,
        "unrecordedCards": [entry for entry in entries if not entry["recordedInEventCardDatabase"]],
        "termGlossary": TERM_GLOSSARY,
        "skippedCells": [
            {
                "sourceImage": "游城拓荒/素材/黄色.jpg",
                "row": 6,
                "column": 1,
                "name": "占位符",
                "reason": "空白占位牌，不是可录入的事件卡。",
            }
        ],
    }
    (OUTPUT_DIR / "event_cards_manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )

    lines = ["# 事件牌拆分清单", "", f"- 已拆分：{len(entries)} 张有效牌面。", "- 未导出：黄色 r06c01 占位符。", "", "## 未录入 EventCardDatabase", ""]
    for entry in manifest["unrecordedCards"]:
        lines.append(f"- {entry['name']}（{entry['sourceImage']} r{entry['row']:02d}c{entry['column']:02d}）")
    lines.extend(["", "## 词语解释", ""])
    for term, explanation in TERM_GLOSSARY.items():
        lines.append(f"- **{term}**：{explanation}")

    lines.extend(["", "## 全部卡片", "", "| 颜色 | 牌名 | 来源位置 | EventCardDatabase | 图片 |", "| --- | --- | --- | --- | --- |"])
    for entry in entries:
        database_id = entry["eventCardDatabaseId"] or "未录入"
        lines.append(
            f"| {entry['color']} | {entry['name']} | r{entry['row']:02d}c{entry['column']:02d} | {database_id} | `{entry['image']}` |"
        )
    (OUTPUT_DIR / "event_cards_manifest.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
