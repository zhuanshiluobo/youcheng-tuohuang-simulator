"""仅补顶盖左侧实体斜撑，保留验收版本全部原有分件和相机。"""
import bpy,bmesh,json
from pathlib import Path
from mathutils import Vector
BASE=Path('F:/游城拓荒模拟器/ArtSource/Materials/NucleicCrystalSinter')
OUT=BASE/'LeftSupportRevision'
bpy.ops.wm.open_mainfile(filepath=str(BASE/'ReferenceViewMatch/NucleicCrystalSinter_SameView.blend'))
bpy.context.preferences.filepaths.save_version=0
s=bpy.context.scene
root=next(o for o in s.objects if o.type=='EMPTY' and o.parent is None)
lid=next(o for o in s.objects if o.name.startswith('五层内收方形凹阶'))
# 外表面保持在顶盖左侧平面内；顶边埋入盖沿，前下端接入左侧晶簇。
outer=[(-.589,.552,2.285),(-.589,-.435,2.20),(-.589,-.35,1.895),(-.589,.24,1.775)]
verts=outer+[(x+.045,y,z) for x,y,z in outer]
faces=[(0,1,2,3),(7,6,5,4),(0,4,5,1),(1,5,6,2),(2,6,7,3),(3,7,4,0)]
mesh=bpy.data.meshes.new('左侧宽斜撑板_闭合实体')
mesh.from_pydata(verts,[],faces);mesh.update()
bm=bmesh.new();bm.from_mesh(mesh)
bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(mesh);bm.free()
mesh.materials.append(lid.data.materials[0])
o=bpy.data.objects.new('顶盖左侧宽斜撑板',mesh);s.collection.objects.link(o);o.parent=root
bevel=o.modifiers.new('斜撑窄倒角','BEVEL');bevel.width=.003;bevel.segments=1
n=o.modifiers.new('侧撑硬面法线','WEIGHTED_NORMAL');n.keep_sharp=True
s['制作状态']='已补齐顶盖左侧实体斜撑板；原验收相机与其余结构保持不变。'
s.render.threads_mode='FIXED';s.render.threads=6
s.render.filepath=str(OUT/'LeftSupport_SameView.png')
bpy.context.view_layer.update()
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'NucleicCrystalSinter_LeftSupport.blend'),compress=True)
bpy.ops.render.render(write_still=True)
c=s.camera;c.data.shift_x=0;c.data.shift_y=0;c.data.lens=65
c.location=(-6.8,-.7,3.5);c.rotation_euler=(Vector((0,0,1.25))-c.location).to_track_quat('-Z','Y').to_euler()
s.render.resolution_x=600;s.render.resolution_y=700;s.cycles.samples=24
s.render.filepath=str(OUT/'LeftSupport_Side.png');bpy.ops.render.render(write_still=True)
print('LEFT_SUPPORT_COMPLETE',len([o for o in s.objects if o.type=='MESH']))
