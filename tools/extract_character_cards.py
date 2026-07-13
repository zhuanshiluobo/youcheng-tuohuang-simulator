from __future__ import annotations

import json
from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
ASSET_DIR = ROOT / "游城拓荒" / "素材"
SOURCE_IMAGE = ASSET_DIR / "角色卡.jpg"
OUTPUT_DIR = ASSET_DIR / "角色卡拆分"

CARD_WIDTH = 600
CARD_HEIGHT = 850


CHARACTER_CARDS = [
    {
        "id": "character_01_liskarm",
        "row": 1,
        "column": 1,
        "name": "雷蛇",
        "description": "黑钢国际的 B.P.R.S 特派干员",
        "expansionPack": "标准",
        "strategyName": "安保协议",
        "strategyEffect": "放置 2 个自己的影响力；本回合收尾阶段移除 1 个自己的影响力。",
        "tacticName": "控制阵地",
        "tacticEffect": "支付 3 金券，替换 1 个影响力。",
        "effectSymbols": ["部署.png", "移除.png", "金券.png", "替换.png"],
    },
    {
        "id": "character_02_texas",
        "row": 1,
        "column": 2,
        "name": "德克萨斯",
        "description": "企鹅物流优秀员工",
        "expansionPack": "标准",
        "strategyName": "特别递送",
        "strategyEffect": "获得 12 金券；选择设施供应区的 1 张设施牌，将其移至设施牌堆底。",
        "tacticName": "叙拉古人",
        "tacticEffect": "支付 3 金券，移除 1 个影响力，调度 2 个影响力。",
        "effectSymbols": ["金券.png", "移除.png", "调度.png"],
    },
    {
        "id": "character_03_tin_man",
        "row": 1,
        "column": 3,
        "name": "锡人",
        "description": "来自梅兰德基金会",
        "expansionPack": "标准",
        "strategyName": "建立威信",
        "strategyEffect": "获得 1 分；然后可以支付 12 金券获得 1 个至纯源石；可以再支付 15 金券获得 1 个至纯源石。",
        "tacticName": "人员召集",
        "tacticEffect": "将你的角色牌弃牌区中的牌收回手牌；每收回 1 张，获得 5 金券或调度 1 个影响力。",
        "effectSymbols": ["分数.png", "金券.png", "至纯源石.png", "调度.png"],
    },
    {
        "id": "character_04_cannot",
        "row": 1,
        "column": 4,
        "name": "坎诺特",
        "description": "早上好 中午好以及晚上好",
        "expansionPack": "标准",
        "strategyName": "秘密渠道",
        "strategyEffect": "可按固定单价出售任意资源：源岩每个 3 金券、源石碎片每个 3 金券、异铁每个 4 金券、至纯源石每个 15 金券。",
        "tacticName": "征收物资",
        "tacticEffect": "从源岩、源石碎片、异铁中选择 1 种；所有玩家以每个 2 金券出售该资源至 0；你获得 1 分。",
        "effectSymbols": ["源岩.png", "源石碎片.png", "异铁.png", "至纯源石.png", "金券.png", "分数.png"],
    },
    {
        "id": "character_05_elysium",
        "row": 2,
        "column": 1,
        "name": "极境",
        "description": "野外生存专业人士",
        "expansionPack": "标准",
        "strategyName": "后勤调遣",
        "strategyEffect": "从自己数量最少的源岩、源石碎片、异铁中选择 1 种，获得 4 个；并列时可任选。",
        "tacticName": "导航通讯",
        "tacticEffect": "支付 3 源石碎片，选择与移动城市相邻且有自己影响力的资源点，免费将城市移动至该资源点。",
        "effectSymbols": ["源岩.png", "源石碎片.png", "异铁.png", "城市移动.png"],
    },
    {
        "id": "character_06_mlynar",
        "row": 2,
        "column": 2,
        "name": "玛恩纳",
        "description": "业务专家",
        "expansionPack": "企业扩展包",
        "strategyName": "公事公办",
        "strategyEffect": "选择 1 家企业，所有玩家将其合作等级提升 1；再选择 1 家企业（可不同），触发其企业特效。",
        "tacticName": "游说赞助",
        "tacticEffect": "选择 1 家企业，将其合作等级提升 1；获得 1 源岩、1 源石碎片、1 异铁。",
        "effectSymbols": ["企业升级.png", "企业特效.png", "源岩.png", "源石碎片.png", "异铁.png"],
    },
    {
        "id": "character_07_mountain",
        "row": 2,
        "column": 3,
        "name": "山",
        "description": "使用代号行动的神秘企业家",
        "expansionPack": "企业扩展包",
        "strategyName": "开辟商路",
        "strategyEffect": "选择 1 条航道，移除其上所有影响力并放置 1 个公路指示物；被移除影响力的玩家各获得 1 分，你也获得 1 分。",
        "tacticName": "商业网络",
        "tacticEffect": "支付 2 金券，选择 1 家企业，将其合作等级提升 1 并触发其企业特效。",
        "effectSymbols": ["移除.png", "分数.png", "金券.png", "企业升级.png", "企业特效.png"],
    },
]


def crop_card(source: Image.Image, row: int, column: int) -> Image.Image:
    left = (column - 1) * CARD_WIDTH
    top = (row - 1) * CARD_HEIGHT
    return source.crop((left, top, left + CARD_WIDTH, top + CARD_HEIGHT))


def write_markdown(manifest: dict, path: Path) -> None:
    lines = [
        "# 角色卡拆分清单",
        "",
        "- 来源：`游城拓荒/素材/角色卡.jpg`，原图为 4 列 × 2 行，每张卡为 600 × 850 像素。",
        "- 仅导出 7 张有效角色卡；第 2 行第 4 列“马位符”为无效果的空白占位，不导出。",
        "- `effectSymbols` 中的图标文件均位于 `游城拓荒/素材`。",
        "",
        "| 编号 | 名称 | 描述 | 扩展包 | 策略 | 计谋 | 截图 |",
        "| --- | --- | --- | --- | --- | --- | --- |",
    ]

    for card in manifest["cards"]:
        lines.append(
            "| {id} | {name} | {description} | {pack} | {strategy_name}：{strategy_effect} | {tactic_name}：{tactic_effect} | `{image}` |".format(
                id=card["id"],
                name=card["name"],
                description=card["description"],
                pack=card["expansionPack"],
                strategy_name=card["strategyName"],
                strategy_effect=card["strategyEffect"],
                tactic_name=card["tacticName"],
                tactic_effect=card["tacticEffect"],
                image=card["image"],
            )
        )

    lines.extend(["", "## 图标引用", ""])
    for card in manifest["cards"]:
        lines.append("- **{0}**：{1}".format(card["name"], "、".join(card["effectSymbols"])))

    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> None:
    with Image.open(SOURCE_IMAGE) as source:
        if source.size != (CARD_WIDTH * 4, CARD_HEIGHT * 2):
            raise ValueError(f"角色卡原图尺寸异常：{source.size}")

        OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
        cards = []
        for card in CHARACTER_CARDS:
            output_path = OUTPUT_DIR / f"{card['id']}_{card['name']}.jpg"
            crop_card(source, card["row"], card["column"]).save(output_path, quality=95)

            entry = dict(card)
            entry["image"] = str(output_path.relative_to(ROOT)).replace("\\", "/")
            entry["symbolPaths"] = [
                str((ASSET_DIR / symbol).relative_to(ROOT)).replace("\\", "/")
                for symbol in card["effectSymbols"]
            ]
            cards.append(entry)

    manifest = {
        "sourceImage": str(SOURCE_IMAGE.relative_to(ROOT)).replace("\\", "/"),
        "outputDirectory": str(OUTPUT_DIR.relative_to(ROOT)).replace("\\", "/"),
        "grid": {"columns": 4, "rows": 2, "cardWidth": CARD_WIDTH, "cardHeight": CARD_HEIGHT},
        "counts": {"exportedCharacterCards": len(cards), "blankPlaceholdersExcluded": 1},
        "cards": cards,
        "notes": [
            "第 2 行第 4 列“马位符”为无效果的空白占位，未导出。",
            "山的描述采用“使用代号行动的神秘企业家”。",
            "玛恩纳和山属于企业扩展包；其余角色卡属于标准。",
        ],
    }

    (OUTPUT_DIR / "character_cards_manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    write_markdown(manifest, OUTPUT_DIR / "character_cards_manifest.md")
    print(f"已导出角色卡：{len(cards)} 张")
    print(f"输出目录：{OUTPUT_DIR}")


if __name__ == "__main__":
    main()
