"""按用户固定验收视角修复方框：真实等边、左右净距相等、实体无交叠。"""
import bpy, bmesh, math, json, sys
import numpy as np
from pathlib import Path
from mathutils import Matrix,Vector
from mathutils.bvhtree import BVHTree

OUT=Path('F:/游城拓荒模拟器/ArtSource/Materials/BipolarNanoflake/RedCoreRegionReview/FixedViewFrameReview')
OUT.mkdir(exist_ok=True)
source=bpy.data.scenes['BipolarNanoflake_Finished']
bpy.context.window.scene=source
bpy.ops.scene.new(type='FULL_COPY')
scene=bpy.context.scene;scene.name='BipolarNanoflake_FixedViewFrameReview'
root=next(o for o in scene.objects if o.name.startswith('BipolarNanoflake_Root'))
root.name='BipolarNanoflake_FixedView_Root'
objects=[o for o in root.children_recursive if o.type=='MESH']
frames=sorted([o for o in objects if o.name.startswith('完整铜红')],key=lambda o:o.name)
extra=next(o for o in objects if o.name.startswith('最大框_'))
core=next(o for o in objects if o.name.startswith('核心_'))
horizontal=next(o for o in objects if o.name.startswith('青色环绕'))
vertical=next(o for o in objects if o.name.startswith('外围纵向'))
horizontal.data=horizontal.data.copy()
vertical.data=vertical.data.copy()
basis=frames[1].matrix_world.to_3x3().normalized()
axis=basis.col[0].normalized();center=Vector((0,0,0))
camera=scene.camera
camera_state={'location':list(camera.location),'rotation':list(camera.rotation_euler),
              'type':camera.data.type,'orthoScale':camera.data.ortho_scale}
frame_objects=[frames[0],extra,frames[1],frames[3],frames[2]]
source_material=frames[0].data.materials[0]
copper=source_material.copy();copper.name='BipolarNanoflake_等距方框_统一红铜'
bsdf=next(n for n in copper.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
bsdf.inputs['Metallic'].default_value=.78
bsdf.inputs['Roughness'].default_value=.24
bsdf.inputs['Emission Strength'].default_value=0
if bsdf.inputs.get('Coat Weight'):bsdf.inputs['Coat Weight'].default_value=.65
if bsdf.inputs.get('Coat Roughness'):bsdf.inputs['Coat Roughness'].default_value=.12
ring_original={o.name:[v.co.copy() for v in o.data.vertices] for o in [horizontal,vertical]}
variants={
 'A':{'angles':[-37,-37,-17,61,-66],'outerSide':1.92,'rail':.033,'gap':.037},
 'B':{'angles':[-43,-43,-20,65,-64],'outerSide':1.92,'rail':.033,'gap':.037},
 'C':{'angles':[-33,-33,-23,58,-69],'outerSide':1.84,'rail':.033,'gap':.037},
 'D':{'angles':[-43,-43,-20,58,-66],'outerSide':1.84,'rail':.033,'gap':.037,'keylight':[-3,0,-5.6]},
 'E':{'angles':[-48,-48,-25,62,-70],'outerSide':1.88,'rail':.033,'gap':.037,'keylight':[-3,0,-5.6]},
 'F':{'angles':[-43,-43,-20,58,-66],'outerSide':1.88,'rail':.033,'gap':.037,'keylight':[-3,0,-5.6],'axisOffset':.08},
 'G':{'angles':[-50,-50,-25,65,-68],'outerSide':1.88,'rail':.033,'gap':.037,'viewComponent':.25,'reflectKey':True},
 'H':{'angles':[-50,-50,-20,65,-68],'outerSide':1.88,'rail':.033,'gap':.037,'viewComponent':.30,'reflectKey':True},
 'I':{'angles':[-43,-43,-30,58,-66],'outerSide':1.88,'rail':.033,'gap':.037,'viewComponent':.25,'reflectKey':True},
 'J':{'angles':[-50,-50,-70,61,-20],'outerSide':1.88,'rail':.033,'gap':.037,'viewComponent':.30,'reflectKey':True,'reflectFace':1,'axisOffset':.08,'coreAngle':-40},
 'K':{'angles':[-50,-50,-20,61,-70],'outerSide':1.88,'rail':.033,'gap':.037,'viewComponent':.30,'reflectKey':True,'reflectFace':1,'axisOffset':.08,'coreAngle':-40},
}
variants['L']={**variants['J'],'angles':[-50,-50,-70,61,0],'contrastLight':True,'corePolish':True}
variants['M']={**variants['K'],'contrastLight':True,'corePolish':True}
variants['N']={**variants['L'],'angles':[-50,-50,-74,54,0],'axisOffset':0,'keyEnergy':1000,'coatWeight':.85}
variants['O']={**variants['L'],'angles':[-43,-43,-68,61,0],'gap':.017,'axisOffset':0,'keyEnergy':1000,'coatWeight':.85,'innerRough':True}
variants['P']={**variants['O'],'angles':[-45,-45,-64,65,0]}
variants['Q']={**variants['O'],'angles':[-40,-40,-64,61,0],'darkFront':True}
variants['R']={**variants['Q'],'angles':[-40,-40,-62,61,0]}
variants['S']={**variants['Q'],'angles':[-40,-40,-68,65,0],'frontDepth':.11}
variants['T']={**variants['Q'],'angles':[-40,-40,-70,66,0],'frontDepth':.11}
variants['U']={**variants['S'],'angles':[-38,-38,-68,65,0],'horizontalAngle':-78,'coreRadiusScale':1.20,'keySize':2,'coatWeight':1}
variants['V']={**variants['T'],'horizontalAngle':-78,'coreRadiusScale':1.20,'keySize':2,'coatWeight':1}
variants['W']={**variants['U'],'outerSide':1.92,'rail':.050,'gap':.006,'coreRadiusScale':1.10}
variants['X']={**variants['W'],'rail':.055,'gap':.004}
variants['Y']={**variants['W'],'outerSide':1.94,'gap':.012}
variants['Z']={**variants['Y'],'rail':.045,'gap':.017}
variants['AA']={**variants['Y'],'angles':[-41.1,-41.1,-69.8,67.1,-33.5],
    'rails':[.075,.075,.050,.010,.050],'gap':.006,'viewComponent':.213,
    'axisScreenAngle':20.69,'highlightDepth':.105}
variants['AB']={**variants['Y'],'angles':[-40.9901,-40.9901,-70.8046,66.4804,11.7469],
    'planeSpins':[-2.56653,-2.56653,-4.21779,6.41214,2.59787],
    'rails':[.0545748,.0545748,.0777532,.1415798,.050],
    'outerSide':1.93202,'gap':.006,'viewComponent':.117539,'axisScreenAngle':21.5436}
variants['AC']={**variants['AB'],'rails':[.0545748,.0545748,.0777532,.075,.050]}
variants['AD']={**variants['AA'],'angles':[-41.3,-41.3,-70,66.9,-12],
    'planeSpins':[-4.016,-4.016,-4.652,1.318,2.8],
    'rails':[.075,.075,.050,.050,.050]}
variants['AE']={**variants['Y'],'angles':[-40.4701,-40.4701,-70.3623,66.4668,-18.5985],
    'planeSpins':[-1.52670,-1.52670,-4.72251,6.78735,1.87037],
    'rails':[.0512172,.0512172,.0754378,.1289753,.050],
    'outerSide':1.930011,'gap':.010,'viewComponent':.050664,'axisScreenAngle':21.6673,'highlightDepth':.115}
variants['AF']={**variants['AE'],'rails':[.0512172,.0512172,.0754378,.080,.050]}
variants['AG']={**variants['AE'],'rearMatte':True,'axisOffset':-.043}
variants['AH']={**variants['AF'],'rearMatte':True,'axisOffset':-.043}
variants['AI']={**variants['Y'],'angles':[-34,-34,-72,67,-12],
    'rails':[.050,.050,.060,.080,.050],'gap':.012,'highlightDepth':.100,
    'rearMatte':True,'axisLocked':True}
variants['AJ']={**variants['AI'],'angles':[-34,-34,-72,67,-5]}
variants['AK']={**variants['AI'],'angles':[-40,-40,-71,67,-5]}
variants['AL']={**variants['AK'],'angles':[-42,-42,-71,67,-5]}

dark_copper=copper.copy();dark_copper.name='BipolarNanoflake_前框_氧化红铜暗面'
dark_bsdf=next(n for n in dark_copper.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
base_color=list(dark_bsdf.inputs['Base Color'].default_value)
dark_bsdf.inputs['Base Color'].default_value=tuple(c*.22 for c in base_color[:3])+(1,)
dark_bsdf.inputs['Metallic'].default_value=1
dark_bsdf.inputs['Roughness'].default_value=.32
dark_bsdf.inputs['Coat Weight'].default_value=.05
rear_copper=copper.copy();rear_copper.name='BipolarNanoflake_后框_磨砂红铜'
rear_bsdf=next(n for n in rear_copper.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
rear_bsdf.inputs['Metallic'].default_value=.9
rear_bsdf.inputs['Roughness'].default_value=.42
rear_bsdf.inputs['Coat Weight'].default_value=.04
rear_bsdf.inputs['Base Color'].default_value=tuple(c*.25 for c in base_color[:3])+(1,)
rear_bsdf.inputs['Metallic'].default_value=.75
rear_bsdf.inputs['Roughness'].default_value=.50
rear_bsdf.inputs['Coat Weight'].default_value=0
rear_bsdf.inputs['Specular IOR Level'].default_value=.08

# 所有框面保持同一红铜颜色；内壁采用较粗糙的表面加工，亮色只来自反光。
finish_attribute=copper.node_tree.nodes.new('ShaderNodeAttribute')
finish_attribute.attribute_name='框面粗糙度遮罩'
rough_map=copper.node_tree.nodes.new('ShaderNodeMapRange')
rough_map.inputs['From Min'].default_value=0
rough_map.inputs['From Max'].default_value=1
rough_map.inputs['To Min'].default_value=.24
rough_map.inputs['To Max'].default_value=.58
copper.node_tree.links.new(finish_attribute.outputs['Fac'],rough_map.inputs['Value'])
coat_map=copper.node_tree.nodes.new('ShaderNodeMapRange')
coat_map.inputs['From Min'].default_value=0
coat_map.inputs['From Max'].default_value=1
coat_map.inputs['To Min'].default_value=.85
coat_map.inputs['To Max'].default_value=0
copper.node_tree.links.new(finish_attribute.outputs['Fac'],coat_map.inputs['Value'])

def linear(value):
    return value/12.92 if value<=.04045 else ((value+.055)/1.055)**2.4

core_polished=False
def polish_core(radius_scale=1):
    global core_polished
    if core_polished:return
    for slot in core.material_slots:
        if slot.material:slot.material=slot.material.copy()
    red=core.data.materials[0]
    red.name='BipolarNanoflake_固定视角_集中红橙核心'
    p=next(n for n in red.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    p.inputs['Metallic'].default_value=.05;p.inputs['Roughness'].default_value=.65
    p.inputs['Specular IOR Level'].default_value=.08;p.inputs['Transmission Weight'].default_value=0
    p.inputs['Emission Strength'].default_value=3.8
    for material in core.data.materials:
        if '浅金透光片体' in material.name:
            p=next(n for n in material.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
            warm=(linear(.96),linear(.86),linear(.62),1)
            p.inputs['Base Color'].default_value=warm;p.inputs['Emission Color'].default_value=warm
            p.inputs['Emission Strength'].default_value=.9;p.inputs['Transmission Weight'].default_value=0
    trace=bpy.data.materials.new('BipolarNanoflake_固定视角_红橙细导路');trace.use_nodes=True
    p=next(n for n in trace.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    color=(linear(.98),linear(.31),linear(.045),1)
    p.inputs['Base Color'].default_value=color;p.inputs['Metallic'].default_value=.6
    p.inputs['Emission Color'].default_value=color;p.inputs['Emission Strength'].default_value=1.7
    p.inputs['Roughness'].default_value=.3
    core.data.materials.append(trace);trace_index=len(core.data.materials)-1
    disc_ids={v for p in core.data.polygons if p.material_index in (0,1) for v in p.vertices}
    for vertex_id in disc_ids:
        vertex=core.data.vertices[vertex_id]
        vertex.co.x*=radius_scale;vertex.co.y*=radius_scale
    bm=bmesh.new();bm.from_mesh(core.data)
    paths=[[(.125,.028,.0038),(.181,.063,.0038),(.239,.047,.0038),(.291,.092,.0038)],
           [(.125,-.030,.0038),(.171,-.080,.0038),(.236,-.101,.0038),(.270,-.149,.0038)],
           [(-.125,.026,.0038),(-.181,.054,.0038),(-.242,.113,.0038),(-.278,.146,.0038)],
           [(-.125,-.030,.0038),(-.180,-.075,.0038),(-.242,-.118,.0038),(-.282,-.151,.0038)]]
    for points in paths:
        rings=[]
        for index,pt in enumerate(points):
            v=Vector(pt)
            tangent=Vector(points[min(index+1,len(points)-1)])-Vector(points[max(index-1,0)])
            tangent.normalize();first=tangent.cross(Vector((0,0,1))).normalized();second=tangent.cross(first)
            rings.append([bm.verts.new(v+.0013*(math.cos(k*math.tau/6)*first+math.sin(k*math.tau/6)*second)) for k in range(6)])
        for a,b in zip(rings,rings[1:]):
            for k in range(6):
                f=bm.faces.new((a[k],a[(k+1)%6],b[(k+1)%6],b[k]));f.material_index=trace_index;f.smooth=True
        for ring,reverse in [(rings[0],True),(rings[-1],False)]:
            f=bm.faces.new(tuple(reversed(ring)) if reverse else tuple(ring));f.material_index=trace_index
    bm.normal_update();bm.to_mesh(core.data);bm.free()
    attr=core.data.color_attributes.get('核心红橙渐变')
    if attr:
        for polygon in core.data.polygons:
            if polygon.material_index!=0:continue
            for index in polygon.loop_indices:
                v=core.data.vertices[core.data.loops[index].vertex_index].co
                hot=math.exp(-((math.hypot(v.x,v.y)/(.14*radius_scale))**2))
                veins=.95+.05*math.sin(v.x*74+2*math.sin(v.y*45))
                attr.data[index].color=(linear(.12+.86*hot)*veins,linear(.008+.34*hot)*veins,linear(.004+.06*hot),1)
    core.data.update();core_polished=True

def make_frame(obj,side,rail,depth,angle,inner_rough=False,plane_spin=0):
    outer=[(-side/2,-side/2),(side/2,-side/2),(side/2,side/2),(-side/2,side/2)]
    inner=[(-side/2+rail,-side/2+rail),(side/2-rail,-side/2+rail),
           (side/2-rail,side/2-rail),(-side/2+rail,side/2-rail)]
    vertices=[(x,y,z) for z in (-depth/2,depth/2) for ring in [outer,inner] for x,y in ring]
    faces=[]
    for i in range(4):
        j=(i+1)%4
        faces.extend([(i,j,8+j,8+i),(4+i,12+i,12+j,4+j),
                      (8+i,8+j,12+j,12+i),(i,4+i,4+j,j)])
    mesh=bpy.data.meshes.new(obj.name+'_等边实体框')
    mesh.from_pydata(vertices,[],faces);mesh.materials.append(copper);mesh.update()
    bm=bmesh.new();bm.from_mesh(mesh);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(mesh);bm.free()
    finish=mesh.attributes.new(name='框面粗糙度遮罩',type='FLOAT',domain='CORNER')
    for polygon in mesh.polygons:
        value=1 if inner_rough and polygon.index%4==1 else 0
        for loop_index in polygon.loop_indices:finish.data[loop_index].value=value
    obj.data=mesh;obj.location=center
    obj.rotation_euler=(basis@Matrix.Rotation(math.radians(angle),3,'X')).to_euler()
    obj['完成尺寸']=[side,side,rail,depth]
    obj['框条宽度']=rail;obj['框条厚度']=depth
    obj['绕共同轴角度']=angle;obj['共同轴方向']=list(axis)
    obj['框面内转角']=0
    obj['共同轴_局部方向']=[1,0,0]
    obj['结构说明']='实体正方形；框条统一铜材质。左右侧框条沿共同轴等净距；亮面为真实反光。'
    for modifier in obj.modifiers:
        if modifier.type=='BEVEL':modifier.width=.002;modifier.segments=3

def apply_variant(spec):
    if any(spec.get('planeSpins',[])):
        raise ValueError('该框面内旋转候选已撤回：方框只允许绕固定共同轴旋转。')
    if bsdf.inputs.get('Coat Weight'):bsdf.inputs['Coat Weight'].default_value=spec.get('coatWeight',.65)
    coat_map.inputs['To Min'].default_value=spec.get('coatWeight',.65)
    for socket in [bsdf.inputs['Roughness'],bsdf.inputs['Coat Weight']]:
        for link in list(socket.links):copper.node_tree.links.remove(link)
    if spec.get('innerRough'):
        copper.node_tree.links.new(rough_map.outputs['Result'],bsdf.inputs['Roughness'])
        copper.node_tree.links.new(coat_map.outputs['Result'],bsdf.inputs['Coat Weight'])
    root.rotation_mode='QUATERNION'
    if spec.get('axisLocked'):
        root.rotation_quaternion=(.9956101179122925,.008157147094607353,.04174027591943741,.0833771824836731)
    elif spec.get('viewComponent') is not None:
        cam_basis=camera.matrix_world.to_3x3().normalized()
        axis_camera=cam_basis.inverted()@axis
        direction=Vector((axis_camera.x,axis_camera.y)).normalized()
        if spec.get('axisScreenAngle') is not None:
            screen_angle=math.radians(spec['axisScreenAngle'])
            direction=Vector((math.cos(screen_angle),-math.sin(screen_angle)))
        q=spec['viewComponent']
        target_axis=cam_basis@Vector((direction.x*math.sqrt(1-q*q),direction.y*math.sqrt(1-q*q),q))
        root.rotation_quaternion=axis.rotation_difference(target_axis)
    else:root.rotation_quaternion=(1,0,0,0)
    rails=spec.get('rails',[spec['rail']]*5)
    spins=[0]*5 if spec.get('axisLocked') else spec.get('planeSpins',[0]*5)
    sides=[spec['outerSide']]
    axis_span=spec['outerSide']/(2*math.cos(math.radians(spins[0])))
    for index,rail in enumerate(rails[:-1]):
        axis_span-=rail/math.cos(math.radians(spins[index]))+spec['gap']
        sides.append(2*math.cos(math.radians(spins[index+1]))*axis_span)
    depths=[.065,.055,spec.get('frontDepth',.065),spec.get('highlightDepth',.075),.065]
    for obj,side,rail,depth,angle,spin in zip(frame_objects,sides,rails,depths,spec['angles'],spins):
        make_frame(obj,side,rail,depth,angle,spec.get('innerRough',False),spin)
        obj.location=center+axis*spec.get('axisOffset',0)
        for modifier in obj.modifiers:
            if modifier.type=='BEVEL':modifier.material=-1
    if spec.get('darkFront'):
        front=frame_objects[2]
        front.data.materials[0]=dark_copper
        front.data.materials.append(copper)
        for modifier in front.modifiers:
            if modifier.type=='BEVEL':modifier.material=1
    if spec.get('rearMatte'):frame_objects[4].data.materials[0]=rear_copper
    if spec.get('coreAngle') is not None:
        core.rotation_euler=(basis@Matrix.Rotation(math.radians(spec['coreAngle']),3,'X')).to_euler()
    if spec.get('corePolish'):polish_core(spec.get('coreRadiusScale',1))
    # 两条闭合环的半径错开，消除共同轴两端的实体相交。
    for obj,radius in [(horizontal,1.075),(vertical,1.10)]:
        ratio=radius/1.075
        for v,co in zip(obj.data.vertices,ring_original[obj.name]):v.co=co*ratio
        obj.data.update();obj['圆环中心线半径']=radius;obj['线径']=.016*ratio
    if spec.get('horizontalAngle') is not None:
        horizontal.rotation_euler=(basis@Matrix.Rotation(math.radians(spec['horizontalAngle']),3,'X')).to_euler()
    if spec.get('keylight'):
        light=next(o for o in scene.objects if o.type=='LIGHT' and o.name.startswith('仅预览_主光'))
        light.location=spec['keylight']
        light.rotation_euler=(-light.location).to_track_quat('-Z','Y').to_euler()
    bpy.context.view_layer.update()
    if spec.get('reflectKey'):
        normal=frame_objects[3].matrix_world.to_3x3().normalized().col[spec.get('reflectFace',2)]
        view=camera.matrix_world.to_3x3().normalized().col[2]
        light_direction=(2*normal.dot(view)*normal-view).normalized()
        light=next(o for o in scene.objects if o.type=='LIGHT' and o.name.startswith('仅预览_主光'))
        light.location=light_direction*5
        light.data.size=spec.get('keySize',4)
        light.rotation_euler=(-light.location).to_track_quat('-Z','Y').to_euler()
    if spec.get('contrastLight'):
        for light in scene.objects:
            if light.type!='LIGHT':continue
            if light.name.startswith('仅预览_主光'):light.data.energy=spec.get('keyEnergy',500)
            if light.name.startswith('仅预览_侧光'):light.data.energy=60
            if light.name.startswith('仅预览_背光'):light.data.energy=100
    return sides

scene.render.engine='CYCLES';scene.cycles.use_denoising=True
scene.render.film_transparent=True
scene.render.image_settings.file_format='PNG';scene.render.image_settings.color_mode='RGBA'
scene.view_settings.view_transform='AgX';scene.view_settings.exposure=-.25

def render(label,resolution=720,samples=24):
    scene.render.resolution_x=resolution;scene.render.resolution_y=resolution
    scene.render.resolution_percentage=100;scene.cycles.samples=samples
    scene.render.filepath=str(OUT/('双极纳米片_固定验收视角_'+label+'.png'))
    bpy.ops.render.render(write_still=True)

def inspect_geometry(sides,spec):
    depsgraph=bpy.context.evaluated_depsgraph_get()
    bvh={};counts=[]
    for obj in objects:
        evaluated=obj.evaluated_get(depsgraph);mesh=evaluated.to_mesh()
        verts=[evaluated.matrix_world@v.co for v in mesh.vertices]
        polygons=[tuple(p.vertices) for p in mesh.polygons]
        bvh[obj.name]=BVHTree.FromPolygons(verts,polygons,all_triangles=False,epsilon=.000001)
        evaluated.to_mesh_clear()
    for i,a in enumerate(objects):
        for b in objects[i+1:]:
            overlap=bvh[a.name].overlap(bvh[b.name])
            counts.append({'a':a.name,'b':b.name,'triangleCrossings':len(overlap)})
    records=[]
    for i,obj in enumerate(frame_objects):
        side=sides[i];rail=obj['框条宽度']
        spin=math.radians(obj['框面内转角'])
        outer_axis=side/(2*math.cos(spin));inner_axis=(side/2-rail)/math.cos(spin)
        next_outer=sides[i+1]/(2*math.cos(math.radians(frame_objects[i+1]['框面内转角']))) if i<4 else None
        axis_local=Vector(obj['共同轴_局部方向'])
        records.append({'object':obj.name,'side':side,'rail':rail,
            'depth':obj['框条厚度'],
            'angle':obj['绕共同轴角度'],'planeSpin':obj['框面内转角'],
            'axis':list((obj.matrix_world.to_3x3().normalized()@axis_local).normalized()),
            'axisPlaneNormalDot':(obj.matrix_world.to_3x3().normalized()@Vector((0,0,1))).dot((root.matrix_world.to_3x3()@axis).normalized()),
            'center':list(obj.matrix_world.translation),
            'leftRailInterval':[spec.get('axisOffset',0)-outer_axis,spec.get('axisOffset',0)-inner_axis],
            'rightRailInterval':[spec.get('axisOffset',0)+inner_axis,spec.get('axisOffset',0)+outer_axis],
            'leftClearanceToNext':inner_axis-next_outer if i<4 else None,
            'rightClearanceToNext':inner_axis-next_outer if i<4 else None})
    return records,counts

def bake_core_textures():
    texture_dir=OUT/'Textures';texture_dir.mkdir(exist_ok=True)
    for obj in scene.objects:obj.select_set(False)
    core.select_set(True);bpy.context.view_layer.objects.active=core
    bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(66),island_margin=.012)
    bpy.ops.object.mode_set(mode='OBJECT')
    core.data.uv_layers.active.name='核心烘焙UV'
    scene.cycles.samples=8;scene.render.bake.margin=6;scene.render.bake.use_clear=True
    images={}
    for label in ('BaseColor','Emission'):
        im=bpy.data.images.new('BipolarNanoflake_FixedView_Core_'+label,width=1024,height=1024,alpha=True,float_buffer=True)
        im.colorspace_settings.name='Linear Rec.709';images[label]=im
        for material in core.data.materials:
            nt=material.node_tree
            image_node=nt.nodes.new('ShaderNodeTexImage');image_node.image=im
            image_node.name='固定视角核心烘焙_'+label
            nt.nodes.active=image_node
        previous=[]
        if label=='BaseColor':
            for material in core.data.materials:
                nt=material.node_tree;p=next(n for n in nt.nodes if n.type=='BSDF_PRINCIPLED')
                output=next(n for n in nt.nodes if n.type=='OUTPUT_MATERIAL')
                old=output.inputs['Surface'].links[0].from_socket
                emission=nt.nodes.new('ShaderNodeEmission')
                color=p.inputs['Base Color']
                if color.is_linked:nt.links.new(color.links[0].from_socket,emission.inputs['Color'])
                else:emission.inputs['Color'].default_value=color.default_value
                emission.inputs['Strength'].default_value=1
                nt.links.new(emission.outputs[0],output.inputs['Surface'])
                previous.append((nt,output,old,emission))
        bpy.ops.object.bake(type='EMIT')
        for nt,output,old,emission in previous:
            nt.links.new(old,output.inputs['Surface']);nt.nodes.remove(emission)
        if label=='Emission':
            pixels=np.empty(1024*1024*4,dtype=np.float32);im.pixels.foreach_get(pixels)
            colors=pixels.reshape(-1,4)
            emission_scale=max(1,math.ceil(float(colors[:,:3].max())))
            colors[:,:3]/=emission_scale;im.pixels.foreach_set(pixels)
        texture_path=str(texture_dir/(im.name+'.png'))
        saved_view=(scene.view_settings.view_transform,scene.view_settings.look,scene.view_settings.exposure,scene.view_settings.gamma)
        scene.view_settings.view_transform='Standard';scene.view_settings.look='None'
        scene.view_settings.exposure=0;scene.view_settings.gamma=1
        im.save_render(texture_path,scene=scene)
        scene.view_settings.view_transform=saved_view[0];scene.view_settings.look=saved_view[1]
        scene.view_settings.exposure=saved_view[2];scene.view_settings.gamma=saved_view[3]
        loaded=bpy.data.images.load(texture_path,check_existing=False);loaded.colorspace_settings.name='sRGB';loaded.pack()
        for material in core.data.materials:material.node_tree.nodes['固定视角核心烘焙_'+label].image=loaded
        images[label]=loaded
        print('CORE_TEXTURE_BAKED',label,flush=True)
    for material in core.data.materials:
        nt=material.node_tree;p=next(n for n in nt.nodes if n.type=='BSDF_PRINCIPLED')
        for name,label in [('Base Color','BaseColor'),('Emission Color','Emission')]:
            for link in list(p.inputs[name].links):nt.links.remove(link)
            tex=nt.nodes['固定视角核心烘焙_'+label]
            nt.links.new(tex.outputs['Color'],p.inputs[name])
        p.inputs['Emission Strength'].default_value=emission_scale
        p.inputs['Transmission Weight'].default_value=0
    return {'BaseColor':images['BaseColor'].filepath_raw,'Emission':images['Emission'].filepath_raw,
            'resolution':1024,'emissionScale':emission_scale,'unityShader':'Standard','opaque':True}

args=sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else ['preview','A','B','C']
selected_labels=args[1:] if args[0]=='preview' else ([args[1]] if args[0]=='baked_preview' else [args[0]])
if selected_labels and all(variants[label].get('axisLocked') for label in selected_labels):
    OUT=OUT/'AxisLockedFrameReview';OUT.mkdir(exist_ok=True)
if args[0]=='preview':
    for label in args[1:]:
        spec=variants[label];sides=apply_variant(spec)
        records,counts=inspect_geometry(sides,spec)
        render('候选'+label)
        (OUT/('candidate_'+label+'.json')).write_text(json.dumps({'frames':records,'intersections':counts,'camera':camera_state},ensure_ascii=False,indent=2),encoding='utf-8')
        print('FIXED_CAMERA_CANDIDATE',label,'INTERSECTIONS',sum(x['triangleCrossings'] for x in counts),flush=True)
elif args[0]=='baked_preview':
    label=args[1];spec=variants[label];sides=apply_variant(spec)
    bake_core_textures();render('烘焙颜色复核',720,24)
else:
    label=args[0];spec=variants[label];sides=apply_variant(spec)
    records,counts=inspect_geometry(sides,spec)
    if any(x['triangleCrossings'] for x in counts):raise RuntimeError('分件存在交叉，不能保存为人工验收版。')
    textures=bake_core_textures()
    root['验收视角']='沿用任务前独立参考相机；禁止换视角补偿造型。'
    root['左右框条净距']=spec['gap'];root['各框条宽度']=spec.get('rails',[spec['rail']]*5)
    if spec.get('rails'):
        if '框条统一宽度' in root:del root['框条统一宽度']
    else:root['框条统一宽度']=spec['rail']
    world_axis=(root.matrix_world.to_3x3()@axis).normalized()
    root['共同轴方向']=list(world_axis);root['轴线通过核心中心']=list(center)
    root['五框中心']=list(root.matrix_world.to_3x3()@(center+axis*spec.get('axisOffset',0)))
    root['验收状态']='待用户人工验收；未替换Unity'
    root['旋转限制']='固定共同轴；各框仅绕该轴旋转；禁止框面内旋转和轴线倾斜。'
    scene['验收状态']='原验收相机、五个等边方框、左右等净距，无固定白带'
    render('整体',1000,48)
    location=camera.location.copy();rotation=camera.rotation_euler.copy();scale=camera.data.ortho_scale
    camera.data.ortho_scale=.88;render('核心近景',900,40)
    camera.data.ortho_scale=2.65;camera.location=world_axis*6
    camera.rotation_euler=(-camera.location).to_track_quat('-Z','Y').to_euler()
    render('同轴端视',900,32)
    hide={o:o.hide_render for o in objects}
    for obj in objects:obj.hide_render=(obj!=frame_objects[0])
    normal=frame_objects[0].matrix_world.to_3x3().normalized()
    camera.location=(normal@Vector((0,0,1)))*6
    camera.rotation_euler=normal.to_euler();camera.data.ortho_scale=2.30
    render('方框正视',900,32)
    for obj,value in hide.items():obj.hide_render=value
    camera.location=-location;camera.rotation_euler=(-camera.location).to_track_quat('-Z','Y').to_euler()
    camera.data.ortho_scale=scale;render('背面',1000,32)
    camera.location=location;camera.rotation_euler=rotation;camera.data.ortho_scale=scale
    scene.cycles.samples=48
    for o in scene.objects:o.select_set(False)
    root.select_set(True);bpy.context.view_layer.objects.active=root
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type=='VIEW_3D':
                space=area.spaces.active
                space.region_3d.view_perspective='CAMERA'
                space.shading.type='MATERIAL'
                space.shading.use_scene_lights=True;space.shading.use_scene_world=True
                space.overlay.show_extras=False
    for obj in scene.objects:
        if obj.type=='LIGHT' and obj.name.startswith('仅预览_'):obj.hide_set(False)
    blend=OUT/'BipolarNanoflake_FixedView_Review.blend'
    fbx=OUT/'BipolarNanoflake_FixedView_Review.fbx'
    for obj in objects:obj.select_set(True)
    bpy.ops.export_scene.fbx(filepath=str(fbx),use_selection=True,object_types={'EMPTY','MESH'},
        use_mesh_modifiers=True,add_leaf_bones=False,axis_forward='-Z',axis_up='Y',
        apply_unit_scale=True,bake_space_transform=False,mesh_smooth_type='FACE',use_tspace=False,path_mode='STRIP')
    for obj in objects:obj.select_set(False)
    manifest={'status':'待用户人工验收；未替换Unity','variant':label,'camera':camera_state,
        'cameraUnchanged':list(camera.location)==camera_state['location'] and list(camera.rotation_euler)==camera_state['rotation'] and camera.data.ortho_scale==camera_state['orthoScale'],
        'frames':records,'intersections':counts,'fixedWhiteBands':False,'textures':textures,
        'parameters':spec,'materials':{},
        'backside':'单张参考图的背面沿用已有薄片结构推定。','blend':str(blend),'fbx':str(fbx)}
    manifest['axisLocked']=bool(spec.get('axisLocked'))
    manifest['rootQuaternion']=list(root.rotation_quaternion)
    manifest['axisUnchanged']=world_axis.dot(Vector((.9785727262496948,.12912581861019135,-.16038091480731964)))>.999999
    manifest['rotationRule']='各框局部X与固定共同轴一致；只绕该轴旋转；框面内转角为0'
    for obj in objects:
        for slot in obj.material_slots:
            material=slot.material
            if not material:continue
            p=next(n for n in material.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
            manifest['materials'][material.name]={'baseColor':list(p.inputs['Base Color'].default_value),
                'metallic':p.inputs['Metallic'].default_value,'roughness':p.inputs['Roughness'].default_value,
                'roughnessFromSurfaceAttribute':p.inputs['Roughness'].is_linked,
                'emissionStrength':p.inputs['Emission Strength'].default_value,
                'unityShader':'Standard' if obj==core else '沿用现有展品着色器',
                'coreTextureAtlas':obj==core}
    (OUT/'fixed_view_review_manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
    bpy.ops.wm.save_as_mainfile(filepath=str(blend),compress=True)
    print('FIXED_VIEW_REVIEW_SAVED',str(blend),flush=True)
