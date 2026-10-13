"""04 中下小块纠正：圈出区域为小立方体；下方暗部由后置立方体叠放形成。"""
from pathlib import Path
import bpy, bmesh, math, json, traceback
import numpy as np
from mathutils import Vector, Quaternion

OUT = Path(__file__).resolve().parent
OUT.mkdir(exist_ok=True)
(OUT/'textures').mkdir(exist_ok=True)
FAMILY = OUT.parents[1]
KEY, SIZE = 'OrirockClusterFull', 2048
STATUS = OUT/'制作状态.json'

def status(stage, **extra):
    STATUS.write_text(json.dumps(dict(stage=stage, **extra),ensure_ascii=False,indent=2),encoding='utf-8')

old = bpy.data.scenes.get('固源岩组_正面对齐版.001')
if old is None:
    with bpy.data.libraries.load(str(OUT.parent/'OrirockCluster_Finished.blend'),link=False) as (available,target):
        target.scenes=available.scenes
    old=target.scenes[0]
scene=bpy.data.scenes.new('固源岩组_原图块面比例与后撑遮挡修订')
bpy.context.window.scene=scene
scene.unit_settings.system=old.unit_settings.system
scene.unit_settings.scale_length=old.unit_settings.scale_length
scene.world=old.world.copy()
scene.view_settings.view_transform=old.view_settings.view_transform
scene.render.engine=old.render.engine
scene.render.resolution_x=scene.render.resolution_y=1000
scene.render.resolution_percentage=100
scene.render.image_settings.file_format=old.render.image_settings.file_format
scene.render.film_transparent=True
author=bpy.data.collections.new('全组_可编辑块体_不导出')
game=bpy.data.collections.new('全组_封闭实体_导出')
preview=bpy.data.collections.new('全组_观察设备_不导出')
for collection in (author,game,preview): scene.collection.children.link(collection)
for o in old.objects:
    if o.type in {'CAMERA','LIGHT'}:
        c=o.copy(); c.data=o.data.copy(); preview.objects.link(c)
        if o.type=='CAMERA': scene.camera=c
camera_quat=scene.camera.rotation_euler.to_quaternion()
camera_location=scene.camera.location.copy()
right,up,front=[camera_quat@Vector(v) for v in [(1,0,0),(0,1,0),(0,0,1)]]

def point(p):
    x,y,d=p
    return right*((x-91)*.016)+up*((91-y)*.016)+front*d

def lin(c):
    return c/12.92 if c<=.04045 else ((c+.055)/1.055)**2.4

palette=[('浅砂黄',(211,195,156)),('赭褐',(150,128,80)),('炭灰',(53,55,58)),
         ('橄榄灰',(99,96,67)),('棕灰',(108,94,64)),('灰褐',(112,108,91)),
         ('深缝',(29,34,35)),('灰青',(42,52,58)),('小块浅色',(192,181,140))]
materials=[]
for name,rgb in palette:
    mat=bpy.data.materials.new('固源岩组_全组_'+name); mat.use_nodes=True
    nodes,links=mat.node_tree.nodes,mat.node_tree.links
    bs=next(n for n in nodes if n.type=='BSDF_PRINCIPLED')
    bs.inputs['Roughness'].default_value=.9
    bs.inputs['Metallic'].default_value=0
    if 'Specular IOR Level' in bs.inputs: bs.inputs['Specular IOR Level'].default_value=.12
    tc=nodes.new('ShaderNodeTexCoord')
    coarse=nodes.new('ShaderNodeTexNoise'); coarse.inputs['Scale'].default_value=18
    coarse.inputs['Detail'].default_value=5; coarse.inputs['Roughness'].default_value=.75
    links.new(tc.outputs['Object'],coarse.inputs['Vector'])
    ramp=nodes.new('ShaderNodeValToRGB')
    base=[lin(v/255) for v in rgb]
    ramp.color_ramp.elements[0].position=.23; ramp.color_ramp.elements[1].position=.78
    ramp.color_ramp.elements[0].color=tuple(v*.63 for v in base)+(1,)
    ramp.color_ramp.elements[1].color=tuple(min(1,v*1.25) for v in base)+(1,)
    links.new(coarse.outputs['Fac'],ramp.inputs[0])
    grain=nodes.new('ShaderNodeTexNoise'); grain.inputs['Scale'].default_value=165
    grain.inputs['Detail'].default_value=2; grain.inputs['Roughness'].default_value=.7
    links.new(tc.outputs['Object'],grain.inputs['Vector'])
    grain_ramp=nodes.new('ShaderNodeValToRGB')
    grain_ramp.color_ramp.elements[0].color=(.34,.34,.34,1)
    grain_ramp.color_ramp.elements[1].color=(1.45,1.45,1.45,1)
    links.new(grain.outputs['Fac'],grain_ramp.inputs[0])
    mix=nodes.new('ShaderNodeMixRGB'); mix.blend_type='MULTIPLY'; mix.inputs[0].default_value=.5
    links.new(ramp.outputs[0],mix.inputs[1]); links.new(grain_ramp.outputs[0],mix.inputs[2])
    links.new(mix.outputs[0],bs.inputs['Base Color'])
    bump=nodes.new('ShaderNodeBump'); bump.inputs['Strength'].default_value=.23
    bump.inputs['Distance'].default_value=.009
    links.new(grain.outputs['Fac'],bump.inputs['Height']); links.new(bump.outputs[0],bs.inputs['Normal'])
    rough=nodes.new('ShaderNodeMapRange'); rough.inputs['To Min'].default_value=.82; rough.inputs['To Max'].default_value=.98
    links.new(grain.outputs[0],rough.inputs[0]); links.new(rough.outputs[0],bs.inputs['Roughness'])
    materials.append(mat)

# 断片底端的赭色风化属于材质，不再拆成突起的多色三角面。
sliver_mat=materials[0].copy(); sliver_mat.name='固源岩组_断片底端风化'
nodes,links=sliver_mat.node_tree.nodes,sliver_mat.node_tree.links
bs=next(n for n in nodes if n.type=='BSDF_PRINCIPLED')
base_socket=bs.inputs['Base Color'].links[0].from_socket
tc=nodes.new('ShaderNodeTexCoord')
height=nodes.new('ShaderNodeVectorMath'); height.operation='DOT_PRODUCT'
height.inputs[1].default_value=up; links.new(tc.outputs['Object'],height.inputs[0])
weather=nodes.new('ShaderNodeMapRange')
weather.inputs['From Min'].default_value=(91-119)*.016
weather.inputs['From Max'].default_value=(91-140)*.016
links.new(height.outputs['Value'],weather.inputs[0])
mix=nodes.new('ShaderNodeMixRGB'); mix.blend_type='MIX'
mix.inputs[2].default_value=tuple(lin(v/255) for v in (174,140,87))+(1,)
links.new(weather.outputs[0],mix.inputs[0]); links.new(base_socket,mix.inputs[1])
links.new(mix.outputs[0],bs.inputs['Base Color']); materials.append(sliver_mat)

parts=[]
def mesh_object(name,verts,faces,indices):
    mesh=bpy.data.meshes.new(name+'_网格'); mesh.from_pydata(verts,[],faces); mesh.update()
    for m in materials: mesh.materials.append(m)
    for face,index in zip(mesh.polygons,indices): face.material_index=index; face.use_smooth=False
    bm=bmesh.new(); bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bmesh.ops.triangulate(bm,faces=list(bm.faces))
    bm.to_mesh(mesh); bm.free()
    obj=bpy.data.objects.new(name,mesh); author.objects.link(obj); parts.append(obj)
    obj['造型依据']='正常立方体整体倾斜；同块不同面的颜色不拆成独立部件。'
    return obj

def prism(name,xy,depth_plane,back_offset,front_mat,back_mat,side_mats):
    d,dx,dy,x0,y0=depth_plane
    verts=[point((x,y,d+dx*(x-x0)+dy*(y-y0))) for x,y in xy]
    w=right*(back_offset[0]*.016)-up*(back_offset[1]*.016)+front*back_offset[2]
    verts += [p+w for p in verts]
    n=len(xy)
    faces=[list(range(n)),list(reversed(range(n,2*n)))]
    indices=[front_mat,back_mat]
    for i in range(n):
        j=(i+1)%n; faces.append([i,j,n+j,n+i]); indices.append(side_mats[i%len(side_mats)])
    return mesh_object(name,verts,faces,indices)

BOX_FACES=[[0,1,2,3],[4,7,6,5],[0,4,5,1],[1,5,6,2],[2,6,7,3],[3,7,4,0]]

def corner_cube(name,corner,screen_edges,indices):
    # 用图中三个主边方向定整体旋转；投影行取最近的等长正交基。
    # 得到的是正常立方体（8顶点/6平面），不对顶点单独拉扯成异形块。
    xy=np.array([[e[0]*.016 for e in screen_edges],[-e[1]*.016 for e in screen_edges]])
    u,s,vt=np.linalg.svd(xy,full_matrices=True)
    length=float(s.mean())
    rows=length*(u@np.array([[1,0,0],[0,1,0]])@vt)
    depth=-np.cross(rows[0],rows[1])/length
    axes=[right*float(rows[0,i])+up*float(rows[1,i])+front*float(depth[i]) for i in range(3)]
    a=point(corner); u,v,w=axes
    verts=[a,a+u,a+u+v,a+v,a+w,a+u+w,a+u+v+w,a+v+w]
    obj=mesh_object(name,verts,BOX_FACES,indices)
    obj['结构']='单个正常立方体：三轴正交等长；整体旋转产生倾斜；六面分别着色。'
    obj['边长']=length
    obj['主边投影']=[(round(axis.dot(right)/.016,3),round(-axis.dot(up)/.016,3)) for axis in axes]
    return obj

# 主要块体从原图的同块交界与轮廓拟合；只改变整体位姿，三轴仍正交等长。
front_poses=json.loads((OUT/'主要块面位姿候选.json').read_text(encoding='utf-8'))
back_poses=json.loads((OUT/'后撑位姿候选.json').read_text(encoding='utf-8'))
def matched_cube(name,key,indices,depth):
    pose=front_poses[key]
    verts=[[v[0],v[1],v[2]+depth-pose['corner'][2]] for v in pose['camera_vertices']]
    obj=mesh_object(name,[point(v) for v in verts],BOX_FACES,indices)
    obj['边长']=pose['edge_length'];obj['结构']='完整等边立方体，只调整整体旋转、大小及摆放；各色属于同块不同面。'
    return obj

# 用户标注的左、右、下：属于同一个倾斜主方块的三个面。
top=matched_cube('上侧_单个倾斜立方体_左灰右浅下褐','top',[2,3,0,1,3,1],.80)

# 下側同样是一个立方体，不用前块加后方大底体拼出两个方块。
def front_cube(name,origin,screen_u,screen_v,indices):
    a=point(origin)
    u_xy=right*(screen_u[0]*.016)-up*(screen_u[1]*.016)
    v_xy=right*(screen_v[0]*.016)-up*(screen_v[1]*.016)
    dot=u_xy.dot(v_xy); delta=u_xy.length_squared-v_xy.length_squared
    du=math.sqrt((-delta+math.sqrt(delta*delta+4*dot*dot))/2)
    dv=-dot/du
    u=u_xy+front*du; v=v_xy+front*dv
    w=u.cross(v).normalized()*u.length
    if w.dot(front)>0: w=-w
    verts=[a,a+u,a+u+v,a+v,a+w,a+u+w,a+u+v+w,a+v+w]
    obj=mesh_object(name,verts,BOX_FACES,indices)
    obj['结构']='单个正常立方体：灰青正面、橄榄顶面、黄褐侧面共享八个顶点。'
    obj['边长']=u.length
    return obj

left_cube=matched_cube('左侧_单个倾斜立方体_灰正面浅右面','left',[7,4,3,2,4,9],.82)
# 圈内小块约为旧版边长的30%，暗色下方不再归入该块。
middle_cube=matched_cube('中下_圈内小立方体_不含下方暗部','middle',[5,2,8,2,3,0],.42+back_poses['params'][14])

# 右前、右后也为普通立方体；“上/正/右”是各自同一块体的三个面。
right_front_cube=matched_cube('右前_单个倾斜立方体_浅顶灰正褐右','right_front',[7,2,0,2,3,1],.95+back_poses['params'][15])
right_back_cube=corner_cube('右后_单个倾斜立方体_灰左浅右褐顶',(155.044,107.133,.10),
                           [(-49.435,-9.800),(.881,32.179),(13.287,-38.596)],[2,1,1,1,1,0])

# 两个后撑只使用完整立方体的旋转与位移；窄斜面露在正面，不形成竖直大面。
candidate=json.loads((OUT/'后撑位姿候选.json').read_text(encoding='utf-8'))
rear_base=mesh_object('后方_大源岩_窄面斜向承托',
    [point(v) for v in candidate['camera_vertices'][0]],BOX_FACES,[5,2,3,2,5,7])
rear_upper=mesh_object('后方_大源岩_另一朝向斜靠',
    [point(v) for v in candidate['camera_vertices'][1]],BOX_FACES,[2,3,4,2,3,2])
for obj,length in [(rear_base,candidate['params'][3]),(rear_upper,candidate['params'][10])]:
    obj['边长']=length;obj['结构']='完整等边立方体，整体刚性旋转；背后仅两块。'
core=top

root=bpy.data.objects.new(KEY+'_Root',None); game.objects.link(root)
root['修改范围']='五个主要方块；中下保持圈内小块尺寸；背后仅两个较大不同朝向方块承托。仅编号04。'
solid=core.copy(); solid.data=core.data.copy(); game.objects.link(solid); solid.parent=root
solid.name='固源岩组_完整倾斜方块组合'
bpy.context.view_layer.objects.active=solid
for obj in scene.objects: obj.select_set(obj==solid)
for part in parts:
    if part==core: continue
    mod=solid.modifiers.new('实体衔接_'+part.name,'BOOLEAN')
    mod.operation='UNION'; mod.solver='EXACT'; mod.material_mode='TRANSFER'; mod.object=part
    bpy.ops.object.modifier_apply(modifier=mod.name)
bm=bmesh.new(); bm.from_mesh(solid.data)
bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.00001)
bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces)); bmesh.ops.triangulate(bm,faces=list(bm.faces))
geometry=dict(boundary_edges=sum(e.is_boundary for e in bm.edges),
              non_manifold_edges=sum(not e.is_manifold for e in bm.edges),
              volume=abs(bm.calc_volume(signed=True)))
bm.to_mesh(solid.data); bm.free(); solid.data.calc_loop_triangles()
geometry['triangles']=len(solid.data.loop_triangles)
author.hide_render=True; author.hide_viewport=True
for obj in scene.objects: obj.select_set(obj==solid)
bpy.context.view_layer.objects.active=solid
bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(60),island_margin=.012)
bpy.ops.object.mode_set(mode='OBJECT')

source=(FAMILY/'build_orirock_family.py').read_text(encoding='utf-8')
exec(compile(source[source.index('def bake_job(job):'):source.index('def run():')],str(FAMILY/'build_orirock_family.py'),'exec'))
job=dict(key=KEY,name='固源岩组全组',scene=scene,root=root,meshes=[solid],materials=materials,dir=OUT)
preserved=[]  # 对照工具只显示完整新模型，不夹带旧阶段的保留块。

def set_front_view():
    scene.camera.location=camera_location; scene.camera.rotation_euler=camera_quat.to_euler()
    for area in bpy.context.screen.areas:
        if area.type=='VIEW_3D':
            space=area.spaces.active; space.shading.type='SOLID' if scene.view_layers[0].material_override else 'MATERIAL'; space.overlay.show_overlays=False
            space.region_3d.view_rotation=camera_quat; space.region_3d.view_location=front*(-.05)
            space.region_3d.view_distance=4.7; space.region_3d.view_perspective='ORTHO'

def preview_full():
    white=bpy.data.materials.new('两块后撑_仅白模观察');white.use_nodes=True
    bs=next(n for n in white.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    bs.inputs['Base Color'].default_value=(.45,.45,.45,1);bs.inputs['Roughness'].default_value=.95
    scene.view_layers[0].material_override=white
    set_front_view(); scene.render.filepath=str(OUT/'结构_正面白模.png')
    bpy.ops.render.render(write_still=True)
    bpy.data.libraries.write(str(OUT/'OrirockCluster_Occlusion_Blockout.blend'),{scene},fake_user=True)
    status('后撑窄斜面遮挡修订，待正面对照，未添加纹理',geometry=geometry)
    return None

def finish():
    try:
        scene.view_layers[0].material_override=None
        result=bake_job(job)
        renders={}
        for name,angle in [('正面',0),('左侧',90),('右侧',-90),('背面',180)]:
            q=Quaternion(up,math.radians(angle))
            scene.camera.location=q@camera_location; scene.camera.rotation_euler=(q@camera_quat).to_euler()
            scene.render.filepath=str(OUT/(name+'_全组修订.png'))
            bpy.ops.render.render(write_still=True); renders[name]=scene.render.filepath
        set_front_view()
        bpy.data.libraries.write(str(OUT/'OrirockClusterFull_Finished.blend'),{scene},fake_user=True)
        info=dict(key=KEY,item_id='04',geometry=geometry,fbx=str(OUT/(KEY+'_Finished.fbx')),
                  blend=str(OUT/(KEY+'_Finished.blend')),renders=renders,
                  front_status='待实图对照及收藏室观察',limitations='背侧由单张图标推定，非官方背面参考。')
        (OUT/'全组修订信息.json').write_text(json.dumps(info,ensure_ascii=False,indent=2),encoding='utf-8')
        status('烘焙与导出完成，尚未通过实图验收',model=result,geometry=geometry,renders=renders)
    except Exception: status('失败',error=traceback.format_exc())
    return None

bpy.app.driver_namespace['cluster_square_revision']=globals().copy()
bpy.app.timers.register(preview_full,first_interval=1)
print(json.dumps(dict(scene=scene.name,geometry=geometry),ensure_ascii=False))
