"""打开本轮待验收文件后，定位右后方视角；不导出、不导入。"""
import bpy,json
from pathlib import Path

OUT=Path(__file__).resolve().parent

def prepare():
    scene=bpy.data.scenes.get('固源岩_大崩口与背面_待验收')
    if scene is None or bpy.context.window is None: return 1.0
    bpy.context.window.scene=scene
    cutters=bpy.data.collections.get('崩口切削参考_隐藏不导出')
    if cutters: cutters.hide_viewport=True; cutters.hide_render=True
    for area in bpy.context.screen.areas:
        if area.type!='VIEW_3D': continue
        space=area.spaces.active
        space.region_3d.view_rotation=scene.camera.rotation_euler.to_quaternion()
        space.region_3d.view_location=(0,0,0)
        space.region_3d.view_distance=3.45
        space.region_3d.view_perspective='ORTHO'
        space.shading.type='MATERIAL'
        space.shading.use_scene_lights=True; space.shading.use_scene_world=True
        space.overlay.show_overlays=False
    (OUT/'opened_in_blender.json').write_text(json.dumps({
        '文件':bpy.data.filepath,'活动场景':scene.name,'状态':'已在 Blender 窗口打开，等待用户验收；未导入 Unity'
    },ensure_ascii=False,indent=2),encoding='utf-8')
    return None

bpy.app.timers.register(prepare,first_interval=1.0)
