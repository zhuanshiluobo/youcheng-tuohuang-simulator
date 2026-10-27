"""补全白模标签、后部铜管和薄铁皮支撑，按底座自身中轴居中。"""
import bpy, bmesh, math, json
from pathlib import Path
from mathutils import Vector

OUT = Path(__file__).resolve().parent
FILE = OUT / 'CrystallineElectronicUnit_FullWhite.blend'
ANGLE = math.radians(22.5)
U = Vector((math.cos(ANGLE), math.sin(ANGLE), 0))
V = Vector((-math.sin(ANGLE), math.cos(ANGLE), 0))


def apply_revision(save=True):
    root = bpy.data.objects['晶体电子单元_完整白模']
    rear = bpy.data.collections['新增_左后水平底座与铁皮支撑']
    details = bpy.data.collections['新增_铭牌与侧壁散热槽']
    white = bpy.data.materials['白模_中性哑光']
    if save:
        backup = OUT / 'CrystallineElectronicUnit_BeforeCenteredDetails.blend'
        if not backup.exists():
            bpy.ops.wm.save_as_mainfile(filepath=str(backup), copy=True)

    def empty(name, coll, parent):
        ob = bpy.data.objects.get(name)
        if ob is None:
            ob = bpy.data.objects.new(name, None)
            coll.objects.link(ob)
        ob.parent = parent
        return ob

    def mesh(name, verts, faces, coll, parent, loc=(0,0,0), bevel=.003):
        data = bpy.data.meshes.new(name + '_精修网格')
        data.from_pydata(verts, [], faces)
        data.update()
        bm = bmesh.new(); bm.from_mesh(data)
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        bm.to_mesh(data); bm.free()
        ob = bpy.data.objects.get(name)
        if ob is None:
            ob = bpy.data.objects.new(name, data)
            coll.objects.link(ob)
        else:
            ob.data = data
        ob.parent = parent
        ob.location = loc; ob.rotation_euler = (0,0,0); ob.scale = (1,1,1)
        ob.hide_render = False; ob.hide_set(False)
        ob.modifiers.clear()
        data.materials.append(white)
        if bevel:
            mod = ob.modifiers.new('边缘小倒角', 'BEVEL')
            mod.width = bevel; mod.segments = 2
        return ob

    def box(name, loc, size, coll, parent, bevel=.003):
        x,y,z = (n/2 for n in size)
        verts = [(-x,-y,-z),(x,-y,-z),(x,y,-z),(-x,y,-z),
                 (-x,-y,z),(x,-y,z),(x,y,z),(-x,y,z)]
        return mesh(name, verts, [(0,3,2,1),(4,5,6,7),(0,1,5,4),
                                 (1,2,6,5),(2,3,7,6),(3,0,4,7)], coll, parent, loc, bevel)

    def cylinder(name, loc, radius, depth, coll, parent, axis='X', count=48, bevel=.002):
        verts = [(radius*math.cos(i*math.tau/count),radius*math.sin(i*math.tau/count),z)
                 for z in (-depth/2,depth/2) for i in range(count)]
        faces = [tuple(reversed(range(count))),tuple(range(count,count*2))]
        faces += [(i,(i+1)%count,(i+1)%count+count,i+count) for i in range(count)]
        ob = mesh(name,verts,faces,coll,parent,loc,bevel)
        if axis == 'X':ob.rotation_euler.y = math.pi/2
        for p in ob.data.polygons:
            if len(p.vertices)==4:p.use_smooth=True
        return ob

    def ring(name,loc,outer,inner,depth,coll,parent,axis='Z',count=64):
        verts = [(r*math.cos(i*math.tau/count),r*math.sin(i*math.tau/count),z)
                 for z,r in [(-depth/2,outer),(depth/2,outer),(depth/2,inner),(-depth/2,inner)]
                 for i in range(count)]
        faces = [(j*count+i,j*count+(i+1)%count,((j+1)%4)*count+(i+1)%count,((j+1)%4)*count+i)
                 for j in range(4) for i in range(count)]
        ob = mesh(name,verts,faces,coll,parent,loc,min(.001,depth/5))
        if axis == 'X':ob.rotation_euler.y=math.pi/2
        for p in ob.data.polygons:
            if p.index//count in (0,2):p.use_smooth=True
        return ob

    def outline(name, outer, inner, bottom, top, coll, parent):
        n=len(outer)
        verts=[(x,y,z) for z,points in [(bottom,outer),(top,outer),(top,inner),(bottom,inner)] for x,y in points]
        faces=[(j*n+i,j*n+(i+1)%n,((j+1)%4)*n+(i+1)%n,((j+1)%4)*n+i) for j in range(4) for i in range(n)]
        return mesh(name,verts,faces,coll,parent,bevel=.0006)

    assembly = bpy.data.objects['左后水平底座与双侧铁皮支撑_整体']
    # 平行于后沿，将底板置于凹口和外缘之间。
    assembly.location = V*1.265 + Vector((0,0,.9675))
    assembly.rotation_euler=(0,0,ANGLE)
    box('后组件_水平窄底板',(0,0,0),(.93,.36,.055),rear,assembly)

    def upright(name, x):
        # 薄铁皮的六边形肩部轮廓与真实轴孔；没有后立柱。
        outline=[(-.16,.028),(.16,.028),(.16,.472),(.105,.592),(-.105,.592),(-.16,.472)]
        center=Vector((0,.414)); thickness=.027
        angles=[i*math.tau/64 for i in range(64)]
        angles += [math.atan2(z-center.y,y-center.x)%math.tau for y,z in outline]
        angles=sorted(set(round(a,9) for a in angles))
        outside=[]; inside=[]
        for angle in angles:
            direction=Vector((math.cos(angle),math.sin(angle)))
            hits=[]
            for i in range(len(outline)):
                a=Vector(outline[i])-center; b=Vector(outline[(i+1)%len(outline)])-center
                edge=b-a
                cross=direction.x*edge.y-direction.y*edge.x
                if abs(cross)<1e-9:continue
                t=(a.x*edge.y-a.y*edge.x)/cross
                s=(a.x*direction.y-a.y*direction.x)/cross
                if t>0 and -.000001<=s<=1.000001:hits.append(t)
            distance=min(hits)
            outside.append(center+direction*distance)
            inside.append(center+direction*.063)
        n=len(angles)
        verts=[(xx,p.x,p.y) for xx,pts in [(-thickness/2,outside),(thickness/2,outside),(thickness/2,inside),(-thickness/2,inside)] for p in pts]
        faces=[(j*n+i,j*n+(i+1)%n,((j+1)%4)*n+(i+1)%n,((j+1)%4)*n+i) for j in range(4) for i in range(n)]
        return mesh(name,verts,faces,rear,assembly,(x,0,0),.0015)

    for sign,label in [(-1,'左'),(1,'右')]:
        upright('后组件_'+label+'侧直立铁皮',sign*.403)
        box('后组件_'+label+'侧折边',(sign*.398,0,.035),(.10,.32,.015),rear,assembly,.002)
        box('后组件_'+label+'侧后折边',(sign*.383,.153,.263),(.067,.014,.449),rear,assembly,.002)
        for j,z in enumerate((.140,.196,.252)):
            box('后组件_'+label+'铁皮浅筋_'+str(j),(sign*.419,-.014,z),(.006,.245,.013),rear,assembly,.001)
        for j,y in enumerate((-.117,.117)):
            cylinder('后组件_'+label+'底脚铆钉_'+str(j),(sign*.434,y,.047),.015,.012,rear,assembly,'Z',12,.001)
        ring('后组件_'+label+'轴孔座',(sign*.422,0,.414),.077,.063,.019,rear,assembly,'X')
        cylinder('后组件_'+label+'端支承轴',(sign*.383,0,.414),.053,.087,rear,assembly)
        ring('后组件_'+label+'端短铜绕组',(sign*.269,0,.414),.135,.058,.181,rear,assembly,'X')
        ring('后组件_'+label+'端绕组外缘',(sign*.351,0,.414),.145,.058,.020,rear,assembly,'X')
        ring('后组件_'+label+'铜管内端套圈',(sign*.191,0,.414),.143,.058,.020,rear,assembly,'X')
    box('后组件_中央方形核心',(0,0,.414),(.36,.285,.300),rear,assembly,.009)
    box('后组件_核心上盖沿',(0,0,.573),(.395,.327,.024),rear,assembly,.003)
    box('后组件_核心顶面压片',(0,0,.595),(.248,.145,.022),rear,assembly,.003)
    box('后组件_核心下托边',(0,0,.255),(.393,.321,.022),rear,assembly,.003)

    labels=empty('台面标签_居中组合',details,root)
    labels.location=V*(-1.29)+Vector((0,0,.94))
    labels.rotation_euler=(0,0,ANGLE)
    # 合并图案整体外包范围 [-.290, .290]，而不是分别叠到中轴。
    box('台面小识别牌',(.145,0,.006),(.290,.260,.012),details,labels,.0015)
    outline('识别牌完整闭合边框',[(.008,-.122),(.282,-.122),(.282,.122),(.008,.122)],
            [(.018,-.112),(.272,-.112),(.272,.112),(.018,.112)],.0125,.0195,details,labels)
    ring('识别牌环形标志',(.145,0,.017),.079,.068,.009,details,labels)
    outline('识别牌中央闭合几何标志',[(.145,-.043),(.183,0),(.145,.043),(.107,0)],
            [(.145,-.032),(.172,0),(.145,.032),(.118,0)],.014,.022,details,labels)
    cylinder('识别牌中心点',(.145,0,.019),.010,.009,details,labels,'Z',16,.0005)
    outline('台面三角闭合标记',[(-.290,-.121),(-.048,-.121),(-.169,.127)],
            [(-.270,-.109),(-.068,-.109),(-.169,.099)],.005,.013,details,labels)
    box('台面三角警示内芯',(-.169,-.018,.009),(.013,.072,.008),details,labels,.001)
    cylinder('台面三角警示圆点',(-.169,-.074,.009),.008,.008,details,labels,'Z',16,.0005)
    # 仅移除明确列出的被替换模型对象，连续闭合轮廓避免接头相交的黑缝。
    obsolete=['识别牌边线-0.133','识别牌边线0.133',
              '识别牌完整边框_左','识别牌完整边框_右','识别牌完整边框_前','识别牌完整边框_后',
              '识别牌中央几何标志_0','识别牌中央几何标志_1','识别牌中央几何标志_2','识别牌中央几何标志_3',
              '台面三角标记_0','台面三角标记_1','台面三角标记_2']
    for name in obsolete:
        old=bpy.data.objects.get(name)
        if old is not None:bpy.data.objects.remove(old,do_unlink=True)
    bpy.context.view_layer.update()

    points=[o.matrix_world@v.co for o in rear.objects if o.type=='MESH' for v in o.data.vertices]
    apothem=1.675*math.cos(math.pi/8)
    normals=[Vector((math.cos(math.pi/8+i*math.pi/4),math.sin(math.pi/8+i*math.pi/4),0)) for i in range(8)]
    clearance=min(apothem-p.dot(n) for p in points for n in normals)
    label_points=[o.matrix_world@v.co for o in labels.children if o.type=='MESH' for v in o.data.vertices]
    label_mid=(min(p.dot(U) for p in label_points)+max(p.dot(U) for p in label_points))/2
    print('后部中轴横向偏移',assembly.location.dot(U),'标签组合外包中心偏移',label_mid,'后部离外缘余量',clearance)
    notes_path=OUT/'full_white_notes.json'
    notes=json.loads(notes_path.read_text(encoding='utf-8')) if notes_path.exists() else {}
    notes['centered_details_revision']={
        'axis_angle_degrees':22.5,'rear_center':list(assembly.location),
        'rear_local_center':[0,1.265],'labels_local_center':[0,-1.29],
        'rear_outer_clearance':clearance,'label_center_error':label_mid,
        'details':'完整方框、环形几何标志及三角内芯；两端铜管及套圈、贯穿短轴、中央壳盖沿、带轴孔双侧薄铁皮、折边和浅筋。',
        'reference_limit':'标签低清参考无法确认文字及内部符号名称，按可见的圆环和中心几何形表达。',
        'materials':'统一白模；铜色与黑色仅用于识别参考部件。'}
    if 'real_parts_revision' in notes:
        notes['real_parts_revision']['rear_assembly_position']=list(assembly.location)
        notes['real_parts_revision']['rear_floor_size']=[.93,.36,.055]
        notes['real_parts_revision']['rear_minimum_outer_clearance']=clearance
    if save:notes_path.write_text(json.dumps(notes,ensure_ascii=False,indent=2),encoding='utf-8')
    bpy.context.scene.camera=bpy.data.objects['参考角度']
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type=='VIEW_3D':
                sp=area.spaces.active
                sp.show_region_ui=False;sp.overlay.show_overlays=False
                sp.region_3d.view_perspective='ORTHO'
                sp.region_3d.view_location=(0,0,1.2775)
                sp.region_3d.view_rotation=bpy.data.objects['参考角度'].rotation_euler.to_quaternion()
                sp.region_3d.view_distance=5.65
    if save:bpy.ops.wm.save_as_mainfile(filepath=str(FILE))
    return notes


if __name__ == '__main__':
    if bpy.app.background:bpy.ops.wm.open_mainfile(filepath=str(FILE))
    elif Path(bpy.data.filepath)!=FILE:raise RuntimeError('活动文件不是指定白模，停止修改。')
    apply_revision()
    print('CENTERED_DETAILS_COMPLETE')
