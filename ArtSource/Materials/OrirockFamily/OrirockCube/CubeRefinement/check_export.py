import bpy,bmesh,json
from pathlib import Path

OUT=Path(__file__).resolve().parent
PROJECT=OUT.parents[4]
files=[OUT/'PreviousAssets/OrirockCube_Finished.fbx',PROJECT/'Assets/YC/Presentation/Models/OrirockFamily/OrirockCube/OrirockCube_Finished.fbx']
result=[]
for path in files:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene=bpy.data.scenes.new(path.stem); bpy.context.window.scene=scene
    bpy.ops.import_scene.fbx(filepath=str(path))
    row={'path':str(path),'objects':[]}
    for ob in scene.objects:
        info={'name':ob.name,'type':ob.type,'scale':list(ob.scale)}
        if ob.type=='MESH':
            info['mesh_name']=ob.data.name; info['size']=list(ob.dimensions)
            bm=bmesh.new(); bm.from_mesh(ob.data)
            info['non_manifold']=sum(not e.is_manifold for e in bm.edges); bm.free()
            ob.data.calc_loop_triangles(); info['triangles']=len(ob.data.loop_triangles)
        row['objects'].append(info)
    result.append(row)
print(json.dumps(result,ensure_ascii=False),flush=True)
(OUT/'fbx_check.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
