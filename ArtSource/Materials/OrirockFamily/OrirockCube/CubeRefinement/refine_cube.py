"""固源岩方体修订。旧源场景保留；仅新建独立修订场景。"""
import bpy, bmesh, math, json
from pathlib import Path
from mathutils import Vector, noise

OUT = Path(__file__).resolve().parent
SOURCE = OUT.parent/'FrontRevision'/'OrirockCube_FrontRevision.blend'
P = {}

def point(name, xyz):
    P[name] = Vector(xyz)
    return name

def rgb(values):
    return tuple((v/255/12.92 if v/255 <= .04045 else ((v/255+.055)/1.055)**2.4) for v in values)+(1,)

def material(name, color, blue=False):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    mat.diffuse_color = rgb(color)
    n,l = mat.node_tree.nodes, mat.node_tree.links
    bs = next(a for a in n if a.type=='BSDF_PRINCIPLED')
    bs.inputs['Metallic'].default_value = 0
    bs.inputs['Specular IOR Level'].default_value = .18
    tc=n.new('ShaderNodeTexCoord')
    scale=n.new('ShaderNodeVectorMath'); scale.operation='MULTIPLY'
    scale.inputs[1].default_value=(1.8,1.4,.60) if blue else (1,1,1)
    l.new(tc.outputs['Object'],scale.inputs[0])
    def tex(s,detail=3):
        t=n.new('ShaderNodeTexNoise'); t.inputs['Scale'].default_value=s
        t.inputs['Detail'].default_value=detail; t.inputs['Roughness'].default_value=.7
        l.new(scale.outputs[0],t.inputs['Vector']); return t
    broad=tex(6.2,4); flakes=tex(38,3); fine=tex(175,2)
    ramp=n.new('ShaderNodeValToRGB'); ramp.label='岩体连续色层'
    for e,pos,mul in zip(ramp.color_ramp.elements,[.20,.79],[.61,1.17]):
        e.position=pos; e.color=rgb(tuple(min(255,int(c*mul)) for c in color))
    l.new(broad.outputs['Fac'],ramp.inputs[0])
    fr=n.new('ShaderNodeValToRGB'); fr.label='片状矿质纹理'
    fr.color_ramp.elements[0].position=.37; fr.color_ramp.elements[0].color=(.39,.39,.39,1)
    fr.color_ramp.elements[1].position=.61; fr.color_ramp.elements[1].color=(1,1,1,1)
    l.new(flakes.outputs['Fac'],fr.inputs[0])
    mul=n.new('ShaderNodeMixRGB'); mul.blend_type='MULTIPLY'; mul.inputs[0].default_value=.48
    l.new(ramp.outputs[0],mul.inputs[1]); l.new(fr.outputs[0],mul.inputs[2])
    grit=n.new('ShaderNodeMapRange'); grit.inputs['To Min'].default_value=.67; grit.inputs['To Max'].default_value=1.08
    l.new(fine.outputs['Fac'],grit.inputs[0])
    mix=n.new('ShaderNodeMixRGB'); mix.blend_type='MULTIPLY'; mix.inputs[0].default_value=.35
    l.new(mul.outputs[0],mix.inputs[1]); l.new(grit.outputs[0],mix.inputs[2])
    # 局部剥落细缝：扭曲的细胞边界被低频噪声截断，避免整面规则龟裂。
    warp=tex(11,2)
    vs=n.new('ShaderNodeVectorMath'); vs.operation='SCALE'; vs.inputs['Scale'].default_value=.065
    l.new(warp.outputs['Color'],vs.inputs[0])
    add=n.new('ShaderNodeVectorMath'); add.operation='ADD'
    l.new(scale.outputs[0],add.inputs[0]); l.new(vs.outputs[0],add.inputs[1])
    vor=n.new('ShaderNodeTexVoronoi'); vor.feature='DISTANCE_TO_EDGE'; vor.inputs['Scale'].default_value=13
    l.new(add.outputs[0],vor.inputs['Vector'])
    cr=n.new('ShaderNodeMapRange'); cr.inputs['From Min'].default_value=.006; cr.inputs['From Max'].default_value=.022
    cr.inputs['To Min'].default_value=.48; cr.inputs['To Max'].default_value=1
    l.new(vor.outputs['Distance'],cr.inputs[0])
    gate=n.new('ShaderNodeMath'); gate.operation='GREATER_THAN'; gate.inputs[1].default_value=.60
    l.new(flakes.outputs['Fac'],gate.inputs[0])
    cm=n.new('ShaderNodeMixRGB'); cm.blend_type='MULTIPLY'
    l.new(gate.outputs[0],cm.inputs[0]); l.new(mix.outputs[0],cm.inputs[1]); l.new(cr.outputs[0],cm.inputs[2])
    l.new(cm.outputs[0],bs.inputs['Base Color'])
    bump1=n.new('ShaderNodeBump'); bump1.inputs['Strength'].default_value=.38; bump1.inputs['Distance'].default_value=.005
    l.new(fr.outputs[0],bump1.inputs['Height'])
    bump2=n.new('ShaderNodeBump'); bump2.inputs['Strength'].default_value=.29; bump2.inputs['Distance'].default_value=.0023
    l.new(fine.outputs['Fac'],bump2.inputs['Height']); l.new(bump1.outputs[0],bump2.inputs['Normal'])
    l.new(bump2.outputs[0],bs.inputs['Normal'])
    rough=n.new('ShaderNodeMapRange'); rough.inputs['To Min'].default_value=.84; rough.inputs['To Max'].default_value=.96
    l.new(flakes.outputs['Fac'],rough.inputs[0]); l.new(rough.outputs[0],bs.inputs['Roughness'])
    return mat

def mesh(name, points, faces, mats, collection, root):
    keys=list(points); me=bpy.data.meshes.new(name+'_Mesh')
    me.from_pydata([points[k] for k in keys],[],[[keys.index(k) for k in face] for face,mid in faces]); me.update()
    ob=bpy.data.objects.new(name,me); collection.objects.link(ob); ob.parent=root
    for m in mats: me.materials.append(m)
    for poly,(_,mid) in zip(me.polygons,faces): poly.material_index=mid
    bm=bmesh.new(); bm.from_mesh(me)
    if '保留断角' in name:
        bmesh.ops.delete(bm,geom=list(bm.faces),context='FACES_ONLY')
        bmesh.ops.convex_hull(bm,input=list(bm.verts),use_existing_faces=False)
        bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
        for f in bm.faces:
            f.material_index=5 if f.normal.x>.35 else (1 if f.normal.z>.3 else 4)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    # 保留大断面的硬转折，细分只为细小石质起伏服务。
    for e in bm.edges:
        if e.is_manifold and (e.calc_face_angle()>.25 or len({f.material_index for f in e.link_faces})>1): e.smooth=False
    bmesh.ops.triangulate(bm,faces=list(bm.faces))
    bmesh.ops.subdivide_edges(bm,edges=list(bm.edges),cuts=3,use_grid_fill=True)
    bm.normal_update()
    for v in bm.verts:
        if any(not e.smooth for e in v.link_edges): continue
        a=noise.noise_vector(v.co*27,noise_basis='PERLIN_ORIGINAL').x*.002
        a+=noise.noise_vector(v.co*8.5,noise_basis='PERLIN_ORIGINAL').x*.008
        v.co+=v.normal*a
    bmesh.ops.triangulate(bm,faces=list(bm.faces)); bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    for f in bm.faces: f.smooth=True
    bm.to_mesh(me); bm.free(); me.update()
    return ob

def main():
    bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
    scene=bpy.data.scenes.new('固源岩_方体精修_程序源'); bpy.context.window.scene=scene
    scene.unit_settings.system='METRIC'; scene.unit_settings.scale_length=1
    col=bpy.data.collections.new('固源岩_方体精修'); scene.collection.children.link(col)
    root=bpy.data.objects.new('OrirockCube_RefinedEditableRoot',None); col.objects.link(root)
    root['说明']='等边方体，完整背面，浅崩口；背面按单视图推定。无地板、背景、圆环。'
    mats=[material('精修_橄榄灰顶面',(138,131,91)),material('精修_褐色断层',(102,94,64)),
          material('精修_灰蓝断面',(53,68,75),True),material('精修_浅黄断面',(224,209,165)),
          material('精修_背面岩皮',(116,110,81)),material('精修_断角亮面',(148,140,104)),
          material('精修_深褐断层',(88,83,59))]
    # 主体 X/Y/Z 边长 1.28。顶面保留厚实岩盖，四角为浅切角。
    point('TL',(-.64,-.54,.56))
    back=[point(n,p) for n,p in [
        ('BL0',(-.64,.39,.60)),('BL1',(-.57,.58,.62)),('B0',(-.44,.64,.61)),
        ('B1',(-.22,.64,.62)),('B2',(-.06,.63,.64)),('B3',(.13,.64,.61)),
        ('B4',(.33,.64,.63)),('B5',(.52,.60,.61)),('B6',(.62,.49,.60))]]
    point('RT',(.64,.39,.60)); point('TR',(.64,-.46,.56))
    lip=[point(n,p) for n,p in [
        ('L0',(-.58,-.60,.48)),('L1',(-.45,-.61,.42)),('L2',(-.28,-.62,.43)),
        ('L3',(-.07,-.61,.40)),('L4',(.07,-.57,.45)),('L5',(.15,-.52,.54)),
        ('L6',(.28,-.52,.50)),('L7',(.44,-.53,.45)),('L8',(.58,-.51,.47))]]
    top=['TL']+back+['RT','TR']+list(reversed(lip))
    # 内部几处高低控制点，形成宽阔断面而非塌陷薄皮。
    inner=[]
    for i,k in enumerate(top):
        p=P[k]; inner.append(point('T'+str(i),(p.x*.58,p.y*.58,.598+noise.noise_vector(Vector((p.x*5,p.y*5,2))).x*.022)))
    faces=[]
    for i in range(len(top)):
        j=(i+1)%len(top); faces.append(([top[i],top[j],inner[j],inner[i]],0))
    faces.append((inner,0))
    lower=[point(n,p) for n,p in [
        ('C0',(-.61,-.56,.30)),('C1',(-.60,-.64,.24)),('C2',(-.48,-.64,.30)),
        ('C3',(-.32,-.63,.26)),('C4',(-.17,-.64,.29)),('C5',(.00,-.63,.25)),
        ('C6',(.13,-.61,.18)),('C7',(.16,-.60,.07)),('C8',(.29,-.61,-.07)),
        ('C9',(.23,-.62,-.16)),('C10',(.43,-.60,-.22))]]
    seam=[point(n,p) for n,p in [
        ('S0',(.64,-.49,.35)),('S1',(.60,-.41,.25)),('S2',(.60,-.40,.07)),
        ('S3',(.64,-.25,.15)),('S4',(.62,-.28,.02)),('S5',(.60,-.38,-.10)),
        ('S6',(.62,-.45,-.16)),('S7',(.62,-.57,-.24))]]
    fold=[point(n,p) for n,p in [('M0',(.30,-.53,.26)),('M1',(.34,-.59,.06)),('M2',(.47,-.49,-.10))]]
    faces += [(['TL']+lip[:6]+['M0','C6','C5','C4','C3','C2','C1','C0'],1),
        (['C6','M0','M1','C8','C7'],6),(['C8','M1','M2','C9'],1),(['C9','M2','S7','C10'],6),
        (lip[5:]+['TR','S0','S1','M0'],1),(['M0','S1','S2','M1'],6),
        (['M1','S2','S3','S4','S5','M2'],1),(['M2','S5','S6','S7'],6)]
    bottom=[point(n,p) for n,p in [
        ('F0',(-.61,-.61,-.37)),('F1',(-.54,-.61,-.59)),('F2',(-.34,-.63,-.64)),
        ('F3',(-.04,-.63,-.64)),('F4',(.25,-.61,-.63))]]
    point('FB',(.58,-.58,-.62))
    ff=[point(n,p) for n,p in [('I0',(-.37,-.63,.06)),('I1',(-.20,-.64,-.14)),('I2',(-.08,-.62,-.36)),('I3',(.16,-.63,-.44))]]
    faces += [(['C1','C2']+ff+['F4','F3','F2','F1','F0'],2),
              (lower[2:]+['S7','FB','F4']+list(reversed(ff)),2)]
    # 侧背面共享明确的体积边界，后缘只有细小切角，不留尖薄延伸。
    point('R1',(.64,.48,.11)); point('R2',(.64,.54,-.22)); point('R3',(.58,.56,-.62))
    point('HB',(-.55,.57,-.61)); point('HL',(-.64,.42,.03)); point('HBM',(-.40,.64,-.06))
    point('BM',(.13,.64,-.08)); point('BR',(.61,.55,.00))
    faces += [(['RT','TR']+seam+['FB','R3','R2','R1'],3),
        (['TL','C0','C1','F0','F1','HB','HL','BL1','BL0'],4),
        (['BL1']+back[2:]+['RT','R1','BR','BM','HBM','HL'],4),
        (['HL','HBM','BM','BR','R1','R2','R3','HB'],2),
        (['F1','F2','F3','F4','FB','R3','HB'],4)]
    body=mesh('OrirockCube_方体主体',P,faces,mats,col,root)
    # 保留参考中的左下断角，收小到不改变主体方体判断。
    cp={k:Vector(p) for k,p in {
        'a':(-.72,-.60,-.53),'b':(-.66,-.49,-.48),'c':(-.54,-.53,-.43),
        'd':(-.44,-.59,-.53),'e':(-.44,-.68,-.61),'f':(-.50,-.69,-.67),
        'g':(-.67,-.67,-.65),'h':(-.75,-.63,-.61),'i':(-.61,-.70,-.52)}.items()}
    chip=mesh('OrirockCube_保留断角',cp,[(['a','b','c','d','i'],1),(['i','d','e','f'],5),
        (['a','i','f','g','h'],4),(['a','h','g','f','e','d','c','b'],4)],mats,col,root)
    objects=[body,chip]
    low=Vector([min(v.co[i] for v in body.data.vertices) for i in range(3)])
    high=Vector([max(v.co[i] for v in body.data.vertices) for i in range(3)])
    center=(low+high)*.5
    for ob in objects:
        for v in ob.data.vertices:
            for i in range(3): v.co[i]=(v.co[i]-center[i])*1.28/(high[i]-low[i])
        ob.data.update()
    for o in scene.objects: o.select_set(o in objects)
    bpy.context.view_layer.objects.active=body
    bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(57),island_margin=.022)
    bpy.ops.object.mode_set(mode='OBJECT')
    old=next(s for s in bpy.data.scenes if '程序源' in s.name and s!=scene)
    scene.world=old.world.copy()
    devices=bpy.data.collections.new('固源岩_精修预览设备_不导出'); scene.collection.children.link(devices)
    for o in old.objects:
        if o.type in {'CAMERA','LIGHT'}:
            new=o.copy(); new.data=o.data.copy(); devices.objects.link(new)
            if o.type=='CAMERA': scene.camera=new
    scene.camera.location=(3.8,-6.2,3.7)
    scene.camera.rotation_euler=(-scene.camera.location).to_track_quat('-Z','Y').to_euler()
    scene.camera.data.ortho_scale=2.30
    scene.render.engine=old.render.engine; scene.cycles.device='CPU'; scene.cycles.samples=32
    scene.cycles.use_denoising=True; scene.render.threads_mode='FIXED'; scene.render.threads=10
    scene.render.resolution_x=scene.render.resolution_y=900; scene.render.resolution_percentage=100
    scene.render.film_transparent=True; scene.render.image_settings.file_format='PNG'; scene.render.image_settings.color_mode='RGBA'
    scene.view_settings.view_transform='Standard'; scene.view_settings.look='None'
    for area in bpy.context.screen.areas:
        if area.type=='VIEW_3D':
            area.spaces.active.region_3d.view_rotation=scene.camera.rotation_euler.to_quaternion()
            area.spaces.active.region_3d.view_distance=3.7; area.spaces.active.region_3d.view_location=(0,0,0)
            area.spaces.active.shading.type='MATERIAL'
    bpy.context.view_layer.update()
    report={'主模型三轴尺寸':list(body.dimensions),'旧源':str(SOURCE),'网格':[]}
    for o in objects:
        bm=bmesh.new(); bm.from_mesh(o.data); o.data.calc_loop_triangles()
        report['网格'].append({'名称':o.name,'三角面':len(o.data.loop_triangles),'非流形边':sum(not e.is_manifold for e in bm.edges)})
        bm.free()
    (OUT/'revision_geometry.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'OrirockCube_Refined.blend'))
    for name,pos in [('front',(3.8,-6.2,3.7)),('back',(-3.8,6.2,3.7)),('side',(6,1,2.0))]:
        scene.camera.location=pos; scene.camera.rotation_euler=(-scene.camera.location).to_track_quat('-Z','Y').to_euler()
        scene.render.filepath=str(OUT/('draft_'+name+'.png')); bpy.ops.render.render(write_still=True)
    print('REFINEMENT_GEOMETRY',json.dumps(report,ensure_ascii=False),flush=True)

if __name__=='__main__': main()
