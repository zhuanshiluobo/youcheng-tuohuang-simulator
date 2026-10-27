"""按最新验收意见制作三面内凹散热槽，并加深中央凹槽。"""
import bpy,bmesh,math,json
from pathlib import Path
from mathutils import Vector

OUT=Path(__file__).resolve().parent
FILE=OUT/'CrystallineElectronicUnit_MaterialReview.blend'

def apply_revision(save=True):
    scene=bpy.data.scenes['晶体电子单元_材质验收']
    white=bpy.data.scenes['晶体电子单元_完整白模验收']
    if bpy.context.window:bpy.context.window.scene=scene
    for wm in bpy.data.window_managers:
        for window in wm.windows:window.scene=scene
    scene.view_layers[0].update()
    white.view_layers[0].update()
    if save:
        backup=OUT/'CrystallineElectronicUnit_BeforeRecessedVentsAndDeepBasin.blend'
        if not backup.exists():bpy.ops.wm.save_as_mainfile(filepath=str(backup),copy=True)
    by_source={o.get('白模对象'):o for o in scene.objects if o.get('白模对象')}
    body=by_source['上下等边_直八棱柱箱体']
    baseline=white.objects['上下等边_直八棱柱箱体']
    body.data=baseline.data.copy()
    body.data.name='箱壳_三面内嵌散热槽'
    body.modifiers.clear()
    # 中央内腔让开新的深槽内衬，避免槽壁与旧内壁重面。
    for vertex in list(body.data.vertices)[16:32]:
        radius=math.hypot(vertex.co.x,vertex.co.y)
        vertex.co.x*=1.14/radius;vertex.co.y*=1.14/radius

    bronze=bpy.data.materials['箱体_暗黄铜']
    rim=bpy.data.materials['边沿_浅香槟金属']
    black=bpy.data.materials['铭牌槽底_炭黑']
    dark=bpy.data.materials.get('散热槽_内壁暗金属')
    if dark is None:
        dark=bronze.copy();dark.name='散热槽_内壁暗金属'
        shader=next(n for n in dark.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
        shader.inputs['Base Color'].default_value=(.035,.030,.020,1)
        dark.diffuse_color=(.035,.030,.020,1)
    body.data.materials.clear()
    for mat in (bronze,dark,rim,black):body.data.materials.append(mat)
    for face in body.data.polygons:face.material_index=2 if face.index in (0,3,5) else 0

    # 移除该型号原先的外凸格栅模型对象；白模档案场景保留。
    obsolete=['散热面_00_槽底','散热面_00_端边_-0.5842544897430109','散热面_00_端边_0.5842544897430109']
    obsolete += ['散热面_00_横齿_'+str(i) for i in range(1,9)]
    for name in obsolete:
        obj=by_source.get(name)
        if obj is not None:bpy.data.objects.remove(obj,do_unlink=True)

    vertices=[];faces=[]
    length=1.14;height=.040;depth=.065
    frames={i:by_source[f'侧面_{i:02d}_局部基准'] for i in (0,3,5)}
    for i,frame in frames.items():
        for row in range(7):
            center=.1505+row*.077
            start=len(vertices)
            # 局部Y正向朝箱内；仅在外壳下挖，不加贴面或外凸百叶。
            local=[(-length/2,-.07,center-height/2),(length/2,-.07,center-height/2),
                   (length/2,depth,center-height/2),(-length/2,depth,center-height/2),
                   (-length/2,-.07,center+height/2),(length/2,-.07,center+height/2),
                   (length/2,depth,center+height/2),(-length/2,depth,center+height/2)]
            vertices.extend(frame.matrix_world@Vector(p) for p in local)
            faces.extend(tuple(start+j for j in f) for f in [(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)])
    data=bpy.data.meshes.new('散热槽切削工具_临时网格');data.from_pydata(vertices,[],faces);data.update()
    bm=bmesh.new();bm.from_mesh(data);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(data);bm.free()
    for mat in (bronze,dark,rim,black):data.materials.append(mat)
    for face in data.polygons:face.material_index=1
    cutter=bpy.data.objects.new('散热槽切削工具_临时',data);scene.collection.objects.link(cutter)
    mod=body.modifiers.new('三面真实内嵌槽','BOOLEAN')
    mod.operation='DIFFERENCE';mod.object=cutter
    options=[e.identifier for e in mod.bl_rna.properties['solver'].enum_items]
    if 'EXACT' in options:mod.solver='EXACT'
    scene.view_layers[0].objects.active=body
    with bpy.context.temp_override(scene=scene,view_layer=scene.view_layers[0],object=body,active_object=body):
        bpy.ops.object.modifier_apply(modifier=mod.name)
    bpy.data.objects.remove(cutter,do_unlink=True)
    # 槽后壁暗一些，凹口侧壁保留金属响应。
    for face in body.data.polygons:
        for frame in frames.values():
            point=frame.matrix_world.inverted()@(body.matrix_world@face.center)
            if abs(point.x)<length/2+.0001 and abs(point.y-depth)<.0001 and .12<point.z<.65:
                face.material_index=3
    bevel=body.modifiers.new('槽口与箱体微倒角','BEVEL');bevel.width=.003;bevel.segments=2

    floor=by_source['中央凹口封闭槽底']
    original_floor=white.objects['中央凹口封闭槽底']
    top=by_source['顶部白模_整体']
    deck=by_source['八角台面_宽斜面凹口']
    previous_floor_top=max(v.co.z for v in original_floor.data.vertices)+original_floor.location.z
    floor_top=.650
    lower=previous_floor_top-floor_top
    floor.location=original_floor.location.copy();floor.location.z-=lower
    top.location=white.objects['顶部白模_整体'].location.copy();top.location.z-=lower
    for i in range(24,32):deck.data.vertices[i].co.z=floor_top+.007
    for i in range(32,40):deck.data.vertices[i].co.z=floor_top-.010
    deck.data.update()
    scene.camera=by_source['参考角度']
    scene['散热槽修正']='面0、3、5各七条内凹横槽；面4正后面保留。'
    scene['中央凹槽深度']=.94-floor_top
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type=='VIEW_3D':
                sp=area.spaces.active;sp.show_region_ui=False;sp.overlay.show_overlays=False
                sp.shading.type='MATERIAL';sp.shading.use_scene_lights=True;sp.shading.use_scene_world=True
                sp.region_3d.view_rotation=scene.camera.rotation_euler.to_quaternion()
                sp.region_3d.view_perspective='ORTHO';sp.region_3d.view_location=(0,0,1.20);sp.region_3d.view_distance=5.65
    info={'vent_faces':[0,3,5],'vent_rows_each':7,'vent_depth':depth,'vent_height':height,'vent_length':length,
          'vent_pitch':.077,'vent_lips_flush_with_housing':True,'basin_top_z':.94,'basin_floor_z':floor_top,
          'basin_depth_before':.94-previous_floor_top,'basin_depth_after':.94-floor_top,'layers_lowered_by':lower,
          'original_white_archive_unchanged':True,'post_positions_unchanged':True}
    if save:
        notes_path=OUT/'material_review_notes.json'
        notes=json.loads(notes_path.read_text(encoding='utf-8'))
        notes['latest_geometry_revision']=info
        notes['geometry']='主体沿用已验收白模；按最新意见将前侧及左右后侧改为内凹散热槽，中央凹槽加深并同步下沉整组层片。'
        notes['mesh_vertices_preserved']=False
        notes_path.write_text(json.dumps(notes,ensure_ascii=False,indent=2),encoding='utf-8')
        bpy.ops.wm.save_as_mainfile(filepath=str(FILE))
    print('RECESSED_VENTS_AND_DEEP_BASIN_COMPLETE',json.dumps(info,ensure_ascii=False))
    print('BODY_FACES',len(body.data.polygons),'FRAMES',[(i,tuple(f.matrix_world.translation)) for i,f in frames.items()])
    return info

if __name__=='__main__':
    if bpy.app.background:bpy.ops.wm.open_mainfile(filepath=str(FILE))
    elif Path(bpy.data.filepath)!=FILE:raise RuntimeError('活动文件不匹配')
    apply_revision()
