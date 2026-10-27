"""按用户确认，将外壳改成上下等大的直八棱柱。"""
import bpy,json,math
from pathlib import Path
from mathutils import Vector
OUT=Path(__file__).resolve().parent
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'CrystallineElectronicUnit_BeforeStraightBase.blend'),copy=True)
body=bpy.data.objects['下收八角箱体']
for i in range(8):
    body.data.vertices[i].co.x=body.data.vertices[i+8].co.x
    body.data.vertices[i].co.y=body.data.vertices[i+8].co.y
body.data.update();body.name='上下等边_直八棱柱箱体'
bottom=bpy.data.objects['封闭八角底盖']
for v in bottom.data.vertices:
    radius=math.hypot(v.co.x,v.co.y)
    v.co.x*=1.675/radius;v.co.y*=1.675/radius
bottom.data.update()
for mod in bottom.modifiers:
    if mod.type=='BEVEL':mod.width=.006
rim=bpy.data.objects['底沿窄护边']
for i,v in enumerate(rim.data.vertices):
    target=1.675 if i<16 else 1.625
    radius=math.hypot(v.co.x,v.co.y)
    v.co.x*=target/radius;v.co.y*=target/radius
rim.data.update()
for mod in rim.modifiers:
    if mod.type=='BEVEL':mod.width=.006
for i in range(8):
    frame=bpy.data.objects[f'侧面_{i:02d}_局部基准']
    a=body.data.vertices[8+i].co.copy();b=body.data.vertices[8+(i+1)%8].co.copy()
    p=(a+b)/2;p.z=0
    frame.location=p
    frame.rotation_euler.x=0;frame.rotation_euler.y=0

notes=json.loads((OUT/'full_white_notes.json').read_text(encoding='utf-8'))
notes['user_confirmed']='整体八角形，背后无立柱；左后是水平底座和两侧铁皮支撑；外箱为上下同尺寸的直八棱柱，侧壁竖直。'
notes['base_revision']={'body_radius_top':1.66,'body_radius_bottom':1.66,'lid_radius':1.675,'bottom_cover_radius':1.675,'side_wall_taper':0}
(OUT/'full_white_notes.json').write_text(json.dumps(notes,ensure_ascii=False,indent=2),encoding='utf-8')
bpy.context.view_layer.update()
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'CrystallineElectronicUnit_FullWhite.blend'))
print('直八棱柱已保存：上下八角形对应顶点XY一致；上盖与底盖半径均1.675。')
