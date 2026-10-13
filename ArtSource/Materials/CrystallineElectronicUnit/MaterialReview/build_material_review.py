"""从已验收白模复制独立材质场景，保留几何及白模阶段。"""
import bpy, math, json, hashlib, runpy
from pathlib import Path
from mathutils import Vector

OUT=Path(__file__).resolve().parent
SOURCE=OUT.parent/'FullWhiteReview'/'CrystallineElectronicUnit_FullWhite.blend'
DEST=OUT/'CrystallineElectronicUnit_MaterialReview.blend'
OUT.mkdir(parents=True,exist_ok=True)
if not bpy.app.background:raise RuntimeError('在独立后台创建材质版本。')
source_hash=hashlib.sha256(SOURCE.read_bytes()).hexdigest()
bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
original=bpy.context.scene
original.use_fake_user=True
scene=original.copy()
scene.name='晶体电子单元_材质验收'
scene.use_fake_user=True
for coll in list(scene.collection.children):scene.collection.children.unlink(coll)
for ob in list(scene.collection.objects):scene.collection.objects.unlink(ob)
objects={};collections={}

def clone_object(ob,coll):
    if ob in objects:
        coll.objects.link(objects[ob]);return
    new=ob.copy()
    if ob.data:new.data=ob.data.copy()
    new.name=ob.name+' · 材质'
    new['白模对象']=ob.name
    coll.objects.link(new)
    objects[ob]=new

def clone_collection(coll,parent):
    new=bpy.data.collections.new(coll.name+' · 材质')
    parent.children.link(new);collections[coll]=new
    for ob in coll.objects:clone_object(ob,new)
    for child in coll.children:clone_collection(child,new)

for coll in original.collection.children:clone_collection(coll,scene.collection)
for ob in original.collection.objects:clone_object(ob,scene.collection)
for old,new in objects.items():new.parent=objects.get(old.parent)
scene.world=original.world.copy()
scene.world.name='晶体电子单元_中性反射环境'
scene.camera=objects[original.camera]
if bpy.context.window:bpy.context.window.scene=scene
for manager in bpy.data.window_managers:
    for window in manager.windows:window.scene=scene

def linear(c):return c/12.92 if c<=.04045 else ((c+.055)/1.055)**2.4
def rgba(hexcode):
    s=hexcode.lstrip('#')
    return tuple(linear(int(s[i:i+2],16)/255) for i in (0,2,4))+(1,)

specs={}
def material(key,label,color,metal,rough,trans=0,coat=0,variation=0):
    mat=bpy.data.materials.new(label)
    mat.diffuse_color=rgba(color)
    bsdf=next(n for n in mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    values={'Base Color':rgba(color),'Metallic':metal,'Roughness':rough,
            'Transmission Weight':trans,'IOR':1.47,'Coat Weight':coat,'Coat Roughness':.16}
    for name,value in values.items():
        if name in bsdf.inputs:bsdf.inputs[name].default_value=value
    if variation:
        coord=mat.node_tree.nodes.new('ShaderNodeTexCoord')
        noise=mat.node_tree.nodes.new('ShaderNodeTexNoise')
        noise.inputs['Scale'].default_value=165
        noise.inputs['Detail'].default_value=2
        ramp=mat.node_tree.nodes.new('ShaderNodeMapRange')
        ramp.inputs['To Min'].default_value=rough-variation
        ramp.inputs['To Max'].default_value=rough+variation
        mat.node_tree.links.new(coord.outputs['Object'],noise.inputs['Vector'])
        mat.node_tree.links.new(noise.outputs['Fac'],ramp.inputs['Value'])
        mat.node_tree.links.new(ramp.outputs['Result'],bsdf.inputs['Roughness'])
    specs[key]={'name':label,'srgb':color,'metallic':metal,'roughness':rough,'transmission':trans,'coat':coat}
    return mat

m={
 'crystal':material('crystal','层片_青绿半透晶体','#148D73',.08,.20,.36,.24,.012),
 'edge':material('edge','层片_浅青晶体切边','#249F86',.12,.17,.30,.20),
 'silver':material('silver','台面_抛光中性银','#BFC7C7',1,.135,0,.18,.009),
 'rim':material('rim','边沿_浅香槟金属','#CFBF91',1,.24,0,.08,.012),
 'bronze':material('bronze','箱体_暗黄铜','#73654C',.94,.31,0,0,.02),
 'copper':material('copper','铜柱铜管_抛光橘铜','#DF7D47',1,.235,0,.10,.014),
 'cap':material('cap','柱帽底圈_深褐金属','#29201B',.85,.35,0,0,.014),
 'iron':material('iron','薄铁皮支架_深枪灰','#353B3D',.75,.30,0,0,.014),
 'core':material('core','后部核心_冷黑外壳','#1A222D',0,.53,0,.015,.012),
 'black':material('black','铭牌槽底_炭黑','#181915',.15,.49),
 'letter':material('letter','铭牌文字_暖灰银','#CABFA5',.55,.32),
 'mark':material('mark','台面标签_深灰图案','#343D3D',.48,.38),
 'steel':material('steel','转轴铆钉_灰钢','#89908A',.9,.27),
}
core_bsdf=next(n for n in m['core'].node_tree.nodes if n.type=='BSDF_PRINCIPLED')
core_bsdf.inputs['Specular IOR Level'].default_value=.20

def set_material(ob,*materials):
    ob.data.materials.clear()
    for mat in materials:ob.data.materials.append(mat)
    if ob.type=='MESH':
        for face in ob.data.polygons:face.material_index=0

for old,new in objects.items():
    if new.type not in {'MESH','FONT'}:continue
    n=old.name
    if n.startswith('层片_'):
        set_material(new,m['crystal'],m['edge'])
        for face in new.data.polygons:face.material_index=0 if abs(face.normal.z)>.9 else 1
    elif n=='八角台面_宽斜面凹口':
        set_material(new,m['silver'],m['rim'],m['bronze'])
        for face in new.data.polygons:
            group=face.index//8
            face.material_index=1 if group==0 else (2 if group>=4 else 0)
    elif n=='中央凹口封闭槽底':set_material(new,m['silver'])
    elif n in {'封闭八角底盖','底沿窄护边','上盖接缝下压边'}:set_material(new,m['rim'])
    elif n=='上下等边_直八棱柱箱体':set_material(new,m['bronze'])
    elif n.endswith('_单段铜柱身'):set_material(new,m['copper'])
    elif '侧' in n and '柱_' in n:set_material(new,m['steel'] if n.endswith('小铆钉') else m['cap'])
    elif n.startswith('后组件_'):
        if any(s in n for s in ['短铜绕组','绕组外缘','铜管内端套圈']):mat=m['copper']
        elif any(s in n for s in ['支承轴','铆钉','轴孔座']):mat=m['steel']
        elif '核心' in n:mat=m['core']
        else:mat=m['iron']
        set_material(new,mat)
    elif n.startswith('散热面_'):set_material(new,m['black'] if '槽底' in n else m['rim'])
    elif n in {'正面铭牌_浅凹底板','识别小框内层'}:set_material(new,m['black'])
    elif n.startswith(('铭牌横边','铭牌竖边')):set_material(new,m['bronze'])
    elif n.startswith('铭牌'):set_material(new,m['letter'])
    elif n=='台面小识别牌':set_material(new,m['silver'])
    elif n.startswith(('识别牌','台面三角')):set_material(new,m['mark'])
    else:raise RuntimeError('未分配的白模对象: '+n)

world_bsdf=next(n for n in scene.world.node_tree.nodes if n.type=='BACKGROUND')
world_bsdf.inputs['Color'].default_value=(.32,.35,.38,1)
world_bsdf.inputs['Strength'].default_value=.42

def aim(ob,point):ob.rotation_euler=(Vector(point)-ob.location).to_track_quat('-Z','Y').to_euler()
target=Vector((0,0,1.2775))
for name,loc,energy,sizex,sizey in [
    ('主光',(-3.5,-4.5,6),1050,3.5,4.8),
    ('右侧补光',(4,-1.8,4),620,2.5,3.8),
    ('后轮廓光',(-1,4,5.3),1150,2.0,4.0)]:
    ob=objects[bpy.data.objects[name]]
    ob.location=loc;ob.data.energy=energy;ob.data.color=(1,1,1)
    # 沿用已存在且有效的 AREA 类型，矩形反光板作为摄影灯。
    ob.data.shape='RECTANGLE';ob.data.size=sizex;ob.data.size_y=sizey
    aim(ob,target)
light_coll=collections[bpy.data.collections['观察相机与灯光_不属于模型']]
data=bpy.data.lights.new('材质验收_前方窄条反光','AREA')
data.energy=260;data.shape='RECTANGLE';data.size=.65;data.size_y=3.2
light=bpy.data.objects.new(data.name,data);light_coll.objects.link(light)
light.location=(-.3,-4,2.0);aim(light,target)

scene.cycles.samples=64
scene.cycles.preview_samples=24
scene.cycles.use_denoising=True
scene.cycles.max_bounces=10
scene.cycles.transmission_bounces=8
scene.cycles.glossy_bounces=6
scene.render.resolution_x=1100;scene.render.resolution_y=1100
scene.render.resolution_percentage=100
scene.render.film_transparent=True
scene.view_settings.exposure=0
for ob in scene.objects:
    ob.select_set(False)
    if ob.type=='CAMERA':ob.hide_set(True)
    elif ob.type=='LIGHT':ob.hide_set(False)
root=objects[bpy.data.objects['晶体电子单元_完整白模']]
root.name='晶体电子单元_材质模型'
bpy.context.view_layer.objects.active=root
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='VIEW_3D':
            sp=area.spaces.active;sp.show_region_ui=False;sp.overlay.show_overlays=False
            sp.region_3d.view_perspective='ORTHO';sp.region_3d.view_location=target
            sp.region_3d.view_rotation=scene.camera.rotation_euler.to_quaternion()
            sp.region_3d.view_distance=5.65
            sp.shading.type='MATERIAL'
            sp.shading.use_scene_world=True;sp.shading.use_scene_lights=True

unchanged=all(old.data.vertices[i].co==new.data.vertices[i].co
              for old,new in objects.items() if old.type=='MESH' for i in range(len(old.data.vertices)))
manifest={'stage':'已验收白模之后的材质验收','white_source':str(SOURCE),'white_sha256':source_hash,
          'preserved_white_scene':original.name,'material_scene':scene.name,'mesh_vertices_preserved':unchanged,
          'material_model':str(DEST),'materials':specs,'preview_lights':'中性棚灯；无彩色自发光。',
          'reference':'用户提供的晶体电子单元图标；黄色圆环仅为背景。',
          'geometry':'32层青色层片、直八角底座、双轴居中四柱及前后细节全部沿用已验收形状。',
          'unity_imported':False,'export_note':'当前为Blender材质验收阶段，程序化粗糙度尚未烘焙至游戏贴图。'}
# 后续验收意见集中在局部修正脚本，完整重建也应用同一修正。
revision=runpy.run_path(str(OUT/'apply_recessed_vents_and_deeper_basin.py'))['apply_revision'](save=False)
manifest['latest_geometry_revision']=revision
manifest['mesh_vertices_preserved']=False
manifest['top_layer_meshes_preserved']=True
manifest['geometry']='主体沿用已验收白模；三面内嵌散热槽，中央槽加深，层片仅整体下沉。'
(OUT/'material_review_notes.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
bpy.ops.wm.save_as_mainfile(filepath=str(DEST))
for ob in scene.objects:
    if '核心' in ob.name:
        print('CORE_MATERIAL',ob.name,list(ob.data.materials.keys()))
print('SCENE_PERSISTENCE',[(s.name,s.users,s.use_fake_user,len(s.objects)) for s in bpy.data.scenes])
print('MATERIAL_REVIEW_SAVED',str(DEST),'GEOMETRY_UNCHANGED',unchanged)
scene.cycles.samples=32
scene.render.resolution_x=900;scene.render.resolution_y=900
scene.render.filepath=str(OUT/'Material_Pilot.png')
bpy.ops.render.render(write_still=True,scene=scene.name)
print('MATERIAL_PILOT_COMPLETE')
