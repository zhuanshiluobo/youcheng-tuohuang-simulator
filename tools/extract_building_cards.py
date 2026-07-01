from __future__ import annotations

import json
from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
SOURCE_IMAGE = ROOT / "游城拓荒" / "素材" / "建设牌堆.jpg"
OUTPUT_ROOT = ROOT / "游城拓荒" / "素材" / "建设牌输出"
BUILDING_DIR = OUTPUT_ROOT / "建筑卡片"
RESERVE_DIR = OUTPUT_ROOT / "备用卡片"

CARD_WIDTH = 600
CARD_HEIGHT = 850


CARD_TYPES = {
    "城邦行政区": {
        "score": 3,
        "resourceCost": {"源岩": 3, "源石碎片": 3, "异铁": 3},
        "goldVoucherCost": 30,
        "description": "行政体系的建立能够解决各种问题，特别是在这片正待开垦的荒原之上。",
        "effect": "唯一。",
    },
    "附属能源设施": {
        "score": 1,
        "resourceCost": {"源岩": 2, "源石碎片": 2, "异铁": 3},
        "goldVoucherCost": 23,
        "description": "通过源源不断的能源供应，移动城市上的设施日以继夜地运作着，一刻不停。",
        "effect": "唯一；选择一个十字相邻的非彩色设施，结算其入场效果。",
    },
    "联邦理事处": {
        "score": 0,
        "resourceCost": {"源岩": 4, "源石碎片": 2},
        "goldVoucherCost": 17,
        "description": "尽管拓荒队之间各自为政，但依旧在一些方面受制于哥伦比亚当局。",
        "effect": "唯一；本回合的收尾阶段传递起始标记时，由你获得起始标记。只有本回合最后建造的联邦理事处会生效。",
        "notes": "效果中的“起始标记”为根据牌面句式与规则语义补全。",
    },
    "简陋工程营": {
        "score": -1,
        "resourceCost": {"源岩": 2, "源石碎片": 1},
        "goldVoucherCost": 8,
        "description": "“我们提供高规格的防护，有效的抑制剂，并维护工人的个人利益。”",
        "effect": "唯一；牌面为吊车/建设类图标效果，待按正式规则术语校准。",
        "effectRaw": "唯一 + 吊车图标。",
    },
    "物流枢纽": {
        "score": 2,
        "resourceCost": {"至纯源石": 1, "金券": 2},
        "goldVoucherCost": 24,
        "description": "“本城的第一座物流港，愿其能我们开辟一条……”",
        "effect": "唯一；可以从余下的延伸枢纽中选择 1 张并建设。",
    },
    "源石精炼厂": {
        "score": 0,
        "resourceCost": {"源岩": 1, "源石碎片": 1, "异铁": 1},
        "goldVoucherCost": 10,
        "description": "现在，移动城市有了跳动的心脏。",
        "effect": "获得 6 源石碎片。",
    },
    "城市化区域": {
        "score": 1,
        "resourceCost": {"源岩": 3, "异铁": 2},
        "goldVoucherCost": 16,
        "description": "“哥伦比亚重视开拓与研究，当然也重视在其背后支持的贸易系统。”",
        "effect": "每有一个与核心指挥塔十字相邻的设施，获得 4 金券。",
    },
    "异铁冶炼厂": {
        "score": 1,
        "resourceCost": {"源岩": 2, "源石碎片": 2, "异铁": 1},
        "goldVoucherCost": 15,
        "description": "少有人造物可以像异铁块一样，承受着这片大地的苦难而不变质。",
        "effect": "获得 4 异铁。",
    },
    "贸易街区": {
        "score": 0,
        "resourceCost": {"源岩": 1, "异铁": 1},
        "goldVoucherCost": 6,
        "description": "“在这不一定能寻到最好的价格，但一定可以解你燃眉之急。”",
        "effect": "你可以将任何资源用以下单价出售：源岩 3 金券、源石碎片 3 金券、异铁 4 金券、至纯源石 15 金券。",
    },
    "城邦工业区": {
        "score": 2,
        "resourceCost": {"源岩": 4, "源石碎片": 3, "金券": 2},
        "goldVoucherCost": 24,
        "description": "移动城市的优势既非武力，也非经济，而是可以躲避天灾的生产力。",
        "effect": "你的建筑面板上每有 1 种非彩色的设施，此设施的建筑花费 -1 源岩。",
    },
    "高性能动力设施": {
        "score": 0,
        "resourceCost": {"源石碎片": 4, "异铁": 2},
        "goldVoucherCost": 20,
        "description": "下层动力区关乎所有人的存亡，有必要对其进行翻修和改良。",
        "effect": "免费执行牌面航道图标效果：在经过的航道上放置 1 影响力。",
        "effectRaw": "免费 + 航道箭头图标；在经过的航道上放置 1 影响力。",
    },
    "固源岩提纯厂": {
        "score": 0,
        "resourceCost": {"源岩": 1, "源石碎片": 1, "异铁": 1},
        "goldVoucherCost": 10,
        "description": "假如精炼源石是移动城市不断流动的血液，那提纯源岩便是坚实的肌肉。",
        "effect": "获得 7 源岩。",
    },
    "开采电铲": {
        "score": 0,
        "resourceCost": {"源石碎片": 2, "异铁": 1},
        "goldVoucherCost": 10,
        "description": "当开采平台发展为移动城市，会有人想起它简陋的过往吗？",
        "effect": "合计获得 5 个源岩、源石碎片或异铁。",
    },
    "佣兵指挥部": {
        "score": 1,
        "resourceCost": {"源岩": 2, "异铁": 2, "金券": 4},
        "goldVoucherCost": 18,
        "description": "“放心，把活儿交给专业的人。”",
        "effect": "触发牌面所示二选一企业等级图标效果：红色上升图标或绿色下降图标。",
        "effectRaw": "红色上升企业图标 或 绿色下降企业图标。",
    },
    "护航调度中心": {
        "score": 0,
        "resourceCost": {"源岩": 2, "源石碎片": 2, "异铁": 2},
        "goldVoucherCost": 18,
        "description": "“虽不及高多汀开拓军的前人来得谨慎，但这些新人拓荒者们勇气尚可。”",
        "effect": "触发牌面所示两个绿色下降企业等级图标效果。",
        "effectRaw": "绿色下降企业图标 x2。",
    },
    "载具仓库": {
        "score": 0,
        "resourceCost": {"源岩": 1, "源石碎片": 2},
        "goldVoucherCost": 9,
        "description": "从矿区回收的探索车辆正在进行细致的清洗，等待它的下一任主人。",
        "effect": "触发红色上升企业图标、蓝色右向企业图标，或执行 1 次探索。",
        "effectRaw": "红色上升企业图标 + 蓝色右向企业图标，或执行 1 次探索。",
    },
    "核心指挥塔": {
        "score": 0,
        "resourceCost": {},
        "goldVoucherCost": None,
        "description": "“起航的第一个信号在塔顶点亮。”",
        "effect": "唯一。",
    },
    "延伸枢纽": {
        "score": -1,
        "resourceCost": {},
        "goldVoucherCost": None,
        "description": "……权力与扩张之路。",
        "effect": "只能通过物流枢纽的入场效果进行建设。",
    },
}


BUILDING_INSTANCES = [
    (1, 1, "城邦行政区", "rainbow"),
    (1, 2, "城邦行政区", "rainbow"),
    (1, 3, "城邦行政区", "rainbow"),
    (1, 4, "附属能源设施", "rainbow"),
    (1, 5, "附属能源设施", "rainbow"),
    (1, 6, "附属能源设施", "rainbow"),
    (1, 7, "联邦理事处", "rainbow"),
    (1, 8, "联邦理事处", "rainbow"),
    (1, 9, "联邦理事处", "rainbow"),
    (2, 1, "简陋工程营", "rainbow"),
    (2, 2, "简陋工程营", "rainbow"),
    (2, 3, "物流枢纽", "blue"),
    (2, 4, "物流枢纽", "blue"),
    (2, 5, "源石精炼厂", "blue"),
    (2, 6, "城市化区域", "blue"),
    (2, 7, "城市化区域", "blue"),
    (2, 8, "城市化区域", "blue"),
    (2, 9, "异铁冶炼厂", "blue"),
    (3, 1, "贸易街区", "blue"),
    (3, 2, "贸易街区", "blue"),
    (3, 3, "贸易街区", "blue"),
    (3, 5, "城邦工业区", "yellow"),
    (3, 6, "城邦工业区", "yellow"),
    (3, 7, "城邦工业区", "yellow"),
    (3, 8, "高性能动力设施", "yellow"),
    (3, 9, "高性能动力设施", "yellow"),
    (4, 1, "固源岩提纯厂", "yellow"),
    (4, 2, "源石精炼厂", "yellow"),
    (4, 3, "开采电铲", "yellow"),
    (4, 4, "开采电铲", "yellow"),
    (4, 5, "开采电铲", "yellow"),
    (4, 7, "异铁冶炼厂", "red"),
    (4, 8, "固源岩提纯厂", "red"),
    (4, 9, "佣兵指挥部", "red"),
    (5, 1, "佣兵指挥部", "red"),
    (5, 2, "佣兵指挥部", "red"),
    (5, 3, "护航调度中心", "red"),
    (5, 4, "护航调度中心", "red"),
    (5, 5, "载具仓库", "red"),
    (5, 6, "载具仓库", "red"),
    (5, 7, "载具仓库", "red"),
]

RESERVE_INSTANCES = [
    (5, 9, "核心指挥塔", "rainbow", 4, "core_command_tower_x4"),
    (3, 4, "延伸枢纽", "blue", 1, "extension_hub_blue"),
    (4, 6, "延伸枢纽", "yellow", 1, "extension_hub_yellow"),
    (5, 8, "延伸枢纽", "red", 1, "extension_hub_red"),
]

SKIPPED_INSTANCES = [
    {"row": 6, "column": 4, "name": "企业办事处", "reason": "按用户要求暂不记录"},
    {"row": 6, "column": 5, "name": "企业办事处", "reason": "按用户要求暂不记录"},
    {"row": 6, "column": 6, "name": "企业办事处", "reason": "按用户要求暂不记录"},
]


def crop_card(source: Image.Image, row: int, column: int) -> Image.Image:
    left = (column - 1) * CARD_WIDTH
    top = (row - 1) * CARD_HEIGHT
    return source.crop((left, top, left + CARD_WIDTH, top + CARD_HEIGHT))


def card_entry(
    index: int,
    row: int,
    column: int,
    name: str,
    color: str,
    category: str,
    image_path: Path,
    copy_count: int = 1,
) -> dict:
    data = dict(CARD_TYPES[name])
    return {
        "id": f"{category}_{index:03d}",
        "name": name,
        "color": color,
        "row": row,
        "column": column,
        "copyCount": copy_count,
        "image": str(image_path.relative_to(ROOT)).replace("\\", "/"),
        **data,
    }


def write_markdown(manifest: dict, path: Path) -> None:
    lines = [
        "# 建设牌拆分清单",
        "",
        f"- 来源图片：`{manifest['sourceImage']}`",
        f"- 建筑卡片：{manifest['counts']['buildingCards']} 张",
        f"- 核心指挥塔：1 张截图，代表重复 {manifest['counts']['coreCommandTowerCopies']} 次",
        f"- 延伸枢纽：{manifest['counts']['extensionHubs']} 张不同色截图",
        f"- 企业办事处：{manifest['counts']['skippedEnterpriseOffices']} 张，暂不记录",
        "",
        "## 建筑卡片",
        "",
        "| 序号 | 牌名 | 颜色 | 位置 | 分数 | 所需资源 | 所需金券 | 描述 | 效果 | 截图 |",
        "| --- | --- | --- | --- | ---: | --- | ---: | --- | --- | --- |",
    ]

    for card in manifest["buildingCards"]:
        resource_cost = "、".join(f"{key} x{value}" for key, value in card["resourceCost"].items()) or "无"
        gold_cost = "无" if card["goldVoucherCost"] is None else str(card["goldVoucherCost"])
        lines.append(
            "| {id} | {name} | {color} | r{row:02d}c{column:02d} | {score} | {resource} | {gold} | {description} | {effect} | `{image}` |".format(
                id=card["id"],
                name=card["name"],
                color=card["color"],
                row=card["row"],
                column=card["column"],
                score=card["score"],
                resource=resource_cost,
                gold=gold_cost,
                description=card["description"].replace("|", "/"),
                effect=card["effect"].replace("|", "/"),
                image=card["image"],
            )
        )

    lines.extend(
        [
            "",
            "## 备用卡片",
            "",
            "| 序号 | 牌名 | 颜色 | 位置 | 代表数量 | 分数 | 所需资源 | 所需金券 | 描述 | 效果 | 截图 |",
            "| --- | --- | --- | --- | ---: | ---: | --- | ---: | --- | --- | --- |",
        ]
    )

    for card in manifest["reserveCards"]:
        resource_cost = "、".join(f"{key} x{value}" for key, value in card["resourceCost"].items()) or "无"
        gold_cost = "无" if card["goldVoucherCost"] is None else str(card["goldVoucherCost"])
        lines.append(
            "| {id} | {name} | {color} | r{row:02d}c{column:02d} | {copy_count} | {score} | {resource} | {gold} | {description} | {effect} | `{image}` |".format(
                id=card["id"],
                name=card["name"],
                color=card["color"],
                row=card["row"],
                column=card["column"],
                copy_count=card["copyCount"],
                score=card["score"],
                resource=resource_cost,
                gold=gold_cost,
                description=card["description"].replace("|", "/"),
                effect=card["effect"].replace("|", "/"),
                image=card["image"],
            )
        )

    lines.extend(["", "## 暂不记录", ""])
    for skipped in manifest["skippedCards"]:
        lines.append(f"- r{skipped['row']:02d}c{skipped['column']:02d}：{skipped['name']}，{skipped['reason']}")

    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> None:
    BUILDING_DIR.mkdir(parents=True, exist_ok=True)
    RESERVE_DIR.mkdir(parents=True, exist_ok=True)

    image = Image.open(SOURCE_IMAGE)
    if image.size != (CARD_WIDTH * 9, CARD_HEIGHT * 6):
        raise ValueError(f"Unexpected source image size: {image.size}")

    building_cards = []
    for index, (row, column, name, color) in enumerate(BUILDING_INSTANCES, start=1):
        file_name = f"building_{index:03d}_{name}_r{row:02d}c{column:02d}.jpg"
        output_path = BUILDING_DIR / file_name
        crop_card(image, row, column).save(output_path, quality=95)
        building_cards.append(card_entry(index, row, column, name, color, "building", output_path))

    reserve_cards = []
    for index, (row, column, name, color, copy_count, stem) in enumerate(RESERVE_INSTANCES, start=1):
        output_path = RESERVE_DIR / f"{stem}_{name}.jpg"
        crop_card(image, row, column).save(output_path, quality=95)
        reserve_cards.append(card_entry(index, row, column, name, color, "reserve", output_path, copy_count))

    manifest = {
        "sourceImage": str(SOURCE_IMAGE.relative_to(ROOT)).replace("\\", "/"),
        "grid": {"columns": 9, "rows": 6, "cardWidth": CARD_WIDTH, "cardHeight": CARD_HEIGHT},
        "counts": {
            "buildingCards": len(building_cards),
            "coreCommandTowerScreenshots": 1,
            "coreCommandTowerCopies": 4,
            "extensionHubs": 3,
            "skippedEnterpriseOffices": len(SKIPPED_INSTANCES),
        },
        "buildingCards": building_cards,
        "reserveCards": reserve_cards,
        "skippedCards": SKIPPED_INSTANCES,
        "notes": [
            "resourceCost 表示牌面 or 上方的资源/混合成本；goldVoucherCost 表示 or 下方的纯金券成本。",
            "红色设施的企业图标效果按截图可见符号转写，正式术语仍建议结合规则书复核。",
            "3 张企业办事处按用户要求暂不记录。",
        ],
    }

    (OUTPUT_ROOT / "building_cards_manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    write_markdown(manifest, OUTPUT_ROOT / "building_cards_manifest.md")

    print(f"建筑卡片: {len(building_cards)}")
    print("核心指挥塔截图: 1, 代表 4 张")
    print(f"延伸枢纽截图: {manifest['counts']['extensionHubs']}")
    print(f"输出目录: {OUTPUT_ROOT}")


if __name__ == "__main__":
    main()
