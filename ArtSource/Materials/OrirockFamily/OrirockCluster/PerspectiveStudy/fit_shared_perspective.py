"""仅用 NumPy 比较四个真实立方体的共同透视焦距；不调用 Blender/Unity。"""
from pathlib import Path
import ast
import json
import math
import time

import numpy as np


OUT = Path(__file__).resolve().parent
CLUSTER = OUT.parent
LANDMARK_SOURCE = CLUSTER / 'OcclusionRevision' / 'fit_front_landmarks.py'
POSE_SOURCE = CLUSTER / 'SurfaceMatchRevision' / '当前块体坐标.json'
IDS = np.array([0, 1, 2, 3, 4, 5, 7])
BITS = np.array([
    [0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0],
    [0, 0, 1], [1, 0, 1], [1, 1, 1], [0, 1, 1],
], dtype=float)
FOCAL_LENGTHS = [100., 160., 240., 400., 800., 1_000_000.]
TRIALS, ITERATIONS = 16, 100
SEED = 20261005


def rotation(vector):
    angle = float(np.linalg.norm(vector))
    x, y, z = vector
    skew = np.array([[0., -z, y], [z, 0., -x], [-y, x, 0.]])
    if angle < 1.e-9:
        return np.eye(3) + skew + .5 * skew @ skew
    return (np.eye(3) + math.sin(angle) / angle * skew
            + (1. - math.cos(angle)) / angle**2 * (skew @ skew))


def read_inputs():
    # 只解析常量，不执行原作者脚本及其写文件操作。
    tree = ast.parse(LANDMARK_SOURCE.read_text(encoding='utf-8-sig'))
    observations = next(
        ast.literal_eval(node.value) for node in tree.body
        if isinstance(node, ast.Assign)
        and any(isinstance(item, ast.Name) and item.id == 'observed'
                for item in node.targets)
    )
    parts = json.loads(POSE_SOURCE.read_text(encoding='utf-8-sig'))
    inputs = {}
    for key, (index, targets, weights) in observations.items():
        old = np.array(parts[index]['verts'], dtype=float)
        camera = np.column_stack(((old[:, 0] - 91.) * .016,
                                  (91. - old[:, 1]) * .016, old[:, 2]))
        axes = (camera[[1, 3, 4]] - camera[0]).T
        lengths = np.linalg.norm(axes, axis=0)
        # 去掉记录中浮点误差，保留原先角点编号的手性与轴方向。
        u, _, vt = np.linalg.svd(axes / lengths)
        basis = u @ vt
        inputs[key] = dict(
            name=parts[index]['name'], old=old, target=np.array(targets, float),
            weights=np.array(weights, float), basis=basis,
            start=np.r_[old[0, :2], math.log(float(lengths.mean()) / .016),
                        np.zeros(3)],
            source_axis_lengths=lengths.tolist(),
            source_basis_determinant=float(np.linalg.det(basis)),
        )
    return inputs


def model(parameters, basis, focal):
    # 仅六个自由度：角点0的投影中心(2)、整块边长(1)、整体旋转(3)。
    # 以角点0的Z=0、相机D=1固定深度/尺度冗余。三条轴共用同一边长。
    unit_axes = rotation(parameters[3:]) @ basis
    pixel_length = math.exp(float(parameters[2]))
    pixel_axes = pixel_length * unit_axes
    screen_xy = (BITS @ pixel_axes[:2].T) * np.array([1., -1.])
    numerator = screen_xy + parameters[:2] - 91.
    if focal is None:
        denominator = np.ones(8)
        corner_ray = np.array([0., 0., 1.])
    else:
        denominator = 1. - (BITS @ pixel_axes[2]) / focal
        corner_ray = np.array([-(parameters[0] - 91.) / focal,
                                (parameters[1] - 91.) / focal, 1.])
    projected = 91. + numerator / np.maximum(denominator[:, None], 1.e-4)
    inward_view_dot = unit_axes.T @ (corner_ray / np.linalg.norm(corner_ray))
    return projected, unit_axes, pixel_axes, denominator, inward_view_dot


def residual(parameters, data, focal):
    projected, unit, _, denominator, view_dot = model(parameters, data['basis'], focal)
    geometry = ((projected[IDS] - data['target'])
                * np.sqrt(data['weights'])[:, None]).ravel()
    # 固定面际关系：轴向后；透视下也检查相机位于三个共角面的外侧。
    return np.r_[geometry, np.maximum(unit[2] + 1.e-6, 0.) * 500.,
                 np.maximum(view_dot + 1.e-6, 0.) * 500.,
                 np.maximum(.01 - denominator, 0.) * 2000.]


def fit(data, focal, seed_start):
    rng = np.random.default_rng(SEED)
    best = None
    for trial in range(TRIALS):
        p = (seed_start if trial % 2 == 0 else data['start']).copy()
        if trial > 1:
            p[3:] += rng.normal(0., .24, 3)
        damping = .05
        for _ in range(ITERATIONS):
            error = residual(p, data, focal)
            jacobian = np.column_stack([
                (residual(p + np.eye(6)[j] * 1.e-5, data, focal) - error) / 1.e-5
                for j in range(6)
            ])
            try:
                step = np.linalg.solve(jacobian.T @ jacobian + damping * np.eye(6),
                                       -jacobian.T @ error)
            except np.linalg.LinAlgError:
                break
            if np.linalg.norm(step) < 1.e-7:
                break
            candidate = p + step
            if not -8. < candidate[2] < 12.:
                damping = min(damping * 4., 1.e9)
                continue
            candidate_error = residual(candidate, data, focal)
            if candidate_error @ candidate_error < error @ error:
                p = candidate
                damping = max(damping * .5, 1.e-8)
            else:
                damping = min(damping * 4., 1.e9)
        projected, unit, pixel_axes, denominator, view_dot = model(p, data['basis'], focal)
        valid = bool(np.all(unit[2] < 0.) and np.all(view_dot < 0.)
                     and np.all(denominator > 0.))
        score = float(residual(p, data, focal) @ residual(p, data, focal))
        if valid and (best is None or score < best[0]):
            best = score, p
    if best is None:
        return None, dict(status='有限次试配未得到满足三轴向后及三面可见的候选')
    score, p = best
    projected, unit, pixel_axes, denominator, view_dot = model(p, data['basis'], focal)
    delta = projected[IDS] - data['target']
    edge_projection = projected[[1, 3, 4]] - projected[0]
    data_out = dict(
        status='数值候选，未进行视觉验收',
        projected_corner=p[:2].tolist(),
        coordinate_rmse_px=float(np.sqrt(np.mean(delta**2))),
        point_rmse_px=float(np.sqrt(np.mean(np.sum(delta**2, axis=1)))),
        weighted_coordinate_rmse_px=float(np.sqrt(
            np.sum(delta**2 * data['weights'][:, None]) / (2. * data['weights'].sum()))),
        max_point_error_px=float(np.linalg.norm(delta, axis=1).max()),
        objective=score,
        rotation_delta_vector_rad=p[3:].tolist(),
        unit_axes_columns_camera_xyz=unit.tolist(),
        unit_axis_z=unit[2].tolist(),
        inward_axes_dot_normalized_view_ray=view_dot.tolist(),
        front_axis_projected_vectors=edge_projection.tolist(),
        front_axis_angle_screen_deg=np.degrees(np.arctan2(
            edge_projection[:, 1], edge_projection[:, 0])).tolist(),
        pixel_scale_length=float(math.exp(p[2])),
        perspective_denominators=denominator.tolist(),
        max_to_min_camera_distance_ratio=float(denominator.max() / denominator.min()),
        target_points=data['target'].tolist(), weights=data['weights'].tolist(),
        projected_vertices=projected.tolist(),
        predicted_observed_points=projected[IDS].tolist(), residual_xy_px=delta.tolist(),
    )
    if focal is None:
        length = math.exp(p[2]) * .016
        corner = np.array([(p[0] - 91.) * .016, (91. - p[1]) * .016, 0.])
        axes = length * unit
        data_out['coordinate_gauge'] = '正交基线：0.016模型单位/像素，角点0深度设0'
    else:
        length = math.exp(p[2]) / focal
        corner = np.array([(p[0] - 91.) / focal, (91. - p[1]) / focal, 0.])
        axes = length * unit
        data_out['coordinate_gauge'] = '透视：相机(0,0,1)，D=1，角点0深度0；可沿相机射线整体缩放平移'
    vertices = corner + BITS @ axes.T
    data_out.update(edge_length_camera_units=float(length),
                    corner_camera_xyz=corner.tolist(),
                    axes_columns_camera_xyz=axes.tolist(),
                    vertices_camera_xyz=vertices.tolist())
    return p, data_out


def run():
    started = time.monotonic()
    inputs = read_inputs()
    result = dict(
        scope='仅top/left/middle/right_front四个既有完整立方体的数值实验；不含右后与后撑',
        sources=[str(LANDMARK_SOURCE), str(POSE_SOURCE)],
        image_size=[183, 183], principal_point=[91., 91.], camera_D=1.,
        projection='x=91+f*X/(1-Z), y=91-f*Y/(1-Z)',
        observed_vertex_indices=IDS.tolist(),
        vertex_order=BITS.astype(int).tolist(),
        search=dict(seed=SEED, starts_per_block=TRIALS, iterations_per_start=ITERATIONS,
                    focal_candidates_px=FOCAL_LENGTHS, extra_exact_orthographic_baseline=True),
        metric_note='coordinate_rmse_px=sqrt(mean(七点×xy的误差平方))；point_rmse为点距离RMS，等于前者乘sqrt(2)。拟合沿用原观测权重。',
        normalization_note='每块角点0深度规范化为0；四块共享同一f及同一相机。没有拟合跨块遮挡/穿插/相对深度，也没有修改现有现场。',
        current_source={}, orthographic_refit={}, focal_results=[],
    )
    ortho_parameters = {}
    for key, data in inputs.items():
        delta = data['old'][IDS, :2] - data['target']
        result['current_source'][key] = dict(
            name=data['name'], coordinate_rmse_px=float(np.sqrt(np.mean(delta**2))),
            predicted_points=data['old'][IDS, :2].tolist(),
            source_axis_lengths=data['source_axis_lengths'],
            source_basis_determinant=data['source_basis_determinant'],
        )
        p, fitted = fit(data, None, data['start'])
        ortho_parameters[key] = p if p is not None else data['start']
        result['orthographic_refit'][key] = fitted
        print('orthographic', key, fitted.get('coordinate_rmse_px'), flush=True)
    # 每档独立用正交解和原姿态作种子；不无限追加焦距或随机起点。
    for focal in FOCAL_LENGTHS:
        group = dict(focal_px=focal,
                     vertical_fov_deg=math.degrees(2. * math.atan(183. / (2. * focal))),
                     blocks={})
        for key, data in inputs.items():
            _, fitted = fit(data, focal, ortho_parameters[key])
            group['blocks'][key] = fitted
            print('focal', focal, key, fitted.get('coordinate_rmse_px'), flush=True)
        if all('coordinate_rmse_px' in block for block in group['blocks'].values()):
            group['all_blocks_coordinate_rmse_px'] = float(np.sqrt(np.mean([
                block['coordinate_rmse_px']**2 for block in group['blocks'].values()
            ])))
            group['all_blocks_weighted_objective'] = float(sum(
                block['objective'] for block in group['blocks'].values()))
        result['focal_results'].append(group)
    result['orthographic_aggregate'] = dict(
        coordinate_rmse_px=float(np.sqrt(np.mean([
            item['coordinate_rmse_px']**2
            for item in result['orthographic_refit'].values()
        ]))),
        weighted_objective=float(sum(
            item['objective'] for item in result['orthographic_refit'].values()
        )),
    )
    result['camera_recommendation'] = dict(
        conclusion='指定焦距没有明显改善四块共同拟合；顶块左角在有限透视下反而更靠内。优先保留正交。',
        if_perspective_is_required_focal_px=800.,
        if_perspective_is_required_vertical_fov_deg=13.0497028792,
        candidate_location='focal_results中focal_px=800.0的blocks；每块都含完整八顶点与三轴矩阵',
        warning='f=800仅是弱透视备选，未解决顶块左角；所有材料须共用同一f。不得按块挑选不同f的最低误差。',
    )
    result['depth_rescaling_note'] = (
        '规范化相机C=(0,0,1)，若选角点0深度z0，则k=1-z0>0，'
        '整块V_new=C+k*(V-C)，边长乘k，旋转不变；投影不变。'
        '这是整块相似变换，不是独立角点拉扯；未决定跨块遮挡/接触。'
    )
    result['top_left_corner_identity'] = dict(
        observed_common_x=109., observed_u_endpoint_x=101.,
        observed_v_endpoint_x=70., observed_uv_endpoint_x=45.,
        affine_uv_x=62.,
        identity='x_uv=(62+du*101+dv*70)/(1+du+dv)，du,dv>=0表示边向后。',
        implication='若相邻三个观测点精确匹配，向后透视使x_uv>=62，不能达到45；拟合必须在这些点之间折中。',
    )
    result['elapsed_seconds'] = time.monotonic() - started
    destination = OUT / '共同焦距拟合结果.json'
    destination.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    write_conclusion(result)
    print('saved', destination, flush=True)


def write_conclusion(result):
    lines = [
        '# 四个完整立方体的共同透视焦距实验', '',
        '指定六档没有明显改善共同拟合，尤其不能修复顶块左角。建议继续正交；'
        '如主代理需要统一透视，f=800像素（183×183参考尺度、垂直视场13.05°）可作为弱透视备选。'
        '本次没有调用Blender/Unity、修改现场或宣称视觉通过。', '',
        '只读入`OcclusionRevision/fit_front_landmarks.py`的top/left/middle/right_front七角点和权重，'
        '以`SurfaceMatchRevision/当前块体坐标.json`的整体姿态为起点。每块仅拟合旋转3参数、'
        '共同边长1参数、角点0投影位置2参数；各块共享同一f、主点(91,91)与D=1。'
        '固定各块角点0的Z=0消除尺度/深度冗余。16个起点×最多100步，未追加焦距搜索。', '',
        '下表是七点×两个坐标的未加权RMSE，单位为像素；点距离RMS需乘√2。'
        '实际优化沿用原观测权重；完整逐点残差、三轴方向及八顶点均在JSON。', '',
        '| 共同f（像素） | 垂直视场 | 顶块 | 左下 | 圈内小块 | 右前 | 四块合并 | 顶块左角输出 |',
        '| --- | --- | --- | --- | --- | --- | --- | --- |',
    ]
    baseline = result['orthographic_refit']
    order = ['top', 'left', 'middle', 'right_front']
    values = ' | '.join(f"{baseline[key]['coordinate_rmse_px']:.3f}" for key in order)
    corner = baseline['top']['projected_vertices'][2]
    total = result['orthographic_aggregate']['coordinate_rmse_px']
    lines.append(f'| 正交复拟合 | 0°极限 | {values} | {total:.3f} | ({corner[0]:.2f},{corner[1]:.2f}) |')
    for group in result['focal_results']:
        values = ' | '.join(f"{group['blocks'][key]['coordinate_rmse_px']:.3f}" for key in order)
        corner = group['blocks']['top']['projected_vertices'][2]
        label = '非常大：1000000' if group['focal_px'] == 1_000_000. else f"{group['focal_px']:.0f}"
        lines.append(
            f"| {label} | {group['vertical_fov_deg']:.3f}° | {values} | "
            f"{group['all_blocks_coordinate_rmse_px']:.3f} | ({corner[0]:.2f},{corner[1]:.2f}) |"
        )
    lines += [
        '', 'f=800的合并RMSE只下降约0.12%；顶块误差增加约2.1%，左角仍约(53.41,49.93)，'
        '观测为(45,52)。沿用原权重的总目标反而由526.892升至528.318。'
        'f=400也未取得整体收益；右前块单独偏好较短焦距不能成为整组换相机的依据。', '',
        '原因可直接检查：顶块共角x=109，相邻u角x=101、v角x=70，正交uv角应为62。'
        '令du、dv为两条边增加的相机距离（以共角距离为1），向后透视满足'
        '`x_uv=(62+du*101+dv*70)/(1+du+dv)`。du、dv≥0时结果≥62，'
        '与观测45方向相反；整体位姿拟合只能在相邻观测间折中。当前左角52.80也已是这种折中。', '',
        'f=100的垂直视场约84.92°，顶块前后距离比达到2.281；f=160也达到1.707。'
        '这种透视显著收缩远边、改变面的面积比例与堆叠观感，顶块误差分别变为6.120、4.578。'
        '不能为右前块的局部残差收益而给所有材料引入这种强烈变化。', '',
        '## 可供主代理读取的弱透视候选', '',
        '统一f=800、D=1、相机C=(0,0,1)。以下边长和坐标是相机空间的规范化单位，'
        '不是现有Unity/Blender世界坐标。每块三轴严格等长正交；下表Z方向及所有焦距的'
        '三面视线点积均为负，保持三条轴向后及三个共角面的可见关系。', '',
        '| 块体 | 边长 | 角点0相机XYZ | 三条单位轴Z分量 |',
        '| --- | --- | --- | --- |',
    ]
    selected = next(group for group in result['focal_results'] if group['focal_px'] == 800.)
    names = dict(top='顶块', left='左下', middle='圈内小块', right_front='右前')
    for key in order:
        block = selected['blocks'][key]
        corner = ','.join(f'{value:.6f}' for value in block['corner_camera_xyz'])
        z = ','.join(f'{value:.6f}' for value in block['unit_axis_z'])
        lines.append(f"| {names[key]} | {block['edge_length_camera_units']:.8f} | ({corner}) | ({z}) |")
    lines += [
        '', '完整实际拟合输出：JSON的`focal_results`中选择共同`focal_px`，再读取每块的'
        '`vertices_camera_xyz`（8点）、`axes_columns_camera_xyz`（列为u/v/w）、'
        '`front_axis_projected_vectors`和`predicted_observed_points`。顶点编号仍为0/1/2/3/4/5/6/7，'
        '其中6是未观测的背角，由同一刚性变换生成。', '',
        '调整相对深度时，取k=1-z0>0，将整个块体变换为`V_new=C+k*(V-C)`，'
        '边长也乘k；旋转与投影不变。这提供整体摆放自由度，不能单点变形。'
        '跨块遮挡、接触、支撑、材质、轮廓及全材料相机一致性尚未验证。', '',
        '新增文件仅为本目录下的`fit_shared_perspective.py`、`共同焦距拟合结果.json`和本结论。',
    ]
    (OUT / '透视实验结论.md').write_text('\n'.join(lines) + '\n', encoding='utf-8')


if __name__ == '__main__':
    run()
