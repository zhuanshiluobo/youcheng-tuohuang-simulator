import bpy
from pathlib import Path
from mathutils import Vector
OUT=Path(__file__).resolve().parent
bpy.ops.wm.open_mainfile(filepath=str(OUT.parent/'FrontRevision/OrirockCube_FrontRevision.blend'))
scene=bpy.data.scenes['固源岩_正面修订_程序源']; bpy.context.window.scene=scene
with bpy.data.libraries.load(str(OUT.parent/'TopTiltOnlyReview/OrirockCube_TopTiltOnlyReview.blend'),link=False) as (available,loaded):
    loaded.materials=[n for n in available.materials if n.startswith('固源岩_统一暖灰岩质')][:1]
mat=loaded.materials[0]
root=next(o for o in scene.objects if o.type=='EMPTY')
for n in mat.node_tree.nodes:
    if n.type=='TEX_COORD': n.object=root
for ob in scene.objects:
    if ob.type=='MESH':
        ob.data=ob.data.copy(); ob.data.materials.clear(); ob.data.materials.append(mat)
        for p in ob.data.polygons: p.material_index=0
scene.render.resolution_x=scene.render.resolution_y=800; scene.cycles.samples=24
scene.render.threads_mode='FIXED'; scene.render.threads=10
cam=scene.camera; saved=(cam.location.copy(),cam.rotation_euler.copy())
for name,pos in [('source_front',saved[0]),('source_right',(6,0,0)),('source_rear',(-3,6,3.2))]:
    cam.location=pos; cam.rotation_euler=(-cam.location).to_track_quat('-Z','Y').to_euler()
    scene.render.filepath=str(OUT/(name+'.png')); print(name,flush=True); bpy.ops.render.render(write_still=True)
