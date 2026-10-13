"""修正用户侧视标注的顶部坡度与上端收缩；沿用连续岩色版本。"""
import bpy, bmesh, json
from pathlib import Path
from mathutils import Vector, noise

OUT=Path(__file__).resolve().parent
SOURCE=OUT.parent/'UnifiedStoneReview/OrirockCube_UnifiedStoneReview.blend'
GROUP_SOURCE=OUT.parent/'LowerNotchReview/OrirockCube_LowerNotchReview.blend'
BLEND=OUT/'OrirockCube_LevelCrownReview.blend'

def smoothstep(lo,hi,value):
    t=max(0,min(1,(value-lo)/(hi-lo)))
    return t*t*(3-2*t)

def camera_view(scene,pos,target=(0,0,0),viewport=False):
    camera=scene.camera
    camera.location=pos
    camera.rotation_euler=(Vector(target)-camera.location).to_track_quat('-Z','Y').to_euler()
    camera.data.ortho_scale=2.05
    if viewport:
        for area in bpy.context.screen.areas:
            if area.type!='VIEW_3D': continue
            space=area.spaces.active
            space.region_3d.view_rotation=camera.rotation_euler.to_quaternion()
            space.region_3d.view_location=target
            space.region_3d.view_distance=2.9
            space.region_3d.view_perspective='ORTHO'
            space.shading.type='MATERIAL'
            space.shading.use_scene_lights=True; space.shading.use_scene_world=True
            space.overlay.show_overlays=False

def main():
    bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
    scene=bpy.data.scenes['固源岩_连续岩色与竖直立面_待验收']
    bpy.context.window.scene=scene
    scene.name='固源岩_顶部坡度修正_待验收'
    body=scene.objects['固源岩_连续岩质主体']
    body.data=body.data.copy()
    body.data.name='固源岩_水平顶冠与竖直轮廓'
    # 上轮未改变拓扑。只从前一阶段读取顶冠区域标识，不使用旧几何覆盖新版。
    with bpy.data.libraries.load(str(GROUP_SOURCE),link=False) as (available,loaded):
        loaded.objects=['固源岩_低位崩口与不规则底部']
    groups=loaded.objects[0].data
    if len(groups.vertices)!=len(body.data.vertices) or len(groups.polygons)!=len(body.data.polygons):
        raise RuntimeError('区域来源拓扑不匹配，停止修改。')
    for a,b in zip(groups.polygons,body.data.polygons):
        if set(a.vertices)!=set(b.vertices): raise RuntimeError('区域来源顶点索引不匹配，停止修改。')
    cap_ids={i for p in groups.polygons if p.material_index==0 for i in p.vertices}
    side_ids={i for p in groups.polygons if p.material_index==3 for i in p.vertices}
    upper_edge=sorted(cap_ids & side_ids)
    original=[v.co.copy() for v in body.data.vertices]
    before=[list(original[i]) for i in upper_edge]
    # 顶部参考平面固定为水平；只保留毫米级石质起伏。
    # 较低的剥落唇口渐变过渡，真实崩口内部与底部不参与拉平。
    for i in cap_ids:
        v=body.data.vertices[i]
        w=smoothstep(.40,.55,original[i].z)
        grain=noise.noise_vector(Vector((original[i].x*9,original[i].y*9,2.3)),noise_basis='PERLIN_ORIGINAL').x*.003
        target=.61+grain
        v.co.z=original[i].z*(1-w)+target*w
    # 上次修正了中部平均立面，仍留下顶端向内收缩。
    # 将上端前缘与下部主立面的基准对齐，避免侧面仍呈斜梯形。
    for v,old in zip(body.data.vertices,original):
        w=smoothstep(.27,.53,old.z)*smoothstep(.28,.50,-old.y)
        if w:
            grain=noise.noise_vector(Vector((old.x*12,3.1,old.z*12)),noise_basis='PERLIN_ORIGINAL').x*.002
            v.co.y=old.y*(1-w)+(-.625+grain)*w
    # 侧面主岩壁保持竖直，真实切入岩体的凹面不投影到外壁。
    for i in side_ids:
        v=body.data.vertices[i]
        grain=noise.noise_vector(Vector((2.8,v.co.y*16,v.co.z*16)),noise_basis='PERLIN_ORIGINAL').x*.002
        v.co.x=.628+grain
    bm=bmesh.new(); bm.from_mesh(body.data)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bm.to_mesh(body.data); bm.free(); body.data.update()
    body['本轮修正']='水平顶冠；顶部前缘对齐竖直主立面；侧面外壁竖直；保留局部崩口与不规则底部。'
    body['默认观察']='严格右侧正交，零俯仰、零滚转，可直接检查顶部坡度。'
    body['当前状态']='待 Blender 验收，未导入 Unity。'
    camera_view(scene,(6,0,0),viewport=True)
    scene.cycles.samples=40
    scene.render.resolution_x=scene.render.resolution_y=900
    bpy.context.view_layer.update()
    bm=bmesh.new(); bm.from_mesh(body.data); body.data.calc_loop_triangles()
    after=[list(body.data.vertices[i].co) for i in upper_edge]
    report={'源文件':str(SOURCE),'当前文件':str(BLEND),'状态':'待验收，未导入 Unity',
        '修正':'顶冠水平，顶端前缘与主立面基准对齐，侧面主岩壁竖直',
        '侧面顶部边缘_修改前':before,'侧面顶部边缘_修改后':after,
        '顶部边缘高度差_修改前':max(p[2] for p in before)-min(p[2] for p in before),
        '顶部边缘高度差_修改后':max(p[2] for p in after)-min(p[2] for p in after),
        '三角面':len(body.data.loop_triangles),'非流形边':sum(not e.is_manifold for e in bm.edges)}
    bm.free()
    (OUT/'review_status.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND))
    for name,pos,target in [('right_ortho',(6,0,0),(0,0,0)),
        ('front',(2.70,-6.20,3.45),(-.03,0,-.015)),('rear',(-2.7,6.2,2.8),(0,0,0))]:
        camera_view(scene,pos,target)
        scene.render.filepath=str(OUT/(name+'.png'))
        print('RENDER',name,flush=True); bpy.ops.render.render(write_still=True)
    camera_view(scene,(6,0,0),viewport=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND))
    print('LEVEL_CROWN_READY',json.dumps(report,ensure_ascii=False),flush=True)

if __name__=='__main__': main()
