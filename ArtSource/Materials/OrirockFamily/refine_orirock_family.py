"""对照 PRTS 图标调整岩石崩口、颗粒感和提纯柱比例。保留首轮造型阶段。"""
import bpy
import json
import math
import random
import traceback
from pathlib import Path
from mathutils import Vector

OUT=Path(__file__).resolve().parent
STATUS=OUT/'制作状态.json'
# 复用同一制作文件中的烘焙方法，不重新创建场景或覆盖首轮阶段文件。
source=(OUT/'build_orirock_family.py').read_text(encoding='utf-8')
helpers=source[source.index('def bake_job(job):'):source.index('def run():')]
exec(compile(helpers,str(OUT/'build_orirock_family.py'),'exec'))
SIZE=1024

def status(stage,**extra):
    STATUS.write_text(json.dumps({'stage':stage,**extra},ensure_ascii=False,indent=2),encoding='utf-8')

def linear(v):
    return v/12.92 if v<=0.04045 else ((v+0.055)/1.055)**2.4

def color(rgb):
    return tuple(linear(v/255) for v in rgb)+(1,)

jobs=[]
for key,name,level in [('OrirockCube','固源岩',2),('OrirockCluster','固源岩组',3),('OrirockConcentration','提纯源岩',4)]:
    scene=bpy.data.scenes[name+'_材质版']
    bpy.context.window.scene=scene
    root=next(o for o in scene.objects if o.name.startswith(key+'_Root'))
    meshes=[o for o in scene.objects if o.type=='MESH']
    materials=[]
    for obj in meshes:
        for mat in obj.data.materials:
            if mat not in materials:materials.append(mat)
    job=dict(key=key,name=name,level=level,scene=scene,root=root,meshes=meshes,
        materials=materials,dir=OUT/key)
    jobs.append(job)
    palette=[(184,166,109),(139,130,84),(57,70,76),(87,93,69)]
    if level==4:palette=[(207,179,118),(134,127,84),(87,92,63),(114,116,77)]
    for mat,rgb in zip(materials,palette):
        nodes,links=mat.node_tree.nodes,mat.node_tree.links
        bsdf=next(n for n in nodes if n.type=='BSDF_PRINCIPLED')
        # 第一色阶是宽尺度矿质色，第二色阶是微孔。
        ramp=next(n for n in nodes if n.type=='VALTORGB')
        ramp.color_ramp.elements[0].color=color(tuple(int(v*0.62) for v in rgb))
        ramp.color_ramp.elements[1].color=color(tuple(min(255,int(v*1.09)) for v in rgb))
        mat.diffuse_color=color(rgb)
        grain=next(n for n in nodes if n.type=='TEX_NOISE' and n.inputs['Scale'].default_value>100)
        grit=nodes.new('ShaderNodeValToRGB')
        grit.color_ramp.elements[0].position=0.27
        grit.color_ramp.elements[0].color=(0.35,0.35,0.35,1)
        grit.color_ramp.elements[1].position=0.74
        grit.color_ramp.elements[1].color=(1,1,1,1)
        links.new(grain.outputs['Fac'],grit.inputs[0])
        base=bsdf.inputs['Base Color'].links[0].from_socket
        mix=nodes.new('ShaderNodeMixRGB')
        mix.blend_type='MULTIPLY'
        mix.inputs[0].default_value=0.36
        links.new(base,mix.inputs[1])
        links.new(grit.outputs[0],mix.inputs[2])
        links.new(mix.outputs[0],bsdf.inputs['Base Color'])

    if level<4:
        for idx,obj in enumerate(meshes):
            rnd=random.Random(220+level*20+idx)
            mesh=obj.data
            low=Vector([min(v.co[i] for v in mesh.vertices) for i in range(3)])
            high=Vector([max(v.co[i] for v in mesh.vertices) for i in range(3)])
            span=high-low
            # 不规则裂口落在上沿和左右棱线上，保持固源岩的整体方块轮廓。
            positions=[Vector((low.x+span.x*.28,low.y+span.y*.07,high.z)),
                       Vector((high.x,low.y+span.y*.05,low.z+span.z*.45)),
                       Vector((low.x,low.y+span.y*.42,low.z+span.z*.65))]
            for cut_i,local in enumerate(positions[:3 if idx==0 else 2]):
                bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=1,radius=1)
                cutter=bpy.context.object
                cutter.name=key+'_临时崩口'
                cutter.location=obj.matrix_world @ local
                cutter.scale=tuple(s*rnd.uniform(.11,.17) for s in span)
                cutter.rotation_euler=tuple(rnd.uniform(0,math.pi) for _ in range(3))
                for mat in materials:cutter.data.materials.append(mat)
                for face in cutter.data.polygons:face.material_index=1 if cut_i%2 else 3
                bpy.context.view_layer.objects.active=obj
                obj.select_set(True)
                mod=obj.modifiers.new('天然崩口','BOOLEAN')
                mod.operation='DIFFERENCE'
                mod.solver='EXACT'
                mod.object=cutter
                bpy.ops.object.modifier_apply(modifier=mod.name)
                # 仅释放刚创建的临时 Blender 对象，不删除任何文件或用户对象。
                bpy.data.objects.remove(cutter,do_unlink=True)
            for face in obj.data.polygons:face.use_smooth=False
    else:
        # 图标是三块宽而短的规则六边形石料，不是细长晶体。
        for obj in meshes:
            for v in obj.data.vertices:v.co.z*=.61
        scene.camera.location=(.7,-6.8,8.4)
        scene.camera.rotation_euler=(-scene.camera.location).to_track_quat('-Z','Y').to_euler()
        scene.camera.data.ortho_scale=3.35

    for obj in scene.objects:obj.select_set(obj in meshes)
    bpy.context.view_layer.objects.active=meshes[0]
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(60),island_margin=.015)
    bpy.ops.object.mode_set(mode='OBJECT')

def finish():
    try:
        result=[bake_job(job) for job in jobs]
        scene=jobs[-1]['scene']
        for area in bpy.context.screen.areas:
            if area.type=='VIEW_3D':
                space=area.spaces.active
                space.shading.type='MATERIAL'
                space.overlay.show_overlays=False
                space.region_3d.view_rotation=scene.camera.rotation_euler.to_quaternion()
                space.region_3d.view_location=(0,0,0)
                space.region_3d.view_distance=4.7
                space.region_3d.view_perspective='ORTHO'
        status('完成',models=result,revision='Wiki 对照：崩口、颗粒感、短柱比例')
    except Exception:status('失败',error=traceback.format_exc())
    return None

status('Wiki 对照调整完成，开始重新烘焙')
bpy.app.timers.register(finish,first_interval=1)
print('已对照图标修正，首轮阶段与 D32钢保持不变。')
