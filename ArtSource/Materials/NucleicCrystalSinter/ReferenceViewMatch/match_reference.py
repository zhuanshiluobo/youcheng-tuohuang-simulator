"""仅匹配 Blender 参考视角，不导出 FBX，不写入 Assets。"""
import bpy, math, json
import numpy as np
from pathlib import Path
from mathutils import Vector, Quaternion
from bpy_extras.object_utils import world_to_camera_view

ART=Path('F:/游城拓荒模拟器/ArtSource/Materials/NucleicCrystalSinter')
OUT=ART/'ReferenceViewMatch'
OUT.mkdir(exist_ok=True)
bpy.ops.wm.open_mainfile(filepath=str(ART/'ReferenceRefinement/NucleicCrystalSinter_Refined.blend'))
bpy.context.preferences.filepaths.save_version=0
scene=bpy.context.scene
cam=scene.camera
scene.render.resolution_x=760
scene.render.resolution_y=836
scene.render.resolution_percentage=100
scene.cycles.samples=32
scene.render.film_transparent=True
scene.render.image_settings.color_mode='RGBA'
scene.render.threads_mode='FIXED';scene.render.threads=6
cam.data.type='PERSP'
cam.data.sensor_fit='HORIZONTAL'
cam.data.sensor_width=36
target=Vector((0,0,.86))
# 参考图 380×418 像素中的顶盖四角，以及黑色装置顶面三条可见交点。
points=[(-.710,.555,1.68),(.520,.555,1.68),(.520,-.435,1.68),(-.710,-.435,1.68),
        (-.75,.57,.32),(-.75,-.57,.32),(.75,-.57,.32)]
pixels=np.array([(73,49),(208,28),(272,94),(116,120),(64,277),(106,376),(294,333)],dtype=float)

def set_camera(p):
    yaw,elev,dist,lens,sx,sy,roll=p[:7]
    yaw,elev,roll=map(math.radians,(yaw,elev,roll))
    cam.location=target+Vector((math.sin(yaw)*math.cos(elev),-math.cos(yaw)*math.cos(elev),math.sin(elev)))*dist
    q=(target-cam.location).to_track_quat('-Z','Y')
    cam.rotation_euler=(q@Quaternion((0,0,1),roll)).to_euler()
    cam.data.lens=lens;cam.data.shift_x=sx;cam.data.shift_y=sy
    bpy.context.view_layer.update()

def projected(p):
    set_camera(p)
    result=[]
    fitted=[(-.095-p[8]/2,.06+p[9]/2,p[7]),(-.095+p[8]/2,.06+p[9]/2,p[7]),(-.095+p[8]/2,.06-p[9]/2,p[7]),(-.095-p[8]/2,.06-p[9]/2,p[7])]+points[4:]
    for xyz in fitted:
        v=world_to_camera_view(scene,cam,Vector(xyz))
        result.append((v.x*380,(1-v.y)*418))
    return np.array(result)

# 有界最小二乘；用屏幕角点作度量，不以参数猜测视角一致。
p=np.array([-18,41,6.5,75,0,0,0,2.2,1.1,.9],dtype=float)
lower=np.array([-45,25,6.5,25,-.4,-.4,-12,1.7,.85,.65])
upper=np.array([-5,65,6.5,180,.4,.4,12,3,1.4,1.3])
steps=np.array([.05,.05,.01,.05,.001,.001,.03,.002,.002,.002])
for iteration in range(65):
    pred=projected(p);error=(pred-pixels).ravel()
    jac=np.stack([((projected(p+np.eye(10)[i]*steps[i])-pred)/steps[i]).ravel() for i in range(10)],axis=1)
    jac[:,2]=0
    delta=np.linalg.lstsq(jac.T@jac+np.eye(10)*.008,-jac.T@error,rcond=None)[0]
    best=p.copy();bestloss=float(error@error)
    for alpha in (1,.5,.2,.05):
        candidate=np.clip(p+alpha*delta,lower,upper)
        e=(projected(candidate)-pixels).ravel();loss=float(e@e)
        if loss<bestloss:best,bestloss=candidate,loss
    if np.linalg.norm(best-p)<.00005:break
    p=best
pred=projected(p)
# 相机不能修复矮宽的模型比例；按拟合结果修订顶盖与中间高度。
heightscale=(p[7]-.32)/(1.68-.32)
for o in scene.objects:
    if o.type!='MESH' or o.parent is None:continue
    if '顶盖' in o.name and ('凹阶' in o.name or '亮棱' in o.name):
        for v in o.data.vertices:
            world=o.matrix_world@v.co
            world.x=-.095+(world.x+.095)*p[8]/1.23
            world.y=.06+(world.y-.06)*p[9]/.99
            world.z=p[7]+(world.z-1.68)*.72
            v.co=o.matrix_world.inverted()@world
    elif o.location.z>.42 or '凝晶' in o.name or '晶' in o.name and '托盘' not in o.name and '凹槽' not in o.name:
        o.location.z=.32+(o.location.z-.32)*heightscale
        o.scale.z*=heightscale
bpy.context.view_layer.update()
report={'status':'仅视角匹配，模型仍需同角度比对；未导入Unity','reference':'codex-clipboard-9a04e545-9755-4266-875f-6e451ab8be1f.png','parameters':p.tolist(),'corner_reference':pixels.tolist(),'corner_projection':pred.tolist(),'corner_error_pixels':np.linalg.norm(pred-pixels,axis=1).tolist(),'camera_location':list(cam.location),'camera_rotation':list(cam.rotation_euler),'lens':cam.data.lens}
(OUT/'camera_match.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
cam.name='参考图匹配相机_左前俯视'
scene['制作状态']='视角与结构比对中；未完成前禁止导入Unity'
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'NucleicCrystalSinter_ViewMatch.blend'),compress=True)
scene.render.filepath=str(OUT/'ReferenceView.png')
bpy.ops.render.render(write_still=True)
print('VIEW_MATCH',json.dumps(report,ensure_ascii=False))
