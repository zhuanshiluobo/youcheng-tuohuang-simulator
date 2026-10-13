"""把真实石质斑纹用于方块六面，不投射整张材料图标，不更改几何。"""
from pathlib import Path
import bpy,json,math,re,numpy as np
from mathutils import Vector,Quaternion

OUT=Path(__file__).resolve().parent
base=bpy.app.driver_namespace['cluster_square_revision'];work=base['scene']
if bpy.context.scene!=work:raise RuntimeError('当前观察场景已改变，先确认模型引用。')
scene=bpy.data.scenes.new('固源岩组_矿物斑纹与原图色面修订')
scene.world=work.world.copy();scene.render.engine=work.render.engine;scene.cycles.samples=20
scene.unit_settings.system=work.unit_settings.system;scene.unit_settings.scale_length=work.unit_settings.scale_length
scene.view_settings.view_transform=work.view_settings.view_transform;scene.view_settings.look=work.view_settings.look
scene.view_settings.exposure=work.view_settings.exposure;scene.view_settings.gamma=work.view_settings.gamma
scene.render.resolution_x=scene.render.resolution_y=1000;scene.render.resolution_percentage=100
scene.render.image_settings.file_format='PNG';scene.render.film_transparent=True
collections={};objects={};material_map={}
for old in work.collection.children:
    new=bpy.data.collections.new(old.name+'_斑纹版');scene.collection.children.link(new)
    new.hide_render=old.hide_render;new.hide_viewport=old.hide_viewport;collections[old]=new
    for obj in old.objects:
        if obj in objects:new.objects.link(objects[obj]);continue
        copy=obj.copy()
        if obj.data:copy.data=obj.data.copy()
        new.objects.link(copy);objects[obj]=copy
        if copy.type=='MESH':
            for k,mat in enumerate(copy.data.materials):
                if mat not in material_map:material_map[mat]=mat.copy()
                copy.data.materials[k]=material_map[mat]
for old,new in objects.items():
    new.parent=objects.get(old.parent);new.matrix_world=old.matrix_world.copy()
scene.camera=objects[work.camera];bpy.context.window.scene=scene
parts=[objects[o] for o in base['parts']];solid=objects[base['solid']];root=objects[base['root']]
preview=collections[base['preview']]
right,up,front=[base[k].copy() for k in ('right','up','front')]
floor=base['floor'];camera_quat=base['camera_quat'];camera_location=base['camera_location'].copy()
image=bpy.data.images.load(str(OUT/'Orirock_StoneAtlas_2x2_v1.png'),check_existing=True)
image.colorspace_settings.name='sRGB'
width,height=image.size
raw=np.empty(width*height*4,dtype=np.float32);image.pixels.foreach_get(raw)
raw=raw.reshape(height,width,4)[:,:,:3]
linear=np.where(raw<=.04045,raw/12.92,((raw+.055)/1.055)**2.4)
means=[]
for u,v in [(0,.5),(.5,.5),(0,0),(.5,0)]:
    x,y=int(u*width),int(v*height)
    means.append(linear[y:y+height//2,x:x+width//2].mean((0,1)))

def to_linear(rgb):
    a=np.array(rgb,dtype=float)/255
    return np.where(a<=.04045,a/12.92,((a+.055)/1.055)**2.4)

def to_srgb(value):
    a=np.array(value[:3]);return np.where(a<=.0031308,a*12.92,1.055*a**(1/2.4)-.055)*255

# 根据当前实图，避免上灰面过亮和浅色面偏白；这是独立模型基色，不改稀有度背景。
targets={(0,0):(47,48,47),(0,2):(191,171,130),(0,5):(126,107,66),
         (1,0):(35,46,56),(1,2):(87,81,57),(1,5):(215,197,159),
         (2,0):(109,100,80),(2,2):(158,141,106),(2,5):(170,150,116),
         (3,0):(29,38,46),(3,2):(190,169,132),(3,5):(105,85,48),
         (4,0):(81,75,72),(4,2):(142,114,65),(4,5):(237,222,176)}
records=[]
for old,mat in material_map.items():
    match=re.match(r'04_(\d+)_(\d+)_',old.name)
    if not match:continue
    block,face=map(int,match.groups());rgb=targets.get((block,face),tuple(to_srgb(old.diffuse_color)))
    # 暖白/中性灰/灰青矿芯/赭色风化皮分别使用真实平面纹理区，不改变面本身。
    if sum(rgb)/3>145:tile=2
    elif rgb[2]>rgb[0]*1.09 or (rgb[0]<45 and rgb[1]>rgb[0]):tile=1
    elif max(rgb)-min(rgb)<22 and sum(rgb)/3<105:tile=0
    else:tile=3
    n,l=mat.node_tree.nodes,mat.node_tree.links
    uv=n.new('ShaderNodeUVMap');uv.uv_map='StoneFace';uv.label='原始真实面UV，不是图标投影'
    scale=n.new('ShaderNodeVectorMath');scale.operation='MULTIPLY';scale.inputs[1].default_value=(.485,.485,1)
    l.new(uv.outputs[0],scale.inputs[0])
    offset=n.new('ShaderNodeVectorMath');offset.operation='ADD'
    u,v=[(0,.5),(.5,.5),(0,0),(.5,0)][tile]
    offset.inputs[1].default_value=(u+.0075,v+.0075,0);l.new(scale.outputs[0],offset.inputs[0])
    tex=n.new('ShaderNodeTexImage');tex.image=image;tex.interpolation='Linear';tex.extension='EXTEND'
    tex.label='矿物斑纹图集，不含物体边界或稀有度圆环';l.new(offset.outputs[0],tex.inputs['Vector'])
    tone=n.new('ShaderNodeMixRGB');tone.blend_type='MULTIPLY';tone.inputs[0].default_value=1
    tone.inputs[2].default_value=tuple(to_linear(rgb)/np.maximum(means[tile],.0001))+(1,)
    l.new(tex.outputs['Color'],tone.inputs[1]);tone.label='各真实色面的矿物底色校准'
    grain=next(x for x in n if x.label=='细密颗粒颜色')
    l.new(tone.outputs[0],grain.inputs[1]);grain.inputs[0].default_value=.09
    mat.diffuse_color=tuple(to_linear(rgb))+(1,)
    records.append(dict(material=mat.name,block=block,face=face,tile=tile,rgb=list(rgb)))

# 侧向的中性软光让近乎侧看的右后浅面可读；只在独立观察场景，不能改游戏公共灯。
data=bpy.data.lights.new('04_右侧原图对照软光','AREA');data.energy=180;data.shape='DISK';data.size=3
light=bpy.data.objects.new('04_右侧软光_不进入游戏',data);preview.objects.link(light)
light.location=right*3.5+front*2.8+up*.10;light.rotation_euler=(-light.location).to_track_quat('-Z','Y').to_euler()
geometry=dict(triangles=len(solid.data.loop_triangles),model_zmin=min(v.co.z for v in solid.data.vertices),floor_z=floor,geometry_unchanged=True)
preserved=[];materials=list(dict.fromkeys(solid.data.materials))

def set_front_view():
    scene.camera.location=camera_location;scene.camera.rotation_euler=camera_quat.to_euler()
    for a in bpy.context.screen.areas:
        if a.type=='VIEW_3D':
            space=a.spaces.active;space.shading.type='MATERIAL';space.overlay.show_overlays=False
            space.region_3d.view_rotation=camera_quat;space.region_3d.view_location=Vector((0,0,0))
            space.region_3d.view_distance=4.7;space.region_3d.view_perspective='ORTHO'
    for o in preview.objects:o.hide_set(True)

def render_atlas_model():
    paths={}
    for label,angle in [('正面',0),('背面',180)]:
        q=Quaternion(Vector((0,0,1)),math.radians(angle));scene.camera.location=q@camera_location;scene.camera.rotation_euler=(q@camera_quat).to_euler()
        scene.render.filepath=str(OUT/('斑纹_'+label+'.png'));bpy.ops.render.render(write_still=True);paths[label]=scene.render.filepath
    set_front_view();image.pack()
    bpy.data.libraries.write(str(OUT/'OrirockCluster_StonePattern.blend'),{scene},fake_user=True)
    (OUT/'制作状态.json').write_text(json.dumps(dict(stage='全侧矿物斑纹与色面修订，待正面验收',scene=scene.name,geometry=geometry,renders=paths,
        atlas=str(OUT/'Orirock_StoneAtlas_2x2_v1.png'),atlas_source='内置image_gen生成的平面石质纹理，不是材料整体截图或图标投射',face_settings=records,unity_imported=False),ensure_ascii=False,indent=2),encoding='utf-8')
    return None

set_front_view();bpy.app.driver_namespace['cluster_square_revision']=globals().copy()
bpy.app.timers.register(render_atlas_model,first_interval=1)
print(json.dumps(dict(scene=scene.name,atlas_size=[width,height],materials=len(records),geometry=geometry),ensure_ascii=False))
