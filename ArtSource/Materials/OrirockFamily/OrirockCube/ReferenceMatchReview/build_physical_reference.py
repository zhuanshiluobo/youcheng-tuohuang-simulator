"""以正交方体为实体骨架，参考轮廓只约束局部崩裂，防止投影拟合产生尖片。"""
import bpy,bmesh,numpy as np,math,json,sys
from pathlib import Path
from mathutils import Vector,noise
OUT=Path(__file__).resolve().parent
sys.path.insert(0,str(OUT))
import reference_shape as ref
import build_reference_match as util
SOURCE=OUT.parent/'TopTiltOnlyReview/OrirockCube_TopTiltOnlyReview.blend'
BLEND=OUT/'OrirockCube_PhysicalReferenceReview.blend'

P0={
'TL':(-.62,-.55,.58),'BL0':(-.63,.39,.60),'BL1':(-.56,.57,.61),'B0':(-.44,.63,.59),
'B1':(-.22,.63,.61),'B2':(-.06,.63,.63),'B3':(.13,.63,.59),'B4':(.33,.63,.61),'B5':(.51,.59,.60),'B6':(.61,.48,.59),
'RT':(.64,.64,.42),'TR':(.64,-.08,.42),
'L0':(-.57,-.60,.48),'L1':(-.45,-.60,.43),'L2':(-.28,-.61,.44),'L3':(-.07,-.60,.42),
'L4':(.07,-.56,.46),'L5':(.15,-.51,.54),'L6':(.28,-.51,.52),'L7':(.44,-.52,.47),'L8':(.58,-.50,.48),
'C0':(-.60,-.56,.35),'C1':(-.60,-.63,.32),'C2':(-.48,-.63,.36),'C3':(-.32,-.62,.33),
'C4':(-.17,-.63,.34),'C5':(.00,-.62,.32),'C6':(.13,-.60,.26),'C7':(.20,-.59,.13),
'C8':(.32,-.60,-.03),'C9':(.32,-.61,-.16),'C10':(.50,-.59,-.22),
'S0':(.64,-.49,.35),'S1':(.64,-.41,.25),'S2':(.64,-.40,.07),'S3':(.64,-.25,.15),
'S4':(.64,-.28,.02),'S5':(.64,-.38,-.10),'S6':(.64,-.45,-.16),'S7':(.64,-.57,-.24),
'M0':(.32,-.53,.32),'M1':(.40,-.55,.11),'M2':(.52,-.54,-.12),
'F0':(-.61,-.61,-.37),'F1':(-.55,-.61,-.57),'F2':(-.34,-.63,-.61),'F3':(-.04,-.63,-.63),
'F4':(.25,-.61,-.61),'FB':(.62,-.59,-.61),
'I0':(-.37,-.63,.06),'I1':(-.20,-.635,-.14),'I2':(-.08,-.62,-.36),'I3':(.16,-.63,-.44),
'R1':(.64,.64,-.23),'R2':(.64,.31,-.17),'R3':(.64,.31,-.53),
'HB':(-.55,.57,-.59),'HL':(-.63,.42,.03),'HBM':(-.40,.63,-.06),'BM':(.13,.63,-.08),'BR':(.61,.55,.0)
}

def faces_for(points):
    faces=[]; center=sum((points[k] for k in ref.TOP),Vector())/len(ref.TOP)
    inner=[]
    for i,k in enumerate(ref.TOP):
        q=points[k].lerp(center,.56); q.z=.594+noise.noise_vector(q*9).x*.012
        name='T'+str(i); points[name]=q; inner.append(name)
    for i,k in enumerate(ref.TOP):
        j=(i+1)%len(ref.TOP); faces.append([k,ref.TOP[j],inner[j],inner[i]])
    faces.append(inner)
    faces += [
        ['TL','L0','C1','C0'],['L0','L1','C2','C1'],['L1','L2','C3','C2'],
        ['L2','L3','C4','C3'],['L3','L4','C5','C4'],['L4','L5','C6','C5'],['L5','M0','C6'],
        ['C6','M0','M1','C8','C7'],['C8','M1','M2','C9'],['C9','M2','S7','C10'],
        ref.LIP[5:]+['TR','S0','S1','M0'],['M0','S1','S2','M1'],['M1','S2','S3','S4','S5','M2'],['M2','S5','S6','S7'],
        ['C1','C2','I0','I1','I2','I3','F4','F3','F2','F1','F0'],
        ref.LOWER[2:]+['S7','FB','F4','I3','I2','I1','I0'],
        ['RT','TR']+ref.SEAM+['FB','R3','R2','R1'],
        ['TL','C0','C1','F0','F1','HB','HL','BL1','BL0'],
        ['BL1']+ref.BACK[2:]+['RT','R1','BR','BM','HBM','HL'],
        ['HL','HBM','BM','BR','R1','R2','R3','HB']]
    bottom=['F1','F2','F3','F4','FB','R3','HB']
    points['BOTTOM']=Vector((0,0,-.52))
    for i,k in enumerate(bottom): faces.append([k,bottom[(i+1)%len(bottom)],'BOTTOM'])
    return faces

def camera_fit(points):
    xyz=np.array([points[n] for n,*_ in ref.ANCHORS]); uv=np.array([[x-91.5,91.5-y] for n,x,y,*_ in ref.ANCHORS])
    contour=set(ref.TOP+['C0','C1','F0','F1','F2','F3','F4','FB','R1','R2','R3'])
    weights=np.array([3.0 if n in contour else 1.0 for n,*_ in ref.ANCHORS])
    best=None
    for az in np.linspace(20,29,37):
        for el in np.linspace(21,33,33):
            d,r,u=ref.axes(az,el); projected=xyz@np.array([r,u]).T
            mean_p=np.average(projected,axis=0,weights=weights); mean_uv=np.average(uv,axis=0,weights=weights)
            scale=np.sum((projected-mean_p)*(uv-mean_uv)*weights[:,None])/np.sum((projected-mean_p)**2*weights[:,None])
            offset=mean_uv-mean_p*scale
            error=np.average(np.sum((projected*scale+offset-uv)**2,axis=1),weights=weights)
            if best is None or error<best[0]: best=(error,np.array([az,el,scale,*offset]))
    return best[1]

def calibrate(points):
    keys=list(P0); index={k:i for i,k in enumerate(keys)}; start=np.array([P0[k] for k in keys])
    face_copy={k:Vector(v) for k,v in P0.items()}; fs=faces_for(face_copy)
    edges={tuple(sorted((a,b))) for face in fs for a,b in zip(face,face[1:]+face[:1]) if a in P0 and b in P0}
    silhouette=set(ref.TOP+['C0','C1','F0','F1','F2','F3','F4','FB','R1','R2','R3'])
    sides={'RT','TR','S0','S1','S2','S3','S4','S5','S6','S7','FB','R1','R2','R3'}
    for _ in range(4):
        q=camera_fit(points); d,r,u=ref.axes(q[0],q[1]); rows=[]; values=[]
        def row(terms,value,weight):
            a=np.zeros(len(keys)*3)
            for k,axis,coefficient in terms: a[index[k]*3+axis]=coefficient*weight
            rows.append(a); values.append(value*weight)
        for k,i in index.items():
            for a in range(3): row([(k,a,1)],start[i,a],35)
            if k in sides: row([(k,0,1)],.64,2000)
            if k in ref.TOP: row([(k,2,1)],start[i,2],180)
            if k in {'M0','M1','M2'}: row([(k,2,1)],start[i,2],100)
            if k in {'C1','C2','C3','C4','C5','C6','C7','C8','C9','C10','I0','I1','I2','I3','F0','F1','F2','F3','F4'}:
                row([(k,1,1)],start[i,1],150)
        for a,b in edges:
            for axis in range(3): row([(a,axis,1),(b,axis,-1)],P0[a][axis]-P0[b][axis],28)
        for k,x,y,axis,value in ref.ANCHORS:
            weight=2.0 if k in silhouette else .7
            row([(k,a,r[a]*q[2]) for a in range(3)],x-91.5-q[3],weight)
            row([(k,a,u[a]*q[2]) for a in range(3)],91.5-y-q[4],weight)
        result=np.linalg.lstsq(np.array(rows),np.array(values),rcond=None)[0].reshape((-1,3))
        result=np.clip(result,start-.10,start+.10)
        result=np.clip(result,-.645,.645)
        for k,i in index.items():
            points[k]=Vector(result[i])
            if k in sides: points[k].x=.64
    return camera_fit(points)

def main():
    bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
    old=bpy.data.scenes['固源岩_仅顶部倾斜修正_待验收']
    scene=bpy.data.scenes.new('固源岩_实体造型匹配_待验收'); bpy.context.window.scene=scene
    scene.world=old.world.copy(); col=bpy.data.collections.new('固源岩_实体造型'); scene.collection.children.link(col)
    root=bpy.data.objects.new('OrirockCube_PhysicalReferenceRoot',None); col.objects.link(root)
    material=old.objects['固源岩_连续岩质主体'].data.materials[0].copy()
    for n in material.node_tree.nodes:
        if n.type=='TEX_COORD': n.object=root
    p={k:Vector(v) for k,v in P0.items()}; q=calibrate(p); fs=faces_for(p)
    body=util.mesh('固源岩_完整方体与参考崩裂',p,fs,col,root,material,smooth_top=len(ref.TOP)+1)
    cp={k:Vector(v) for k,v in {'a':(-.72,-.60,-.53),'b':(-.66,-.49,-.48),'c':(-.54,-.53,-.43),'d':(-.44,-.59,-.53),'e':(-.44,-.68,-.61),'f':(-.50,-.69,-.67),'g':(-.67,-.67,-.65),'h':(-.75,-.63,-.61),'i':(-.61,-.70,-.52)}.items()}
    chip=util.mesh('固源岩_左下断角',cp,[['a','b','c','d','i'],['i','d','e','f'],['a','i','f','g','h'],['a','h','g','f','e','d','c','b']],col,root,material)
    d,r,u=ref.axes(q[0],q[1]); target=Vector(r*(-q[3]/q[2])+u*(-q[4]/q[2]))
    cam=old.camera.copy(); cam.data=old.camera.data.copy(); scene.collection.objects.link(cam); scene.camera=cam
    cam.location=target+Vector(d)*7; cam.rotation_euler=(target-cam.location).to_track_quat('-Z','Y').to_euler(); cam.data.ortho_scale=183/q[2]
    for o in old.objects:
        if o.type=='LIGHT':
            lamp=o.copy(); lamp.data=o.data.copy(); scene.collection.objects.link(lamp)
            if '主光' in lamp.name:
                lamp.location=(4.6,1.2,2.8); lamp.data.energy=520; lamp.data.size=2.8
            elif '右补光' in lamp.name:
                lamp.location=(-3.5,-4.5,3.2); lamp.data.energy=28
            elif '背光' in lamp.name: lamp.data.energy=55
            lamp.rotation_euler=(-lamp.location).to_track_quat('-Z','Y').to_euler()
    bg=next(n for n in scene.world.node_tree.nodes if n.type=='BACKGROUND')
    bg.inputs['Color'].default_value=(.12,.20,.32,1); bg.inputs['Strength'].default_value=.42
    scene.render.engine='CYCLES'; scene.cycles.samples=32; scene.cycles.use_denoising=True; scene.cycles.device='CPU'
    scene.render.threads_mode='FIXED'; scene.render.threads=10; scene.render.resolution_x=scene.render.resolution_y=915
    scene.render.resolution_percentage=100; scene.render.film_transparent=True
    scene.render.image_settings.file_format='PNG'; scene.render.image_settings.color_mode='RGBA'
    scene.view_settings.view_transform='Standard'; scene.view_settings.look='None'
    bpy.context.view_layer.update()
    report={'文件':str(BLEND),'相机参数':q.tolist(),'状态':'迭代中，未导入 Unity','网格':[]}
    for ob in [body,chip]:
        bm=bmesh.new(); bm.from_mesh(ob.data); ob.data.calc_loop_triangles()
        report['网格'].append({'名称':ob.name,'尺寸':list(ob.dimensions),'三角面':len(ob.data.loop_triangles),'非流形边':sum(not e.is_manifold for e in bm.edges)})
        bm.free()
    (OUT/'physical_status.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND)); saved=(cam.location.copy(),cam.rotation_euler.copy())
    for name,pos,targ in [('physical_front',saved[0],target),('physical_right',(6,0,0),(0,0,0)),('physical_rear',(-3,6,3.2),(0,0,0))]:
        cam.location=pos; cam.rotation_euler=(Vector(targ)-cam.location).to_track_quat('-Z','Y').to_euler(); scene.render.filepath=str(OUT/(name+'.png'))
        print(name,flush=True); bpy.ops.render.render(write_still=True)
    cam.location=saved[0]; cam.rotation_euler=saved[1]; bpy.ops.wm.save_as_mainfile(filepath=str(BLEND)); print(json.dumps(report,ensure_ascii=False),flush=True)

if __name__=='__main__': main()
