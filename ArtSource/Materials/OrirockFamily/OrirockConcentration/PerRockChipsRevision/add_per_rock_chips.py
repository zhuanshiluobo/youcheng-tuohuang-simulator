"""每块岩柱独立切出参照图的小崩口，最后仅清理嵌合内部面。"""
import bpy,bmesh,json,math,random
from pathlib import Path
from mathutils import Vector

OUT=Path(__file__).resolve().parent
KEY='OrirockConcentration'
SOURCE=OUT.parent/'AlignedHexRevision'/(KEY+'_AlignedHex.blend')
bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
bpy.context.preferences.filepaths.save_version=0
scene=bpy.context.scene;scene.name='提纯源岩_逐块制作参考崩口'
parts=sorted([o for o in scene.objects if o.type=='MESH'],key=lambda o:0 if '左后' in o.name else 1 if '右后' in o.name else 2)
names=[o.data.name for o in parts]
stone=parts[0].data.materials[0]
layout=json.loads((OUT.parent/'AlignedHexRevision/layout.json').read_text(encoding='utf-8'))
rng=random.Random(241013)
changes=[]

def cut(obj,a,b,t,width,inset,depth,kind):
    along=(b-a).normalized();inward=Vector((-along.y,along.x,0));center=a.lerp(b,t)
    shift=rng.uniform(-.17,.17)*width
    local=[(-.59*width,-.018,.006),(.52*width,-.018,.006),
           (shift+.06*width,inset,.004),(shift-.22*width,inset*.52,.005),
           (shift-.12*width,-.024,-depth)]
    bm=bmesh.new()
    for u,v,z in local:bm.verts.new(center+along*u+inward*v+Vector((0,0,z)))
    bmesh.ops.convex_hull(bm,input=list(bm.verts));bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    mesh=bpy.data.meshes.new('单块顶缘浅缺口刀具');bm.to_mesh(mesh);bm.free();mesh.materials.append(stone)
    cutter=bpy.data.objects.new(mesh.name,mesh);scene.collection.objects.link(cutter)
    bpy.context.view_layer.objects.active=obj
    mod=obj.modifiers.new('独立制作_'+kind,'BOOLEAN');mod.operation='DIFFERENCE';mod.solver='EXACT';mod.object=cutter
    bpy.ops.object.modifier_apply(modifier=mod.name)
    bpy.data.objects.remove(cutter,do_unlink=True);bpy.data.meshes.remove(mesh)
    changes.append(dict(part=obj.name,kind=kind,position=list(center),width=width,inset=inset,depth=depth))

for owner,(obj,spec) in enumerate(zip(parts,layout['parts'])):
    # 恢复当前同向六棱柱的完整母体；尺寸、朝向、高度及旧缺口均已在其中。
    mother=bpy.data.meshes[obj['完整母体网格']]
    obj.data=mother.copy()
    obj.data.materials.clear();obj.data.materials.append(stone)
    for p in obj.data.polygons:p.material_index=0
    center=Vector((*spec['center'],spec['top']))
    ring=[center+Vector((math.cos(math.radians(layout['yaw_degrees']+k*60)),math.sin(math.radians(layout['yaw_degrees']+k*60)),0))*spec['radius'] for k in range(6)]
    for k,a in enumerate(ring):
        b=ring[(k+1)%6]
        # 缺口在转角两边不等宽，只削去一小片，避免圆角或规则锯齿。
        cut(obj,a,b,rng.uniform(.003,.024),rng.uniform(.040,.066),rng.uniform(.014,.025),rng.uniform(.021,.034),'顶缘转角')
    # 原版正面已有不等距小缺口，仅补齐各块侧后方的顶缘。
    for k,t in [(0,.36+owner*.09),(1,.68-owner*.10),(2,.43+owner*.07)]:
        cut(obj,ring[k],ring[(k+1)%6],t,rng.uniform(.027,.050),rng.uniform(.010,.019),rng.uniform(.015,.026),'侧后顶缘')
    bm=bmesh.new();bm.from_mesh(obj.data)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bmesh.ops.dissolve_limit(bm,angle_limit=.00001,verts=list(bm.verts),edges=list(bm.edges))
    bmesh.ops.triangulate(bm,faces=list(bm.faces))
    assert all(e.is_manifold for e in bm.edges),obj.name
    bm.to_mesh(obj.data);bm.free();obj.data.update()
    completed=obj.data.copy();completed.name=obj.name+'_独立缺口完成母体';completed.use_fake_user=True
    obj['完整母体网格']=completed.name
    obj['本轮新增缺口']=9
    obj['缺口说明']='各岩块分别制作；参考顶缘小崩口，保留竖向主棱与平整底面。'

# 逐块独立源保留为独立场景，主场景仍显示原有嵌合关系。
editable=bpy.data.scenes.new('逐块编辑_完整岩柱')
editable.world=scene.world
for obj in parts:
    copy=obj.copy();copy.data=obj.data.copy();copy.name=obj.name+'_独立编辑'
    editable.collection.objects.link(copy)
for obj in scene.objects:
    if obj.type in {'CAMERA','LIGHT','EMPTY'}:editable.collection.objects.link(obj)
editable.camera=scene.camera

# 延续上一版的内部面清理；不会拿整组外壳作为缺口的切割对象。
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
whole=left.data.copy();whole.name='提纯源岩_逐块崩口_完整嵌合外壳';whole.use_fake_user=True
for i,obj in enumerate(parts):
    polygons=[p for p in whole.polygons if p.material_index==i]
    indices=sorted({v for p in polygons for v in p.vertices});lookup={v:j for j,v in enumerate(indices)}
    old=bpy.data.meshes.get(names[i])
    if old:old.name=names[i]+'_先前记录'
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
scene.render.filepath=str(OUT/'正面_逐块参考缺口.png');bpy.ops.render.render(write_still=True)
camera=scene.camera;matrix=camera.matrix_world.copy();scale=camera.data.ortho_scale;engine=scene.render.engine
scene.render.engine='BLENDER_WORKBENCH';scene.display.shading.color_type='MATERIAL';scene.display.shading.light='STUDIO';scene.display.shading.show_cavity=True
for filename,pos in [('背面_逐块缺口.png',(3,4,3.5)),('底面_保持完整.png',(.5,-3,-4))]:
    camera.location=pos;camera.rotation_euler=(-camera.location).to_track_quat('-Z','Y').to_euler();camera.data.ortho_scale=2.1
    scene.render.filepath=str(OUT/filename);bpy.ops.render.render(write_still=True)
camera.matrix_world=matrix;camera.data.ortho_scale=scale;scene.render.engine=engine
scene.render.filepath=str(OUT/'正面_逐块参考缺口.png')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_PerRockChipsReview.blend')))
(OUT/'chip_review.json').write_text(json.dumps(dict(source=str(SOURCE),added_chips=len(changes),changes=changes,non_manifold_edges=nonmanifold,triangles=sum(len(o.data.polygons) for o in parts),body_transforms_unchanged=True,material=stone.name,independent_edit_scene=editable.name),ensure_ascii=False,indent=2),encoding='utf-8')
print('三个岩块独立新增顶缘小缺口完成',flush=True)
