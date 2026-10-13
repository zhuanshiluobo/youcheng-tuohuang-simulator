"""按参考图的局部轮廓和三块提纯石料投影关系做最终对齐。"""
import bpy
import json
import traceback
from pathlib import Path

OUT=Path(__file__).resolve().parent
SIZE=1024
STATUS=OUT/'制作状态.json'
source=(OUT/'build_orirock_family.py').read_text(encoding='utf-8')
exec(compile(source[source.index('def bake_job(job):'):source.index('def run():')],str(OUT/'build_orirock_family.py'),'exec'))

def status(stage,**extra):
    STATUS.write_text(json.dumps({'stage':stage,**extra},ensure_ascii=False,indent=2),encoding='utf-8')

def color(rgb):
    def linear(v):return v/12.92 if v<=.04045 else ((v+.055)/1.055)**2.4
    return tuple(linear(v/255) for v in rgb)+(1,)

jobs=[]
for key,name in [('Orirock','源岩'),('OrirockCube','固源岩'),('OrirockCluster','固源岩组'),('OrirockConcentration','提纯源岩')]:
    scene=bpy.data.scenes[name+'_材质版']
    bpy.context.window.scene=scene
    meshes=[o for o in scene.objects if o.type=='MESH']
    root=next(o for o in scene.objects if o.name.startswith(key+'_Root'))
    materials=list(meshes[0].data.materials)
    jobs.append(dict(key=key,name=name,scene=scene,root=root,meshes=meshes,materials=materials,dir=OUT/key))
    if key=='Orirock':
        main=next(o for o in meshes if '主岩' in o.name)
        maximum=max(v.co.z for v in main.data.vertices)
        for v in main.data.vertices:
            factor=1-.28*max(0,v.co.z)/maximum
            v.co.x*=factor
            v.co.y*=factor
        main.data.update()
    elif key=='OrirockConcentration':
        right=next(o for o in meshes if '后右' in o.name)
        front=next(o for o in meshes if '前方' in o.name)
        right.location.x-=.13
        right.location.z-=.32
        for v in right.data.vertices:
            v.co.x*=.85
            v.co.y*=.85
        front.location.x-=.35
        right.data.update()
    if key=='Orirock':palette={0:(201,183,126)}
    elif key=='OrirockCube':palette={0:(211,195,142)}
    elif key=='OrirockCluster':palette={0:(210,192,139),1:(90,87,72)}
    else:palette={0:(207,179,118)}
    for index,rgb in palette.items():
        mat=materials[index]
        ramp=next(n for n in mat.node_tree.nodes if n.type=='VALTORGB')
        ramp.color_ramp.elements[0].color=color(tuple(int(v*.82) for v in rgb))
        ramp.color_ramp.elements[1].color=color(tuple(min(255,int(v*1.09)) for v in rgb))
        mat.diffuse_color=color(rgb)
    for mat in materials:
        broad=next(n for n in mat.node_tree.nodes if n.type=='TEX_NOISE' and n.inputs['Scale'].default_value<30)
        broad.inputs['Scale'].default_value=23 if key=='OrirockConcentration' else 17

def finish():
    try:
        result=[bake_job(job) for job in jobs]
        status('完成',models=result,revision='Wiki 最终对照：收尖轮廓、矿质深浅、提纯石料大小与高低')
    except Exception:status('失败',error=traceback.format_exc())
    return None

status('最后一轮图标轮廓和构图对齐')
bpy.app.timers.register(finish,first_interval=1)
print('对齐参考图的收尖轮廓、深浅分区和提纯石料大小高低，开始最后烘焙。')
