"""左后石柱向下嵌合；形成一个连续外壳，保留游戏既有三个网格引用。"""
import bpy,bmesh,json
from pathlib import Path
from mathutils import Vector,Quaternion

OUT=Path(__file__).resolve().parent
KEY='OrirockConcentration'
SOURCE=OUT.parent/'ViewSizeRevision'/(KEY+'_ViewSizeReview.blend')
bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
bpy.context.preferences.filepaths.save_version=0
scene=bpy.context.scene
parts=[o for o in scene.objects if o.type=='MESH']
parts.sort(key=lambda o:0 if '左后' in o.name else (1 if '右后' in o.name else 2))
left,right,front=parts
stone=left.data.materials[0]
names=[o.data.name for o in parts]
chips={o.name:o.get('崩口数量',0) for o in parts}
old_bottom=min(v.co.z for v in left.data.vertices)
bottom=min(v.co.z for obj in (right,front) for v in obj.data.vertices)
old_top=max(v.co.z for v in left.data.vertices)
fixed_top={v.index:tuple(v.co) for v in left.data.vertices if v.co.z>old_bottom+.015}
# 只下移底部倒角环；顶部、六个角与所有顶缘崩口坐标不动。
for v in left.data.vertices:
    if v.co.z<old_bottom+.015:v.co.z+=bottom-old_bottom
left.data.update()
assert all(tuple(left.data.vertices[i].co)==co for i,co in fixed_top.items())

# 临时面标签随布尔保留，最后仍用同一个材质。联合后去掉内部穿插面，
# 不是以柱底高低错开来掩盖显示条纹。
labels=[stone.copy() for _ in parts]
for i,(obj,mat) in enumerate(zip(parts,labels)):
    mat.name='嵌合处理_临时分件标记_'+str(i)
    obj.data.materials.clear()
    for tag in labels:obj.data.materials.append(tag)
    for p in obj.data.polygons:p.material_index=i
bpy.context.view_layer.objects.active=left
for other in (right,front):
    mod=left.modifiers.new('移除内部重叠面_连续岩体','BOOLEAN')
    mod.operation='UNION';mod.solver='EXACT';mod.object=other
    bpy.ops.object.modifier_apply(modifier=mod.name)
bm=bmesh.new();bm.from_mesh(left.data)
bmesh.ops.remove_doubles(bm,verts=list(bm.verts),dist=.000001)
bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
bmesh.ops.triangulate(bm,faces=list(bm.faces))
non_manifold=sum(not e.is_manifold for e in bm.edges)
todo=set(bm.verts);components=0
while todo:
    components+=1;stack=[todo.pop()]
    while stack:
        v=stack.pop()
        for e in v.link_edges:
            n=e.other_vert(v)
            if n in todo:todo.remove(n);stack.append(n)
volume=bm.calc_volume(signed=True)
assert non_manifold==0,(non_manifold,components)
assert components==1,components
assert volume>0,volume
bm.to_mesh(left.data);bm.free();left.data.update()
whole=left.data.copy();whole.name='提纯源岩_完整闭合嵌合外壳';whole.use_fake_user=True
counts={i:sum(p.material_index==i for p in whole.polygons) for i in range(3)}
assert all(counts.values()),counts

# 三个可见表面分件合在一起就是完整外壳；保留名称防止原 Unity 引用断开。
for i,obj in enumerate(parts):
    polygons=[p for p in whole.polygons if p.material_index==i]
    indices=sorted({v for p in polygons for v in p.vertices})
    lookup={v:j for j,v in enumerate(indices)}
    vertices=[tuple(whole.vertices[v].co) for v in indices]
    faces=[tuple(lookup[v] for v in p.vertices) for p in polygons]
    obj.data.name=names[i]+'_嵌合前记录'
    mesh=bpy.data.meshes.new(names[i]);mesh.from_pydata(vertices,[],faces);mesh.update()
    obj.data=mesh;mesh.materials.append(stone)
    obj['结构']='整体向下嵌合岩体的外表面分件；合起来是单个闭合连通外壳。'
    obj['崩口数量']=chips[obj.name]
    obj.select_set(True)
bpy.context.view_layer.objects.active=left
bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=.872664626,island_margin=.018)
bpy.ops.object.mode_set(mode='OBJECT')
for obj in scene.objects:obj.select_set(False)
scene.name='提纯源岩_完整向下嵌合岩体'
scene.cycles.samples=40
camera=scene.camera
front_camera=camera.matrix_world.copy();scale=camera.data.ortho_scale
scene.render.filepath=str(OUT/'正面_完整嵌合.png')
bpy.ops.render.render(write_still=True)
engine=scene.render.engine
scene.render.engine='BLENDER_WORKBENCH'
scene.display.shading.light='STUDIO';scene.display.shading.color_type='MATERIAL'
scene.display.shading.show_shadows=False;scene.display.shading.show_cavity=True
camera.location=(-3,-4,.7)
camera.rotation_euler=(-camera.location).to_track_quat('-Z','Y').to_euler();camera.data.ortho_scale=2.2
scene.render.filepath=str(OUT/'侧面_连续岩体.png');bpy.ops.render.render(write_still=True)
camera.location=(1,-3,-4)
camera.rotation_euler=(-camera.location).to_track_quat('-Z','Y').to_euler()
scene.render.filepath=str(OUT/'底面_无穿插检查.png');bpy.ops.render.render(write_still=True)
camera.matrix_world=front_camera;camera.data.ortho_scale=scale;scene.render.engine=engine
scene.render.filepath=str(OUT/'正面_完整嵌合.png')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_EmbeddedReview.blend')))
report=dict(source=str(SOURCE),old_left_bottom=old_bottom,new_left_bottom=bottom,left_top=old_top,
    top_and_chip_positions_preserved=True,whole_shell_components=components,whole_shell_non_manifold_edges=non_manifold,
    whole_shell_volume=volume,whole_shell_mesh=whole.name,triangles=sum(len(o.data.polygons) for o in parts),
    parts=[dict(name=o.name,mesh=o.data.name,faces=len(o.data.polygons),chips=o.get('崩口数量')) for o in parts],
    note='三个表面分件保留导出引用，合并后的外壳为单个闭合连通岩体，没有内部交叠面。')
(OUT/'embedded_info.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(report,ensure_ascii=False),flush=True)
