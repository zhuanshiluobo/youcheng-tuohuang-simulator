"""用实际 Blender 渲染制作白模验收排版。"""
from PIL import Image,ImageDraw,ImageFont,ImageOps
from pathlib import Path
OUT=Path(__file__).resolve().parent
REF=Path(r'C:\Users\hby\.codex\attachments\f2e1e3bd-664e-4c04-8a58-9d7496f22982\image-1.png')
FONT=r'C:\Windows\Fonts\msyh.ttc'
def text(im,xy,label,size=27,color='#26333d'):
    ImageDraw.Draw(im).text(xy,label,font=ImageFont.truetype(FONT,size),fill=color)
def panel(canvas,path,rect,color):
    x,y,w,h=rect
    card=Image.new('RGBA',(w,h),color)
    im=Image.open(path).convert('RGBA')
    if im.getextrema()[3][0]==0:
        bbox=im.getchannel('A').getbbox()
        if bbox:im=im.crop(bbox)
    im=ImageOps.contain(im,(w-52,h-52),Image.Resampling.LANCZOS)
    card.alpha_composite(im,((w-im.width)//2,(h-im.height)//2))
    canvas.paste(card.convert('RGB'),(x,y))

board=Image.new('RGB',(1580,960),'#f2f4f5')
text(board,(38,25),'晶体电子单元 · 完整白模',38)
text(board,(40,86),'四根铜柱横纵双向居中  /  前后组件中轴对齐  /  标签与后部结构补全',24,'#52636c')
text(board,(40,146),'参考图')
text(board,(786,146),'完整白模 · 参考角度')
panel(board,REF,(38,194,706,658),'#ffffff')
panel(board,OUT/'FullWhite_Reference.png',(784,194,756,658),'#70777e')
text(board,(40,894),'统一白模验收；标签低清图案按可辨认的外框、圆环和中心几何形表达。',23,'#52636c')
board.save(OUT/'完整白模_验收对照.png')

board=Image.new('RGB',(1520,1480),'#f2f4f5')
text(board,(35,20),'晶体电子单元 · 四视图',36)
for key,label,x,y in [('Reference','参考角度',30,98),('Side','侧面',775,98),('Back','背面（无立柱）',30,784),('Top','俯视 · 四柱横纵双向居中',775,784)]:
    text(board,(x+7,y),label,25)
    panel(board,OUT/f'FullWhite_{key}.png',(x,y+44,712,590),'#70777e')
board.save(OUT/'完整白模_四视图.png')

if (OUT/'FullWhite_CenteredTop.png').exists() and (OUT/'FullWhite_LabelDetail.png').exists():
    board=Image.new('RGB',(1580,1000),'#f2f4f5')
    text(board,(35,20),'本轮修正 · 标签与后部组件',36)
    text(board,(37,78),'前后部件沿底座中轴对齐；以下均为实际白模渲染。',23,'#52636c')
    text(board,(38,130),'俯视 · 前后组合居中',25)
    panel(board,OUT/'FullWhite_CenteredTop.png',(30,175,660,760),'#70777e')
    text(board,(735,130),'后部 · 铜管、壳体与薄铁皮支撑',25)
    panel(board,OUT/'FullWhite_BracketDetail.png',(725,175,815,420),'#70777e')
    text(board,(735,625),'台面 · 标签浅浮雕',25)
    panel(board,OUT/'FullWhite_LabelDetail.png',(725,670,815,265),'#d9dcde')
    board.save(OUT/'标签与后部_修正验收.png')
print('完整白模验收图已生成')
