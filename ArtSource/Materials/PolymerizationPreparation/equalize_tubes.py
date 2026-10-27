"""测量完整管体轴向长度，以前管为基准，只调整直管段，保留两端圆头。"""
import bpy, json
from pathlib import Path
from mathutils import Vector
ART=Path('F:/游城拓荒模拟器/ArtSource/Materials/PolymerizationPreparation')
SRC=ART/'BoxRoundHeadReview'
OUT=ART/'EqualLengthReview'
OUT.mkdir(exist_ok=True)
bpy.ops.wm.open_mainfile(filepath=str(SRC/'PolymerizationPreparation_BoxRoundHead.blend'))
scene=bpy.context.scene;root=scene.objects['PolymerizationPreparation_Root']
names=['前侧横置管','中部斜置管','后部斜立管']
def measure(name):
    head=scene.objects[name+'_圆弧封头']
    h=[head.matrix_world@v.co for v in head.data.vertices]
    ring0=sum(h[:48],Vector())/48;ring1=sum(h[48:96],Vector())/48
    axis=(ring0-ring1).normalized()
    objects=[o for o in scene.objects if o.type=='MESH' and o.name.startswith(name+'_')]
    projections=[(o.matrix_world@v.co).dot(axis) for o in objects for v in o.data.vertices]
    return objects,axis,min(projections),max(projections),ring0.dot(axis)
before={}
for name in names:
    _,_,lo,hi,_=measure(name);before[name]=hi-lo
print('BEFORE',json.dumps(before,ensure_ascii=False),flush=True)
target=before[names[0]]
for name in names:
    objects,axis,lo,hi,head_seam=measure(name)
    if abs(hi-lo-target)<1e-6:continue
    head=scene.objects[name+'_圆弧封头']
    verts=[head.matrix_world@v.co for v in head.data.vertices[:48]]
    ringcenter=sum(verts,Vector())/48
    radius=(verts[0]-ringcenter).length
    bottom_seam=hi-radius
    delta=(hi-lo)-target
    stretch=(bottom_seam-head_seam-delta)/(bottom_seam-head_seam)
    for ob in objects:
        mat=ob.matrix_world.copy();inv=mat.inverted();ob.data=ob.data.copy()
        for v in ob.data.vertices:
            p=mat@v.co;t=p.dot(axis)
            newt=t if t>=bottom_seam else t+delta if t<=head_seam else bottom_seam+(t-bottom_seam)*stretch
            v.co=inv@(p+axis*(newt-t))
        ob.data.update()
bpy.context.view_layer.update()
after={name:measure(name)[3]-measure(name)[2] for name in names}
print('AFTER',json.dumps(after,ensure_ascii=False),flush=True)
scene['长度核对']='按管轴测量，含两端圆头；三根均以前侧管长度为基准，底端位置、倾斜角度、管径、圆头及原材质不变。'
scene['三管长度']=json.dumps(after,ensure_ascii=False)
scene.render.threads_mode='FIXED';scene.render.threads=6;scene.cycles.samples=32
scene.render.filepath=str(OUT/'PolymerizationPreparation_EqualLength.png')
meshes=[o for o in scene.objects if o.type=='MESH' and o.parent==root]
bpy.ops.object.select_all(action='DESELECT')
for o in [root]+meshes:o.select_set(True)
bpy.context.view_layer.objects.active=root
# 保存进入 Blender 后直接可看的材质视图，不把摄影灯图标盖在模型上。
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='VIEW_3D':
            space=area.spaces.active;space.shading.type='MATERIAL';space.overlay.show_overlays=False
            space.region_3d.view_perspective='CAMERA'
bpy.ops.export_scene.fbx(filepath=str(OUT/'PolymerizationPreparation_Review.fbx'),use_selection=True,object_types={'EMPTY','MESH'},use_mesh_modifiers=True,add_leaf_bones=False,axis_forward='-Z',axis_up='Y',apply_unit_scale=True,bake_space_transform=False,mesh_smooth_type='FACE',path_mode='AUTO')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'PolymerizationPreparation_EqualLength.blend'),compress=True)
(OUT/'length_review.json').write_text(json.dumps({'before':before,'after':after,'unit':'m','measurement':'包含两端圆头的轴向长度','unityImported':False},ensure_ascii=False,indent=2),encoding='utf8')
bpy.ops.render.render(write_still=True)
print('EQUAL_LENGTH_DONE',flush=True)
