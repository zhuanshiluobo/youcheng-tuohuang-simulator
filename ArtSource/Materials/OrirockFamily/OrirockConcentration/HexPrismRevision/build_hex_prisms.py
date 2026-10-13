"""沿用当前材质源，修正为三根真正的六边形棱柱；旧阶段文件不覆盖。"""
import bpy
import bmesh
import json
import math
import sys
from pathlib import Path
from mathutils import Vector, Matrix
import numpy as np

OUT = Path(__file__).resolve().parent
SOURCE = OUT.parent / 'FrontRevision' / 'OrirockConcentration_FrontRevision.blend'
KEY = 'OrirockConcentration'
FINAL = '--final' in sys.argv
SIZE = 2048

def progress(message):
    print(message, flush=True)

def rgba(rgb):
    def linear(v):
        v = v / 255
        return v / 12.92 if v <= .04045 else ((v + .055) / 1.055) ** 2.4
    return tuple(linear(v) for v in rgb) + (1,)

def point_at(obj, target=(0, 0, 0)):
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat('-Z', 'Y').to_euler()

def build():
    bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
    scene = bpy.context.scene
    scene.name = '提纯源岩_六边形棱柱_可编辑源'
    bpy.context.preferences.filepaths.save_version = 0
    meshes = [o for o in scene.objects if o.type == 'MESH']
    root = next(o for o in scene.objects if o.type == 'EMPTY')
    camera = scene.camera
    orientation = camera.rotation_euler.to_quaternion()
    right, up = orientation @ Vector((1,0,0)), orientation @ Vector((0,1,0))
    inv = Matrix(((right.x,right.y),(up.x,up.y))).inverted()
    # 六个角仅用于拟合位置、半径和朝向；最终截面为正六边形。
    specs = [
        ('左后', [(17.5,40),(39.5,10.5),(84,7),(113.5,32.5),(97,62.5),(45.5,67)], .50, (214,187,136)),
        ('右后', [(112,35),(130.5,34.5),(164,58.5),(153,92.5),(99.5,97),(80.5,79)], .15, (225,191,135)),
        ('前方', [(39.5,84),(81.5,82.5),(112,110.5),(100.5,137.5),(47.5,142),(20.5,109)], -.18, (157,131,81)),
    ]
    for label, pixels, top_z, rgb in specs:
        obj = next(o for o in meshes if label in o.name)
        source_mesh_name = obj.data.name
        mats = [m.copy() for m in obj.data.materials]
        for mat in mats:
            mat.name = mat.name.replace('.001','') + '_六棱修订'
            for node in mat.node_tree.nodes:
                if node.type == 'TEX_NOISE' and node.inputs['Scale'].default_value < 40:
                    node.inputs['Scale'].default_value = 44
                if node.type == 'BUMP':
                    node.inputs['Distance'].default_value = .0015
        topmat = mats[0]
        ramp = next(n for n in topmat.node_tree.nodes if n.type == 'VALTORGB')
        ramp.color_ramp.elements[0].color = rgba(tuple(v * .93 for v in rgb))
        ramp.color_ramp.elements[1].color = rgba(tuple(min(255, v * 1.05) for v in rgb))
        topmat.diffuse_color = rgba(rgb)
        for node in topmat.node_tree.nodes:
            if node.type == 'MIX_RGB' and abs(node.inputs[0].default_value - .5) < .01:
                node.inputs[0].default_value = .34
        target = []
        for x,y in pixels:
            xy = inv @ Vector(((x-91.5)*.0125, (91.5-y)*.0125-up.z*top_z))
            target.append(complex(xy.x, xy.y))
        center = sum(target)/6
        unit = [complex(math.cos(-i*math.tau/6),math.sin(-i*math.tau/6)) for i in range(6)]
        fit = sum((p-center)*u.conjugate() for p,u in zip(target,unit))/6
        ring = [center+fit*u for u in unit]
        bottom_z = -.20 if label == '左后' else -.66
        vertices = [(p.real,p.imag,z) for z in (bottom_z,top_z) for p in ring]
        faces = [tuple(range(6)), tuple(reversed(range(6,12)))]
        faces += [(i,(i+1)%6,(i+1)%6+6,i+6) for i in range(6)]
        # 保留网格及对象名称，使现有 Unity Mesh 引用可继续解析。
        obj.data.name = source_mesh_name + '_旧网格内存副本'
        mesh = bpy.data.meshes.new(source_mesh_name)
        mesh.from_pydata(vertices,[],faces)
        mesh.update()
        obj.data = mesh
        for mat in mats:
            mesh.materials.append(mat)
        for polygon in mesh.polygons:
            polygon.material_index = 2
        mesh.polygons[1].material_index = 0
        # 三根柱采用连续整面岩壁，保留参考左侧的灰青深色断面。
        for i in range(6):
            mesh.polygons[i+2].material_index = 3 if i == 5 else 2
        bm = bmesh.new()
        bm.from_mesh(mesh)
        bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
        bm.to_mesh(mesh)
        bm.free()
        obj['结构'] = '正六边形等截面直棱柱；六个纵向平面；微倒角仅用于棱缘'
        obj['截面边数'] = 6
        obj['原始棱柱半径'] = abs(fit)
        obj['原始棱柱高度'] = top_z - bottom_z
        obj['背面说明'] = '完整底面与背面为单视图推定，左后短柱搭靠于前方低柱'
    # 保持既有源的最大边 1.65，并居中；不改展示预制体的姿态和比例。
    points = [v.co.copy() for o in meshes for v in o.data.vertices]
    low = Vector(tuple(min(p[i] for p in points) for i in range(3)))
    high = Vector(tuple(max(p[i] for p in points) for i in range(3)))
    center, scale = (low+high)/2, 1.65/max(high-low)
    for obj in meshes:
        for v in obj.data.vertices:
            v.co = (v.co-center)*scale
        bpy.context.view_layer.objects.active = obj
        bevel = obj.modifiers.new('细小石质棱缘', 'BEVEL')
        bevel.width = .004
        bevel.segments = 1
        bevel.material = -1
        bpy.ops.object.modifier_apply(modifier=bevel.name)
    for obj in scene.objects:
        obj.select_set(obj in meshes)
    bpy.context.view_layer.objects.active = meshes[0]
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(50),island_margin=.025)
    bpy.ops.object.mode_set(mode='OBJECT')
    for obj in meshes:
        bm=bmesh.new()
        bm.from_mesh(obj.data)
        bmesh.ops.triangulate(bm,faces=list(bm.faces))
        bm.to_mesh(obj.data)
        bm.free()
        obj.data.update()
    scene.render.resolution_x = scene.render.resolution_y = 900
    scene.render.film_transparent = True
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGBA'
    scene.cycles.samples = 48
    scene.cycles.use_denoising = True
    camera.data.ortho_scale = 2.25
    scene.render.filepath = str(OUT/'正面_六棱柱.png')
    bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_HexPrisms.blend')))
    bpy.ops.render.render(write_still=True)
    if FINAL:
        finish(scene,meshes,root)
    return scene,meshes

def finish(scene, meshes, root):
    # 只复用无副作用的烘焙函数；不执行旧生成器，也不重建其他材料。
    import ast
    old = OUT.parent/'FrontRevision'/'build_front_revision.py'
    tree = ast.parse(old.read_text(encoding='utf-8'))
    functions = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in ('bake_textures','game_material')]
    scope = dict(bpy=bpy, OUT=OUT, KEY=KEY, SIZE=SIZE, progress=progress, np=np)
    exec(compile(ast.Module(body=functions,type_ignores=[]),str(old),'exec'),scope)
    materials = list({m for o in meshes for m in o.data.materials if m})
    images = scope['bake_textures'](scene,meshes,materials)
    assignments=[]
    game=scope['game_material'](images)
    for obj in meshes:
        assignments.append((list(obj.data.materials),[p.material_index for p in obj.data.polygons]))
        obj.data.materials.clear()
        obj.data.materials.append(game)
        for p in obj.data.polygons:
            p.material_index=0
    for obj in scene.objects:
        obj.select_set(obj in meshes or obj == root)
    bpy.context.view_layer.objects.active = root
    bpy.ops.export_scene.fbx(filepath=str(OUT/(KEY+'_HexPrisms.fbx')),
        use_selection=True,object_types={'EMPTY','MESH'},axis_forward='-Z',axis_up='Y',
        apply_unit_scale=True,global_scale=1.0,use_mesh_modifiers=True,mesh_smooth_type='FACE',
        use_tspace=True,add_leaf_bones=False,bake_anim=False,path_mode='STRIP')
    scene.cycles.samples=64
    scene.render.filepath=str(OUT/'正面_烘焙材质.png')
    bpy.ops.render.render(write_still=True)
    camera=scene.camera
    front=camera.matrix_world.copy()
    camera.location=(-3.5,4.5,3.8)
    point_at(camera)
    scene.render.filepath=str(OUT/'背面_烘焙材质.png')
    bpy.ops.render.render(write_still=True)
    camera.location=(0,0,8)
    point_at(camera)
    scene.render.filepath=str(OUT/'顶视_六边形截面.png')
    bpy.ops.render.render(write_still=True)
    camera.matrix_world=front
    for obj,(mats,indices) in zip(meshes,assignments):
        obj.data.materials.clear()
        for mat in mats:
            obj.data.materials.append(mat)
        for p,index in zip(obj.data.polygons,indices):
            p.material_index=index
    for image in images.values():
        image.filepath='//textures/'+Path(image.filepath).name
    scene.render.filepath=str(OUT/'正面_六棱柱.png')
    for area in bpy.context.screen.areas:
        if area.type=='VIEW_3D':
            area.spaces.active.region_3d.view_perspective='CAMERA'
            area.spaces.active.shading.type='MATERIAL'
    bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_HexPrisms.blend')))
    counts=[]
    for obj in meshes:
        obj.data.calc_loop_triangles()
        bm=bmesh.new(); bm.from_mesh(obj.data)
        counts.append(dict(name=obj.name,mesh=obj.data.name,triangles=len(obj.data.loop_triangles),
                           non_manifold_edges=sum(not e.is_manifold for e in bm.edges)))
        bm.free()
    (OUT/'revision_info.json').write_text(json.dumps(dict(
        source=str(SOURCE),blend=str(OUT/(KEY+'_HexPrisms.blend')),fbx=str(OUT/(KEY+'_HexPrisms.fbx')),
        meshes=counts,texture_resolution=[SIZE,SIZE],
        export_contents='仅三根完整六边形棱柱及模型根，无背景、底板、相机或灯光',
        notes='上下截面相同；侧壁沿竖直轴平行；前低后高；单视图不可见的背面及底面为推定'),
        ensure_ascii=False,indent=2),encoding='utf-8')
    progress('六边形棱柱模型、贴图与多视角渲染完成')

if __name__ == '__main__':
    build()
