"""打开完成文件后显示正面材质视图，并记录实际窗口以确认交付。"""
import bpy
import json
from pathlib import Path

out=Path(__file__).resolve().parent

def ready():
    if Path(bpy.data.filepath).name!='OrirockConcentration_EdgeChips.blend':
        return None
    scene=bpy.context.scene
    for window in bpy.context.window_manager.windows:
        for area in window.screen.areas:
            if area.type=='VIEW_3D':
                space=area.spaces.active
                space.region_3d.view_rotation=scene.camera.rotation_euler.to_quaternion()
                space.region_3d.view_location=(0,0,0)
                space.region_3d.view_distance=2.7
                space.region_3d.view_perspective='ORTHO'
                space.overlay.show_overlays=False
                space.shading.type='MATERIAL'
                space.shading.use_scene_world=True
                space.shading.use_scene_lights=True
                area.tag_redraw()
    (out/'opened_for_review.json').write_text(json.dumps(dict(file=bpy.data.filepath,
        scene=scene.name,window_count=len(bpy.context.window_manager.windows)),ensure_ascii=False,indent=2),encoding='utf-8')
    bpy.app.timers.register(capture,first_interval=8)
    return None

def capture():
    try:
        bpy.ops.screen.screenshot(filepath=str(out/'Blender_验收窗口.png'))
    except Exception as error:
        print('验收窗口截图：',error,flush=True)
    return None

bpy.app.timers.register(ready,first_interval=3)
