"""固定用户实际视角，修正前后比例；不再统一棱柱的方向。"""
import bpy,bmesh,json,math
from pathlib import Path
from mathutils import Vector,Matrix

OUT=Path(__file__).resolve().parent
KEY='OrirockConcentration'
state=json.loads((OUT/'current_user_view.json').read_text(encoding='utf-8'))
bpy.ops.wm.open_mainfile(filepath=str(OUT/(KEY+'_UserViewBaseline.blend')))
bpy.context.preferences.filepaths.save_version=0
scene=bpy.context.scene
camera=scene.camera
# 从用户窗口的实际 view_matrix 同步，不沿用另猜的相机角度。
camera.matrix_world=Matrix(state['views'][0]['view_matrix']).inverted()
camera.data.ortho_scale=state['camera']['ortho_scale']
q=camera.matrix_world.to_quaternion()
R=q@Vector((1,0,0));U=q@Vector((0,1,0))
P=Matrix(((R.x,R.y),(U.x,U.y))).inverted()
meshes=[o for o in scene.objects if o.type=='MESH']
scale=json.loads((OUT.parent/'ReferenceShapeRevision'/'reference_shape_info.json').read_text(encoding='utf-8'))['fit_scale']
units_per_pixel=.01*scale

def ring(obj):
    planes={}
    for p in sorted(obj.data.polygons,key=lambda p:p.area,reverse=True):
        if abs(p.normal.z)>1e-5 or p.area<.02:continue
        angle=round(math.atan2(p.normal.y,p.normal.x),4)
        if angle not in planes:planes[angle]=(p.normal.copy(),p.normal.dot(p.center))
    assert len(planes)==6,(obj.name,len(planes))
    ordered=[planes[k] for k in sorted(planes)]
    points=[Matrix(((n.x,n.y),(n2.x,n2.y))).inverted()@Vector((d,d2)) for (n,d),(n2,d2) in zip(ordered,ordered[1:]+ordered[:1])]
    center=sum(points,Vector((0,0)))/6
    def pixel(p):return Vector((R.dot(Vector((p.x,p.y,0))),-U.dot(Vector((p.x,p.y,0)))))
    pc=pixel(center)
    points.sort(key=lambda p:math.atan2((pixel(p)-pc).y,(pixel(p)-pc).x))
    i=min(range(6),key=lambda i:pixel(points[i]).x)
    return points[i:]+points[:i]

changes=[]
front=next(o for o in meshes if '前方' in o.name)
old=ring(front)
# 轮廓列表从左后角开始，ring() 则从最左角开始；先对应同一个实体角。
old=old[1:]+old[:1]
center=sum(old,Vector((0,0)))/6
# 实际参考中后角约在 y112、右转角约在 y145，旧版误放为 y121、154。
before_pixels=[(54,121),(107,119),(143,154),(130,181),(65,186),(32,151)]
after_pixels=[(53,112),(105,110),(145,145),(130,181),(65,185),(30,145)]
target=[p+P@Vector(((b[0]-a[0])*units_per_pixel,-(b[1]-a[1])*units_per_pixel)) for p,a,b in zip(old,before_pixels,after_pixels)]
tc=sum(target,Vector((0,0)))/6
for v in front.data.vertices:
    xy=Vector((v.co.x,v.co.y))-center
    candidates=[]
    for i in range(6):
        a=old[i]-center;b=old[(i+1)%6]-center
        uv=Matrix(((a.x,b.x),(a.y,b.y))).inverted()@xy
        error=max(0,-uv.x)+max(0,-uv.y)+max(0,uv.x+uv.y-1)
        candidates.append((error,i,uv))
    error,i,uv=min(candidates,key=lambda a:a[0])
    assert error<.002
    p=tc+(target[i]-tc)*uv.x+(target[(i+1)%6]-tc)*uv.y
    v.co.x=p.x;v.co.y=p.y
front.data.update()
changes.append(dict(name=front.name,old_outline=before_pixels,new_outline=after_pixels,height_unchanged=True))

for obj in meshes:
    if obj==front:continue
    r=ring(obj);center=sum(r,Vector((0,0)))/6
    factor=.975 if '左后' in obj.name else .985
    for v in obj.data.vertices:
        v.co.x=center.x+(v.co.x-center.x)*factor
        v.co.y=center.y+(v.co.y-center.y)*factor
    obj.data.update()
    changes.append(dict(name=obj.name,footprint_scale=factor,height_unchanged=True,orientation_unchanged=True))

# 图中右后柱在前块上方的可见短壁约13像素。旧版前块放得过低，
# 使右壁深入前块顶面，缩掉其可见面积。沿同一视线移动前块，保持
# 所有已校准轮廓像素位置不变，只恢复两者正确的遮挡深度。
right=next(o for o in meshes if '右后' in o.name)
delta_z=max(v.co.z for v in right.data.vertices)-max(v.co.z for v in front.data.vertices)-13*units_per_pixel/U.z
delta_xy=P@Vector((0,-U.z*delta_z))
for v in front.data.vertices:
    v.co+=Vector((delta_xy.x,delta_xy.y,delta_z))
front.data.update()
changes.append(dict(name=front.name,occlusion_shift=list(Vector((delta_xy.x,delta_xy.y,delta_z))),
    projected_outline_preserved=True,right_front_visible_wall_pixels=13))

for obj in meshes:
    bm=bmesh.new();bm.from_mesh(obj.data)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    assert all(e.is_manifold for e in bm.edges),obj.name
    bm.to_mesh(obj.data);bm.free();obj.data.update()
    obj['比例说明']='在用户实际视角下分别校正前后占比，保留独立朝向和15处原有崩口。'
    obj.select_set(True)
bpy.context.view_layer.objects.active=front
bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(50),island_margin=.018)
bpy.ops.object.mode_set(mode='OBJECT')
for obj in scene.objects:obj.select_set(False)
scene.name='提纯源岩_用户视角_比例修正'
scene.cycles.samples=48
scene.render.filepath=str(OUT/'正面_比例修正.png')
bpy.ops.render.render(write_still=True)
engine=scene.render.engine
scene.render.engine='BLENDER_WORKBENCH'
scene.display.shading.light='STUDIO';scene.display.shading.color_type='MATERIAL'
scene.display.shading.show_shadows=False;scene.display.shading.show_cavity=False
scene.render.filepath=str(OUT/'正面_无投影轮廓.png')
bpy.ops.render.render(write_still=True)
scene.render.engine=engine
scene.render.filepath=str(OUT/'正面_比例修正.png')
for area in bpy.context.screen.areas:
    if area.type=='VIEW_3D':
        s=area.spaces.active;r=s.region_3d;view=state['views'][0]
        r.view_rotation=q;r.view_location=Vector(view['location']);r.view_distance=view['distance']
        r.view_perspective=view['perspective'];r.view_camera_zoom=view['camera_zoom'];r.view_camera_offset=view['camera_offset']
        s.shading.type=view['shading'];s.overlay.show_overlays=False
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_ViewSizeReview.blend')))
(OUT/'view_size_info.json').write_text(json.dumps(dict(source=state['source'],user_view=state['views'][0],changes=changes,
    triangles=sum(len(o.data.polygons) for o in meshes),chips=sum(o.get('崩口数量',0) for o in meshes)),ensure_ascii=False,indent=2),encoding='utf-8')
print('用户视角比例修正版已保存',flush=True)
