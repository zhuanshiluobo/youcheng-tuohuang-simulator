"""观察用同尺度正面对照，不导出图标平面到游戏模型。"""
import bpy
from pathlib import Path

ns=bpy.app.driver_namespace['cluster_square_revision']
work=ns['scene']
right,up,front=(ns[k] for k in ('right','up','front'))
qa=bpy.data.scenes.new('固源岩组_正面对照_仅观察')
qa.render.engine=work.render.engine
qa.world=work.world.copy()
qa.view_settings.view_transform=work.view_settings.view_transform
qa.render.resolution_x=1400
qa.render.resolution_y=700
qa.render.resolution_percentage=100
qa.render.image_settings.file_format=work.render.image_settings.file_format
qa.render.film_transparent=True
shift=right*1.68
for obj in [p[1] for p in ns['preserved']]+[ns['solid']]:
    copy=obj.copy()
    qa.collection.objects.link(copy)
    copy.parent=None
    copy.matrix_world=obj.matrix_world.copy()
    copy.location+=shift
for obj in work.objects:
    if obj.type in {'CAMERA','LIGHT'}:
        copy=obj.copy()
        copy.data=obj.data.copy()
        qa.collection.objects.link(copy)
        if obj.type=='CAMERA':
            qa.camera=copy
            copy.data.ortho_scale=6.60
        else:
            copy.location+=shift
image_path=Path(__file__).resolve().parents[2]/'reference'/'OrirockCluster_PRTS.png'
image=bpy.data.images.load(str(image_path),check_existing=True)
center=-shift-front*2
half=183*.016*.5
mesh=bpy.data.meshes.new('原图对照平面_绝不导出')
mesh.from_pydata([center-right*half-up*half,center+right*half-up*half,
                  center+right*half+up*half,center-right*half+up*half],[],[(0,1,2,3)])
uv=mesh.uv_layers.new(name='UVMap')
for loop,value in zip(uv.data,[(0,0),(1,0),(1,1),(0,1)]):
    loop.uv=value
obj=bpy.data.objects.new('PRTS原图_仅用于左侧对照_不是模型',mesh)
qa.collection.objects.link(obj)
mat=bpy.data.materials.new('原图对照_保持原像素')
mat.use_nodes=True
nodes,links=mat.node_tree.nodes,mat.node_tree.links
output=next(n for n in nodes if n.type=='OUTPUT_MATERIAL')
tex=nodes.new('ShaderNodeTexImage')
tex.image=image
emit=nodes.new('ShaderNodeEmission')
links.new(tex.outputs['Color'],emit.inputs['Color'])
links.new(emit.outputs[0],output.inputs['Surface'])
mesh.materials.append(mat)
qa.render.filepath=str(Path(r'F:\游城拓荒模拟器\Artifacts\OrirockClusterSquare')/globals().get('COMPARE_NAME','正面对照_当前.png'))


def preview_qa():
    bpy.context.window.scene=qa
    bpy.ops.render.render(write_still=True)
    bpy.context.window.scene=work
    return None


bpy.app.timers.register(preview_qa,first_interval=1)
print(qa.render.filepath)
