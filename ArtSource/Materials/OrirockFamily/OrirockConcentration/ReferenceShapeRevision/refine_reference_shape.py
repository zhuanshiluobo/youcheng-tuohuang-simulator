"""按用户图分别匹配六边截面和厚度；撤销统一朝向，不增加或删除已有崩口。"""
import bpy, bmesh, json, math
from pathlib import Path
from mathutils import Vector, Matrix

OUT=Path(__file__).resolve().parent
KEY='OrirockConcentration'
SOURCE=OUT.parent/'UniformMaterialRevision'/(KEY+'_UniformStone.blend')
bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
bpy.context.preferences.filepaths.save_version=0
scene=bpy.context.scene
scene.name='提纯源岩_参考轮廓与独立朝向'
meshes=[o for o in scene.objects if o.type=='MESH']
camera=scene.camera
q=camera.rotation_euler.to_quaternion()
screen_right=q@Vector((1,0,0));screen_up=q@Vector((0,1,0))
inv=Matrix(((screen_right.x,screen_right.y),(screen_up.x,screen_up.y))).inverted()
PX=.01
WIDTH,HEIGHT=235,241
specs={
    '左后':dict(ring=[(26,58),(54,21),(111,16),(146,48),(125,86),(61,92)],top=.50,wall_pixels=30.5),
    '右后':dict(ring=[(144,52),(169,50),(210,81),(196,124),(130,131),(106,108)],top=.15,wall_pixels=46),
    '前方':dict(ring=[(54,121),(107,119),(143,154),(130,181),(65,186),(32,151)],top=-.18,wall_pixels=29),
}

def base_ring(obj):
    planes={}
    for p in sorted(obj.data.polygons,key=lambda p:p.area,reverse=True):
        if abs(p.normal.z)>1e-5 or p.area<.025:continue
        angle=round(math.atan2(p.normal.y,p.normal.x),4)
        if angle not in planes:planes[angle]=(p.normal.copy(),p.normal.dot(p.center))
    assert len(planes)==6
    ordered=[planes[k] for k in sorted(planes)]
    ring=[]
    for (n,d),(n2,d2) in zip(ordered,ordered[1:]+ordered[:1]):
        ring.append(Matrix(((n.x,n.y),(n2.x,n2.y))).inverted()@Vector((d,d2)))
    center=sum(ring,Vector((0,0)))/6
    def pixel(p):
        v=Vector((p.x,p.y,0))
        return Vector((screen_right.dot(v),-screen_up.dot(v)))
    pc=pixel(center)
    ring.sort(key=lambda p:math.atan2((pixel(p)-pc).y,(pixel(p)-pc).x))
    leftmost=min(range(6),key=lambda i:pixel(ring[i]).x)
    return ring[leftmost:]+ring[:leftmost]

records=[]
for obj in meshes:
    label=next(k for k in specs if k in obj.name)
    spec=specs[label]
    old=base_ring(obj)
    old_center=sum(old,Vector((0,0)))/6
    target=[inv@Vector(((x-WIDTH/2)*PX,(HEIGHT/2-y)*PX-screen_up.z*spec['top'])) for x,y in spec['ring']]
    target_center=sum(target,Vector((0,0)))/6
    zmin=min(v.co.z for v in obj.data.vertices);zmax=max(v.co.z for v in obj.data.vertices)
    height=spec['wall_pixels']*PX/screen_up.z
    bottom=spec['top']-height
    for vert in obj.data.vertices:
        xy=Vector((vert.co.x,vert.co.y))-old_center
        candidates=[]
        for i in range(6):
            a=old[i]-old_center;b=old[(i+1)%6]-old_center
            uv=Matrix(((a.x,b.x),(a.y,b.y))).inverted()@xy
            penalty=max(0,-uv.x)+max(0,-uv.y)+max(0,uv.x+uv.y-1)
            candidates.append((penalty,i,uv))
        error,i,uv=min(candidates,key=lambda p:p[0])
        assert error<.002,(obj.name,error)
        newxy=target_center+(target[i]-target_center)*uv.x+(target[(i+1)%6]-target_center)*uv.y
        vert.co=(newxy.x,newxy.y,bottom+(vert.co.z-zmin)/(zmax-zmin)*height)
    obj.data.update()
    obj['结构']='按参考分别匹配的六边形直棱柱，保持上下等截面；不强制正六边形或统一方向。'
    obj['接触说明']='各柱保留原图错位关系；不同底高避免原共面底部显示冲突。'
    records.append(dict(name=obj.name,reference_top_pixels=spec['ring'],wall_pixels=spec['wall_pixels'],top=spec['top'],bottom=bottom,chips=obj.get('崩口数量')))

# 模型整体仍保持既有最大尺寸；相机同步换算，便于同框对照用户参考。
points=[v.co.copy() for o in meshes for v in o.data.vertices]
low=Vector(tuple(min(p[i] for p in points) for i in range(3)))
high=Vector(tuple(max(p[i] for p in points) for i in range(3)))
center=(low+high)/2;scale=1.647727/max(high-low)
for obj in meshes:
    for v in obj.data.vertices:v.co=(v.co-center)*scale
    bm=bmesh.new();bm.from_mesh(obj.data)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    assert all(e.is_manifold for e in bm.edges),obj.name
    bm.to_mesh(obj.data);bm.free();obj.data.update()
    obj.select_set(True)
bpy.context.view_layer.objects.active=meshes[0]
bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(50),island_margin=.018)
bpy.ops.object.mode_set(mode='OBJECT')
for obj in scene.objects:obj.select_set(False)
target=-center*scale
camera.location=target+q@Vector((0,0,8))
camera.data.ortho_scale=WIDTH*PX*scale
scene.render.resolution_x=WIDTH*4
scene.render.resolution_y=HEIGHT*4
scene.render.resolution_percentage=100
scene.render.film_transparent=True
scene.cycles.samples=48
scene.render.filepath=str(OUT/'正面_参考轮廓.png')
bpy.ops.render.render(write_still=True)

for area in bpy.context.screen.areas:
    if area.type=='VIEW_3D':
        space=area.spaces.active
        space.region_3d.view_rotation=q
        space.region_3d.view_location=target
        space.region_3d.view_distance=2.7
        space.region_3d.view_perspective='CAMERA'
        space.shading.type='RENDERED'
        space.shading.use_scene_world=True;space.shading.use_scene_lights=True
        space.overlay.show_overlays=False
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_ReferenceShapeReview.blend')))
(OUT/'reference_shape_info.json').write_text(json.dumps(dict(source=str(SOURCE),reference_image='codex-clipboard-e501a3d3-a238-42a4-a2b3-3d0af4d974b3.png',parts=records,
    fit_scale=scale,triangles=sum(len(o.data.polygons) for o in meshes),notes='保留15处原有浅崩口和统一材质；仅调整各自的六边截面、位置与厚度，不统一朝向。'),ensure_ascii=False,indent=2),encoding='utf-8')
print('参考轮廓预览已保存',flush=True)
