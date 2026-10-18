"""Encode the native sprite frames as a small looping review animation."""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

PACKAGE = Path(__file__).resolve().parent
ROOT = PACKAGE.parents[2]
SPRITES = ROOT / "Assets/YC/Presentation/CommonUi/Sprites/FrontierSupplementV2/forms"
FONT = ROOT / "Assets/YC/Presentation/CommonUi/Fonts/MicrosoftYaHei-Regular-face0.ttf"


def frame(index, caption):
    image = Image.new("RGB", (600, 144), "#292F30")
    glyph = Image.open(SPRITES / f"checkbox-transition-{index:02d}.png").convert("RGBA")
    image.paste(glyph.resize((112,112), Image.Resampling.LANCZOS), (30,16), glyph.resize((112,112), Image.Resampling.LANCZOS))
    draw = ImageDraw.Draw(image)
    draw.text((180,35), caption, font=ImageFont.truetype(str(FONT),30), fill="#ECEBDA")
    draw.text((181,86), "复选框动效 · 放大示意", font=ImageFont.truetype(str(FONT),17), fill="#A7ADA6")
    return image


images = [frame(0,"未选中")]
durations = [700]
for index in range(1,8):
    images.append(frame(index,"选中"))
    durations.append(700 if index==7 else (20 if index%2 else 30))
for index in range(6,-1,-1):
    images.append(frame(index,"取消"))
    durations.append(700 if index==0 else 20)
images[0].save(PACKAGE / "checkbox-motion.gif", save_all=True, append_images=images[1:], duration=durations, loop=0, disposal=2)
print("checkbox-motion.gif")
