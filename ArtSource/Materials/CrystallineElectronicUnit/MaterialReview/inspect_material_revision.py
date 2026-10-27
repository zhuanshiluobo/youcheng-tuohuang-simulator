"""只读检查已保存模型的散热槽及凹槽坐标；不保存、不修改网格。"""
import bpy,json
from pathlib import Path
from mathutils import Vector
from mathutils.bvhtree import BVHTree
OUT=Path(__file__).resolve().parent
bpy.ops.wm.open_mainfile(filepath=str(OUT/'CrystallineElectronicUnit_MaterialReview.blend'))
for s in bpy.data.scenes:s.view_layers[0].update()
scene=bpy.data.scenes['晶体电子单元_材质验收']
by_source={o.get('白模对象'):o for o in scene.objects if o.get('白模对象')}
body=by_source['上下等边_直八棱柱箱体']
tree=BVHTree.FromPolygons([body.matrix_world@v.co for v in body.data.vertices],[tuple(p.vertices) for p in body.data.polygons])
results={}
for i in (0,3,5):
    frame=by_source[f'侧面_{i:02d}_局部基准']
    rows=[]
    for z in (.3815,.343):
        origin=frame.matrix_world@Vector((0,-.2,z))
        direction=frame.matrix_world.to_3x3()@Vector((0,1,0))
        hit=tree.ray_cast(origin,direction,.5)
        rows.append(None if hit[0] is None else (frame.matrix_world.inverted()@hit[0]).y)
    results[str(i)]=rows
print('READ_ONLY_SLOT_DEPTHS',json.dumps(results))
print('BODY_COUNTS',len(body.data.vertices),len(body.data.polygons))
for s in bpy.data.scenes:
    floor=next(o for o in s.objects if o.get('白模对象',o.name)=='中央凹口封闭槽底')
    top=next(o for o in s.objects if o.get('白模对象',o.name)=='顶部白模_整体')
    print('BASIN',s.name,'floor_location',tuple(floor.location),'floor_mesh_top',max(v.co.z for v in floor.data.vertices),
          'floor_world_top',max((floor.matrix_world@v.co).z for v in floor.data.vertices),'top_root',tuple(top.location))
print('READ_ONLY_COMPLETE_NO_SAVE')
