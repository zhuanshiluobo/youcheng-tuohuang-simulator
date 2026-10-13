import bpy, json, math
from pathlib import Path

out=Path(__file__).resolve().parent
base=out.parent
reports=[]
for relative in ('UniformMaterialRevision/OrirockConcentration_UniformStone.blend','ContactFixRevision/OrirockConcentration_ContactFixed.blend'):
    bpy.ops.wm.open_mainfile(filepath=str(base/relative))
    scene=bpy.context.scene
    report=dict(file=bpy.data.filepath,camera=[list(row) for row in scene.camera.matrix_world],meshes=[])
    for obj in scene.objects:
        if obj.type!='MESH': continue
        faces=[dict(area=p.area,normal=list(p.normal),center=list(p.center),vertices=[list(obj.data.vertices[i].co) for i in p.vertices]) for p in obj.data.polygons]
        sides=sorted([p for p in faces if abs(p['normal'][2])<.001],key=lambda p:p['area'],reverse=True)
        zmin=min(v.co.z for v in obj.data.vertices);zmax=max(v.co.z for v in obj.data.vertices)
        bottoms=[p for p in faces if all(abs(v[2]-zmin)<1e-6 for v in p['vertices'])]
        tops=[p for p in faces if all(abs(v[2]-zmax)<1e-6 for v in p['vertices'])]
        report['meshes'].append(dict(name=obj.name,matrix=[list(r) for r in obj.matrix_world],z=[zmin,zmax],sides=sides[:16],bottoms=bottoms,tops=tops))
    reports.append(report)
(out/'alignment_inspection.json').write_text(json.dumps(reports,ensure_ascii=False,indent=2),encoding='utf-8')
for report in reports:
    print(Path(report['file']).name)
    for obj in report['meshes']:
        print(obj['name'],obj['z'],[(round(p['area'],4),round(math.degrees(math.atan2(p['normal'][1],p['normal'][0]))%60,4)) for p in obj['sides'][:8]])
