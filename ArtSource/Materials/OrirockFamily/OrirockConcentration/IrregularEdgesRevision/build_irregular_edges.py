"""保留已确认顶缘，只重做侧棱与底缘的疏密、方向及断口形状。"""
import bpy,bmesh,json,math,random
from pathlib import Path
from mathutils import Vector,Quaternion

OUT=Path(__file__).resolve().parent
KEY='OrirockConcentration'
SOURCE=OUT.parent/'PerRockChipsRevision'/(KEY+'_PerRockChips.blend')
bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
bpy.context.preferences.filepaths.save_version=0
scene=bpy.data.scenes['提纯源岩_逐块制作参考崩口']
if bpy.context.window:bpy.context.window.scene=scene
scene.name='提纯源岩_侧底不规则缺口'
parts=sorted([o for o in scene.objects if o.type=='MESH'],key=lambda o:0 if '左后' in o.name else 1 if '右后' in o.name else 2)
names=[o.data.name for o in parts]
stone=parts[0].data.materials[0]
layout=json.loads((OUT.parent/'AlignedHexRevision/layout.json').read_text(encoding='utf-8'))
editable=bpy.data.scenes['逐块编辑_完整岩柱']
rng=random.Random(131025)
changes=[]

def cut(obj,center,u,v,w,width,inset,depth,kind,variant):
    skew=rng.uniform(-.3,.3)
    left=rng.uniform(.38,.72);right=1-left
    tip=rng.uniform(-.25,.22)*width
    if variant==0:
        local=[(-left*width,-.017,.008),(right*width,-.020,.006),
               (tip,inset,.004),(tip-.15*width,inset*.55,.003),
               (tip+.09*width,-.026,-depth)]
    elif variant==1:
        local=[(-left*width,-.020,.007),(right*width,-.017,.009),
               (-.29*width,inset*.52,.003),(.13*width,inset,.005),
               (-.19*width,-.017,-depth),(.23*width,-.023,-depth*.43)]
    else:
        local=[(-left*width,-.022,.009),(right*width,-.013,.005),
               (-.07*width,inset*.95,.003),(.26*width,inset*.27,.003),
               (-.32*width,-.020,-depth*.40),(.16*width,-.018,-depth)]
    bm=bmesh.new()
    for x,y,z in local:bm.verts.new(center+u*x+v*(y+skew*x)+w*(z-skew*x*.38))
    bmesh.ops.convex_hull(bm,input=list(bm.verts));bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    mesh=bpy.data.meshes.new('不规则局部断口刀具');bm.to_mesh(mesh);bm.free();mesh.materials.append(stone)
    cutter=bpy.data.objects.new(mesh.name,mesh);scene.collection.objects.link(cutter)
    bpy.context.view_layer.objects.active=obj
    mod=obj.modifiers.new('逐块加工_'+kind,'BOOLEAN');mod.operation='DIFFERENCE';mod.solver='EXACT';mod.object=cutter
    bpy.ops.object.modifier_apply(modifier=mod.name)
    bpy.data.objects.remove(cutter,do_unlink=True);bpy.data.meshes.remove(mesh)
    changes.append(dict(part=obj.name,kind=kind,position=list(center),width=width,inset=inset,depth=depth,profile=variant))

def positions(count,lo,hi,min_gap):
    # 无等距分层：一处邻近小簇加上稀疏断口，留出明显较长的完整棱段。
    if count==1:return [rng.uniform(lo,hi)]
    cluster=rng.uniform(lo+.10*(hi-lo),hi-.22*(hi-lo))
    result=[cluster,cluster+min_gap*rng.uniform(1.05,1.50)]
    for attempt in range(500):
        if len(result)>=count:break
        p=rng.uniform(lo,hi)
        if all(abs(p-q)>min_gap for q in result):result.append(p)
    assert len(result)==count
    return sorted(result)

reports=[]
for owner,(obj,spec) in enumerate(zip(parts,layout['parts'])):
    # 从仅有已确认顶部缺口的完整母体重新加工，撤掉上一版规则排列。
    obj.data=bpy.data.meshes[obj['完整母体网格']].copy()
    obj.data.materials.clear();obj.data.materials.append(stone)
    for p in obj.data.polygons:p.material_index=0
    bottom,top=spec['bottom'],spec['top']
    preserved_top={tuple(round(c,7) for c in v.co) for v in obj.data.vertices if v.co.z>top-.045}
    center=Vector((*spec['center'],bottom))
    ring=[center+Vector((math.cos(math.radians(layout['yaw_degrees']+k*60)),math.sin(math.radians(layout['yaw_degrees']+k*60)),0))*spec['radius'] for k in range(6)]
    counts=[[2,5,3,6,4,4],[4,1,3,2,4,4],[1,3,2,1,3,2]][owner].copy();rng.shuffle(counts)
    bottom_counts=[2,0,1,3,1,1];rng.shuffle(bottom_counts)
    bottom_corner_sizes=[.025,.038,.069,.030,.052,.042];rng.shuffle(bottom_corner_sizes)
    for k,p in enumerate(ring):
        nextp=ring[(k+1)%6];previous=ring[(k-1)%6]
        along=(nextp-p).normalized();inward=Vector((-along.y,along.x,0))
        # 底角不再镜像顶部；左右偏移和破损宽度分别设定。
        corner=p+along*rng.uniform(-.007,.012)
        cut(obj,corner,along,inward,Vector((0,0,-1)),bottom_corner_sizes[k],rng.uniform(.011,.029),rng.uniform(.015,.034),'底角',rng.randrange(3))
        for j,t in enumerate(positions(bottom_counts[k],.14,.87,.095) if bottom_counts[k] else []):
            clustered=bottom_counts[k]>1 and j==1
            width=rng.uniform(.021,.035) if clustered else rng.uniform(.033,.072)
            cut(obj,p.lerp(nextp,t),along,inward,Vector((0,0,-1)),width,rng.uniform(.010,.028),rng.uniform(.014,.035),'底缘',rng.randrange(3))
        # 每根纵棱独立安排不同数量和不同高度；偏斜方向、短浅/宽长形状交替。
        height=top-bottom
        heights=positions(counts[k],.075,height-.085,.049)
        for j,h in enumerate(heights):
            short=(j==1 and counts[k]>2) or rng.random()<.22
            width=rng.uniform(.023,.038) if short else rng.uniform(.041,.082)
            zaxis=Vector((0,0,-1 if rng.random()<.5 else 1))
            nextdir=(nextp-p).normalized();prevdir=(previous-p).normalized()
            if rng.random()<.5:v,w=nextdir,-prevdir
            else:v,w=prevdir,-nextdir
            cut(obj,Vector((p.x,p.y,bottom+h)),zaxis,v,w,width,rng.uniform(.009,.029),rng.uniform(.013,.036),'侧棱',rng.randrange(3))
    bm=bmesh.new();bm.from_mesh(obj.data)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bmesh.ops.dissolve_limit(bm,angle_limit=.00001,verts=list(bm.verts),edges=list(bm.edges))
    bmesh.ops.triangulate(bm,faces=list(bm.faces))
    nonmanifold=sum(not e.is_manifold for e in bm.edges)
    assert nonmanifold==0,(obj.name,nonmanifold)
    bm.to_mesh(obj.data);bm.free();obj.data.update()
    actual_top={tuple(round(c,7) for c in v.co) for v in obj.data.vertices if v.co.z>top-.045}
    assert preserved_top<=actual_top,(obj.name,'顶部被改动')
    completed=obj.data.copy();completed.name=obj.name+'_侧底不规则缺口母体';completed.use_fake_user=True
    obj['完整母体网格']=completed.name
    obj['缺口说明']='只改侧棱与底缘：疏密不同、局部成簇、非对称斜口；顶部原缺口保留。'
    editobj=next(o for o in editable.objects if o.type=='MESH' and o.name.startswith(obj.name))
    editobj.data=completed.copy();editobj['完整母体网格']=completed.name;editobj['缺口说明']=obj['缺口说明']
    reports.append(dict(name=obj.name,side_counts_by_edge=counts,bottom_chips=6+sum(bottom_counts),non_manifold_edges=nonmanifold,top_vertices_preserved=True))

# 各块缺口已单独完成，复用既有装配、内部面清理和三分件命名方式。
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
whole=left.data.copy();whole.name='提纯源岩_不规则侧底缺口_完整外壳';whole.use_fake_user=True
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
scene.render.filepath=str(OUT/'正面_侧底不规则缺口.png');bpy.ops.render.render(write_still=True)
camera=scene.camera;matrix=camera.matrix_world.copy();scale=camera.data.ortho_scale;engine=scene.render.engine
scene.render.engine='BLENDER_WORKBENCH';scene.display.shading.color_type='MATERIAL';scene.display.shading.light='STUDIO';scene.display.shading.show_cavity=True
for filename,pos in [('侧面_错落断口.png',(.8,-6.8,1.8)),('底面_错落断口.png',(.5,-3,-4)),('背面_错落断口.png',(3,4,3.5))]:
    camera.location=pos;camera.rotation_euler=(-camera.location).to_track_quat('-Z','Y').to_euler();camera.data.ortho_scale=2.15
    scene.render.filepath=str(OUT/filename);bpy.ops.render.render(write_still=True)
camera.matrix_world=matrix;camera.data.ortho_scale=scale;scene.render.engine=engine
editable.render.filepath=str(OUT/'逐块检查_侧棱.png');bpy.ops.render.render(write_still=True,scene=editable.name)
ec=editable.camera;em=ec.matrix_world.copy()
ec.location=(1.4,-7,-5.3);ec.rotation_euler=(-ec.location).to_track_quat('-Z','Y').to_euler()
editable.render.filepath=str(OUT/'逐块检查_底缘.png');bpy.ops.render.render(write_still=True,scene=editable.name)
ec.matrix_world=em
# 初始视口沿用本轮用户实际侧视角；再次打开时还会保留当时最新的观察位置。
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='VIEW_3D':
            s=area.spaces.active;r=s.region_3d
            r.view_rotation=Quaternion((.6300351024,.6197696924,.3281353116,.3335692585))
            r.view_location=(.024697483,-.017474174,-.016203403);r.view_distance=2.713299036;r.view_perspective='PERSP';s.shading.type='SOLID'
scene.render.filepath=str(OUT/'正面_侧底不规则缺口.png')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_IrregularEdgesReview.blend')))
(OUT/'chip_review.json').write_text(json.dumps(dict(source=str(SOURCE),parts=reports,changes=changes,non_manifold_edges=nonmanifold,triangles=sum(len(o.data.polygons) for o in parts),body_transforms_unchanged=True,material=stone.name,independent_edit_scene=editable.name),ensure_ascii=False,indent=2),encoding='utf-8')
print('侧面与底部的不规则断口已完成；顶部保持',flush=True)
