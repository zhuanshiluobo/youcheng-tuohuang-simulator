"""恢复正六棱柱与统一方向，保留崩口、统一材质和完整向下嵌合。"""
import bpy,bmesh,json,math
from pathlib import Path
from mathutils import Vector,Matrix

OUT=Path(__file__).resolve().parent
KEY='OrirockConcentration'
layout=json.loads((OUT/'layout.json').read_text(encoding='utf-8'))
SOURCE=OUT.parent/'UniformMaterialRevision'/(KEY+'_UniformStone.blend')
bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
bpy.context.preferences.filepaths.save_version=0
scene=bpy.context.scene
scene.name='提纯源岩_同向正六棱柱_完整嵌合'
parts=sorted([o for o in scene.objects if o.type=='MESH'],key=lambda o:0 if '左后' in o.name else 1 if '右后' in o.name else 2)
left,right,front=parts
stone=left.data.materials[0]
names=[o.data.name for o in parts]
report=[]
for obj,spec in zip(parts,layout['parts']):
    planes={}
    for p in sorted(obj.data.polygons,key=lambda p:p.area,reverse=True):
        if abs(p.normal.z)>1e-5 or p.area<.02:continue
        angle=round(math.atan2(p.normal.y,p.normal.x),4)
        if angle not in planes:planes[angle]=(p.normal.copy(),p.normal.dot(p.center))
    assert len(planes)==6,(obj.name,len(planes))
    ordered=[planes[k] for k in sorted(planes)]
    ring=[Matrix(((a.x,a.y),(b.x,b.y))).inverted()@Vector((d,e)) for (a,d),(b,e) in zip(ordered,ordered[1:]+ordered[:1])]
    center=sum(ring,Vector((0,0)))/6
    radius=sum((p-center).length for p in ring)/6
    p=ring[0]-center
    oldyaw=math.atan2(p.y,p.x)%(math.pi/3)
    turn=math.radians(layout['yaw_degrees'])-oldyaw
    rot=Matrix(((math.cos(turn),-math.sin(turn)),(math.sin(turn),math.cos(turn))))
    scale=spec['radius']/radius
    low=min(v.co.z for v in obj.data.vertices);high=max(v.co.z for v in obj.data.vertices)
    for v in obj.data.vertices:
        xy=Vector(spec['center'])+rot@(Vector(v.co[:2])-center)*scale
        v.co.x,v.co.y=xy
        v.co.z+=spec['top']-high if v.co.z>(low+high)/2 else spec['bottom']-low
    obj.data.update()
    for p in obj.data.polygons:p.material_index=0
    obj.data.materials.clear();obj.data.materials.append(stone)
    # 完整母体保留在源文件数据块中，避免联合后的遮挡面被误当作原始六角结构。
    source_mesh=obj.data.copy();source_mesh.name=obj.name+'_正六棱柱母体';source_mesh.use_fake_user=True
    obj['结构']='正六边形等截面直棱柱，三柱朝向一致；主侧壁竖直；整体向下嵌合。'
    obj['正六边形半径']=spec['radius'];obj['共同方向_度']=layout['yaw_degrees']
    obj['完整母体网格']=source_mesh.name
    obj['底部高度']=spec['bottom'];obj['顶部高度']=spec['top']
    obj['背面说明']='完整正六棱柱下延至共同底面；嵌合内部面从展示网格移除。'
    sides=[(ring[(i+1)%6]-ring[i]).length*scale for i in range(6)]
    report.append(dict(name=obj.name,source_mesh=source_mesh.name,radius=spec['radius'],yaw=layout['yaw_degrees'],side_length_error=max(sides)-min(sides),bottom=spec['bottom'],top=spec['top']))

# 对嵌合部分去除内表面，外面的六组竖直侧面仍保持共同方向。
labels=[stone.copy() for _ in parts]
for i,obj in enumerate(parts):
    obj.data.materials.clear()
    for tag in labels:obj.data.materials.append(tag)
    for p in obj.data.polygons:p.material_index=i
bpy.context.view_layer.objects.active=left
for other in (right,front):
    mod=left.modifiers.new('清除嵌合内部面','BOOLEAN');mod.operation='UNION';mod.solver='EXACT';mod.object=other
    bpy.ops.object.modifier_apply(modifier=mod.name)
bm=bmesh.new();bm.from_mesh(left.data)
bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=1e-6)
bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bmesh.ops.triangulate(bm,faces=list(bm.faces))
non_manifold=sum(not e.is_manifold for e in bm.edges)
todo=set(bm.verts);components=0
while todo:
    components+=1;stack=[todo.pop()]
    while stack:
        v=stack.pop()
        for e in v.link_edges:
            n=e.other_vert(v)
            if n in todo:todo.remove(n);stack.append(n)
assert non_manifold==0 and components==1,(non_manifold,components)
bm.to_mesh(left.data);bm.free()
whole=left.data.copy();whole.name='提纯源岩_同向六棱柱联合外壳';whole.use_fake_user=True
for i,obj in enumerate(parts):
    faces=[p for p in whole.polygons if p.material_index==i]
    indices=sorted({v for p in faces for v in p.vertices});lookup={v:j for j,v in enumerate(indices)}
    obj.data.name=names[i]+'_联合前'
    mesh=bpy.data.meshes.new(names[i]);mesh.from_pydata([tuple(whole.vertices[v].co) for v in indices],[],[tuple(lookup[v] for v in p.vertices) for p in faces]);mesh.update()
    obj.data=mesh;mesh.materials.append(stone)
whole.materials.clear();whole.materials.append(stone)
for p in whole.polygons:p.material_index=0
for obj in scene.objects:obj.select_set(obj in parts)
bpy.context.view_layer.objects.active=left
bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT');bpy.ops.uv.smart_project(angle_limit=math.radians(50),island_margin=.018);bpy.ops.object.mode_set(mode='OBJECT')
for obj in scene.objects:obj.select_set(False)
bpy.context.view_layer.objects.active=None
camera=scene.camera;camera.matrix_world=Matrix(layout['camera']['matrix']);camera.data.ortho_scale=layout['camera']['ortho_scale']
scene.render.resolution_x=940;scene.render.resolution_y=964;scene.render.resolution_percentage=100
scene.render.film_transparent=True;scene.cycles.samples=40
scene.render.filepath=str(OUT/'正面_同向六棱柱.png');bpy.ops.render.render(write_still=True)
front_camera=camera.matrix_world.copy();scale=camera.data.ortho_scale;engine=scene.render.engine
scene.render.engine='BLENDER_WORKBENCH';scene.display.shading.color_type='MATERIAL';scene.display.shading.light='STUDIO';scene.display.shading.show_cavity=True;scene.display.shading.show_shadows=False
for name,position in [('顶视_六角方向.png',(0,0,8)),('底视_接缝.png',(0,0,-8)),('侧视_竖直棱.png',(.8,-7,1.5))]:
    camera.location=position;camera.rotation_euler=(-camera.location).to_track_quat('-Z','Y').to_euler();camera.data.ortho_scale=2.15
    scene.render.filepath=str(OUT/name);bpy.ops.render.render(write_still=True)
camera.matrix_world=front_camera;camera.data.ortho_scale=scale;scene.render.engine=engine
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='VIEW_3D':
            s=area.spaces.active;r=s.region_3d;r.view_rotation=camera.rotation_euler.to_quaternion();r.view_perspective='CAMERA';r.view_distance=2.7;r.view_camera_zoom=0
            s.shading.type='SOLID';s.overlay.show_overlays=False
scene.render.filepath=str(OUT/'正面_同向六棱柱.png')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_AlignedHexReview.blend')))
(OUT/'geometry_review.json').write_text(json.dumps(dict(parts=report,components=components,non_manifold=non_manifold,triangles=sum(len(o.data.polygons) for o in parts),material=stone.name),ensure_ascii=False,indent=2),encoding='utf-8')
print('同向正六棱柱修正完成',flush=True)
