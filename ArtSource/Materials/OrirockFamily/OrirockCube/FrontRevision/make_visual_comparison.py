"""仅用于查看：并排放置参考与实际渲染，绝不作为模型或纹理。"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
import sys
OUT=Path(__file__).resolve().parent
render=OUT/(sys.argv[1] if len(sys.argv)>1 else 'OrirockCube_Front_v01.png')
ref=Image.open(OUT.parent.parent/'reference/OrirockCube_PRTS.png').convert('RGBA')
im=Image.open(render).convert('RGBA')
bbox=im.getchannel('A').getbbox()
im=im.crop(bbox)
im.thumbnail((610,610),Image.Resampling.LANCZOS)
canvas=Image.new('RGB',(1440,800),(38,40,43))
ref=ref.resize((720,720),Image.Resampling.NEAREST)
canvas.paste(ref,(0,50),ref)
canvas.paste(im,(720+(720-im.width)//2,50+(720-im.height)//2),im)
font=ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',26)
draw=ImageDraw.Draw(canvas)
draw.text((30,12),'PRTS 原参考（含圆环）',font=font,fill=(235,235,235))
draw.text((750,12),'固源岩实际正面渲染',font=font,fill=(235,235,235))
canvas.save(OUT/('comparison_'+render.stem+'.png'))
