"""导出已验收的材质版本；不占用当前 Blender 文件。"""
import bpy,json,shutil
from pathlib import Path
from mathutils import Vector
ART=Path('F:/游城拓荒模拟器/ArtSource/Materials/PolymerizationPreparation')
SRC=ART/'GlassEmissionReview'
GAME=Path('F:/游城拓荒模拟器/Assets/YC/Presentation/Models/PolymerizationPreparation')
BACK=ART/'BeforeApprovedImport';BACK.mkdir(exist_ok=True)
for source in [GAME/'PolymerizationPreparation_Finished.fbx',GAME/'material_manifest.json',GAME.parent.parent/'Collection/Prefabs/PolymerizationPreparationDisplay.prefab',*list((GAME/'Materials').glob('*.mat'))]:
    dest=BACK/source.name
    if source.exists() and not dest.exists():shutil.copy2(source,dest)
bpy.ops.wm.open_mainfile(filepath=str(SRC/'PolymerizationPreparation_GlassEmission.blend'))
s=bpy.context.scene;root=s.objects['PolymerizationPreparation_Root']
meshes=[o for o in s.objects if o.type=='MESH' and o.parent==root]
bpy.ops.object.select_all(action='DESELECT')
for o in [root]+meshes:o.select_set(True)
bpy.context.view_layer.objects.active=root
bpy.ops.export_scene.fbx(filepath=str(SRC/'PolymerizationPreparation_Approved.fbx'),use_selection=True,object_types={'EMPTY','MESH'},use_mesh_modifiers=True,add_leaf_bones=False,axis_forward='-Z',axis_up='Y',apply_unit_scale=True,bake_space_transform=False,mesh_smooth_type='FACE',path_mode='AUTO')
materials={}
for mat in {mat for o in meshes for mat in o.data.materials if mat}:
    p=next(n for n in mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
    materials[mat.name]={'unityMaterialName':mat.name.replace('_玻璃发光验收',''),
      'colorLinear':list(p.inputs['Base Color'].default_value),'metallic':p.inputs['Metallic'].default_value,
      'smoothness':1-p.inputs['Roughness'].default_value,
      'emissionLinear':list(p.inputs['Emission Color'].default_value),
      'emissionStrength':p.inputs['Emission Strength'].default_value,
      'glass':'青蓝透光盖板' in mat.name}
points=[o.matrix_world@Vector(v) for o in meshes for v in o.bound_box]
lo=[min(p[i] for p in points) for i in range(3)];hi=[max(p[i] for p in points) for i in range(3)]
manifest={'model':'PolymerizationPreparation','editableSource':str(SRC/'PolymerizationPreparation_GlassEmission.blend'),
          'materials':materials,'meshCount':len(meshes),'bounds':{'min':lo,'max':hi,'size':[hi[i]-lo[i] for i in range(3)]},
          'geometryReview':json.loads((ART/'AlignedPivotReview/alignment_review.json').read_text(encoding='utf8')),
          'materialReview':json.loads((SRC/'material_review.json').read_text(encoding='utf8')),
          'description':'已验收：六个独立底夹，等长加长圆头管，0/30/65度夹持姿态，加高盒体与加厚侧框，玻璃与青色液体自发光。'}
(SRC/'approved_manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf8')
print('APPROVED_EXPORT_DONE',len(meshes),len(materials),flush=True)
