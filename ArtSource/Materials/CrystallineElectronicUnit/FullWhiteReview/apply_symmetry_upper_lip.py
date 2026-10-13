"""按标注修正四柱镜像和上盖外立边，保留下箱体与接缝。"""
import bpy,json,math
from pathlib import Path
from mathutils import Vector
OUT=Path(__file__).resolve().parent
if not bpy.app.background:raise RuntimeError('仅在独立后台执行，以免干扰用户正在打开的其他模型。')
bpy.ops.wm.open_mainfile(filepath=str(OUT/'CrystallineElectronicUnit_FullWhite.blend'))
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'CrystallineElectronicUnit_BeforeSymmetryUpperLip.blend'),copy=True)
scene=bpy.context.scene
DZ=.075
ANGLE=math.radians(22.5)
u=Vector((math.cos(ANGLE),math.sin(ANGLE)))
v=Vector((-math.sin(ANGLE),math.cos(ANGLE)))
post_positions=[]
for side,side_name in [(-1,'左'),(1,'右')]:
    for along,row_name in [(.16,'后'),(-.46,'前')]:
        label=side_name+'侧'+row_name+'柱'
        p=u*(side*1.22)+v*along
        post_positions.append([label,p.x,p.y])
        for obj in bpy.data.collections['新增_四根单段铜柱'].objects:
            if obj.name.startswith(label+'_'):
                obj.location.x=p.x;obj.location.y=p.y
                obj.location.z+=DZ
                obj.rotation_euler.z=ANGLE

deck=bpy.data.objects['八角台面_宽斜面凹口']
before=[tuple(vert.co) for vert in deck.data.vertices]
# 六圈剖面中只抬高顶面及凹口，保持外下沿 .800 与接缝不动。
for index in range(8,40):deck.data.vertices[index].co.z+=DZ
deck.data.update()
bpy.data.objects['中央凹口封闭槽底'].location.z+=DZ
bpy.data.objects['顶部白模_整体'].location.z+=DZ
bpy.data.objects['左后水平底座与双侧铁皮支撑_整体'].location.z+=DZ
for obj in bpy.data.collections['新增_铭牌与侧壁散热槽'].objects:
    if obj.name.startswith(('台面小识别牌','识别牌边线','台面三角标记_')):
        obj.location.z+=DZ

# 取景角不变，仅平移半个增高量使完整白模仍在画面中央。
for name in ['参考角度','侧面观察','背面观察_推定']:
    bpy.data.objects[name].location.z+=DZ/2
bpy.context.view_layer.update()

mirror_errors=[]
for row in ['后','前']:
    left=bpy.data.objects['左侧'+row+'柱_单段铜柱身'].location
    right=bpy.data.objects['右侧'+row+'柱_单段铜柱身'].location
    lp=Vector((left.x,left.y));expected=lp-u*(2*lp.dot(u))
    mirror_errors.append((expected-Vector((right.x,right.y))).length)
print('铜柱镜像中心最大误差:',max(mirror_errors))
print('上盖外侧边:',deck.data.vertices[0].co.z,deck.data.vertices[8].co.z)
print('上下盖外轮廓XY变化:',max(math.hypot(vertex.co.x-before[i][0],vertex.co.y-before[i][1]) for i,vertex in enumerate(deck.data.vertices)))
notes=json.loads((OUT/'full_white_notes.json').read_text(encoding='utf-8'))
notes['symmetry_upper_lip_revision']={
    'symmetry':'沿底座局部前后中轴镜像；局部横轴绕世界Z旋转22.5度',
    'local_post_positions':[[-1.22,.16],[-1.22,-.46],[1.22,.16],[1.22,-.46]],
    'world_post_positions':post_positions,'front_back_spacing':.62,
    'mirror_error':max(mirror_errors),'lid_outer_bottom_z':.8,'lid_outer_top_z':.94,
    'lid_side_height_before':.065,'lid_side_height_after':.14,'height_added':DZ,
    'lower_housing_and_panels_unchanged':True}
notes['top_change']='仅整体上移至 Z=0.820，保持相对台面的嵌入深度；32 层网格、相对位置、角度及比例未改。'
notes['real_parts_revision']['post_positions']=post_positions
notes['real_parts_revision']['rear_assembly_position']=list(bpy.data.objects['左后水平底座与双侧铁皮支撑_整体'].location)
notes['bracket_revision']='水平底板底面位于 Z=0.940，贴合加高后的台面；两侧铁皮直立，绕组轴线水平。'
(OUT/'full_white_notes.json').write_text(json.dumps(notes,ensure_ascii=False,indent=2),encoding='utf-8')
scene.camera=bpy.data.objects['参考角度']
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'CrystallineElectronicUnit_FullWhite.blend'))
print('SYMMETRY_UPPER_LIP_COMPLETE')
