"""去除误建的反射实体，修正真实铜柱与后部支架；独立后台执行。"""
import bpy,bmesh,math,json
from pathlib import Path
from mathutils import Matrix,Vector
OUT=Path(__file__).resolve().parent
if not bpy.app.background:raise RuntimeError('请在独立后台进程运行，避免干扰用户当前打开的其他藏品。')
bpy.ops.wm.open_mainfile(filepath=str(OUT/'CrystallineElectronicUnit_FullWhite.blend'))
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'CrystallineElectronicUnit_BeforeReflectionFix.blend'),copy=True)
scene=bpy.context.scene
root=bpy.data.objects['晶体电子单元_完整白模']
white=bpy.data.materials['白模_中性哑光']

def resize_mesh(ob,size):
    ext=[max(v.co[i] for v in ob.data.vertices)-min(v.co[i] for v in ob.data.vertices) for i in range(3)]
    ob.data.transform(Matrix.Diagonal((*[size[i]/ext[i] for i in range(3)],1)))
    ob.data.update()

positions=[('左侧后柱',-1.20,-.40),('左侧前柱',-1.00,-.90),('右侧后柱',.99,.66),('右侧前柱',1.28,.025)]
base=.865
for label,x,y in positions:
    # 台面下方的反射不对应实体，保留单根短圆柱。
    for suffix in ('_上圆柱','_中央腰环'):
        bpy.data.objects.remove(bpy.data.objects[label+suffix],do_unlink=True)
    pad=bpy.data.objects[label+'_底座'];resize_mesh(pad,(.195,.200,.028));pad.location=(x,y,base+.014)
    collar=bpy.data.objects[label+'_下端圈'];resize_mesh(collar,(.245,.245,.025));collar.location=(x,y,base+.038)
    shaft=bpy.data.objects[label+'_下圆柱'];resize_mesh(shaft,(.232,.232,.245));shaft.location=(x,y,base+.173)
    shaft.name=label+'_单段铜柱身'
    cap=bpy.data.objects[label+'_多边形顶帽'];resize_mesh(cap,(.274,.274,.039));cap.location=(x,y,base+.313)
    pin=bpy.data.objects[label+'_顶帽小铆钉'];pin.location=(x,y,base+.338)
bpy.data.collections['新增_四根分段立柱'].name='新增_四根单段铜柱'

# 留出内移柱脚的实心台面。槽底与已验收的顶部模型不变。
deck=bpy.data.objects['八角台面_宽斜面凹口']
for i in range(16,24):
    v=deck.data.vertices[i];r=math.hypot(v.co.x,v.co.y)
    v.co.x*=1.12/r;v.co.y*=1.12/r
deck.data.update()

rear=bpy.data.collections['新增_左后水平底座与铁皮支撑']
# 此集合完全属于用户要求重做的后部组件；备份已保存，不涉及任何磁盘删除。
for ob in list(rear.objects):bpy.data.objects.remove(ob,do_unlink=True)
assembly=bpy.data.objects.new('左后水平底座与双侧铁皮支撑_整体',None)
rear.objects.link(assembly);assembly.parent=root
assembly.location=(-.70,.99,.8925)
assembly.rotation_euler=(0,0,math.radians(25))
assembly['结构说明']='单组横向组件：中央方形核心、两端短绕组；水平底板和两侧直立薄铁皮。下方没有第二个反射实体。'

def mesh_obj(name,vertices,faces,loc=(0,0,0),bevel=.003):
    mesh=bpy.data.meshes.new(name+'_网格');mesh.from_pydata(vertices,[],faces);mesh.update()
    bm=bmesh.new();bm.from_mesh(mesh);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(mesh);bm.free()
    ob=bpy.data.objects.new(name,mesh);rear.objects.link(ob);ob.parent=assembly;ob.location=loc
    mesh.materials.append(white)
    if bevel:
        mod=ob.modifiers.new('边缘小倒角','BEVEL');mod.width=bevel;mod.segments=2
    return ob

def box(name,loc,size,bevel=.003):
    x,y,z=[v/2 for v in size]
    verts=[(-x,-y,-z),(x,-y,-z),(x,y,-z),(-x,y,-z),(-x,-y,z),(x,-y,z),(x,y,z),(-x,y,z)]
    return mesh_obj(name,verts,[(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)],loc,bevel)

def cylinder_x(name,loc,radius,length,bevel=.003):
    n=32
    verts=[(x,radius*math.cos(i*math.tau/n),radius*math.sin(i*math.tau/n)) for x in (-length/2,length/2) for i in range(n)]
    faces=[tuple(reversed(range(n))),tuple(range(n,2*n))]
    faces.extend((i,(i+1)%n,(i+1)%n+n,i+n) for i in range(n))
    ob=mesh_obj(name,verts,faces,loc,bevel)
    for p in ob.data.polygons:
        if len(p.vertices)==4:p.use_smooth=True
    return ob

box('后组件_水平窄底板',(0,0,0),(.85,.38,.055))
for side,label in [(-1,'左'),(1,'右')]:
    box('后组件_'+label+'侧直立铁皮',(side*.367,0,.305),(.030,.35,.550),.0025)
    box('后组件_'+label+'侧折边',(side*.365,0,.033),(.092,.35,.012),.002)
box('后组件_中央方形核心',(0,0,.430),(.340,.300,.300),.009)
for side,label in [(-1,'左'),(1,'右')]:
    cylinder_x('后组件_'+label+'端短铜绕组',(side*.245,0,.430),.120,.150)
    cylinder_x('后组件_'+label+'端支承轴',(side*.344,0,.430),.057,.051,.002)
    cylinder_x('后组件_'+label+'端绕组外缘',(side*.313,0,.430),.124,.014,.0015)

bpy.context.view_layer.update()
# 检查实际所有外轮廓顶点到八角边界的最小余量，防止只移动轴心而仍出框。
poly=[Vector((1.675*math.cos(-math.pi/2+i*math.pi/4),1.675*math.sin(-math.pi/2+i*math.pi/4))) for i in range(8)]
clearance=1e6
root_inv=root.matrix_world.inverted()
for ob in rear.objects:
    if ob.type!='MESH':continue
    for v in ob.data.vertices:
        p=root_inv@ob.matrix_world@v.co;q=Vector((p.x,p.y))
        for i,a in enumerate(poly):
            edge=poly[(i+1)%8]-a
            distance=(edge.x*(q.y-a.y)-edge.y*(q.x-a.x))/edge.length
            clearance=min(clearance,distance)
print('后部组件到八角外边界的最小余量:',round(clearance,5))

notes=json.loads((OUT/'full_white_notes.json').read_text(encoding='utf-8'))
notes['scope']='完整白模：等截面八角箱体、4 根单段短铜柱、单组后部方形核心与两端绕组、水平底板和双侧铁皮支撑、铭牌和散热槽。'
top_objects=set(bpy.data.objects['顶部白模_整体'].children)
notes['new_part_count']=sum(ob.type in {'MESH','FONT'} and ob not in top_objects for ob in scene.objects)
notes['real_parts_revision']={'rear_minimum_outer_clearance':round(clearance,6),'post_positions':positions,
    'post_shaft_height':.245,'post_shaft_diameter':.232,'rear_floor_size':[.85,.38,.055],
    'rear_assembly_position':list(assembly.location),'rear_physical_module_count':1,
    'removed_reflections':'四根柱的重复下段解释及后部第二排绕组不再作为实体。',
    'opening_lip_radius':1.12}
(OUT/'full_white_notes.json').write_text(json.dumps(notes,ensure_ascii=False,indent=2),encoding='utf-8')
scene.camera=bpy.data.objects['参考角度']
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'CrystallineElectronicUnit_FullWhite.blend'))
print('REAL_PARTS_REVISION_COMPLETE')
