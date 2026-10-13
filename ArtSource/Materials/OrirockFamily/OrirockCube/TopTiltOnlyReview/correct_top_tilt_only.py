"""回退到统一岩色版，只调整顶面高度；保留原正面相机及所有崩口顶点。"""
import bpy, bmesh, json
import numpy as np
from pathlib import Path
from mathutils import Vector, kdtree

OUT=Path(__file__).resolve().parent
SOURCE=OUT.parent/'UnifiedStoneReview/OrirockCube_UnifiedStoneReview.blend'
REGIONS=OUT.parent/'LowerNotchReview/OrirockCube_LowerNotchReview.blend'
BLEND=OUT/'OrirockCube_TopTiltOnlyReview.blend'

def main():
    bpy.ops.wm.open_mainfile(filepath=str(SOURCE))
    scene=bpy.data.scenes['固源岩_连续岩色与竖直立面_待验收']
    bpy.context.window.scene=scene
    scene.name='固源岩_仅顶部倾斜修正_待验收'
    body=scene.objects['固源岩_连续岩质主体']
    body.data=body.data.copy()
    original=[v.co.copy() for v in body.data.vertices]
    camera=scene.camera
    saved_camera=(camera.location.copy(),camera.rotation_euler.copy(),camera.data.ortho_scale)
    # 原材质分区仅用来定位顶面与崩口；模型几何和材质全部沿用 SOURCE。
    with bpy.data.libraries.load(str(REGIONS),link=False) as (available,loaded):
        loaded.objects=['固源岩_低位崩口与不规则底部']
    old=loaded.objects[0].data
    if len(old.polygons)!=len(body.data.polygons) or any(set(a.vertices)!=set(b.vertices) for a,b in zip(old.polygons,body.data.polygons)):
        raise RuntimeError('区域索引不匹配，未修改模型。')
    top={i for p in old.polygons if p.material_index==0 for i in p.vertices}
    side={i for p in old.polygons if p.material_index==3 for i in p.vertices}
    # 前面剥落断层、两个大崩口、背面小崩口、底部崩边：全部固定。
    protected={i for p in old.polygons if p.material_index in {1,6,7,8,9} for i in p.vertices}
    edge=sorted(top & side)
    coords=np.array([[original[i].y,1] for i in edge])
    slope,offset=np.linalg.lstsq(coords,np.array([original[i].z for i in edge]),rcond=None)[0]
    front=min(edge,key=lambda i:original[i].y)
    pivot_y=original[front].y
    # 以前端已经完成的崩口接点为支点，消去整体坡度，不把岩面原有起伏压成平板。
    tree=kdtree.KDTree(len(protected))
    for j,i in enumerate(protected): tree.insert(original[i],j)
    tree.balance()
    changed=[]
    for i in sorted(top-protected):
        p=original[i]
        distance=tree.find(p)[2]
        t=max(0,min(1,distance/.12))
        weight=t*t*(3-2*t)
        if i in edge: weight=1.0
        dz=-float(slope)*(p.y-pivot_y)*weight
        if abs(dz)>1e-8:
            body.data.vertices[i].co.z=p.z+dz
            changed.append(i)
    bm=bmesh.new(); bm.from_mesh(body.data)
    bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
    bm.to_mesh(body.data); bm.free(); body.data.update()
    actual_changed={i for i,(v,p) in enumerate(zip(body.data.vertices,original)) if (v.co-p).length>1e-8}
    report={'恢复基准':str(SOURCE),'当前文件':str(BLEND),'状态':'等待 Blender 验收，未导入 Unity',
        '修改内容':'仅顶面非崩口顶点的 Z 高度；以原前缘接点为支点消除整体坡度',
        '修改顶点数量':len(actual_changed),
        'X_Y_发生修改的顶点数':sum(v.co.x!=p.x or v.co.y!=p.y for v,p in zip(body.data.vertices,original)),
        '崩口顶点修改数量':len(actual_changed & protected),
        '非顶部顶点修改数量':len(actual_changed-top),
        '侧面顶部边缘高度_修改前':[original[i].z for i in edge],
        '侧面顶部边缘高度_修改后':[body.data.vertices[i].co.z for i in edge],
        '保留正面相机':list(saved_camera[0]),'保留相机缩放':saved_camera[2]}
    body['本轮修正']='恢复统一岩色版；只修改顶部整体坡度。正面视角、崩口和其它几何保留。'
    body['当前状态']='待用户 Blender 验收，未导入 Unity。'
    # 直接沿用源文件已保存的正面视口、相机、灯光、材质与显示设置。
    (OUT/'review_status.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND))
    scene.render.filepath=str(OUT/'front.png')
    print('RENDER front',flush=True); bpy.ops.render.render(write_still=True)
    camera.location=(6,0,0)
    camera.rotation_euler=Vector((-1,0,0)).to_track_quat('-Z','Y').to_euler()
    scene.render.filepath=str(OUT/'right_ortho.png')
    print('RENDER right_ortho',flush=True); bpy.ops.render.render(write_still=True)
    camera.location=saved_camera[0]; camera.rotation_euler=saved_camera[1]; camera.data.ortho_scale=saved_camera[2]
    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND))
    print('TOP_TILT_ONLY_READY',json.dumps(report,ensure_ascii=False),flush=True)

if __name__=='__main__': main()
