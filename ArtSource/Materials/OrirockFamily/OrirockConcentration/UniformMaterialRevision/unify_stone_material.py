"""修正把图标明暗误作分面底色的问题；不修改现有几何、UV 或摆放。"""
import ast
import bpy
import hashlib
import json
import struct
import sys
from pathlib import Path
from mathutils import Vector
import numpy as np

OUT=Path(__file__).resolve().parent
SOURCE=OUT.parent/'EdgeChipRevision'/'OrirockConcentration_EdgeChips.blend'
KEY='OrirockConcentration'
SIZE=2048
FINAL='--final' in sys.argv

def progress(message): print(message,flush=True)

def geometry_hash(obj):
    digest=hashlib.sha256()
    for v in obj.data.vertices: digest.update(struct.pack('3f',*v.co))
    for p in obj.data.polygons:
        for i in p.vertices: digest.update(struct.pack('I',i))
    for uv in obj.data.uv_layers.active.data: digest.update(struct.pack('2f',*uv.uv))
    for row in obj.matrix_world: digest.update(struct.pack('4f',*row))
    return digest.hexdigest()

def point_at(obj,target=(0,0,0)):
    obj.rotation_euler=(Vector(target)-obj.location).to_track_quat('-Z','Y').to_euler()

def render(scene,name):
    scene.render.filepath=str(OUT/name)
    bpy.ops.render.render(write_still=True)

def setup_view(scene,meshes):
    for obj in scene.objects: obj.select_set(False)
    bpy.context.view_layer.objects.active=meshes[0]
    for area in bpy.context.screen.areas:
        if area.type=='VIEW_3D':
            space=area.spaces.active
            space.region_3d.view_rotation=scene.camera.rotation_euler.to_quaternion()
            space.region_3d.view_location=(0,0,0)
            space.region_3d.view_distance=2.7
            space.region_3d.view_perspective='ORTHO'
            space.overlay.show_overlays=False
            # 渲染模式实际显示场景灯光和阴影，避免材质预览默认 HDRI 干扰验收。
            space.shading.type='RENDERED'

def main():
    bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
    bpy.context.preferences.filepaths.save_version=0
    scene=bpy.context.scene
    scene.name='提纯源岩_统一岩石材质_光照验收'
    meshes=[o for o in scene.objects if o.type=='MESH']
    root=next(o for o in scene.objects if o.type=='EMPTY')
    before={o.name:geometry_hash(o) for o in meshes}
    baseline=[dict(name=o.name,materials=[m.name for m in o.data.materials],
        used_materials=sorted({p.material_index for p in o.data.polygons})) for o in meshes]
    left=next(o for o in meshes if '左后' in o.name)
    stone=left.data.materials[0].copy()
    stone.name='提纯源岩_统一黄褐岩石_全表面共用'
    stone['说明']='各柱所有表面及崩口共用同一基础色、颗粒和粗糙度；不按法线方向或面编号配色。'
    nodes,links=stone.node_tree.nodes,stone.node_tree.links
    # 清除来自上阶段烘焙的未连接目标，保留真正生成岩质颗粒的节点。
    for node in list(nodes):
        if node.type=='TEX_IMAGE' and not any(s.is_linked for s in node.outputs): nodes.remove(node)
    bsdf=next(n for n in nodes if n.type=='BSDF_PRINCIPLED')
    output=next(n for n in nodes if n.type=='OUTPUT_MATERIAL')
    for node in nodes:
        if node.type=='MAP_RANGE':
            node.inputs['To Min'].default_value=.82
            node.inputs['To Max'].default_value=.92
    for obj in meshes:
        obj.data.materials.clear()
        obj.data.materials.append(stone)
        for face in obj.data.polygons: face.material_index=0
        obj['材质说明']='顶、侧、底、崩口及三根柱共用同一黄褐岩石材质，明暗由受光与遮挡产生。'
    scene.render.resolution_x=scene.render.resolution_y=1000
    scene.cycles.samples=48
    scene.cycles.preview_samples=24
    scene.cycles.use_denoising=True
    scene.render.film_transparent=True
    # 中性顶光与弱环境光仅属于此 Blender 模型的预览设备，不导出，不改 Unity 场景。
    lights=[o for o in scene.objects if o.type=='LIGHT']
    for lamp in lights:
        lamp.data.color=(1,1,1)
        if lamp.name.startswith('中性主光'):
            lamp.location=(1.5,3.0,7.0)
            lamp.data.energy=650
            lamp.data.size=2.2
        elif lamp.name.startswith('弱补光'):
            lamp.location=(-3,-4,2.5)
            lamp.data.energy=8
            lamp.data.size=4.5
        else:
            lamp.location=(2,4,5)
            lamp.data.energy=22
            lamp.data.size=3
        point_at(lamp)
    background=next(n for n in scene.world.node_tree.nodes if n.type=='BACKGROUND')
    background.inputs['Color'].default_value=(.21,.21,.21,1)
    background.inputs['Strength'].default_value=.10
    render(scene,'正面_统一材质光照.png')
    # 临时显示纯 Base Color：所有方向均为同一岩色；该输出不会留在正式材质中。
    base=bsdf.inputs['Base Color'].links[0].from_socket
    emission=nodes.new('ShaderNodeEmission')
    links.new(base,emission.inputs['Color'])
    links.new(emission.outputs[0],output.inputs['Surface'])
    render(scene,'底色核对_无光照.png')
    links.new(bsdf.outputs[0],output.inputs['Surface'])
    nodes.remove(emission)
    camera=scene.camera
    front=camera.matrix_world.copy()
    camera.location=(.8,-6.8,1.9)
    point_at(camera)
    # 改为照亮侧壁：验证旧版蓝黑色面能正常显示同一黄褐色。
    lamp_state=[(lamp,lamp.matrix_world.copy(),lamp.data.energy,lamp.data.size) for lamp in lights]
    for lamp in lights:
        if lamp.name.startswith('中性主光'):
            lamp.location=(-3.8,-4.2,2.8)
            lamp.data.energy=460
            lamp.data.size=2.0
            point_at(lamp)
    render(scene,'侧面_换光核对.png')
    for lamp,matrix,energy,size in lamp_state:
        lamp.matrix_world=matrix
        lamp.data.energy=energy
        lamp.data.size=size
    camera.matrix_world=front
    for obj in meshes: assert geometry_hash(obj)==before[obj.name],obj.name
    if FINAL: finish(scene,meshes,root,stone)
    setup_view(scene,meshes)
    scene.render.filepath=str(OUT/'正面_统一材质光照.png')
    bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_UniformStone.blend')))
    report=dict(source=str(SOURCE),blend=str(OUT/(KEY+'_UniformStone.blend')),
        original_materials=baseline,shared_material=stone.name,
        geometry_uv_transform_unchanged=True,mesh_hashes=before,
        triangles=sum(len(o.data.polygons) for o in meshes),
        material_slots_per_mesh=[len(o.data.materials) for o in meshes],
        all_faces_use_same_material=all(p.material_index==0 for o in meshes for p in o.data.polygons),
        validation=['相同基础色无光照渲染','正面光照渲染','侧面换光渲染'],
        export_contents='三个岩柱与模型根；无背景、底板、相机或灯光')
    (OUT/'revision_info.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    progress('全表面共用材质修正完成，几何及 UV 保持原样')

def finish(scene,meshes,root,stone):
    source=OUT.parent/'FrontRevision'/'build_front_revision.py'
    tree=ast.parse(source.read_text(encoding='utf-8'))
    selected=[n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name in ('bake_textures','game_material')]
    class Names(ast.NodeTransformer):
        def visit_Constant(self,node):
            if isinstance(node.value,str):
                node.value=node.value.replace('_FrontRevision','_UniformStone').replace('1024 游戏贴图','2048 游戏贴图')
            return node
    module=Names().visit(ast.Module(body=selected,type_ignores=[]))
    scope=dict(bpy=bpy,np=np,OUT=OUT,KEY=KEY,SIZE=SIZE,progress=progress)
    exec(compile(module,str(source),'exec'),scope)
    images=scope['bake_textures'](scene,meshes,[stone])
    game=scope['game_material'](images)
    for obj in meshes:
        obj.data.materials.clear(); obj.data.materials.append(game)
    for obj in scene.objects: obj.select_set(obj in meshes or obj==root)
    bpy.context.view_layer.objects.active=root
    bpy.ops.export_scene.fbx(filepath=str(OUT/(KEY+'_UniformStone.fbx')),use_selection=True,
        object_types={'EMPTY','MESH'},axis_forward='-Z',axis_up='Y',apply_unit_scale=True,
        global_scale=1.0,use_mesh_modifiers=True,mesh_smooth_type='FACE',use_tspace=True,
        add_leaf_bones=False,bake_anim=False,path_mode='STRIP')
    scene.cycles.samples=64
    render(scene,'正面_烘焙材质.png')
    for obj in meshes:
        obj.data.materials.clear(); obj.data.materials.append(stone)
    for image in images.values(): image.filepath='//textures/'+Path(image.filepath).name

if __name__=='__main__': main()
