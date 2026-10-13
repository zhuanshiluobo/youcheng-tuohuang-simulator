"""独立后台：去掉空白启动场景并回读本材料 FBX，不调用 Unity。"""
import bpy, json, bmesh
from pathlib import Path
from mathutils import Vector

out=Path(__file__).resolve().parent
blend=out/'OrirockConcentration_FrontRevision.blend'
bpy.ops.wm.open_mainfile(filepath=str(blend))
source=next(s for s in bpy.data.scenes if s.name=='提纯源岩_正面修订_可编辑源')
bpy.context.window.scene=source
# Explicit names refer only to the factory-startup defaults in this new file.
default=bpy.data.scenes.get('Scene')
if default and default!=source:
    for name in ('Cube','Camera','Light'):
        obj=bpy.data.objects.get(name)
        if obj and obj.name in default.objects:
            data=obj.data
            bpy.data.objects.remove(obj,do_unlink=True)
            if data and data.users==0:
                if name=='Cube': bpy.data.meshes.remove(data)
                elif name=='Camera': bpy.data.cameras.remove(data)
                elif name=='Light': bpy.data.lights.remove(data)
    bpy.data.scenes.remove(default)
bpy.context.preferences.filepaths.save_version=0
bpy.ops.wm.save_as_mainfile(filepath=str(blend))
saved=dict(scene=source.name,scenes=[s.name for s in bpy.data.scenes],
           source_meshes=[o.name for o in source.objects if o.type=='MESH'],
           source_material_graphs=sum(m.use_nodes for m in bpy.data.materials if '游戏PBR' not in m.name))
# Import into a separate empty scene only for a small export-content/bounds check.
check=bpy.data.scenes.new('仅回读检查_不保存')
bpy.context.window.scene=check
bpy.ops.import_scene.fbx(filepath=str(out/'OrirockConcentration_FrontRevision.fbx'))
rows=[]
points=[]
for obj in check.objects:
    row=dict(name=obj.name,type=obj.type,location=list(obj.location),scale=list(obj.scale))
    if obj.type=='MESH':
        obj.data.calc_loop_triangles()
        bm=bmesh.new(); bm.from_mesh(obj.data)
        row.update(triangles=len(obj.data.loop_triangles),uv_layers=len(obj.data.uv_layers),
                   non_manifold_edges=sum(not e.is_manifold for e in bm.edges),material_slots=len(obj.data.materials))
        bm.free()
        points.extend(obj.matrix_world@v.co for v in obj.data.vertices)
    rows.append(row)
saved.update(fbx_objects=rows,fbx_mesh_count=sum(r['type']=='MESH' for r in rows),
             fbx_triangle_count=sum(r.get('triangles',0) for r in rows),
             fbx_preview_equipment_count=sum(r['type'] in ('CAMERA','LIGHT') for r in rows),
             fbx_world_bounds_size=[max(p[i] for p in points)-min(p[i] for p in points) for i in range(3)])
(out/'delivery_inspection.json').write_text(json.dumps(saved,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(saved,ensure_ascii=False))
