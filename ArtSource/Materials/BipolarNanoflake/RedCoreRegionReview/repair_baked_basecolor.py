"""以独立颜色通道烘焙核心，使用 Standard 显示转换写入 sRGB PNG。"""
import bpy, json, traceback
from pathlib import Path
import numpy as np
OUT=Path('F:/游城拓荒模拟器/ArtSource/Materials/BipolarNanoflake/RedCoreRegionReview')

def repair():
    scene=bpy.context.scene
    core=scene.objects['BNF_一体红盘与暖白薄片']
    restore=[]
    settings=(scene.view_settings.view_transform,scene.view_settings.look,scene.view_settings.exposure,scene.view_settings.gamma)
    try:
        (OUT/'texture_repair_progress.json').write_text('{"stage":"正在重烘焙基础色"}',encoding='utf-8')
        bpy.ops.object.select_all(action='DESELECT');core.select_set(True);bpy.context.view_layer.objects.active=core
        im=bpy.data.images['BipolarNanoflake_Core_BaseColor']
        for mat in core.data.materials:
            nt=mat.node_tree;p=next(n for n in nt.nodes if n.type=='BSDF_PRINCIPLED')
            restore.append((p,p.inputs['Metallic'].default_value,p.inputs['Emission Strength'].default_value))
            p.inputs['Metallic'].default_value=0;p.inputs['Emission Strength'].default_value=0
            for n in nt.nodes:n.select=False
            target=next(n for n in nt.nodes if n.type=='TEX_IMAGE' and n.image==im)
            target.select=True;nt.nodes.active=target
        scene.render.bake.use_clear=True;scene.render.bake.use_pass_direct=False
        scene.render.bake.use_pass_indirect=False;scene.render.bake.use_pass_color=True
        bpy.context.view_layer.update()
        bpy.ops.object.bake(type='DIFFUSE',pass_filter={'COLOR'})
        for p,metal,emission in restore:p.inputs['Metallic'].default_value=metal;p.inputs['Emission Strength'].default_value=emission
        restore.clear()
        ei=bpy.data.images['BipolarNanoflake_Core_Emission']
        for mat in core.data.materials:
            nt=mat.node_tree
            for n in nt.nodes:n.select=False
            target=next(n for n in nt.nodes if n.type=='TEX_IMAGE' and n.image==ei)
            target.select=True;nt.nodes.active=target
        bpy.ops.object.bake(type='EMIT')
        values=np.empty(1024*1024*4,dtype=np.float32);ei.pixels.foreach_get(values)
        values.reshape(-1,4)[:,:3]/=2.0
        ei.pixels.foreach_set(values)
        scene.view_settings.view_transform='Standard';scene.view_settings.look='None'
        scene.view_settings.exposure=0;scene.view_settings.gamma=1
        stats={}
        for label in ('BaseColor','Emission'):
            image=bpy.data.images['BipolarNanoflake_Core_'+label]
            data=np.empty(1024*1024*4,dtype=np.float32);image.pixels.foreach_get(data)
            stats[label]={'maximum':float(data.reshape(-1,4)[:,:3].max()),'mean':float(data.reshape(-1,4)[:,:3].mean())}
            assert stats[label]['maximum']>.1,label+'烘焙为空'
            image.colorspace_settings.name='Linear Rec.709'
            image.update()
            image.save_render(str(OUT/'Textures'/('BipolarNanoflake_Core_'+label+'.png')),scene=scene)
        manifest=json.loads((OUT/'finished_material_manifest.json').read_text(encoding='utf-8'))
        for name,entry in manifest['materials'].items():
            mat=bpy.data.materials[name];p=next(n for n in mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
            entry['color']=list(p.inputs['Base Color'].default_value)
            entry['emission']=[v*p.inputs['Emission Strength'].default_value for v in list(p.inputs['Emission Color'].default_value)[:3]]
        manifest['colorEncoding']='材质常量为线性；PNG经过Standard转换保存为sRGB；Emission图归一化乘2恢复强度'
        (OUT/'finished_material_manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
        (OUT/'texture_repair_progress.json').write_text(json.dumps({'stage':'修复完成','statistics':stats},ensure_ascii=False,indent=2),encoding='utf-8')
    except Exception:
        (OUT/'texture_repair_progress.json').write_text(json.dumps({'stage':'失败','error':traceback.format_exc()},ensure_ascii=False),encoding='utf-8')
    finally:
        for p,metal,emission in restore:p.inputs['Metallic'].default_value=metal;p.inputs['Emission Strength'].default_value=emission
        scene.view_settings.view_transform=settings[0];scene.view_settings.look=settings[1]
        scene.view_settings.exposure=settings[2];scene.view_settings.gamma=settings[3]
        bpy.ops.object.select_all(action='DESELECT')
        root=scene.objects['BipolarNanoflake_Finished_Root'];root.select_set(True);bpy.context.view_layer.objects.active=root
        bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'BipolarNanoflake_Finished.blend'),compress=True)
    return None
bpy.app.timers.register(repair,first_interval=.7)
print('TEXTURE_REPAIR_QUEUED',flush=True)
