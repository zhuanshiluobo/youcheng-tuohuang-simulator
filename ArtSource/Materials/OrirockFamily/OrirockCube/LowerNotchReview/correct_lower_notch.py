"""沿用已验收前的最新材质和顶部崩口，只修正下部缺口、底部与观察视角。"""
import bpy,bmesh,json,importlib.util,math
from pathlib import Path
from mathutils import Vector,noise

OUT=Path(__file__).resolve().parent
PREVIOUS=OUT.parent/'NotchReview'/'OrirockCube_NotchReview.blend'
BASE=OUT.parent/'CubeRefinement'/'OrirockCube_Refined.blend'
BLEND=OUT/'OrirockCube_LowerNotchReview.blend'
FRONT=Vector((2.70,-6.20,3.45))
TARGET=Vector((-.03,0,-.015))

def set_view(scene):
    scene.camera.location=FRONT
    scene.camera.rotation_euler=(TARGET-FRONT).to_track_quat('-Z','Y').to_euler()
    scene.camera.data.ortho_scale=2.19
    bpy.context.window.scene=scene
    for a in bpy.context.screen.areas:
        if a.type=='VIEW_3D':
            s=a.spaces.active
            s.region_3d.view_rotation=scene.camera.rotation_euler.to_quaternion()
            s.region_3d.view_location=TARGET
            s.region_3d.view_distance=3.25
            s.region_3d.view_perspective='ORTHO'
            s.shading.type='MATERIAL'
            s.shading.use_scene_world=True; s.shading.use_scene_lights=True
            s.overlay.show_overlays=False

def apply_cut(body,cutter):
    cutter.hide_set(False); cutter.hide_viewport=False
    bpy.context.view_layer.objects.active=body
    for ob in bpy.context.scene.objects: ob.select_set(ob==body)
    modifier=body.modifiers.new('复用_'+cutter.name,'BOOLEAN')
    modifier.operation='DIFFERENCE'; modifier.solver='EXACT'; modifier.object=cutter
    bpy.ops.object.modifier_apply(modifier=modifier.name)
    cutter.hide_set(True); cutter.hide_render=True

def main():
    bpy.ops.wm.open_mainfile(filepath=str(PREVIOUS))
    scene=bpy.data.scenes['固源岩_大崩口与背面_待验收']; bpy.context.window.scene=scene
    scene.name='固源岩_底部与低位崩口_待验收'
    body=scene.objects['固源岩_双崩口连续实体']
    body.name='固源岩_低位崩口与不规则底部'
    materials=list(body.data.materials)
    # 此前的布尔已应用。读取同一底模，重用当前文件中的 A 与背面切削体和材质，
    # 只撤去位置错误的 B，避免叠加一个新缺口却留下原错误缺口。
    with bpy.data.libraries.load(str(BASE),link=False) as (available,loaded):
        loaded.objects=['OrirockCube_方体主体']
    base=loaded.objects[0]
    body.data=base.data.copy(); body.data.name='固源岩_低位崩口_网格'
    original_material_indices=[p.material_index for p in body.data.polygons]
    body.data.materials.clear()
    for m in materials: body.data.materials.append(m)
    for p,index in zip(body.data.polygons,original_material_indices):
        p.material_index=index
        if p.material_index==2 and p.normal.y>.4: p.material_index=10
    # 底部为连续但不共面的岩质断面，位移限制在下层，保留主要方体体积。
    for v in body.data.vertices:
        if v.co.z>-.43: continue
        w=min(1,max(0,(-.43-v.co.z)/.18))
        q=Vector((v.co.x*3.8,v.co.y*3.8,1.71))
        broad=noise.noise_vector(q,noise_basis='PERLIN_ORIGINAL').x
        detail=noise.noise_vector(q*2.1,noise_basis='PERLIN_ORIGINAL').y
        lift=max(.006,min(.118,.051+.067*broad+.037*detail))
        # 接近外边缘的起伏同样保留，使轮廓不是平直的一条底线。
        v.co.z+=lift*w
    body.data.update()
    cutters=bpy.data.collections['崩口切削参考_隐藏不导出']
    cutters.hide_viewport=False
    for cutter in list(cutters.objects):
        if cutter.name.startswith('A_') or cutter.name.startswith('背面_'):
            apply_cut(body,cutter)
    # 原 B 整体下移 0.34m，轻收切深；底端开到基底，不再形成悬在侧面中部的凹洞。
    for cutter in list(cutters.objects):
        if not cutter.name.startswith('B_'): continue
        for v in cutter.data.vertices:
            v.co.x=.64+(v.co.x-.64)*.83
            v.co.z-=.34
        cutter.name=cutter.name.replace('中段','下部').replace('侧后','侧后下部')
        cutter.data.update(); apply_cut(body,cutter)
    spec=importlib.util.spec_from_file_location('notch_helpers',str(OUT.parent/'NotchReview/build_notch_review.py'))
    helper=importlib.util.module_from_spec(spec); spec.loader.exec_module(helper)
    # 三处低而宽的自然崩边；都与底面连通，不添加底板或独立石片。
    helper.cut(body,'底部_前缘斜断',[
        (-.38,-.82,-.85),(.01,-.82,-.85),(-.17,-.42,-.83),
        (-.34,-.68,-.55),(-.04,-.68,-.59),(-.14,-.53,-.53)],7,cutters)
    helper.cut(body,'底部_左后角崩边',[
        (-.86,.30,-.87),(-.37,.85,-.86),(-.83,.86,-.86),
        (-.65,.30,-.54),(-.40,.64,-.52),(-.56,.48,-.45)],8,cutters)
    helper.cut(body,'底部_不对称深断面',[
        (-.43,-.24,-.88),(.43,-.22,-.88),(.39,.43,-.88),(-.39,.43,-.88),
        (-.38,-.18,-.56),(.30,-.13,-.51),(.32,.33,-.56),(-.28,.34,-.53),
        (-.06,.08,-.38),(.17,.16,-.43)],7,cutters)
    bm=bmesh.new(); bm.from_mesh(body.data)
    bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.000005)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    for face in bm.faces:
        c=face.calc_center_median()
        if face.normal.z<-.48 and c.z<-.40:
            face.material_index=7 if c.x+c.y*.4>-.22 else 8
            face.smooth=False
        elif face.material_index in {7,8,9}: face.smooth=False
    bm.to_mesh(body.data); bm.free(); body.data.update()
    cutters.hide_render=True; cutters.hide_viewport=True
    body['本轮修正']='侧后缺口下移0.34m并与底边贯通；底部增加高低断面和崩边。'
    body['默认观察']='参考图正面：灰蓝面朝前、浅黄侧面在右。'
    body['当前状态']='待用户 Blender 验收；未导入 Unity。'
    bpy.context.view_layer.update()
    meshes=[o for o in scene.objects if o.type=='MESH' and not o.hide_render]
    # 底面检查专用补光只在底面渲染时启用。
    lampdata=bpy.data.lights.new('底面检查补光','AREA')
    lamp=bpy.data.objects.new('底面检查补光',lampdata)
    scene.collection.objects.link(lamp)
    lamp.location=(.3,-1.3,-3.8); lamp.rotation_euler=(-lamp.location).to_track_quat('-Z','Y').to_euler()
    lampdata.energy=130; lampdata.size=4; lamp.hide_render=True; lamp.hide_set(True)
    scene.cycles.samples=32
    set_view(scene)
    for o in scene.objects: o.select_set(o==body)
    bpy.context.view_layer.objects.active=body
    report={'源文件':str(PREVIOUS),'当前文件':str(BLEND),'状态':'待验收，未导入 Unity',
        '正面相机':list(FRONT),'目标点':list(TARGET),'修正':'B 缺口下移0.34m；底面不共面并增加崩边', '网格':[]}
    for o in meshes:
        bm=bmesh.new(); bm.from_mesh(o.data); o.data.calc_loop_triangles()
        report['网格'].append({'名称':o.name,'三角面':len(o.data.loop_triangles),'非流形边':sum(not e.is_manifold for e in bm.edges),'尺寸':list(o.dimensions)})
        bm.free()
    (OUT/'review_status.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND))
    for name,pos,target in [
        ('front',FRONT,TARGET),('right',(6,-1.4,2.7),(0,0,-.06)),
        ('bottom',(3.2,-5,-4.6),(0,0,-.14))]:
        scene.camera.location=pos
        scene.camera.rotation_euler=(Vector(target)-scene.camera.location).to_track_quat('-Z','Y').to_euler()
        lamp.hide_render=name!='bottom'; lamp.hide_set(name!='bottom')
        scene.render.filepath=str(OUT/(name+'.png'))
        print('RENDER',name,flush=True); bpy.ops.render.render(write_still=True)
    lamp.hide_render=True; lamp.hide_set(True)
    set_view(scene); bpy.ops.wm.save_as_mainfile(filepath=str(BLEND))
    print('LOWER_REVIEW_READY',json.dumps(report,ensure_ascii=False),flush=True)

if __name__=='__main__': main()
