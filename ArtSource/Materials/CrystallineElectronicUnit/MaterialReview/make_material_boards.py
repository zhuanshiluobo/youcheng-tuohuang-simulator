"""使用参考图和实际 Blender 渲染，生成材质阶段验收排版。"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont, ImageOps


OUT = Path(__file__).resolve().parent
REFERENCE = Path(
    r"C:\Users\hby\.codex\attachments\f2e1e3bd-664e-4c04-8a58-9d7496f22982\image-1.png"
)
FONT = r"C:\Windows\Fonts\msyh.ttc"
PAGE = "#edf0f2"
CARD = "#343434"
INK = "#263139"
SECONDARY = "#58656e"
VIEWS = (
    ("Reference", "参考角度"),
    ("Side", "侧面"),
    ("Back", "背面"),
    ("Top", "俯视"),
)


def label(canvas, position, content, size=27, color=INK):
    ImageDraw.Draw(canvas).text(
        position, content, font=ImageFont.truetype(FONT, size), fill=color
    )


def panel(canvas, source, rect, padding=34):
    x, y, width, height = rect
    card = Image.new("RGBA", (width, height), CARD)
    with Image.open(source) as original:
        picture = original.convert("RGBA")
    # 只裁去透明画布，保留参考原图和真实渲染中的全部可见内容。
    alpha = picture.getchannel("A")
    if alpha.getextrema()[0] == 0:
        bounds = alpha.getbbox()
        if bounds:
            picture = picture.crop(bounds)
    picture = ImageOps.contain(
        picture, (width - padding * 2, height - padding * 2), Image.Resampling.LANCZOS
    )
    card.alpha_composite(
        picture, ((width - picture.width) // 2, (height - picture.height) // 2)
    )
    canvas.paste(card.convert("RGB"), (x, y))


def main():
    inputs = [REFERENCE] + [OUT / f"Material_{key}.png" for key, _ in VIEWS]
    missing = [str(path) for path in inputs if not path.is_file()]
    if missing:
        raise FileNotFoundError("等待材质渲染输入：\n" + "\n".join(missing))

    comparison = Image.new("RGB", (1760, 1080), PAGE)
    label(comparison, (40, 26), "晶体电子单元 · 材质验收", 40)
    label(comparison, (42, 88), "中央凹槽加深；前侧与左右后侧采用内嵌散热槽。", 25, SECONDARY)
    label(comparison, (42, 147), "原始参考", 28)
    label(comparison, (914, 147), "材质版 · 实际渲染", 28)
    panel(comparison, REFERENCE, (40, 193, 806, 768))
    panel(comparison, OUT / "Material_Reference.png", (912, 193, 806, 768))
    label(comparison, (42, 1004), "青绿晶体层片  /  抛光金属台面  /  铜件与深色支撑", 25, SECONDARY)
    comparison.save(OUT / "材质版_参考对照.png")

    views = Image.new("RGB", (1720, 1730), PAGE)
    label(views, (40, 25), "晶体电子单元 · 材质四视图", 40)
    label(views, (42, 85), "中央凹槽加深；前侧与左右后侧采用内嵌散热槽。", 25, SECONDARY)
    positions = ((40, 148), (880, 148), (40, 896), (880, 896))
    for (key, title), (x, y) in zip(VIEWS, positions):
        label(views, (x + 3, y), title, 27)
        panel(views, OUT / f"Material_{key}.png", (x, y + 47, 800, 660), padding=30)
    label(views, (42, 1652), "四个视角均来自同一材质版 Blender 文件。", 24, SECONDARY)
    views.save(OUT / "材质版_四视图.png")
    print("材质版参考对照与四视图已生成。")


if __name__ == "__main__":
    main()
