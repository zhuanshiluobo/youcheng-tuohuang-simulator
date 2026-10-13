import bpy,json
from pathlib import Path
from mathutils import Vector

OUT=Path(__file__).resolve().parent
def prepare():
    scene=bpy.data.scenes.get('固源岩_底部与低位崩口_待验收')
    if scene is None or bpy.context.window is None: return 1.0
    bpy.context.window.scene=scene
    scene.camera.location=(2.70,-6.20,3.45)
    target=Vector((-.03,0,-.015))
    scene.camera.rotation_euler=(target-scene.camera.location).to_track_quat('-Z','Y').to_euler()
    for area in bpy.context.screen.areas:
        if area.type!='VIEW_3D': continue
        space=area.spaces.active
        space.region_3d.view_rotation=scene.camera.rotation_euler.to_quaternion()
        space.region_3d.view_location=target
        space.region_3d.view_distance=3.25
        space.region_3d.view_perspective='ORTHO'
        space.shading.type='MATERIAL'
        space.shading.use_scene_lights=True; space.shading.use_scene_world=True
        space.overlay.show_overlays=False
    (OUT/'opened_in_blender.json').write_text(json.dumps({'文件':bpy.data.filepath,
        '活动场景':scene.name,'观察方向':'正面，灰蓝面朝前、浅黄面在右','状态':'等待验收，未导入 Unity'},
        ensure_ascii=False,indent=2),encoding='utf-8')
    return None
bpy.app.timers.register(prepare,first_interval=1.0)
