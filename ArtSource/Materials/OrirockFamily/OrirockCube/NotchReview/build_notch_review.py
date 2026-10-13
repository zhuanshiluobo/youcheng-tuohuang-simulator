"""从上一轮精修源复制，补做顶部后缘及右后缘大崩口；仅供 Blender 验收。"""
import bpy, bmesh, json, importlib.util, math
from pathlib import Path
from mathutils import Vector

OUT=Path(__file__).resolve().parent
SOURCE=OUT.parent/'CubeRefinement'/'OrirockCube_Refined.blend'
BLEND=OUT/'OrirockCube_NotchReview.blend'

def copy_scene(source):
    scene=bpy.data.scenes.new('固源岩_大崩口与背面_待验收')
    scene.unit_settings.system=source.unit_settings.system
    scene.unit_settings.scale_length=source.unit_settings.scale_length
    scene.world=source.world.copy()
    col=bpy.data.collections.new('固源岩_待验收模型')
    dev=bpy.data.collections.new('验收灯光与相机_不导出')
    scene.collection.children.link(col); scene.collection.children.link(dev)
    mapping={}
    for obj in source.objects:
        new=obj.copy()
        if obj.data: new.data=obj.data.copy()
        (col if obj.type in {'MESH','EMPTY'} else dev).objects.link(new)
        mapping[obj]=new
    for old,new in mapping.items():
        if old.parent in mapping: new.parent=mapping[old.parent]
        if old==source.camera: scene.camera=new
        if new.type=='MESH':
            for i,m in enumerate(new.data.materials):
                new.data.materials[i]=m.copy()
    return scene,[o for o in col.objects if o.type=='MESH']

def flake_detail(mat, scale=17, strength=.50):
    n,l=mat.node_tree.nodes,mat.node_tree.links
    bs=next(a for a in n if a.type=='BSDF_PRINCIPLED')
    previous=bs.inputs['Base Color'].links[0].from_socket
    prevnormal=bs.inputs['Normal'].links[0].from_socket
    tc=n.new('ShaderNodeTexCoord')
    noise=n.new('ShaderNodeTexNoise'); noise.inputs['Scale'].default_value=8
    noise.inputs['Detail'].default_value=3
    l.new(tc.outputs['Object'],noise.inputs['Vector'])
    mult=n.new('ShaderNodeVectorMath'); mult.operation='SCALE'; mult.inputs['Scale'].default_value=.15
    l.new(noise.outputs['Color'],mult.inputs[0])
    add=n.new('ShaderNodeVectorMath'); add.operation='ADD'
    l.new(tc.outputs['Object'],add.inputs[0]); l.new(mult.outputs[0],add.inputs[1])
    stretch=n.new('ShaderNodeVectorMath'); stretch.operation='MULTIPLY'; stretch.inputs[1].default_value=(1.25,1.25,2.0)
    l.new(add.outputs[0],stretch.inputs[0])
    vor=n.new('ShaderNodeTexVoronoi'); vor.feature='DISTANCE_TO_EDGE'; vor.inputs['Scale'].default_value=scale
    l.new(stretch.outputs[0],vor.inputs['Vector'])
    ramp=n.new('ShaderNodeValToRGB'); ramp.label='背面层状剥落与裂隙'
    ramp.color_ramp.elements[0].position=.009; ramp.color_ramp.elements[0].color=(.54,.54,.54,1)
    ramp.color_ramp.elements[1].position=.036; ramp.color_ramp.elements[1].color=(1,1,1,1)
    l.new(vor.outputs['Distance'],ramp.inputs[0])
    patch=n.new('ShaderNodeTexNoise'); patch.inputs['Scale'].default_value=9; patch.inputs['Detail'].default_value=3
    l.new(tc.outputs['Object'],patch.inputs['Vector'])
    mask=n.new('ShaderNodeMapRange'); mask.inputs['From Min'].default_value=.46; mask.inputs['From Max'].default_value=.68
    mask.inputs['To Min'].default_value=0; mask.inputs['To Max'].default_value=strength
    l.new(patch.outputs['Fac'],mask.inputs[0])
    mix=n.new('ShaderNodeMixRGB'); mix.blend_type='MULTIPLY'; l.new(mask.outputs[0],mix.inputs[0])
    l.new(previous,mix.inputs[1]); l.new(ramp.outputs[0],mix.inputs[2]); l.new(mix.outputs[0],bs.inputs['Base Color'])
    relief=n.new('ShaderNodeMixRGB'); relief.inputs[1].default_value=(1,1,1,1)
    l.new(mask.outputs[0],relief.inputs[0]); l.new(ramp.outputs[0],relief.inputs[2])
    bump=n.new('ShaderNodeBump'); bump.label='真实切面上的细层理'
    bump.inputs['Strength'].default_value=.28; bump.inputs['Distance'].default_value=.0035
    l.new(relief.outputs[0],bump.inputs['Height']); l.new(prevnormal,bump.inputs['Normal']); l.new(bump.outputs[0],bs.inputs['Normal'])

def hull(name,points,matindex,body,collection):
    me=bpy.data.meshes.new(name+'_Mesh')
    bm=bmesh.new()
    for p in points: bm.verts.new(p)
    bmesh.ops.convex_hull(bm,input=list(bm.verts),use_existing_faces=False)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    for f in bm.faces: f.material_index=matindex
    bm.to_mesh(me); bm.free(); me.update()
    ob=bpy.data.objects.new(name,me); collection.objects.link(ob)
    for m in body.data.materials: me.materials.append(m)
    return ob

def cut(body,name,points,matindex,collection):
    cutter=hull(name,points,matindex,body,collection)
    bpy.context.view_layer.objects.active=body
    for ob in bpy.context.scene.objects: ob.select_set(ob==body)
    mod=body.modifiers.new(name,'BOOLEAN'); mod.operation='DIFFERENCE'; mod.solver='EXACT'; mod.object=cutter
    bpy.ops.object.modifier_apply(modifier=mod.name)
    cutter.hide_render=True; cutter.hide_set(True)
    cutter.display_type='WIRE'
    cutter['用途']='大崩口切削体，已应用到主体；保留以便后续调整，不导出。'

def setup_view(scene):
    scene.camera.location=(4.3,6.1,4.1)
    scene.camera.rotation_euler=(-scene.camera.location).to_track_quat('-Z','Y').to_euler()
    scene.camera.data.ortho_scale=2.32
    bpy.context.window.scene=scene
    for a in bpy.context.screen.areas:
        if a.type=='VIEW_3D':
            space=a.spaces.active
            space.region_3d.view_rotation=scene.camera.rotation_euler.to_quaternion()
            space.region_3d.view_location=(0,0,0)
            space.region_3d.view_distance=3.45
            space.region_3d.view_perspective='ORTHO'
            space.shading.type='MATERIAL'
            space.shading.use_scene_world=False; space.shading.use_scene_lights=False
            space.overlay.show_overlays=False
            space.clip_end=100

def main():
    bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
    original=bpy.data.scenes['固源岩_方体精修_程序源']
    scene,meshes=copy_scene(original); bpy.context.window.scene=scene
    body=next(o for o in meshes if '主体' in o.name)
    body.name='固源岩_双崩口连续实体'
    spec=importlib.util.spec_from_file_location('previous_material_helpers',str(OUT.parent/'CubeRefinement/refine_cube.py'))
    helper=importlib.util.module_from_spec(spec); spec.loader.exec_module(helper)
    newmaterials=[
        helper.material('崩口_橄榄褐新断面',(121,113,78)),
        helper.material('崩口_深层灰褐断面',(87,84,64)),
        helper.material('崩口_浅黄层理断面',(177,166,129)),
        helper.material('背面_灰蓝剥落层',(57,69,75),True)]
    for m in newmaterials:
        flake_detail(m,14 if m==newmaterials[-1] else 18,.38)
        body.data.materials.append(m)
    flake_detail(body.data.materials[4],14,.30)
    # 仅为背面原有灰蓝面增添剥落层理，前部灰蓝面保持此前结构和材质。
    for p in body.data.polygons:
        if p.material_index==2 and p.normal.y>.40: p.material_index=10
    cutters=bpy.data.collections.new('崩口切削参考_隐藏不导出'); scene.collection.children.link(cutters)

    # A：顶部后缘中左的大 V 形崩口。断口向顶面内侧、背面下方贯通。
    cut(body,'A_顶部后缘大崩口',[
        (-.49,.70,.72),(-.14,.70,.72),(-.32,.34,.63),
        (-.48,.77,.39),(-.32,.48,.36),(-.14,.75,.46),
        (-.54,.90,1.02),(-.08,.90,1.02),(-.33,.29,1.02)],7,cutters)
    # 同一顶部断裂带右段的宽浅阶梯，避免后缘仍是一条直线。
    cut(body,'A_顶部后缘右段阶梯',[
        (.16,.78,.76),(.55,.77,.73),(.35,.34,.63),
        (.20,.76,.43),(.34,.49,.40),(.52,.74,.49),
        (.13,.92,.98),(.62,.92,.98),(.37,.34,.99)],7,cutters)
    # A 的边缘剥片，形成有厚度的斜肩与小破边。
    cut(body,'A_断口左肩剥片',[
        (-.53,.61,.58),(-.41,.60,.51),(-.43,.39,.62),
        (-.56,.80,.80),(-.33,.79,.81),(-.45,.37,.82)],8,cutters)

    # B：右后棱中段的大阶梯崩口。浅黄色侧面明确内缩，露出深层石料。
    cut(body,'B_右后棱中段大阶梯',[
        (.76,.30,.22),(.42,.68,.24),(.76,.88,.24),
        (.41,.44,.11),(.40,.44,-.17),(.67,.26,-.13),
        (.79,.41,-.34),(.47,.80,-.32),(.91,.92,-.35),
        (.96,.28,.32),(.98,.96,.36)],8,cutters)
    # B 上沿再剥去一层浅色皮，轮廓呈两级落差。
    cut(body,'B_侧后缺口上缘浅层',[
        (.80,.26,.26),(.57,.49,.29),(.57,.42,.08),(.84,.24,.08),
        (.96,.84,.35),(.49,.80,.30),(.93,.86,.02)],9,cutters)
    # 背面局部浅断痕与大崩口相呼应，深度远小于 A/B。
    cut(body,'背面_斜向浅剥落',[
        (-.08,.66,.21),(.14,.66,.08),(.10,.66,-.13),(-.14,.66,-.05),
        (-.015,.585,.01),(-.18,.88,.26),(.22,.88,.19),(.22,.88,-.19),(-.21,.88,-.13)],7,cutters)
    # 新切面已有体积，再加入小尺度凹凸；仅为切面法线保留清楚的层次。
    bm=bmesh.new(); bm.from_mesh(body.data)
    bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.000005)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    for face in bm.faces:
        if face.material_index in {7,8,9}: face.smooth=False
    bm.to_mesh(body.data); bm.free(); body.data.update()
    body['验收重点']='A 顶部后缘双段崩裂；B 右后棱中段两级崩口；两处均连到背面。'
    body['当前状态']='待用户在 Blender 验收；不得自动导入 Unity。'
    cutters.hide_viewport=True; cutters.hide_render=True
    for o in meshes: o.select_set(o==body)
    bpy.context.view_layer.objects.active=body
    # 使深色切面在背面视角也可读，不改游戏场景灯光。
    for o in scene.objects:
        if o.type=='LIGHT' and '背光' in o.name:
            o.data.energy=240; o.data.size=4
    scene.render.engine=original.render.engine
    scene.cycles.device='CPU'; scene.cycles.samples=40; scene.cycles.use_denoising=True
    scene.render.threads_mode='FIXED'; scene.render.threads=10
    scene.render.resolution_x=scene.render.resolution_y=1000
    scene.render.resolution_percentage=100; scene.render.film_transparent=True
    scene.render.image_settings.file_format='PNG'; scene.render.image_settings.color_mode='RGBA'
    scene.view_settings.view_transform=original.view_settings.view_transform
    scene.view_settings.look=original.view_settings.look
    setup_view(scene)
    report={'基准':str(SOURCE),'状态':'仅 Blender 待验收，未导入','网格':[]}
    for o in meshes:
        bm=bmesh.new(); bm.from_mesh(o.data); o.data.calc_loop_triangles()
        report['网格'].append({'名称':o.name,'三角面':len(o.data.loop_triangles),
            '非流形边':sum(not e.is_manifold for e in bm.edges),'尺寸':list(o.dimensions)})
        bm.free()
    (OUT/'review_status.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    # 独立验收文件仅收录本轮场景；原精修文件及其历史场景保留在原路径。
    bpy.data.libraries.write(str(BLEND),{scene},fake_user=True)
    bpy.ops.wm.open_mainfile(filepath=str(BLEND))
    scene=bpy.data.scenes['固源岩_大崩口与背面_待验收']; setup_view(scene)
    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND))
    for name,pos in [('front',(3.8,-6.2,3.7)),('rear_right',(4.3,6.1,4.1)),('rear_left',(-4.3,6.1,3.6)),('right',(6.5,2.0,2.6))]:
        scene.camera.location=pos; scene.camera.rotation_euler=(-scene.camera.location).to_track_quat('-Z','Y').to_euler()
        scene.render.filepath=str(OUT/(name+'.png'))
        print('RENDER',name,flush=True); bpy.ops.render.render(write_still=True)
    setup_view(scene); bpy.ops.wm.save_as_mainfile(filepath=str(BLEND))
    print('NOTCH_REVIEW_READY',json.dumps(report,ensure_ascii=False),flush=True)

if __name__=='__main__': main()
