"""保存本次真实模型渲染；恢复编辑参考相机与完成场景。"""
import bpy, json, traceback
from pathlib import Path
from mathutils import Vector
OUT=Path('F:/游城拓荒模拟器/ArtSource/Materials/BipolarNanoflake/RedCoreRegionReview')

def render_views():
    s=bpy.context.scene;c=s.camera
    location=c.location.copy();rotation=c.rotation_euler.copy();scale=c.data.ortho_scale
    samples=s.cycles.samples
    try:
        root=s.objects['BipolarNanoflake_Finished_Root']
        axis=Vector(root['共同轴方向']);center=Vector(root['共同中心'])
        views=[('整体',location,2.62,48),('核心近景',location,.91,48),
               ('同轴侧视',center+axis*6,2.48,32),('背面',center-location,2.62,32)]
        for label,pos,ortho,count in views:
            (OUT/'render_progress.json').write_text(json.dumps({'stage':'渲染'+label},ensure_ascii=False),encoding='utf-8')
            c.location=pos;c.rotation_euler=(center-c.location).to_track_quat('-Z','Y').to_euler()
            c.data.ortho_scale=ortho;s.cycles.samples=count
            s.render.film_transparent=True;s.render.image_settings.file_format='PNG';s.render.image_settings.color_mode='RGBA'
            s.render.filepath=str(OUT/('双极纳米片_完成版_'+label+'.png'))
            bpy.ops.render.render(write_still=True)
        (OUT/'render_progress.json').write_text(json.dumps({'stage':'四视图完成'},ensure_ascii=False),encoding='utf-8')
    except Exception:
        (OUT/'render_progress.json').write_text(json.dumps({'stage':'失败','error':traceback.format_exc()},ensure_ascii=False),encoding='utf-8')
    finally:
        c.location=location;c.rotation_euler=rotation;c.data.ortho_scale=scale;s.cycles.samples=samples
        bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'BipolarNanoflake_Finished.blend'),compress=True)
    return None
bpy.app.timers.register(render_views,first_interval=.7)
print('RENDER_VIEWS_QUEUED',flush=True)
