"""只读研究：成组保持上部/右部相对关系，为用户决定提供可行摆放证据。"""
from pathlib import Path
import json
import math
import numpy as np

OUT = Path(__file__).resolve().parent
data = json.loads((OUT / '成组摆放只读基准.json').read_text(encoding='utf-8'))
right, up, front = [np.array(data[k]) for k in ('right', 'up', 'front')]
bits = np.array([[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0],
                 [0, 0, 1], [1, 0, 1], [1, 1, 1], [0, 1, 1]], float)
orig = [np.array(p['cube_vertices']) if p['cube_vertices'] else None for p in data['parts']]
mesh = [np.array(p['mesh_vertices']) for p in data['parts']]
lengths = [p['cube_edge_length'] for p in data['parts']]
axes = {i: np.column_stack([orig[i][k] - orig[i][0] for k in (1, 3, 4)]) / lengths[i]
        for i in (0, 1, 4)}
cut = {c['block']: c for c in data['cuts']}
left_local = (mesh[1] - orig[1][0]) @ axes[1] / lengths[1]
pivot = orig[4][3]


def rotation(v):
    a = np.linalg.norm(v)
    x, y, z = v
    skew = np.array([[0, -z, y], [z, 0, -x], [-y, x, 0]])
    return np.eye(3) + skew * (math.sin(a) / a if a > 1e-8 else 1) + skew @ skew * ((1 - math.cos(a)) / a**2 if a > 1e-8 else .5)


def screen(vertices):
    return np.column_stack([91 + vertices @ right / .016, 91 - vertices @ up / .016])


def poses(p):
    group_rotation = rotation(p[:3])
    translation = np.array([p[3], p[4], 0.])
    right_raw = (mesh[4] - pivot) @ group_rotation.T + pivot + translation
    translation[2] += data['floor'] - right_raw[:, 2].min()
    result = {}
    for i in (0, 4):
        origin = group_rotation @ (orig[i][0] - pivot) + pivot + translation
        result[i] = (group_rotation @ axes[i], lengths[i], origin)
    left_axes = rotation(p[5:8]) @ axes[1]
    left_length = np.exp(p[8])
    left_xy = right * ((p[9] - 91) * .016) + up * ((91 - p[10]) * .016)
    depth = (data['floor'] - left_xy[2] - (left_local @ left_axes.T * left_length)[:, 2].min()) / front[2]
    result[1] = (left_axes, left_length, left_xy + front * depth)
    return result, group_rotation, translation


def project_landmarks(pose, indices, cube_cut):
    basis, length, origin = pose
    vertices = bits @ basis.T * length + origin
    projected = screen(vertices)
    vertex = projected[cube_cut['corner']]
    cap = np.array([vertex + (projected[j] - vertex) * f
                    for j, f in zip(cube_cut['neighbors'], cube_cut['fractions'])])
    return np.vstack([projected[indices], cap])


def ray_hit(pose, xy, cube_cut):
    basis, length, origin = pose
    xy = np.array(xy)
    rays = (xy[:, :1] - 91) * .016 * right + (91 - xy[:, 1:]) * .016 * up
    origins = (rays + front * 5 - origin) @ basis / length
    direction = -front @ basis / length
    entry, exit = np.full(len(xy), -1e6), np.full(len(xy), 1e6)
    for i in range(3):
        d = direction[i]
        if abs(d) < 1e-9:
            entry = np.maximum(entry, np.where((origins[:, i] >= 0) & (origins[:, i] <= 1), -1e6, 1e6))
            continue
        t0, t1 = -origins[:, i] / d, (1 - origins[:, i]) / d
        entry = np.maximum(entry, np.minimum(t0, t1))
        exit = np.minimum(exit, np.maximum(t0, t1))
    coefficients = 1 / np.array(cube_cut['fractions'])
    sign = np.ones(3)
    offset = 0.
    if cube_cut['corner'] == 1:
        sign[0] = -1
        offset = coefficients[0]
    coefficients *= sign
    a = origins @ coefficients + offset
    b = direction @ coefficients
    cross = (1 - a) / b
    if b > 0:
        entry = np.maximum(entry, cross)
    else:
        exit = np.minimum(exit, cross)
    return 5 - entry, np.maximum(entry - exit, 0)


targets = {
    0: (np.array([[109, 72], [46, 50], [68, 95], [143, 82], [126, 36],
                  [98, 42], [72, 25], [102, 24]]), [0, 2, 3, 4, 5], [1.8, 1.3, .5, 1.5, 1.5, 2, 2, 2]),
    1: (np.array([[13, 113], [29, 143], [67, 142], [66, 81], [49, 108], [58, 115], [59, 101]]),
        [1, 2, 3, 4], [1, 3, 1.5, 2, .7, .7, .7]),
    4: (np.array([[156, 98], [155, 137], [168, 65], [146, 94], [155, 107], [158, 91]]),
        [0, 3, 4], [1.3, 1.6, 1.6, 1.5, 1.5, 1.5]),
}
left_xy = [[66, 82], [66, 86], [67, 90], [69, 94], [71, 98], [63, 97]]
green_xy = [[130, 60], [138, 75], [117, 75], [120, 85]]
initial = np.array([0., 0., 0., 0., 0., 0., 0., 0., math.log(lengths[1]),
                    91 + orig[1][0] @ right / .016, 91 - orig[1][0] @ up / .016])
solutions = []
for visibility_weight in (40, 100, 200):
    p = initial.copy()

    def residual(candidate):
        geometry, group_r, group_t = poses(candidate)
        errors = []
        for i in (0, 1, 4):
            target, indices, weights = targets[i]
            pred = project_landmarks(geometry[i], indices, cut[i])
            errors.extend(((pred - target) * np.array(weights)[:, None]).ravel())
        left_d, left_out = ray_hit(geometry[1], left_xy, cut[1])
        top_d, top_out = ray_hit(geometry[0], left_xy, cut[0])
        errors.extend(np.maximum(top_d + .015 - left_d, 0) * visibility_weight * (top_out < 1e-6))
        errors.extend(left_out * visibility_weight)
        top_d2, top_out2 = ray_hit(geometry[0], green_xy, cut[0])
        right_d, right_out = ray_hit(geometry[4], green_xy, cut[4])
        errors.extend(np.maximum(right_d + .015 - top_d2, 0) * visibility_weight * (right_out < 1e-6))
        errors.extend(top_out2 * visibility_weight)
        top_world = (mesh[0] - pivot) @ group_r.T + pivot + group_t
        errors.append(max(data['floor'] - top_world[:, 2].min(), 0) * 200)
        for i in (0, 1, 4):
            errors.extend(np.maximum(front @ geometry[i][0] + .03, 0) * 30)
        errors.extend(candidate[:3] * 3)
        return np.array(errors)

    damping = 1.
    for iteration in range(140):
        f = residual(p)
        jac = np.column_stack([(residual(p + np.eye(11)[j] * 1e-5) - f) / 1e-5 for j in range(11)])
        step = np.linalg.solve(jac.T @ jac + damping * np.eye(11), -jac.T @ f)
        q = p + step
        if np.linalg.norm(residual(q)) < np.linalg.norm(f):
            p = q
            damping = max(damping * .5, 1e-7)
        else:
            damping *= 4
    geometry, group_r, group_t = poses(p)
    errors = {i: float(np.sqrt(((project_landmarks(geometry[i], targets[i][1], cut[i]) - targets[i][0])**2).mean())) for i in (0, 1, 4)}
    left_d, left_out = ray_hit(geometry[1], left_xy, cut[1])
    top_d, top_out = ray_hit(geometry[0], left_xy, cut[0])
    green_d, _ = ray_hit(geometry[0], green_xy, cut[0])
    blue_d, _ = ray_hit(geometry[4], green_xy, cut[4])
    record = dict(visibility_weight=visibility_weight, landmark_rms_pixels=errors,
                  group_rotation_matrix=group_r.tolist(), group_translation=group_t.tolist(),
                  group_pivot=pivot.tolist(), group_rotation_degrees=(p[:3] * 180 / math.pi).tolist(),
                  left_origin_world=geometry[1][2].tolist(), left_axes_world=geometry[1][0].tolist(),
                  left_edge_length=float(geometry[1][1]),
                  left_vs_top_depth_gaps=(left_d - top_d).tolist(),
                  top_vs_right_depth_gaps=(green_d - blue_d).tolist(),
                  upper_right_relative_transform_preserved=True, applied_to_blender=False)
    solutions.append(record)
    print(json.dumps({k: record[k] for k in ('visibility_weight', 'landmark_rms_pixels',
          'group_rotation_degrees', 'left_vs_top_depth_gaps', 'top_vs_right_depth_gaps')}, ensure_ascii=False))

(OUT / '成组刚体摆放研究.json').write_text(json.dumps(dict(
    note='只读候选研究，未修改实际场景；等待用户决定是否允许成组调整。',
    solutions=solutions), ensure_ascii=False, indent=2), encoding='utf-8')
