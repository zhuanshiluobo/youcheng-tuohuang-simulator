"""在当前完成场景中烘焙核心，导出限定模型与真实材质映射。"""
import bpy, json, traceback
from pathlib import Path
from mathutils import Vector
import numpy as np

OUT=Path('F:/游城拓荒模拟器/ArtSource/Materials/BipolarNanoflake/RedCoreRegionReview')
TEX=OUT/'Textures';TEX.mkdir(exist_ok=True)
STATE=OUT/'finish_progress.json'

def state(stage,**extra):
    STATE.write_text(json.dumps({'stage':stage,**extra},ensure_ascii=False,indent=2),encoding='utf-8')

def run():
    try:
        scene=bpy.context.scene
        assert scene.name=='BipolarNanoflake_ReferenceFinished'
        root=scene.objects['BipolarNanoflake_Finished_Root']
        core=scene.objects['BNF_一体红盘与暖白薄片']
        bpy.ops.object.select_all(action='DESELECT');core.select_set(True);bpy.context.view_layer.objects.active=core
        scene.cycles.samples=16;scene.render.bake.use_clear=True
        scene.render.bake.margin=8
        outputs=[]
        state('烘焙核心基础色')
        im=bpy.data.images['BipolarNanoflake_Core_BaseColor']
        for mat in core.data.materials:
            nt=mat.node_tree;bsdf=next(n for n in nt.nodes if n.type=='BSDF_PRINCIPLED')
            out=next(n for n in nt.nodes if n.type=='OUTPUT_MATERIAL')
            previous=out.inputs['Surface'].links[0].from_socket
            temp=nt.nodes.new('ShaderNodeEmission')
            source=bsdf.inputs['Base Color']
            if source.is_linked:nt.links.new(source.links[0].from_socket,temp.inputs['Color'])
            else:temp.inputs['Color'].default_value=source.default_value
            temp.inputs['Strength'].default_value=1
            nt.links.new(temp.outputs[0],out.inputs['Surface'])
            outputs.append((nt,out,previous,temp))
            tex=next(n for n in nt.nodes if n.type=='TEX_IMAGE' and n.image==im)
            nt.nodes.active=tex
        bpy.ops.object.bake(type='EMIT')
        for nt,out,previous,temp in outputs:
            nt.links.new(previous,out.inputs['Surface']);nt.nodes.remove(temp)
        im.filepath_raw=str(TEX/'BipolarNanoflake_Core_BaseColor.png');im.file_format='PNG';im.save()
        state('烘焙核心发光')
        ei=bpy.data.images['BipolarNanoflake_Core_Emission']
        for mat in core.data.materials:
            tex=next(n for n in mat.node_tree.nodes if n.type=='TEX_IMAGE' and n.image==ei)
            mat.node_tree.nodes.active=tex
        bpy.ops.object.bake(type='EMIT')
        pixels=np.empty(1024*1024*4,dtype=np.float32);ei.pixels.foreach_get(pixels)
        arr=pixels.reshape(-1,4);actual_max=float(arr[:,:3].max())
        emission_scale=2.0
        arr[:,:3]/=emission_scale
        ei.pixels.foreach_set(pixels)
        ei.filepath_raw=str(TEX/'BipolarNanoflake_Core_Emission.png');ei.file_format='PNG';ei.save()
        state('导出模型与材质',emissionMaximum=actual_max)
        meshes=[o for o in root.children_recursive if o.type=='MESH']
        axis=Vector(root['共同轴方向']).normalized();center=Vector(root['共同中心'])
        records=[];materials={}
        for obj in meshes:
            own=(obj.matrix_world.to_3x3().normalized()@Vector((1,0,0))).normalized()
            offset=obj.matrix_world.translation-center
            distance=(offset-axis*offset.dot(axis)).length
            records.append({'name':obj.name,'position':list(obj.location),'rotationEuler':list(obj.rotation_euler),
                            'axis':list(own),'axisAgreement':abs(own.dot(axis)),'axisDistance':distance,
                            'materialSlots':[m.name for m in obj.data.materials]})
            assert abs(own.dot(axis))>.99999 and distance<.00001,obj.name
            for mat in obj.data.materials:
                p=next(n for n in mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
                c=list(p.inputs['Base Color'].default_value)
                e=list(p.inputs['Emission Color'].default_value)[:3]
                strength=float(p.inputs['Emission Strength'].default_value)
                materials[mat.name]={'color':c,'metallic':float(p.inputs['Metallic'].default_value),
                    'smoothness':1-float(p.inputs['Roughness'].default_value),
                    'emission':[v*strength for v in e],'transparent':False,
                    'shaderType':'Standard' if obj==core else 'ExhibitPBR',
                    'coreTexture':obj==core,'emissionTextureScale':emission_scale if obj==core else 1}
        points=[o.matrix_world@Vector(v) for o in meshes for v in o.bound_box]
        lo=[min(p[i] for p in points) for i in range(3)];hi=[max(p[i] for p in points) for i in range(3)]
        manifest={'material':'双极纳米片','revision':'同心共轴参考完成版','sourceBlend':str(OUT/'BipolarNanoflake_Finished.blend'),
            'sharedCenter':list(center),'sharedRotationAxis':list(axis),'objects':records,'materials':materials,
            'bounds':{'min':lo,'max':hi,'size':[b-a for a,b in zip(lo,hi)]},
            'textures':{'BaseColor':str(TEX/'BipolarNanoflake_Core_BaseColor.png'),
                        'Emission':str(TEX/'BipolarNanoflake_Core_Emission.png')},
            'emissionTextureScale':emission_scale,'colorEncoding':'材质常量为线性；PNG为sRGB',
            'bakedCoreMaterials':[m.name for m in core.data.materials],
            'export':{'axisForward':'-Z','axisUp':'Y','previewObjectsExcluded':True},
            'excluded':'外金色稀有度圈、灰色背景、粒子、悬空光晕、电路板',
            'backside':'根据薄片结构合理补齐，单张原图无法核实。','reviewStatus':'Blender完成，Unity待同步'}
        (OUT/'finished_material_manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
        bpy.ops.object.select_all(action='DESELECT')
        for obj in [root]+meshes:obj.select_set(True)
        bpy.context.view_layer.objects.active=root
        bpy.ops.export_scene.fbx(filepath=str(OUT/'BipolarNanoflake_Finished.fbx'),use_selection=True,
            object_types={'EMPTY','MESH'},use_mesh_modifiers=True,add_leaf_bones=False,axis_forward='-Z',axis_up='Y',
            apply_unit_scale=True,bake_space_transform=False,mesh_smooth_type='FACE',use_tspace=True,path_mode='STRIP')
        bpy.ops.object.select_all(action='DESELECT');root.select_set(True)
        scene.cycles.samples=48
        scene['完成阶段']='几何、核心UV、基础色/发光烘焙与限定FBX导出完成'
        bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'BipolarNanoflake_Finished.blend'),compress=True)
        state('烘焙与导出完成',fbx=str(OUT/'BipolarNanoflake_Finished.fbx'),meshCount=len(meshes),
              materials=len(materials),emissionMaximum=actual_max,sharedAxisVerified=True)
    except Exception:
        state('失败',error=traceback.format_exc())
    return None

state('已排队')
bpy.app.timers.register(run,first_interval=.7)
print('BAKE_EXPORT_QUEUED',flush=True)
