"""固源岩组石质材质：StoneFace 面内风化 + Object 坐标的连续纹理。

只创建材质与节点；不创建几何、不操作场景、不加载或投射参考图。
"""


def _linear_rgba(rgb):
    """输入 0..255 的 sRGB，返回 Blender 着色器使用的线性 RGBA。"""
    result = []
    for channel in rgb:
        value = max(0.0, min(1.0, float(channel) / 255.0))
        result.append(value / 12.92 if value <= 0.04045
                      else ((value + 0.055) / 1.055) ** 2.4)
    return (*result, 1.0)


def build_face_material(name, rgb, weather_rgb=None, weather_amount=0.12, scale=1.0):
    """返回新材质，显式使用 Blender 5.2 的 StoneFace UV 层。

    每个原始立方体面独立使用 0..1 UV；白面的 V=0 对应物理下边缘。
    另一个 UVMap 层可打包烘焙，改变活动 UV 不会移动风化。
    weather_amount 控制风化带宽度，0 关闭 UV 风化；scale 控制纹理密度。
    颜色均为 0..255 sRGB。浅色面自动减弱粗斑，并添加下部局部风化。
    """
    import bpy

    scale = max(0.01, float(scale))
    weather_amount = max(0.0, min(0.5, float(weather_amount)))
    weather_rgb = (147, 128, 82) if weather_rgb is None else weather_rgb
    lightness = sum(c * weight for c, weight in zip(rgb, (0.2126, 0.7152, 0.0722))) / 255.0
    light_face = lightness >= 0.72
    cool_face = rgb[2] >= rgb[0] * 0.90 and rgb[1] >= rgb[0] * 0.94

    mat = bpy.data.materials.new(name=name)
    mat.use_nodes = True
    mat.diffuse_color = _linear_rgba(rgb)
    mat.metallic = 0.0
    mat.roughness = 0.92
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    # 默认节点通过 type 找到，不依赖节点名或界面语言。
    principled = next(n for n in nodes if n.type == 'BSDF_PRINCIPLED')
    output = next(n for n in nodes if n.type == 'OUTPUT_MATERIAL')
    principled.location, output.location = (1700, 100), (2000, 100)
    rows = {}

    def node(kind, label, column):
        current = nodes.new(kind)
        current.label = label
        row = rows.get(column, 0)
        current.location = (column * 250, 750 - row * 190)
        rows[column] = row + 1
        return current

    def socket(sockets, identifier):
        # Socket 的 API identifier 不使用本地化显示文字。
        return next(s for s in sockets if s.identifier == identifier)

    def feed(target, value):
        if isinstance(value, (int, float, tuple, list)):
            target.default_value = value
        else:
            links.new(value, target)

    def math_node(operation, a, b, label, column=2, clamp=False):
        current = node('ShaderNodeMath', label, column)
        current.operation, current.use_clamp = operation, clamp
        feed(current.inputs[0], a)
        feed(current.inputs[1], b)
        return current.outputs[0]

    def noise(vector, frequency, label, dimensions='3D', detail=2.0):
        current = node('ShaderNodeTexNoise', label, 0)
        current.noise_dimensions, current.noise_type = dimensions, 'FBM'
        current.normalize = True
        links.new(vector, socket(current.inputs, 'Vector'))
        socket(current.inputs, 'Scale').default_value = frequency * scale
        socket(current.inputs, 'Detail').default_value = detail
        socket(current.inputs, 'Roughness').default_value = 0.72
        return current

    def ramp(value, start, end, low, high, label, column=1):
        current = node('ShaderNodeValToRGB', label, column)
        current.color_ramp.interpolation = 'EASE'
        for element, position, grey in zip(current.color_ramp.elements,
                                           (start, end), (low, high)):
            element.position, element.color = position, (grey, grey, grey, 1.0)
        links.new(value, current.inputs[0])
        return current.outputs[0]

    def mix(factor, a, b, label, blend='MIX', column=5):
        current = node('ShaderNodeMixRGB', label, column)
        current.blend_type = blend
        feed(current.inputs[0], factor)
        feed(current.inputs[1], a)
        feed(current.inputs[2], b)
        return current.outputs[0]

    texcoord = node('ShaderNodeTexCoord', '物体坐标：正面、侧面与背面连续', -1)
    object_coord = socket(texcoord.outputs, 'Object')
    coarse = noise(object_coord, 3.5, '局部矿物粗斑', detail=4.0)
    medium = noise(object_coord, 31.0, '中粒纹理与裂纹分布')
    fine = noise(object_coord, 105.0, '细密石粒：不依赖面 UV', detail=4.0)
    coarse_mask = ramp(coarse.outputs[0], 0.46, 0.65, 0.0, 1.0, '粗斑局部覆盖')
    if light_face:
        patch_rgb, patch_strength = (239, 236, 217), 0.07
    elif cool_face:
        patch_rgb = tuple(c * 1.18 + 3.0 for c in rgb)
        patch_strength = 0.32
    else:
        patch_rgb = tuple(c * 1.12 + 9.0 for c in rgb)
        patch_strength = 0.24
    patch_factor = math_node('MULTIPLY', coarse_mask, patch_strength, '粗斑颜色强度')
    color = mix(patch_factor, _linear_rgba(rgb), _linear_rgba(patch_rgb), '保留底色的矿物斑')

    if weather_amount > 0.0:
        uv = node('ShaderNodeUVMap', 'StoneFace：原始面内 0..1 UV', -1)
        uv.uv_map = 'StoneFace'
        separate = node('ShaderNodeSeparateXYZ', '面内 U、V；V=0 为下边', 0)
        links.new(uv.outputs[0], separate.inputs[0])
        u, v = separate.outputs[0], separate.outputs[1]
        inverse_u = math_node('SUBTRACT', 1.0, u, '距右边')
        inverse_v = math_node('SUBTRACT', 1.0, v, '距上边')
        horizontal = math_node('MINIMUM', u, inverse_u, '距左右边')
        vertical = math_node('MINIMUM', v, inverse_v, '距上下边')
        edge_distance = math_node('MINIMUM', horizontal, vertical, '距最近真实面边')
        edge_noise = noise(uv.outputs[0], 9.0, '不均匀风化带宽度', dimensions='2D')
        jagged = ramp(edge_noise.outputs[0], 0.28, 0.68, 0.08, 1.0, '风化边缘凹入与断续')
        width = math_node('MULTIPLY', jagged, weather_amount * 0.98, '随机风化带宽')
        width = math_node('ADD', width, weather_amount * 0.10, '细窄基础风化带')
        border = math_node('SUBTRACT', width, edge_distance, '不规则边缘颜色掩膜', 3)
        border = math_node('MULTIPLY', border, 160.0, '窄过渡，避免均匀柔边', 3)
        border = math_node('ADD', border, 0.5, '风化掩膜限幅', 3, clamp=True)
        weather_mask = math_node('MULTIPLY', border, 0.72 if light_face else 0.62,
                                 '风化皮与底色混合', 4)
        if light_face:
            lower = math_node('SUBTRACT', 0.42, v, '白面下部风化范围', 3)
            lower = math_node('MULTIPLY', lower, 1.0 / 0.42, '向上衰减', 3, clamp=True)
            lower = math_node('MULTIPLY', lower, coarse_mask, '下部斑驳而非整条染色', 4)
            lower = math_node('MULTIPLY', lower, weather_amount * 1.15, '下部浅风化强度', 4)
            weather_mask = math_node('MAXIMUM', weather_mask, lower, '边缘与下部风化合并', 4)
        color = mix(weather_mask, color, _linear_rgba(weather_rgb), '赭色风化皮，仅影响颜色')

    # 轻度扭曲的 Voronoi 边界被两级噪声打断，避免铺满规则龟裂网。
    warp = node('ShaderNodeVectorMath', '裂纹轻度曲折', 1)
    warp.operation = 'SCALE'
    links.new(medium.outputs[1], warp.inputs[0])
    socket(warp.inputs, 'Scale').default_value = 0.11 / scale
    warped_coord = node('ShaderNodeVectorMath', '物体内裂纹坐标', 2)
    warped_coord.operation = 'ADD'
    links.new(object_coord, warped_coord.inputs[0])
    links.new(warp.outputs[0], warped_coord.inputs[1])
    cracks = node('ShaderNodeTexVoronoi', '少量细褐裂隙', 3)
    cracks.voronoi_dimensions, cracks.feature = '3D', 'DISTANCE_TO_EDGE'
    links.new(warped_coord.outputs[0], socket(cracks.inputs, 'Vector'))
    socket(cracks.inputs, 'Scale').default_value = 11.0 * scale
    crack_line = ramp(socket(cracks.outputs, 'Distance'), 0.002, 0.012, 1.0, 0.0,
                      '细裂纹宽度', 4)
    crack_gate = ramp(medium.outputs[0], 0.57, 0.70, 0.0, 1.0, '打断裂纹，保留短段', 4)
    crack_mask = math_node('MULTIPLY', crack_line, crack_gate, '零碎裂隙', 4)
    crack_mask = math_node('MULTIPLY', crack_mask, coarse_mask, '裂隙只出现于局部矿物斑', 4)
    crack_color = math_node('MULTIPLY', crack_mask, 0.48 if light_face else 0.38,
                            '褐裂纹颜色强度', 4)
    color = mix(crack_color, color, _linear_rgba((103, 79, 52)), '褐裂纹染色')

    # 最后叠细粒，使风化皮也有颗粒，而不是平滑的彩色边框。
    grain = ramp(fine.outputs[0], 0.38, 0.63, 0.70 if light_face else 0.46,
                 1.12 if light_face else 1.43, '细粒线性明暗倍率', 4)
    color = mix(1.0, color, grain, '细密颗粒颜色', blend='MULTIPLY')
    links.new(color, socket(principled.inputs, 'Base Color'))
    socket(principled.inputs, 'Metallic').default_value = 0.0
    socket(principled.inputs, 'IOR').default_value = 1.46
    socket(principled.inputs, 'Specular IOR Level').default_value = 0.28

    roughness = math_node('MULTIPLY', fine.outputs[0], 0.13, '颗粒粗糙度幅度', 5)
    roughness = math_node('ADD', roughness, 0.85, '粗糙度 0.85..0.98', 5)
    links.new(roughness, socket(principled.inputs, 'Roughness'))
    medium_height = math_node('MULTIPLY', medium.outputs[0], 0.16, '中粒低幅起伏', 5)
    height = math_node('ADD', fine.outputs[0], medium_height, '石粒微表面', 5)
    crack_depth = math_node('MULTIPLY', crack_mask, 0.10, '浅裂隙凹痕', 5)
    height = math_node('SUBTRACT', height, crack_depth, '物体纹理凹凸，无 UV 边界', 5)
    bump = node('ShaderNodeBump', '微弱凹凸，不连接位移', 6)
    socket(bump.inputs, 'Strength').default_value = 0.12
    socket(bump.inputs, 'Distance').default_value = 0.0015 / scale
    links.new(height, socket(bump.inputs, 'Height'))
    links.new(socket(bump.outputs, 'Normal'), socket(principled.inputs, 'Normal'))
    return mat
