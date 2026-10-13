"""原主岩不动：左右两块恢复六面方块，取消额外扇形切面。"""
from pathlib import Path
import bpy
from mathutils import Euler

OUT = Path(__file__).resolve().parent
OUT.mkdir(exist_ok=True)
previous = bpy.data.scenes.get('固源岩组_顶部保留_下部实体修订')
if previous is None:
    with bpy.data.libraries.load(str(OUT.parent/'SolidRevision'/'OrirockCluster_TopPreserved.blend'), link=False) as (available, target):
        target.scenes = available.scenes
    previous = target.scenes[0]
previous_core = next(o for o in previous.objects if o.type == 'MESH' and o.name.startswith('下部_连续实体基岩'))
if bpy.data.scenes.get('固源岩组_正面对齐版.001') is None:
    with bpy.data.libraries.load(str(OUT.parent/'OrirockCluster_Finished.blend'), link=False) as (available, target):
        target.scenes = available.scenes

template = (OUT.parent/'SolidRevision'/'complete_cluster_lower.py').read_text(encoding='utf-8')
FULL_LOWER = globals().get('FULL_LOWER', False)
if FULL_LOWER:
    template = template.replace("replaced = ('左前_灰青断岩', '后右_黄褐边岩', '内部_裂隙基岩')",
        "replaced = ('左前_灰青断岩','后右_黄褐边岩','内部_裂隙基岩','左侧_低位横岩','中间_横向搭接岩','右前_浅色顶面暗色断岩','前中_深色裂块','右下_收尾碎岩')")
square_geometry = r'''
# 六个连续平面：三角化只为导出，同一个平面不再制造材质拼色或折面。
BOX_FACES = [[0,1,2,3],[4,7,6,5],[0,4,5,1],[1,5,6,2],[2,6,7,3],[3,7,4,0]]

def front_square(name, origin, screen_u, screen_v, thickness, face_materials):
    a = point(origin)
    u = right*(screen_u[0]*.016) - up*(screen_u[1]*.016) + front*screen_u[2]
    v_xy = right*(screen_v[0]*.016) - up*(screen_v[1]*.016)
    # u/v 正交，完整矩形正面，不引入中心隆起或多边形棱角。
    v = v_xy + front*(-u.dot(v_xy)/screen_u[2])
    w = u.cross(v).normalized()*thickness
    if w.dot(front) > 0:
        w = -w
    verts=[a,a+u,a+u+v,a+v]
    verts += [p+w for p in verts]
    obj=mesh_object(name,verts,BOX_FACES,face_materials)
    obj['结构']='完整六面方块；每个主面共面，不用三角隆起模拟岩石。'
    return obj

front_square('左前_灰青方块', (13,112,.45), (37,-6,.18), (8,34), .62,
             [7,7,3,2,2,7])

# 右块同样是完整方块，朝向和遮挡形成图中窄的浅黄面，
# 不把这条可见面再折成多个三角面或尖角。
a=point((127,75,-.22))
u=right*(30*.016)+front*.25
v=right*(-5*.016)-up*(61*.016)-front*.08
w=right*(14*.016)+up*(9*.016)-front*.75
vertices=[a,a+u,a+u+v,a+v]
vertices += [p+w for p in vertices]
right_box=mesh_object('后右_黄褐方块',vertices,BOX_FACES,[2,1,4,0,1,3])
right_box['结构']='完整六面块体；灰色正面、窄浅黄右侧、灰褐顶面，每个主面连续共面。'

# 下部衔接体仍沿用上一阶段的真实体积，不把新方块做成正面薄片。
core=previous_core.copy()
core.data=previous_core.data.copy()
parts_collection.objects.link(core)
core.parent=authoring_root
core.name='下部_连续实体基岩_保留'
indices=[p.material_index for p in core.data.polygons]
core.data.materials.clear()
for mat in materials:
    core.data.materials.append(mat)
for face,index in zip(core.data.polygons,indices):
    face.material_index=index
parts.append(core)
'''
if FULL_LOWER:
    square_geometry += r'''
# 整体下部对照草稿：主岩与左中薄断片仍保持原样。
# 底部轮廓按原图收回，避免把原图的落地阴影误建成向下突出的碎块。
for vertex in core.data.vertices:
    y=91-vertex.co.dot(up)/.016
    if y>144:
        new_y=144+(y-144)*.30
        vertex.co += up*((y-new_y)*.016)
core.data.update()

def planar_box(name, xy, depth_plane, back_offset, indices):
    c0,cx,cy,x0,y0=depth_plane
    verts=[point((x,y,c0+cx*(x-x0)+cy*(y-y0))) for x,y in xy]
    w=right*(back_offset[0]*.016)-up*(back_offset[1]*.016)+front*back_offset[2]
    verts += [p+w for p in verts]
    return mesh_object(name,verts,BOX_FACES,indices)

planar_box('左上_连续平面方岩',[(17,96),(55,89),(57,104),(13,110)],
           (.38,.0025,.012,35,103),(2,6,-.40),[4,2,3,3,2,2])
planar_box('中间_灰褐搭接方岩',[(81,92),(108,78),(132,82),(117,96)],
           (.38,.0015,.003,100,88),(0,10,-.32),[4,2,3,1,3,2])

# 原图这里是短而窄的浅色碎片，不是旧版横跨整块的亮色三角折面。
chip_mat=materials[0].copy()
chip_mat.name='固源岩组_中间短片_浅灰褐'
nodes,links=chip_mat.node_tree.nodes,chip_mat.node_tree.links
bsdf=next(n for n in nodes if n.type=='BSDF_PRINCIPLED')
base=bsdf.inputs['Base Color'].links[0].from_socket
mix=nodes.new('ShaderNodeMixRGB')
mix.blend_type='MULTIPLY'
mix.inputs[0].default_value=1
mix.inputs[2].default_value=(.77,.79,.76,1)
links.new(base,mix.inputs[1])
links.new(mix.outputs[0],bsdf.inputs['Base Color'])
materials.append(chip_mat)
planar_box('中间_窄浅色短片',[(80,94),(94,96),(97,105),(89,107)],
           (.53,.003,.004,87,100),(0,3,-.30),[8,3,3,3,3,2])

planar_box('右前_连续白顶方岩',[(104,100),(120,100),(143,126),(123,126)],
           (.53,-.003125,.0119951923,104,100),(-14,16,-.54),[0,2,3,1,2,7])
a=point((84,112,.70))
u=right*(9*.016)-up*(5*.016)+front*.09
v=right*(-7*.016)-up*(25*.016)-front*.05
w=right*(7*.016)+up*(2*.016)-front*.33
verts=[a,a+u,a+u+v,a+v]
verts += [p+w for p in verts]
mesh_object('前中_短灰黑方岩',verts,BOX_FACES,[6,2,3,7,2,2])
'''
start=template.index('def rock_volume(')
end=template.index('# 只合并新制作的下部三件')
code=template[:start]+square_geometry+'\n'+template[end:]
code=code.replace("bpy.data.scenes.new('固源岩组_顶部保留_下部实体修订')", "bpy.data.scenes.new('固源岩组_顶部保留_左右方块修订')")
if FULL_LOWER:
    code=code.replace('固源岩组_顶部保留_左右方块修订','固源岩组_顶部保留_下部正面对齐方块')
code=code.replace('OrirockCluster_TopPreserved.blend','OrirockCluster_SquareRevision.blend')
code=code.replace('_顶部保留.png','_方块修订.png')
code=code.replace('局部制作状态.json','方块制作状态.json')
code=code.replace('局部修订信息.json','方块修订信息.json')
code=code.replace("changes=['保留原主岩全部几何、UV、材质和姿态','仅修左前灰青岩和右侧边岩','补完整下部衔接和背侧体积']", "changes=['保留原主岩全部几何、UV、材质和姿态','左右两块改为完整六面方块，主面共面','取消额外尖角和三角隆起，不改变其余断片','保留下部衔接体与完整侧背厚度']")
code=code.replace('bpy.app.timers.register(finish,first_interval=1)', '''
def preview_square():
    scene.camera.location=camera_location
    scene.camera.rotation_euler=camera_quat.to_euler()
    scene.render.filepath=str(OUT/'正面_方块结构待对照.png')
    bpy.ops.render.render(write_still=True)
    for area in bpy.context.screen.areas:
        if area.type=='VIEW_3D':
            space=area.spaces.active
            space.shading.type='MATERIAL'
            space.overlay.show_overlays=False
            space.region_3d.view_rotation=camera_quat
            space.region_3d.view_location=front*(-.05)
            space.region_3d.view_distance=4.7
            space.region_3d.view_perspective='ORTHO'
    status('方块正面预览已生成，尚未验收或导入',geometry=geometry)
    return None
bpy.app.timers.register(preview_square,first_interval=1)
''')
namespace=dict(__file__=str(Path(__file__).resolve()),__name__='__main__',previous_core=previous_core,Euler=Euler)
exec(compile(code,str(Path(__file__).resolve()),'exec'),namespace)
bpy.app.driver_namespace['cluster_square_revision']=namespace
