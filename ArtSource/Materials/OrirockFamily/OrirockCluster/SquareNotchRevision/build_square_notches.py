"""只制作真实方口/直角槽。主块标准立方体、姿态、材质、后撑不改。"""
from pathlib import Path
import bpy,bmesh,json,math
import numpy as np
from mathutils import Vector,Quaternion

OUT=Path(__file__).resolve().parent
base=bpy.app.driver_namespace['cluster_square_revision'];work=base['scene']
if bpy.context.scene!=work or bpy.app.is_job_running('RENDER'):
    raise RuntimeError('活动场景改变或仍在渲染，不能盲目创建新缺口。')
rows=json.loads((OUT.parent/'SurfaceMatchRevision'/'当前块体坐标.json').read_text(encoding='utf-8'))
corners=[np.array(row['world_vertices'],float) for row in rows]
corners += [np.array([v.co[:] for v in p.data.vertices]) for p in base['parts'][5:]]
if len(corners)!=7 or any(len(v)!=8 for v in corners):raise RuntimeError('只允许七个现有标准方块。')
floor=base['floor']
faces=[[0,1,2,3],[4,7,6,5],[0,4,5,1],[1,5,6,2],[2,6,7,3],[3,7,4,0]]
bits=np.array([[0,0,0],[1,0,0],[1,1,0],[0,1,0],[0,0,1],[1,0,1],[1,1,1],[0,1,1]],float)
varying=[(0,1),(0,1),(0,2),(1,2),(0,2),(1,2)]
scene=bpy.data.scenes.new('固源岩组_标准方块_方口凹槽')
scene.world=work.world;scene.render.engine=work.render.engine;scene.cycles.samples=28
scene.unit_settings.system=work.unit_settings.system;scene.unit_settings.scale_length=work.unit_settings.scale_length
scene.view_settings.view_transform=work.view_settings.view_transform;scene.view_settings.look=work.view_settings.look
scene.view_settings.exposure=work.view_settings.exposure;scene.view_settings.gamma=work.view_settings.gamma
scene.render.resolution_x=scene.render.resolution_y=1000;scene.render.resolution_percentage=100
scene.render.image_settings.file_format=work.render.image_settings.file_format;scene.render.film_transparent=True
bpy.context.window.scene=scene
author=bpy.data.collections.new('04_标准立方体与方口槽_可编辑')
game=bpy.data.collections.new('04_真实闭合实体_导出')
cutters=bpy.data.collections.new('04_矩形减材刀具_不导出')
preview=bpy.data.collections.new('04_观察设备_不导出')
for c in (author,game,cutters,preview):scene.collection.children.link(c)
for o in work.objects:
    if o.type in {'CAMERA','LIGHT'}:
        c=o.copy();c.data=o.data.copy();preview.objects.link(c)
        if o==work.camera:scene.camera=c
right,up,front=[base[k].copy() for k in ('right','up','front')]
camera_location=base['camera_location'].copy();camera_quat=base['camera_quat'].copy()
parts=[];body_checks=[]

for block,v in enumerate(corners):
    axes=np.column_stack([v[i]-v[0] for i in [1,3,4]])
    lengths=np.linalg.norm(axes,axis=0);unit=axes/lengths
    check=dict(block=block,equal_edge_error=float(lengths.max()-lengths.min()),
        orthogonality_error=float(np.max(np.abs(unit.T@unit-np.eye(3)))))
    if max(check['equal_edge_error'],check['orthogonality_error'])>1e-5:
        raise RuntimeError('主体必须为标准正方体，不执行非等边校形。')
    body_checks.append(check)
    mesh=bpy.data.meshes.new(base['parts'][block].name.split('.')[0]+'_标准六面网格')
    mesh.from_pydata(v,[],faces);mesh.update()
    for m in base['parts'][block].data.materials[:6]:mesh.materials.append(m)
    for name in ('UVMap','StoneFace'):
        uv=mesh.uv_layers.new(name=name)
        for fi,p in enumerate(mesh.polygons):
            p.material_index=fi;a,b=varying[fi]
            for li in p.loop_indices:
                vi=mesh.loops[li].vertex_index;uv.data[li].uv=(bits[vi,a],bits[vi,b])
    bm=bmesh.new();bm.from_mesh(mesh);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(mesh);bm.free()
    p=bpy.data.objects.new(base['parts'][block].name.split('.')[0]+'_方口修订',mesh)
    author.objects.link(p);parts.append(p)
    p['结构']='标准等边、正交立方体主体；只有参考图的局部矩形减材，八个原角点保留。'
    p['主体边长']=float(lengths[0])

# 每一项给出原立方体局部坐标的矩形盒范围，槽壁严格平行主面。
# 不用楔形、V口、波浪面或贴图暗块代替几何。
specs=[
 (0,'顶部赭皮_靠浅面长方口槽',(.67,.055,-.065),(1.035,.125,.055),0,(0,1)),
 (0,'顶部赭皮_左侧较短方口槽',(.81,.27,-.06),(1.035,.345,.039),0,(0,1)),
 (0,'顶部灰面左棱_矩形小台阶',(.855,.945,-.05),(.925,1.04,.04),0,(0,1)),
 (1,'左块灰面上缘_直角阶梯口',(.26,-.045,-.045),(.37,.04,.04),0,(0,1)),
 (1,'左块灰浅交界_矩形凹口',(-.04,.32,-.04),(.042,.415,.042),5,(1,2)),
 (4,'右后褐浅交界_下段矩形阶梯',(-.04,-.04,.17),(.045,.045,.25),2,(0,2)),
 (4,'右后褐浅交界_上段矩形阶梯',(-.033,-.033,.39),(.038,.038,.455),2,(0,2)),
 (3,'右前浅顶外缘_矩形小缺口',(-.035,-.035,.50),(.04,.04,.595),5,(1,2)),
]
notches=[]
for block,label,low,high,mi,uvaxes in specs:
    v=corners[block];axes=np.column_stack([v[i]-v[0] for i in (1,3,4)])
    low=np.array(low);high=np.array(high)
    local=low+bits*(high-low);world=local@axes.T+v[0]
    mesh=bpy.data.meshes.new(label+'_矩形刀具网格');mesh.from_pydata(world,[],faces);mesh.update()
    mesh.materials.append(parts[block].data.materials[mi])
    for name in ('UVMap','StoneFace'):
        uv=mesh.uv_layers.new(name=name)
        for p in mesh.polygons:
            for li in p.loop_indices:
                vi=mesh.loops[li].vertex_index;uv.data[li].uv=tuple(local[vi,list(uvaxes)])
    bm=bmesh.new();bm.from_mesh(mesh);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(mesh);bm.free()
    cutter=bpy.data.objects.new(label+'_真实减材盒',mesh);cutters.objects.link(cutter)
    p=parts[block]
    for o in scene.objects:o.select_set(o==p)
    bpy.context.view_layer.objects.active=p
    before=len(p.data.vertices)
    mod=p.modifiers.new(label,'BOOLEAN');mod.operation='DIFFERENCE';mod.solver='EXACT';mod.material_mode='TRANSFER';mod.object=cutter
    bpy.ops.object.modifier_apply(modifier=mod.name)
    notches.append(dict(block=block,label=label,local_low=low.tolist(),local_high=high.tolist(),
        real_geometry_changed=len(p.data.vertices)!=before,shape='方口、平槽壁、平槽底',
        preserved_cube_corners=True))

# 八角点没有被削平或移动，最低接地角不受缺口影响。
for i,p in enumerate(parts):
    actual=np.array([v.co[:] for v in p.data.vertices])
    body_checks[i]['original_corner_max_distance']=float(np.linalg.norm(corners[i][:,None]-actual[None,:],axis=2).min(1).max())
    if body_checks[i]['original_corner_max_distance']>1e-6:raise RuntimeError('原主角点被移除，不符合标准方块范围。')

root=bpy.data.objects.new('OrirockClusterSquareNotch_Root',None);game.objects.link(root)
solid=parts[0].copy();solid.data=parts[0].data.copy();solid.parent=root;game.objects.link(solid)
solid.name='固源岩组_标准方块_真实方口缺槽实体'
for o in scene.objects:o.select_set(o==solid)
bpy.context.view_layer.objects.active=solid
for p in parts[1:]:
    mod=solid.modifiers.new('完整方块实体衔接','BOOLEAN');mod.operation='UNION';mod.solver='EXACT';mod.material_mode='TRANSFER';mod.object=p
    bpy.ops.object.modifier_apply(modifier=mod.name)
bm=bmesh.new();bm.from_mesh(solid.data);bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.000001)
bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bmesh.ops.triangulate(bm,faces=list(bm.faces))
geometry=dict(boundary_edges=sum(e.is_boundary for e in bm.edges),non_manifold_edges=sum(not e.is_manifold for e in bm.edges),volume=abs(bm.calc_volume(signed=True)))
bm.to_mesh(solid.data);bm.free();solid.data.calc_loop_triangles()
geometry.update(triangles=len(solid.data.loop_triangles),floor_z=floor,model_zmin=min(v.co.z for v in solid.data.vertices))
author.hide_render=True;author.hide_viewport=True;cutters.hide_render=True;cutters.hide_viewport=True
solid.data.uv_layers.active_index=0
for uv in solid.data.uv_layers:uv.active_render=(uv.name=='UVMap')
bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT');bpy.ops.uv.smart_project(angle_limit=math.radians(66),island_margin=.02);bpy.ops.object.mode_set(mode='OBJECT')
materials=list(solid.data.materials);preserved=[]

def set_front_view():
    scene.camera.location=camera_location;scene.camera.rotation_euler=camera_quat.to_euler()
    for a in bpy.context.screen.areas:
        if a.type=='VIEW_3D':
            s=a.spaces.active;s.shading.type='MATERIAL';s.overlay.show_overlays=False
            s.region_3d.view_rotation=camera_quat;s.region_3d.view_perspective='ORTHO'
            region=next(r for r in a.regions if r.type=='WINDOW')
            with bpy.context.temp_override(area=a,region=region):bpy.ops.view3d.view_selected(use_all_regions=False)
            s.region_3d.view_distance*=1.10
    for o in preview.objects:o.hide_set(True)

def render_notches():
    paths={}
    scene.render.filepath=str(OUT/'方口_整体正面.png');bpy.ops.render.render(write_still=True);paths['正面']=scene.render.filepath
    # 真实近看同一模型，不是生成假效果图。观察顶部槽的开口、壁和底面。
    v=corners[0];focus=Vector(v[1]*.65+v[0]*.20+v[2]*.15)
    scene.camera.location=focus+front*8;scene.camera.rotation_euler=camera_quat.to_euler();scene.camera.data.ortho_scale=.64
    scene.render.filepath=str(OUT/'方口_顶部近看.png');bpy.ops.render.render(write_still=True);paths['顶部']=scene.render.filepath
    q=Quaternion(Vector((0,0,1)),math.radians(-24))
    scene.camera.location=focus+(q@front)*8;scene.camera.rotation_euler=(q@camera_quat).to_euler()
    scene.render.filepath=str(OUT/'方口_顶部侧看.png');bpy.ops.render.render(write_still=True);paths['顶部侧看']=scene.render.filepath
    scene.camera.data.ortho_scale=183*.016;set_front_view()
    bpy.data.libraries.write(str(OUT/'OrirockCluster_SquareNotches.blend'),{scene},fake_user=True)
    (OUT/'制作状态.json').write_text(json.dumps(dict(stage='矩形凹槽与直角台阶已制作，待实图确认',scene=scene.name,geometry=geometry,body_checks=body_checks,
        notches=notches,renders=paths,removed_wrong_wedge_chips=True,materials_unchanged=True,poses_unchanged=True,
        posterior_cubes_unchanged=True,unity_imported=False),ensure_ascii=False,indent=2),encoding='utf-8')
    return None

set_front_view();bpy.app.driver_namespace['cluster_square_revision']=globals().copy()
bpy.app.timers.register(render_notches,first_interval=1)
print(json.dumps(dict(scene=scene.name,geometry=geometry,notches_changed=sum(n['real_geometry_changed'] for n in notches),total_notches=len(notches),body_checks=body_checks),ensure_ascii=False))
