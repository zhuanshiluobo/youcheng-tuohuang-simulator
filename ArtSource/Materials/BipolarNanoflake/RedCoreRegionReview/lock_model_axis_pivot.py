"""把已经共轴的分件挂到同一 X 轴枢轴，保留所有世界空间几何。"""
import bpy, math, json
from pathlib import Path
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

scene=bpy.context.scene
root=scene.objects['BipolarNanoflake_FixedView_Root']
meshes=[o for o in root.children_recursive if o.type=='MESH']
frames=[o for o in meshes if o.name.startswith(('完整铜红','最大框_'))]
first=next(o for o in frames if o.name.startswith('完整铜红长方形外框_1_'))
before={o:o.matrix_world.copy() for o in meshes}
basis=(root.matrix_world.inverted()@first.matrix_world).to_3x3()@Matrix.Rotation(-math.radians(first['绕共同轴角度']),3,'X')
pivot=bpy.data.objects.new('共用X轴_固定枢轴',None)
scene.collection.objects.link(pivot)
pivot.parent=root;pivot.rotation_euler=basis.to_euler()
pivot.empty_display_type='ARROWS';pivot.empty_display_size=.25
pivot.lock_rotation=(True,True,True);root.lock_rotation=(True,True,True)
bpy.context.view_layer.update()
for obj in meshes:
    obj.parent=pivot;obj.matrix_parent_inverse=Matrix.Identity(4)
    obj.matrix_basis=pivot.matrix_world.inverted()@before[obj]
    if obj in frames:
        obj.rotation_mode='XYZ'
        obj.rotation_euler=(math.radians(obj['绕共同轴角度']),0,0)
        obj.lock_rotation=(False,True,True)
        obj.lock_location=(False,True,True)
bpy.context.view_layer.update()
axis=pivot.matrix_world.to_3x3().col[0].normalized()
root['共同轴方向']=list(axis);pivot['共同轴方向']=list(axis)
pivot['旋转规则']='各方框只开放局部X旋转；Y、Z旋转锁定。'
delta=max((obj.matrix_world@v.co-before[obj]@v.co).length for obj in meshes for v in obj.data.vertices)
depsgraph=bpy.context.evaluated_depsgraph_get();trees={}
for obj in meshes:
    ev=obj.evaluated_get(depsgraph);mesh=ev.to_mesh()
    trees[obj]=BVHTree.FromPolygons([ev.matrix_world@v.co for v in mesh.vertices],[tuple(p.vertices) for p in mesh.polygons],epsilon=.000001)
    ev.to_mesh_clear()
intersections=[{'a':a.name,'b':b.name,'triangleCrossings':len(trees[a].overlap(trees[b]))} for i,a in enumerate(meshes) for b in meshes[i+1:]]
folder=Path(bpy.data.filepath).parent
manifest_path=folder/'fixed_view_review_manifest.json'
manifest=json.loads(manifest_path.read_text(encoding='utf-8'))
manifest['axisPivot']=pivot.name;manifest['sharedAxis']=list(axis)
manifest['worldGeometryChangeOnReparent']=delta
manifest['frameRotationControls']=[{'object':o.name,'localEulerDegrees':[math.degrees(v) for v in o.rotation_euler],'rotationLocked':list(o.lock_rotation),'axisError':((o.matrix_world.to_3x3().col[0]).normalized()-axis).length} for o in frames]
manifest['intersections']=intersections
manifest_path.write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
for obj in scene.objects:obj.select_set(False)
for obj in [root,pivot]+meshes:obj.select_set(True)
bpy.context.view_layer.objects.active=root
bpy.ops.export_scene.fbx(filepath=manifest['fbx'],use_selection=True,object_types={'EMPTY','MESH'},use_mesh_modifiers=True,add_leaf_bones=False,axis_forward='-Z',axis_up='Y',apply_unit_scale=True,bake_space_transform=False,mesh_smooth_type='FACE',path_mode='STRIP')
for obj in meshes+[pivot]:obj.select_set(False)
bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath,compress=True)
print(json.dumps({'pivot':pivot.name,'worldGeometryChange':delta,'crossings':sum(x['triangleCrossings'] for x in intersections),'frames':manifest['frameRotationControls']},ensure_ascii=False))
