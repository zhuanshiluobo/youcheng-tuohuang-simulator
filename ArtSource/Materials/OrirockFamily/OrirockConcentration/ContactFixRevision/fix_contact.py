"""只裁除右后岩柱与前方岩柱相交的内部体积，消除外露底面的深度冲突。"""
import bpy
import bmesh
import hashlib
import json
import struct
from pathlib import Path
from mathutils import Vector

OUT=Path(__file__).resolve().parent
KEY='OrirockConcentration'

def geometry_hash(obj):
    h=hashlib.sha256()
    for v in obj.data.vertices: h.update(struct.pack('3f',*v.co))
    for face in obj.data.polygons:
        for index in face.vertices: h.update(struct.pack('I',index))
    return h.hexdigest()

def cross(a,b): return a.x*b.y-a.y*b.x

def clip(poly,cutter):
    sign=1 if sum(cross(a,b) for a,b in zip(cutter,cutter[1:]+cutter[:1]))>0 else -1
    for a,b in zip(cutter,cutter[1:]+cutter[:1]):
        output=[]
        if not poly: return []
        prev=poly[-1]; pd=sign*cross(b-a,prev-a)
        for cur in poly:
            cd=sign*cross(b-a,cur-a)
            if (cd>=-1e-10)!=(pd>=-1e-10): output.append(prev+(cur-prev)*(pd/(pd-cd)))
            if cd>=-1e-10: output.append(cur)
            prev,pd=cur,cd
        poly=output
    return poly

def overlap_area(a,b):
    def bottom(obj):
        z=min(v.co.z for v in obj.data.vertices)
        return [[Vector((obj.data.vertices[i].co.x,obj.data.vertices[i].co.y)) for i in p.vertices]
                for p in obj.data.polygons if all(abs(obj.data.vertices[i].co.z-z)<1e-6 for i in p.vertices)]
    area=0
    for pa in bottom(a):
        for pb in bottom(b):
            polygon=clip(pa.copy(),pb)
            if len(polygon)>2:
                area+=abs(sum(cross(u,v) for u,v in zip(polygon,polygon[1:]+polygon[:1])))*.5
    return area

scene=bpy.context.scene
original_file=bpy.data.filepath
assert Path(original_file).name=='OrirockConcentration_UniformStone.blend',original_file
meshes=[o for o in scene.objects if o.type=='MESH']
front=next(o for o in meshes if '前方' in o.name)
right=next(o for o in meshes if '右后' in o.name)
left=next(o for o in meshes if '左后' in o.name)
before_hash={o.name:geometry_hash(o) for o in meshes}
before_overlap=overlap_area(front,right)
assert before_overlap>.001
view=[dict(rotation=list(a.spaces.active.region_3d.view_rotation),
           location=list(a.spaces.active.region_3d.view_location),
           distance=a.spaces.active.region_3d.view_distance,
           perspective=a.spaces.active.region_3d.view_perspective,
           shading=a.spaces.active.shading.type) for a in bpy.context.screen.areas if a.type=='VIEW_3D']

bpy.context.view_layer.objects.active=right
mod=right.modifiers.new('裁除与前方岩柱重叠的内部体积','BOOLEAN')
mod.operation='DIFFERENCE'
mod.solver='EXACT'
mod.object=front
bpy.ops.object.modifier_apply(modifier=mod.name)
bm=bmesh.new(); bm.from_mesh(right.data)
bmesh.ops.triangulate(bm,faces=list(bm.faces))
bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces))
boundary=sum(not e.is_manifold for e in bm.edges)
bm.to_mesh(right.data); bm.free()
right.data.update()
assert boundary==0,boundary
after_overlap=overlap_area(front,right)
assert after_overlap<1e-8,after_overlap
assert geometry_hash(front)==before_hash[front.name]
assert geometry_hash(left)==before_hash[left.name]

# 新增接触面必须有独立 UV 岛，避免和前方柱的法线/AO 烘焙重叠。
for obj in scene.objects: obj.select_set(obj in meshes)
bpy.context.view_layer.objects.active=right
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=.872664626,island_margin=.018)
bpy.ops.object.mode_set(mode='OBJECT')
for obj in scene.objects: obj.select_set(False)
scene.name='提纯源岩_接缝修正_统一材质'
right['接缝说明']='裁掉被前方岩柱遮住的相交体积，底面接缝无重叠面；可见外轮廓保持。'
bpy.context.preferences.filepaths.save_version=0
for area in bpy.context.screen.areas:
    if area.type=='VIEW_3D': area.tag_redraw()
OUT.mkdir(exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/(KEY+'_ContactReview.blend')))
report=dict(source=original_file,review_file=bpy.data.filepath,
    issue='前方与右后岩柱外露底面同高并交叠，产生 Z-fighting',
    bottom_overlap_before=before_overlap,bottom_overlap_after=after_overlap,
    front_and_left_geometry_unchanged=True,right_non_manifold_edges=boundary,
    original_view=view,triangles={o.name:len(o.data.polygons) for o in meshes})
(OUT/'contact_fix_info.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(report,ensure_ascii=False),flush=True)
