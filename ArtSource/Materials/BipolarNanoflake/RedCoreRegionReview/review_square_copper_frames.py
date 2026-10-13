"""人工验收候选：各层真实正方形、统一红铜，白色仅来自光照。"""
import bpy, bmesh, json, math
from pathlib import Path
from mathutils import Vector

SOURCE=Path('F:/游城拓荒模拟器/ArtSource/Materials/BipolarNanoflake/RedCoreRegionReview/BipolarNanoflake_Finished.blend')
OUT=SOURCE.parent/'SquareFrameReview'
OUT.mkdir(exist_ok=True)
assert Path(bpy.data.filepath)==SOURCE, bpy.data.filepath
source_scene=bpy.data.scenes.get('BipolarNanoflake_ReferenceFinished')
assert source_scene is not None
bpy.context.window.scene=source_scene
source_root=source_scene.objects['BipolarNanoflake_Finished_Root']
source_axes={o.name:(o.matrix_world.to_3x3().normalized()@Vector((1,0,0))).normalized() for o in source_root.children_recursive if o.type=='MESH'}
source_centers={o.name:o.matrix_world.translation.copy() for o in source_root.children_recursive if o.type=='MESH'}
bpy.ops.scene.new(type='FULL_COPY')
scene=bpy.context.scene;scene.name='BipolarNanoflake_SquareFrameReview'
root=next(o for o in scene.objects if o.name.startswith('BipolarNanoflake_Finished_Root'))
root.name='BipolarNanoflake_SquareReview_Root'
frames=sorted([o for o in root.children_recursive if o.type=='MESH' and o.name.startswith('BNF_铜框_')],key=lambda o:o.name)
assert len(frames)==5
reference=frames[0].data.materials[0]
copper=reference.copy();copper.name='BipolarNanoflake_审阅_统一红铜'
principled=next(n for n in copper.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
principled.inputs['Metallic'].default_value=.78
principled.inputs['Roughness'].default_value=.27
principled.inputs['Emission Strength'].default_value=0
principled.inputs['Transmission Weight'].default_value=0
coat=principled.inputs.get('Coat Weight')
if coat is not None:coat.default_value=.16
coat_rough=principled.inputs.get('Coat Roughness')
if coat_rough is not None:coat_rough.default_value=.14

records=[]
for obj in frames:
    old=obj.data
    width=max(v.co.x for v in old.vertices)-min(v.co.x for v in old.vertices)
    height=max(v.co.y for v in old.vertices)-min(v.co.y for v in old.vertices)
    rail=float(obj['完成尺寸'][2]);depth=float(obj['完成尺寸'][3])
    edge=width
    outer=[(-edge/2,-edge/2),(edge/2,-edge/2),(edge/2,edge/2),(-edge/2,edge/2)]
    inner=[(-edge/2+rail,-edge/2+rail),(edge/2-rail,-edge/2+rail),
           (edge/2-rail,edge/2-rail),(-edge/2+rail,edge/2-rail)]
    vertices=[(x,y,z) for z in (-depth/2,depth/2) for ring in (outer,inner) for x,y in ring]
    faces=[]
    for i in range(4):
        j=(i+1)%4
        faces.extend([(i,j,8+j,8+i),(4+i,12+i,12+j,4+j),
                      (8+i,8+j,12+j,12+i),(i,4+i,4+j,j)])
    mesh=bpy.data.meshes.new(obj.name+'_等边正方形')
    mesh.from_pydata(vertices,[],faces);mesh.update();mesh.materials.append(copper)
    for p in mesh.polygons:p.material_index=0
    bm=bmesh.new();bm.from_mesh(mesh);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(mesh);bm.free()
    obj.data=mesh
    obj['完成尺寸']=[edge,edge,rail,depth]
    obj['造型修正']='真实正方形；全部面统一铜材质，无浅色分带。高光由灯光、倒角与反射形成。'
    for mod in obj.modifiers:
        if mod.type=='BEVEL':mod.width=.0022;mod.segments=3
    axis=(obj.matrix_world.to_3x3().normalized()@Vector((1,0,0))).normalized()
    original_name=next(name for name in source_axes if obj.name.startswith(name))
    assert abs(axis.dot(source_axes[original_name]))>.99999
    assert (obj.matrix_world.translation-source_centers[original_name]).length<.00001
    bounds=[max(v.co[i] for v in mesh.vertices)-min(v.co[i] for v in mesh.vertices) for i in range(3)]
    assert abs(bounds[0]-bounds[1])<.00001
    records.append({'object':obj.name,'before':[width,height],'after':bounds,
                    'frameRail':rail,'materialSlots':[copper.name],
                    'axis':list(axis),'center':list(obj.matrix_world.translation),
                    'rotationEuler':list(obj.rotation_euler)})

root['验收状态']='待用户人工验收；禁止未获确认即替换Unity。'
root['外框说明']='五个实体正方形框，正视宽高相等；统一红铜材质，没有白色线条或固定高光材质条带。'
scene['验收状态']='人工验收候选，尚未替换Unity正式资产'
scene['修正说明']='只修正五框等边比例与材质条带；同心共轴、原转角、核心和双环保留。'
scene.render.engine='CYCLES';scene.cycles.samples=40;scene.cycles.use_denoising=True
scene.render.resolution_x=1000;scene.render.resolution_y=1000;scene.render.resolution_percentage=100
scene.render.image_settings.file_format='PNG';scene.render.image_settings.color_mode='RGBA'
scene.render.film_transparent=True
scene.camera.data.ortho_scale=3.12
scene.camera.location=(3.6,-5.3,2.8)
scene.camera.rotation_euler=(-scene.camera.location).to_track_quat('-Z','Y').to_euler()
camera=scene.camera
camera_location=camera.location.copy();camera_rotation=camera.rotation_euler.copy();camera_scale=camera.data.ortho_scale
blend=OUT/'BipolarNanoflake_SquareFrames_Review.blend'
fbx=OUT/'BipolarNanoflake_SquareFrames_Review.fbx'

def select_model():
    bpy.ops.object.select_all(action='DESELECT')
    for o in [root]+[o for o in root.children_recursive if o.type=='MESH']:o.select_set(True)
    bpy.context.view_layer.objects.active=root

select_model()
bpy.ops.export_scene.fbx(filepath=str(fbx),use_selection=True,object_types={'EMPTY','MESH'},
    use_mesh_modifiers=True,add_leaf_bones=False,axis_forward='-Z',axis_up='Y',
    apply_unit_scale=True,bake_space_transform=False,mesh_smooth_type='FACE',use_tspace=True,path_mode='STRIP')
bpy.ops.object.select_all(action='DESELECT');root.select_set(True)
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='VIEW_3D':
            space=area.spaces.active;space.shading.type='MATERIAL'
            space.region_3d.view_rotation=camera.rotation_euler.to_quaternion()
            space.region_3d.view_location=Vector((0,0,0));space.region_3d.view_distance=4
            space.region_3d.view_perspective='ORTHO'
manifest={'source':str(SOURCE),'reviewBlend':str(blend),'reviewFbx':str(fbx),
          'reviewStatus':'待用户人工验收；未替换Unity','squareFrames':records,
          'frameMaterial':{'name':copper.name,'uniformAllFaces':True,'fixedWhiteBands':False,
              'baseColorLinear':list(principled.inputs['Base Color'].default_value),
              'metallic':.78,'roughness':.27,'emissionStrength':0},
          'sharedCenter':list(root['共同中心']),'sharedAxis':list(root['共同轴方向'])}
(OUT/'square_review_manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
bpy.ops.wm.save_as_mainfile(filepath=str(blend),compress=True)
print('SQUARE_GEOMETRY_SAVED',json.dumps(records,ensure_ascii=False),flush=True)

def render(label,pos,scale,samples=40):
    (OUT/'review_progress.json').write_text(json.dumps({'stage':'渲染'+label},ensure_ascii=False),encoding='utf-8')
    camera.location=pos;camera.rotation_euler=(-camera.location).to_track_quat('-Z','Y').to_euler()
    camera.data.ortho_scale=scale;scene.cycles.samples=samples
    scene.render.filepath=str(OUT/('双极纳米片_正方形审阅_'+label+'.png'))
    bpy.ops.render.render(write_still=True)

render('参考角度',camera_location,3.12)
axis=Vector(root['共同轴方向']).normalized()
render('同轴侧视',axis*6,3.10,28)
# 从主外框平面法线正视整个模型，展示真实等边的主体框。
normal=(frames[0].matrix_world.to_3x3().normalized()@Vector((0,0,1))).normalized()
render('主框正视',normal*6,3.00,28)
# 单独显示正方形主框，其他部件在这张辅助验收图中临时隐藏。
hidden={o:o.hide_render for o in root.children_recursive if o.type=='MESH'}
for obj in hidden:obj.hide_render=(obj!=frames[0])
render('单框正视',normal*6,2.35,28)
for obj,value in hidden.items():obj.hide_render=value
camera.location=camera_location;camera.rotation_euler=camera_rotation;camera.data.ortho_scale=camera_scale
scene.cycles.samples=40
bpy.ops.wm.save_as_mainfile(filepath=str(blend),compress=True)
(OUT/'review_progress.json').write_text(json.dumps({'stage':'审阅版与四视图完成','requiresManualAcceptance':True,'unityReplaced':False},ensure_ascii=False),encoding='utf-8')
print('SQUARE_REVIEW_COMPLETE',str(blend),flush=True)
