"""按参考轮廓重新校准固源岩；保留现有统一岩质材质，输出独立验收文件。"""
import bpy,bmesh,math,json,sys
import numpy as np
from pathlib import Path
from mathutils import Vector,noise

OUT=Path(__file__).resolve().parent
sys.path.insert(0,str(OUT))
import reference_shape as ref
SOURCE=OUT.parent/'TopTiltOnlyReview/OrirockCube_TopTiltOnlyReview.blend'
BLEND=OUT/'OrirockCube_ReferenceMatchReview.blend'

def mesh(name,points,faces,col,root,mat,smooth_top=0):
    keys=list(points); me=bpy.data.meshes.new(name+'_网格')
    me.from_pydata([points[k] for k in keys],[],[[keys.index(k) for k in f] for f in faces]); me.update()
    ob=bpy.data.objects.new(name,me); col.objects.link(ob); ob.parent=root; me.materials.append(mat)
    bm=bmesh.new(); bm.from_mesh(me)
    if '断角' in name:
        bmesh.ops.delete(bm,geom=list(bm.faces),context='FACES_ONLY')
        bmesh.ops.convex_hull(bm,input=list(bm.verts),use_existing_faces=False)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    # 保留大断裂面的折线，细分只承载细小矿石起伏。
    bm.faces.ensure_lookup_table()
    for e in bm.edges: e.smooth=len(e.link_faces)==2 and all(f.index<smooth_top for f in e.link_faces)
    bmesh.ops.triangulate(bm,faces=list(bm.faces))
    bmesh.ops.subdivide_edges(bm,edges=list(bm.edges),cuts=3,use_grid_fill=True)
    bm.normal_update()
    for v in bm.verts:
        if any(not e.smooth for e in v.link_edges): continue
        value=noise.noise_vector(v.co*19,noise_basis='PERLIN_ORIGINAL').x*.003
        v.co+=v.normal*value
    bmesh.ops.triangulate(bm,faces=list(bm.faces)); bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    for f in bm.faces: f.smooth=True
    bm.to_mesh(me); bm.free(); me.update()
    return ob

def fit_camera(points,landmarks):
    xyz=np.array([points[n] for n,x,y,axis,value in landmarks])
    expected=np.array([[x-91.5,91.5-y] for n,x,y,axis,value in landmarks])
    def top_level_elevation(az):
        return math.degrees(math.asin(min(.95,max(.10,math.tan(math.radians(az))*20/17))))
    def project(q):
        direction,right,up=ref.axes(q[0],top_level_elevation(q[0]))
        return xyz@np.array([right,up]).T*q[2]+q[3:5]
    q=np.array([26.,35.,83.33,0.,0.])
    for iteration in range(35):
        result=project(q); residual=(result-expected).ravel()
        jac=np.column_stack([((project(q+np.eye(5)[j]*.0001)-result)/.0001).ravel() for j in range(5)])
        step=np.linalg.lstsq(jac,-residual,rcond=None)[0]
        q+=step*.75
        if np.linalg.norm(step)<1e-6: break
    errors=np.linalg.norm(project(q)-expected,axis=1)
    q[1]=top_level_elevation(q[0])
    return q,{'参考控制点平均偏差_像素':float(errors.mean()),'参考控制点最大偏差_像素':float(errors.max())}

def main():
    bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
    previous=bpy.data.scenes['固源岩_仅顶部倾斜修正_待验收']
    oldbody=previous.objects['固源岩_连续岩质主体']
    material=oldbody.data.materials[0].copy()
    scene=bpy.data.scenes.new('固源岩_参考造型校准_待验收'); bpy.context.window.scene=scene
    scene.world=previous.world.copy()
    col=bpy.data.collections.new('固源岩_参考造型'); scene.collection.children.link(col)
    dev=bpy.data.collections.new('参考造型预览设备_不导出'); scene.collection.children.link(dev)
    root=bpy.data.objects.new('OrirockCube_ReferenceMatchRoot',None); col.objects.link(root)
    for n in material.node_tree.nodes:
        if n.type=='TEX_COORD': n.object=root
    P={k:Vector(p) for k,p in ref.build_points(26,35).items()}
    chip_points={a[0]:P.pop(a[0]) for a in ref.CHIP}
    # 顶面为低起伏的浅崩裂，后缘缺口直接来自参考轮廓。
    inner=[]
    center=sum((P[k] for k in ref.TOP),Vector())/len(ref.TOP)
    center.z=.606
    for i,k in enumerate(ref.TOP):
        p=P[k]; q=p.lerp(center,.54)
        q.z=.605+noise.noise_vector(Vector((q.x*8,q.y*8,1.7))).x*.024
        name='T'+str(i); P[name]=q; inner.append(name)
    faces=[]
    for i in range(len(ref.TOP)):
        j=(i+1)%len(ref.TOP); faces.append([ref.TOP[i],ref.TOP[j],inner[j],inner[i]])
    faces.append(inner)
    faces += [
        ['TL','L0','C0'],['L0','C1','C0'],['L0','L1','C2','C1'],['L1','L2','C3','C2'],
        ['L2','L3','C4','C3'],['L3','L4','C5','C4'],['L4','L5','C6','C5'],['L5','M0','C6'],
        ['C6','M0','M1','C8','C7'],['C8','M1','M2','C9'],['C9','M2','S7','C10'],
        ['L5','L6','M0'],['L6','L7','S1','M0'],['L7','L8','TR','S0','S1'],['M0','S1','S2','M1'],
        ['M1','S2','S3','S4','S5','M2'],['M2','S5','S6','S7'],
        ['C1','C2','I0','I1','I2','I3','F4','F3','F2','F1','F0'],
        ref.LOWER[2:]+['S7','FB','F4','I3','I2','I1','I0'],
        ['RT','TR']+ref.SEAM+['FB','R3','R2','R1']]
    # 背面是完整体积。右后边缘保持参考中的阶梯剪影，并接回背部实体。
    P['HB']=Vector((P['BL1'].x,P['BL1'].y,min(P[k].z for k in ['F1','F2','F3','F4','FB'])))
    P['BC']=Vector((.05,.34,.06))
    backloop=['HB','R3','R2','R1','RT']+list(reversed(ref.BACK[1:]))
    faces += [['TL','C0','C1','F0','F1','HB','BL1','BL0']]
    for i,k in enumerate(backloop): faces.append([k,backloop[(i+1)%len(backloop)],'BC'])
    # 不规则底面由外围崩边接到略凹的中心，不制作底板。
    bottom=['F1','F2','F3','F4','FB','R3','HB']
    P['BOTTOM']=sum((P[k] for k in bottom),Vector())/len(bottom); P['BOTTOM'].z+=.045
    for i,k in enumerate(bottom): faces.append([k,bottom[(i+1)%len(bottom)],'BOTTOM'])
    coords=np.array([tuple(p) for p in P.values()]); low=coords.min(0); high=coords.max(0)
    center3=(low+high)*.5; factors=1.28/(high-low)
    # 等比例方体边界，再拟合正交观察方向，使外轮廓与参考保持对齐。
    for group in [P,chip_points]:
        for k,p in group.items(): group[k]=Vector((np.array(p)-center3)*factors)
    # 同时收敛轮廓和等边方体比例，避免只看正面投影却压扁模型厚度。
    convergence=[]
    rim_offsets={'TL':-.025,'BL0':.01,'BL1':.025,'B0':-.005,'B1':.008,'B2':.03,
        'B3':-.005,'B4':.015,'B5':0.,'B6':-.015,
        'L0':-.035,'L1':-.05,'L2':-.045,'L3':-.06,'L4':-.025,'L5':-.008,'L6':-.005,'L7':-.02,'L8':-.005}
    for iteration in range(32):
        q,metrics=fit_camera(P,ref.ANCHORS)
        convergence.append(metrics['参考控制点平均偏差_像素'])
        direction,right,up=ref.axes(q[0],q[1])
        prior={k:p.copy() for k,p in P.items()}
        for name,x,y,axis,value in ref.ANCHORS:
            if name in ref.TOP and name not in {'RT','TR'}: axis='Z'
            if name in {'C0','C1','C2','C3','C4','C5','C6','M0','M1','M2'}: axis='Z'
            a='XYZ'.index(axis)
            point=right*((x-91.5-q[3])/q[2])+up*((91.5-y-q[4])/q[2])
            point+=direction*((P[name][a]-point[a])/direction[a])
            P[name]=Vector(point)
        for i,k in enumerate(ref.TOP): P['T'+str(i)]+=(P[k]-prior[k])*.46
        # 顶冠围绕同一水平面起伏，避免自由追踪像素时把后缘拉成薄翅。
        top_z=(P['RT'].z+P['TR'].z)*.5
        for k,offset in rim_offsets.items(): P[k].z=top_z+offset
        for lower,upper,gap in [('C0','TL',.04),('C1','L0',.07),('C2','L1',.06),
            ('C3','L2',.07),('C4','L3',.075),('C5','L4',.08),('C6','L5',.13)]:
            P[lower].z=P[upper].z-gap
        P['M0'].z=(P['L5'].z+P['C6'].z)*.5
        P['M1'].z=(P['C7'].z+P['C8'].z)*.5+.025
        P['M2'].z=(P['C8'].z+P['C10'].z)*.5+.015
        cap_center=sum((P[k] for k in ref.TOP),Vector())/len(ref.TOP)
        for i,k in enumerate(ref.TOP):
            P['T'+str(i)]=P[k].lerp(cap_center,.54)
            P['T'+str(i)].z=top_z+.005+noise.noise_vector(P['T'+str(i)]*8).x*.009
        # 背面中心随同一圈实体边缘更新，避免旧中心形成大三角凹坑。
        P['HB']=Vector((P['BL1'].x,P['BL1'].y,float(np.median([P[k].z for k in bottom if k!='HB']))+.008))
        P['BC']=sum((P[k] for k in backloop),Vector())/len(backloop)
        P['BOTTOM']=sum((P[k] for k in bottom),Vector())/len(bottom); P['BOTTOM'].z+=.035
        verts=np.array([p for p in P.values()]); lo,hi=verts.min(0),verts.max(0)
        for group in [P,chip_points]:
            for k,p in group.items(): group[k]=Vector((np.array(p)-(lo+hi)/2)*1.28/(hi-lo))
    q,metrics=fit_camera(P,ref.ANCHORS)
    metrics['轮廓与方体校准过程']=convergence
    direction,right,up=ref.axes(q[0],q[1])
    # 顶面内圈在参考中的位置明确位于外轮廓内，避免非凸边界的扇形重叠。
    center_screen=np.array([92.,51.])
    reference_xy={n:np.array([x,y],dtype=float) for n,x,y,axis,value in ref.ANCHORS}
    for i,k in enumerate(ref.TOP):
        xy=reference_xy[k]*.46+center_screen*.54
        z=P['T'+str(i)].z
        p=right*((xy[0]-91.5-q[3])/q[2])+up*((91.5-xy[1]-q[4])/q[2])
        p+=direction*((z-p[2])/direction[2])
        P['T'+str(i)]=Vector(p)
    # 左下断角为浅楔片，轮廓沿用图中原位，不做鼓起的大块。
    depth=sum(np.dot(p,direction) for p in chip_points.values())/len(chip_points)
    for name,x,y,axis,value in ref.CHIP:
        local_depth=depth+(np.dot(chip_points[name],direction)-depth)*.52
        chip_points[name]=Vector(right*((x-91.5-q[3])/q[2])+up*((91.5-y-q[4])/q[2])+direction*local_depth)
    metrics['最终控制点投影']='保留方体厚度并校准参考轮廓；仍以渲染形状和多视图检查为准。'
    body=mesh('固源岩_参考轮廓连续主体',P,faces,col,root,material)
    (OUT/'control_points.json').write_text(json.dumps({k:list(v) for k,v in P.items()},ensure_ascii=False,indent=2),encoding='utf-8')
    chipfaces=[['a','b','c','d','i'],['i','d','e','f'],['a','i','f','g','h'],['a','h','g','f','e','d','c','b']]
    chip=mesh('固源岩_左下原位断角',chip_points,chipfaces,col,root,material)
    # 正面主视角始终展示参考图里的同一组可见面。
    target=Vector(right*(-q[3]/q[2])+up*(-q[4]/q[2]))
    cam=previous.camera.copy(); cam.data=previous.camera.data.copy(); dev.objects.link(cam)
    cam.name='固源岩_参考正面相机'; scene.camera=cam
    cam.location=target+Vector(direction)*7
    cam.rotation_euler=(target-cam.location).to_track_quat('-Z','Y').to_euler()
    cam.data.ortho_scale=183/q[2]
    for o in previous.objects:
        if o.type!='LIGHT': continue
        lamp=o.copy(); lamp.data=o.data.copy(); dev.objects.link(lamp)
    scene.render.engine='CYCLES'; scene.cycles.device='CPU'; scene.cycles.samples=40; scene.cycles.use_denoising=True
    scene.render.threads_mode='FIXED'; scene.render.threads=10
    scene.render.resolution_x=scene.render.resolution_y=915; scene.render.resolution_percentage=100
    scene.render.film_transparent=True; scene.render.image_settings.file_format='PNG'; scene.render.image_settings.color_mode='RGBA'
    scene.view_settings.view_transform=previous.view_settings.view_transform; scene.view_settings.look=previous.view_settings.look
    root['参考']='OrirockCube_PRTS.png；仅岩石轮廓，不含圆环、地面、投影。'
    root['说明']='同一连续岩质材质；背面按参考可见边缘延续推定。等待 Blender 验收。'
    for a in bpy.context.screen.areas:
        if a.type=='VIEW_3D':
            s=a.spaces.active; s.region_3d.view_rotation=cam.rotation_euler.to_quaternion()
            s.region_3d.view_location=target; s.region_3d.view_distance=3.15; s.region_3d.view_perspective='ORTHO'
            s.shading.type='MATERIAL'; s.shading.use_scene_world=True; s.shading.use_scene_lights=True; s.overlay.show_overlays=False
    for o in scene.objects: o.select_set(o==body)
    bpy.context.view_layer.objects.active=body; bpy.context.view_layer.update()
    report={'基准':str(SOURCE),'当前文件':str(BLEND),'状态':'待 Blender 验收，未导入 Unity','正面拟合':metrics,
        '相机参数':q.tolist(),'目标点':list(target),'网格':[]}
    for ob in [body,chip]:
        bm=bmesh.new(); bm.from_mesh(ob.data); ob.data.calc_loop_triangles()
        report['网格'].append({'名称':ob.name,'尺寸':list(ob.dimensions),'三角面':len(ob.data.loop_triangles),'非流形边':sum(not e.is_manifold for e in bm.edges)})
        bm.free()
    (OUT/'review_status.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND))
    if '--no-render' in sys.argv:
        bm=bmesh.new(); bm.from_mesh(body.data)
        print('OPEN_EDGES',[[list(v.co) for v in e.verts] for e in bm.edges if not e.is_manifold],flush=True)
        bm.free()
        return
    saved=(cam.location.copy(),cam.rotation_euler.copy())
    for name,pos,targ in [('front',saved[0],target),('right',(6,0,0),(0,0,0)),('rear',(-3,6,3.2),(0,0,0))]:
        cam.location=pos; cam.rotation_euler=(Vector(targ)-cam.location).to_track_quat('-Z','Y').to_euler()
        scene.render.filepath=str(OUT/(name+'.png')); print('RENDER',name,flush=True); bpy.ops.render.render(write_still=True)
    cam.location=saved[0]; cam.rotation_euler=saved[1]
    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND))
    print('REFERENCE_MATCH_READY',json.dumps(report,ensure_ascii=False),flush=True)

if __name__=='__main__': main()
