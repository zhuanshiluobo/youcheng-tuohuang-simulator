"""Export the review-only Frontier UI supplement; never touch scene assets."""

from __future__ import annotations

import base64
import json
import math
import re
import uuid
from collections import Counter
from pathlib import Path
from xml.etree import ElementTree as ET

import fitz
from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[3]
PACKAGE = Path(__file__).resolve().parent
DEST = ROOT / "Assets/YC/Presentation/CommonUi/Sprites/FrontierSupplementV1"
BASE = ROOT / "Assets/YC/Presentation/CommonUi/Sprites/Frontier31"
BUTTONS = ROOT / "Assets/YC/Presentation/GameplayHud/Sprites/Bases"
FONT = ROOT / "Assets/YC/Presentation/CommonUi/Fonts/MicrosoftYaHei-Regular-face0.ttf"
SCALE = 2
COLORS = {
    "ink": "#ECEBDA", "dark": "#263030", "muted": "#747E77",
    "face": "#343A3B", "edge": "#777D79", "inner": "#525957",
    "hover": "#424A49", "ivory": "#D9D9CB", "gold": "#DDC063",
    "info": "#B7C4C8", "success": "#ACBA8A", "warning": "#DDC063",
    "error": "#CF9484", "pending": "#DDC063",
}
ASSETS = []


def svg(width, height, body):
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" '
        f'xmlns:xlink="http://www.w3.org/1999/xlink" '
        f'width="{width}" height="{height}" viewBox="0 0 {width} {height}">'
        f'{body}</svg>'
    )


def folder_meta(path):
    meta = path.with_name(path.name + ".meta")
    if meta.exists():
        return
    guid = uuid.uuid5(uuid.NAMESPACE_URL, path.relative_to(ROOT).as_posix()).hex
    meta.write_text(
        f"fileFormatVersion: 2\nguid: {guid}\nfolderAsset: yes\n"
        "DefaultImporter:\n  externalObjects: {}\n  userData: \n"
        "  assetBundleName: \n  assetBundleVariant: \n", encoding="utf-8"
    )


def export(asset_id, category, width, height, body, border=(0, 0, 0, 0), state="", role=""):
    source = PACKAGE / "sources" / category / f"{asset_id}.svg"
    target = DEST / category / f"{asset_id}.png"
    source.parent.mkdir(parents=True, exist_ok=True)
    target.parent.mkdir(parents=True, exist_ok=True)
    source.write_text(svg(width, height, body), encoding="utf-8")
    with fitz.open("svg", source.read_bytes()) as document:
        pdf = document.convert_to_pdf()
    with fitz.open("pdf", pdf) as document:
        page = document[0]
        pixmap = page.get_pixmap(
            matrix=fitz.Matrix(width * SCALE / page.rect.width,
                               height * SCALE / page.rect.height), alpha=True
        )
        pixmap.save(target)
    template = (BUTTONS / "action-default.png.meta").read_text(encoding="utf-8")
    guid = uuid.uuid5(uuid.NAMESPACE_URL, target.relative_to(ROOT).as_posix()).hex
    template = re.sub(r"(?m)^guid: .*", f"guid: {guid}", template)
    template = re.sub(r"spritePixelsToUnits: .*", "spritePixelsToUnits: 200", template)
    left, bottom, right, top = [value * SCALE for value in border]
    template = re.sub(
        r"spriteBorder: .*",
        f"spriteBorder: {{x: {left}, y: {bottom}, z: {right}, w: {top}}}", template
    )
    sprite_id = uuid.uuid5(uuid.NAMESPACE_URL, guid + ":sprite").hex
    template = re.sub(r"spriteID: .*", f"spriteID: {sprite_id}", template)
    target.with_suffix(".png.meta").write_text(template, encoding="utf-8")
    ASSETS.append({
        "id": asset_id, "category": category, "state": state, "role": role,
        "png": target.relative_to(ROOT).as_posix(),
        "svg": source.relative_to(ROOT).as_posix(), "guid": guid,
        "logicalSize": [width, height], "pixelSize": [width * SCALE, height * SCALE],
        "border": [left, bottom, right, top], "pixelsPerUnit": 200,
    })


def rect(x, y, w, h, fill, stroke="none", sw=1, radius=4):
    return (f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{radius}" '
            f'fill="{fill}" stroke="{stroke}" stroke-width="{sw}"/>')


def frame(width, height, state="normal"):
    face, edge, inner = COLORS["face"], COLORS["edge"], COLORS["inner"]
    if state == "hover":
        face, edge, inner = COLORS["hover"], "#CBCBBC", "#747D73"
    elif state == "focus":
        face, edge, inner = "#383E3D", COLORS["gold"], "#8B8159"
    elif state == "error":
        edge, inner = COLORS["error"], "#8D665E"
    elif state == "disabled":
        face, edge, inner = "#2B3031", "#4D5452", "#3C4240"
    body = rect(1, 1, width - 2, height - 2, "#151A1B", radius=6)
    body += rect(2.5, 2.5, width - 5, height - 6, face, edge, 1.5, 6)
    body += rect(6.5, 6.5, width - 13, height - 15, "none", inner, 1, 3)
    body += f'<path d="M9 {height-8}H{width-9}" stroke="#252B2D" stroke-width="2"/>'
    body += f'<path d="M10 8.5H{width-10}" stroke="{edge}" stroke-opacity=".42"/>'
    mark = COLORS["gold"] if state in ("hover", "focus") else edge
    body += (f'<path d="M11 14V20M11 {height-22}V{height-16}'
             f'M{width-11} 14V20M{width-11} {height-22}V{height-16}" '
             f'stroke="{mark}" stroke-opacity=".8" stroke-width="1.5"/>')
    return body


def textured_panel(width, height, light=False):
    path = BASE / ("paper-bg.png" if light else "module-bg.png")
    texture = base64.b64encode(path.read_bytes()).decode("ascii")
    fill, edge = ("#D9D9CB", "#A6A697") if light else ("#343A3B", "#777D79")
    body = rect(1, 1, width - 2, height - 2, "#151A1B", radius=6)
    body += rect(2.5, 2.5, width - 5, height - 6, fill, edge, 1.5, 6)
    body += (f'<defs><clipPath id="surface">'
             f'{rect(3.5, 3.5, width-7, height-8, "white", radius=5)}</clipPath></defs>'
             f'<image x="3.5" y="3.5" width="{width-7}" height="{height-8}" '
             f'preserveAspectRatio="none" opacity=".72" '
             f'clip-path="url(#surface)" xlink:href="data:image/png;base64,{texture}"/>')
    body += rect(6.5, 6.5, width - 13, height - 15, "none", "#747A70", 1, 3)
    body += f'<path d="M10 8.5H{width-10}" stroke="#CBCBBC" stroke-opacity=".45"/>'
    return body


GLYPHS = {
    "info": '<circle cx="32" cy="32" r="23"/><path d="M32 29V45M32 20V21"/>',
    "success": '<circle cx="32" cy="32" r="23"/><path d="M20 32L28 40L44 24"/>',
    "warning": '<path d="M32 9L57 52H7ZM32 24V36M32 44V45"/>',
    "error": '<circle cx="32" cy="32" r="23"/><path d="M23 23L41 41M41 23L23 41"/>',
    "pending": '<circle cx="32" cy="32" r="23"/><path d="M32 18V32L42 39"/>',
    "reconnect": '<path d="M49 23A20 20 0 0 0 15 17L9 24M9 13V24H20M15 41A20 20 0 0 0 49 47L55 40M55 51V40H44"/>',
    "offline": '<path d="M11 20Q32 6 53 20M18 30Q32 20 46 30M26 40Q32 35 38 40M32 50V51M10 9L55 54"/>',
    "retry": '<path d="M12 27A22 22 0 1 1 14 46M9 13V28H24"/>',
    "save": '<path d="M12 10H44L54 20V54H10V10ZM21 10V27H43V10M19 54V36H45V54M35 16V21"/>',
    "chevron-down": '<path d="M15 24L32 41L49 24"/>',
    "check": '<path d="M14 33L27 46L51 19"/>',
    "close": '<path d="M18 18L46 46M46 18L18 46"/>',
}


def make_assets():
    for name, paths in GLYPHS.items():
        for tone, color in (("light", COLORS["ink"]), ("dark", COLORS["dark"]),
                            ("muted", COLORS["muted"])):
            export(f"icon-{name}-{tone}", "icons", 64, 64,
                   f'<g fill="none" stroke="{color}" stroke-width="4.2" '
                   f'stroke-linecap="round" stroke-linejoin="round">{paths}</g>',
                   state=tone, role="semantic-icon")
        if name in ("info", "success", "warning", "error"):
            export(f"icon-{name}-semantic", "icons", 64, 64,
                   f'<g fill="none" stroke="{COLORS[name]}" stroke-width="4.2" '
                   f'stroke-linecap="round" stroke-linejoin="round">{paths}</g>',
                   state="semantic", role="semantic-icon")
    for name in ("info", "success", "warning", "error", "pending"):
        body = textured_panel(384, 88)
        body += rect(9, 15, 3, 56, COLORS[name], radius=1)
        export(f"notice-{name}", "feedback", 384, 88, body,
               (18, 16, 16, 16), name, "notification-background")
    export("tooltip-dark", "feedback", 320, 112, textured_panel(320, 112),
           (16, 16, 16, 16), "dark", "tooltip-background")
    export("tooltip-light", "feedback", 320, 112, textured_panel(320, 112, True),
           (16, 16, 16, 16), "light", "tooltip-background")
    for name, points in (("up", "2,11 12,1 22,11"), ("down", "2,1 12,11 22,1")):
        export(f"tooltip-pointer-{name}", "feedback", 24, 12,
               f'<path d="M{points.replace(" ", "L")}Z" fill="#343A3B" '
               'stroke="#777D79" stroke-width="1.2"/>', role="tooltip-pointer")
    for tone, color in (("gold", COLORS["gold"]), ("light", COLORS["ink"]),
                        ("muted", COLORS["muted"])):
        segments = ""
        for i in range(12):
            angle = 2 * math.pi * i / 12
            x1, y1 = 32 + math.sin(angle) * 16, 32 - math.cos(angle) * 16
            x2, y2 = 32 + math.sin(angle) * 23, 32 - math.cos(angle) * 23
            segments += (f'<path d="M{x1:.3f} {y1:.3f}L{x2:.3f} {y2:.3f}" '
                         f'opacity="{.18+.82*i/11:.3f}"/>')
        export(f"spinner-{tone}", "feedback", 64, 64,
               f'<g fill="none" stroke="{color}" stroke-width="4.2" '
               f'stroke-linecap="round">{segments}</g>', role="rotating-indicator")
    for state in ("normal", "hover", "focus", "error", "disabled"):
        export(f"input-{state}", "forms", 256, 48, frame(256, 48, state),
               (16, 12, 16, 12), state, "input-or-dropdown-background")
    export("dropdown-panel", "forms", 256, 176, textured_panel(256, 176),
           (16, 16, 16, 16), role="dropdown-background")
    for state in ("normal", "hover", "selected", "disabled"):
        fill = {"normal": "#343A3B", "hover": "#424A49",
                "selected": "#D9D9CB", "disabled": "#2B3031"}[state]
        body = rect(1, 1, 254, 38, fill, radius=3)
        if state == "selected":
            body += rect(7, 10, 3, 20, COLORS["gold"], radius=1)
        elif state == "hover":
            body += rect(2, 2, 252, 36, "none", COLORS["edge"], 1, 3)
        export(f"dropdown-row-{state}", "forms", 256, 40, body,
               (14, 6, 10, 6), state, "dropdown-item-background")
    for state in ("off", "on", "off-focus", "on-focus", "off-disabled", "on-disabled"):
        disabled = state.endswith("disabled")
        on = state.startswith("on")
        edge = "#4D5452" if disabled else COLORS["gold"] if "focus" in state else COLORS["edge"]
        face = "#2B3031" if disabled else COLORS["ivory"] if on else COLORS["face"]
        body = rect(1, 1, 26, 26, face, edge, 1.5, 4)
        body += rect(4.5, 4.5, 19, 19, "none", "#525957", 1, 2)
        if on:
            ink = COLORS["muted"] if disabled else COLORS["dark"]
            body += f'<path d="M7 14L12 19L21 9" fill="none" stroke="{ink}" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/>'
        export(f"checkbox-{state}", "forms", 28, 28, body,
               state=state, role="checkbox-full-state")
    for state in ("off", "on", "off-disabled", "on-disabled"):
        disabled = state.endswith("disabled")
        on = state.startswith("on")
        edge = "#4D5452" if disabled else COLORS["gold"] if on else COLORS["edge"]
        fill = "#2B3031" if disabled else "#44463D" if on else "#303637"
        body = rect(1.5, 2, 49, 24, fill, edge, 1.5, 6)
        body += rect(5, 5.5, 42, 17, "none", "#525957", 1, 3)
        export(f"switch-track-{state}", "forms", 52, 28, body,
               (12, 8, 12, 8), state, "switch-track")
    for state in ("normal", "hover", "disabled"):
        fill = "#777D79" if state == "disabled" else "#F2F0DE" if state == "hover" else COLORS["ivory"]
        body = rect(1, 1, 18, 20, fill, "#263030", 1.2, 4)
        body += '<path d="M8 7V15M12 7V15" stroke="#777D79" stroke-width="1"/>'
        export(f"switch-thumb-{state}", "forms", 20, 22, body,
               state=state, role="switch-handle")
    for state in ("track", "fill"):
        fill = "#252B2D" if state == "track" else COLORS["gold"]
        stroke = "#525957" if state == "track" else "#F2D889"
        export(f"slider-{state}", "forms", 256, 8,
               rect(1, 1, 254, 6, fill, stroke, 1, 3),
               (5, 3, 5, 3), state, "slider-or-progress-track")
    for state in ("normal", "hover", "pressed", "disabled"):
        fill = {"normal": COLORS["ivory"], "hover": "#F2F0DE",
                "pressed": COLORS["gold"], "disabled": "#747E77"}[state]
        body = rect(1, 1, 18, 26, fill, "#263030", 1.2, 4)
        body += '<path d="M8 9V19M12 9V19" stroke="#525957" stroke-width="1"/>'
        export(f"slider-thumb-{state}", "forms", 20, 28, body,
               state=state, role="slider-handle")


def asset_image(asset_id, width=None, height=None):
    entry = next(asset for asset in ASSETS if asset["id"] == asset_id)
    image = Image.open(ROOT / entry["png"]).convert("RGBA")
    return image.resize((width or entry["logicalSize"][0], height or entry["logicalSize"][1]),
                        Image.Resampling.LANCZOS)


def create_overview():
    image = Image.new("RGBA", (1600, 1420), "#292F30")
    draw = ImageDraw.Draw(image)

    def text(x, y, value, size=19, color=COLORS["ink"]):
        draw.text((x, y), value, font=ImageFont.truetype(str(FONT), size), fill=color)

    def paste(asset_id, x, y, w=None, h=None):
        image.alpha_composite(asset_image(asset_id, w, h), (x, y))

    def title(y, number, value):
        draw.line((40, y, 1560, y), fill="#525957", width=1)
        text(40, y+14, number, 17, COLORS["gold"])
        text(78, y+10, value, 23)

    paper = Image.open(BASE / "paper-bg.png").convert("RGBA").resize((1600, 114))
    image.alpha_composite(paper, (0, 0))
    text(40, 23, "游城拓荒 · 对局 UI 补充包", 32, COLORS["dark"])
    text(40, 72, "审批稿 V1  /  石墨灰 · 象牙白 · 工程黄", 18, COLORS["dark"])
    text(1178, 39, "独立资产 · 等待审批", 20, COLORS["dark"])
    title(140, "01", "与现有皮肤对照")
    for index, (label, file) in enumerate((("现有行动按钮", BUTTONS/"action-default.png"),
                                        ("现有选中页签", BUTTONS/"tab-selected.png"))):
        x = 40 + index*390
        reference = Image.open(file).convert("RGBA").resize((300, 76))
        image.alpha_composite(reference, (x, 196))
        text(x+66, 218, "部署" if index == 0 else "主要行动", 22,
             COLORS["ink"] if index == 0 else COLORS["dark"])
        text(x, 282, label, 16, "#A7ADA6")
    paste("input-focus", 822, 207, 326, 60)
    text(843, 221, "新的焦点输入框", 20)
    paste("checkbox-on", 1210, 222)
    text(1254, 222, "同线宽、同圆角", 20)
    title(328, "02", "提示与等待状态")
    samples = [
        ("info", "行动提示", "选择航道后确认支付路费。"),
        ("success", "建设完成", "设施已加入你的城市面板。"),
        ("warning", "资源不足", "还需要 2 张金券。"),
        ("error", "提交失败", "连接暂时中断，请重试。"),
    ]
    for index, (kind, heading, message) in enumerate(samples):
        x = 40 + (index % 2)*508
        y = 390 + (index // 2)*108
        paste(f"notice-{kind}", x, y, 480, 90)
        paste(f"icon-{kind}-semantic", x+22, y+26, 32, 32)
        text(x+68, y+14, heading, 21)
        text(x+68, y+49, message, 18, "#BEC4B8")
    paste("notice-pending", 1070, 390, 490, 90)
    paste("spinner-gold", 1092, 416, 32, 32)
    text(1138, 404, "正在等待主机确认", 21)
    text(1138, 439, "命令已发送，等待结果。", 18, "#BEC4B8")
    paste("tooltip-dark", 1070, 498, 490, 90)
    text(1094, 514, "探索", 21)
    text(1094, 549, "查看可到达的资源点与路线费用。", 18, "#BEC4B8")
    title(624, "03", "输入框状态 · 输入框与下拉框共用")
    for index, (state, label) in enumerate((("normal", "普通"), ("hover", "悬停"),
                                          ("focus", "焦点"), ("error", "错误"),
                                          ("disabled", "禁用"))):
        x = 40 + index*306
        text(x, 682, label, 18, "#A7ADA6")
        paste(f"input-{state}", x, 721, 282, 52)
        text(x+21, 734, "房间编号" if state != "error" else "编号格式不正确", 18,
             COLORS["muted"] if state == "disabled" else COLORS["ink"])
    title(814, "04", "通用控件 · 原生结构与独立贴图")
    paste("dropdown-panel", 40, 878, 290, 173)
    for i, (state, label) in enumerate((("normal", "1280 × 720"), ("hover", "1600 × 900"),
                                      ("selected", "1920 × 1080"), ("disabled", "2560 × 1440"))):
        paste(f"dropdown-row-{state}", 48, 885+i*39, 274, 36)
        text(65, 891+i*39, label, 18, COLORS["dark"] if state == "selected" else
             COLORS["muted"] if state == "disabled" else COLORS["ink"])
    text(40, 1068, "下拉选项 · 普通 / 悬停 / 选中 / 禁用", 16, "#A7ADA6")
    for index, (state, label) in enumerate((("off", "未选中"), ("on", "选中"),
                                          ("on-focus", "焦点"), ("on-disabled", "禁用"))):
        paste(f"checkbox-{state}", 410+index*98, 891)
        text(398+index*98, 936, label, 16, "#A7ADA6")
    for index, state in enumerate(("off", "on", "on-disabled")):
        x = 420+index*112
        paste(f"switch-track-{state}", x, 997)
        paste("switch-thumb-disabled" if "disabled" in state else "switch-thumb-normal",
              x+(28 if state.startswith("on") else 4), 1000)
    text(400, 1068, "复选框与开关", 16, "#A7ADA6")
    paste("slider-track", 832, 917, 302, 8)
    paste("slider-fill", 832, 917, 194, 8)
    paste("slider-thumb-normal", 1014, 907)
    text(826, 879, "音量", 18)
    text(1115, 879, "64%", 18, COLORS["gold"])
    paste("slider-track", 832, 1004, 302, 8)
    paste("slider-fill", 832, 1004, 98, 8)
    paste("slider-thumb-disabled", 920, 994)
    text(826, 1068, "滑块 · 进度条可复用轨道", 16, "#A7ADA6")
    paste("tooltip-light", 1220, 878, 340, 173)
    text(1242, 902, "未满足使用条件", 21, COLORS["dark"])
    text(1242, 940, "完成主要行动后", 18, COLORS["dark"])
    text(1242, 970, "才可结束本轮行动。", 18, COLORS["dark"])
    text(1220, 1068, "浅色提示面板", 16, "#A7ADA6")
    title(1118, "05", "语义图标 · 浅色 / 深色 / 灰显三套")
    names = ["info", "success", "warning", "error", "pending", "reconnect",
             "offline", "retry", "save", "chevron-down", "check", "close"]
    labels = ["信息", "成功", "警告", "错误", "等待", "重连", "离线", "重试", "保存", "展开", "勾选", "关闭"]
    for index, (name, label) in enumerate(zip(names, labels)):
        x = 48+index*126
        paste(f"icon-{name}-light", x+18, 1182, 44, 44)
        text(x+20, 1242, label, 16, "#A7ADA6")
        paste(f"icon-{name}-muted", x+27, 1293, 26, 26)
    draw.line((40, 1352, 1560, 1352), fill="#525957")
    text(40, 1370, "所有文字由界面组件绘制；PNG 无烘焙文案。透明导出 / 2 倍分辨率 / 九宫格边距已配置。", 16, "#A7ADA6")
    image.convert("RGB").save(PACKAGE / "approval.png")


def validate_exports():
    problems = []
    for entry in ASSETS:
        with Image.open(ROOT / entry["png"]) as image:
            image.load()
            if list(image.size) != entry["pixelSize"] or image.mode != "RGBA":
                problems.append(entry["id"])
            if image.getpixel((0, 0))[3] != 0:
                problems.append(entry["id"] + ": opaque corner")
            if image.getchannel("A").getextrema()[1] == 0:
                problems.append(entry["id"] + ": empty")
        ET.parse(ROOT / entry["svg"])
        left, bottom, right, top = entry["border"]
        width, height = entry["pixelSize"]
        if left+right >= width or top+bottom >= height:
            problems.append(entry["id"] + ": invalid border")
    if problems:
        raise ValueError("Export issue: " + ", ".join(problems))


def main():
    make_assets()
    folder_meta(DEST)
    for path in sorted(DEST.iterdir()):
        if path.is_dir():
            folder_meta(path)
    validate_exports()
    manifest = {
        "name": "Frontier UI 通用控件与反馈补充包", "version": "1.0-review",
        "status": "待用户审批，尚未接入现有场景或预制体", "date": "2026-10-08",
        "renderScale": SCALE, "borderOrder": ["left", "bottom", "right", "top"],
        "palette": COLORS, "assets": ASSETS,
        "references": [
            "游城拓荒/UI素材/整理增量-2026-09-25-按钮3.2/原包/assets/bases/action-default.svg",
            "游城拓荒/UI素材/整理增量-2026-09-25-按钮3.2/原包/assets/icons/explore-light.svg",
            "Assets/YC/Presentation/CommonUi/Sprites/Frontier31/module-bg.png",
            "Assets/YC/Presentation/CommonUi/Sprites/Frontier31/paper-bg.png",
        ],
    }
    (PACKAGE / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    template = (PACKAGE / "preview.template.html").read_text(encoding="utf-8")
    (PACKAGE / "preview.html").write_text(template, encoding="utf-8")
    create_overview()
    report = {
        "assetCount": len(ASSETS), "categories": dict(Counter(a["category"] for a in ASSETS)),
        "pngDecode": "通过", "transparentCorners": "通过", "svgParse": "通过",
        "sliceBounds": "通过", "sceneOrPrefabModified": False,
        "gameplayIntegration": "未执行，等待审批", "visualApproval": "待用户审批",
    }
    (PACKAGE / "export-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
