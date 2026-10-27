"""仅根据本轮两张附件制作顶部白模；不读取或覆盖原有模型。"""
import bpy
import bmesh
import math
import json
from pathlib import Path
from mathutils import Vector

OUT = Path(__file__).resolve().parent
# 此脚本只在独立后台 Blender 中运行；不清空用户正在编辑的会话。
if not bpy.app.background:
    raise RuntimeError('仅允许在独立后台进程中运行本生成脚本。')
startup_scenes = tuple(bpy.data.scenes)
scene = bpy.data.scenes.new('顶部青色部件_白模验收')
if bpy.context.window:
    bpy.context.window.scene = scene
for startup in startup_scenes:
    # 只解除此后台进程自带的默认场景；没有加载用户文件。
    bpy.data.scenes.remove(startup)
model = bpy.data.collections.new('模型_仅顶部层片')
scene.collection.children.link(model)
studio = bpy.data.collections.new('观察相机与灯光_不属于模型')
scene.collection.children.link(studio)

white = bpy.data.materials.new('白模_中性哑光')
white.diffuse_color = (.73, .73, .73, 1)
white.use_nodes = True
bsdf = next(n for n in white.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
bsdf.inputs['Base Color'].default_value = (.73, .73, .73, 1)
bsdf.inputs['Roughness'].default_value = .72
bsdf.inputs['Metallic'].default_value = 0

root = bpy.data.objects.new('顶部白模_整体', None)
model.objects.link(root)
root['说明'] = '窄矩形顶端，逐层展开并错转；背面与层内孔为单视图推定。'
root['制作范围'] = '仅青色顶部；不含底座、金属柱、标牌或圆环。'

N = 32
H = 1.72
THICKNESS = .032
BEAM = .029

def layer_parameters(t):
    # t=0 为顶端，t=1 为最下层。
    width = .70 + .79 * t ** 1.22
    depth = .125 + 1.20 * t ** 1.02
    angle = math.radians(-66 * t ** 1.18)
    return width, depth, angle

def frame_mesh(name, w, d, thickness, beam):
    outer = [(-w/2,-d/2),(w/2,-d/2),(w/2,d/2),(-w/2,d/2)]
    inner = [(-w/2+beam,-d/2+beam),(w/2-beam,-d/2+beam),
             (w/2-beam,d/2-beam),(-w/2+beam,d/2-beam)]
    verts = [(x,y,z) for z in (-thickness/2,thickness/2) for loop in (outer,inner) for x,y in loop]
    faces = []
    for i in range(4):
        j=(i+1)%4
        faces.extend([(i,j,j+8,i+8),(i+4,i+12,j+12,j+4),
                      (i+8,j+8,j+12,i+12),(i+4,j+4,j,i)])
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts,[],faces)
    mesh.update()
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bm.to_mesh(mesh)
    bm.free()
    return mesh

for i in range(N):
    t = i / (N-1)
    w,d,angle = layer_parameters(t)
    mesh = frame_mesh(f'第{i+1:02d}层_矩形薄框网格', w,d,THICKNESS,BEAM + .066*t)
    obj = bpy.data.objects.new(f'层片_{i+1:02d}_由顶至底',mesh)
    model.objects.link(obj)
    obj.parent = root
    obj.location.z = THICKNESS/2 + H*(1-t)
    obj.rotation_euler.z = angle
    mesh.materials.append(white)
    bevel=obj.modifiers.new('极小倒角_保留薄片棱线','BEVEL')
    bevel.width=.0025
    bevel.segments=2
    obj['层序_顶部为1']=i+1

world=bpy.data.worlds.new('白模中性环境')
world.use_nodes=True
bg=next(n for n in world.node_tree.nodes if n.type=='BACKGROUND')
bg.inputs['Color'].default_value=(.19,.19,.19,1)
bg.inputs['Strength'].default_value=.55
scene.world=world
scene.render.engine='CYCLES'
scene.cycles.samples=48
scene.cycles.use_denoising=True
scene.render.resolution_x=1000
scene.render.resolution_y=1000
scene.render.resolution_percentage=100
scene.render.image_settings.file_format='PNG'
scene.view_settings.view_transform='AgX'
scene.render.film_transparent=True

target=Vector((0,0,.69))
def aim(obj, point):
    obj.rotation_euler=(Vector(point)-obj.location).to_track_quat('-Z','Y').to_euler()

for name,loc,power,size in [('主光',(-3,-4,6),550,4),('右侧补光',(4,-1,3.7),230,3),('后轮廓光',(1,4,5),430,3)]:
    data=bpy.data.lights.new(name,'AREA')
    data.energy=power
    data.shape='DISK'
    data.size=size
    ob=bpy.data.objects.new(name,data)
    studio.objects.link(ob)
    ob.location=loc
    aim(ob,target)

views={
    'Reference':((0,-7,4.73),2.58,'参考角度'),
    'Side':((6,-1,3.8),2.58,'侧面观察'),
    'Back':((0,7,4.73),2.58,'背面观察_推定'),
    'Top':((0,0,7),2.20,'顶部观察'),
}
cameras={}
for key,(loc,scale,label) in views.items():
    data=bpy.data.cameras.new(label)
    data.type='ORTHO'
    data.ortho_scale=scale
    cam=bpy.data.objects.new(label,data)
    studio.objects.link(cam)
    cam.location=loc
    aim(cam, target if key!='Top' else (0,0,0))
    cameras[key]=cam

scene.camera=cameras['Reference']
for ob in scene.objects:
    ob.select_set(False)
root.select_set(True)
bpy.context.view_layer.objects.active=root
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='VIEW_3D':
            space=area.spaces.active
            space.region_3d.view_perspective='CAMERA'
            space.overlay.show_overlays=False
            space.shading.type='SOLID'
            space.shading.color_type='MATERIAL'
            space.shading.light='STUDIO'
            space.shading.show_shadows=True
            space.shading.show_cavity=True

bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'CrystallineElectronicUnit_TopWhite.blend'))
for key in views:
    if '--pilot' in __import__('sys').argv and key != 'Reference':
        continue
    scene.camera=cameras[key]
    scene.render.filepath=str(OUT/f'TopWhite_{key}.png')
    bpy.ops.render.render(write_still=True,scene=scene.name)
scene.camera=cameras['Reference']
manifest={'scope':'仅顶部白模','source':'用户本轮附件 image-1.png 与 image-2.png',
          'shape':'上窄下宽，窄矩形截面逐层展开并绕竖轴错转，前角形成弧形轨迹。',
          'layer_count':N,'height':H+THICKNESS,'top_dimensions':list(layer_parameters(0)[:2]),
          'bottom_dimensions':list(layer_parameters(1)[:2]),'total_turn_degrees':66,
          'uncertain':'两图为同一视角；背面、底部、层内孔及精确层数为白模推定，等待用户验收。',
          'no_other_asset_writes':True}
(OUT/'shape_notes.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
print('TOP_WHITE_COMPLETE',str(OUT))
