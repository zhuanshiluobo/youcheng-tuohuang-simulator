"""在已确认的六边形棱柱源文件上局部切出石质崩口，保留旧阶段。"""
import ast
import bpy
import bmesh
import json
import math
import sys
from pathlib import Path
from mathutils import Vector
import numpy as np

OUT=Path(__file__).resolve().parent
SOURCE=OUT.parent/'HexPrismRevision'/'OrirockConcentration_HexPrisms.blend'
KEY='OrirockConcentration'
SIZE=2048
FINAL='--final' in sys.argv

def progress(message):
    print(message,flush=True)

def top_edges(obj):
    z=max(v.co.z for v in obj.data.vertices)
    ring=[v.co.copy() for v in obj.data.vertices if abs(v.co.z-z)<1e-5]
    center=sum(ring,Vector())/len(ring)
    ring.sort(key=lambda p:math.atan2(p.y-center.y,p.x-center.x))
    return [(a,b) for a,b in zip(ring,ring[1:]+ring[:1]) if (b-a).length>.1]

def cut_chip(obj, a, b, fraction, width, inset, depth, number):
    along=(b-a).normalized()
    inward=Vector((-along.y,along.x,0))
    center=a.lerp(b,fraction)
    # 不对称凸楔：切口向柱内变浅，形成几片有厚度的天然断面。
    shift=(number%3-1)*.12*width
    local=[(-.59*width,-.016,.005),(.52*width,-.016,.005),
           (shift+.06*width,inset,.004),
           (shift-.22*width,inset*.52,.006),
           (-.12*width,-.027,-depth)]
    bm=bmesh.new()
    for u,v,z in local:
        bm.verts.new(center+along*u+inward*v+Vector((0,0,z)))
    bmesh.ops.convex_hull(bm,input=list(bm.verts))
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    mesh=bpy.data.meshes.new('局部崩口刀具_'+str(number))
    bm.to_mesh(mesh)
    bm.free()
    cutter=bpy.data.objects.new(mesh.name,mesh)
    bpy.context.scene.collection.objects.link(cutter)
    for mat in obj.data.materials:
        mesh.materials.append(mat)
    for p in mesh.polygons:
        p.material_index=6
    bpy.context.view_layer.objects.active=obj
    mod=obj.modifiers.new('小崩口_'+str(number),'BOOLEAN')
    mod.operation='DIFFERENCE'
    mod.solver='EXACT'
    mod.object=cutter
    bpy.ops.object.modifier_apply(modifier=mod.name)
    bpy.data.objects.remove(cutter,do_unlink=True)
    bpy.data.meshes.remove(mesh)

def main():
    bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
    bpy.context.preferences.filepaths.save_version=0
    scene=bpy.context.scene
    scene.name='提纯源岩_六棱柱小崩口_验收版'
    meshes=[o for o in scene.objects if o.type=='MESH']
    root=next(o for o in scene.objects if o.type=='EMPTY')
    # edge, position, width, inward depth, vertical depth; 按参考图控制少量浅缺口。
    placements={
        '左后':[(0,.97,.056,.024,.032),(1,.29,.061,.022,.031),
                (1,.63,.038,.015,.024),(1,.76,.024,.012,.018),(2,.13,.035,.018,.026)],
        '右后':[(0,.87,.051,.022,.031),(1,.23,.043,.020,.025),
                (1,.71,.058,.022,.029),(2,.18,.031,.016,.024)],
        '前方':[(0,.95,.046,.020,.029),(1,.30,.078,.029,.035),
                (1,.39,.028,.014,.023),(1,.72,.050,.022,.029),
                (2,.15,.036,.019,.028),(5,.50,.030,.016,.024)]}
    changes=[]
    for obj in meshes:
        part=next(label for label in placements if label in obj.name)
        edges=top_edges(obj)
        assert len(edges)==6
        fresh=obj.data.materials[2].copy()
        fresh.name='提纯源岩_'+part+'_崩口新鲜断面'
        obj.data.materials.append(fresh)
        for number,(edge,t,w,d,h) in enumerate(placements[part],1):
            cut_chip(obj,*edges[edge],t,w,d,h,number)
        # 仅清理切割后共面的三角边，六个柱面和已有微倒角不移动。
        bm=bmesh.new(); bm.from_mesh(obj.data)
        bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
        bmesh.ops.dissolve_limit(bm,angle_limit=.0001,verts=list(bm.verts),edges=list(bm.edges),delimit={'MATERIAL'})
        bmesh.ops.triangulate(bm,faces=list(bm.faces))
        non_manifold=sum(not e.is_manifold for e in bm.edges)
        bm.to_mesh(obj.data); bm.free()
        obj.data.update()
        assert non_manifold==0,(obj.name,non_manifold)
        obj['结构']='保留六边形直棱柱主体；顶面边缘局部浅崩口，凹口有完整封闭断面'
        obj['崩口数量']=len(placements[part])
        changes.append(dict(name=obj.name,chips=len(placements[part]),non_manifold_edges=non_manifold))
    for obj in scene.objects:
        obj.select_set(obj in meshes)
    bpy.context.view_layer.objects.active=meshes[0]
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(50),island_margin=.018)
    bpy.ops.object.mode_set(mode='OBJECT')
    scene.render.resolution_x=scene.render.resolution_y=1100
    scene.cycles.samples=48
    scene.render.filepath=str(OUT/'正面_小崩口.png')
    bpy.ops.render.render(write_still=True)
    setup_view(scene,meshes)
    bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_EdgeChips.blend')))
    if FINAL:
        finish(scene,meshes,root)
    for item,obj in zip(changes,meshes):
        obj.data.calc_loop_triangles()
        item['triangles']=len(obj.data.loop_triangles)
    (OUT/'revision_info.json').write_text(json.dumps(dict(source=str(SOURCE),
        blend=str(OUT/(KEY+'_EdgeChips.blend')),meshes=changes,
        description='三根六边形棱柱的位置、尺度和主体面保持原样，只在参考图顶缘与转角添加15处小崩口。',
        export_contents='仅模型根和三根岩柱，不含刀具、地板、背景、相机或灯光',
        texture_resolution=[SIZE,SIZE] if FINAL else None),ensure_ascii=False,indent=2),encoding='utf-8')

def setup_view(scene,meshes):
    for obj in scene.objects:
        obj.select_set(False)
    bpy.context.view_layer.objects.active=meshes[0]
    for area in bpy.context.screen.areas:
        if area.type=='VIEW_3D':
            space=area.spaces.active
            space.region_3d.view_rotation=scene.camera.rotation_euler.to_quaternion()
            space.region_3d.view_location=(0,0,0)
            space.region_3d.view_distance=2.7
            space.region_3d.view_perspective='ORTHO'
            space.overlay.show_overlays=False
            space.shading.type='MATERIAL'
            space.shading.use_scene_world=True
            space.shading.use_scene_lights=True

def finish(scene,meshes,root):
    source=OUT.parent/'FrontRevision'/'build_front_revision.py'
    tree=ast.parse(source.read_text(encoding='utf-8'))
    functions=[n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name in ('bake_textures','game_material')]
    class RevisionNames(ast.NodeTransformer):
        def visit_Constant(self,node):
            if isinstance(node.value,str):
                node.value=node.value.replace('_FrontRevision','_EdgeChips').replace('1024 游戏贴图','2048 游戏贴图')
            return node
    module=RevisionNames().visit(ast.Module(body=functions,type_ignores=[]))
    scope=dict(bpy=bpy,np=np,OUT=OUT,KEY=KEY,SIZE=SIZE,progress=progress)
    exec(compile(module,str(source),'exec'),scope)
    materials=list({m for obj in meshes for m in obj.data.materials})
    images=scope['bake_textures'](scene,meshes,materials)
    game=scope['game_material'](images)
    assignments=[]
    for obj in meshes:
        assignments.append((list(obj.data.materials),[p.material_index for p in obj.data.polygons]))
        obj.data.materials.clear(); obj.data.materials.append(game)
        for p in obj.data.polygons: p.material_index=0
    for obj in scene.objects: obj.select_set(obj in meshes or obj==root)
    bpy.context.view_layer.objects.active=root
    bpy.ops.export_scene.fbx(filepath=str(OUT/(KEY+'_EdgeChips.fbx')),
        use_selection=True,object_types={'EMPTY','MESH'},axis_forward='-Z',axis_up='Y',
        apply_unit_scale=True,global_scale=1.0,use_mesh_modifiers=True,mesh_smooth_type='FACE',
        use_tspace=True,add_leaf_bones=False,bake_anim=False,path_mode='STRIP')
    scene.cycles.samples=64
    scene.render.filepath=str(OUT/'正面_烘焙材质.png')
    bpy.ops.render.render(write_still=True)
    camera=scene.camera
    before=camera.data.ortho_scale
    camera.data.ortho_scale=1.48
    camera.location.z-=.17
    scene.render.filepath=str(OUT/'边角缺口_近景.png')
    bpy.ops.render.render(write_still=True)
    camera.location.z+=.17
    camera.data.ortho_scale=before
    for obj,(mats,indices) in zip(meshes,assignments):
        obj.data.materials.clear()
        for mat in mats: obj.data.materials.append(mat)
        for p,index in zip(obj.data.polygons,indices): p.material_index=index
    for image in images.values(): image.filepath='//textures/'+Path(image.filepath).name
    setup_view(scene,meshes)
    scene.render.filepath=str(OUT/'正面_小崩口.png')
    bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_EdgeChips.blend')))
    progress('小崩口模型、材质烘焙与导出完成')

if __name__=='__main__': main()
