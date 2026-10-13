import bpy,json
from pathlib import Path
from mathutils import Vector

OUT=Path(__file__).resolve().parent
def prepare():
    scene=bpy.data.scenes.get('固源岩_顶部坡度修正_待验收')
    if scene is None or bpy.context.window is None: return 1.0
    bpy.context.window.scene=scene
    scene.camera.location=(6,0,0)
    scene.camera.rotation_euler=Vector((-1,0,0)).to_track_quat('-Z','Y').to_euler()
    for area in bpy.context.screen.areas:
        if area.type!='VIEW_3D': continue
        space=area.spaces.active
        space.region_3d.view_rotation=scene.camera.rotation_euler.to_quaternion()
        space.region_3d.view_location=(0,0,0)
        space.region_3d.view_distance=2.9
        space.region_3d.view_perspective='ORTHO'
        space.shading.type='MATERIAL'
        space.shading.use_scene_lights=True; space.shading.use_scene_world=True
        space.overlay.show_overlays=False
    (OUT/'opened_in_blender.json').write_text(json.dumps({'文件':bpy.data.filepath,
        '活动场景':scene.name,'观察方式':'严格右侧正交，零俯仰、零滚转',
        '状态':'等待验收，未导入 Unity'},ensure_ascii=False,indent=2),encoding='utf-8')
    return None
bpy.app.timers.register(prepare,first_interval=1.0)
