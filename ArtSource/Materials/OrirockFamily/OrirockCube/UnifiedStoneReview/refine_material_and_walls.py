"""从低位崩口验收版局部修正：连续本色、竖直立面，不回滚崩口和底部。"""
import bpy, bmesh, json, importlib.util
import numpy as np
from pathlib import Path
from mathutils import Vector, noise

OUT=Path(__file__).resolve().parent
SOURCE=OUT.parent/'LowerNotchReview/OrirockCube_LowerNotchReview.blend'
BLEND=OUT/'OrirockCube_UnifiedStoneReview.blend'
FRONT=Vector((2.70,-6.20,3.45))
TARGET=Vector((-.03,0,-.015))

def fitted_wall(mesh, axis, side, slots):
    other=1-axis
    ps=[p for p in mesh.polygons if p.material_index in slots and p.normal[axis]*side>.78
        and abs(p.center[other])<.47 and -.48<p.center.z<.48]
    design=np.array([[1,p.center[other],p.center.z] for p in ps])
    coords=np.array([p.center[axis] for p in ps])
    weights=np.sqrt(np.array([p.area for p in ps]))
    coeff=np.linalg.lstsq(design*weights[:,None],coords*weights,rcond=None)[0]
    return coeff.tolist()

def set_view(scene):
    scene.camera.location=FRONT
    scene.camera.rotation_euler=(TARGET-FRONT).to_track_quat('-Z','Y').to_euler()
    scene.camera.data.ortho_scale=2.19
    for area in bpy.context.screen.areas:
        if area.type!='VIEW_3D': continue
        space=area.spaces.active
        space.region_3d.view_rotation=scene.camera.rotation_euler.to_quaternion()
        space.region_3d.view_location=TARGET
        space.region_3d.view_distance=3.25
        space.region_3d.view_perspective='ORTHO'
        space.shading.type='MATERIAL'
        space.shading.use_scene_world=True; space.shading.use_scene_lights=True
        space.overlay.show_overlays=False

def main():
    bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
    scene=bpy.data.scenes['固源岩_底部与低位崩口_待验收']
    bpy.context.window.scene=scene
    scene.name='固源岩_连续岩色与竖直立面_待验收'
    body=scene.objects['固源岩_低位崩口与不规则底部']
    body.name='固源岩_连续岩质主体'
    body.data=body.data.copy()
    body.data.name='固源岩_扶正立面_连续闭合网格'
    chip=scene.objects['OrirockCube_保留断角.001']
    chip.data=chip.data.copy()
    meshes=[body,chip]
    # 面积加权求四个主要立面的倾斜，只移除随高度变化的横向收缩。
    wall_specs=[('右面',0,1,{3}),('左面',0,-1,{4}),
                ('正面',1,-1,{2}),('背面',1,1,{4,10})]
    before={name:fitted_wall(body.data,axis,side,slots) for name,axis,side,slots in wall_specs}
    back_verts={i for p in body.data.polygons if p.material_index in {4,10} and p.normal.y>.72 for i in p.vertices}
    def unlean(co):
        old=co.copy()
        for axis,positive,negative in [(0,'右面','左面'),(1,'背面','正面')]:
            c,a,b=before[positive if old[axis]>=0 else negative]
            section=c+a*old[1-axis]
            co[axis]=old[axis]*section/(section+b*old.z)
    for ob in meshes:
        for v in ob.data.vertices: unlean(v.co)
    # 背面上下原为两个人工分色面；同一主体中的共有顶点连接不变。
    # 整理连续岩壁，保留外缘切角和切入内部的崩口表面。
    back_intercept=before['背面'][0]
    for i in back_verts:
        co=body.data.vertices[i].co
        w=max(0,min(1,(.60-abs(co.x))/.13))
        w=w*w*(3-2*w)
        grain=noise.noise_vector(Vector((co.x*14,1.3,co.z*14)),noise_basis='PERLIN_ORIGINAL').x*.002
        co.y=co.y*(1-w)+(back_intercept+grain)*w
    low=Vector([min(v.co[i] for v in body.data.vertices) for i in range(2)]+[0])
    high=Vector([max(v.co[i] for v in body.data.vertices) for i in range(2)]+[0])
    for ob in meshes:
        for v in ob.data.vertices:
            for axis in (0,1): v.co[axis]=(v.co[axis]-(low[axis]+high[axis])/2)*1.28/(high[axis]-low[axis])
        bm=bmesh.new(); bm.from_mesh(ob.data)
        bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
        # 不重拓扑，不重新布尔，保留上一轮所有崩口与底面。
        bm.to_mesh(ob.data); bm.free(); ob.data.update()
    after={name:fitted_wall(body.data,axis,side,slots) for name,axis,side,slots in wall_specs}
    spec=importlib.util.spec_from_file_location('stone_material_only',str(OUT.parent/'CubeRefinement/refine_cube.py'))
    helper=importlib.util.module_from_spec(spec); spec.loader.exec_module(helper)
    # 仅复用材质节点构造，绝不调用旧生成器的 main/几何流程。
    mat=helper.material('固源岩_统一暖灰岩质_无烘焙光影',(177,169,143))
    nodes=mat.node_tree.nodes
    for n in nodes:
        if n.type=='VALTORGB' and n.label=='岩体连续色层':
            for e,mul in zip(n.color_ramp.elements,[.82,1.09]):
                e.color=helper.rgb(tuple(round(c*mul) for c in (177,169,143)))
        elif n.type=='VALTORGB' and n.label=='片状矿质纹理':
            n.color_ramp.elements[0].color=(.68,.68,.68,1)
        elif n.type=='BUMP':
            n.inputs['Strength'].default_value*=.72
    # 主体和左下断角共享同一个坐标源，纹理不因旧分区或独立对象而重新起算。
    for n in nodes:
        if n.type=='TEX_COORD': n.object=body.parent
    for ob in meshes:
        ob.data.materials.clear(); ob.data.materials.append(mat)
        for p in ob.data.polygons: p.material_index=0
    mat['说明']='单一连续本色。所有朝向与崩口共用同一材质；阴影仅来自场景照明和几何遮挡。'
    # 采用中性灯，明暗随朝向改变，不把阴面、受光面写进 Base Color。
    for o in scene.objects:
        if o.type!='LIGHT' or '底面检查' in o.name: continue
        if '主光' in o.name: pos,energy,size=(4.2,1.0,5.5),560,3.2
        elif '右补光' in o.name: pos,energy,size=(-3.5,-4.5,3.2),55,4.5
        else: pos,energy,size=(-2.0,4.5,3.0),95,4.0
        o.data=o.data.copy(); o.location=pos
        o.rotation_euler=(-o.location).to_track_quat('-Z','Y').to_euler()
        o.data.energy=energy; o.data.size=size; o.data.color=(1,1,1)
    scene.world=scene.world.copy()
    bg=next(n for n in scene.world.node_tree.nodes if n.type=='BACKGROUND')
    bg.inputs['Color'].default_value=(.32,.32,.32,1)
    bg.inputs['Strength'].default_value=.28
    body['本轮修正']='统一所有岩面本色及背面纹理；按主要立面移除上宽下窄；沿用低位崩口和不规则底部。'
    body['默认观察']='正面三分之四正交视图，明暗由中性灯产生。'
    body['当前状态']='待用户 Blender 验收，未导入 Unity。'
    body.parent['说明']='主体立面竖直，连续岩质，无背景、底板或圆环。背面结构由参考推定。'
    scene.cycles.samples=40; scene.cycles.use_denoising=True
    scene.render.resolution_x=scene.render.resolution_y=900
    scene.render.film_transparent=True
    set_view(scene)
    for ob in scene.objects: ob.select_set(ob==body)
    bpy.context.view_layer.objects.active=body
    bpy.context.view_layer.update()
    report={'源文件':str(SOURCE),'当前文件':str(BLEND),'状态':'待 Blender 验收，未导入 Unity',
            '材质':'全主体及断角共用一个连续岩质材质；没有按朝向分配的亮暗色块',
            '主要立面拟合_修改前':before,'主要立面拟合_修改后':after,'网格':[]}
    for ob in meshes:
        bm=bmesh.new(); bm.from_mesh(ob.data); ob.data.calc_loop_triangles()
        report['网格'].append({'名称':ob.name,'三角面':len(ob.data.loop_triangles),
            '非流形边':sum(not e.is_manifold for e in bm.edges),'尺寸':list(ob.dimensions),'材质槽':len(ob.data.materials)})
        bm.free()
    (OUT/'review_status.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND))
    for name,pos,target in [('front',FRONT,TARGET),('rear',(-2.7,6.2,2.8),(0,0,0)),
                            ('right_ortho',(6,0,0),(0,0,0))]:
        scene.camera.location=pos
        scene.camera.rotation_euler=(Vector(target)-scene.camera.location).to_track_quat('-Z','Y').to_euler()
        scene.render.filepath=str(OUT/(name+'.png'))
        print('RENDER',name,flush=True); bpy.ops.render.render(write_still=True)
    set_view(scene)
    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND))
    print('UNIFIED_STONE_READY',json.dumps(report,ensure_ascii=False),flush=True)

if __name__=='__main__': main()
