"""Export independent review sprites without touching existing Unity UI assets."""

from __future__ import annotations

import base64
import json
import math
import re
import shutil
import sys
import uuid
from collections import Counter
from pathlib import Path

import fitz
from PIL import Image


ROOT = Path(__file__).resolve().parents[3]
PACKAGE = Path(__file__).resolve().parent
DEST = ROOT / "Assets/YC/Presentation/CommonUi/Sprites/FrontierSupplementV2"
BASE = ROOT / "Assets/YC/Presentation/CommonUi/Sprites/Frontier31"
META_TEMPLATE = ROOT / "Assets/YC/Presentation/GameplayHud/Sprites/Bases/action-default.png.meta"
SCALE = 2
COLORS = {
    "ink": "#ECEBDA", "dark": "#293131", "muted": "#7B827B",
    "face": "#343A3B", "edge": "#858980", "inner": "#555D59",
    "paper": "#E3E0D1", "gold": "#DDC063", "info": "#6F929B",
    "success": "#758459", "warning": "#B59538", "error": "#AA6657",
    "pending": "#B59538",
}
ASSETS = []
PAPER_URI = "data:image/png;base64," + base64.b64encode((BASE / "paper-bg.png").read_bytes()).decode("ascii")


def svg(width, height, body):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" '
            f'width="{width}" height="{height}" viewBox="0 0 {width} {height}">{body}</svg>')


def cut_path(x, y, width, height, cut=6):
    return (f'M{x+cut} {y}H{x+width-cut}L{x+width} {y+cut}V{y+height-cut}'
            f'L{x+width-cut} {y+height}H{x+cut}L{x} {y+height-cut}V{y+cut}Z')


def plate(x, y, width, height, fill, edge="none", stroke=1, cut=6):
    return f'<path d="{cut_path(x,y,width,height,cut)}" fill="{fill}" stroke="{edge}" stroke-width="{stroke}"/>'


def meta_for(target, border):
    guid = uuid.uuid5(uuid.NAMESPACE_URL, target.relative_to(ROOT).as_posix()).hex
    template = META_TEMPLATE.read_text(encoding="utf-8")
    template = re.sub(r"(?m)^guid: .*", f"guid: {guid}", template)
    template = re.sub(r"spritePixelsToUnits: .*", "spritePixelsToUnits: 200", template)
    template = re.sub(r"spriteBorder: .*", f"spriteBorder: {{x: {border[0]}, y: {border[1]}, z: {border[2]}, w: {border[3]}}}", template)
    template = re.sub(r"spriteID: .*", f"spriteID: {uuid.uuid5(uuid.NAMESPACE_URL, guid + ':sprite').hex}", template)
    target.with_suffix(".png.meta").write_text(template, encoding="utf-8")
    return guid


def folder_meta(path):
    meta = path.with_name(path.name + ".meta")
    if not meta.exists():
        guid = uuid.uuid5(uuid.NAMESPACE_URL, path.relative_to(ROOT).as_posix()).hex
        meta.write_text(f"fileFormatVersion: 2\nguid: {guid}\nfolderAsset: yes\nDefaultImporter:\n"
                        "  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n", encoding="utf-8")


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
        page.get_pixmap(matrix=fitz.Matrix(width*SCALE/page.rect.width, height*SCALE/page.rect.height), alpha=True).save(target)
    pixels_border = [v*SCALE for v in border]
    ASSETS.append({
        "id": asset_id, "category": category, "state": state, "role": role,
        "png": target.relative_to(ROOT).as_posix(), "svg": source.relative_to(ROOT).as_posix(),
        "guid": meta_for(target, pixels_border), "logicalSize": [width, height],
        "pixelSize": [width*SCALE, height*SCALE], "border": pixels_border, "pixelsPerUnit": 200,
    })


def paper_surface(width, height, cut=6, opacity=.88):
    return (f'<defs><clipPath id="paper"><path d="{cut_path(3,3,width-6,height-6,cut)}"/></clipPath></defs>'
            f'<image x="3" y="3" width="{width-6}" height="{height-6}" preserveAspectRatio="none" '
            f'opacity="{opacity}" clip-path="url(#paper)" xlink:href="{PAPER_URI}"/>')


def frame(width, height, state="normal", light=True, primary=False):
    dark, gold = COLORS["dark"], COLORS["gold"]
    fill = gold if primary else COLORS["paper"] if light else COLORS["face"]
    edge = "#797E75"
    if state == "hover":
        fill, edge = ("#ECD786" if primary else "#EFEDDF" if light else "#414947"), "#D2CBAE"
    elif state in ("focus", "pressed"):
        fill, edge = ("#C5A849" if primary else "#DEDCCB" if light else "#2D3433"), gold
    elif state == "error":
        edge = COLORS["error"]
    elif state == "disabled":
        fill, edge = ("#626760" if primary or not light else "#BEBFB4"), "#69716A"
    body = plate(1, 1, width-2, height-2, "#1F2727", cut=6)
    body += plate(2.5, 2.5, width-5, height-5, fill, edge, 1.1, 5)
    if light and not primary and state != "disabled":
        body += paper_surface(width, height, 5, .7)
    body += f'<path d="M9 7H{width-9}M9 {height-7}H{width-9}" stroke="{dark if light or primary else COLORS["inner"]}" stroke-opacity=".23" stroke-width=".7"/>'
    tick = edge if state in ("focus", "error", "hover") else COLORS["edge"]
    body += f'<path d="M8 13V19M{width-8} {height-19}V{height-13}" stroke="{tick}" stroke-width="1.2"/>'
    if state in ("focus", "error"):
        body += f'<path d="M6 7H16M6 7V17M{width-6} {height-7}H{width-16}M{width-6} {height-7}V{height-17}" fill="none" stroke="{edge}" stroke-width="2"/>'
    return body


def panel(width, height, light=True, semantic=None):
    body = plate(1, 1, width-2, height-2, "#202727", cut=8)
    body += plate(2.5, 2.5, width-5, height-5, COLORS["paper"] if light else COLORS["face"], COLORS["edge"], 1, 7)
    if light:
        body += paper_surface(width, height, 7)
    body += f'<path d="M14 8H{width-20}M20 {height-8}H{width-14}" stroke="{COLORS["dark"] if light else COLORS["ink"]}" stroke-opacity=".25" stroke-width=".7"/>'
    if semantic:
        color = COLORS[semantic]
        body += f'<path d="M11 17V{height-17}" stroke="{color}" stroke-width="3"/>'
        body += f'<path d="M{width-29} 9H{width-22}M{width-19} 9H{width-12}" stroke="{color}" stroke-width="2"/>'
    else:
        body += f'<path d="M11 13V20M8 16.5H14M{width-14} {height-16.5}H{width-8}" stroke="{COLORS["edge"]}" stroke-width=".8"/>'
    return body


GLYPHS = {
    "info": '<path d="M22 9H42L55 22V42L42 55H22L9 42V22ZM32 29V44M32 20V21"/>',
    "success": '<path d="M22 9H42L55 22V42L42 55H22L9 42V22ZM20 32L28 40L44 24"/>',
    "warning": '<path d="M32 9L57 52H7ZM32 24V36M32 44V45"/>',
    "error": '<path d="M22 9H42L55 22V42L42 55H22L9 42V22ZM23 23L41 41M41 23L23 41"/>',
    "pending": '<path d="M17 9H47M17 55H47M20 10V21L32 32L44 21V10M20 54V43L32 32L44 43V54M27 44H37"/>',
    "reconnect": '<path d="M49 23A20 20 0 0 0 15 17L9 24M9 13V24H20M15 41A20 20 0 0 0 49 47L55 40M55 51V40H44"/>',
    "offline": '<path d="M15 45V50H22V39M30 34V50H37V27M45 22V50H52V14M9 9L56 56"/>',
    "retry": '<path d="M12 27A22 22 0 1 1 14 46M9 13V28H24"/>',
    "save": '<path d="M12 10H44L54 20V54H10V10ZM21 10V27H43V10M19 54V36H45V54M35 16V21"/>',
    "chevron-down": '<path d="M15 24L32 41L49 24"/>',
    "check": '<path d="M14 33L27 46L51 19"/>',
    "close": '<path d="M18 18L46 46M46 18L18 46"/>',
}


def make_assets():
    for name, paths in GLYPHS.items():
        tones = [("light", COLORS["ink"]), ("dark", COLORS["dark"]), ("muted", COLORS["muted"])]
        if name in ("info", "success", "warning", "error"):
            tones.append(("semantic", COLORS[name]))
        for tone, color in tones:
            export(f"icon-{name}-{tone}", "icons", 64, 64,
                   f'<g fill="none" stroke="{color}" stroke-width="4.2" stroke-linecap="square" stroke-linejoin="miter">{paths}</g>', state=tone, role="semantic-icon")
    for name in ("info", "success", "warning", "error", "pending"):
        export(f"notice-{name}", "feedback", 400, 96, panel(400, 96, semantic=name), (22, 18, 22, 18), name, "notification-background")
    for tone in ("dark", "light"):
        export(f"tooltip-{tone}", "feedback", 320, 112, panel(320, 112, tone == "light"), (20, 18, 20, 18), tone, "tooltip-background")
    for name, path in (("up", "M2 11L12 1L22 11Z"), ("down", "M2 1L12 11L22 1Z")):
        export(f"tooltip-pointer-{name}", "feedback", 24, 12, f'<path d="{path}" fill="{COLORS["face"]}" stroke="{COLORS["edge"]}" stroke-width="1"/>', role="tooltip-pointer")
    for tone, color in (("gold", COLORS["gold"]), ("light", COLORS["ink"]), ("muted", COLORS["muted"]), ("dark", COLORS["dark"])):
        segments = ""
        for index in range(8):
            angle = math.pi*index/4
            x1, y1 = 32+math.sin(angle)*17, 32-math.cos(angle)*17
            x2, y2 = 32+math.sin(angle)*23, 32-math.cos(angle)*23
            segments += f'<path d="M{x1:.3f} {y1:.3f}L{x2:.3f} {y2:.3f}" opacity="{.16+.84*index/7:.3f}"/>'
        export(f"spinner-{tone}", "feedback", 64, 64, f'<g fill="none" stroke="{color}" stroke-width="4.2">{segments}</g>', role="rotating-indicator")
    for state in ("normal", "hover", "focus", "error", "disabled"):
        export(f"input-{state}", "forms", 256, 48, frame(256, 48, state), (20, 12, 20, 12), state, "input-or-dropdown-background")
    export("dropdown-panel", "forms", 256, 176, panel(256, 176), (20, 18, 20, 18), role="dropdown-background")
    for state in ("normal", "hover", "selected", "disabled"):
        fill = {"normal": "#DEDCCC", "hover": "#F0EAD4", "selected": COLORS["face"], "disabled": "#C4C4B9"}[state]
        body = plate(1, 1, 254, 38, fill, cut=3)
        if state == "selected":
            body += f'<path d="M7 10V30" stroke="{COLORS["gold"]}" stroke-width="3"/>'
        elif state == "hover":
            body += '<path d="M8 34H248" stroke="#B59538" stroke-width="1"/>'
        export(f"dropdown-row-{state}", "forms", 256, 40, body, (14, 6, 14, 6), state, "dropdown-item-background")
    for state in ("off", "on", "off-focus", "on-focus", "off-disabled", "on-disabled"):
        disabled, on = state.endswith("disabled"), state.startswith("on")
        fill = "#59615B" if disabled else COLORS["face"] if on else COLORS["paper"]
        edge = COLORS["gold"] if "focus" in state else "#858980"
        body = plate(1, 1, 26, 26, fill, edge, 1.2, 3)
        if on:
            body += f'<path d="M7 14L12 19L21 9" fill="none" stroke="{COLORS["muted"] if disabled else COLORS["gold"]}" stroke-width="2.6"/>'
        else:
            body += '<path d="M7 6H15M6 7V15" stroke="#92988C" stroke-width=".8"/>'
        export(f"checkbox-{state}", "forms", 28, 28, body, state=state, role="checkbox-full-state")
    for state in ("off", "on", "off-disabled", "on-disabled"):
        disabled, on = state.endswith("disabled"), state.startswith("on")
        edge = "#606B62" if disabled else COLORS["gold"] if on else COLORS["edge"]
        body = plate(1.5, 2, 49, 24, "#252D2D", edge, 1.2, 4)
        body += f'<path d="M9 14H42" stroke="{edge}" stroke-width="1"/>'
        body += f'<path d="M9 9V19M42 9V19" stroke="{edge}" stroke-width="1"/>'
        export(f"switch-track-{state}", "forms", 52, 28, body, (12, 8, 12, 8), state, "switch-track")
    for state in ("normal", "hover", "disabled"):
        fill = COLORS["muted"] if state == "disabled" else "#F0EDDD" if state == "hover" else COLORS["paper"]
        body = plate(1, 1, 18, 20, fill, COLORS["dark"], 1, 3)
        body += '<path d="M7 6V16M10 6V16M13 6V16" stroke="#757C73" stroke-width=".8"/>'
        export(f"switch-thumb-{state}", "forms", 20, 22, body, state=state, role="switch-handle")
    for state in ("track", "fill"):
        color = COLORS["gold"] if state == "fill" else "#202727"
        export(f"slider-{state}", "forms", 256, 8, plate(1, 1, 254, 6, color, "#7B827B" if state == "track" else "#DDC063", .8, 2), (5, 3, 5, 3), state, "slider-or-progress-track")
    for state in ("normal", "hover", "pressed", "disabled"):
        fill = {"normal": COLORS["paper"], "hover": "#F0EDDD", "pressed": COLORS["gold"], "disabled": COLORS["muted"]}[state]
        body = plate(1, 1, 18, 26, fill, COLORS["dark"], 1, 3)
        body += '<path d="M7 8V20M10 8V20M13 8V20" stroke="#596159" stroke-width=".8"/>'
        export(f"slider-thumb-{state}", "forms", 20, 28, body, state=state, role="slider-handle")
    for kind in ("action", "primary"):
        for state in ("normal", "hover", "pressed", "disabled"):
            export(f"button-{kind}-{state}", "buttons", 180, 48, frame(180, 48, state, light=False, primary=kind == "primary"), (20, 12, 20, 12), state, f"{kind}-button-background")


def copy_generated_panel():
    source = PACKAGE / "generated/dispatch-panel.png"
    target = DEST / "panels/dispatch-panel.png"
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, target)
    with Image.open(target) as image:
        width, height = image.size
    border = [80, 104, 80, 184]
    ASSETS.append({
        "id": "dispatch-panel", "category": "panels", "role": "dispatch-window-background",
        "png": target.relative_to(ROOT).as_posix(), "source": source.relative_to(ROOT).as_posix(),
        "guid": meta_for(target, border), "logicalSize": [width//2, height//2],
        "pixelSize": [width, height], "border": border, "pixelsPerUnit": 200,
        "notes": "内建图像生成工具；推荐固定 3:2 比例，九宫格仅供保持标题与角部的受控伸展。",
    })


def make_revision_assets():
    width, height = 1424, 60
    for state in ("info", "success", "warning", "error", "pending"):
        color = {"info": "#A6BCC0", "success": "#ABB58D", "warning": COLORS["gold"],
                 "error": "#D39785", "pending": COLORS["gold"]}[state]
        body = plate(1, 1, width-2, height-2, "#252C2C", "#858980", .9, 5)
        body += plate(4, 4, width-8, height-8, "#343A3B", "#555D59", .6, 3)
        body += f'<path d="M7 10V{height-10}" stroke="{color}" stroke-width="2"/>'
        body += f'<path d="M18 5H34M{width-34} {height-5}H{width-18}" stroke="{color}" stroke-opacity=".65" stroke-width=".8"/>'
        export(f"notice-bar-{state}", "feedback", width, height, body, (14, 10, 14, 10), state, "persistent-resolution-message-background")
    export("checkbox-on-plate", "forms", 28, 28,
           plate(1, 1, 26, 26, COLORS["face"], COLORS["edge"], 1.2, 3), role="checkbox-animation-face")
    export("checkbox-mark-gold", "forms", 28, 28,
           f'<path d="M7 14L12 19L21 9" fill="none" stroke="{COLORS["gold"]}" stroke-width="2.6"/>', role="checkbox-animation-mark")
    start_color, end_color = (227, 224, 209), (52, 58, 59)
    points = [(7, 14), (12, 19), (21, 9)]
    length1 = math.dist(points[0], points[1])
    length2 = math.dist(points[1], points[2])
    for index in range(8):
        progress = index/7
        eased = 1-(1-progress)**3
        fill = "#" + "".join(f"{round(a+(b-a)*eased):02X}" for a,b in zip(start_color, end_color))
        body = plate(1, 1, 26, 26, fill, COLORS["edge"], 1.2, 3)
        if index == 0:
            body += '<path d="M7 6H15M6 7V15" stroke="#92988C" stroke-width=".8"/>'
        else:
            distance = eased*(length1+length2)
            if distance < length1:
                t = distance/length1
                path = f'M7 14L{7+5*t:.3f} {14+5*t:.3f}'
            else:
                t = (distance-length1)/length2
                path = f'M7 14L12 19L{12+9*t:.3f} {19-10*t:.3f}'
            body += f'<path d="{path}" fill="none" stroke="{COLORS["gold"]}" stroke-width="2.6"/>'
        export(f"checkbox-transition-{index:02d}", "forms", 28, 28, body, state=f"{index}/7", role="checkbox-animation-frame")


def main():
    if "--revision-only" in sys.argv:
        existing = json.loads((PACKAGE / "manifest.json").read_text(encoding="utf-8"))
        ASSETS.extend(asset for asset in existing["assets"] if not (
            asset["id"].startswith(("notice-bar-", "checkbox-transition-")) or
            asset["id"] in ("checkbox-on-plate", "checkbox-mark-gold")))
    else:
        make_assets()
        copy_generated_panel()
    make_revision_assets()
    folder_meta(DEST)
    for directory in DEST.iterdir():
        if directory.is_dir():
            folder_meta(directory)
    problems = []
    for entry in ASSETS:
        with Image.open(ROOT / entry["png"]) as image:
            image.load()
            if image.mode != "RGBA" or list(image.size) != entry["pixelSize"]:
                problems.append(entry["id"] + ": format")
            if image.getpixel((0, 0))[3] != 0 or image.getchannel("A").getextrema()[1] == 0:
                problems.append(entry["id"] + ": alpha")
        left, bottom, right, top = entry["border"]
        if left+right >= entry["pixelSize"][0] or top+bottom >= entry["pixelSize"][1]:
            problems.append(entry["id"] + ": border")
    if problems:
        raise ValueError(", ".join(problems))
    manifest = {
        "name": "游城拓荒 · 移动城市调度终端皮肤补充包", "version": "2.1",
        "date": "2026-10-09", "status": "全部制作完成，用户复核通过；未接入正式对局",
        "renderScale": SCALE, "borderOrder": ["left", "bottom", "right", "top"],
        "palette": COLORS, "styleReferences": "style-references.md", "assets": ASSETS,
        "revision": {"scope": ["checkbox-animation", "persistent-resolution-message"],
                     "existingPngRedrawn": False, "messageSlotLogicalSize": [1424,60],
                     "checkboxSelectSeconds": .18, "checkboxCancelSeconds": .14},
    }
    (PACKAGE / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    report = {
        "assetCount": len(ASSETS), "categories": dict(Counter(a["category"] for a in ASSETS)),
        "pngAndAlpha": "通过", "sliceBounds": "通过", "sceneOrPrefabModified": False,
        "visualApproval": "全部制作完成，用户复核通过", "gameplayIntegration": "未执行",
    }
    (PACKAGE / "export-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
