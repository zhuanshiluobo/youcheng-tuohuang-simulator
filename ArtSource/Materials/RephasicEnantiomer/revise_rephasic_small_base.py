"""仅缩小第五版四层承台；完整保留光学壳、内镜片和原显示朝向。"""
import bpy, json, math, shutil, hashlib
from pathlib import Path
from mathutils import Vector

ROOT = Path('F:/游城拓荒模拟器')
KEY = 'RephasicEnantiomer'
ART = ROOT/'ArtSource/Materials'/KEY
GAME = ROOT/'Assets/YC/Presentation/Models'/KEY
REVIEW = ART/'SmallBaseRevision'
REVIEW.mkdir(exist_ok=True)
bpy.context.preferences.filepaths.save_version = 0
for src, dst in [
    (ART/(KEY+'_Finished.blend'), ART/(KEY+'_BeforeSmallBaseRevision.blend')),
    (GAME/(KEY+'_Finished.fbx'), ART/(KEY+'_BeforeSmallBaseRevision.fbx')),
    (GAME/'material_manifest.json', ART/'material_manifest_BeforeSmallBaseRevision.json')]:
    if not dst.exists(): shutil.copy2(src, dst)

shader_digest = hashlib.sha256((GAME/'RephasicOpticalPyramid.shader').read_bytes()).hexdigest()
scene = bpy.data.scenes[KEY+'_Finished']
bpy.context.window.scene = scene
root = next(o for o in scene.objects if o.type=='EMPTY' and o.name==KEY)
print('SOURCE_INSPECT', bpy.app.version_string, bpy.data.filepath,
      [(s.name, len(s.objects)) for s in bpy.data.scenes])
base_names = {'同心圆形深色底座': .715, '同心金色薄夹层': .720,
              '同心银色承托边': .715, '锥底共面接触托板': .705}
changed = []
current_blockout = next((s for s in bpy.data.scenes if s.name.startswith(KEY+'_OpticalBlockout') and len(s.objects)==78), None)
for source_scene in [scene, current_blockout]:
    if source_scene is None: continue
    for obj in source_scene.objects:
        match = next((name for name in base_names if obj.name==name or obj.name.startswith(name+'.')), None)
        if match is None: continue
        previous_radius = max(math.hypot(v.co.x, v.co.y) for v in obj.data.vertices)
        scale = base_names[match]/previous_radius
        for v in obj.data.vertices: v.co.x *= scale; v.co.y *= scale
        obj.data.update()
        obj['说明'] = '中心 xy=(0,0)；小型低薄金属承台，三角底顶角自然伸出；最上托面 z=.24，与锥底接触。'
        changed.append([source_scene.name, obj.name, previous_radius, base_names[match]])

meshes = [o for o in scene.objects if o.type=='MESH' and o.parent==root]
shell = next(o for o in meshes if any(m.name=='RE5_OpticalGlassShell' for m in o.data.materials))
assert len(meshes)==77
assert len([o for o in meshes if o.name.startswith('内部有限反射镜片_')])==72
bpy.context.view_layer.update()
corners = [o.matrix_world@Vector(p) for o in meshes for p in o.bound_box]
lo = [min(p[i] for p in corners) for i in range(3)]
hi = [max(p[i] for p in corners) for i in range(3)]
manifest = json.loads((GAME/'material_manifest.json').read_text(encoding='utf-8'))
manifest['bounds'] = {'min':lo, 'max':hi, 'size':[hi[i]-lo[i] for i in range(3)]}
manifest['revision'] = '第五版小底座修订：同心小型低薄承台，允许三角底顶角伸出；72 个分层内部实体镜片、十跳光学机制及原相机均保留。'
manifest['support_alignment'] = {
    'pyramid_base_center':[0,0,.24], 'support_center_xy':[0,0], 'support_top_z':.24,
    'support_contact_radius':.705, 'support_outer_radius':.720, 'pyramid_circumradius':1.109,
    'contact':'中心承托，三个底角超出托板；底面和最上托面 z=.24 共面，无偏心、无悬浮。'}
cam = scene.camera
right = cam.rotation_euler.to_quaternion()@Vector((1,0,0))
projected_shell = [right.dot(o.co) for o in shell.data.vertices]
span = max(projected_shell)-min(projected_shell)
base_span = 2*.720*math.sqrt(right.x**2+right.y**2)
manifest['support_alignment']['reference_projected_width_ratio'] = base_span/span
(GAME/'material_manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
bpy.ops.object.select_all(action='DESELECT')
root.select_set(True)
for obj in meshes: obj.select_set(True)
bpy.context.view_layer.objects.active = root
bpy.ops.export_scene.fbx(filepath=str(GAME/(KEY+'_Finished.fbx')), use_selection=True,
    object_types={'EMPTY','MESH'}, apply_unit_scale=True, apply_scale_options='FBX_SCALE_UNITS',
    axis_forward='-Z', axis_up='Y', use_mesh_modifiers=True, mesh_smooth_type='FACE',
    use_tspace=False, add_leaf_bones=False, bake_anim=False, path_mode='AUTO')
bpy.ops.wm.save_as_mainfile(filepath=str(ART/(KEY+'_Finished.blend')))

# 白模只临时降低棚拍能量；保持正式材质灯光、相机及已有阶段原样。
target = Vector(manifest['reference_camera']['target'])
views = {'Front': tuple(manifest['reference_camera']['position']),
         'Side': (6.5,0,3.0), 'Back': (0,6.5,3.4)}
original_camera = cam.location.copy(), cam.rotation_euler.copy()
original_renderpath = scene.render.filepath
original_samples = scene.cycles.samples
white = bpy.data.materials.new('RE5_SmallBaseReviewWhite')
white.use_nodes = True
p = next(n for n in white.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
p.inputs['Base Color'].default_value = (.3,.3,.3,1)
p.inputs['Roughness'].default_value = .65
light_energy = [(o.data,o.data.energy) for o in scene.objects if o.type=='LIGHT']
scene.cycles.samples = 40
for data, energy in light_energy: data.energy = energy*.15
scene.view_layers[0].material_override = white
for view, loc in views.items():
    cam.location = loc
    cam.rotation_euler = (target-cam.location).to_track_quat('-Z','Y').to_euler()
    scene.render.filepath = str(REVIEW/(view+'_Blockout.png'))
    bpy.ops.render.render(write_still=True)
scene.view_layers[0].material_override = None
for data, energy in light_energy: data.energy = energy
cam.location, cam.rotation_euler = original_camera
scene.render.filepath = str(REVIEW/'ReferenceFront.png')
scene.cycles.samples = 64
bpy.ops.render.render(write_still=True)
scene.render.filepath = original_renderpath
scene.cycles.samples = original_samples
bpy.ops.wm.save_as_mainfile(filepath=str(ART/(KEY+'_Finished.blend')))
assert shader_digest==hashlib.sha256((GAME/'RephasicOpticalPyramid.shader').read_bytes()).hexdigest()
print('SMALL_BASE_READY', json.dumps({'changed':changed, 'meshes':len(meshes),
    'bounds':manifest['bounds'], 'alignment':manifest['support_alignment'],
    'camera':manifest['reference_camera'], 'shader_unchanged':True}, ensure_ascii=False))
