from pathlib import Path
from PIL import Image,ImageDraw,ImageFont

out=Path(__file__).resolve().parent
paths=[Path('C:/Users/hby/AppData/Local/Temp/codex-clipboard-e501a3d3-a238-42a4-a2b3-3d0af4d974b3.png'),out.parent/'ReferenceShapeRevision'/'正面_烘焙材质.png',out/'正面_比例修正.png']
board=Image.new('RGB',(1050,404),(247,248,249))
draw=ImageDraw.Draw(board)
font=ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',19)
for i,(path,title) in enumerate(zip(paths,['示意图','修改前（同角度）','修改后（同角度）'])):
    im=Image.open(path).convert('RGBA').resize((345,354),Image.Resampling.LANCZOS)
    board.paste(im,(i*350+2,42),im)
    draw.text((i*350+45,10),title,font=font,fill=(42,42,42))
board.save(out/'当前视角_参考与修改前后.png')
