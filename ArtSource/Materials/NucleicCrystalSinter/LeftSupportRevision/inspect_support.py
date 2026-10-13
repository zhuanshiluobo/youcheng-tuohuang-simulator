import bpy,json
from mathutils import Vector
from bpy_extras.object_utils import world_to_camera_view
from pathlib import Path
base=Path('F:/游城拓荒模拟器/ArtSource/Materials/NucleicCrystalSinter')
bpy.ops.wm.open_mainfile(filepath=str(base/'ReferenceViewMatch/NucleicCrystalSinter_SameView.blend'))
s=bpy.context.scene;c=s.camera
bpy.context.view_layer.update()
print('SOURCE',bpy.app.version_string,s.name)
for o in s.objects:
    if o.type=='MESH' and (o.name.startswith('五层') or o.name.startswith('左侧分叉')):
        print('PART',o.name,list(o.location),list(o.dimensions),[m.name for m in o.data.materials])
p=c.calc_matrix_camera(bpy.context.evaluated_depsgraph_get(),x=760,y=836).inverted()
for x,y in [(73,49),(77,66),(87,143),(107,157),(116,120)]:
    v=p@Vector((x/380*2-1,1-y/418*2,-1,1));v/=v.w
    start=c.matrix_world.translation;direction=c.matrix_world@Vector(v[:3])-start
    q=start+direction*((-.592935-start.x)/direction.x)
    print('POINT',x,y,list(q))
