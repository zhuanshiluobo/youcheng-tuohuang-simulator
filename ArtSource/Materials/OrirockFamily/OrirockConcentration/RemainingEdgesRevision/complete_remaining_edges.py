"""沿用每块岩石已确认的顶缘崩口，补齐底缘及纵棱，逐块加工。"""
import bpy,bmesh,json,math,random
from pathlib import Path
from mathutils import Vector

OUT=Path(__file__).resolve().parent
KEY='OrirockConcentration'
SOURCE=OUT.parent/'PerRockChipsRevision'/(KEY+'_PerRockChips.blend')
bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
bpy.context.preferences.filepaths.save_version=0
scene=bpy.data.scenes['提纯源岩_逐块制作参考崩口']
if bpy.context.window:bpy.context.window.scene=scene
scene.name='提纯源岩_逐块补齐底缘与纵棱缺口'
parts=sorted([o for o in scene.objects if o.type=='MESH'],key=lambda o:0 if '左后' in o.name else 1 if '右后' in o.name else 2)
names=[o.data.name for o in parts]
stone=parts[0].data.materials[0]
layout=json.loads((OUT.parent/'AlignedHexRevision/layout.json').read_text(encoding='utf-8'))
editable=bpy.data.scenes['逐块编辑_完整岩柱']
rng=random.Random(101317)
changes=[]

def recalc(mesh):
    bm=bmesh.new();bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bm.to_mesh(mesh);bm.free();mesh.update()

def side_chip(obj,center,along,inward,outward,width,inset,depth):
    # 使用现有顶缘缺口的同一楔形，转到竖直棱两侧的局部坐标中。
    shift=rng.uniform(-.17,.17)*width
    local=[(-.59*width,-.018,.006),(.52*width,-.018,.006),
           (shift+.06*width,inset,.004),(shift-.22*width,inset*.52,.005),
           (shift-.12*width,-.024,-depth)]
    bm=bmesh.new()
    for u,v,z in local:bm.verts.new(center+along*u+inward*v+outward*z)
    bmesh.ops.convex_hull(bm,input=list(bm.verts));bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    mesh=bpy.data.meshes.new('纵棱局部浅缺口刀具');bm.to_mesh(mesh);bm.free();mesh.materials.append(stone)
    cutter=bpy.data.objects.new(mesh.name,mesh);scene.collection.objects.link(cutter)
    bpy.context.view_layer.objects.active=obj
    mod=obj.modifiers.new('独立加工_竖棱缺口','BOOLEAN');mod.operation='DIFFERENCE';mod.solver='EXACT';mod.object=cutter
    bpy.ops.object.modifier_apply(modifier=mod.name)
    bpy.data.objects.remove(cutter,do_unlink=True);bpy.data.meshes.remove(mesh)
    changes.append(dict(part=obj.name,position=list(center),width=width,inset=inset,depth=depth))

reports=[]
for owner,(obj,spec) in enumerate(zip(parts,layout['parts'])):
    mother=bpy.data.meshes[obj['完整母体网格']]
    obj.data=mother.copy();obj.data.materials.clear();obj.data.materials.append(stone)
    for p in obj.data.polygons:p.material_index=0
    bottom,top=spec['bottom'],spec['top']
    # 上半块沿中面镜像到底部并焊合，使底缘精确沿用现有顶缘缺口。
    # 主体等截面，不改变六个侧平面；避免共面布尔产生碎边。
    mid=(top+bottom)/2
    bm=bmesh.new();bm.from_mesh(obj.data)
    bmesh.ops.bisect_plane(bm,geom=list(bm.verts)+list(bm.edges)+list(bm.faces),dist=1e-7,plane_co=Vector((0,0,mid)),plane_no=Vector((0,0,1)),clear_inner=True,clear_outer=False)
    assert min(v.co.z for v in bm.verts)>mid-1e-5
    duplicated=bmesh.ops.duplicate(bm,geom=list(bm.verts)+list(bm.edges)+list(bm.faces))
    for element in duplicated['geom']:
        if isinstance(element,bmesh.types.BMVert):element.co.z=2*mid-element.co.z
    bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=1e-6)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    assert all(e.is_manifold for e in bm.edges),(obj.name,'底缘镜像')
    bm.to_mesh(obj.data);bm.free();obj.data.update()
    center=Vector((*spec['center'],0))
    ring=[center+Vector((math.cos(math.radians(layout['yaw_degrees']+k*60)),math.sin(math.radians(layout['yaw_degrees']+k*60)),0))*spec['radius'] for k in range(6)]
    old_top_chips=[14,13,15][owner]
    spacing=6*spec['radius']/old_top_chips
    per_edge=max(1,round((top-bottom)/spacing))
    for k,p in enumerate(ring):
        inward=(ring[(k+1)%6]-p).normalized()
        outward=-(ring[(k-1)%6]-p).normalized()
        for j in range(per_edge):
            fraction=(j+1)/(per_edge+1)+rng.uniform(-.033,.033)
            pos=Vector((p.x,p.y,bottom+(top-bottom)*fraction))
            side_chip(obj,pos,Vector((0,0,1)),inward,outward,rng.uniform(.033,.066),rng.uniform(.014,.025),rng.uniform(.021,.034))
    bm=bmesh.new();bm.from_mesh(obj.data)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bmesh.ops.dissolve_limit(bm,angle_limit=.00001,verts=list(bm.verts),edges=list(bm.edges))
    bmesh.ops.triangulate(bm,faces=list(bm.faces))
    nonmanifold=sum(not e.is_manifold for e in bm.edges)
    assert nonmanifold==0,(obj.name,nonmanifold)
    bm.to_mesh(obj.data);bm.free();obj.data.update()
    completed=obj.data.copy();completed.name=obj.name+'_各边缘独立完成母体';completed.use_fake_user=True
    obj['完整母体网格']=completed.name
    obj['本轮纵棱缺口数']=6*per_edge
    obj['缺口说明']='顶部原缺口保留，底缘沿用本块顶缘大小与密度；六条纵棱按相近间距分布浅小缺口。'
    editobj=next(o for o in editable.objects if o.type=='MESH' and o.name.startswith(obj.name))
    editobj.data=completed.copy();editobj['完整母体网格']=completed.name;editobj['缺口说明']=obj['缺口说明']
    reports.append(dict(name=obj.name,bottom_uses_existing_top_chips=True,side_chips=6*per_edge,source_spacing=spacing,non_manifold_edges=nonmanifold))

# 维持原装配及输出分件，只清除相交体积中的隐藏面。
labels=[stone.copy() for _ in parts]
for i,obj in enumerate(parts):
    obj.data.materials.clear()
    for tag in labels:obj.data.materials.append(tag)
    for p in obj.data.polygons:p.material_index=i
left=parts[0];bpy.context.view_layer.objects.active=left
for other in parts[1:]:
    mod=left.modifiers.new('嵌合内部面清理','BOOLEAN');mod.operation='UNION';mod.solver='EXACT';mod.object=other
    bpy.ops.object.modifier_apply(modifier=mod.name)
bm=bmesh.new();bm.from_mesh(left.data)
bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=1e-6);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bmesh.ops.triangulate(bm,faces=list(bm.faces))
nonmanifold=sum(not e.is_manifold for e in bm.edges)
assert nonmanifold==0,nonmanifold
bm.to_mesh(left.data);bm.free()
whole=left.data.copy();whole.name='提纯源岩_各边缘缺口_完整嵌合外壳';whole.use_fake_user=True
for i,obj in enumerate(parts):
    polygons=[p for p in whole.polygons if p.material_index==i]
    indices=sorted({v for p in polygons for v in p.vertices});lookup={v:j for j,v in enumerate(indices)}
    old=bpy.data.meshes.get(names[i])
    if old:old.name=names[i]+'_前阶段记录'
    mesh=bpy.data.meshes.new(names[i]);mesh.from_pydata([tuple(whole.vertices[v].co) for v in indices],[],[tuple(lookup[v] for v in p.vertices) for p in polygons]);mesh.update()
    obj.data=mesh;mesh.materials.append(stone)
whole.materials.clear();whole.materials.append(stone)
for p in whole.polygons:p.material_index=0
for obj in scene.objects:obj.select_set(obj in parts)
bpy.context.view_layer.objects.active=left
bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT');bpy.ops.uv.smart_project(angle_limit=math.radians(50),island_margin=.018);bpy.ops.object.mode_set(mode='OBJECT')
for obj in scene.objects:obj.select_set(False)
bpy.context.view_layer.objects.active=None
scene.cycles.samples=40
scene.render.filepath=str(OUT/'正面_完整边缘缺口.png');bpy.ops.render.render(write_still=True)
camera=scene.camera;matrix=camera.matrix_world.copy();scale=camera.data.ortho_scale;engine=scene.render.engine
scene.render.engine='BLENDER_WORKBENCH';scene.display.shading.color_type='MATERIAL';scene.display.shading.light='STUDIO';scene.display.shading.show_cavity=True
for filename,pos in [('侧面_竖棱缺口.png',(.8,-6.8,1.8)),('底面_边缘缺口.png',(.5,-3,-4)),('背面_边缘缺口.png',(3,4,3.5))]:
    camera.location=pos;camera.rotation_euler=(-camera.location).to_track_quat('-Z','Y').to_euler();camera.data.ortho_scale=2.15
    scene.render.filepath=str(OUT/filename);bpy.ops.render.render(write_still=True)
camera.matrix_world=matrix;camera.data.ortho_scale=scale;scene.render.engine=engine
editable.render.filepath=str(OUT/'逐块检查_顶部与侧棱.png');bpy.ops.render.render(write_still=True,scene=editable.name)
ec=editable.camera;em=ec.matrix_world.copy()
ec.location=(1.4,-7,-5.3);ec.rotation_euler=(-ec.location).to_track_quat('-Z','Y').to_euler()
editable.render.filepath=str(OUT/'逐块检查_底缘与侧棱.png');bpy.ops.render.render(write_still=True,scene=editable.name)
ec.matrix_world=em
scene.render.filepath=str(OUT/'正面_完整边缘缺口.png')
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='VIEW_3D':
            s=area.spaces.active;s.region_3d.view_perspective='CAMERA';s.region_3d.view_camera_zoom=0;s.shading.type='SOLID'
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_RemainingEdgesReview.blend')))
(OUT/'chip_review.json').write_text(json.dumps(dict(source=str(SOURCE),parts=reports,side_changes=changes,non_manifold_edges=nonmanifold,triangles=sum(len(o.data.polygons) for o in parts),body_transforms_unchanged=True,material=stone.name,independent_edit_scene=editable.name),ensure_ascii=False,indent=2),encoding='utf-8')
print('三块岩石的底缘和竖棱缺口已分别完成',flush=True)
