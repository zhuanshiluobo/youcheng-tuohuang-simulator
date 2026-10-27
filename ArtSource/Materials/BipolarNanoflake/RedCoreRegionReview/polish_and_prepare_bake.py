"""将框亮边收窄成真实连续表面；准备核心的独立烘焙 UV。"""
import bpy, bmesh, json, math
from pathlib import Path
from mathutils import Vector

scene=bpy.context.scene
root=scene.objects['BipolarNanoflake_Finished_Root']
for obj in [o for o in root.children_recursive if o.name.startswith('BNF_铜框_')]:
    old=obj.data;verts=[tuple(v.co) for v in old.vertices]
    faces=[];ids=[]
    pale=obj.name not in ('BNF_铜框_03_前横框','BNF_铜框_04_交错框')
    for t in (.22,.57):
        for i in range(4):
            v=old.vertices[8+i].co.lerp(old.vertices[12+i].co,t)
            verts.append(tuple(v))
    for i in range(4):
        j=(i+1)%4
        faces.extend([(i,j,8+j,8+i),(4+i,12+i,12+j,4+j),(i,4+i,4+j,j)])
        ids.extend([0,1,3])
        for outer,inner,mid in [(8,16,0),(16,20,2 if pale else 0),(20,12,0)]:
            faces.append((outer+i,outer+j,inner+j,inner+i));ids.append(mid)
    mesh=bpy.data.meshes.new(obj.name+'_连续窄亮面')
    mesh.from_pydata(verts,[],faces);mesh.update()
    for m in old.materials:mesh.materials.append(m)
    for p,mid in zip(mesh.polygons,ids):p.material_index=mid
    bm=bmesh.new();bm.from_mesh(mesh);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(mesh);bm.free()
    obj.data=mesh
core=scene.objects['BNF_一体红盘与暖白薄片']
mesh=core.data
uv=mesh.uv_layers.new(name='核心独立图集UV')
# 盘面整体保持统一映射；正背独立两个方形区块；银色盘沿单独分配条带。
# 薄片/贴面线面与盘面同投影会覆盖，因此对所有非盘面的分件先自动独立展开。
bpy.ops.object.select_all(action='DESELECT');core.select_set(True);bpy.context.view_layer.objects.active=core
bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(66),island_margin=.008)
bpy.ops.object.mode_set(mode='OBJECT')
scene.cycles.samples=24
scene.render.bake.margin=8
scene.render.bake.use_clear=True
scene.render.bake.use_pass_direct=False
scene.render.bake.use_pass_indirect=False
scene.render.bake.use_pass_color=True
images={}
for label in ('BaseColor','Emission'):
    im=bpy.data.images.new('BipolarNanoflake_Core_'+label,1024,1024,alpha=False,float_buffer=True)
    im.colorspace_settings.name='sRGB'
    images[label]=im
    for mat in mesh.materials:
        n=mat.node_tree.nodes.new('ShaderNodeTexImage');n.image=im;n.label='核心烘焙_'+label
        for node in mat.node_tree.nodes:node.select=False
        n.select=True;mat.node_tree.nodes.active=n
scene['核心UV与贴图']='1024 BaseColor/Emission 独立图集；源材质保持可编辑。'
print('BAKE_PREPARED',len(mesh.uv_layers),len(mesh.materials),flush=True)
