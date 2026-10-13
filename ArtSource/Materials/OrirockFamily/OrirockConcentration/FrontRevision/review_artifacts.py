from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
import json
import numpy as np

out=Path(__file__).resolve().parent
reference=out.parent.parent/'reference'/'OrirockConcentration_PRTS.png'
ref=Image.open(reference).convert('RGBA')
front=Image.open(out/'正面_最终_游戏贴图.png').convert('RGBA')
draft=Image.open(out/'正面_修订01.png').convert('RGBA')
font=ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',23)
small=ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',17)
canvas=Image.new('RGB',(1140,670),(22,26,31))
draw=ImageDraw.Draw(canvas)
draw.text((35,20),'PRTS 原始图标参考',font=font,fill=(235,231,220))
draw.text((605,20),'提纯源岩修订 · 实际烘焙贴图渲染',font=font,fill=(235,231,220))
canvas.paste(ref.resize((549,549),Image.Resampling.NEAREST),(10,67),ref.resize((549,549),Image.Resampling.NEAREST))
def on_reference_frame(image):
    bbox=image.getchannel('A').point(lambda x:255 if x>128 else 0).getbbox()
    crop=image.crop(bbox)
    # Align object bounds to the visible material bounds of the supplied icon.
    crop=crop.resize((447,477),Image.Resampling.LANCZOS)
    framed=Image.new('RGBA',(549,549),(0,0,0,0))
    framed.alpha_composite(crop,(51,21))
    return framed
framed=on_reference_frame(front)
canvas.paste(framed,(580,67),framed)
draw.text((35,635),'紫色稀有度圆环不属于石料模型；参考图只有 183×183 像素。',font=small,fill=(173,177,181))
canvas.save(out/'参考对照_最终.png')
texture_stats={}
sheet=Image.new('RGB',(1064,1144),(25,29,34))
sd=ImageDraw.Draw(sheet)
for n,(label,name) in enumerate([('BaseColor · sRGB','BaseColor'),('Normal · 非颜色','Normal'),('AO · 非颜色','AO'),('金属度 R / 光滑度 A','MetallicSmoothness')]):
    path=out/'textures'/('OrirockConcentration_FrontRevision_'+name+'.png')
    im=Image.open(path).convert('RGBA')
    a=np.asarray(im)
    texture_stats[name]={'size':list(im.size),'RGB_min':a[:,:,:3].min(axis=(0,1)).tolist(),'RGB_max':a[:,:,:3].max(axis=(0,1)).tolist(),'alpha_min':int(a[:,:,3].min()),'alpha_max':int(a[:,:,3].max())}
    preview=im.convert('RGB') if name!='MetallicSmoothness' else im.getchannel('A').convert('RGB')
    x=20+(n%2)*532;y=20+(n//2)*572
    sd.text((x,y),label,font=font,fill=(230,230,226))
    sheet.paste(preview.resize((512,512),Image.Resampling.LANCZOS),(x,y+40))
sheet.save(out/'贴图检查_四通道.png')
info=json.loads((out/'revision_info.json').read_text(encoding='utf-8'))
old=json.loads((out/'source_inspection.json').read_text(encoding='utf-8'))
points=[p for o in old['objects'] for p in o.get('bounds',[])]
old_size=[max(p[i] for p in points)-min(p[i] for p in points) for i in range(3)]
info['renders']['reference_comparison']=str(out/'参考对照_最终.png')
info['renders']['texture_inspection']=str(out/'贴图检查_四通道.png')
info['texture_content_inspection']=texture_stats
info['size_change']={'old_source_bounds_size':old_size,'new_source_bounds_size':info['bounds']['size'],'maximum_axis_ratio':max(info['bounds']['size'])/max(old_size),'note':'单位与根缩放保持一致；仅几何最大尺寸缩小约22.5%；收藏室若重新自动归一化尺寸，仍需主代理另调展示尺寸。'}
info['reference_features']=[
    {'part':'左后块','top_outline_px':[[17.5,40],[39.5,10.5],[84,7],[113.5,32.5],[97,62.5],[45.5,67]],'top_width_px':96,'top_height_px':60,'visible_front_wall_px':24.5,'notes':'宽顶、短侧壁；右侧顶边斜切；前壁从深褐上阶连续转入凹阶；左侧是灰蓝断面。'},
    {'part':'右后块','top_outline_px':[[112,35],[130.5,34.5],[164,58.5],[153,92.5],[99.5,97],[80.5,79]],'top_width_px':83.5,'top_height_px':62.5,'visible_front_wall_px':40,'notes':'左后顶角被左后块遮住；右上斜边较长；上壁褐橄榄，下部凹阶及局部灰蓝矿心与前块相接。'},
    {'part':'前块','top_outline_px':[[39.5,84],[81.5,82.5],[112,110.5],[100.5,137.5],[47.5,142],[20.5,109]],'top_width_px':91.5,'top_height_px':59.5,'visible_front_wall_px':23,'notes':'顶面前缘更宽、轻斜；后缘被两块后石挡住，呈切出的可见角；侧壁深褐，左断面灰蓝。'}
]
info['visual_review']={
    'actually_viewed':['PRTS 原始图标及放大图','旧收藏室实际截图','正面修订01','正面修订02程序源','正面修订03程序源','最终烘焙贴图正面','背侧实体渲染','BaseColor UV图集','最终参考并排对照','四张贴图通道检查图'],
    'iteration_01_findings':['首轮轮廓与遮挡接近参考，但表面颗粒不足','侧壁阶线过于整齐，前块侧壁偏亮'],
    'iteration_02_changes':['增强可烘焙的细密颗粒，降低大尺度云状色差','阶线改为共享边界的连续不齐凹阶','前块前壁改深褐色，右块下部加入连续灰蓝断面'],
    'iteration_02_findings':['并排对照后左后与前块顶面偏亮','右块预览投影比图标更暗','源材质色差经过图标尺寸缩小后仍不足'],
    'iteration_03_changes':['按顶面参考色关系调整三块独立褐金/棕褐材质','使用更接近顶光的中性预览，缩减右块顶面投影','增加空间连续的矿质团簇色差；没有随机三角面材质'],
    'result':'已实际对照正面渲染：三块顶面的不同宽高、短壁及遮挡较旧规整柱体接近图标；未把导出成功当作造型一致。',
    'remaining_differences':['程序和烘焙渲染的投影与软阴影不同于图标绘制光影','顶面微崩口与矿心纹理为立体化近似，不保证逐像素吻合','背面、底面、绝对厚度及看不见的接触位置为单视图推定','本任务不操作Unity，收藏室实际显示尺寸与颜色待主代理导入后观察']
}
info['delivery_inspection']=str(out/'delivery_inspection.json')
(out/'revision_info.json').write_text(json.dumps(info,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'textures':texture_stats,'old_bounds':old_size,'size_ratio':info['size_change']['maximum_axis_ratio']},ensure_ascii=False))
