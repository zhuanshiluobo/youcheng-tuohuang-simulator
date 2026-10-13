"""仅从当前验收模型减去局部缺口，不重新拟合或移动三根柱体。"""
import bpy,bmesh,json,math,random
from pathlib import Path
from mathutils import Vector
from mathutils.bvhtree import BVHTree

OUT=Path(__file__).resolve().parent
KEY='OrirockConcentration'
SOURCE=OUT.parent/'AlignedHexRevision'/(KEY+'_AlignedHex.blend')
bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
bpy.context.preferences.filepaths.save_version=0
scene=bpy.context.scene
scene.name='提纯源岩_各边角浅缺口'
parts=sorted([o for o in scene.objects if o.type=='MESH'],key=lambda o:0 if '左后' in o.name else 1 if '右后' in o.name else 2)
names=[o.data.name for o in parts]
stone=parts[0].data.materials[0]
layout=json.loads((OUT.parent/'AlignedHexRevision/layout.json').read_text(encoding='utf-8'))
labels=[stone.copy() for _ in parts]
vertices=[];faces=[];face_labels=[]
for i,obj in enumerate(parts):
    offset=len(vertices)
    vertices.extend([tuple(v.co) for v in obj.data.vertices])
    faces.extend([tuple(offset+j for j in p.vertices) for p in obj.data.polygons])
    face_labels.extend([i]*len(obj.data.polygons))
mesh=bpy.data.meshes.new('边角加工_联合外壳');mesh.from_pydata(vertices,[],faces);mesh.update()
for tag in labels:mesh.materials.append(tag)
for p,i in zip(mesh.polygons,face_labels):p.material_index=i
bm=bmesh.new();bm.from_mesh(mesh)
bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=1e-6)
bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
bm.to_mesh(mesh);bm.free()
shell=bpy.data.objects.new('边角加工_临时实体',mesh);scene.collection.objects.link(shell)
bpy.context.view_layer.objects.active=shell
base_bvh=BVHTree.FromPolygons([v.co for v in mesh.vertices],[p.vertices for p in mesh.polygons])
rng=random.Random(20261013)
changes=[]

def chip(center,normal,along,width,height,depth,owner,kind):
    near=base_bvh.find_nearest(center)
    # 内部交叠区没有外露的棱角，不在邻柱表面打洞。
    if near[0] is None or near[3]>.008:return
    n=normal.normalized();u=along.normalized();v=n.cross(u).normalized()
    reach=.020
    points=[center+u*x*width+v*y*height+n*reach for x,y in [(-.61,-.45),(.45,-.55),(.58,.40),(-.35,.57)]]
    points.extend([center-n*depth+u*width*.07-v*height*.05,
                   center-n*depth*.70-u*width*.21+v*height*.09])
    bm=bmesh.new()
    for p in points:bm.verts.new(p)
    bmesh.ops.convex_hull(bm,input=list(bm.verts));bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    cutter_mesh=bpy.data.meshes.new('局部浅缺口刀具');bm.to_mesh(cutter_mesh);bm.free()
    for tag in labels:cutter_mesh.materials.append(tag)
    for p in cutter_mesh.polygons:p.material_index=owner
    cutter=bpy.data.objects.new('局部浅缺口刀具',cutter_mesh);scene.collection.objects.link(cutter)
    before=len(shell.data.polygons)
    mod=shell.modifiers.new('局部崩口','BOOLEAN');mod.operation='DIFFERENCE';mod.solver='EXACT';mod.object=cutter
    bpy.context.view_layer.objects.active=shell;bpy.ops.object.modifier_apply(modifier=mod.name)
    bpy.data.objects.remove(cutter,do_unlink=True);bpy.data.meshes.remove(cutter_mesh)
    if len(shell.data.polygons)!=before:
        changes.append(dict(part=parts[owner].name,kind=kind,center=list(center),width=width,depth=depth))

zaxis=Vector((0,0,1))
for owner,spec in enumerate(layout['parts']):
    center=Vector((*spec['center'],0));radius=spec['radius'];top=spec['top'];bottom=spec['bottom']
    ring=[center+Vector((math.cos(math.radians(layout['yaw_degrees']+k*60)),math.sin(math.radians(layout['yaw_degrees']+k*60)),0))*radius for k in range(6)]
    for k,p in enumerate(ring):
        radial=(p-center).normalized();tangent=zaxis.cross(radial)
        chip(Vector((p.x,p.y,top)),radial+zaxis,tangent,rng.uniform(.065,.100),.09,rng.uniform(.025,.036),owner,'顶部转角')
        chip(Vector((p.x,p.y,bottom)),radial-zaxis,tangent,rng.uniform(.055,.083),.075,rng.uniform(.020,.029),owner,'底部转角')
        # 每条外露纵棱只留一处短小断口，主体仍是竖直直棱。
        z=bottom+(top-bottom)*rng.uniform(.30,.67)
        chip(Vector((p.x,p.y,z)),radial,zaxis,rng.uniform(.075,.115),.066,rng.uniform(.025,.035),owner,'竖向棱边')
        q=ring[(k+1)%6];along=(q-p).normalized();outward=Vector((along.y,-along.x,0))
        edge=p.lerp(q,rng.uniform(.40,.60))
        chip(Vector((edge.x,edge.y,top)),outward+zaxis,along,rng.uniform(.072,.11),.075,rng.uniform(.023,.034),owner,'顶部边缘')
        edge=p.lerp(q,rng.uniform(.27,.72))
        chip(Vector((edge.x,edge.y,bottom)),outward-zaxis,along,rng.uniform(.050,.076),.065,rng.uniform(.018,.026),owner,'底部边缘')

bm=bmesh.new();bm.from_mesh(shell.data)
bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=1e-6)
bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
bmesh.ops.dissolve_limit(bm,angle_limit=.00001,verts=list(bm.verts),edges=list(bm.edges),delimit={'MATERIAL'})
bmesh.ops.triangulate(bm,faces=list(bm.faces))
nonmanifold=sum(not e.is_manifold for e in bm.edges)
assert nonmanifold==0,nonmanifold
bm.to_mesh(shell.data);bm.free();shell.data.update()
whole=shell.data.copy();whole.name='提纯源岩_全边角缺口_完整闭合外壳';whole.use_fake_user=True
for i,obj in enumerate(parts):
    polygons=[p for p in whole.polygons if p.material_index==i]
    indices=sorted({v for p in polygons for v in p.vertices});lookup={v:j for j,v in enumerate(indices)}
    obj.data.name=names[i]+'_添缺口前'
    new=bpy.data.meshes.new(names[i]);new.from_pydata([tuple(whole.vertices[v].co) for v in indices],[],[tuple(lookup[v] for v in p.vertices) for p in polygons]);new.update()
    obj.data=new;new.materials.append(stone)
    obj['本轮新增缺口']=sum(c['part']==obj.name for c in changes)
    obj['缺口说明']='顶缘、顶部转角、外露纵棱和底部边角的局部浅断口；主体的六边形、位置与共同方向保持。'
whole.materials.clear();whole.materials.append(stone)
for p in whole.polygons:p.material_index=0
bpy.data.objects.remove(shell,do_unlink=True)
for obj in scene.objects:obj.select_set(obj in parts)
bpy.context.view_layer.objects.active=parts[0]
bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT');bpy.ops.uv.smart_project(angle_limit=math.radians(50),island_margin=.018);bpy.ops.object.mode_set(mode='OBJECT')
for obj in scene.objects:obj.select_set(False)
bpy.context.view_layer.objects.active=None
scene.cycles.samples=40
scene.render.filepath=str(OUT/'正面_各边角缺口.png');bpy.ops.render.render(write_still=True)
camera=scene.camera;matrix=camera.matrix_world.copy();scale=camera.data.ortho_scale;engine=scene.render.engine
scene.render.engine='BLENDER_WORKBENCH';scene.display.shading.color_type='MATERIAL';scene.display.shading.light='STUDIO';scene.display.shading.show_cavity=True
scene.display.shading.cavity_type='BOTH';scene.display.shading.show_shadows=True
for filename,pos in [('背面_各边角缺口.png',(3,4,3.5)),('底面_各边角缺口.png',(.5,-3,-4))]:
    camera.location=pos;camera.rotation_euler=(-camera.location).to_track_quat('-Z','Y').to_euler();camera.data.ortho_scale=2.1
    scene.render.filepath=str(OUT/filename);bpy.ops.render.render(write_still=True)
camera.matrix_world=matrix;camera.data.ortho_scale=scale;scene.render.engine=engine
scene.render.filepath=str(OUT/'正面_各边角缺口.png')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_AllEdgeChipsReview.blend')))
(OUT/'chip_review.json').write_text(json.dumps(dict(source=str(SOURCE),added_chips=len(changes),changes=changes,non_manifold_edges=nonmanifold,triangles=sum(len(o.data.polygons) for o in parts),body_transforms_unchanged=True,material=stone.name),ensure_ascii=False,indent=2),encoding='utf-8')
print('新增浅缺口：'+str(len(changes)),flush=True)
