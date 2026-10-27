"""在 Blender 中运行，创建独立的 D32钢白模场景。"""

import bpy
import math
import json
from pathlib import Path
from mathutils import Vector, Euler

OUTPUT_DIR = Path(__file__).resolve().parent
SCENE_NAME = "D32钢_白模"

# 每次运行创建新场景，保留原场景与其他模型。
scene = bpy.data.scenes.new(SCENE_NAME)
bpy.context.window.scene = scene
scene.unit_settings.system = 'METRIC'
scene.unit_settings.scale_length = 1.0
model_collection = bpy.data.collections.new("D32钢_模型")
scene.collection.children.link(model_collection)
stage_collection = bpy.data.collections.new("D32钢_预览")
scene.collection.children.link(stage_collection)

root = bpy.data.objects.new("D32Steel_Root", None)
model_collection.objects.link(root)
root["参考来源"] = "https://prts.wiki/w/D32钢"
root["制作阶段"] = "白模：金属带簇、内部宽带与外部细环；背面为推定造型"

white = bpy.data.materials.new("D32钢_白模材质")
white.diffuse_color = (0.77, 0.79, 0.81, 1.0)
white.use_nodes = True
shader = next(n for n in white.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
shader.inputs['Base Color'].default_value = (0.77, 0.79, 0.81, 1.0)
shader.inputs['Roughness'].default_value = 0.64
shader.inputs['Metallic'].default_value = 0.0

meshes = []

def ribbon(name, radius, squash, orientation, width, start=0.0,
           extent=math.tau, thickness=0.015, twist=0.0, phase=0.0,
           waviness=0.0, taper=False, center=(0, 0, 0), segments=144):
    """有厚度的曲面带；宽度方向可扭转，开口带的两端收尖。"""
    rotation = Euler(tuple(math.radians(v) for v in orientation), 'XYZ').to_matrix()
    offset = Vector(center)
    vertices, faces = [], []
    closed = abs(extent - math.tau) < 1e-6 and not taper
    count = segments if closed else segments + 1
    for i in range(count):
        fraction = i / segments
        angle = start + extent * fraction
        r = radius * (1 + waviness * math.sin(3 * angle + phase))
        point = Vector((r * math.cos(angle), r * squash * math.sin(angle),
                        0.06 * radius * math.sin(2 * angle + phase)))
        tangent = Vector((-math.sin(angle), squash * math.cos(angle),
                          0.12 * math.cos(2 * angle + phase))).normalized()
        outward = Vector((math.cos(angle), math.sin(angle) / squash, 0)).normalized()
        outward = (outward - tangent * outward.dot(tangent)).normalized()
        normal = tangent.cross(outward).normalized()
        roll = twist + 0.65 * math.sin(angle * 2 + phase)
        across = (outward * math.cos(roll) + normal * math.sin(roll)).normalized()
        local_width = width * (0.84 + 0.16 * math.sin(angle + phase))
        if taper:
            local_width *= 0.055 + 0.945 * math.sin(math.pi * fraction) ** 0.6
        for sign in (-1, 1):
            vertices.append(rotation @ (point + sign * local_width * 0.5 * across) + offset)
    for i in range(segments):
        j = (i + 1) % count
        faces.append((2*i, 2*j, 2*j+1, 2*i+1))
    mesh = bpy.data.meshes.new(name + "_网格")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    model_collection.objects.link(obj)
    obj.parent = root
    obj.data.materials.append(white)
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    solid = obj.modifiers.new("带体厚度", 'SOLIDIFY')
    solid.thickness = thickness
    solid.offset = 0.0
    solid.use_even_offset = True
    bevel = obj.modifiers.new("边缘微倒角", 'BEVEL')
    bevel.width = min(thickness * 0.32, 0.006)
    bevel.segments = 2
    meshes.append(obj)
    return obj

# 中层宽带形成内部曲面和空腔，不放置实体球芯。
main_bands = [
    ("宽带_01_纵向折转", 0.77, 1.16, (78, 18, 24), 0.37, -1.7, 5.45, 0.25, 0.5),
    ("宽带_02_横向翻卷", 0.79, 1.05, (32, 68, -18), 0.32, -0.5, 5.2, -0.25, 1.3),
    ("宽带_03_侧向内弯", 0.80, 0.97, (54, -35, 68), 0.29, -2.0, 5.7, 0.35, 2.3),
    ("宽带_04_后侧层叠", 0.81, 1.04, (-48, 21, -48), 0.31, 0.15, 5.25, -0.3, 3.2),
    ("宽带_05_下方回环", 0.73, 1.14, (12, 42, 12), 0.29, -2.6, 5.5, 0.5, 4.1),
    ("宽带_06_顶部弧片", 0.85, 0.89, (69, 62, 42), 0.25, 0.45, 4.85, -0.5, 2.7),
]
for name, radius, squash, rotation, width, start, extent, twist, phase in main_bands:
    ribbon(name, radius, squash, rotation, width, start, extent,
           thickness=0.021, twist=twist, phase=phase, waviness=0.025, taper=True)

# 内部较短的弯曲片：让中心有交叉的斜向尖端和宽窄变化。
inner_bands = [
    ("内带_01_正面斜片", 0.52, 1.2, (80, 6, 36), 0.36, -2.7, 3.65, 0.20, 0.0, (0.03, -0.16, 0.04)),
    ("内带_02_右侧折片", 0.59, 0.9, (41, -62, 8), 0.27, -0.8, 4.1, -0.60, 1.8, (0.08, 0.02, -0.02)),
    ("内带_03_中间竖片", 0.49, 1.05, (73, 49, -32), 0.31, -2.0, 3.9, 0.40, 3.5, (-0.1, -0.08, 0.03)),
    ("内带_04_背面弧片", 0.60, 1.03, (-27, 33, 87), 0.25, 0.3, 3.95, -0.30, 4.8, (0.02, 0.08, -0.05)),
]
for name, radius, squash, rotation, width, start, extent, twist, phase, center in inner_bands:
    ribbon(name, radius, squash, rotation, width, start, extent,
           thickness=0.018, twist=twist, phase=phase, taper=True, center=center, segments=112)

# 外层较宽的环带和细环，使轮廓接近图标中的缠绕球簇。
outer_bands = [
    ("外环_01_主轮廓", 1.00, 1.01, (71, -12, 20), 0.069, 0.15),
    ("外环_02_倾斜轮廓", 1.01, 0.99, (42, 64, -15), 0.060, 1.2),
    ("外环_03_横向薄带", 1.035, 0.91, (17, -13, 36), 0.071, 2.5),
    ("细环_01", 1.015, 1.07, (76, 36, -32), 0.022, 0.0),
    ("细环_02", 1.07, 0.95, (88, -56, 9), 0.020, 0.8),
    ("细环_03", 1.06, 0.93, (33, 31, 69), 0.023, 1.6),
    ("细环_04", 1.00, 1.12, (57, -21, -54), 0.018, 2.4),
    ("细环_05", 1.055, 0.99, (20, 77, 27), 0.019, 3.2),
    ("细环_06", 1.04, 0.94, (-44, 18, -7), 0.024, 4.0),
]
for name, radius, squash, rotation, width, phase in outer_bands:
    ribbon(name, radius, squash, rotation, width, thickness=0.009,
           twist=0.12, phase=phase, waviness=0.015)

# 图标正面有两片穿过中心的斜向尖带，不能只用同心环代替。
view_direction = Vector((3.0, -6.2, 2.55)).normalized()
view_rotation = (-view_direction).to_track_quat('-Z', 'Y').to_matrix()
view_right = view_rotation.col[0]
view_up = view_rotation.col[1]

def front_blade(name, controls, width, phase):
    controls = [view_right*x + view_up*y + view_direction*z for x, y, z in controls]
    vertices, faces = [], []
    steps = 88
    for i in range(steps+1):
        t = i / steps
        s = 1-t
        point = s**3*controls[0] + 3*s*s*t*controls[1] + 3*s*t*t*controls[2] + t**3*controls[3]
        tangent = (3*s*s*(controls[1]-controls[0]) + 6*s*t*(controls[2]-controls[1]) +
                   3*t*t*(controls[3]-controls[2])).normalized()
        across = tangent.cross(view_direction).normalized()
        perpendicular = tangent.cross(across).normalized()
        roll = 0.85*math.sin(math.pi*2*t+phase)
        across = across*math.cos(roll)+perpendicular*math.sin(roll)
        band_width = width * (0.01 + 0.99*math.sin(math.pi*t)**0.85)
        vertices.extend((point-band_width*0.5*across, point+band_width*0.5*across))
    faces = [(2*i, 2*i+2, 2*i+3, 2*i+1) for i in range(steps)]
    mesh = bpy.data.meshes.new(name+"_网格")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    model_collection.objects.link(obj)
    obj.parent = root
    obj.data.materials.append(white)
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    solid = obj.modifiers.new("带体厚度", 'SOLIDIFY')
    solid.thickness = 0.017
    solid.offset = 0.0
    bevel = obj.modifiers.new("边缘微倒角", 'BEVEL')
    bevel.width = 0.004
    bevel.segments = 2
    meshes.append(obj)

front_blade("主片_01_斜向尖带", [(-0.71,-0.55,0.60), (-0.33,-0.17,0.77),
           (0.30,0.38,0.63), (0.01,0.83,0.24)], 0.29, 0.1)
front_blade("主片_02_交叉翻卷", [(-0.64,0.45,0.50), (-0.25,0.51,0.78),
           (0.19,-0.29,0.75), (0.61,-0.40,0.32)], 0.24, 1.4)

def stage_object(name, data):
    obj = bpy.data.objects.new(name, data)
    stage_collection.objects.link(obj)
    return obj

camera = stage_object("D32钢_展示相机", bpy.data.cameras.new("D32钢_展示相机"))
camera.location = (3.0, -6.2, 2.55)
camera.rotation_euler = (-camera.location).to_track_quat('-Z', 'Y').to_euler()
camera.data.type = 'ORTHO'
camera.data.ortho_scale = 2.85
scene.camera = camera

for name, location, power, size in [
    ("主光", (-3.5, -4.2, 5.0), 650, 4.0),
    ("补光", (4.2, -1.0, 1.5), 350, 3.0),
    ("轮廓光", (1.0, 3.0, 4.0), 800, 3.0),
]:
    light = stage_object("D32钢_" + name, bpy.data.lights.new("D32钢_" + name, 'AREA'))
    light.location = location
    light.rotation_euler = (-light.location).to_track_quat('-Z', 'Y').to_euler()
    light.data.energy = power
    light.data.shape = 'DISK'
    light.data.size = size

world = bpy.data.worlds.new("D32钢_预览环境")
world.use_nodes = True
scene.world = world
background = next(n for n in world.node_tree.nodes if n.type == 'BACKGROUND')
background.inputs['Color'].default_value = (0.028, 0.036, 0.05, 1.0)
background.inputs['Strength'].default_value = 0.45
scene.render.engine = 'BLENDER_EEVEE'
scene.render.resolution_x = 1000
scene.render.resolution_y = 1000
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = 'PNG'
scene.render.film_transparent = False

# 将来源图打包进 .blend，方便后续在图像编辑器中查看。
reference_path = OUTPUT_DIR / 'reference' / 'D32Steel_PRTS.png'
if reference_path.exists():
    reference = bpy.data.images.load(str(reference_path), check_existing=True)
    reference.pack()

for area in bpy.context.screen.areas:
    if area.type == 'VIEW_3D':
        space = area.spaces.active
        space.shading.type = 'SOLID'
        space.shading.light = 'STUDIO'
        space.shading.color_type = 'MATERIAL'
        space.shading.show_cavity = True
        space.overlay.show_overlays = False
        space.region_3d.view_rotation = camera.rotation_euler.to_quaternion()
        space.region_3d.view_distance = 4.9
        space.region_3d.view_location = (0, 0, 0)
        space.region_3d.view_perspective = 'ORTHO'

bpy.context.view_layer.objects.active = root
root.select_set(True)
scene['说明'] = '根据 PRTS D32钢图标制作的可编辑白模；宽带、内带与外环分件保留厚度和倒角修改器。'
scene.render.filepath = str(OUTPUT_DIR / 'D32Steel_Blockout_Preview.png')
bpy.ops.wm.save_as_mainfile(filepath=str(OUTPUT_DIR / 'D32Steel_Blockout.blend'))
print(json.dumps({'scene':scene.name, 'mesh_count':len(meshes),
                  'blend':bpy.data.filepath,
                  'reference':str(reference_path)}, ensure_ascii=False))
