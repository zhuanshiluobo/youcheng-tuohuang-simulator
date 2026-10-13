"""补足天然断面的不平整和矿质碎片分区，不把岩石做成光滑规则积木。"""
import bpy
import bmesh
import math
import json
import random
import traceback
from pathlib import Path
from mathutils import Vector, noise

OUT=Path(__file__).resolve().parent
SIZE=1024
STATUS=OUT/'制作状态.json'
source=(OUT/'build_orirock_family.py').read_text(encoding='utf-8')
exec(compile(source[source.index('def bake_job(job):'):source.index('def run():')],str(OUT/'build_orirock_family.py'),'exec'))

def status(stage,**extra):
    STATUS.write_text(json.dumps({'stage':stage,**extra},ensure_ascii=False,indent=2),encoding='utf-8')

jobs=[]
for key,name,level in [('Orirock','源岩',1),('OrirockCube','固源岩',2),('OrirockCluster','固源岩组',3),('OrirockConcentration','提纯源岩',4)]:
    scene=bpy.data.scenes[name+'_材质版']
    bpy.context.window.scene=scene
    root=next(o for o in scene.objects if o.name.startswith(key+'_Root'))
    meshes=[o for o in scene.objects if o.type=='MESH']
    materials=list(meshes[0].data.materials)
    jobs.append(dict(key=key,name=name,level=level,scene=scene,root=root,meshes=meshes,materials=materials,dir=OUT/key))
    if level<4:
        for obj in meshes:
            bm=bmesh.new()
            bm.from_mesh(obj.data)
            faces=[f for f in bm.faces if f.calc_area()>.02]
            edges=list({e for f in faces for e in f.edges if e.calc_length()>.15})
            bmesh.ops.triangulate(bm,faces=faces)
            bmesh.ops.subdivide_edges(bm,edges=edges,cuts=2,use_grid_fill=True)
            bm.normal_update()
            strength=.034 if level==1 else .025
            for v in bm.verts:
                value=noise.noise_vector(v.co*13+Vector((level*3,1,2)))
                v.co+=v.normal*value.x*strength
            bm.normal_update()
            for f in bm.faces:
                center=f.calc_center_median()
                signal=noise.noise_vector(center*9+Vector((2,5,7))).x
                old=f.material_index
                # 矿质斑块顺着断面分布；密集小三角不是明暗烘焙。
                if old==2 and signal>.18:f.material_index=1 if signal<.45 else 0
                elif old in (0,1) and signal<-.32:f.material_index=3
                f.smooth=False
            bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
            bm.to_mesh(obj.data)
            bm.free()
            obj.data.update()
    else:
        front=next(o for o in meshes if '前方' in o.name)
        front.location.y+=.10
        # 六边形的规则顶面保留，不给提纯材料添加天然崩口。
    for o in scene.objects:o.select_set(o in meshes)
    bpy.context.view_layer.objects.active=meshes[0]
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(60),island_margin=.008)
    bpy.ops.object.mode_set(mode='OBJECT')

def finish():
    try:
        result=[bake_job(job) for job in jobs]
        status('完成',models=result,revision='天然破断面与矿质分区；提纯源岩保留规则短柱')
    except Exception:status('失败',error=traceback.format_exc())
    return None

status('正在补足天然断面')
bpy.app.timers.register(finish,first_interval=1)
print('已补足天然石块断面与矿质碎片分区，开始最终烘焙。')
