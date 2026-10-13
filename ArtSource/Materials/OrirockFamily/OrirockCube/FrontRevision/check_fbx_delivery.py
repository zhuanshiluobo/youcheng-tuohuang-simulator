"""轻量查看实际导出的FBX对象、单位和纹理引用；不调用Unity。"""
import bpy, json
from pathlib import Path
from mathutils import Vector
OUT=Path(__file__).resolve().parent
for obj in list(bpy.context.scene.objects):
    bpy.data.objects.remove(obj,do_unlink=True)
bpy.ops.import_scene.fbx(filepath=str(OUT/'OrirockCube_FrontRevision.fbx'))
meshes=[o for o in bpy.context.scene.objects if o.type=='MESH']
points=[o.matrix_world @ v.co for o in meshes for v in o.data.vertices]
lo=Vector([min(p[i] for p in points) for i in range(3)])
hi=Vector([max(p[i] for p in points) for i in range(3)])
report={'mesh_count':len(meshes),'object_types':[o.type for o in bpy.context.scene.objects],
        'bounds_size':list(hi-lo),'triangle_count':0,'image_references':[]}
for obj in meshes:
    obj.data.calc_loop_triangles()
    report['triangle_count']+=len(obj.data.loop_triangles)
    for mat in obj.data.materials:
        if mat and mat.node_tree:
            for node in mat.node_tree.nodes:
                if node.type=='TEX_IMAGE' and node.image:
                    report['image_references'].append(node.image.filepath)
(OUT/'fbx_delivery_check.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(report,ensure_ascii=False),flush=True)
