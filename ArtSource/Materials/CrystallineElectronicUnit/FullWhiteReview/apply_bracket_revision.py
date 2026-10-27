"""按用户澄清，只修正左后组件为水平底座、两侧直立铁皮支撑。"""
import bpy,json
from pathlib import Path
from mathutils import Matrix
OUT=Path(__file__).resolve().parent
assembly=bpy.data.objects['左后倾斜模块_整体']
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'CrystallineElectronicUnit_BeforeBracketRevision.blend'),copy=True)

def resize_box(ob,size):
    spans=[max(v.co[i] for v in ob.data.vertices)-min(v.co[i] for v in ob.data.vertices) for i in range(3)]
    ob.data.transform(Matrix.Diagonal((*[size[i]/spans[i] for i in range(3)],1)))
    ob.data.update()

assembly.name='左后水平底座与双侧铁皮支撑_整体'
assembly.rotation_euler.x=0
assembly.rotation_euler.y=0
assembly.location.z=.900
assembly['结构说明']='水平底座贴合八角台面；轴向两端为直立薄铁皮支架。参考图中的斜向为透视与平面朝向。'
floor=bpy.data.objects['倾斜模块_承载底板']
floor.name='支架_水平底座'
for side in (-1,1):
    old_name='倾斜模块_侧护边'+str(side*.402)
    ob=bpy.data.objects[old_name]
    ob.name='支架_左侧直立铁皮' if side<0 else '支架_右侧直立铁皮'
    resize_box(ob,(.036,.735,.620))
    ob.location=(side*.342,0,.345)
for j in (1,2):
    for ob in assembly.children:
        if ob.name==f'后置横向绕组_{j}' or ob.name.startswith(f'后置绕组_{j}_'):
            ob.location.z=.465
hood=bpy.data.objects['倾斜模块_后部罩块']
hood.name='支架_后部罩块';hood.location.z=.675
for side,old_name in [(-1,'倾斜模块_前支脚'),(1,'倾斜模块_后支脚')]:
    ob=bpy.data.objects[old_name]
    ob.name='支架_左侧底部折边' if side<0 else '支架_右侧底部折边'
    ob.parent=assembly
    ob.rotation_euler=(0,0,0);ob.scale=(1,1,1)
    resize_box(ob,(.115,.735,.020))
    ob.location=(side*.355,0,.038)
bpy.data.collections['新增_左后倾斜电气组件'].name='新增_左后水平底座与铁皮支撑'

notes=json.loads((OUT/'full_white_notes.json').read_text(encoding='utf-8'))
notes['scope']='完整白模：八角箱体、凹槽台面、4 根分段立柱、左后水平底座与两侧铁皮支撑、侧面铭牌和散热槽。'
notes['user_confirmed']='整体八角形，背后无立柱；左后组件不是倾斜体，是水平底座和两侧铁皮支撑结构。'
notes['bracket_revision']='取消组件俯仰；底板底面位于 0.865，贴合台面；两块铁皮从底板向上竖立，绕组轴线水平。'
(OUT/'full_white_notes.json').write_text(json.dumps(notes,ensure_ascii=False,indent=2),encoding='utf-8')
bpy.context.view_layer.update()
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'CrystallineElectronicUnit_FullWhite.blend'))
print('水平底座与双侧直立铁皮支撑已修正并保存')
