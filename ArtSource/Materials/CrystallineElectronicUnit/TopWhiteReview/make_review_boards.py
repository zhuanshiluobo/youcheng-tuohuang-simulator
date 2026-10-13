"""把实际 Blender 渲染排成验收图；不修改模型或重绘参考图。"""
from PIL import Image, ImageDraw, ImageFont, ImageOps
from pathlib import Path

OUT = Path(__file__).resolve().parent
REF = Path(r'C:\Users\hby\.codex\attachments\fc076ab3-9ed3-4282-9987-0cec334c91e0\image-1.png')
FONT = r'C:\Windows\Fonts\msyh.ttc'

def label(canvas, xy, text, size=26, fill='#26333d'):
    ImageDraw.Draw(canvas).text(xy,text,font=ImageFont.truetype(FONT,size),fill=fill)

def panel(canvas,path,box,background):
    x,y,w,h=box
    card=Image.new('RGBA',(w,h),background)
    im=Image.open(path).convert('RGBA')
    # 渲染使用透明背景；按 alpha 裁掉空白后等比排版。
    if im.getextrema()[3][0] == 0:
        bbox=im.getchannel('A').getbbox()
        if bbox:
            im=im.crop(bbox)
    im=ImageOps.contain(im,(w-64,h-64),Image.Resampling.LANCZOS)
    card.alpha_composite(im,((w-im.width)//2,(h-im.height)//2))
    canvas.paste(card.convert('RGB'),(x,y))

sheet=Image.new('RGB',(1440,990),'#f2f4f5')
label(sheet,(46,24),'顶部白模 · 结构验收',40)
label(sheet,(47,84),'窄矩形顶端  /  向下展开  /  逐层转向',24,'#52636c')
label(sheet,(50,148),'参考图',27)
label(sheet,(639,148),'白模 · 接近参考的观察角度',27)
panel(sheet,REF,(46,193,553,696),'#ffffff')
panel(sheet,OUT/'TopWhite_Reference.png',(637,193,757,696),'#70777e')
label(sheet,(48,915),'仅制作顶部青色部件；背面、层内孔和精确层数按单视图推定。',23,'#52636c')
sheet.save(OUT/'顶部白模_验收对照.png')

board=Image.new('RGB',(1440,1480),'#f2f4f5')
label(board,(40,20),'顶部白模 · 多角度观察',36)
for key,caption,x,y in [('Reference','参考角度',35,95),('Side','侧面',735,95),
                         ('Back','背面（推定）',35,775),('Top','俯视（可见逐层错转）',735,775)]:
    label(board,(x+10,y),caption,25)
    panel(board,OUT/f'TopWhite_{key}.png',(x,y+44,670,596),'#70777e')
board.save(OUT/'顶部白模_四视图.png')
print('验收排版已完成')
