"""对齐既有六棱柱的朝向与接触边，保留崩口及统一材质。"""
import bpy, bmesh, math, json
from pathlib import Path
from mathutils import Vector, Matrix, Quaternion

OUT=Path(__file__).resolve().parent
KEY='OrirockConcentration'
SOURCE=OUT.parent/'ContactFixRevision'/(KEY+'_ContactFixed.blend')
bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
bpy.context.preferences.filepaths.save_version=0
scene=bpy.context.scene
meshes=[o for o in scene.objects if o.type=='MESH']
front=next(o for o in meshes if '前方' in o.name)
right=next(o for o in meshes if '右后' in o.name)
left=next(o for o in meshes if '左后' in o.name)
stone=front.data.materials[0]

# 上次布尔裁切只处理穿插；这次恢复右柱被裁掉的内部部分，以完整棱柱调整摆放。
# 从已保存的相同崩口、相同统一材质阶段取网格，不重跑旧生成器。
mesh_name=right.data.name
with bpy.data.libraries.load(str(OUT.parent/'UniformMaterialRevision'/(KEY+'_UniformStone.blend')),link=False) as (src,dst):
    dst.meshes=[mesh_name]
restored=dst.meshes[0]
right.data.name=mesh_name+'_上阶段裁切记录'
right.data=restored
right.data.name=mesh_name
right.data.materials.clear();right.data.materials.append(stone)
for p in right.data.polygons:p.material_index=0

def parameters(obj):
    # 每个完整柱面可分成多块三角形，只取六个主体侧平面。
    planes={}
    for p in sorted(obj.data.polygons,key=lambda p:p.area,reverse=True):
        if abs(p.normal.z)>.00001 or p.area<.025:continue
        a=math.atan2(p.normal.y,p.normal.x)
        key=round(a,4)
        if key not in planes:planes[key]=(p.normal.copy(),p.normal.dot(p.center))
    assert len(planes)==6,(obj.name,len(planes))
    center=sum((n*d for n,d in planes.values()),Vector())/3
    center.z=0
    apothem=sum(d for n,d in planes.values())/6
    angles=[math.atan2(n.y,n.x)%(math.pi/3) for n,d in planes.values()]
    return center,apothem,sum(angles)/len(angles)

before={o.name:parameters(o) for o in meshes}
fc,fa,angle=before[front.name]
changes=[]
for obj in (left,right):
    center,apo,current=before[obj.name]
    delta=angle-current
    rotation=Matrix.Rotation(delta,3,'Z')
    for v in obj.data.vertices:v.co=center+rotation@(v.co-center)
    obj.data.update()
    changes.append(dict(name=obj.name,rotation_degrees=math.degrees(delta)))

rc,ra,_=parameters(right)
normal=Vector((math.cos(angle),math.sin(angle),0))
# 两条侧边平行且中心正对，无相交体积。原有细倒角自然形成接缝。
desired=fc+normal*(fa+ra)
shift=desired-rc
for v in right.data.vertices:v.co+=shift
right.data.update()
changes[-1]['translation']=list(shift)
right['接缝说明']='完整六棱柱与前柱平行边相接，不穿插、不裁成斜面。'
for obj in meshes:
    obj['结构']='同向六边形直棱柱，保留原有微倒角与浅崩口。'
    bm=bmesh.new();bm.from_mesh(obj.data)
    assert all(e.is_manifold for e in bm.edges),obj.name
    bm.free()
    obj.select_set(True)
bpy.context.view_layer.objects.active=right
bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(50),island_margin=.018)
bpy.ops.object.mode_set(mode='OBJECT')
for obj in scene.objects:obj.select_set(False)
scene.name='提纯源岩_棱柱对齐_统一材质'
scene.render.resolution_x=scene.render.resolution_y=900
scene.render.resolution_percentage=100
scene.cycles.samples=32
camera=scene.camera
camera.data.ortho_scale=2.35
original_camera=camera.matrix_world.copy()
scene.render.filepath=str(OUT/'正面_对齐预览.png')
bpy.ops.render.render(write_still=True)

# 用原验收视角观察底部；不靠旋转视图掩盖棱柱朝向差异。
view=json.loads((OUT.parent/'ContactFixRevision'/'contact_fix_info.json').read_text(encoding='utf-8'))['original_view'][0]
q=Quaternion(view['rotation'])
camera.rotation_euler=q.to_euler();camera.location=Vector(view['location'])+q@Vector((0,0,8))
engine=scene.render.engine
scene.render.engine='BLENDER_WORKBENCH'
scene.display.shading.light='STUDIO'
scene.display.shading.color_type='MATERIAL'
scene.display.shading.show_shadows=True
scene.display.shading.show_cavity=True
scene.render.filepath=str(OUT/'底面_对齐预览.png')
bpy.ops.render.render(write_still=True)
scene.render.engine=engine
camera.matrix_world=original_camera
for area in bpy.context.screen.areas:
    if area.type=='VIEW_3D':
        sp=area.spaces.active
        sp.region_3d.view_rotation=q
        sp.region_3d.view_location=(0,0,0)
        sp.region_3d.view_distance=2.3
        sp.region_3d.view_perspective='ORTHO'
        sp.shading.type='SOLID'
        sp.overlay.show_overlays=False
scene.render.filepath=str(OUT/'正面_对齐预览.png')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_AlignedReview.blend')))
report=dict(source=str(SOURCE),restored_right_mesh_from='UniformMaterialRevision',changes=changes,
    seam_normal=list(normal),contact_gap=0.0,triangles=sum(len(o.data.polygons) for o in meshes),
    parts=[dict(name=o.name,side_angle_degrees=math.degrees(parameters(o)[2]),chips=o.get('崩口数量'),triangles=len(o.data.polygons)) for o in meshes])
(OUT/'alignment_info.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(report,ensure_ascii=False),flush=True)
