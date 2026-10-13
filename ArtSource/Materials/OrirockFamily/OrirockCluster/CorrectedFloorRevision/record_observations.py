"""只拼接真实参考与渲染用于对照，并更新当前阶段记录。"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
import json

OUT = Path(__file__).resolve().parent
reference = OUT.parents[1] / 'reference' / 'OrirockCluster_PRTS.png'
font = ImageFont.truetype(r'C:\Windows\Fonts\msyh.ttc', 24)
tile = 549
canvas = Image.new('RGB', (tile*2+60, tile+80), (44,44,44))
draw = ImageDraw.Draw(canvas)
for x,path,label in ((20, reference, '原图（圆环不属于模型）'),
                      (tile+40, OUT/'新地板_材质正面.png', '当前模型（实际 Blender 渲染）')):
    draw.text((x,15),label,font=font,fill=(240,240,240))
    image = Image.open(path).convert('RGBA').resize((tile,tile),Image.Resampling.LANCZOS)
    canvas.paste(image,(x,60),image)
canvas.save(OUT/'新地板_正面对照.png')

state = json.loads((OUT/'制作状态.json').read_text(encoding='utf-8'))
progress_path = OUT.parents[4]/'Artifacts'/'OrirockClusterSquare'/'验收进度.json'
progress = json.loads(progress_path.read_text(encoding='utf-8'))
# 不把旧任务调度状态混入模型的实际验收状态。
progress.pop('goal_status',None)
progress.update(stage='左下已前移；重定地板观察基准；两块完整后撑不同朝向，正面未验收。',
    model_scene=state['scene'],source=str(OUT/'OrirockCluster_CorrectedFloor.blend'),
    floor_z=0,model_min_z=state['geometry']['model_zmin'],posterior_cubes=2,
    primary_changes='左下沿原观察方向前移0.76；上部和右部关系不动；模型与相机整体刚体对齐新接地平面。',
    front_comparison=str(OUT/'新地板_正面对照.png'),side_comparison=str(OUT/'新地板_侧面接地.png'),
    rear_comparison=str(OUT/'新地板_背面支撑.png'),geometry=state['geometry'],
    plane_truncations=state['cuts'],top_right_relative_unchanged=True,left_foreground_fixed=True,
    poses_unchanged=False,blocking_condition_resolved=True,full_reference_match_accepted=False,
    ground_camera_elevation_degrees=state['ground_alignment']['elevation'],
    materials_unchanged=True,material_changes='沿用前阶段石质材质；本次未制作新纹理。',
    remaining='中下方暗部仍与原图有差距，正面整体不能判定通过。',
    unity_status='未导入、未覆盖正式04模型、Excel、UI或场景。')
progress_path.write_text(json.dumps(progress,ensure_ascii=False,indent=2),encoding='utf-8')
print(str(OUT/'新地板_正面对照.png'))
